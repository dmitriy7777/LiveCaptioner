using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LiveCaptioner.Services.Diagnostics;

namespace LiveCaptioner.Services.Speech;

public sealed class OpenAITranscriptionService
{
    private const string ApiUrl = "https://api.openai.com/v1/audio/transcriptions";
    private readonly HttpClient _httpClient = new();

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    public async Task<OpenAITranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        string model,
        string language,
        string prompt,
        CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Set OPENAI_API_KEY before using OpenAI transcription.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        if (string.Equals(model, "gpt-4o-transcribe-diarize", StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new StringContent("diarized_json"), "response_format");
            content.Add(new StringContent("auto"), "chunking_strategy");
        }
        else
        {
            content.Add(new StringContent("json"), "response_format");
        }

        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new StringContent(language), "language");
        }

        if (!string.IsNullOrWhiteSpace(prompt) &&
            !string.Equals(model, "gpt-4o-transcribe-diarize", StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new StringContent(prompt), "prompt");
        }

        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", $"live-caption-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Warn($"OpenAI transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
            throw new InvalidOperationException($"OpenAI transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return ParseResult(json);
    }

    private static string? GetApiKey()
        => Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private static OpenAITranscriptionResult ParseResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = TryGetString(root, "text") ?? "";
        var segments = new List<OpenAISpeakerSegment>();

        if (root.TryGetProperty("segments", out var segmentArray) &&
            segmentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentArray.EnumerateArray())
            {
                var segmentText = TryGetString(segment, "text") ?? "";
                if (string.IsNullOrWhiteSpace(segmentText))
                {
                    continue;
                }

                var speaker = TryGetString(segment, "speaker") ??
                              TryGetString(segment, "speaker_label") ??
                              TryGetString(segment, "speaker_id") ??
                              "Speaker";
                segments.Add(new OpenAISpeakerSegment(
                    NormalizeSpeakerName(speaker),
                    segmentText.Trim(),
                    TryGetDouble(segment, "start"),
                    TryGetDouble(segment, "end")));
            }
        }

        return new OpenAITranscriptionResult(text.Trim(), segments);
    }

    private static string NormalizeSpeakerName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("Speaker", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"Speaker {trimmed}";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }
}
