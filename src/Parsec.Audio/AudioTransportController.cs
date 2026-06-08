using System.IO;

namespace Parsec.Audio;

public sealed class AudioTransportController : IAsyncDisposable
{
    private readonly IAudioPlaybackBackend _backend;
    private IAudioPlaybackSession? _session;
    private string _statusText;

    public AudioTransportController(IAudioPlaybackBackend backend)
    {
        _backend = backend;
        _statusText = _backend.IsAvailable
            ? "No audio file loaded."
            : _backend.UnavailableReason;
        State = BuildState();
    }

    public AudioTransportState State { get; private set; }

    public event Action<AudioTransportState>? StateChanged;

    public async Task OpenAsync(Uri source, CancellationToken cancellationToken = default)
    {
        if (!_backend.IsAvailable)
        {
            Publish(_backend.UnavailableReason);
            return;
        }

        try
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            _session = await _backend.OpenAsync(source, cancellationToken).ConfigureAwait(false);
            Publish($"Loaded {DisplayNameFor(source)}.");
        }
        catch (Exception ex)
        {
            _session = null;
            Publish(ex.Message);
        }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        if (_session == null)
        {
            Publish("Load an audio file first.");
            return;
        }

        try
        {
            await _session.PlayAsync(cancellationToken).ConfigureAwait(false);
            Publish($"Playing {_session.DisplayName}.");
        }
        catch (Exception ex)
        {
            Publish(ex.Message);
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (_session == null) return;
        try
        {
            await _session.PauseAsync(cancellationToken).ConfigureAwait(false);
            Publish($"Paused {_session.DisplayName}.");
        }
        catch (Exception ex)
        {
            Publish(ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_session == null) return;
        try
        {
            await _session.StopAsync(cancellationToken).ConfigureAwait(false);
            Publish($"Stopped {_session.DisplayName}.");
        }
        catch (Exception ex)
        {
            Publish(ex.Message);
        }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (_session == null || !_session.CanSeek) return;

        if (_session.Duration is { } duration)
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > duration) position = duration;
        }

        try
        {
            await _session.SeekAsync(position, cancellationToken).ConfigureAwait(false);
            Publish();
        }
        catch (Exception ex)
        {
            Publish(ex.Message);
        }
    }

    public void Refresh() => Publish();

    public async ValueTask DisposeAsync() => await DisposeSessionAsync().ConfigureAwait(false);

    private async ValueTask DisposeSessionAsync()
    {
        if (_session == null) return;
        await _session.DisposeAsync().ConfigureAwait(false);
        _session = null;
        _statusText = _backend.IsAvailable ? "No audio file loaded." : _backend.UnavailableReason;
    }

    private void Publish(string? statusText = null)
    {
        if (!string.IsNullOrWhiteSpace(statusText))
            _statusText = statusText;

        State = BuildState();
        StateChanged?.Invoke(State);
    }

    private AudioTransportState BuildState()
    {
        if (_session == null)
        {
            return new AudioTransportState(
                BackendAvailable: _backend.IsAvailable,
                BackendName: _backend.DisplayName,
                StatusText: _statusText,
                Source: null,
                DisplayName: null,
                PlaybackStatus: _backend.IsAvailable ? AudioPlaybackStatus.Stopped : AudioPlaybackStatus.Unavailable,
                Position: TimeSpan.Zero,
                Duration: null,
                CanLoad: _backend.IsAvailable,
                CanPlay: false,
                CanPause: false,
                CanStop: false,
                CanSeek: false);
        }

        return new AudioTransportState(
            BackendAvailable: _backend.IsAvailable,
            BackendName: _backend.DisplayName,
            StatusText: _statusText,
            Source: _session.Source,
            DisplayName: _session.DisplayName,
            PlaybackStatus: _session.PlaybackStatus,
            Position: _session.Position,
            Duration: _session.Duration,
            CanLoad: _backend.IsAvailable,
            CanPlay: _session.PlaybackStatus != AudioPlaybackStatus.Playing,
            CanPause: _session.PlaybackStatus == AudioPlaybackStatus.Playing,
            CanStop: _session.PlaybackStatus != AudioPlaybackStatus.Stopped,
            CanSeek: _session.CanSeek);
    }

    private static string DisplayNameFor(Uri source)
    {
        if (source.IsFile && !string.IsNullOrWhiteSpace(source.LocalPath))
            return Path.GetFileName(source.LocalPath);
        return source.ToString();
    }
}
