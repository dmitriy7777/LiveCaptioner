using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveCaptioner.Models;
using LiveCaptioner.Services.Audio;
using LiveCaptioner.Services.Diagnostics;
using LiveCaptioner.Services.Speech;
using Microsoft.Win32;

namespace LiveCaptioner;

public partial class MainWindow : Window
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly TimeSpan LowLatencyOverlap = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OpenAIOverlap = TimeSpan.Zero;
    private static readonly TimeSpan CaptionParagraphGap = TimeSpan.FromSeconds(3);
    private const int DefaultMaxPendingChunks = 3;
    private const int OpenAIMaxPendingChunks = 16;
    private readonly WhisperModelManager _whisperModelManager = new(ProjectRoot);
    private readonly VoskSpeechRecognitionService _voskSpeechRecognition = new(ProjectRoot);
    private readonly OpenAITranscriptionService _openAITranscription = new();
    private readonly SherpaOnnxSpeechRecognitionService _sherpaOnnxRecognition = new(ProjectRoot);
    private readonly LocalSpeakerDiarizer _openAISpeakerDiarizer = new();
    private readonly ConcurrentQueue<AudioChunk> _pendingChunks = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private IAudioCapture? _audioCapture;
    private RealtimePcmAudioCapture? _openAIRealtimeCapture;
    private OpenAIRealtimeTranscriptionService? _openAIRealtimeTranscription;
    private WindowsSpeechRecognitionService? _windowsSpeechRecognition;
    private CancellationTokenSource? _captionCancellation;
    private Task? _captionTask;
    private bool _isRunning;
    private bool _isApplyingProfile;
    private string _activeEngineKey = "openai-cloud";
    private string _activeLanguage = "en";
    private string _activeAudioSourceName = "Windows system audio";
    private string _lastCaptionText = "";
    private TextBlock? _activeCaptionTextBlock;
    private string _activeCaptionSpeaker = "";
    private DateTime _lastCaptionAddedAt = DateTime.MinValue;
    private DateTime _lastAudibleChunkCapturedAt = DateTime.MinValue;
    private string _recognitionPrompt = "";
    private string _streamingCaptionPrefix = "";
    private DateTime _lastStreamingSpeechAt = DateTime.MinValue;
    private bool _streamingSpeechActive;
    private bool _pendingStreamingSpeakerTurn;
    private bool _startNewCaptionParagraph = true;
    private readonly List<SpeakerCluster> _speakerClusters = new();
    private string _currentVoskSpeaker = "Speaker 1";
    private DateTime _lastMissingSpeakerVectorWarningAt = DateTime.MinValue;
    private DateTime _lastOpenAIBacklogWarningAt = DateTime.MinValue;
    private string _openAIRealtimePartialText = "";
    private string _currentOpenAIRealtimeSpeaker = "Monologue";
    private bool _openAIRealtimeSpeakerSplitEnabled;

    public MainWindow()
    {
        AppLogger.Initialize(ProjectRoot);
        AppLogger.Info("MainWindow constructing.");
        AppLogger.Memory("Startup");
        InitializeComponent();
        _whisperModelManager.MoveLegacyModelIfNeeded();
        SelectAvailableModel();
        ApplySelectedProfile(updateStatus: false);
        UpdateSettingsVisibility();
        UpdateModelStatus();
        AppLogger.Info("MainWindow initialized.");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        try
        {
            AppLogger.Info("Start requested.");
            _activeEngineKey = GetSelectedEngineKey();
            _activeLanguage = GetSelectedLanguage();
            var modelKey = GetSelectedModelKey();
            AppLogger.Info($"Selected engine={_activeEngineKey}, language={_activeLanguage}, audioSource={GetSelectedAudioSourceName()}, whisperModel={modelKey}.");
            AppLogger.Memory("Before start");
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
                StatusText.Text = $"Download the {modelKey} model first, or select a model that already exists.";
                return;
            }

            if (IsOpenAIEngine(_activeEngineKey) && !_openAITranscription.HasApiKey)
            {
                UpdateModelStatus();
                StatusText.Text = "Set OPENAI_API_KEY before starting OpenAI cloud transcription.";
                return;
            }

            if (IsSherpaEngine(_activeEngineKey) && !_sherpaOnnxRecognition.HasRuntime)
            {
                UpdateModelStatus();
                StatusText.Text = "Sherpa-ONNX runtime bridge is not installed yet.";
                return;
            }

            _captionCancellation = new CancellationTokenSource();
            _pendingChunks.Clear();
            _lastCaptionText = "";
            _activeCaptionTextBlock = null;
            _activeCaptionSpeaker = "";
            _lastCaptionAddedAt = DateTime.MinValue;
            _lastAudibleChunkCapturedAt = DateTime.MinValue;
            _recognitionPrompt = "";
            _streamingCaptionPrefix = "";
            _lastStreamingSpeechAt = DateTime.MinValue;
            _streamingSpeechActive = false;
            _pendingStreamingSpeakerTurn = false;
            _startNewCaptionParagraph = true;
            _speakerClusters.Clear();
            _currentVoskSpeaker = "Speaker 1";
            _openAIRealtimePartialText = "";
            _openAIRealtimeSpeakerSplitEnabled = OpenAIRealtimeSpeakerSplitCheckBox.IsChecked == true;
            _currentOpenAIRealtimeSpeaker = _openAIRealtimeSpeakerSplitEnabled ? "Speaker 1" : "Monologue";
            _openAISpeakerDiarizer.Reset();
            _activeAudioSourceName = GetSelectedAudioSourceName();

            if (IsWindowsSpeechEngine(_activeEngineKey))
            {
                StartWindowsSpeechRecognition();
            }
            else if (IsVoskEngine(_activeEngineKey))
            {
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                StatusText.Text = _voskSpeechRecognition.IsModelLoaded(_activeLanguage)
                    ? "Starting Vosk with the already loaded model..."
                    : "Loading Vosk model...";
                await StartVoskRecognitionAsync();
            }
            else if (IsOpenAIEngine(_activeEngineKey) || IsSherpaEngine(_activeEngineKey))
            {
                if (IsOpenAIRealtimeEngine(_activeEngineKey))
                {
                    await StartOpenAIRealtimeRecognitionAsync();
                    _isRunning = true;
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = true;
                    ChunkSecondsSlider.IsEnabled = false;
                    AudioSourceComboBox.IsEnabled = false;
                    LanguageComboBox.IsEnabled = false;
                    EngineComboBox.IsEnabled = false;
                    ModelComboBox.IsEnabled = false;
                    OpenAIChunkSecondsSlider.IsEnabled = false;
                    ProfileComboBox.IsEnabled = false;
                    StatusText.Text = $"Streaming {_activeAudioSourceName.ToLowerInvariant()} to OpenAI realtime.";
                    AppLogger.Memory("After start");
                    AppLogger.Info("Start completed.");
                    return;
                }

                var chunkSeconds = IsOpenAIDiarizeEngine(_activeEngineKey)
                    ? TimeSpan.FromSeconds(Math.Round(OpenAIChunkSecondsSlider.Value))
                    : TimeSpan.FromSeconds(Math.Round(ChunkSecondsSlider.Value));
                _audioCapture = CreateAudioCapture(chunkSeconds);
                _audioCapture.AudioLevelChanged += OnAudioLevelChanged;
                _audioCapture.AudioChunkReady += OnAudioChunkReady;
                _audioCapture.Start();
                _captionTask = Task.Run(() => CaptionLoopAsync(_captionCancellation.Token));
            }
            else
            {
                StatusText.Text = "Loading Whisper model...";
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
            EngineComboBox.IsEnabled = false;
            ModelComboBox.IsEnabled = false;
            OpenAIChunkSecondsSlider.IsEnabled = false;
            ProfileComboBox.IsEnabled = false;
            StatusText.Text = IsWindowsSpeechEngine(_activeEngineKey)
                ? "Listening through Windows Speech Recognition."
                : IsVoskEngine(_activeEngineKey)
                ? $"Listening to {_activeAudioSourceName.ToLowerInvariant()}."
                : IsOpenAIEngine(_activeEngineKey)
                ? $"Listening to {_activeAudioSourceName.ToLowerInvariant()} and sending chunks to OpenAI."
                : IsSherpaEngine(_activeEngineKey)
                ? $"Listening to {_activeAudioSourceName.ToLowerInvariant()} through Sherpa-ONNX."
                : !_whisperModelManager.IsModelLoaded
                ? $"Listening to {_activeAudioSourceName.ToLowerInvariant()}. Download a Whisper model to get text."
                : $"Listening to {_activeAudioSourceName.ToLowerInvariant()} and recognizing speech.";
            AppLogger.Memory("After start");
            AppLogger.Info("Start completed.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Start failed.", ex);
            StopListening();
            MessageBox.Show(ex.Message, "LiveCaptioner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Stop requested.");
        StopListening();
        StatusText.Text = "Stopped. Loaded Vosk models are kept in memory for a faster next Start.";
        AppLogger.Memory("After stop");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CaptionPanel.Children.Clear();
        _lastCaptionText = "";
        _activeCaptionTextBlock = null;
        _activeCaptionSpeaker = "";
        _lastCaptionAddedAt = DateTime.MinValue;
        _lastAudibleChunkCapturedAt = DateTime.MinValue;
        _recognitionPrompt = "";
        _streamingCaptionPrefix = "";
        _lastStreamingSpeechAt = DateTime.MinValue;
        _streamingSpeechActive = false;
        _pendingStreamingSpeakerTurn = false;
        _startNewCaptionParagraph = true;
        _speakerClusters.Clear();
        _currentVoskSpeaker = "Speaker 1";
        _openAIRealtimePartialText = "";
        _openAIRealtimeSpeakerSplitEnabled = OpenAIRealtimeSpeakerSplitCheckBox.IsChecked == true;
        _currentOpenAIRealtimeSpeaker = _openAIRealtimeSpeakerSplitEnabled ? "Speaker 1" : "Monologue";
        _openAISpeakerDiarizer.Reset();
        StatusText.Text = "Text cleared.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var text = BuildTranscriptText();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Nothing to save.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save transcript",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"live-captions-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
            StatusText.Text = $"Saved: {dialog.FileName}";
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
                StatusText.Text = "Model already exists. No download is needed.";
                UpdateModelStatus();
                return;
            }

            StatusText.Text = $"Downloading {Path.GetFileName(modelPath)}. This can take a few minutes...";
            DownloadModelButton.IsEnabled = false;

            await _whisperModelManager.DownloadModelAsync(modelKey);
            UpdateModelStatus();
            StatusText.Text = "Model downloaded. You can press Start.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Model download failed.";
            MessageBox.Show(ex.Message, "Model download", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _isRunning || _isApplyingProfile)
        {
            return;
        }

        ApplySelectedProfile(updateStatus: true);
        UpdateSettingsVisibility();
        UpdateModelStatus();
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
            StatusText.Text = "Press Stop before switching to or from a streaming engine.";
            return;
        }

        _activeEngineKey = GetSelectedEngineKey();
        _recognitionPrompt = "";
        UpdateSettingsVisibility();
        UpdateModelStatus();
        StatusText.Text = $"Engine changed: {EngineComboBox.Text}";
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _isRunning)
        {
            return;
        }

        UpdateModelStatus();
    }

    private void AudioSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _isRunning)
        {
            return;
        }

        UpdateSettingsVisibility();
    }

    private void ApplySelectedProfile(bool updateStatus)
    {
        var profile = GetSelectedProfileKey();
        _isApplyingProfile = true;
        try
        {
            switch (profile)
            {
                case "meeting":
                    SelectComboBoxItemByTag(EngineComboBox, "vosk-local");
                    AudioSourceComboBox.SelectedIndex = 1;
                    VoskPartialResultsCheckBox.IsChecked = true;
                    VoskInterviewVocabularyCheckBox.IsChecked = false;
                    VoskSystemAudioGainSlider.Value = 1.6;
                    VoskSystemNoiseGateCheckBox.IsChecked = true;
                    VoskAutoSpeakerTurnsCheckBox.IsChecked = false;
                    VoskSpeakerVectorsCheckBox.IsChecked = true;
                    VoskSpeakerVectorThresholdSlider.Value = 0.68;
                    VoskSpeakerPauseSlider.Value = 4.0;
                    VoskSentenceFormattingCheckBox.IsChecked = true;
                    ProfileHintText.Text = "Optimized for meetings and calls: system audio, noise gate, speaker split.";
                    break;
                case "interview":
                    SelectComboBoxItemByTag(EngineComboBox, "vosk-local");
                    AudioSourceComboBox.SelectedIndex = 0;
                    VoskPartialResultsCheckBox.IsChecked = true;
                    VoskInterviewVocabularyCheckBox.IsChecked = true;
                    VoskSystemAudioGainSlider.Value = 1.4;
                    VoskSystemNoiseGateCheckBox.IsChecked = true;
                    VoskAutoSpeakerTurnsCheckBox.IsChecked = false;
                    VoskSpeakerVectorsCheckBox.IsChecked = true;
                    VoskSpeakerVectorThresholdSlider.Value = 0.66;
                    VoskSpeakerPauseSlider.Value = 4.0;
                    VoskSentenceFormattingCheckBox.IsChecked = true;
                    ProfileHintText.Text = "For interview practice: microphone, interview vocabulary, readable sentence formatting.";
                    break;
                case "dictation":
                    SelectComboBoxItemByTag(EngineComboBox, "vosk-local");
                    AudioSourceComboBox.SelectedIndex = 0;
                    VoskPartialResultsCheckBox.IsChecked = true;
                    VoskInterviewVocabularyCheckBox.IsChecked = false;
                    VoskSystemAudioGainSlider.Value = 1.2;
                    VoskSystemNoiseGateCheckBox.IsChecked = true;
                    VoskAutoSpeakerTurnsCheckBox.IsChecked = false;
                    VoskSpeakerVectorsCheckBox.IsChecked = false;
                    VoskSpeakerVectorThresholdSlider.Value = 0.65;
                    VoskSpeakerPauseSlider.Value = 5.0;
                    VoskSentenceFormattingCheckBox.IsChecked = true;
                    ProfileHintText.Text = "For one speaker: microphone, speaker split off, stable readable text.";
                    break;
                case "custom":
                    ProfileHintText.Text = "Manual mode. Advanced settings are available below.";
                    break;
                case "podcast":
                default:
                    SelectComboBoxItemByTag(EngineComboBox, "openai-cloud");
                    AudioSourceComboBox.SelectedIndex = 1;
                    LanguageComboBox.SelectedIndex = 2;
                    OpenAIChunkSecondsSlider.Value = 15;
                    VoskPartialResultsCheckBox.IsChecked = true;
                    VoskInterviewVocabularyCheckBox.IsChecked = false;
                    VoskSystemAudioGainSlider.Value = 1.8;
                    VoskSystemNoiseGateCheckBox.IsChecked = false;
                    VoskAutoSpeakerTurnsCheckBox.IsChecked = false;
                    VoskSpeakerVectorsCheckBox.IsChecked = true;
                    VoskSpeakerVectorThresholdSlider.Value = 0.64;
                    VoskSpeakerPauseSlider.Value = 4.0;
                    VoskSentenceFormattingCheckBox.IsChecked = true;
                    ProfileHintText.Text = "Default for English monologues and videos: OpenAI fast, system audio, one speaker.";
                    break;
            }
        }
        finally
        {
            _isApplyingProfile = false;
        }

        if (updateStatus)
        {
            StatusText.Text = $"Profile applied: {ProfileComboBox.Text}";
        }
    }

    private string GetSelectedProfileKey()
        => (ProfileComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "podcast";

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.Info("Application closing.");
        StopListening();
        _whisperModelManager.Dispose();
        _voskSpeechRecognition.Dispose();
        base.OnClosed(e);
    }

    private void OnAudioChunkReady(object? sender, AudioChunk chunk)
    {
        var maxPendingChunks = IsOpenAIEngine(_activeEngineKey)
            ? OpenAIMaxPendingChunks
            : DefaultMaxPendingChunks;
        var droppedChunks = 0;
        while (_pendingChunks.Count >= maxPendingChunks && _pendingChunks.TryDequeue(out _))
        {
            droppedChunks++;
        }

        if (droppedChunks > 0)
        {
            AppLogger.Warn($"Recognition queue backlog exceeded {maxPendingChunks}; dropped {droppedChunks} old audio chunk(s).");
            if (IsOpenAIEngine(_activeEngineKey) &&
                DateTime.Now - _lastOpenAIBacklogWarningAt > TimeSpan.FromSeconds(10))
            {
                _lastOpenAIBacklogWarningAt = DateTime.Now;
                Dispatcher.Invoke(() => StatusText.Text = "OpenAI is behind the live audio. Increase chunk length or wait for the queue to catch up.");
            }
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
                ? $"Source: {_activeAudioSourceName} - signal"
                : $"Source: {_activeAudioSourceName} - silence";
        });
    }

    private void StartWindowsSpeechRecognition()
    {
        _windowsSpeechRecognition = new WindowsSpeechRecognitionService();
        _windowsSpeechRecognition.AudioLevelChanged += OnWindowsSpeechAudioLevelChanged;
        _windowsSpeechRecognition.TextRecognized += OnWindowsSpeechTextRecognized;
        _windowsSpeechRecognition.Start(_activeLanguage);
        _activeAudioSourceName = "Microphone / Windows Speech";
    }

    private void OnWindowsSpeechAudioLevelChanged(object? sender, double level)
    {
        Dispatcher.Invoke(() =>
        {
            AudioLevelBar.Value = Math.Clamp(level * 3.5, 0, 1);
            AudioStatusText.Text = level > 0.01
                ? "Source: Microphone / Windows Speech - signal"
                : "Source: Microphone / Windows Speech - silence";
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

    private async Task StartVoskRecognitionAsync()
    {
        AppLogger.Info("Preparing Vosk recognition.");
        _voskSpeechRecognition.AudioLevelChanged += OnVoskAudioLevelChanged;
        _voskSpeechRecognition.PartialTextRecognized += OnVoskPartialTextRecognized;
        _voskSpeechRecognition.TextRecognized += OnVoskTextRecognized;
        var audioSource = GetSelectedVoskAudioSource();
        var options = new VoskRecognitionOptions(
            VoskPartialResultsCheckBox.IsChecked == true,
            VoskInterviewVocabularyCheckBox.IsChecked == true,
            VoskSystemAudioGainSlider.Value,
            VoskSystemNoiseGateCheckBox.IsChecked == true);
        _activeAudioSourceName = audioSource == VoskAudioSource.SystemAudio
            ? "Windows system audio / Vosk"
            : "Microphone / Vosk";
        AppLogger.Info($"Starting Vosk: language={_activeLanguage}, audioSource={audioSource}, partial={options.EnablePartialResults}, vocabulary={options.UseInterviewVocabulary}, gain={options.SystemAudioGain:0.00}, noiseGate={options.SystemAudioNoiseGate}.");
        await Task.Run(() => _voskSpeechRecognition.Start(_activeLanguage, audioSource, options));
        AppLogger.Info("Vosk recognition started.");
    }

    private void OnVoskAudioLevelChanged(object? sender, double level)
    {
        Dispatcher.Invoke(() =>
        {
            AudioLevelBar.Value = Math.Clamp(level * 3.5, 0, 1);
            TrackVoskLongSilence(level);
            AudioStatusText.Text = level > 0.01
                ? $"Source: {_activeAudioSourceName} - signal"
                : $"Source: {_activeAudioSourceName} - silence";
        });
    }

    private void OnVoskTextRecognized(object? sender, VoskRecognitionResult result)
    {
        AppLogger.Info($"Vosk final text length={result.Text.Length}, speakerVector={(result.SpeakerVector == null ? "none" : result.SpeakerVector.Length)}.");
        Dispatcher.Invoke(() =>
        {
            var previousSpeaker = _currentVoskSpeaker;
            var speakerResolvedByVector = VoskSpeakerVectorsCheckBox.IsChecked == true &&
                                          result.SpeakerVector is { Length: > 0 };
            var speaker = ResolveVoskSpeaker(result.SpeakerVector, result.Text);

            if (speakerResolvedByVector)
            {
                _pendingStreamingSpeakerTurn = false;
                _currentVoskSpeaker = speaker;
                if (!string.Equals(previousSpeaker, speaker, StringComparison.Ordinal))
                {
                    _streamingCaptionPrefix = "";
                    _activeCaptionTextBlock = null;
                    _startNewCaptionParagraph = true;
                }
            }

            CommitStreamingCaptionLine(speaker, result.Text, speakerResolvedByVector);
        });
    }

    private void OnVoskPartialTextRecognized(object? sender, string text)
    {
        Dispatcher.Invoke(() => UpdateStreamingCaptionLine(GetVoskCaptionSpeaker(), text));
    }

    private async Task StartOpenAIRealtimeRecognitionAsync()
    {
        _activeAudioSourceName = AudioSourceComboBox.SelectedIndex == 1
            ? "Windows system audio / OpenAI realtime"
            : "Microphone / OpenAI realtime";
        _openAIRealtimeTranscription = new OpenAIRealtimeTranscriptionService();
        _openAIRealtimeTranscription.PartialTextReceived += OnOpenAIRealtimePartialTextReceived;
        _openAIRealtimeTranscription.FinalTextReceived += OnOpenAIRealtimeFinalTextReceived;
        _openAIRealtimeTranscription.StatusReceived += OnOpenAIRealtimeStatusReceived;
        _openAIRealtimeTranscription.ErrorReceived += OnOpenAIRealtimeErrorReceived;
        await _openAIRealtimeTranscription.StartAsync(_activeLanguage, _captionCancellation!.Token);

        _openAIRealtimeCapture = new RealtimePcmAudioCapture(
            AudioSourceComboBox.SelectedIndex == 1,
            VoskSystemAudioGainSlider.Value);
        _openAIRealtimeCapture.AudioLevelChanged += OnAudioLevelChanged;
        _openAIRealtimeCapture.PcmAvailable += OnOpenAIRealtimePcmAvailable;
        _openAIRealtimeCapture.Start();
        AppLogger.Info($"OpenAI realtime recognition started: language={_activeLanguage}, source={_activeAudioSourceName}.");
    }

    private void OnOpenAIRealtimePcmAvailable(object? sender, byte[] pcmBytes)
    {
        var service = _openAIRealtimeTranscription;
        var cancellationToken = _captionCancellation?.Token ?? CancellationToken.None;
        if (service == null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_openAIRealtimeSpeakerSplitEnabled)
        {
            var speaker = _openAISpeakerDiarizer.AddPcm(pcmBytes);
            if (!string.Equals(speaker, _currentOpenAIRealtimeSpeaker, StringComparison.Ordinal))
            {
                var carriedPartialText = _openAIRealtimePartialText;
                _currentOpenAIRealtimeSpeaker = speaker;
                Dispatcher.Invoke(() =>
                {
                    TrimActiveStreamingPartial();
                    _openAIRealtimePartialText = carriedPartialText;
                    _streamingCaptionPrefix = "";
                    _activeCaptionTextBlock = null;
                    _activeCaptionSpeaker = "";
                    _startNewCaptionParagraph = true;
                    if (!string.IsNullOrWhiteSpace(carriedPartialText))
                    {
                        UpdateStreamingCaptionLine(_currentOpenAIRealtimeSpeaker, "", speakerLocked: true);
                    }

                    StatusText.Text = $"Detected {_currentOpenAIRealtimeSpeaker}.";
                });
            }
        }

        _ = service.SendAudioAsync(pcmBytes, cancellationToken);
    }

    private void OnOpenAIRealtimePartialTextReceived(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            _openAIRealtimePartialText = AppendCaptionText(_openAIRealtimePartialText, text);
            UpdateStreamingCaptionLine(GetOpenAIRealtimeSpeaker(), _openAIRealtimePartialText, speakerLocked: true);
        });
    }

    private void OnOpenAIRealtimeFinalTextReceived(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            _openAIRealtimePartialText = "";
            CommitStreamingCaptionLine(GetOpenAIRealtimeSpeaker(), text, speakerLocked: true);
        });
    }

    private string GetOpenAIRealtimeSpeaker()
    {
        return _openAIRealtimeSpeakerSplitEnabled
            ? _currentOpenAIRealtimeSpeaker
            : "Monologue";
    }

    private void OnOpenAIRealtimeStatusReceived(object? sender, string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }

    private void OnOpenAIRealtimeErrorReceived(object? sender, string text)
    {
        AppLogger.Warn($"OpenAI realtime error: {text}");
        Dispatcher.Invoke(() => AddSystemLine($"OpenAI realtime error: {text}"));
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

            if (IsWhisperEngine(_activeEngineKey) && !_whisperModelManager.IsModelLoaded)
            {
                Dispatcher.Invoke(() => AddSystemLine($"Audio received: {chunk.Duration.TotalSeconds:0} sec., RMS {chunk.Level:P0}. Whisper model was not found."));
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
                if (IsOpenAIEngine(_activeEngineKey))
                {
                    await ProcessOpenAIChunkAsync(chunk, cancellationToken);
                    continue;
                }

                if (IsSherpaEngine(_activeEngineKey))
                {
                    await ProcessSherpaChunkAsync(chunk, cancellationToken);
                    continue;
                }

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
                    Dispatcher.Invoke(() => StatusText.Text = "No speech was found in the last chunk.");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AddSystemLine($"Recognition error: {ex.Message}"));
            }
        }
    }

    private async Task ProcessOpenAIChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        var model = IsOpenAIDiarizeEngine(_activeEngineKey)
            ? "gpt-4o-transcribe-diarize"
            : "gpt-4o-mini-transcribe";
        var queuedChunks = _pendingChunks.Count;
        AppLogger.Info($"OpenAI chunk queued={queuedChunks}, duration={chunk.Duration.TotalSeconds:0.0}s, level={chunk.Level:0.0000}, model={model}.");
        Dispatcher.Invoke(() => StatusText.Text = queuedChunks > 0
            ? $"Sending audio to OpenAI. Queue: {queuedChunks} chunk(s)."
            : "Sending audio to OpenAI.");
        var result = await _openAITranscription.TranscribeAsync(
            chunk.WavBytes,
            model,
            _activeLanguage,
            IsOpenAIDiarizeEngine(_activeEngineKey) ? "" : _recognitionPrompt,
            cancellationToken);

        if (IsOpenAIDiarizeEngine(_activeEngineKey) && result.HasSpeakerSegments)
        {
            foreach (var segment in result.Segments)
            {
                if (ShouldAddCaption(segment.Text))
                {
                    Dispatcher.Invoke(() => AddCaptionLine(segment.Speaker, segment.Text));
                }
            }

            return;
        }

        if (ShouldAddCaption(result.Text))
        {
            Dispatcher.Invoke(() => AddCaptionLine(_activeAudioSourceName, result.Text));
        }
        else
        {
            Dispatcher.Invoke(() => StatusText.Text = "OpenAI returned no new text for the last chunk.");
        }
    }

    private async Task ProcessSherpaChunkAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        var text = await _sherpaOnnxRecognition.TranscribeAsync(
            chunk.WavBytes,
            _activeLanguage,
            cancellationToken);
        if (ShouldAddCaption(text))
        {
            Dispatcher.Invoke(() => AddCaptionLine(_activeAudioSourceName, text));
        }
    }

    private void UpdateModelStatus()
    {
        if (!IsInitialized)
        {
            return;
        }

        var engineKey = GetSelectedEngineKey();
        if (IsVoskEngine(engineKey))
        {
            var language = GetSelectedLanguage();
            var voskModelPath = _voskSpeechRecognition.GetModelPath(language);
            var hasVoskModel = _voskSpeechRecognition.HasModel(language);
            var isLoaded = _voskSpeechRecognition.IsModelLoaded(language);
            ModelStatusText.Text = hasVoskModel
                ? isLoaded
                    ? $"Vosk model loaded: {voskModelPath}"
                    : $"Vosk model ready: {voskModelPath}"
                : $"Vosk model not found: {voskModelPath}";
            var speakerModelText = _voskSpeechRecognition.HasSpeakerModel()
                ? "Speaker model: vosk-model-spk-0.4 is available"
                : "Speaker model not found: voice-based speaker split is unavailable";
            VoskModelStatusText.Text = hasVoskModel
                ? $"{(isLoaded ? "Loaded" : "Ready")}: {Path.GetFileName(voskModelPath)}\n{speakerModelText}"
                : $"Language {language} needs a model folder: {voskModelPath}\n{speakerModelText}";
            return;
        }

        if (IsWindowsSpeechEngine(engineKey))
        {
            ModelStatusText.Text = "Windows Speech Recognition: no external model needed";
            return;
        }

        if (IsOpenAIEngine(engineKey))
        {
            ModelStatusText.Text = _openAITranscription.HasApiKey
                ? "OpenAI cloud: OPENAI_API_KEY is set"
                : "OpenAI cloud: OPENAI_API_KEY is not set";
            OpenAIStatusText.Text = IsOpenAIDiarizeEngine(engineKey)
                ? "Uses gpt-4o-transcribe-diarize. Speaker labels are returned per audio chunk, with higher latency."
                : "Uses OpenAI realtime transcription for one-speaker low-latency captions. Set OPENAI_API_KEY in the environment.";
            return;
        }

        if (IsSherpaEngine(engineKey))
        {
            ModelStatusText.Text = _sherpaOnnxRecognition.HasRuntime
                ? $"Sherpa-ONNX runtime ready: {_sherpaOnnxRecognition.RuntimePath}"
                : "Sherpa-ONNX runtime bridge is not installed";
            SherpaStatusText.Text = _sherpaOnnxRecognition.HasRuntime
                ? $"Runtime: {_sherpaOnnxRecognition.RuntimePath}"
                : $"Expected runtime bridge: {_sherpaOnnxRecognition.RuntimePath}";
            return;
        }

        var modelKey = GetSelectedModelKey();
        var modelPath = _whisperModelManager.GetModelPath(modelKey);
        var hasModel = _whisperModelManager.HasModel(modelKey);
        ModelStatusText.Text = hasModel
            ? $"Model ready: {modelPath}"
            : $"Whisper model not found: {modelPath}";
        DownloadModelButton.Content = hasModel ? "Model already downloaded" : $"Download {GetSelectedModelKey()} model";
        DownloadModelButton.IsEnabled = !hasModel;
    }

    private void UpdateSettingsVisibility()
    {
        if (!IsInitialized)
        {
            return;
        }

        var engineKey = GetSelectedEngineKey();
        var isVosk = IsVoskEngine(engineKey);
        var isWhisper = IsWhisperEngine(engineKey);
        var isWindowsSpeech = IsWindowsSpeechEngine(engineKey);
        var isOpenAI = IsOpenAIEngine(engineKey);
        var isOpenAIDiarize = IsOpenAIDiarizeEngine(engineKey);
        var isSherpa = IsSherpaEngine(engineKey);
        var isSystemAudio = AudioSourceComboBox.SelectedIndex == 1;

        VoskSettingsPanel.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        WhisperSettingsPanel.Visibility = isWhisper ? Visibility.Visible : Visibility.Collapsed;
        WindowsSpeechSettingsPanel.Visibility = isWindowsSpeech ? Visibility.Visible : Visibility.Collapsed;
        OpenAISettingsPanel.Visibility = isOpenAI ? Visibility.Visible : Visibility.Collapsed;
        SherpaSettingsPanel.Visibility = isSherpa ? Visibility.Visible : Visibility.Collapsed;
        OpenAIChunkLabel.Text = isOpenAIDiarize ? "Audio chunk length, sec." : "Realtime streaming";
        OpenAIRealtimeSpeakerSplitCheckBox.Visibility = isOpenAI && !isOpenAIDiarize ? Visibility.Visible : Visibility.Collapsed;
        OpenAIChunkSecondsSlider.Visibility = isOpenAIDiarize ? Visibility.Visible : Visibility.Collapsed;
        OpenAIChunkSecondsValueText.Visibility = isOpenAIDiarize ? Visibility.Visible : Visibility.Collapsed;
        OpenAIHelpText.Text = isOpenAIDiarize
            ? "Uses gpt-4o-transcribe-diarize for speaker labels. This is slower because it sends audio chunks."
            : "Streams audio to OpenAI realtime and treats the result as one Monologue speaker.";

        VoskSystemAudioGainLabel.Visibility = isVosk && isSystemAudio ? Visibility.Visible : Visibility.Collapsed;
        VoskSystemAudioGainSlider.Visibility = isVosk && isSystemAudio ? Visibility.Visible : Visibility.Collapsed;
        VoskSystemAudioGainValueText.Visibility = isVosk && isSystemAudio ? Visibility.Visible : Visibility.Collapsed;
        VoskSystemNoiseGateCheckBox.Visibility = isVosk && isSystemAudio ? Visibility.Visible : Visibility.Collapsed;
        VoskAutoSpeakerTurnsCheckBox.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerVectorsCheckBox.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerVectorThresholdLabel.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerVectorThresholdSlider.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerVectorThresholdValueText.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerPauseLabel.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerPauseSlider.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
        VoskSpeakerPauseValueText.Visibility = isVosk ? Visibility.Visible : Visibility.Collapsed;
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
        AppLogger.Info("Stopping audio/recognition services.");
        _audioCapture?.Dispose();
        _audioCapture = null;
        if (_openAIRealtimeCapture != null)
        {
            _openAIRealtimeCapture.PcmAvailable -= OnOpenAIRealtimePcmAvailable;
            _openAIRealtimeCapture.AudioLevelChanged -= OnAudioLevelChanged;
            _openAIRealtimeCapture.Dispose();
            _openAIRealtimeCapture = null;
        }

        if (_openAIRealtimeTranscription != null)
        {
            _openAIRealtimeTranscription.PartialTextReceived -= OnOpenAIRealtimePartialTextReceived;
            _openAIRealtimeTranscription.FinalTextReceived -= OnOpenAIRealtimeFinalTextReceived;
            _openAIRealtimeTranscription.StatusReceived -= OnOpenAIRealtimeStatusReceived;
            _openAIRealtimeTranscription.ErrorReceived -= OnOpenAIRealtimeErrorReceived;
            _openAIRealtimeTranscription.Dispose();
            _openAIRealtimeTranscription = null;
        }

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
        EngineComboBox.IsEnabled = true;
        ModelComboBox.IsEnabled = true;
        OpenAIChunkSecondsSlider.IsEnabled = true;
        ProfileComboBox.IsEnabled = true;
        AudioLevelBar.Value = 0;
        UpdateModelStatus();
        AppLogger.Info("Services stopped.");
    }

    private void TrackVoskLongSilence(double level)
    {
        if (!IsVoskEngine(_activeEngineKey) ||
            VoskAutoSpeakerTurnsCheckBox.IsChecked != true)
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
            now - _lastStreamingSpeechAt > TimeSpan.FromSeconds(VoskSpeakerPauseSlider.Value))
        {
            _streamingSpeechActive = false;
            _pendingStreamingSpeakerTurn = !string.IsNullOrWhiteSpace(_streamingCaptionPrefix);
        }
    }

    private IAudioCapture CreateAudioCapture(TimeSpan chunkDuration)
    {
        var overlap = IsOpenAIEngine(_activeEngineKey)
            ? OpenAIOverlap
            : LowLatencyOverlap;
        return AudioSourceComboBox.SelectedIndex == 1
            ? new SystemAudioCapture(chunkDuration, overlap)
            : new MicrophoneAudioCapture(chunkDuration, overlap);
    }

    private string GetSelectedAudioSourceName()
    {
        return AudioSourceComboBox.SelectedIndex == 1
            ? "Windows system audio"
            : "Microphone";
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

    private static bool IsOpenAIEngine(string engineKey)
        => engineKey.StartsWith("openai-", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenAIRealtimeEngine(string engineKey)
        => engineKey.Equals("openai-cloud", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenAIDiarizeEngine(string engineKey)
        => engineKey.Equals("openai-diarize", StringComparison.OrdinalIgnoreCase);

    private static bool IsSherpaEngine(string engineKey)
        => engineKey.Equals("sherpa-onnx", StringComparison.OrdinalIgnoreCase);

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
            _activeCaptionSpeaker = speaker;
            _startNewCaptionParagraph = false;
        }
        else
        {
            _activeCaptionTextBlock.Text = AppendCaptionText(_activeCaptionTextBlock.Text, text);
        }

        _lastCaptionAddedAt = now;
        CaptionScrollViewer.ScrollToEnd();
        StatusText.Text = "Recognized.";
    }

    private void UpdateStreamingCaptionLine(string speaker, string partialText, bool speakerLocked = false)
    {
        if (!speakerLocked)
        {
            ApplyPendingStreamingParagraphBreak();
            speaker = GetVoskCaptionSpeaker();
        }

        var displayText = AppendCaptionText(_streamingCaptionPrefix, partialText);
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return;
        }

        if (_activeCaptionTextBlock == null ||
            _startNewCaptionParagraph ||
            !string.Equals(_activeCaptionSpeaker, speaker, StringComparison.Ordinal))
        {
            _activeCaptionTextBlock = CreateCaptionParagraph(speaker, displayText);
            _activeCaptionSpeaker = speaker;
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
        StatusText.Text = "Recognizing...";
    }

    private void CommitStreamingCaptionLine(string speaker, string finalText, bool speakerLocked = false)
    {
        if (string.IsNullOrWhiteSpace(finalText))
        {
            return;
        }

        if (!speakerLocked)
        {
            ApplyPendingStreamingParagraphBreak();
            speaker = GetVoskCaptionSpeaker();
        }
        else
        {
            _currentVoskSpeaker = speaker;
        }

        var formattedText = VoskSentenceFormattingCheckBox.IsChecked == true
            ? FormatCaptionSentence(finalText)
            : finalText.Trim();
        _streamingCaptionPrefix = AppendCaptionText(_streamingCaptionPrefix, formattedText);
        UpdateStreamingCaptionLine(speaker, "", speakerLocked);
        StatusText.Text = "Recognized.";
    }

    private void TrimActiveStreamingPartial()
    {
        if (_activeCaptionTextBlock == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_streamingCaptionPrefix))
        {
            _activeCaptionTextBlock.Text = _streamingCaptionPrefix;
            return;
        }

        var row = _activeCaptionTextBlock.Parent;
        while (row is FrameworkElement { Parent: not null } element && row is not Border)
        {
            row = element.Parent;
        }

        if (row is Border border)
        {
            CaptionPanel.Children.Remove(border);
        }

        _activeCaptionTextBlock = null;
        _activeCaptionSpeaker = "";
    }

    private void ApplyPendingStreamingParagraphBreak()
    {
        if (VoskAutoSpeakerTurnsCheckBox.IsChecked != true || !_pendingStreamingSpeakerTurn)
        {
            return;
        }

        _pendingStreamingSpeakerTurn = false;
        _streamingCaptionPrefix = "";
        _activeCaptionTextBlock = null;
        _startNewCaptionParagraph = true;
    }

    private bool ShouldUseVoskSpeakerVectors()
    {
        return IsVoskEngine(_activeEngineKey) &&
               VoskSpeakerVectorsCheckBox.IsChecked == true;
    }

    private string GetVoskCaptionSpeaker()
    {
        if (ShouldUseVoskSpeakerVectors())
        {
            return _currentVoskSpeaker;
        }

        return _activeAudioSourceName;
    }

    private string ResolveVoskSpeaker(float[]? speakerVector, string text)
    {
        if (VoskSpeakerVectorsCheckBox.IsChecked != true || speakerVector == null || speakerVector.Length == 0)
        {
            if (VoskSpeakerVectorsCheckBox.IsChecked == true &&
                DateTime.Now - _lastMissingSpeakerVectorWarningAt > TimeSpan.FromSeconds(10))
            {
                _lastMissingSpeakerVectorWarningAt = DateTime.Now;
                AppLogger.Warn("Vosk final result has no speaker vector. Check that Models\\vosk-model-spk-0.4 is loaded and compatible.");
                StatusText.Text = "Vosk did not return a voice vector; keeping the current speaker.";
            }

            return GetVoskCaptionSpeaker();
        }

        var matchThreshold = VoskSpeakerVectorThresholdSlider.Value;
        var continuityThreshold = Math.Max(0.35, matchThreshold - 0.20);
        var normalizedText = NormalizeCaptionText(text);
        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;

        for (var i = 0; i < _speakerClusters.Count; i++)
        {
            var score = CosineSimilarity(_speakerClusters[i].Centroid, speakerVector);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0 && bestScore >= matchThreshold)
        {
            _speakerClusters[bestIndex].Update(speakerVector);
            AppLogger.Info($"Speaker vector matched {_speakerClusters[bestIndex].Name}, score={bestScore:0.000}.");
            return _speakerClusters[bestIndex].Name;
        }

        var currentCluster = _speakerClusters.FirstOrDefault(cluster =>
            string.Equals(cluster.Name, _currentVoskSpeaker, StringComparison.Ordinal));
        if (currentCluster != null)
        {
            var currentScore = CosineSimilarity(currentCluster.Centroid, speakerVector);
            var shortPhrase = normalizedText.Length < 40;
            if (currentScore >= continuityThreshold || shortPhrase)
            {
                currentCluster.Update(speakerVector);
                AppLogger.Info($"Speaker vector kept {_currentVoskSpeaker}, score={currentScore:0.000}, bestScore={(double.IsNegativeInfinity(bestScore) ? "none" : bestScore.ToString("0.000"))}, shortPhrase={shortPhrase}.");
                return _currentVoskSpeaker;
            }
        }

        var speakerName = $"Speaker {_speakerClusters.Count + 1}";
        _speakerClusters.Add(new SpeakerCluster(speakerName, speakerVector));
        AppLogger.Info($"Speaker vector created {speakerName}, bestScore={(double.IsNegativeInfinity(bestScore) ? "none" : bestScore.ToString("0.000"))}.");
        return speakerName;
    }

    private static double CosineSimilarity(float[] first, float[] second)
    {
        var length = Math.Min(first.Length, second.Length);
        if (length == 0)
        {
            return 0;
        }

        double dot = 0;
        double firstNorm = 0;
        double secondNorm = 0;

        for (var i = 0; i < length; i++)
        {
            dot += first[i] * second[i];
            firstNorm += first[i] * first[i];
            secondNorm += second[i] * second[i];
        }

        if (firstNorm <= 0 || secondNorm <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(firstNorm) * Math.Sqrt(secondNorm));
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

public sealed class SpeakerCluster
{
    public string Name { get; }
    public float[] Centroid { get; private set; }
    private int _sampleCount;

    public SpeakerCluster(string name, float[] vector)
    {
        Name = name;
        Centroid = (float[])vector.Clone();
        _sampleCount = 1;
    }

    public void Update(float[] vector)
    {
        var length = Math.Min(Centroid.Length, vector.Length);
        for (var i = 0; i < length; i++)
        {
            Centroid[i] = (Centroid[i] * _sampleCount + vector[i]) / (_sampleCount + 1);
        }

        _sampleCount++;
    }
}

