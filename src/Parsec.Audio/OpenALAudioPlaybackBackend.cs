using OpenTK.Audio.OpenAL;

namespace Parsec.Audio;

public sealed class OpenALAudioPlaybackBackend : IAudioPlaybackBackend
{
    private static bool _overrideInstalled;

    public OpenALAudioPlaybackBackend()
    {
        // macOS 15 (Sequoia) broke the system OpenAL framework — alcOpenDevice(NULL)
        // causes a native SIGSEGV. Redirect OpenTK to openal-soft via its built-in
        // OverridePath before any AL/ALC type is touched.
        if (OperatingSystem.IsMacOS() && Environment.OSVersion.Version.Major >= 15
            && !_overrideInstalled)
        {
            var cellarVersions = Directory.Exists("/opt/homebrew/Cellar/openal-soft")
                ? Directory.GetDirectories("/opt/homebrew/Cellar/openal-soft")
                    .Select(v => Path.Combine(v, "lib", "libopenal.dylib"))
                : [];
            string[] softPaths =
            [
                ..cellarVersions,
                "/opt/homebrew/lib/libopenal.dylib",
                "/usr/local/Cellar/openal-soft/1.25.2/lib/libopenal.dylib",
                "/usr/local/lib/libopenal.dylib",
            ];
            string? softPath = softPaths.FirstOrDefault(File.Exists);

            if (softPath == null)
            {
                IsAvailable = false;
                UnavailableReason =
                    "System OpenAL is broken on macOS 15+. " +
                    "Install openal-soft for playback: brew install openal-soft";
                return;
            }

            OpenALLibraryNameContainer.OverridePath = softPath;
            _overrideInstalled = true;
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
