using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace LiveCaptioner;

public partial class MainWindow : Window
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private readonly string _modelPath = Path.Combine(ProjectRoot, "Models", "ggml-base.bin");
    private readonly string _legacyModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.bin");
    private readonly ConcurrentQueue<AudioChunk> _pendingChunks = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private IAudioCapture? _audioCapture;
    private CancellationTokenSource? _captionCancellation;
    private WhisperFactory? _whisperFactory;
    private Task? _captionTask;
    private bool _isRunning;
    private string _activeLanguage = "auto";
    private string _activeAudioSourceName = "Микрофон";

    public MainWindow()
    {
        InitializeComponent();
        MoveLegacyModelIfNeeded();
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
            _captionCancellation = new CancellationTokenSource();
            _pendingChunks.Clear();
            _activeLanguage = GetSelectedLanguage();
            _activeAudioSourceName = GetSelectedAudioSourceName();

            await EnsureWhisperFactoryAsync(_captionCancellation.Token);

            var chunkSeconds = TimeSpan.FromSeconds(Math.Round(ChunkSecondsSlider.Value));
            _audioCapture = CreateAudioCapture(chunkSeconds);
            _audioCapture.AudioLevelChanged += OnAudioLevelChanged;
            _audioCapture.AudioChunkReady += OnAudioChunkReady;
            _audioCapture.Start();

            _captionTask = Task.Run(() => CaptionLoopAsync(_captionCancellation.Token));
            _isRunning = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            ChunkSecondsSlider.IsEnabled = false;
            AudioSourceComboBox.IsEnabled = false;
            LanguageComboBox.IsEnabled = false;
            StatusText.Text = _whisperFactory == null
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
            if (File.Exists(_modelPath))
            {
                StatusText.Text = "Модель уже скачана. Повторная загрузка не нужна.";
                UpdateModelStatus();
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            StatusText.Text = "Скачиваю ggml-base.bin. Это может занять несколько минут...";
            DownloadModelButton.IsEnabled = false;

            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
            await using var fileWriter = File.Create(_modelPath);
            await modelStream.CopyToAsync(fileWriter);

            _whisperFactory?.Dispose();
            _whisperFactory = null;
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

    protected override void OnClosed(EventArgs e)
    {
        StopListening();
        _whisperFactory?.Dispose();
        base.OnClosed(e);
    }

    private void OnAudioChunkReady(object? sender, AudioChunk chunk)
    {
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

    private async Task CaptionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(cancellationToken);

            if (!_pendingChunks.TryDequeue(out var chunk))
            {
                continue;
            }

            if (_whisperFactory == null)
            {
                Dispatcher.Invoke(() => AddSystemLine($"Аудио получено: {chunk.Duration.TotalSeconds:0} сек., RMS {chunk.Level:P0}. Модель Whisper не найдена."));
                continue;
            }

            if (chunk.Level < 0.004)
            {
                continue;
            }

            try
            {
                await using var stream = new MemoryStream(chunk.WavBytes, writable: false);
                using var processor = CreateProcessor();

                var hasText = false;
                await foreach (var result in processor.ProcessAsync(stream, cancellationToken))
                {
                    var text = result.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    hasText = true;
                    Dispatcher.Invoke(() => AddCaptionLine(_activeAudioSourceName, text));
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

    private WhisperProcessor CreateProcessor()
    {
        var builder = _whisperFactory!.CreateBuilder()
            .WithNoContext()
            .WithSingleSegment();

        if (_activeLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            builder.WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(_activeLanguage);
        }

        return builder.Build();
    }

    private async Task EnsureWhisperFactoryAsync(CancellationToken cancellationToken)
    {
        if (_whisperFactory != null || !File.Exists(_modelPath))
        {
            UpdateModelStatus();
            return;
        }

        StatusText.Text = "Загружаю модель Whisper...";
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _whisperFactory = WhisperFactory.FromPath(_modelPath);
        }, cancellationToken);
        UpdateModelStatus();
    }

    private void UpdateModelStatus()
    {
        var hasModel = File.Exists(_modelPath);
        ModelStatusText.Text = hasModel
            ? $"Модель готова: {_modelPath}"
            : $"Модель Whisper не найдена: {_modelPath}";
        DownloadModelButton.Content = hasModel ? "Модель уже скачана" : "Скачать base-модель";
        DownloadModelButton.IsEnabled = !hasModel;
    }

    private void MoveLegacyModelIfNeeded()
    {
        if (File.Exists(_modelPath) || !File.Exists(_legacyModelPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        File.Move(_legacyModelPath, _modelPath);
        StatusText.Text = "Найдена ранее скачанная модель. Перенес ее в постоянную папку проекта.";
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
        _captionCancellation?.Dispose();
        _captionCancellation = null;
        _isRunning = false;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ChunkSecondsSlider.IsEnabled = true;
        AudioSourceComboBox.IsEnabled = true;
        LanguageComboBox.IsEnabled = true;
        AudioLevelBar.Value = 0;
    }

    private IAudioCapture CreateAudioCapture(TimeSpan chunkDuration)
    {
        return AudioSourceComboBox.SelectedIndex == 1
            ? new SystemAudioCapture(chunkDuration)
            : new MicrophoneAudioCapture(chunkDuration);
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
                    new TextBlock
                    {
                        Text = text,
                        Foreground = Brushes.White,
                        FontSize = 19,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 5, 0, 0)
                    }
                }
            }
        };

        CaptionPanel.Children.Add(row);
        CaptionScrollViewer.ScrollToEnd();
        StatusText.Text = "Распознано.";
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
}

public interface IAudioCapture : IDisposable
{
    event EventHandler<AudioChunk>? AudioChunkReady;
    event EventHandler<double>? AudioLevelChanged;
    void Start();
}

public sealed class SystemAudioCapture : IAudioCapture
{
    private readonly TimeSpan _chunkDuration;
    private readonly List<byte> _buffer = new();
    private readonly object _syncRoot = new();
    private WasapiLoopbackCapture? _capture;
    private int _chunkByteCount;

    public event EventHandler<AudioChunk>? AudioChunkReady;
    public event EventHandler<double>? AudioLevelChanged;

    public SystemAudioCapture(TimeSpan chunkDuration)
    {
        _chunkDuration = chunkDuration;
    }

    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        _chunkByteCount = Math.Max(_capture.WaveFormat.AverageBytesPerSecond, (int)(_capture.WaveFormat.AverageBytesPerSecond * _chunkDuration.TotalSeconds));
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    public void Dispose()
    {
        if (_capture == null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null || e.BytesRecorded <= 0)
        {
            return;
        }

        var level = CalculateRms(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
        AudioLevelChanged?.Invoke(this, level);

        lock (_syncRoot)
        {
            _buffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());

            while (_buffer.Count >= _chunkByteCount)
            {
                var chunkBytes = _buffer.GetRange(0, _chunkByteCount).ToArray();
                _buffer.RemoveRange(0, _chunkByteCount);
                var wavBytes = AudioMath.CreateWavBytes(chunkBytes, _capture.WaveFormat);
                var chunkLevel = AudioMath.CalculateRms(chunkBytes, chunkBytes.Length, _capture.WaveFormat);
                AudioChunkReady?.Invoke(this, new AudioChunk(wavBytes, _chunkDuration, chunkLevel));
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AudioChunkReady?.Invoke(this, new AudioChunk([], TimeSpan.Zero, 0));
        }
    }

    private static double CalculateRms(byte[] buffer, int bytesRecorded, WaveFormat format)
        => AudioMath.CalculateRms(buffer, bytesRecorded, format);
}

public sealed class MicrophoneAudioCapture : IAudioCapture
{
    private readonly TimeSpan _chunkDuration;
    private readonly List<byte> _buffer = new();
    private readonly object _syncRoot = new();
    private WaveInEvent? _capture;
    private WaveFormat _waveFormat = new(16000, 16, 1);
    private int _chunkByteCount;

    public event EventHandler<AudioChunk>? AudioChunkReady;
    public event EventHandler<double>? AudioLevelChanged;

    public MicrophoneAudioCapture(TimeSpan chunkDuration)
    {
        _chunkDuration = chunkDuration;
    }

    public void Start()
    {
        if (WaveInEvent.DeviceCount <= 0)
        {
            throw new InvalidOperationException("Микрофон не найден. Проверьте устройство записи в Windows.");
        }

        _capture = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = _waveFormat,
            BufferMilliseconds = 100
        };

        _chunkByteCount = Math.Max(_waveFormat.AverageBytesPerSecond, (int)(_waveFormat.AverageBytesPerSecond * _chunkDuration.TotalSeconds));
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    public void Dispose()
    {
        if (_capture == null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var level = AudioMath.CalculateRms(e.Buffer, e.BytesRecorded, _waveFormat);
        AudioLevelChanged?.Invoke(this, level);

        lock (_syncRoot)
        {
            _buffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());

            while (_buffer.Count >= _chunkByteCount)
            {
                var chunkBytes = _buffer.GetRange(0, _chunkByteCount).ToArray();
                _buffer.RemoveRange(0, _chunkByteCount);
                var wavBytes = AudioMath.CreateWavBytes(chunkBytes, _waveFormat);
                var chunkLevel = AudioMath.CalculateRms(chunkBytes, chunkBytes.Length, _waveFormat);
                AudioChunkReady?.Invoke(this, new AudioChunk(wavBytes, _chunkDuration, chunkLevel));
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AudioChunkReady?.Invoke(this, new AudioChunk([], TimeSpan.Zero, 0));
        }
    }
}

public sealed record AudioChunk(byte[] WavBytes, TimeSpan Duration, double Level);

public static class AudioMath
{
    public static byte[] CreateWavBytes(byte[] audioBytes, WaveFormat waveFormat)
    {
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(memory, waveFormat))
        {
            writer.Write(audioBytes, 0, audioBytes.Length);
        }
        return memory.ToArray();
    }

    public static double CalculateRms(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = bytesRecorded / 4;
            if (samples == 0)
            {
                return 0;
            }

            double sum = 0;
            for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
            {
                var sample = BitConverter.ToSingle(buffer, offset);
                sum += sample * sample;
            }

            return Math.Sqrt(sum / samples);
        }

        if (format.BitsPerSample == 16)
        {
            var samples = bytesRecorded / 2;
            if (samples == 0)
            {
                return 0;
            }

            double sum = 0;
            for (var offset = 0; offset + 2 <= bytesRecorded; offset += 2)
            {
                var sample = BitConverter.ToInt16(buffer, offset) / 32768.0;
                sum += sample * sample;
            }

            return Math.Sqrt(sum / samples);
        }

        return 0;
    }
}
