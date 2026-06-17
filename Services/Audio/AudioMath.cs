using System.IO;
using NAudio.Wave;

namespace LiveCaptioner.Services.Audio;

public static class AudioMath
{
    public static int AlignByteCount(int byteCount, WaveFormat format)
    {
        var blockAlign = Math.Max(1, format.BlockAlign);
        return Math.Max(blockAlign, byteCount - byteCount % blockAlign);
    }

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
