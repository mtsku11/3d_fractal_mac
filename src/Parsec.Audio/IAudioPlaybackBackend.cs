namespace Parsec.Audio;

public interface IAudioPlaybackBackend
{
    string DisplayName { get; }
    bool IsAvailable { get; }
    string UnavailableReason { get; }

    Task<IAudioPlaybackSession> OpenAsync(Uri source, CancellationToken cancellationToken = default);
}
