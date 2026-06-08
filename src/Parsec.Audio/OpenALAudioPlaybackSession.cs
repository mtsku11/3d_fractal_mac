using System.IO;
using OpenTK.Audio.OpenAL;

namespace Parsec.Audio;

public sealed class OpenALAudioPlaybackSession : IAudioPlaybackSession
{
    private readonly object _gate = new();
    private readonly ALDevice _device;
    private readonly ALContext _context;
    private readonly int _buffer;
    private readonly int _source;
    private readonly TimeSpan _duration;
    private TimeSpan? _positionOverride;
    private bool _disposed;

    public OpenALAudioPlaybackSession(Uri source)
    {
        if (!source.IsFile || string.IsNullOrWhiteSpace(source.LocalPath))
            throw new NotSupportedException("Only local audio files are supported in Phase 1.");

        var audio = WavePcmDecoder.Load(source.LocalPath);

        _device = ALC.OpenDevice(null);
        if (_device == ALDevice.Null)
            throw new InvalidOperationException("OpenAL could not open the default playback device.");

        _context = ALC.CreateContext(_device, new ALContextAttributes
        {
            Frequency = audio.SampleRate,
            MonoSources = 4,
            StereoSources = 4,
        });
        if (_context == ALContext.Null)
        {
            ALC.CloseDevice(_device);
            throw new InvalidOperationException("OpenAL could not create a playback context.");
        }

        Source = source;
        DisplayName = Path.GetFileName(source.LocalPath);
        CanSeek = true;
        _duration = audio.Duration;
        _positionOverride = TimeSpan.Zero;

        MakeContextCurrent();
        _buffer = AL.GenBuffer();
        AL.BufferData(_buffer, audio.Format, audio.Samples, audio.SampleRate);

        _source = AL.GenSource();
        AL.Source(_source, ALSourcei.Buffer, _buffer);
        AL.Source(_source, ALSourceb.Looping, false);
        AL.Source(_source, ALSourcef.Gain, 1.0f);
        AL.DistanceModel(ALDistanceModel.None);
        ThrowIfAlError("uploading audio data");
    }

    public Uri Source { get; }
    public string DisplayName { get; }
    public bool CanSeek { get; }
    public TimeSpan? Duration => _duration;

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                MakeContextCurrent();
                var state = GetSourceState();
                if (state != ALSourceState.Playing && _positionOverride is { } overridePosition)
                    return overridePosition;

                float seconds = AL.GetSource(_source, ALSourcef.SecOffset);
                return ClampPosition(TimeSpan.FromSeconds(seconds));
            }
        }
    }

    public AudioPlaybackStatus PlaybackStatus
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                MakeContextCurrent();
                return MapState(GetSourceState());
            }
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            MakeContextCurrent();
            if (GetSourceState() == ALSourceState.Stopped && Position >= _duration)
                AL.SourceRewind(_source);

            AL.SourcePlay(_source);
            _positionOverride = null;
            ThrowIfAlError("starting playback");
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            MakeContextCurrent();
            _positionOverride = Position;
            AL.SourcePause(_source);
            ThrowIfAlError("pausing playback");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            MakeContextCurrent();
            AL.SourceStop(_source);
            AL.SourceRewind(_source);
            _positionOverride = TimeSpan.Zero;
            ThrowIfAlError("stopping playback");
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            MakeContextCurrent();

            TimeSpan clamped = ClampPosition(position);
            var state = GetSourceState();
            bool resumePlayback = state == ALSourceState.Playing;

            if (state == ALSourceState.Stopped || state == ALSourceState.Initial)
                AL.SourceRewind(_source);

            AL.Source(_source, ALSourcef.SecOffset, (float)clamped.TotalSeconds);
            _positionOverride = resumePlayback ? null : clamped;
            if (resumePlayback)
                AL.SourcePlay(_source);

            ThrowIfAlError("seeking playback");
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;

            MakeContextCurrent();
            AL.SourceStop(_source);
            AL.DeleteSource(_source);
            AL.DeleteBuffer(_buffer);
            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(_context);
            ALC.CloseDevice(_device);
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private void MakeContextCurrent()
    {
        if (!ALC.MakeContextCurrent(_context))
            throw new InvalidOperationException("OpenAL could not activate the playback context.");
    }

    private static AudioPlaybackStatus MapState(ALSourceState state) => state switch
    {
        ALSourceState.Playing => AudioPlaybackStatus.Playing,
        ALSourceState.Paused => AudioPlaybackStatus.Paused,
        _ => AudioPlaybackStatus.Stopped,
    };

    private ALSourceState GetSourceState() =>
        (ALSourceState)AL.GetSource(_source, ALGetSourcei.SourceState);

    private TimeSpan ClampPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (position > _duration) return _duration;
        return position;
    }

    private void ThrowIfAlError(string operation)
    {
        var error = AL.GetError();
        if (error != ALError.NoError)
            throw new InvalidOperationException($"OpenAL error while {operation}: {AL.GetErrorString(error)}");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OpenALAudioPlaybackSession));
    }
}
