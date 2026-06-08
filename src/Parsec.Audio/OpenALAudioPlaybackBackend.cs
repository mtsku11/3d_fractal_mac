using OpenTK.Audio.OpenAL;

namespace Parsec.Audio;

public sealed class OpenALAudioPlaybackBackend : IAudioPlaybackBackend
{
    public OpenALAudioPlaybackBackend()
    {
        try
        {
            ALDevice device = ALC.OpenDevice(null);
            if (device == ALDevice.Null)
            {
                IsAvailable = false;
                UnavailableReason = "OpenAL could not open a playback device.";
                return;
            }

            ALC.CloseDevice(device);
            IsAvailable = true;
            UnavailableReason = string.Empty;
        }
        catch (DllNotFoundException ex)
        {
            IsAvailable = false;
            UnavailableReason = $"OpenAL runtime not found: {ex.Message}";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"OpenAL backend unavailable: {ex.Message}";
        }
    }

    public string DisplayName => "OpenAL (WAV)";
    public bool IsAvailable { get; }
    public string UnavailableReason { get; }

    public Task<IAudioPlaybackSession> OpenAsync(Uri source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
            throw new InvalidOperationException(UnavailableReason);
        return Task.FromResult<IAudioPlaybackSession>(new OpenALAudioPlaybackSession(source));
    }
}
