using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using LiveCaptioner.Services.Audio;
using LiveCaptioner.Services.Diagnostics;
using NAudio.Wave;
using Vosk;

namespace LiveCaptioner.Services.Speech;

public sealed class VoskSpeechRecognitionService : IDisposable
{
    private readonly string _modelsDirectory;
    private Model? _model;
    private SpkModel? _speakerModel;
    private string? _loadedModelPath;
    private string? _loadedSpeakerModelPath;
    private VoskRecognizer? _recognizer;
    private WaveInEvent? _microphoneCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private Pcm16kMonoConverter? _pcmConverter;
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingTask;
    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private readonly SemaphoreSlim _pcmSignal = new(0);
    private bool _enablePartialResults;
    private string _lastPartialText = "";
    private DateTime _lastPartialEmittedAt = DateTime.MinValue;
    private DateTime _lastBacklogWarningAt = DateTime.MinValue;
    private bool _missingSpeakerVectorLogged;
    private const int MaxQueuedPcmChunks = 160;
    private static readonly TimeSpan PartialEmitInterval = TimeSpan.FromMilliseconds(350);

    public event EventHandler<VoskRecognitionResult>? TextRecognized;
    public event EventHandler<string>? PartialTextRecognized;
    public event EventHandler<double>? AudioLevelChanged;

    public VoskSpeechRecognitionService(string projectRoot)
    {
        _modelsDirectory = Path.Combine(projectRoot, "Models");
    }

    public string GetModelPath(string language)
    {
        var preferredPath = Path.Combine(_modelsDirectory, GetModelFolderName(language));
        if (Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        var languagePrefix = GetModelFolderPrefix(language);
        return Directory
            .EnumerateDirectories(_modelsDirectory, languagePrefix + "*")
            .OrderByDescending(GetModelPriority)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? preferredPath;
    }

    public bool HasModel(string language)
        => Directory.Exists(GetModelPath(language));

    public bool HasSpeakerModel()
        => Directory.Exists(GetSpeakerModelPath());

    public bool IsModelLoaded(string language)
        => _model != null &&
           string.Equals(_loadedModelPath, GetModelPath(language), StringComparison.OrdinalIgnoreCase);

    public void Start(string language, VoskAudioSource audioSource, VoskRecognitionOptions options)
    {
        AppLogger.Info("Vosk service Start entered.");
        Stop();

        var modelPath = GetModelPath(language);
        AppLogger.Info($"Vosk model path resolved: {modelPath}");
        if (!Directory.Exists(modelPath))
        {
            AppLogger.Warn($"Vosk model directory does not exist: {modelPath}");
            throw new DirectoryNotFoundException($"Vosk model not found: {modelPath}");
        }

        Vosk.Vosk.SetLogLevel(-1);
        EnsureModelLoaded(modelPath);
        AppLogger.Info("Creating VoskRecognizer...");
        _recognizer = options.UseInterviewVocabulary
            ? new VoskRecognizer(_model, Pcm16kMonoConverter.TargetSampleRate, BuildInterviewGrammar())
            : new VoskRecognizer(_model, Pcm16kMonoConverter.TargetSampleRate);
        EnsureSpeakerModelLoaded();

        if (_speakerModel != null)
        {
            _recognizer.SetSpkModel(_speakerModel);
            AppLogger.Info("Vosk speaker model enabled for current recognizer.");
        }

        _recognizer.SetWords(false);
        AppLogger.Memory("After VoskRecognizer create");
        _enablePartialResults = options.EnablePartialResults;
        _lastPartialText = "";
        _lastPartialEmittedAt = DateTime.MinValue;
        _lastBacklogWarningAt = DateTime.MinValue;
        _missingSpeakerVectorLogged = false;
        _processingCancellation = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessPcmQueueAsync(_processingCancellation.Token));

        if (audioSource == VoskAudioSource.SystemAudio)
        {
            AppLogger.Info("Starting Vosk system audio capture.");
            StartSystemAudioCapture(options);
        }
        else
        {
            AppLogger.Info("Starting Vosk microphone capture.");
            StartMicrophoneCapture();
        }

        AppLogger.Info("Vosk service Start completed.");
    }

    private void StartMicrophoneCapture()
    {
        AppLogger.Info("Configuring WaveInEvent microphone capture.");
        var waveFormat = new WaveFormat(Pcm16kMonoConverter.TargetSampleRate, 16, 1);
        _pcmConverter = new Pcm16kMonoConverter(waveFormat, 1, false);
        _microphoneCapture = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = waveFormat,
            BufferMilliseconds = 50
        };
        _microphoneCapture.DataAvailable += OnDataAvailable;
        _microphoneCapture.StartRecording();
        AppLogger.Info("Microphone capture started.");
    }

    private void StartSystemAudioCapture(VoskRecognitionOptions options)
    {
        AppLogger.Info("Configuring WASAPI loopback capture.");
        _systemCapture = new WasapiLoopbackCapture();
        AppLogger.Info($"System audio wave format: {_systemCapture.WaveFormat}");
        _pcmConverter = new Pcm16kMonoConverter(
            _systemCapture.WaveFormat,
            options.SystemAudioGain,
            options.SystemAudioNoiseGate);
        _systemCapture.DataAvailable += OnDataAvailable;
        _systemCapture.StartRecording();
        AppLogger.Info("System audio capture started.");
    }

    public void Stop()
    {
        StopRuntime(unloadModels: false);
    }

    private void StopRuntime(bool unloadModels)
    {
        AppLogger.Info("Vosk service Stop entered.");
        _processingCancellation?.Cancel();
        if (_microphoneCapture != null)
        {
            _microphoneCapture.DataAvailable -= OnDataAvailable;
            _microphoneCapture.StopRecording();
            _microphoneCapture.Dispose();
            _microphoneCapture = null;
        }

        if (_systemCapture != null)
        {
            _systemCapture.DataAvailable -= OnDataAvailable;
            _systemCapture.StopRecording();
            _systemCapture.Dispose();
            _systemCapture = null;
        }

        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        catch (OperationCanceledException)
        {
        }

        _processingTask = null;
        _processingCancellation?.Dispose();
        _processingCancellation = null;
        while (_pcmQueue.TryDequeue(out _))
        {
        }

        while (_pcmSignal.Wait(0))
        {
        }

        _pcmConverter = null;
        _recognizer?.Dispose();
        _recognizer = null;
        if (unloadModels)
        {
            UnloadModels();
        }

        AppLogger.Memory("After Vosk service stop");
        AppLogger.Info("Vosk service Stop completed.");
    }

    public void Dispose()
        => StopRuntime(unloadModels: true);

    private void EnsureModelLoaded(string modelPath)
    {
        if (_model != null &&
            string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info($"Reusing already loaded Vosk Model: {modelPath}");
            return;
        }

        UnloadModels();
        AppLogger.Memory("Before Vosk Model load");
        AppLogger.Info($"Loading Vosk Model: {modelPath}");
        _model = new Model(modelPath);
        _loadedModelPath = modelPath;
        AppLogger.Memory("After Vosk Model load");
    }

    private void EnsureSpeakerModelLoaded()
    {
        var speakerModelPath = GetSpeakerModelPath();
        if (!Directory.Exists(speakerModelPath))
        {
            if (_speakerModel != null)
            {
                _speakerModel.Dispose();
                _speakerModel = null;
                _loadedSpeakerModelPath = null;
            }

            AppLogger.Warn($"Vosk speaker model not found: {speakerModelPath}");
            return;
        }

        if (_speakerModel != null &&
            string.Equals(_loadedSpeakerModelPath, speakerModelPath, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info($"Reusing already loaded Vosk speaker model: {speakerModelPath}");
            return;
        }

        _speakerModel?.Dispose();
        _speakerModel = null;
        _loadedSpeakerModelPath = null;
        AppLogger.Info($"Loading Vosk speaker model: {speakerModelPath}");
        AppLogger.Memory("Before Vosk SpkModel load");
        _speakerModel = new SpkModel(speakerModelPath);
        _loadedSpeakerModelPath = speakerModelPath;
        AppLogger.Memory("After Vosk SpkModel load");
    }

    private void UnloadModels()
    {
        _speakerModel?.Dispose();
        _speakerModel = null;
        _loadedSpeakerModelPath = null;
        _model?.Dispose();
        _model = null;
        _loadedModelPath = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_recognizer == null || _pcmConverter == null || e.BytesRecorded <= 0)
        {
            return;
        }

        var sourceFormat = _systemCapture?.WaveFormat ?? _microphoneCapture?.WaveFormat;
        if (sourceFormat != null)
        {
            AudioLevelChanged?.Invoke(this, AudioMath.CalculateRms(e.Buffer, e.BytesRecorded, sourceFormat));
        }

        var pcmBytes = _pcmConverter.Convert(e.Buffer, e.BytesRecorded);
        if (pcmBytes.Length == 0)
        {
            return;
        }

        EnqueuePcm(pcmBytes);
    }

    private void EnqueuePcm(byte[] pcmBytes)
    {
        while (_pcmQueue.Count >= MaxQueuedPcmChunks && _pcmQueue.TryDequeue(out _))
        {
            var now = DateTime.Now;
            if (now - _lastBacklogWarningAt > TimeSpan.FromSeconds(5))
            {
                _lastBacklogWarningAt = now;
                AppLogger.Warn($"Vosk PCM queue backlog exceeded {MaxQueuedPcmChunks}; dropping oldest audio to protect memory.");
            }
        }

        _pcmQueue.Enqueue(pcmBytes);
        _pcmSignal.Release();
    }

    private async Task ProcessPcmQueueAsync(CancellationToken cancellationToken)
    {
        AppLogger.Info("Vosk PCM processing worker started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            await _pcmSignal.WaitAsync(cancellationToken);
            if (!_pcmQueue.TryDequeue(out var pcmBytes))
            {
                continue;
            }

            try
            {
                ProcessPcm(pcmBytes);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Vosk PCM processing failed.", ex);
            }
        }
    }

    private void ProcessPcm(byte[] pcmBytes)
    {
        var recognizer = _recognizer;
        if (recognizer == null)
        {
            return;
        }

        if (recognizer.AcceptWaveform(pcmBytes, pcmBytes.Length))
        {
            var json = recognizer.Result();
            var text = ExtractText(json, "text");
            _lastPartialText = "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                var speakerVector = ExtractSpeakerVector(json);
                if (_speakerModel != null && speakerVector == null && !_missingSpeakerVectorLogged)
                {
                    _missingSpeakerVectorLogged = true;
                    AppLogger.Warn($"Vosk speaker model is enabled but final result has no spk vector. Raw result: {json}");
                }

                AppLogger.Info($"Vosk recognized final text length={text.Length}.");
                TextRecognized?.Invoke(this, new VoskRecognitionResult(text, speakerVector));
            }

            return;
        }

        if (!_enablePartialResults)
        {
            return;
        }

        var partialText = ExtractText(recognizer.PartialResult(), "partial");
        if (string.IsNullOrWhiteSpace(partialText) ||
            string.Equals(partialText, _lastPartialText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.Now;
        if (now - _lastPartialEmittedAt < PartialEmitInterval)
        {
            return;
        }

        _lastPartialText = partialText;
        _lastPartialEmittedAt = now;
        PartialTextRecognized?.Invoke(this, partialText);
    }

    private static string GetModelFolderName(string language)
    {
        return language switch
        {
            "ru" => "vosk-model-ru",
            "uk" => "vosk-model-uk",
            _ => "vosk-model-en"
        };
    }

    private static string GetModelFolderPrefix(string language)
    {
        return language switch
        {
            "ru" => "vosk-model-ru",
            "uk" => "vosk-model-uk",
            _ => "vosk-model-en"
        };
    }

    private string GetSpeakerModelPath()
        => Path.Combine(_modelsDirectory, "vosk-model-spk-0.4");

    private static int GetModelPriority(string path)
    {
        var name = Path.GetFileName(path);

        if (name.Contains("small", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("gigaspeech", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        if (name.Contains("0.42", StringComparison.OrdinalIgnoreCase))
        {
            return 35;
        }

        if (name.Contains("0.22", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        return 10;
    }

    private static string ExtractText(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var text)
            ? text.GetString()?.Trim() ?? ""
            : "";
    }

    private static float[]? ExtractSpeakerVector(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("spk", out var speakerElement) ||
            speakerElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var vector = new List<float>();
        foreach (var item in speakerElement.EnumerateArray())
        {
            if (item.TryGetSingle(out var value))
            {
                vector.Add(value);
            }
        }

        return vector.Count == 0 ? null : vector.ToArray();
    }

    private static string BuildInterviewGrammar()
    {
        var phrases = new[]
        {
            "opening",
            "tell me about yourself",
            "i have more than nineteen years of experience",
            "software development",
            ".net technologies",
            "enterprise applications",
            "telecommunications",
            "recruitment",
            "logistics",
            "banking",
            "property related solutions",
            "recent project",
            "banking lending platform",
            "mortgage and property finance",
            "broker onboarding",
            "mortgage applications",
            "customer and property details",
            "document workflows",
            "credit checks",
            "approvals",
            "further information requests",
            "internal case management",
            "backend",
            ".net eight",
            "rest apis",
            "ef core",
            "sql server",
            "layered architecture",
            "api",
            "application",
            "domain",
            "infrastructure",
            "business rules",
            "operational workflows",
            "maintainable",
            "flexible",
            "integrations",
            "business processes",
            "[unk]"
        };

        return JsonSerializer.Serialize(phrases);
    }

}
