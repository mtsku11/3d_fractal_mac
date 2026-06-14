using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Raymarching;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

public sealed class MetalRiemannSphereRenderer : IDisposable
{
    private MTLDevice _device; private MTLCommandQueue _queue; private MTLComputePipelineState _pso;
    private bool _isAvailable; private bool _disposed;
    public bool IsAvailable => _isAvailable;
    public long LastComputeMs { get; private set; }
    public long LastReadbackMs { get; private set; }

    public MetalRiemannSphereRenderer()
    {
        try {
            var dev = MTLDevice.CreateSystemDefaultDevice();
            var src = MetalSurfaceTextureShaderInjector.Inject(LoadEmbeddedMsl("riemannsphere_raymarch.metal"));
            NSError le = default; var lib = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref le);
            var fn = lib.NewFunction(NSString.String("riemannsphere_raymarch"));
            NSError pe = default; _pso = dev.NewComputePipelineState(fn, ref pe);
            _queue = dev.NewCommandQueue(); _device = dev; _isAvailable = true;
        } catch { _isAvailable = false; }
    }

    public uint[] RenderRiemannSphere(RiemannSphereParams rs, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface, Vector3 lightDirection,
        PaletteParams palette,
        PostProcessParams? postProcess = null)
    {
        ThrowIfDisposed();
        if (!_isAvailable) throw new InvalidOperationException("Metal backend unavailable.");
        var _hdrAcc = MetalSsaa.AccumulateHdr(settings.HeroSamples, width, height, jitter =>
        {
            using var fb = UploadStruct(_device, BuildFoldParams(rs));
            using var rb = UploadStruct(_device, BuildRenderParams(camera, width, height, lightDirection, background, surface, settings, palette, jitter));
            using var ob = MetalBufferIO.CreateSharedBuffer(_device, (ulong)(width * height * 4 * sizeof(float)));
            var cmd = _queue.CommandBuffer(); var enc = cmd.ComputeCommandEncoder();
            enc.SetComputePipelineState(_pso!); enc.SetBuffer(fb,0,0); enc.SetBuffer(rb,0,1); enc.SetBuffer(ob,0,2);
            enc.SetTexture(MetalSurfaceTextureManager.GetTexture(_device), 0);
            enc.DispatchThreadgroups(new MTLSize{width=(ulong)((width+7)/8),height=(ulong)((height+7)/8),depth=1}, new MTLSize{width=8,height=8,depth=1});
            enc.EndEncoding();
            var sw = System.Diagnostics.Stopwatch.StartNew(); cmd.Commit(); cmd.WaitUntilCompleted(); LastComputeMs = sw.ElapsedMilliseconds;
            sw.Restart(); var result = ReadFloat4Buffer(ob, width * height); LastReadbackMs = sw.ElapsedMilliseconds;
            return result;
        });
        return MetalPostProcess.Apply(_device, _queue, _hdrAcc, width, height, postProcess);
    }

    // Trimmed MIDI-only telemetry pass — geometry stats + 4x4 cells + full-res centroid.
    private MTLComputePipelineState _telemetryPso;
    private bool _telemetryPsoReady, _telemetryPsoAvailable;

    public FractalGeometryStats? RunTelemetryPass(RiemannSphereParams fractal, Camera3D camera, RaymarchSettings settings)
    {
        if (!_isAvailable) return null;
        ThrowIfDisposed();
        if (!EnsureTelemetryPso()) return null;

        int gridW = 64, gridH = 36;
        int cellCount = gridW * gridH;
        int cellSize = Marshal.SizeOf<TelemetryCell>();

        using var foldBuf   = UploadStruct(_device, BuildFoldParams(fractal));
        using var telBuf    = UploadStruct(_device, BuildTelemetryParams(camera, gridW, gridH, settings));
        using var outputBuf = _device.NewBuffer((ulong)(cellCount * cellSize), MTLResourceOptions.ResourceStorageModeShared);

        var cmd = _queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_telemetryPso);
        enc.SetBuffer(foldBuf, 0, 0); enc.SetBuffer(telBuf, 0, 1); enc.SetBuffer(outputBuf, 0, 2);
        enc.DispatchThreadgroups(
            new MTLSize { width = (ulong)((gridW + 7) / 8), height = (ulong)((gridH + 7) / 8), depth = 1 },
            new MTLSize { width = 8, height = 8, depth = 1 });
        enc.EndEncoding();
        cmd.Commit(); cmd.WaitUntilCompleted();

        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * camera.AspectRatio;

        var cells = TelemetryReduction.Read(outputBuf, cellCount);
        return TelemetryReduction.Reduce(cells, gridW, gridH,
            camera.Position, fwd, right, up, tanX, tanY, settings.MaxDistance);
    }

    private bool EnsureTelemetryPso()
    {
        if (_telemetryPsoReady) return _telemetryPsoAvailable;
        _telemetryPsoReady = true;
        try
        {
            var src = LoadEmbeddedMsl("riemannsphere_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("riemannsphere_telemetry"));
            NSError psoErr = default;
            _telemetryPso = _device.NewComputePipelineState(function, ref psoErr);
            _telemetryPsoAvailable = true;
        }
        catch { /* leave _telemetryPsoAvailable = false */ }
        return _telemetryPsoAvailable;
    }

    private static MetalTelemetryParams BuildTelemetryParams(Camera3D camera, int gridW, int gridH, RaymarchSettings s)
    {
        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * camera.AspectRatio;
        return new MetalTelemetryParams
        {
            GridWidth = gridW, GridHeight = gridH, Pad0 = 0, Pad1 = 0,
            CamPos = new Vector4(camera.Position, 0f), CamForward = new Vector4(fwd, 0f),
            CamRight = new Vector4(right, 0f), CamUp = new Vector4(up, 0f),
            TanFov = new Vector4(tanX, tanY, 0f, 0f),
            March = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, 0f),
            MaxSteps = s.MaxSteps, Pad2 = 0, Pad3 = 0, Pad4 = 0,
        };
    }

    public void Dispose() { if (_disposed) return; _disposed = true; if (_isAvailable) { _pso.Dispose(); if (_telemetryPsoAvailable) _telemetryPso.Dispose(); _queue.Dispose(); _device.Dispose(); } }

    private static MetalFoldParams BuildFoldParams(RiemannSphereParams rs) => new()
    {
        Iterations = rs.Iterations, Mode = 0, JuliaMode = rs.JuliaMode, Pad0 = 0,
        BoxParams  = new Vector4(rs.Scale, rs.OffsetA, rs.OffsetB, rs.Bailout),
        SurfParams = new Vector4(rs.PowerClamp, 0f, 0f, 0f),
        JuliaCVec  = new Vector4(rs.JuliaC, 0f),
        Rot        = new Vector4(rs.RotationRadians, rs.Fudge),
        BoundSphere= new Vector4(0f, 0f, 0f, rs.BoundRadius),
    };

    private static MetalRenderParams BuildRenderParams(Camera3D camera, int width, int height, Vector3 lightDirection, Color background, Color surface, RaymarchSettings s, PaletteParams palette, Vector2 subpixelJitter = default) {
        var fwd=Vector3.Normalize(camera.LookAt-camera.Position); var right=Vector3.Normalize(Vector3.Cross(fwd,camera.Up)); var up=Vector3.Cross(right,fwd);
        float tanY=MathF.Tan(camera.VerticalFovRadians*0.5f); float tanX=tanY*((float)width/height); var ld=Vector3.Normalize(lightDirection);
        int flags=(s.EnableSoftShadows?1:0)|(s.EnableAmbientOcclusion?2:0);
        return new MetalRenderParams { ImageWidth=width,ImageHeight=height,RowOffset=0,RowCount=height,
            CamPos=new Vector4(camera.Position,0f),CamForward=new Vector4(fwd,0f),CamRight=new Vector4(right,0f),CamUp=new Vector4(up,0f),
            TanFov=new Vector4(tanX,tanY,0f,0f),LightDir=new Vector4(ld,s.LightIntensity),
            Background = MetalSurfaceTextureManager.EncodeBackground(background),Surface = MetalSurfaceTextureManager.EncodeSurface(surface),
            MarchA=new Vector4(s.HitEpsilon,s.MaxDistance,s.NormalEpsilon,s.ShadowSoftness),MarchB = MetalSurfaceTextureManager.EncodeMarchB(s.AOStepDistance, s.AOIntensity),
            MarchI0=s.MaxSteps,MarchI1=s.ShadowSteps,MarchI2=s.AOSamples,MarchI3=flags,
            PalBase=new Vector4(palette.Base,palette.Frequency),PalAmp=new Vector4(palette.Amp,palette.TrapScale),PalPhase=new Vector4(palette.Phase,palette.ShellMix),TrapMix=new Vector4(palette.TrapMix,DomainWarpState.GetPhase()),
            SubpixelJitter = DomainWarpState.EncodeSubpixelJitter(subpixelJitter),ReflectParams=new Vector4(s.EnableReflections?1f:0f,s.ReflectionBounces,s.Gloss,s.F0) };
    }

    private static MTLBuffer UploadStruct<T>(MTLDevice device, T value) where T : struct => MetalBufferIO.UploadStruct(device, value);
    private static float[] ReadFloat4Buffer(MTLBuffer buf, int count) => MetalBufferIO.ReadFloat4Buffer(buf, count);
    private static string LoadEmbeddedMsl(string fn) { var asm=Assembly.GetExecutingAssembly(); using var s=asm.GetManifestResourceStream($"Parsec.Rendering.Metal.Shaders.{fn}")??throw new FileNotFoundException(fn); using var r=new StreamReader(s); return r.ReadToEnd(); }
    private void ThrowIfDisposed() { if(_disposed) throw new ObjectDisposedException(nameof(MetalRiemannSphereRenderer)); }

    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct MetalFoldParams { public int Iterations,Mode,JuliaMode,Pad0; public Vector4 BoxParams,SurfParams,JuliaCVec,Rot,BoundSphere; }
    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct MetalRenderParams { public int ImageWidth,ImageHeight,RowOffset,RowCount; public Vector4 CamPos,CamForward,CamRight,CamUp,TanFov,LightDir,Background,Surface,MarchA,MarchB; public int MarchI0,MarchI1,MarchI2,MarchI3; public Vector4 PalBase,PalAmp,PalPhase,TrapMix,SubpixelJitter,ReflectParams; }
}
