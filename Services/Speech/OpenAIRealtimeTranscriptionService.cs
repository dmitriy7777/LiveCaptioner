using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.IO;
using LiveCaptioner.Services.Audio;
using LiveCaptioner.Services.Diagnostics;

namespace LiveCaptioner.Services.Speech;

public sealed class OpenAIRealtimeTranscriptionService : IDisposable
{
    private static readonly Uri RealtimeUri = new("wss://api.openai.com/v1/realtime?model=gpt-realtime-2");
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveTask;
    private Task? _commitTask;
    private int _pendingAudioBytes;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    public event EventHandler<string>? PartialTextReceived;
    public event EventHandler<string>? FinalTextReceived;
    public event EventHandler<string>? StatusReceived;
    public event EventHandler<string>? ErrorReceived;

    public async Task StartAsync(string language, CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Set OPENAI_API_KEY before using OpenAI realtime transcription.");
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        AppLogger.Info("Connecting to OpenAI Realtime transcription.");
        await _webSocket.ConnectAsync(RealtimeUri, _cancellation.Token);
        await SendSessionUpdateAsync(language, _cancellation.Token);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cancellation.Token));
        _commitTask = Task.Run(() => CommitLoopAsync(_cancellation.Token));
        StatusReceived?.Invoke(this, "OpenAI realtime transcription connected.");
    }

    public async Task SendAudioAsync(byte[] pcmBytes, CancellationToken cancellationToken)
    {
        if (pcmBytes.Length == 0 || _webSocket?.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcmBytes)
        });
        await SendJsonAsync(json, cancellationToken);
        Interlocked.Add(ref _pendingAudioBytes, pcmBytes.Length);
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"OpenAI realtime close failed: {ex.Message}");
        }

        _webSocket?.Dispose();
        _cancellation?.Dispose();
        _sendLock.Dispose();
    }

    private async Task SendSessionUpdateAsync(string language, CancellationToken cancellationToken)
    {
        var languageValue = string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : language;
        var json = JsonSerializer.Serialize(new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                output_modalities = new[] { "text" },
                audio = new
                {
                    input = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = RealtimePcmAudioCapture.TargetSampleRate
                        },
                        transcription = new
                        {
                            model = "gpt-realtime-whisper",
                            language = languageValue
                        },
                        turn_detection = (object?)null
                    }
                }
            }
        });

        await SendJsonAsync(json, cancellationToken);
    }

    private async Task CommitLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1200));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_webSocket?.State != WebSocketState.Open)
            {
                return;
            }

            var pendingBytes = Interlocked.Exchange(ref _pendingAudioBytes, 0);
            if (pendingBytes < RealtimePcmAudioCapture.TargetSampleRate)
            {
                Interlocked.Add(ref _pendingAudioBytes, pendingBytes);
                continue;
            }

            await SendJsonAsync("{\"type\":\"input_audio_buffer.commit\"}", cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    StatusReceived?.Invoke(this, "OpenAI realtime transcription disconnected.");
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(message.ToArray());
            HandleServerEvent(json);
        }
    }

    private void HandleServerEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = TryGetString(root, "type") ?? "";

            switch (type)
            {
                case "conversation.item.input_audio_transcription.delta":
                    var delta = TryGetString(root, "delta");
                    if (!string.IsNullOrWhiteSpace(delta))
                    {
                        PartialTextReceived?.Invoke(this, delta);
                    }
                    break;
                case "conversation.item.input_audio_transcription.completed":
                    var transcript = TryGetString(root, "transcript");
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        FinalTextReceived?.Invoke(this, transcript);
                    }
                    break;
                case "error":
                    ErrorReceived?.Invoke(this, TryReadError(root));
                    break;
                case "session.updated":
                case "transcription_session.updated":
                    AppLogger.Info($"OpenAI realtime session updated: {type}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not parse OpenAI realtime event: {ex.Message}. Raw={json}");
        }
    }

    private async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string? GetApiKey()
        => Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string TryReadError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            return TryGetString(error, "message") ?? error.GetRawText();
        }

        return root.GetRawText();
    }
}
