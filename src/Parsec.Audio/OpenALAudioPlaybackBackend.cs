using OpenTK.Audio.OpenAL;

namespace Parsec.Audio;

public sealed class OpenALAudioPlaybackBackend : IAudioPlaybackBackend
{
    public OpenALAudioPlaybackBackend()
    {
        // macOS 15 (Sequoia) broke the system OpenAL framework — ALC.OpenDevice
        // generates a native SIGSEGV that cannot be caught by managed code.
        // If openal-soft (which works) is not present, declare unavailable rather
        // than crash the process.
        if (OperatingSystem.IsMacOS() && Environment.OSVersion.Version.Major >= 15)
        {
            string[] softPaths =
            [
                "/opt/homebrew/lib/libopenal.dylib",   // Apple Silicon Homebrew
                "/usr/local/lib/libopenal.dylib",       // Intel Homebrew
            ];
            if (!softPaths.Any(File.Exists))
            {
                IsAvailable = false;
                UnavailableReason =
                    "System OpenAL is broken on macOS 15+. " +
                    "Install openal-soft for playback: brew install openal-soft";
                return;
            }
            // openal-soft is present; fall through to the normal probe below,
            // which will pick it up via DYLD_LIBRARY_PATH or direct load.
        }

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
