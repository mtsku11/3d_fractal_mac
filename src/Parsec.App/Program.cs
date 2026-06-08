using Avalonia;
using Avalonia.OpenGL;

namespace Parsec.App;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsMacOS())
        {
            var startupBlocker = await MacDisplayPreflight.TryGetBlockingErrorAsync();
            if (startupBlocker is not null)
            {
                Console.Error.WriteLine(startupBlocker);
                return 1;
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // LOAD-BEARING: RenderingMode forces Avalonia to actually SELECT the
                // WGL backend (with a software fallback). Without this, platform
                // detection may not choose WGL at all, so the WglProfiles request
                // below has nothing to apply to and we never get a 4.3 core context
                // — which is why '#version 430' compute shaders failed to render.
                // WglProfiles then pins that context to real desktop OpenGL 4.3 core
                // rather than a GL ES context over ANGLE.
                RenderingMode = new[] { Win32RenderingMode.Wgl, Win32RenderingMode.Software },
                WglProfiles = new[] { new GlVersion(GlProfileType.OpenGL, 4, 3) },
                OverlayPopups = true
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Software }
            })
            .With(new AvaloniaNativePlatformOptions
            {
                // Avalonia 11.2.0 + macOS 15: the native GL and Metal compositors
                // can crash in Skia before managed code can recover. Keep the UI in
                // software mode; FractalView presents Metal compute output through
                // its WriteableBitmap fallback when OpenGlControlBase has no context.
                RenderingMode = new[] { AvaloniaNativeRenderingMode.Software }
            })
            .WithInterFont()
            .LogToTrace();
}
