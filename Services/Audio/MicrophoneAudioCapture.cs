using LiveCaptioner.Models;
using NAudio.Wave;

namespace LiveCaptioner.Services.Audio;

public sealed class MicrophoneAudioCapture : IAudioCapture
{
    private readonly TimeSpan _chunkDuration;
    private readonly TimeSpan _overlapDuration;
    private readonly List<byte> _buffer = new();
    private readonly object _syncRoot = new();
    private WaveInEvent? _capture;
    private readonly WaveFormat _waveFormat = new(16000, 16, 1);
    private int _chunkByteCount;
    private int _stepByteCount;

    public event EventHandler<AudioChunk>? AudioChunkReady;
    public event EventHandler<double>? AudioLevelChanged;

    public MicrophoneAudioCapture(TimeSpan chunkDuration, TimeSpan overlapDuration)
    {
        _chunkDuration = chunkDuration;
        _overlapDuration = overlapDuration;
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

        _chunkByteCount = AudioMath.AlignByteCount((int)(_waveFormat.AverageBytesPerSecond * _chunkDuration.TotalSeconds), _waveFormat);
        var overlapByteCount = AudioMath.AlignByteCount((int)(_waveFormat.AverageBytesPerSecond * _overlapDuration.TotalSeconds), _waveFormat);
        _stepByteCount = Math.Max(_waveFormat.BlockAlign, _chunkByteCount - Math.Min(overlapByteCount, _chunkByteCount / 2));
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
                _buffer.RemoveRange(0, _stepByteCount);
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
