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
/// Metal compute backend for RotBox (rotation-augmented Mandelbox) raymarching.
/// Compiles rotbox_raymarch.metal at construction and dispatches a full-image
/// compute pass on each RenderRotBox call. Returns packed RGBA8 uint[] with
/// the same packing as the OpenGL finalize shader in RaymarchPipeline.
/// </summary>
public sealed class MetalRotBoxRenderer : IDisposable
{
    private MTLDevice               _device;
    private MTLCommandQueue         _queue;
    private MTLComputePipelineState _pso;
    private bool                    _isAvailable;
    private bool                    _disposed;

    public bool IsAvailable => _isAvailable;

    /// <summary>Time spent waiting for the GPU command buffer to complete (milliseconds).</summary>
    public long LastComputeMs { get; private set; }

    /// <summary>Time spent copying the output buffer from GPU-shared memory into a managed uint[] (milliseconds).</summary>
    public long LastReadbackMs { get; private set; }

    public MetalRotBoxRenderer()
    {
        try
        {
            var dev = MTLDevice.CreateSystemDefaultDevice();
            var src = LoadEmbeddedMsl("rotbox_raymarch.metal");
            NSError libError = default;
            var library = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libError);
            var function = library.NewFunction(NSString.String("rotbox_raymarch"));
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

    public uint[] RenderRotBox(
        RotBoxParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette)
    {
        ThrowIfDisposed();
        if (!_isAvailable)
            throw new InvalidOperationException("Metal backend is not available on this machine.");
        return MetalSsaa.Accumulate(settings.HeroSamples, width, height, jitter =>
        {
            int pixelCount = width * height;
            using var foldBuf   = UploadStruct(_device, BuildFoldParams(fractal));
            using var renderBuf = UploadStruct(_device, BuildRenderParams(camera, width, height, lightDirection, background, surface, settings, palette, jitter));
            using var outBuf    = _device.NewBuffer((ulong)(pixelCount * sizeof(uint)), MTLResourceOptions.ResourceStorageModeShared);

            var cmd = _queue.CommandBuffer();
            var enc = cmd.ComputeCommandEncoder();

            enc.SetComputePipelineState(_pso!);
            enc.SetBuffer(foldBuf,   0, 0);
            enc.SetBuffer(renderBuf, 0, 1);
            enc.SetBuffer(outBuf,    0, 2);

            enc.DispatchThreadgroups(
                new MTLSize { width = (ulong)((width + 7) / 8), height = (ulong)((height + 7) / 8), depth = 1 },
                new MTLSize { width = 8, height = 8, depth = 1 });
            enc.EndEncoding();

            var computeSw = System.Diagnostics.Stopwatch.StartNew();
            cmd.Commit();
            cmd.WaitUntilCompleted();
            LastComputeMs = computeSw.ElapsedMilliseconds;

            var readbackSw = System.Diagnostics.Stopwatch.StartNew();
            var result = ReadUintBuffer(outBuf, pixelCount);
            LastReadbackMs = readbackSw.ElapsedMilliseconds;
            return result;
        });
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

    private static MetalFoldParams BuildFoldParams(RotBoxParams rb) => new()
    {
        Iterations  = rb.Iterations,
        Mode        = 0,
        JuliaMode   = 0,
        Pad0        = 0,
        // boxParams: (scale, minRadius, fixedRadius, foldLimit) — matches rotbox_core.glsl
        BoxParams   = new Vector4(rb.Scale, rb.MinRadius, rb.FixedRadius, rb.FoldLimit),
        SurfParams  = new Vector4(rb.Rotation, 0f),   // Euler angles in surfParams.xyz
        JuliaCVec   = Vector4.Zero,
        Rot         = new Vector4(0f, 0f, 0f, rb.Fudge),
        BoundSphere = new Vector4(0f, 0f, 0f, rb.BoundRadius),
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
            Background  = new Vector4(background.R, background.G, background.B, 1f),
            Surface     = new Vector4(surface.R,    surface.G,    surface.B,    1f),
            MarchA      = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, s.ShadowSoftness),
            MarchB      = new Vector4(s.AOStepDistance, s.AOIntensity, 0f, 0f),
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
    {
        int size = Marshal.SizeOf<T>();
        var buf = device.NewBuffer((ulong)size, MTLResourceOptions.ResourceStorageModeShared);
        Marshal.StructureToPtr(value, buf.Contents, false);
        return buf;
    }

    private static unsafe uint[] ReadUintBuffer(MTLBuffer buf, int count)
    {
        var result = new uint[count];
        fixed (uint* dst = result)
        {
            Buffer.MemoryCopy((void*)buf.Contents, dst, (long)count * sizeof(uint), (long)count * sizeof(uint));
        }
        return result;
    }

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
        if (_disposed) throw new ObjectDisposedException(nameof(MetalRotBoxRenderer));
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
}
