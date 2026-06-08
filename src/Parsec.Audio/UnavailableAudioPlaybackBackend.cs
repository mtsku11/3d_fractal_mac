namespace Parsec.Audio;

public sealed class UnavailableAudioPlaybackBackend : IAudioPlaybackBackend
{
    public UnavailableAudioPlaybackBackend(string unavailableReason)
    {
        UnavailableReason = string.IsNullOrWhiteSpace(unavailableReason)
            ? "Audio playback backend not configured."
            : unavailableReason;
    }

    public string DisplayName => "Unavailable";
    public bool IsAvailable => false;
    public string UnavailableReason { get; }

    public Task<IAudioPlaybackSession> OpenAsync(Uri source, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(UnavailableReason);
}
