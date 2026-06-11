using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Raymarching;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

public sealed class MetalBurningShipRenderer : IDisposable
{
    private MTLDevice               _device;
    private MTLCommandQueue         _queue;
    private MTLComputePipelineState _pso;
    private bool                    _isAvailable;
    private bool                    _disposed;

    public bool IsAvailable => _isAvailable;
    public long LastComputeMs  { get; private set; }
    public long LastReadbackMs { get; private set; }

    public MetalBurningShipRenderer()
    {
        try
        {
            var dev = MTLDevice.CreateSystemDefaultDevice();
            var src = LoadEmbeddedMsl("burningship_raymarch.metal");
            NSError libError = default;
            var library = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libError);
            var function = library.NewFunction(NSString.String("burningship_raymarch"));
            NSError psoError = default;
            _pso    = dev.NewComputePipelineState(function, ref psoError);
            _queue  = dev.NewCommandQueue();
            _device = dev;
            _isAvailable = true;
        }
        catch { _isAvailable = false; }
    }

    public uint[] RenderBurningShip(
        BurningShipParams bs, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette,
        PostProcessParams? postProcess = null)
    {
        ThrowIfDisposed();
        if (!_isAvailable) throw new InvalidOperationException("Metal backend unavailable.");
        var _hdrAcc = MetalSsaa.AccumulateHdr(settings.HeroSamples, width, height, jitter =>
        {
            var foldBuf   = UploadStruct(_device, BuildFoldParams(bs));
            var renderBuf = UploadStruct(_device, BuildRenderParams(camera, width, height, lightDirection, background, surface, settings, palette, jitter));
            var outBuf    = _device.NewBuffer((ulong)(width * height * 4 * sizeof(float)), MTLResourceOptions.ResourceStorageModeShared);

            var cmd = _queue.CommandBuffer();
            var enc = cmd.ComputeCommandEncoder();
            enc.SetComputePipelineState(_pso!);
            enc.SetBuffer(foldBuf, 0, 0); enc.SetBuffer(renderBuf, 0, 1); enc.SetBuffer(outBuf, 0, 2);
            enc.DispatchThreadgroups(new MTLSize { width = (ulong)((width+7)/8), height = (ulong)((height+7)/8), depth = 1 },
                                     new MTLSize { width = 8, height = 8, depth = 1 });
            enc.EndEncoding();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            cmd.Commit(); cmd.WaitUntilCompleted();
            LastComputeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var result = ReadFloat4Buffer(outBuf, width * height);
            LastReadbackMs = sw.ElapsedMilliseconds;
            foldBuf.Dispose(); renderBuf.Dispose(); outBuf.Dispose();
            return result;
        });
        return MetalPostProcess.Apply(_device, _queue, _hdrAcc, width, height, postProcess);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_isAvailable)
        {
            _pso.Dispose();
            if (_telemetryPsoAvailable) _telemetryPso.Dispose();
            _queue.Dispose();
            _device.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Telemetry pass (M6 — fractal sonification)
    // -------------------------------------------------------------------------

    private MTLComputePipelineState _telemetryPso;
    private bool _telemetryPsoReady;
    private bool _telemetryPsoAvailable;

    public FractalGeometryStats? RunTelemetryPass(
        BurningShipParams fractal,
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
            var src = LoadEmbeddedMsl("burningship_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("burningship_telemetry"));
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

    private static MetalFoldParams BuildFoldParams(BurningShipParams bs) => new()
    {
        Iterations  = bs.Iterations, Mode = 0, JuliaMode = 0, Pad0 = 0,
        BoxParams   = new Vector4(bs.Power, bs.Bailout, 0f, 0f),
        SurfParams  = Vector4.Zero,
        JuliaCVec   = Vector4.Zero,
        Rot         = new Vector4(0f, 0f, 0f, bs.Fudge),
        BoundSphere = new Vector4(0f, 0f, 0f, bs.BoundRadius),
    };

    private static MetalRenderParams BuildRenderParams(Camera3D camera, int width, int height,
        Vector3 lightDirection, Color background, Color surface,
        RaymarchSettings s, PaletteParams palette, Vector2 subpixelJitter = default)
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
            ImageWidth = width, ImageHeight = height, RowOffset = 0, RowCount = height,
            CamPos = new Vector4(camera.Position, 0f), CamForward = new Vector4(fwd, 0f),
            CamRight = new Vector4(right, 0f), CamUp = new Vector4(up, 0f),
            TanFov = new Vector4(tanX, tanY, 0f, 0f),
            LightDir = new Vector4(lightDir, s.LightIntensity),
            Background = new Vector4(background.R, background.G, background.B, 1f),
            Surface = new Vector4(surface.R, surface.G, surface.B, 1f),
            MarchA = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, s.ShadowSoftness),
            MarchB = new Vector4(s.AOStepDistance, s.AOIntensity, 0f, 0f),
            MarchI0 = s.MaxSteps, MarchI1 = s.ShadowSteps, MarchI2 = s.AOSamples, MarchI3 = flags,
            PalBase  = new Vector4(palette.Base,  palette.Frequency),
            PalAmp   = new Vector4(palette.Amp,   palette.TrapScale),
            PalPhase = new Vector4(palette.Phase, palette.ShellMix),
            TrapMix  = new Vector4(palette.TrapMix, 0f),
            SubpixelJitter = new Vector4(subpixelJitter.X, subpixelJitter.Y, 0f, 0f),
            ReflectParams  = new Vector4(s.EnableReflections ? 1f : 0f, s.ReflectionBounces, s.Gloss, s.F0),
        };
    }

    private static MTLBuffer UploadStruct<T>(MTLDevice device, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var buf = device.NewBuffer((ulong)size, MTLResourceOptions.ResourceStorageModeShared);
        Marshal.StructureToPtr(value, buf.Contents, false);
        return buf;
    }

    private static unsafe float[] ReadFloat4Buffer(MTLBuffer buf, int count)
    {
        var result = new float[count * 4];
        fixed (float* dst = result)
            Buffer.MemoryCopy((void*)buf.Contents, dst,
                (long)result.Length * sizeof(float), (long)result.Length * sizeof(float));
        return result;
    }

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
        if (_disposed) throw new ObjectDisposedException(nameof(MetalBurningShipRenderer));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MetalFoldParams
    {
        public int Iterations, Mode, JuliaMode, Pad0;
        public Vector4 BoxParams, SurfParams, JuliaCVec, Rot, BoundSphere;
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
        public Vector4 SubpixelJitter, ReflectParams;
    }
}
