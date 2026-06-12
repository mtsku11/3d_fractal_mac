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
/// Metal compute backend for pseudo-Kleinian (inversive limit set) raymarching.
/// Uses a numerical-gradient DE (7 potential evaluations per estimate call).
/// Compiles kleinian_raymarch.metal at construction. Returns packed RGBA8 uint[].
/// </summary>
public sealed class MetalKleinianRenderer : IDisposable
{
    private MTLDevice               _device;
    private MTLCommandQueue         _queue;
    private MTLComputePipelineState _pso;
    private bool                    _isAvailable;
    private bool                    _disposed;

    public bool IsAvailable => _isAvailable;

    public long LastComputeMs  { get; private set; }
    public long LastReadbackMs { get; private set; }

    public MetalKleinianRenderer()
    {
        try
        {
            var dev = MTLDevice.CreateSystemDefaultDevice();
            var src = MetalSurfaceTextureShaderInjector.Inject(LoadEmbeddedMsl("kleinian_raymarch.metal"));
            NSError libError = default;
            var library = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libError);
            var function = library.NewFunction(NSString.String("kleinian_raymarch"));
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

    public uint[] RenderKleinian(
        KleinianParams fractal,
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
            if (_telemetryPsoAvailable) _telemetryPso.Dispose();
            if (_fieldScanPsoAvailable) _fieldScanPso.Dispose();
            _queue.Dispose();
            _device.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // M8: Field-scan pass — Lissajous 3:2:1 DE field scan for synthesis
    // -------------------------------------------------------------------------

    private MTLComputePipelineState _fieldScanPso;
    private bool _fieldScanPsoReady;
    private bool _fieldScanPsoAvailable;

    /// <summary>
    /// Dispatches a 64-thread 1-D kernel that samples the Kleinian DE field along
    /// a 3:2:1 Lissajous orbit centred on the camera.  Returns a 64-sample waveform
    /// (AC-coupled, normalised [-1,1]) for use as a drone wavetable in M8 synthesis.
    /// Returns null if the field-scan PSO failed to compile.
    /// </summary>
    public (float[]? tl, float[]? tr, float[]? bl, float[]? br) RunFieldScanPass(KleinianParams fractal, Camera3D camera, RaymarchSettings settings)
    {
        if (!_isAvailable) return (null, null, null, null);
        ThrowIfDisposed();
        if (!EnsureFieldScanPso()) return (null, null, null, null);

        const int N = 64;
        using var foldBuf = UploadStruct(_device, BuildFoldParams(fractal));
        using var telBuf  = UploadStruct(_device, BuildTelemetryParams(camera, 64, 36, settings));
        using var outBuf  = _device.NewBuffer((ulong)(4 * N * 4), MTLResourceOptions.ResourceStorageModeShared);

        var cmd = _queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_fieldScanPso);
        enc.SetBuffer(foldBuf, 0, 0);
        enc.SetBuffer(telBuf,  0, 1);
        enc.SetBuffer(outBuf,  0, 2);
            enc.SetTexture(MetalSurfaceTextureManager.GetTexture(_device), 0);
        enc.DispatchThreadgroups(
            new MTLSize { width = 1, height = 1, depth = 1 },
            new MTLSize { width = 256, height = 1, depth = 1 });
        enc.EndEncoding();
        cmd.Commit();
        cmd.WaitUntilCompleted();

        return TelemetryReduction.ReadFieldScanWaveform(outBuf);
    }

    private bool EnsureFieldScanPso()
    {
        if (_fieldScanPsoReady) return _fieldScanPsoAvailable;
        _fieldScanPsoReady = true;
        try
        {
            var src = LoadEmbeddedMsl("kleinian_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("kleinian_fieldscan"));
            NSError psoErr = default;
            _fieldScanPso = _device.NewComputePipelineState(function, ref psoErr);
            _fieldScanPsoAvailable = true;
        }
        catch { /* leave _fieldScanPsoAvailable = false */ }
        return _fieldScanPsoAvailable;
    }

    // -------------------------------------------------------------------------
    // Telemetry pass (M6 — fractal sonification)
    // -------------------------------------------------------------------------

    private MTLComputePipelineState _telemetryPso;
    private bool _telemetryPsoReady;
    private bool _telemetryPsoAvailable;

    public FractalGeometryStats? RunTelemetryPass(
        KleinianParams fractal,
        Camera3D camera,
        RaymarchSettings settings)
    {
        if (!_isAvailable) return null;
        ThrowIfDisposed();
        if (!EnsureTelemetryPso()) return null;

        int gridW = 64, gridH = 36;
        int cellCount = gridW * gridH;
        int cellSize = System.Runtime.InteropServices.Marshal.SizeOf<TelemetryCell>();

        const int WavetableTiles  = 16;
        const int WavetableBytes  = WavetableTiles * 64 * 2 * 4;
        const int WaveshaperBytes = 64 * 4;
        const int OrbitTrajBytes  = WavetableTiles * 128 * 16; // 16 tiles × 128 float4

        using var foldBuf       = UploadStruct(_device, BuildFoldParams(fractal));
        using var telBuf        = UploadStruct(_device, BuildTelemetryParams(camera, gridW, gridH, settings));
        using var outputBuf     = _device.NewBuffer((ulong)(cellCount * cellSize),
                                                    MTLResourceOptions.ResourceStorageModeShared);
        using var wavetableBuf  = _device.NewBuffer((ulong)WavetableBytes,
                                                    MTLResourceOptions.ResourceStorageModeShared);
        using var waveshaperBuf = _device.NewBuffer((ulong)WaveshaperBytes,
                                                    MTLResourceOptions.ResourceStorageModeShared);
        using var orbitTrajBuf  = _device.NewBuffer((ulong)OrbitTrajBytes,
                                                    MTLResourceOptions.ResourceStorageModeShared);

        var cmd = _queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_telemetryPso);
        enc.SetBuffer(foldBuf,       0, 0);
        enc.SetBuffer(telBuf,        0, 1);
        enc.SetBuffer(outputBuf,     0, 2);
            enc.SetTexture(MetalSurfaceTextureManager.GetTexture(_device), 0);
        enc.SetBuffer(wavetableBuf,  0, 3);
        enc.SetBuffer(waveshaperBuf, 0, 4);
        enc.SetBuffer(orbitTrajBuf,  0, 5);
        enc.DispatchThreadgroups(
            new MTLSize { width = (ulong)((gridW + 7) / 8), height = (ulong)((gridH + 7) / 8), depth = 1 },
            new MTLSize { width = 8, height = 8, depth = 1 });
        enc.EndEncoding();
        cmd.Commit();
        cmd.WaitUntilCompleted();

        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * camera.AspectRatio;

        var cells      = TelemetryReduction.Read(outputBuf, cellCount);
        var stats      = TelemetryReduction.Reduce(cells, gridW, gridH,
            camera.Position, fwd, right, up, tanX, tanY, settings.MaxDistance);
        var waveshaper = TelemetryReduction.ReadWaveshaperCurve(waveshaperBuf);

        if (stats.Cells is { Length: > 0 })
        {
            var orbits       = TelemetryReduction.ReadOrbitWavetables(wavetableBuf, WavetableTiles);
            var trajectories = TelemetryReduction.ReadOrbitTrajectories(orbitTrajBuf, WavetableTiles);
            var enriched     = new MetalSpatialCell[stats.Cells.Length];
            for (int ti = 0; ti < stats.Cells.Length; ti++)
                enriched[ti] = stats.Cells[ti] with {
                    OrbitWavetable  = orbits[ti],
                    OrbitTrajectory = trajectories[ti]
                };
            return stats with { Cells = enriched, WaveshaperCurve = waveshaper };
        }
        return stats with { WaveshaperCurve = waveshaper };
    }

    private bool EnsureTelemetryPso()
    {
        if (_telemetryPsoReady) return _telemetryPsoAvailable;
        _telemetryPsoReady = true;
        try
        {
            var src = LoadEmbeddedMsl("kleinian_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("kleinian_telemetry"));
            NSError psoErr = default;
            _telemetryPso = _device.NewComputePipelineState(function, ref psoErr);
            _telemetryPsoAvailable = true;
        }
        catch { /* leave _telemetryPsoAvailable = false */ }
        return _telemetryPsoAvailable;
    }

    private static MetalTelemetryParams BuildTelemetryParams(
        Camera3D camera, int gridW, int gridH, RaymarchSettings s)
    {
        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * camera.AspectRatio;

        return new MetalTelemetryParams
        {
            GridWidth  = gridW,
            GridHeight = gridH,
            Pad0 = 0, Pad1 = 0,
            CamPos     = new Vector4(camera.Position, 0f),
            CamForward = new Vector4(fwd,   0f),
            CamRight   = new Vector4(right, 0f),
            CamUp      = new Vector4(up,    0f),
            TanFov     = new Vector4(tanX, tanY, 0f, 0f),
            March      = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, 0f),
            MaxSteps   = s.MaxSteps,
            Pad2 = 0, Pad3 = 0, Pad4 = 0,
        };
    }

    // -------------------------------------------------------------------------
    // Parameter builders
    // -------------------------------------------------------------------------

    private static MetalFoldParams BuildFoldParams(KleinianParams kl) => new()
    {
        Iterations  = kl.Iterations,
        Mode        = 0,
        JuliaMode   = 0,
        Pad0        = 0,
        // boxParams: (scale, cell, minRadius, fixedRadius) — matches kleinian_core.glsl
        BoxParams   = new Vector4(kl.Scale, kl.Cell, kl.MinRadius, kl.FixedRadius),
        SurfParams  = Vector4.Zero,
        JuliaCVec   = new Vector4(kl.Offset, 0f),
        Rot         = new Vector4(0f, 0f, 0f, kl.Fudge),
        BoundSphere = new Vector4(0f, 0f, 0f, kl.BoundRadius),
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
            TrapMix     = new Vector4(palette.TrapMix, 0f),
            SubpixelJitter = new Vector4(jitter.X, jitter.Y, 0f, 0f),
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
        if (_disposed) throw new ObjectDisposedException(nameof(MetalKleinianRenderer));
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
