using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveCaptioner.Models;
using LiveCaptioner.Services.Audio;
using LiveCaptioner.Services.Speech;
using Microsoft.Win32;

namespace LiveCaptioner;

public partial class MainWindow : Window
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly TimeSpan LowLatencyOverlap = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CaptionParagraphGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StreamingSpeakerPause = TimeSpan.FromSeconds(1.4);
    private readonly WhisperModelManager _whisperModelManager = new(ProjectRoot);
    private readonly VoskSpeechRecognitionService _voskSpeechRecognition = new(ProjectRoot);
    private readonly ConcurrentQueue<AudioChunk> _pendingChunks = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private IAudioCapture? _audioCapture;
    private WindowsSpeechRecognitionService? _windowsSpeechRecognition;
    private CancellationTokenSource? _captionCancellation;
    private Task? _captionTask;
    private bool _isRunning;
    private string _activeEngineKey = "whisper-balanced";
    private string _activeLanguage = "auto";
    private string _activeAudioSourceName = "Микрофон";
    private string _lastCaptionText = "";
    private TextBlock? _activeCaptionTextBlock;
    private DateTime _lastCaptionAddedAt = DateTime.MinValue;
    private DateTime _lastAudibleChunkCapturedAt = DateTime.MinValue;
    private string _recognitionPrompt = "";
    private string _streamingCaptionPrefix = "";
    private DateTime _lastStreamingSpeechAt = DateTime.MinValue;
    private bool _streamingSpeechActive;
    private bool _pendingStreamingSpeakerTurn;
    private int _streamingSpeakerIndex = 1;
    private bool _startNewCaptionParagraph = true;

    public MainWindow()
    {
        InitializeComponent();
        _whisperModelManager.MoveLegacyModelIfNeeded();
        SelectAvailableModel();
        UpdateModelStatus();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        try
        {
            _activeEngineKey = GetSelectedEngineKey();
            _activeLanguage = GetSelectedLanguage();
            var modelKey = GetSelectedModelKey();
            if (IsVoskEngine(_activeEngineKey) && !_voskSpeechRecognition.HasModel(_activeLanguage))
            {
                var modelPath = _voskSpeechRecognition.GetModelPath(_activeLanguage);
                StatusText.Text = $"Vosk model not found: {modelPath}";
                MessageBox.Show(
                    $"Vosk model not found:\n{modelPath}\n\nDownload a Vosk model and unpack it into that folder.",
                    "LiveCaptioner",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (IsWhisperEngine(_activeEngineKey) && !_whisperModelManager.HasModel(modelKey))
            {
                UpdateModelStatus();
                StatusText.Text = $"Сначала скачайте {modelKey}-модель или выберите уже скачанную модель.";
                return;
            }

            _captionCancellation = new CancellationTokenSource();
            _pendingChunks.Clear();
            _lastCaptionText = "";
            _activeCaptionTextBlock = null;
            _lastCaptionAddedAt = DateTime.MinValue;
            _lastAudibleChunkCapturedAt = DateTime.MinValue;
            _recognitionPrompt = "";
            _streamingCaptionPrefix = "";
            _lastStreamingSpeechAt = DateTime.MinValue;
            _streamingSpeechActive = false;
            _pendingStreamingSpeakerTurn = false;
            _streamingSpeakerIndex = 1;
            _startNewCaptionParagraph = true;
            _activeAudioSourceName = GetSelectedAudioSourceName();

            if (IsWindowsSpeechEngine(_activeEngineKey))
            {
                StartWindowsSpeechRecognition();
            }
            else if (IsVoskEngine(_activeEngineKey))
            {
                StartVoskRecognition();
            }
            else
            {
                StatusText.Text = "Загружаю модель Whisper...";
                await _whisperModelManager.EnsureFactoryAsync(modelKey, _captionCancellation.Token);

                var chunkSeconds = TimeSpan.FromSeconds(Math.Round(ChunkSecondsSlider.Value));
                _audioCapture = CreateAudioCapture(chunkSeconds);
                _audioCapture.AudioLevelChanged += OnAudioLevelChanged;
                _audioCapture.AudioChunkReady += OnAudioChunkReady;
                _audioCapture.Start();

                _captionTask = Task.Run(() => CaptionLoopAsync(_captionCancellation.Token));
            }

            _isRunning = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            ChunkSecondsSlider.IsEnabled = false;
            AudioSourceComboBox.IsEnabled = false;
            LanguageComboBox.IsEnabled = false;
            ModelComboBox.IsEnabled = false;
            StatusText.Text = IsWindowsSpeechEngine(_activeEngineKey)
                ? "Слушаю микрофон через Windows Speech Recognition."
                : IsVoskEngine(_activeEngineKey)
                ? "Слушаю микрофон через Vosk local."
                : !_whisperModelManager.IsModelLoaded
                ? $"Слушаю {_activeAudioSourceName.ToLowerInvariant()}. Скачайте модель Whisper, чтобы получить текст."
                : $"Слушаю {_activeAudioSourceName.ToLowerInvariant()} и распознаю речь.";
        }
        catch (Exception ex)
        {
            StopListening();
            MessageBox.Show(ex.Message, "LiveCaptioner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopListening();
        StatusText.Text = "Остановлено.";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CaptionPanel.Children.Clear();
        _lastCaptionText = "";
        _activeCaptionTextBlock = null;
        _lastCaptionAddedAt = DateTime.MinValue;
        _lastAudibleChunkCapturedAt = DateTime.MinValue;
        _recognitionPrompt = "";
        _streamingCaptionPrefix = "";
        _lastStreamingSpeechAt = DateTime.MinValue;
        _streamingSpeechActive = false;
        _pendingStreamingSpeakerTurn = false;
        _streamingSpeakerIndex = 1;
        _startNewCaptionParagraph = true;
        StatusText.Text = "Текст очищен.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var text = BuildTranscriptText();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Нечего сохранять.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить расшифровку",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"live-captions-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
            StatusText.Text = $"Сохранено: {dialog.FileName}";
        }
    }

    private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var modelKey = GetSelectedModelKey();
            var modelPath = _whisperModelManager.GetModelPath(modelKey);
            if (_whisperModelManager.HasModel(modelKey))
            {
                StatusText.Text = "Модель уже скачана. Повторная загрузка не нужна.";
                UpdateModelStatus();
                return;
            }

            StatusText.Text = $"Скачиваю {Path.GetFileName(modelPath)}. Это может занять несколько минут...";
            DownloadModelButton.IsEnabled = false;

            await _whisperModelManager.DownloadModelAsync(modelKey);
            UpdateModelStatus();
            StatusText.Text = "Модель скачана. Можно нажать Старт.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось скачать модель.";
            MessageBox.Show(ex.Message, "Скачивание модели", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateModelStatus();
        }
    }

    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopCheckBox.IsChecked == true;
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _isRunning)
        {
            return;
        }

        _whisperModelManager.ResetLoadedModel();
        UpdateModelStatus();
    }

    private void EngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        var newEngineKey = GetSelectedEngineKey();
        if (_isRunning && (IsStreamingEngine(newEngineKey) || IsStreamingEngine(_activeEngineKey)))
        {
            StatusText.Text = "Для переключения на/с streaming-движка нажмите Стоп и снова Старт.";
            return;
        }

        _activeEngineKey = GetSelectedEngineKey();
        _recognitionPrompt = "";
        StatusText.Text = $"Движок переключен: {EngineComboBox.Text}";
    }

    protected override void OnClosed(EventArgs e)
    {
        StopListening();
        _whisperModelManager.Dispose();
        _voskSpeechRecognition.Dispose();
        base.OnClosed(e);
    }

    private void OnAudioChunkReady(object? sender, AudioChunk chunk)
    {
        while (_pendingChunks.Count > 2 && _pendingChunks.TryDequeue(out _))
        {
        }

        _pendingChunks.Enqueue(chunk);
        _queueSignal.Release();
    }

    private void OnAudioLevelChanged(object? sender, double level)
    {
        Dispatcher.Invoke(() =>
        {
            AudioLevelBar.Value = Math.Clamp(level * 3.5, 0, 1);
            AudioStatusText.Text = level > 0.01
                ? $"Источник: {_activeAudioSourceName} - сигнал есть"
                : $"Источник: {_activeAudioSourceName} - тишина";
        });
    }

    private void StartWindowsSpeechRecognition()
    {
        _windowsSpeechRecognition = new WindowsSpeechRecognitionService();
        _windowsSpeechRecognition.AudioLevelChanged += OnWindowsSpeechAudioLevelChanged;
        _windowsSpeechRecognition.TextRecognized += OnWindowsSpeechTextRecognized;
        _windowsSpeechRecognition.Start(_activeLanguage);
        _activeAudioSourceName = "Микрофон / Windows Speech";
    }

    private void OnWindowsSpeechAudioLevelChanged(object? sender, double level)
    {
        Dispatcher.Invoke(() =>
        {
            AudioLevelBar.Value = Math.Clamp(level * 3.5, 0, 1);
            AudioStatusText.Text = level > 0.01
                ? "Источник: Микрофон / Windows Speech - сигнал есть"
                : "Источник: Микрофон / Windows Speech - тишина";
        });
    }

    private void OnWindowsSpeechTextRecognized(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (ShouldAddCaption(text))
            {
                AddCaptionLine(_activeAudioSourceName, text);
            }
        });
    }

    private void StartVoskRecognition()
    {
        _voskSpeechRecognition.AudioLevelChanged += OnVoskAudioLevelChanged;
        _voskSpeechRecognition.PartialTextRecognized += OnVoskPartialTextRecognized;
        _voskSpeechRecognition.TextRecognized += OnVoskTextRecognized;
        _voskSpeechRecognition.Start(
            _activeLanguage,
            GetSelectedVoskAudioSource(),
            new VoskRecognitionOptions(
                VoskPartialResultsCheckBox.IsChecked == true,
                VoskInterviewVocabularyCheckBox.IsChecked == true,
                VoskSystemAudioGainSlider.Value,
                VoskSystemNoiseGateCheckBox.IsChecked == true));
        _activeAudioSourceName = GetSelectedVoskAudioSource() == VoskAudioSource.SystemAudio
            ? "Системный звук Windows / Vosk"
            : "Микрофон / Vosk";
    }

    private void OnVoskAudioLevelChanged(object? sender, double level)
    {
        Dispatcher.Invoke(() =>
        {
            AudioLevelBar.Value = Math.Clamp(level * 3.5, 0, 1);
            TrackVoskSpeakerPause(level);
            AudioStatusText.Text = level > 0.01
                ? $"Источник: {_activeAudioSourceName} - сигнал есть"
                : $"Источник: {_activeAudioSourceName} - тишина";
        });
    }

    private void OnVoskTextRecognized(object? sender, string text)
    {
        Dispatcher.Invoke(() => CommitStreamingCaptionLine(GetVoskCaptionSpeaker(), text));
    }

    private void OnVoskPartialTextRecognized(object? sender, string text)
    {
        Dispatcher.Invoke(() => UpdateStreamingCaptionLine(GetVoskCaptionSpeaker(), text));
    }

    private async Task CaptionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(cancellationToken);

            if (!_pendingChunks.TryDequeue(out var chunk))
            {
                continue;
            }

            if (!_whisperModelManager.IsModelLoaded)
            {
                Dispatcher.Invoke(() => AddSystemLine($"Аудио получено: {chunk.Duration.TotalSeconds:0} сек., RMS {chunk.Level:P0}. Модель Whisper не найдена."));
                continue;
            }

            if (chunk.Level < 0.004)
            {
                continue;
            }

            if (_lastAudibleChunkCapturedAt != DateTime.MinValue &&
                chunk.CapturedAt - _lastAudibleChunkCapturedAt > CaptionParagraphGap)
            {
                Dispatcher.Invoke(() => _startNewCaptionParagraph = true);
            }

            _lastAudibleChunkCapturedAt = chunk.CapturedAt;

            try
            {
                await using var stream = new MemoryStream(chunk.WavBytes, writable: false);
                using var processor = _whisperModelManager.CreateProcessor(_activeEngineKey, _activeLanguage, _recognitionPrompt);

                var hasText = false;
                await foreach (var result in processor.ProcessAsync(stream, cancellationToken))
                {
                    var text = result.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (ShouldAddCaption(text))
                    {
                        hasText = true;
                        Dispatcher.Invoke(() => AddCaptionLine(_activeAudioSourceName, text));
                    }
                }

                if (!hasText)
                {
                    Dispatcher.Invoke(() => StatusText.Text = "Речь в последнем фрагменте не найдена.");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddSystemLine($"Ошибка распознавания: {ex.Message}"));
            }
        }
    }

    private void UpdateModelStatus()
    {
        if (!IsInitialized)
        {
            return;
        }

        var modelKey = GetSelectedModelKey();
        var modelPath = _whisperModelManager.GetModelPath(modelKey);
        var hasModel = _whisperModelManager.HasModel(modelKey);
        ModelStatusText.Text = hasModel
            ? $"Модель готова: {modelPath}"
            : $"Модель Whisper не найдена: {modelPath}";
        DownloadModelButton.Content = hasModel ? "Модель уже скачана" : $"Скачать {GetSelectedModelKey()}-модель";
        DownloadModelButton.IsEnabled = !hasModel;
    }

    private void SelectAvailableModel()
    {
        var preferredModelKey = _whisperModelManager.HasModel("small") ? "small" : "base";
        preferredModelKey = _whisperModelManager.HasModel("base") ? "base" : preferredModelKey;
        if (!_whisperModelManager.HasModel(preferredModelKey))
        {
            return;
        }

        foreach (var item in ModelComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), preferredModelKey, StringComparison.OrdinalIgnoreCase))
            {
                ModelComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void StopListening()
    {
        if (!_isRunning && _audioCapture == null)
        {
            return;
        }

        _captionCancellation?.Cancel();
        _audioCapture?.Dispose();
        _audioCapture = null;
        _windowsSpeechRecognition?.Dispose();
        _windowsSpeechRecognition = null;
        _voskSpeechRecognition.TextRecognized -= OnVoskTextRecognized;
        _voskSpeechRecognition.PartialTextRecognized -= OnVoskPartialTextRecognized;
        _voskSpeechRecognition.AudioLevelChanged -= OnVoskAudioLevelChanged;
        _voskSpeechRecognition.Stop();
        _captionCancellation?.Dispose();
        _captionCancellation = null;
        _isRunning = false;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ChunkSecondsSlider.IsEnabled = true;
        AudioSourceComboBox.IsEnabled = true;
        LanguageComboBox.IsEnabled = true;
        ModelComboBox.IsEnabled = true;
        AudioLevelBar.Value = 0;
    }

    private void TrackVoskSpeakerPause(double level)
    {
        if (!IsVoskEngine(_activeEngineKey))
        {
            return;
        }

        var now = DateTime.Now;
        if (level > 0.012)
        {
            _lastStreamingSpeechAt = now;
            _streamingSpeechActive = true;
            return;
        }

        if (_streamingSpeechActive &&
            _lastStreamingSpeechAt != DateTime.MinValue &&
            now - _lastStreamingSpeechAt > StreamingSpeakerPause)
        {
            _streamingSpeechActive = false;
            _pendingStreamingSpeakerTurn = !string.IsNullOrWhiteSpace(_streamingCaptionPrefix);
        }
    }

    private IAudioCapture CreateAudioCapture(TimeSpan chunkDuration)
    {
        return AudioSourceComboBox.SelectedIndex == 1
            ? new SystemAudioCapture(chunkDuration, LowLatencyOverlap)
            : new MicrophoneAudioCapture(chunkDuration, LowLatencyOverlap);
    }

    private string GetSelectedAudioSourceName()
    {
        return AudioSourceComboBox.SelectedIndex == 1
            ? "Системный звук Windows"
            : "Микрофон";
    }

    private string GetSelectedLanguage()
    {
        return (LanguageComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
    }

    private string GetSelectedEngineKey()
    {
        return (EngineComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "whisper-balanced";
    }

    private VoskAudioSource GetSelectedVoskAudioSource()
    {
        return AudioSourceComboBox.SelectedIndex == 1
            ? VoskAudioSource.SystemAudio
            : VoskAudioSource.Microphone;
    }

    private static bool IsWhisperEngine(string engineKey)
        => engineKey.StartsWith("whisper", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsSpeechEngine(string engineKey)
        => engineKey.Equals("windows-speech", StringComparison.OrdinalIgnoreCase);

    private static bool IsVoskEngine(string engineKey)
        => engineKey.Equals("vosk-local", StringComparison.OrdinalIgnoreCase);

    private static bool IsStreamingEngine(string engineKey)
        => IsWindowsSpeechEngine(engineKey) || IsVoskEngine(engineKey);

    private string GetSelectedModelKey()
    {
        return (ModelComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "small";
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiveCaptioner.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private void AddCaptionLine(string speaker, string text)
    {
        _lastCaptionText = text;
        _recognitionPrompt = BuildRecognitionPrompt(text);
        var now = DateTime.Now;

        if (_activeCaptionTextBlock == null || _startNewCaptionParagraph)
        {
            _activeCaptionTextBlock = CreateCaptionParagraph(speaker, text);
            _startNewCaptionParagraph = false;
        }
        else
        {
            _activeCaptionTextBlock.Text = AppendCaptionText(_activeCaptionTextBlock.Text, text);
        }

        _lastCaptionAddedAt = now;
        CaptionScrollViewer.ScrollToEnd();
        StatusText.Text = "Распознано.";
    }

    private void UpdateStreamingCaptionLine(string speaker, string partialText)
    {
        ApplyPendingStreamingSpeakerTurn();
        speaker = GetVoskCaptionSpeaker();
        var displayText = AppendCaptionText(_streamingCaptionPrefix, partialText);
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return;
        }

        if (_activeCaptionTextBlock == null || _startNewCaptionParagraph)
        {
            _activeCaptionTextBlock = CreateCaptionParagraph(speaker, displayText);
            _startNewCaptionParagraph = false;
        }
        else
        {
            _activeCaptionTextBlock.Text = displayText;
        }

        _lastCaptionText = displayText;
        _recognitionPrompt = BuildRecognitionPrompt(displayText);
        _lastCaptionAddedAt = DateTime.Now;
        CaptionScrollViewer.ScrollToEnd();
        StatusText.Text = "Распознаю...";
    }

    private void CommitStreamingCaptionLine(string speaker, string finalText)
    {
        if (string.IsNullOrWhiteSpace(finalText))
        {
            return;
        }

        ApplyPendingStreamingSpeakerTurn();
        speaker = GetVoskCaptionSpeaker();
        var formattedText = VoskSentenceFormattingCheckBox.IsChecked == true
            ? FormatCaptionSentence(finalText)
            : finalText.Trim();
        _streamingCaptionPrefix = AppendCaptionText(_streamingCaptionPrefix, formattedText);
        UpdateStreamingCaptionLine(speaker, "");
        StatusText.Text = "Распознано.";
    }

    private void ApplyPendingStreamingSpeakerTurn()
    {
        if (!ShouldAutoSplitVoskSpeakers() || !_pendingStreamingSpeakerTurn)
        {
            return;
        }

        _pendingStreamingSpeakerTurn = false;
        _streamingCaptionPrefix = "";
        _activeCaptionTextBlock = null;
        _startNewCaptionParagraph = true;
        _streamingSpeakerIndex = _streamingSpeakerIndex == 1 ? 2 : 1;
    }

    private bool ShouldAutoSplitVoskSpeakers()
    {
        return IsVoskEngine(_activeEngineKey) &&
               GetSelectedVoskAudioSource() == VoskAudioSource.SystemAudio &&
               VoskAutoSpeakerTurnsCheckBox.IsChecked == true;
    }

    private string GetVoskCaptionSpeaker()
    {
        if (ShouldAutoSplitVoskSpeakers())
        {
            return $"Speaker {_streamingSpeakerIndex}";
        }

        return _activeAudioSourceName;
    }

    private TextBlock CreateCaptionParagraph(string speaker, string text)
    {
        var captionTextBlock = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 15,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        };

        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 35, 58)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{DateTime.Now:HH:mm:ss}  {speaker}",
                        Foreground = new SolidColorBrush(Color.FromRgb(124, 212, 255)),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    },
                    captionTextBlock
                }
            }
        };

        CaptionPanel.Children.Add(row);
        return captionTextBlock;
    }

    private void AddSystemLine(string text)
    {
        CaptionPanel.Children.Add(new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {text}",
            Foreground = new SolidColorBrush(Color.FromRgb(154, 167, 189)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        CaptionScrollViewer.ScrollToEnd();
    }

    private string BuildTranscriptText()
    {
        var builder = new StringBuilder();
        foreach (var child in CaptionPanel.Children)
        {
            if (child is Border { Child: StackPanel stack })
            {
                foreach (var item in stack.Children.OfType<TextBlock>())
                {
                    builder.AppendLine(item.Text);
                }
                builder.AppendLine();
            }
            else if (child is TextBlock textBlock)
            {
                builder.AppendLine(textBlock.Text);
            }
        }

        return builder.ToString();
    }

    private bool ShouldAddCaption(string text)
    {
        var current = NormalizeCaptionText(text);
        var previous = NormalizeCaptionText(_lastCaptionText);

        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(previous))
        {
            return true;
        }

        if (current == previous)
        {
            return false;
        }

        var similarLength = Math.Abs(current.Length - previous.Length) <= 20;
        return !(similarLength && (current.Contains(previous) || previous.Contains(current)));
    }

    private static string NormalizeCaptionText(string text)
        => Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string BuildRecognitionPrompt(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        const int maxPromptLength = 300;

        if (normalized.Length <= maxPromptLength)
        {
            return normalized;
        }

        return normalized[^maxPromptLength..];
    }

    private static string AppendCaptionText(string existingText, string newText)
    {
        var existing = existingText.TrimEnd();
        var addition = newText.Trim();

        if (string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        if (string.IsNullOrWhiteSpace(addition))
        {
            return existing;
        }

        return $"{existing} {addition}";
    }

    private static string FormatCaptionSentence(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        normalized = Regex.Replace(normalized, @"\bi\b", "I", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bi'm\b", "I'm", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bdot net\b", ".NET", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bnet\b", ".NET", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bef core\b", "EF Core", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bsql server\b", "SQL Server", RegexOptions.IgnoreCase);
        normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];

        if (Regex.IsMatch(normalized, @"^(what|why|how|when|where|who|can|could|would|should|do|does|did|is|are|was|were|have|has|tell me|could you|would you)\b", RegexOptions.IgnoreCase))
        {
            return normalized.TrimEnd('.', '?', '!') + "?";
        }

        return normalized.EndsWith('.') || normalized.EndsWith('?') || normalized.EndsWith('!')
            ? normalized
            : normalized + ".";
    }
}

