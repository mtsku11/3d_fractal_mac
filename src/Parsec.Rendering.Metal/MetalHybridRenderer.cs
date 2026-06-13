using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Raymarching;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

/// <summary>
/// Metal compute backend for the hybrid Mandelbox+Mandelbulb raymarcher.
/// Compiles hybrid_raymarch.metal at construction. Returns packed RGBA8 uint[].
/// </summary>
public sealed class MetalHybridRenderer : IDisposable
{
    private MTLDevice               _device;
    private MTLCommandQueue         _queue;
    private MTLComputePipelineState _pso;
    private bool                    _isAvailable;
    private bool                    _disposed;

    public bool IsAvailable => _isAvailable;

    public long LastComputeMs  { get; private set; }
    public long LastReadbackMs { get; private set; }

    public MetalHybridRenderer()
    {
        try
        {
            var dev = MTLDevice.CreateSystemDefaultDevice();
            var src = MetalSurfaceTextureShaderInjector.Inject(LoadEmbeddedMsl("hybrid_raymarch.metal"));
            NSError libError = default;
            var library = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libError);
            var function = library.NewFunction(NSString.String("hybrid_raymarch"));
            NSError psoError = default;
            _pso    = dev.NewComputePipelineState(function, ref psoError);
            _queue  = dev.NewCommandQueue();
            _device = dev;
            _isAvailable = true;
        }
        catch
        {
            _isAvailable = false;
        }
    }

    public uint[] RenderHybrid(
        HybridParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        PostProcessParams? postProcess = null)
    {
        ThrowIfDisposed();
        if (!_isAvailable)
            throw new InvalidOperationException("Metal backend is not available on this machine.");
        var _hdrAcc = MetalSsaa.AccumulateHdr(settings.HeroSamples, width, height, jitter =>
        {
            int pixelCount = width * height;
            using var foldBuf   = UploadStruct(_device, BuildFoldParams(fractal));
            using var renderBuf = UploadStruct(_device, BuildRenderParams(camera, width, height, lightDirection, background, surface, settings, palette, jitter));
            using var outBuf    = MetalBufferIO.CreateSharedBuffer(_device, (ulong)(pixelCount * 4 * sizeof(float)));

            var cmd = _queue.CommandBuffer();
            var enc = cmd.ComputeCommandEncoder();

            enc.SetComputePipelineState(_pso!);
            enc.SetBuffer(foldBuf,   0, 0);
            enc.SetBuffer(renderBuf, 0, 1);
            enc.SetBuffer(outBuf,    0, 2);
            enc.SetTexture(MetalSurfaceTextureManager.GetTexture(_device), 0);

            enc.DispatchThreadgroups(
                new MTLSize { width = (ulong)((width + 7) / 8), height = (ulong)((height + 7) / 8), depth = 1 },
                new MTLSize { width = 8, height = 8, depth = 1 });
            enc.EndEncoding();

            var computeSw = System.Diagnostics.Stopwatch.StartNew();
            cmd.Commit();
            cmd.WaitUntilCompleted();
            LastComputeMs = computeSw.ElapsedMilliseconds;

            var readbackSw = System.Diagnostics.Stopwatch.StartNew();
            var result = ReadFloat4Buffer(outBuf, pixelCount);
            LastReadbackMs = readbackSw.ElapsedMilliseconds;
            return result;
        });
        return MetalPostProcess.Apply(_device, _queue, _hdrAcc, width, height, postProcess);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isAvailable)
        {
            _pso.Dispose();
            _queue.Dispose();
            _device.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Parameter builders
    // -------------------------------------------------------------------------

    private static MetalFoldParams BuildFoldParams(HybridParams hp) => new()
    {
        Iterations  = hp.Iterations,
        Mode        = 0,
        JuliaMode   = 0,
        Pad0        = 0,
        // boxParams: (scale, minRadius, fixedRadius, foldLimit) — matches hybrid_core.glsl
        BoxParams   = new Vector4(hp.Scale, hp.MinRadius, hp.FixedRadius, hp.FoldLimit),
        SurfParams  = new Vector4(hp.Rotation, hp.Power),
        JuliaCVec   = Vector4.Zero,
        Rot         = new Vector4(0f, 0f, 0f, hp.Fudge),
        BoundSphere = new Vector4(0f, 0f, 0f, hp.BoundRadius),
    };

    private static MetalRenderParams BuildRenderParams(
        Camera3D camera, int width, int height,
        Vector3 lightDirection, Color background, Color surface,
        RaymarchSettings s, PaletteParams palette, Vector2 jitter = default)
    {
        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * ((float)width / height);

        var lightDir = Vector3.Normalize(lightDirection);
        int flags = (s.EnableSoftShadows ? 1 : 0) | (s.EnableAmbientOcclusion ? 2 : 0);

        return new MetalRenderParams
        {
            ImageWidth  = width,
            ImageHeight = height,
            RowOffset   = 0,
            RowCount    = height,
            CamPos      = new Vector4(camera.Position, 0f),
            CamForward  = new Vector4(fwd,   0f),
            CamRight    = new Vector4(right, 0f),
            CamUp       = new Vector4(up,    0f),
            TanFov      = new Vector4(tanX, tanY, 0f, 0f),
            LightDir    = new Vector4(lightDir, s.LightIntensity),
            Background = MetalSurfaceTextureManager.EncodeBackground(background),
            Surface = MetalSurfaceTextureManager.EncodeSurface(surface),
            MarchA      = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, s.ShadowSoftness),
            MarchB = MetalSurfaceTextureManager.EncodeMarchB(s.AOStepDistance, s.AOIntensity),
            MarchI0     = s.MaxSteps,
            MarchI1     = s.ShadowSteps,
            MarchI2     = s.AOSamples,
            MarchI3     = flags,
            PalBase     = new Vector4(palette.Base,  palette.Frequency),
            PalAmp      = new Vector4(palette.Amp,   palette.TrapScale),
            PalPhase    = new Vector4(palette.Phase, palette.ShellMix),
            TrapMix     = new Vector4(palette.TrapMix, DomainWarpState.GetPhase()),
            SubpixelJitter = DomainWarpState.EncodeSubpixelJitter(jitter),
            ReflectParams  = new Vector4(
                s.EnableReflections ? 1f : 0f,
                s.ReflectionBounces,
                s.Gloss,
                s.F0),
        };
    }

    // -------------------------------------------------------------------------
    // Metal buffer utilities
    // -------------------------------------------------------------------------

    private static MTLBuffer UploadStruct<T>(MTLDevice device, T value) where T : struct
        => MetalBufferIO.UploadStruct(device, value);

    private static float[] ReadFloat4Buffer(MTLBuffer buf, int count)
        => MetalBufferIO.ReadFloat4Buffer(buf, count);

    private static string LoadEmbeddedMsl(string filename)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"Parsec.Rendering.Metal.Shaders.{filename}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetalHybridRenderer));
    }

    // -------------------------------------------------------------------------
    // GPU parameter structs
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MetalFoldParams
    {
        public int Iterations, Mode, JuliaMode, Pad0;
        public Vector4 BoxParams;
        public Vector4 SurfParams;
        public Vector4 JuliaCVec;
        public Vector4 Rot;
        public Vector4 BoundSphere;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MetalRenderParams
    {
        public int ImageWidth, ImageHeight, RowOffset, RowCount;
        public Vector4 CamPos, CamForward, CamRight, CamUp, TanFov;
        public Vector4 LightDir, Background, Surface;
        public Vector4 MarchA, MarchB;
        public int MarchI0, MarchI1, MarchI2, MarchI3;
        public Vector4 PalBase, PalAmp, PalPhase, TrapMix;
        public Vector4 SubpixelJitter;
        public Vector4 ReflectParams;
    }
}
