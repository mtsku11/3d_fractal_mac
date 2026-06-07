using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Parsec.App;

[SupportedOSPlatform("macos")]
internal static partial class MacDisplayPreflight
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<string?> TryGetBlockingErrorAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var timeout = GetTimeout();
        var startedAt = DateTime.UtcNow;
        var status = GetStatus();

        while (!status.IsReady && DateTime.UtcNow - startedAt < timeout)
        {
            await Task.Delay(PollInterval, cancellationToken);
            status = GetStatus();
        }

        return status.IsReady ? null : status.ToFailureMessage(timeout);
    }

    private static TimeSpan GetTimeout()
    {
        var value = Environment.GetEnvironmentVariable("PARSEC_MAC_DISPLAY_WAIT_MS");
        return int.TryParse(value, out var ms) && ms >= 0
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultTimeout;
    }

    private static DisplayStatus GetStatus()
    {
        var onlineError = CGGetOnlineDisplayList(0, nint.Zero, out var onlineDisplays);
        var activeError = CGGetActiveDisplayList(0, nint.Zero, out var activeDisplays);
        var mainDisplayId = CGMainDisplayID();
        var cvError = CVDisplayLinkCreateWithActiveCGDisplays(out var displayLink);

        if (displayLink != nint.Zero)
        {
            CVDisplayLinkRelease(displayLink);
        }

        return new DisplayStatus(
            onlineDisplays,
            activeDisplays,
            mainDisplayId,
            onlineError,
            activeError,
            cvError,
            displayLink != nint.Zero && cvError == 0);
    }

    [LibraryImport(CoreGraphics)]
    private static partial int CGGetOnlineDisplayList(uint maxDisplays, nint onlineDisplays, out uint displayCount);

    [LibraryImport(CoreGraphics)]
    private static partial int CGGetActiveDisplayList(uint maxDisplays, nint activeDisplays, out uint displayCount);

    [LibraryImport(CoreGraphics)]
    private static partial uint CGMainDisplayID();

    [LibraryImport(CoreVideo)]
    private static partial int CVDisplayLinkCreateWithActiveCGDisplays(out nint displayLink);

    [LibraryImport(CoreVideo)]
    private static partial void CVDisplayLinkRelease(nint displayLink);

    private sealed record DisplayStatus(
        uint OnlineDisplays,
        uint ActiveDisplays,
        uint MainDisplayId,
        int OnlineError,
        int ActiveError,
        int CvError,
        bool IsReady)
    {
        public string ToFailureMessage(TimeSpan timeout) =>
            $"Parsec could not start on macOS because no usable active display was available after waiting {timeout.TotalMilliseconds:0} ms. " +
            $"onlineDisplays={OnlineDisplays} (err={OnlineError}), activeDisplays={ActiveDisplays} (err={ActiveError}), mainDisplayId={MainDisplayId}, cvDisplayLinkError={CvError}. " +
            "Wake or attach a display and retry. Set PARSEC_MAC_DISPLAY_WAIT_MS=0 to fail immediately.";
    }
}
