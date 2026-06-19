using LiveCaptioner.Services.Diagnostics;
using NAudio.Wave;

namespace LiveCaptioner.Services.Audio;

public sealed class RealtimePcmAudioCapture : IDisposable
{
    public const int TargetSampleRate = 24000;

    private readonly bool _useSystemAudio;
    private readonly double _systemAudioGain;
    private WaveInEvent? _microphoneCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private PcmMonoConverter? _converter;

    public event EventHandler<byte[]>? PcmAvailable;
    public event EventHandler<double>? AudioLevelChanged;

    public RealtimePcmAudioCapture(bool useSystemAudio, double systemAudioGain = 1.4)
    {
        _useSystemAudio = useSystemAudio;
        _systemAudioGain = systemAudioGain;
    }

    public void Start()
    {
        if (_useSystemAudio)
        {
            StartSystemAudio();
            return;
        }

        StartMicrophone();
    }

    public void Dispose()
    {
        if (_microphoneCapture != null)
        {
            _microphoneCapture.DataAvailable -= OnMicrophoneDataAvailable;
            _microphoneCapture.StopRecording();
            _microphoneCapture.Dispose();
            _microphoneCapture = null;
        }

        if (_systemCapture != null)
        {
            _systemCapture.DataAvailable -= OnSystemDataAvailable;
            _systemCapture.StopRecording();
            _systemCapture.Dispose();
            _systemCapture = null;
        }
    }

    private void StartMicrophone()
    {
        if (WaveInEvent.DeviceCount <= 0)
        {
            throw new InvalidOperationException("Microphone was not found. Check the Windows recording device.");
        }

        var format = new WaveFormat(TargetSampleRate, 16, 1);
        _microphoneCapture = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = format,
            BufferMilliseconds = 100
        };
        _converter = new PcmMonoConverter(format, TargetSampleRate);
        _microphoneCapture.DataAvailable += OnMicrophoneDataAvailable;
        _microphoneCapture.StartRecording();
        AppLogger.Info("OpenAI realtime microphone capture started.");
    }

    private void StartSystemAudio()
    {
        _systemCapture = new WasapiLoopbackCapture();
        _converter = new PcmMonoConverter(_systemCapture.WaveFormat, TargetSampleRate, _systemAudioGain);
        _systemCapture.DataAvailable += OnSystemDataAvailable;
        _systemCapture.StartRecording();
        AppLogger.Info($"OpenAI realtime system audio capture started. Source format={_systemCapture.WaveFormat}, target={TargetSampleRate} Hz mono PCM16.");
    }

    private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_converter == null || e.BytesRecorded <= 0)
        {
            return;
        }

        AudioLevelChanged?.Invoke(this, AudioMath.CalculateRms(e.Buffer, e.BytesRecorded, _microphoneCapture!.WaveFormat));
        var pcm = _converter.Convert(e.Buffer, e.BytesRecorded);
        if (pcm.Length > 0)
        {
            PcmAvailable?.Invoke(this, pcm);
        }
    }

    private void OnSystemDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_converter == null || _systemCapture == null || e.BytesRecorded <= 0)
        {
            return;
        }

        AudioLevelChanged?.Invoke(this, AudioMath.CalculateRms(e.Buffer, e.BytesRecorded, _systemCapture.WaveFormat));
        var pcm = _converter.Convert(e.Buffer, e.BytesRecorded);
        if (pcm.Length > 0)
        {
            PcmAvailable?.Invoke(this, pcm);
        }
    }
}
