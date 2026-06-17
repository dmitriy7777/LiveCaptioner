using System.Globalization;
using System.Speech.Recognition;

namespace LiveCaptioner.Services.Speech;

public sealed class WindowsSpeechRecognitionService : IDisposable
{
    private SpeechRecognitionEngine? _recognizer;

    public event EventHandler<string>? TextRecognized;
    public event EventHandler<double>? AudioLevelChanged;

    public void Start(string language)
    {
        Stop();

        _recognizer = CreateRecognizer(language);
        _recognizer.LoadGrammar(new DictationGrammar());
        _recognizer.SetInputToDefaultAudioDevice();
        _recognizer.SpeechRecognized += OnSpeechRecognized;
        _recognizer.AudioLevelUpdated += OnAudioLevelUpdated;
        _recognizer.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void Stop()
    {
        if (_recognizer == null)
        {
            return;
        }

        _recognizer.SpeechRecognized -= OnSpeechRecognized;
        _recognizer.AudioLevelUpdated -= OnAudioLevelUpdated;
        _recognizer.RecognizeAsyncCancel();
        _recognizer.RecognizeAsyncStop();
        _recognizer.Dispose();
        _recognizer = null;
    }

    public void Dispose()
        => Stop();

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || e.Result.Confidence < 0.35)
        {
            return;
        }

        TextRecognized?.Invoke(this, text);
    }

    private void OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs e)
    {
        AudioLevelChanged?.Invoke(this, Math.Clamp(e.AudioLevel / 100.0, 0, 1));
    }

    private static SpeechRecognitionEngine CreateRecognizer(string language)
    {
        var culture = ToCulture(language);
        var installedRecognizer = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(info => string.Equals(info.Culture.Name, culture.Name, StringComparison.OrdinalIgnoreCase));

        return installedRecognizer == null
            ? new SpeechRecognitionEngine()
            : new SpeechRecognitionEngine(installedRecognizer);
    }

    private static CultureInfo ToCulture(string language)
    {
        return language switch
        {
            "ru" => CultureInfo.GetCultureInfo("ru-RU"),
            "uk" => CultureInfo.GetCultureInfo("uk-UA"),
            "en" => CultureInfo.GetCultureInfo("en-US"),
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }
}
