using LiveCaptioner.Models;

namespace LiveCaptioner.Services.Audio;

public interface IAudioCapture : IDisposable
{
    event EventHandler<AudioChunk>? AudioChunkReady;
    event EventHandler<double>? AudioLevelChanged;
    void Start();
}
