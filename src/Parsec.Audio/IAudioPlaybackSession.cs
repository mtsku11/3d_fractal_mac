namespace Parsec.Audio;

public interface IAudioPlaybackSession : IAsyncDisposable
{
    Uri Source { get; }
    string DisplayName { get; }
    bool CanSeek { get; }
    TimeSpan? Duration { get; }
    TimeSpan Position { get; }
    AudioPlaybackStatus PlaybackStatus { get; }

    Task PlayAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}
