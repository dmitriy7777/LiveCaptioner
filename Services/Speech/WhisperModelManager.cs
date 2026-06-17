using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace LiveCaptioner.Services.Speech;

public sealed class WhisperModelManager : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly string _legacyModelPath;
    private WhisperFactory? _whisperFactory;
    private string? _loadedModelPath;

    public WhisperModelManager(string projectRoot)
    {
        _modelsDirectory = Path.Combine(projectRoot, "Models");
        _legacyModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.bin");
    }

    public bool IsModelLoaded => _whisperFactory != null;

    public string GetModelPath(string modelKey)
        => Path.Combine(_modelsDirectory, $"ggml-{modelKey}.bin");

    public bool HasModel(string modelKey)
        => File.Exists(GetModelPath(modelKey));

    public async Task DownloadModelAsync(string modelKey, CancellationToken cancellationToken = default)
    {
        var modelPath = GetModelPath(modelKey);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GetModelType(modelKey), cancellationToken: cancellationToken);
        await using var fileWriter = File.Create(modelPath);
        await modelStream.CopyToAsync(fileWriter, cancellationToken);

        ResetLoadedModel();
    }

    public async Task EnsureFactoryAsync(string modelKey, CancellationToken cancellationToken)
    {
        var modelPath = GetModelPath(modelKey);
        if (_whisperFactory != null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ResetLoadedModel();

        if (!File.Exists(modelPath))
        {
            return;
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _whisperFactory = WhisperFactory.FromPath(modelPath);
            _loadedModelPath = modelPath;
        }, cancellationToken);
    }

    public WhisperProcessor CreateProcessor(string engineKey, string language, string prompt)
    {
        var builder = _whisperFactory!.CreateBuilder()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithTemperature(0f);

        if (engineKey.Equals("whisper-fast", StringComparison.OrdinalIgnoreCase))
        {
            builder
                .WithNoContext()
                .WithSingleSegment();
        }
        else
        {
            builder.WithMaxLastTextTokens(64);

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                builder.WithPrompt(prompt);
            }
        }

        if (language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            builder.WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(language);
        }

        return builder.Build();
    }

    public void MoveLegacyModelIfNeeded()
    {
        var baseModelPath = GetModelPath("base");
        if (File.Exists(baseModelPath) || !File.Exists(_legacyModelPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(baseModelPath)!);
        File.Move(_legacyModelPath, baseModelPath);
    }

    public void ResetLoadedModel()
    {
        _whisperFactory?.Dispose();
        _whisperFactory = null;
        _loadedModelPath = null;
    }

    public void Dispose()
        => ResetLoadedModel();

    private static GgmlType GetModelType(string modelKey)
    {
        return modelKey switch
        {
            "base" => GgmlType.Base,
            "small" => GgmlType.Small,
            _ => GgmlType.Small
        };
    }
}
