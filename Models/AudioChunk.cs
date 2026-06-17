namespace LiveCaptioner.Models;

public sealed record AudioChunk(byte[] WavBytes, TimeSpan Duration, double Level, DateTime CapturedAt)
{
    public AudioChunk(byte[] wavBytes, TimeSpan duration, double level)
        : this(wavBytes, duration, level, DateTime.Now)
    {
    }
}
