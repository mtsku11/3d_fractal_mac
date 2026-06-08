namespace Parsec.Audio;

public sealed record AudioTransportState(
    bool BackendAvailable,
    string BackendName,
    string StatusText,
    Uri? Source,
    string? DisplayName,
    AudioPlaybackStatus PlaybackStatus,
    TimeSpan Position,
    TimeSpan? Duration,
    bool CanLoad,
    bool CanPlay,
    bool CanPause,
    bool CanStop,
    bool CanSeek);
