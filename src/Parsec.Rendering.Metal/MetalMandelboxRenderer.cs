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
/// Metal compute backend for Mandelbox raymarching.
/// Compiles mandelbox_raymarch.metal at construction and dispatches a full-image
/// compute pass on each RenderMandelbox call. Returns packed RGBA8 uint[] with
/// the same packing as the OpenGL finalize shader in RaymarchPipeline.
/// </summary>
public sealed class MetalMandelboxRenderer : IThreeDimensionalRenderBackend
{
    // SharpMetal types are structs; hold as non-nullable fields, guard via _isAvailable.
    private MTLDevice               _device;
    private MTLCommandQueue         _queue;
    private MTLComputePipelineState _pso;
    private bool                    _isAvailable;
    private bool                    _disposed;

    // Telemetry PSO (lazy-compiled on first RunTelemetryPass call).
    private MTLComputePipelineState _telemetryPso;
    private bool                    _telemetryPsoReady;
    private bool                    _telemetryPsoAvailable;

    public bool IsAvailable => _isAvailable;

    /// <summary>Time spent waiting for the GPU command buffer to complete (milliseconds).</summary>
    public long LastComputeMs { get; private set; }

    /// <summary>Time spent copying the output buffer from GPU-shared memory into a managed uint[] (milliseconds).</summary>
    public long LastReadbackMs { get; private set; }

    public MetalMandelboxRenderer()
    {
        try
        {
            var dev = MTLDevice.CreateSystemDefaultDevice();
            var src = MetalSurfaceTextureShaderInjector.InjectOrbitTrap(LoadEmbeddedMsl("mandelbox_raymarch.metal"));
            NSError libError = default;
            var library = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libError);
            var function = library.NewFunction(NSString.String("mandelbox_raymarch"));
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

    // Backward-compatible overload satisfying IThreeDimensionalRenderBackend (identity post-process).
    public uint[] RenderMandelbox(
        MandelboxParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette)
        => RenderMandelbox(fractal, camera, width, height, settings, background, surface, lightDirection, palette, null);

    public uint[] RenderMandelbox(
        MandelboxParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        PostProcessParams? postProcess)
    {
        ThrowIfDisposed();
        if (!_isAvailable)
            throw new InvalidOperationException("Metal backend is not available on this machine.");
        var hdr = MetalSsaa.AccumulateHdr(settings.HeroSamples, width, height, jitter =>
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
        return MetalPostProcess.Apply(_device, _queue, hdr, width, height, postProcess);
    }

    /// <summary>
    /// Runs a low-resolution (64×36) telemetry march of the Mandelbox DE using a
    /// separate, lazily-compiled PSO.  Returns reduced per-frame geometry statistics
    /// (<see cref="FractalGeometryStats"/>) that feed <c>SonificationController</c>.
    /// Returns null if Metal is unavailable or the telemetry shader failed to compile.
    /// </summary>
    public FractalGeometryStats? RunTelemetryPass(
        MandelboxParams fractal,
        Camera3D camera,
        RaymarchSettings settings)
    {
        if (!_isAvailable) return null;
        ThrowIfDisposed();
        if (!EnsureTelemetryPso()) return null;

        int gridW = 64, gridH = 36;
        int cellCount = gridW * gridH;
        int cellSize = Marshal.SizeOf<MandelboxTelemetryCell>();

        // M7b: 4×4 spatial tiles × 2 wavetables × 64 samples × 4 bytes = 8 KB
        const int WavetableTiles   = 16;
        const int WavetableBytes   = WavetableTiles * 64 * 2 * 4;
        const int WaveshaperBytes  = 64 * 4;   // M7h: 64 floats
        const int OrbitTrajBytes   = WavetableTiles * 128 * 16; // M9a: 16 tiles × 128 float4 = 32 KB

        using var foldBuf        = UploadStruct(_device, BuildFoldParams(fractal));
        using var telBuf         = UploadStruct(_device, BuildTelemetryParams(camera, gridW, gridH, settings));
        using var outputBuf      = _device.NewBuffer((ulong)(cellCount * cellSize),
                                                     MTLResourceOptions.ResourceStorageModeShared);
        using var wavetableBuf   = _device.NewBuffer((ulong)WavetableBytes,
                                                     MTLResourceOptions.ResourceStorageModeShared);
        using var waveshaperBuf  = _device.NewBuffer((ulong)WaveshaperBytes,
                                                     MTLResourceOptions.ResourceStorageModeShared);
        using var orbitTrajBuf   = _device.NewBuffer((ulong)OrbitTrajBytes,
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

        var cells      = ReadTelemetryCells(outputBuf, cellCount);
        var stats      = ReduceTelemetry(cells, gridW, gridH,
            camera.Position, fwd, right, up, tanX, tanY, settings.MaxDistance);
        var waveshaper = TelemetryReduction.ReadWaveshaperCurve(waveshaperBuf);

        // Enrich each spatial cell with wavetable (M7b) and orbit trajectory (M9a) data.
        if (stats.Cells is { Length: > 0 })
        {
            var wavetables   = ReadWavetableCells(wavetableBuf, WavetableTiles);
            var trajectories = TelemetryReduction.ReadOrbitTrajectories(orbitTrajBuf, WavetableTiles);
            var enriched     = new MetalSpatialCell[stats.Cells.Length];
            for (int ti = 0; ti < stats.Cells.Length; ti++)
                enriched[ti] = stats.Cells[ti] with {
                    RayWavetable    = wavetables[ti].ray,
                    OrbitWavetable  = wavetables[ti].orbit,
                    OrbitTrajectory = trajectories[ti]
                };
            return stats with { Cells = enriched, WaveshaperCurve = waveshaper };
        }
        return stats with { WaveshaperCurve = waveshaper };
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
    /// Dispatches a 64-thread 1-D kernel that samples the Mandelbox DE field along
    /// a 3:2:1 Lissajous orbit centred on the camera.  Returns a 64-sample waveform
    /// (AC-coupled, normalised [-1,1]) for use as a drone wavetable in M8 synthesis.
    /// Returns null if the field-scan PSO failed to compile.
    /// </summary>
    public (float[]? tl, float[]? tr, float[]? bl, float[]? br) RunFieldScanPass(MandelboxParams fractal, Camera3D camera, RaymarchSettings settings)
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
            var src = LoadEmbeddedMsl("mandelbox_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("mandelbox_fieldscan"));
            NSError psoErr = default;
            _fieldScanPso = _device.NewComputePipelineState(function, ref psoErr);
            _fieldScanPsoAvailable = true;
        }
        catch { /* leave _fieldScanPsoAvailable = false */ }
        return _fieldScanPsoAvailable;
    }

    // -------------------------------------------------------------------------
    // Parameter builders
    // -------------------------------------------------------------------------

    private static MetalFoldParams BuildFoldParams(MandelboxParams mb) => new()
    {
        Iterations  = mb.Iterations,
        Mode        = mb.Mode,
        JuliaMode   = mb.JuliaMode,
        Pad0        = 0,
        BoxParams   = new Vector4(mb.Scale, mb.FoldingLimit, mb.MinRadius, mb.FixedRadius),
        SurfParams  = new Vector4(mb.FoldXY, mb.ScaleVary, 0f, 0f),
        JuliaCVec   = new Vector4(mb.JuliaC, 0f),
        Rot         = new Vector4(mb.RotationRadians, mb.Fudge),
        BoundSphere = new Vector4(0f, 0f, 0f, mb.BoundRadius),
    };

    private static MetalRenderParams BuildRenderParams(
        Camera3D camera, int width, int height,
        Vector3 lightDirection, Color background, Color surface,
        RaymarchSettings s, PaletteParams palette, Vector2 jitter = default)
    {
        // Rebuild camera frame inline (mirrors CameraFrame.Build from Parsec.Rendering.Gpu)
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
            TanFov      = GlowState.EncodeTanFov(tanX, tanY),
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

    // -------------------------------------------------------------------------
    // Embedded MSL loader
    // -------------------------------------------------------------------------

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
        if (_disposed) throw new ObjectDisposedException(nameof(MetalMandelboxRenderer));
    }

    // -------------------------------------------------------------------------
    // Telemetry helpers
    // -------------------------------------------------------------------------

    private bool EnsureTelemetryPso()
    {
        if (_telemetryPsoReady) return _telemetryPsoAvailable;
        _telemetryPsoReady = true;
        try
        {
            var src = LoadEmbeddedMsl("mandelbox_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("mandelbox_telemetry"));
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

    // M7b: read 16-tile wavetable buffer and normalise each 64-sample array to [-1, 1].
    private static unsafe (float[] ray, float[] orbit)[] ReadWavetableCells(MTLBuffer buf, int tileCount)
    {
        const int N = 64;
        const int BytesPerArray = N * 4;   // 256 bytes (64 floats × 4 bytes)
        const int BytesPerCell  = BytesPerArray * 2; // 512 bytes
        var result = new (float[], float[])[tileCount];
        var src = (byte*)MetalBufferIO.RequireContents(buf, "wavetable cell readback");
        for (int i = 0; i < tileCount; i++)
        {
            var ray   = new float[N];
            var orbit = new float[N];
            fixed (float* dstR = ray)
                Buffer.MemoryCopy(src + i * BytesPerCell, dstR, BytesPerArray, BytesPerArray);
            fixed (float* dstO = orbit)
                Buffer.MemoryCopy(src + i * BytesPerCell + BytesPerArray, dstO, BytesPerArray, BytesPerArray);
            NormalizeWavetable(ray);
            NormalizeWavetable(orbit);
            result[i] = (ray, orbit);
        }
        return result;
    }

    private static void NormalizeWavetable(float[] data)
    {
        // Percentile clamp: use 5th/95th percentile as lo/hi so a single outlier
        // (e.g. one escape sample in an orbit sequence) doesn't flatten the rest.
        var sorted = (float[])data.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        float lo = sorted[Math.Max(0, (int)(n * 0.05f))];
        float hi = sorted[Math.Min(n - 1, (int)(n * 0.95f))];
        float range = hi - lo;
        if (range < 1e-6f) { Array.Clear(data); return; }
        for (int i = 0; i < n; i++)
            data[i] = Math.Clamp((data[i] - lo) / range * 2f - 1f, -1f, 1f);
    }

    private static unsafe MandelboxTelemetryCell[] ReadTelemetryCells(MTLBuffer buf, int count)
    {
        var result = new MandelboxTelemetryCell[count];
        int size = Marshal.SizeOf<MandelboxTelemetryCell>();
        IntPtr ptr = MetalBufferIO.RequireContents(buf, "mandelbox telemetry readback");
        fixed (MandelboxTelemetryCell* dst = result)
        {
            Buffer.MemoryCopy((void*)ptr, dst,
                (long)count * size, (long)count * size);
        }
        return result;
    }

    private static FractalGeometryStats ReduceTelemetry(
        MandelboxTelemetryCell[] cells, int gridW, int gridH,
        Vector3 camPos, Vector3 camFwd, Vector3 camRight, Vector3 camUp,
        float tanFovX, float tanFovY, float maxDist)
    {
        int n = cells.Length;
        int hits = 0;
        double depthSum = 0, depthSumSq = 0;
        double stepSum = 0;
        double nxSum = 0, nySum = 0, nzSum = 0;
        double txSum = 0, tySum = 0, tzSum = 0, twSum = 0;

        var steps = new int[n];
        for (int i = 0; i < n; i++)
        {
            ref var c = ref cells[i];
            steps[i] = c.Steps;
            stepSum += c.Steps;
            if (c.Hit == 0) continue;
            hits++;
            depthSum   += c.Depth;
            depthSumSq += (double)c.Depth * c.Depth;
            nxSum += c.Normal.X; nySum += c.Normal.Y; nzSum += c.Normal.Z;
            txSum += c.Trap.X;   tySum += c.Trap.Y;
            tzSum += c.Trap.Z;   twSum += c.Trap.W;
        }

        float hitRatio = (float)hits / n;
        float stepMean = (float)(stepSum / n);

        Array.Sort(steps);
        float stepP90 = steps[Math.Min((int)(n * 0.9), n - 1)];

        // Compute 4×4 spatial cells regardless of global hit count
        var spatialCells = ComputeSpatialCells(cells, gridW, gridH,
            camPos, camFwd, camRight, camUp, tanFovX, tanFovY, maxDist);

        if (hits == 0)
        {
            return new FractalGeometryStats(hitRatio, 0f, 0f, stepMean, stepP90,
                Vector3.Zero, 0f, Vector4.Zero, Vector4.Zero, Cells: spatialCells);
        }

        float meanDepth  = (float)(depthSum / hits);
        float depthVar   = Math.Max(0f, (float)(depthSumSq / hits - (depthSum / hits) * (depthSum / hits)));
        var   normalMean = new Vector3((float)(nxSum / hits), (float)(nySum / hits), (float)(nzSum / hits));
        var   trapMean   = new Vector4(
            (float)(txSum / hits), (float)(tySum / hits),
            (float)(tzSum / hits), (float)(twSum / hits));

        double nVarSum = 0;
        double tvxSum = 0, tvySum = 0, tvzSum = 0, tvwSum = 0;
        for (int i = 0; i < n; i++)
        {
            ref var c = ref cells[i];
            if (c.Hit == 0) continue;
            var dn = new Vector3(c.Normal.X, c.Normal.Y, c.Normal.Z) - normalMean;
            nVarSum += dn.Length();
            tvxSum += Math.Abs(c.Trap.X - trapMean.X);
            tvySum += Math.Abs(c.Trap.Y - trapMean.Y);
            tvzSum += Math.Abs(c.Trap.Z - trapMean.Z);
            tvwSum += Math.Abs(c.Trap.W - trapMean.W);
        }
        float normalVar = (float)(nVarSum / hits);
        var   trapVar   = new Vector4(
            (float)(tvxSum / hits), (float)(tvySum / hits),
            (float)(tvzSum / hits), (float)(tvwSum / hits));

        return new FractalGeometryStats(hitRatio, meanDepth, depthVar, stepMean, stepP90,
            normalMean, normalVar, trapMean, trapVar, Cells: spatialCells);
    }

    // Partition the 64×36 telemetry grid into a 4×4 array of spatial cells.
    // Each cell covers 16×9 rays; WorldPosition is the mean hit point in world space.
    private static MetalSpatialCell[] ComputeSpatialCells(
        MandelboxTelemetryCell[] cells, int gridW, int gridH,
        Vector3 camPos, Vector3 camFwd, Vector3 camRight, Vector3 camUp,
        float tanFovX, float tanFovY, float maxDist)
    {
        const int TilesX = 4, TilesY = 4;
        int tileW = gridW / TilesX;   // 16
        int tileH = gridH / TilesY;   // 9
        var result = new MetalSpatialCell[TilesX * TilesY];

        for (int ty = 0; ty < TilesY; ty++)
        for (int tx = 0; tx < TilesX; tx++)
        {
            int tileHits = 0;
            double depSum = 0, nxS = 0, nyS = 0, nzS = 0;
            double txS = 0, tyS = 0, tzS = 0, twS = 0;
            double wxS = 0, wyS = 0, wzS = 0;
            int maxSteps = 0;
            int tileTotal = tileW * tileH;

            for (int py = ty * tileH; py < (ty + 1) * tileH; py++)
            for (int px = tx * tileW; px < (tx + 1) * tileW; px++)
            {
                ref var c = ref cells[py * gridW + px];
                if (c.Steps > maxSteps) maxSteps = c.Steps;
                if (c.Hit == 0) continue;

                tileHits++;
                depSum += c.Depth;
                nxS += c.Normal.X; nyS += c.Normal.Y; nzS += c.Normal.Z;
                txS += c.Trap.X;   tyS += c.Trap.Y;
                tzS += c.Trap.Z;   twS += c.Trap.W;

                // Reconstruct world position for this ray
                float u = (px + 0.5f) / gridW;
                float v = 1f - (py + 0.5f) / gridH;
                float rx = (2f * u - 1f) * tanFovX;
                float ry = (2f * v - 1f) * tanFovY;
                var rd = Vector3.Normalize(camFwd + rx * camRight + ry * camUp);
                wxS += camPos.X + rd.X * c.Depth;
                wyS += camPos.Y + rd.Y * c.Depth;
                wzS += camPos.Z + rd.Z * c.Depth;
            }

            int cellIdx = ty * TilesX + tx;
            float hitRatio = (float)tileHits / tileTotal;

            if (tileHits == 0)
            {
                // No hits: place emitter at the tile's cone center, at maxDist
                float cu = (tx + 0.5f) / TilesX;
                float cv = 1f - (ty + 0.5f) / TilesY;
                float crx = (2f * cu - 1f) * tanFovX;
                float cry = (2f * cv - 1f) * tanFovY;
                var crd = Vector3.Normalize(camFwd + crx * camRight + cry * camUp);
                result[cellIdx] = new MetalSpatialCell(
                    WorldPosition:  camPos + crd * maxDist,
                    HitRatio:       0f,
                    MeanDepth:      maxDist,
                    StepComplexity: 0f,
                    NormalMean:     Vector3.Zero,
                    TrapMean:       Vector4.Zero,
                    Energy:         0f);
                continue;
            }

            float meanDepth    = (float)(depSum / tileHits);
            var   worldPos     = new Vector3((float)(wxS / tileHits), (float)(wyS / tileHits), (float)(wzS / tileHits));
            var   normalMean   = new Vector3((float)(nxS / tileHits), (float)(nyS / tileHits), (float)(nzS / tileHits));
            var   trapMean     = new Vector4((float)(txS / tileHits), (float)(tyS / tileHits),
                                             (float)(tzS / tileHits), (float)(twS / tileHits));
            float stepComplexity = Math.Min(1f, maxSteps / 80f);
            float energy         = hitRatio * MathF.Max(0f, 1f - meanDepth / maxDist);

            result[cellIdx] = new MetalSpatialCell(
                WorldPosition:  worldPos,
                HitRatio:       hitRatio,
                MeanDepth:      meanDepth,
                StepComplexity: stepComplexity,
                NormalMean:     normalMean,
                TrapMean:       trapMean,
                Energy:         energy);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // GPU parameter structs — layout must match the MSL structs in the shader
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MetalFoldParams
    {
        public int Iterations, Mode, JuliaMode, Pad0;    // 16 bytes at 0
        public Vector4 BoxParams;                         // 16 bytes at 16
        public Vector4 SurfParams;                        // 16 bytes at 32
        public Vector4 JuliaCVec;                         // 16 bytes at 48
        public Vector4 Rot;                               // 16 bytes at 64
        public Vector4 BoundSphere;                       // 16 bytes at 80
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MetalRenderParams
    {
        public int ImageWidth, ImageHeight, RowOffset, RowCount;  // 16 at 0
        public Vector4 CamPos, CamForward, CamRight, CamUp, TanFov; // 80 at 16
        public Vector4 LightDir, Background, Surface;             // 48 at 96
        public Vector4 MarchA, MarchB;                            // 32 at 144
        public int MarchI0, MarchI1, MarchI2, MarchI3;           // 16 at 176
        public Vector4 PalBase, PalAmp, PalPhase, TrapMix;       // 64 at 192
        public Vector4 SubpixelJitter;                            // 16 at 256
        public Vector4 ReflectParams;                             // 16 at 272
    }

    // Matches TelemetryParams in mandelbox_telemetry.metal (128 bytes)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MetalTelemetryParams
    {
        public int GridWidth, GridHeight, Pad0, Pad1;           // 16 at 0
        public Vector4 CamPos;                                   // 16 at 16
        public Vector4 CamForward;                               // 16 at 32
        public Vector4 CamRight;                                 // 16 at 48
        public Vector4 CamUp;                                    // 16 at 64
        public Vector4 TanFov;                                   // 16 at 80
        public Vector4 March;                                    // 16 at 96 (hitEps, maxDist, normalEps, 0)
        public int MaxSteps, Pad2, Pad3, Pad4;                  // 16 at 112
    }

    // Matches TelemetryCell in mandelbox_telemetry.metal (48 bytes)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MandelboxTelemetryCell
    {
        public int Hit, Steps;    // 8 at 0
        public float Depth, Pad;  // 8 at 8
        public Vector4 Normal;    // 16 at 16
        public Vector4 Trap;      // 16 at 32
    }
}
