using System.IO;
using System.Text.Json;
using LiveCaptioner.Services.Audio;
using NAudio.Wave;
using Vosk;

namespace LiveCaptioner.Services.Speech;

public sealed class VoskSpeechRecognitionService : IDisposable
{
    private readonly string _modelsDirectory;
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WaveInEvent? _microphoneCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private Pcm16kMonoConverter? _pcmConverter;
    private bool _enablePartialResults;
    private string _lastPartialText = "";
    private DateTime _lastPartialEmittedAt = DateTime.MinValue;

    public event EventHandler<string>? TextRecognized;
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

    public void Start(string language, VoskAudioSource audioSource, VoskRecognitionOptions options)
    {
        Stop();

        var modelPath = GetModelPath(language);
        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException($"Vosk model not found: {modelPath}");
        }

        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);
        _recognizer = options.UseInterviewVocabulary
            ? new VoskRecognizer(_model, Pcm16kMonoConverter.TargetSampleRate, BuildInterviewGrammar())
            : new VoskRecognizer(_model, Pcm16kMonoConverter.TargetSampleRate);
        _recognizer.SetWords(false);
        _enablePartialResults = options.EnablePartialResults;
        _lastPartialText = "";
        _lastPartialEmittedAt = DateTime.MinValue;

        if (audioSource == VoskAudioSource.SystemAudio)
        {
            StartSystemAudioCapture(options);
        }
        else
        {
            StartMicrophoneCapture();
        }
    }

    private void StartMicrophoneCapture()
    {
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
    }

    private void StartSystemAudioCapture(VoskRecognitionOptions options)
    {
        _systemCapture = new WasapiLoopbackCapture();
        _pcmConverter = new Pcm16kMonoConverter(
            _systemCapture.WaveFormat,
            options.SystemAudioGain,
            options.SystemAudioNoiseGate);
        _systemCapture.DataAvailable += OnDataAvailable;
        _systemCapture.StartRecording();
    }

    public void Stop()
    {
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

        _pcmConverter = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;
    }

    public void Dispose()
        => Stop();

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

        if (_recognizer.AcceptWaveform(pcmBytes, pcmBytes.Length))
        {
            var text = ExtractText(_recognizer.Result(), "text");
            _lastPartialText = "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                TextRecognized?.Invoke(this, text);
            }

            return;
        }

        if (!_enablePartialResults)
        {
            return;
        }

        var partialText = ExtractText(_recognizer.PartialResult(), "partial");
        if (string.IsNullOrWhiteSpace(partialText) ||
            string.Equals(partialText, _lastPartialText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.Now;
        if (now - _lastPartialEmittedAt < TimeSpan.FromMilliseconds(180))
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

    private static int GetModelPriority(string path)
    {
        var name = Path.GetFileName(path);

        if (name.Contains("small", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
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
