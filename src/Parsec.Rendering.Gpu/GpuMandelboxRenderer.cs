using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// Parameters for a Mandelbox / AmazingSurf render.
/// </summary>
public sealed record MandelboxParams
{
    public int Iterations { get; init; } = 14;
    /// <summary>0 = Mandelbox (3D box fold), 1 = AmazingSurf (xy-only fold).</summary>
    public int Mode { get; init; } = 0;
    /// <summary>0 = Mandelbrot-style (offset = position), 1 = Julia (offset = fixed C).</summary>
    public int JuliaMode { get; init; } = 0;

    public float Scale { get; init; } = -1.5f;
    public float FoldingLimit { get; init; } = 1.0f;
    public float MinRadius { get; init; } = 0.5f;
    public float FixedRadius { get; init; } = 1.0f;

    public float FoldXY { get; init; } = 1.0f;
    public float ScaleVary { get; init; } = 0.0f;

    public Vector3 JuliaC { get; init; } = Vector3.Zero;
    public Vector3 RotationRadians { get; init; } = Vector3.Zero;

    /// <summary>DE step fudge in (0,1]. Lower = safer against overshoot, slower.</summary>
    public float Fudge { get; init; } = 0.8f;

    /// <summary>Bounding sphere radius around the origin for the marcher fast-skip.</summary>
    public float BoundRadius { get; init; } = 6.0f;
}

/// <summary>
/// GPU raymarcher for the Mandelbox / AmazingSurf fold-fractal family. Owns only
/// its compute shader; shared buffers and the tile/AA loop live in RaymarchPipeline.
/// </summary>
public sealed class GpuMandelboxRenderer : IThreeDimensionalRenderBackend
{
    private readonly RaymarchPipeline _pipeline;
    private readonly ComputeShader _shader;
    private bool _disposed;

    public bool IsAvailable => true;

    public GpuMandelboxRenderer(Gl gl, RaymarchPipeline pipeline)
    {
        _pipeline = pipeline;
        var src = ShaderLoader.LoadComposite("mandelbox_core.glsl", "raymarch_main.glsl");
        _shader = ComputeShader.FromSource(gl, src, "mandelbox");
    }

    /// <summary>
    /// Render and return the raw RGBA8 pixel buffer (one uint per pixel). Used
    /// by the interactive path, which uploads these directly to a GL texture.
    /// </summary>
    public uint[] RenderToBuffer(
        MandelboxParams mb,
        Camera3D camera,
        int width, int height,
        RaymarchSettings settings,
        Color background, Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        int tileRows = 32,
        Action<int, int>? progress = null)
    {
        ThrowIfDisposed();
        var foldParams = new FoldParamsGpu
        {
            Iterations = mb.Iterations,
            Mode = mb.Mode,
            JuliaMode = mb.JuliaMode,
            Pad0 = 0,
            BoxParams = new Vector4(mb.Scale, mb.FoldingLimit, mb.MinRadius, mb.FixedRadius),
            SurfParams = new Vector4(mb.FoldXY, mb.ScaleVary, 0, 0),
            JuliaCVec = new Vector4(mb.JuliaC, 0),
            Rot = new Vector4(mb.RotationRadians, mb.Fudge),
            BoundSphere = new Vector4(0, 0, 0, mb.BoundRadius),
        };
        return _pipeline.Render(_shader, foldParams, camera, width, height, settings,
                                background, surface, lightDirection, palette,
                                heroSamples: settings.HeroSamples,
                                tileRows: tileRows, progress: progress);
    }

    /// <summary>
    /// Render and return an SKBitmap (for the headless/file path).
    /// </summary>
    public SKBitmap Render(
        MandelboxParams mb, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette,
        int tileRows = 32, Action<int, int>? progress = null)
    {
        uint[] pixels = RenderToBuffer(mb, camera, width, height, settings,
            background, surface, lightDirection, palette, tileRows, progress);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var bytes = new byte[pixels.Length * 4];
        System.Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    uint[] IThreeDimensionalRenderBackend.RenderMandelbox(
        MandelboxParams fractal, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette)
        => RenderToBuffer(fractal, camera, width, height, settings, background, surface, lightDirection, palette);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GpuMandelboxRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
    }
}
