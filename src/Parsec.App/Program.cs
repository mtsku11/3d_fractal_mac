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
                // Skia-Metal crashes on HDMI dummy plugs (gr_backendrendertarget_new_metal
                // gets a null drawable). OpenGl is the default but explicitly listed here
                // so Software is the fallback if OpenGl also fails.
                RenderingMode = new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }
            })
            .WithInterFont()
            .LogToTrace();
}
