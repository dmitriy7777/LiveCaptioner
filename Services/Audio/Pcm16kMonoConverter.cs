using NAudio.Wave;

namespace LiveCaptioner.Services.Audio;

public sealed class Pcm16kMonoConverter
{
    public const int TargetSampleRate = 16000;

    private readonly WaveFormat _sourceFormat;
    private readonly double _gain;
    private readonly bool _noiseGateEnabled;
    private readonly List<float> _sourceSamples = new();
    private double _sourcePosition;

    public Pcm16kMonoConverter(WaveFormat sourceFormat, double gain, bool noiseGateEnabled)
    {
        _sourceFormat = sourceFormat;
        _gain = Math.Max(0.1, gain);
        _noiseGateEnabled = noiseGateEnabled;
    }

    public byte[] Convert(byte[] buffer, int bytesRecorded)
    {
        AppendMonoSamples(buffer, bytesRecorded);

        var ratio = (double)_sourceFormat.SampleRate / TargetSampleRate;
        var estimatedSamples = Math.Max(0, (int)((_sourceSamples.Count - _sourcePosition - 1) / ratio) + 1);
        var output = new byte[estimatedSamples * 2];
        var outputOffset = 0;

        while (_sourcePosition + 1 < _sourceSamples.Count)
        {
            var index = (int)_sourcePosition;
            var fraction = _sourcePosition - index;
            var sample = _sourceSamples[index] + (_sourceSamples[index + 1] - _sourceSamples[index]) * fraction;

            if (_noiseGateEnabled && Math.Abs(sample) < 0.0035f)
            {
                sample = 0;
            }

            sample = Math.Clamp(sample * _gain, -1.0, 1.0);
            var pcm = (short)(sample * short.MaxValue);
            if (outputOffset + 2 > output.Length)
            {
                Array.Resize(ref output, output.Length + 512);
            }

            output[outputOffset++] = (byte)(pcm & 0xff);
            output[outputOffset++] = (byte)((pcm >> 8) & 0xff);

            _sourcePosition += ratio;
        }

        var consumed = Math.Min((int)_sourcePosition, _sourceSamples.Count);
        if (consumed > 0)
        {
            _sourceSamples.RemoveRange(0, consumed);
            _sourcePosition -= consumed;
        }

        if (outputOffset == output.Length)
        {
            return output;
        }

        Array.Resize(ref output, outputOffset);
        return output;
    }

    private void AppendMonoSamples(byte[] buffer, int bytesRecorded)
    {
        var channels = Math.Max(1, _sourceFormat.Channels);
        var bytesPerSample = Math.Max(1, _sourceFormat.BitsPerSample / 8);
        var frameSize = Math.Max(1, bytesPerSample * channels);

        for (var offset = 0; offset + frameSize <= bytesRecorded; offset += frameSize)
        {
            var mixed = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var sampleOffset = offset + channel * bytesPerSample;
                mixed += ReadSample(buffer, sampleOffset);
            }

            _sourceSamples.Add(mixed / channels);
        }
    }

    private float ReadSample(byte[] buffer, int offset)
    {
        if (_sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && _sourceFormat.BitsPerSample == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        if (_sourceFormat.BitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        if (_sourceFormat.BitsPerSample == 32)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }

        return 0;
    }
}
