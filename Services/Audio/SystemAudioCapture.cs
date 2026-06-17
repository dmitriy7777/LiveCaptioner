using LiveCaptioner.Models;
using NAudio.Wave;

namespace LiveCaptioner.Services.Audio;

public sealed class SystemAudioCapture : IAudioCapture
{
    private readonly TimeSpan _chunkDuration;
    private readonly TimeSpan _overlapDuration;
    private readonly List<byte> _buffer = new();
    private readonly object _syncRoot = new();
    private WasapiLoopbackCapture? _capture;
    private int _chunkByteCount;
    private int _stepByteCount;

    public event EventHandler<AudioChunk>? AudioChunkReady;
    public event EventHandler<double>? AudioLevelChanged;

    public SystemAudioCapture(TimeSpan chunkDuration, TimeSpan overlapDuration)
    {
        _chunkDuration = chunkDuration;
        _overlapDuration = overlapDuration;
    }

    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        _chunkByteCount = AudioMath.AlignByteCount((int)(_capture.WaveFormat.AverageBytesPerSecond * _chunkDuration.TotalSeconds), _capture.WaveFormat);
        var overlapByteCount = AudioMath.AlignByteCount((int)(_capture.WaveFormat.AverageBytesPerSecond * _overlapDuration.TotalSeconds), _capture.WaveFormat);
        _stepByteCount = Math.Max(_capture.WaveFormat.BlockAlign, _chunkByteCount - Math.Min(overlapByteCount, _chunkByteCount / 2));
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

        var level = AudioMath.CalculateRms(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
        AudioLevelChanged?.Invoke(this, level);

        lock (_syncRoot)
        {
            _buffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());

            while (_buffer.Count >= _chunkByteCount)
            {
                var chunkBytes = _buffer.GetRange(0, _chunkByteCount).ToArray();
                _buffer.RemoveRange(0, _stepByteCount);
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
}
