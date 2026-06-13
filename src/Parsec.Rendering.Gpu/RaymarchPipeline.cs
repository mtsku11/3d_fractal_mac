using System.Numerics;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;

namespace Parsec.Rendering.Raymarching;

/// <summary>
/// Shared raymarch pipeline. Owns the SSBOs and shaders that every Gpu*Renderer
/// needs identically -- the accumulator buffer, the packed-pixel output buffer,
/// the fold-params and render-params buffers, plus the small clear/finalize
/// compute shaders that drive super-sampling.
///
/// Lifecycle: one instance per GL context. FractalView constructs the pipeline
/// in OnOpenGlInit and passes it to every renderer's constructor; renderers
/// own only their per-fractal compute shader.
///
/// Render flow per call:
///   1. Allocate / resize buffers for this image size.
///   2. Clear the vec4 accumulator buffer to zero (one dispatch).
///   3. For each AA sample (1 for preview, N for hero):
///        Compute Halton-jittered sub-pixel offset.
///        For each tile:
///          Upload RenderParams (with jitter), dispatch fractalShader.
///          fractalShader reads RenderParams + FoldParams, writes accumulated
///          vec4 colors into the accumulator buffer.
///   4. Finalize: divide accumulator by sampleCount, pack to RGBA8 (one dispatch).
///   5. Download and return the uint[] image.
///
/// SSAA = "supersampling antialiasing." sampleCount=1 reproduces the old single-
/// ray-per-pixel behavior exactly; sampleCount=4/9/16 fires N jittered rays per
/// pixel and averages them, giving smooth edges and resolving fine fractal detail
/// that aliases into noise at 1x.
///
/// SSBO binding map (chosen to avoid the attractor core's 6/7/8):
///   1 = FoldParams (per-fractal core)      4 = RenderParams (raymarch_main)
///   2 = packed RGBA8 output (finalize)     5 = vec4 accumulator (raymarch_main)
///   3 = AAParams (clear + finalize)        6/7/8 = attractor trajectory/hash/idx
/// </summary>
public sealed class RaymarchPipeline : IDisposable
{
    private readonly Gl _gl;
    private readonly StorageBuffer<FoldParamsGpu> _foldBuffer;
    private readonly StorageBuffer<RenderParamsGpu> _renderBuffer;
    private readonly StorageBuffer<Vector4> _accumBuffer;
    private readonly StorageBuffer<uint> _imageBuffer;
    private readonly StorageBuffer<AAParamsGpu> _aaParamsBuffer;
    private readonly ComputeShader _clearShader;
    private readonly ComputeShader _finalizeShader;
    private uint _surfaceTexture;
    private int _surfaceTextureVersion = -1;
    private bool _disposed;

    public RaymarchPipeline(Gl gl)
    {
        _gl = gl;
        _foldBuffer = new StorageBuffer<FoldParamsGpu>(gl);
        _renderBuffer = new StorageBuffer<RenderParamsGpu>(gl);
        _accumBuffer = new StorageBuffer<Vector4>(gl);
        _imageBuffer = new StorageBuffer<uint>(gl);
        _aaParamsBuffer = new StorageBuffer<AAParamsGpu>(gl);
        _clearShader = ComputeShader.FromSource(gl, ClearShaderSource, "aa_clear");
        _finalizeShader = ComputeShader.FromSource(gl, FinalizeShaderSource, "aa_finalize");
    }

    /// <summary>
    /// Run the fractal shader N times (N = heroSamples) with Halton-jittered
    /// sub-pixel offsets, accumulating into a vec4 buffer, then finalize to
    /// packed RGBA8 and download. The fractal shader is whatever
    /// ComputeShader the calling Gpu*Renderer built from its *_core.glsl +
    /// raymarch_main.glsl composite source.
    /// </summary>
    internal uint[] Render(
        ComputeShader fractalShader,
        FoldParamsGpu foldParams,
        Camera3D camera,
        int width, int height,
        RaymarchSettings settings,
        Color background, Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        int heroSamples,
        int tileRows,
        Action<int, int>? progress)
    {
        ThrowIfDisposed();
        int samples = Math.Max(1, heroSamples);
        int pixelCount = width * height;

        _accumBuffer.Allocate(pixelCount);
        _imageBuffer.Allocate(pixelCount);

        // Upload params that are constant across the whole render.
        _foldBuffer.Upload(new[] { foldParams });
        _aaParamsBuffer.Upload(new[] { new AAParamsGpu {
            Width = width, Height = height, SampleCount = samples, Pad0 = 0,
        }});

        // Clear the accumulator.
        _accumBuffer.BindBase(5);
        _aaParamsBuffer.BindBase(3);
        _clearShader.Use();
        _clearShader.Dispatch((width + 7) / 8, (height + 7) / 8);
        _gl.MemoryBarrier(GlConst.ShaderStorageBarrierBit);

        // Pre-build the camera frame and lighting; these don't change between
        // AA samples (only the sub-pixel jitter does).
        var frame = CameraFrame.Build(camera, width, height);
        var lightDir = Vector3.Normalize(lightDirection);
        int flags = (settings.EnableSoftShadows ? 1 : 0)
                  | (settings.EnableAmbientOcclusion ? 2 : 0);

        int tiles = (height + tileRows - 1) / tileRows;
        int totalUnits = samples * tiles;
        int unitsDone = 0;

        // Main render: N AA samples * T tiles.
        _foldBuffer.BindBase(1);
        _renderBuffer.BindBase(4);
        _accumBuffer.BindBase(5);
        fractalShader.Use();
        EnsureSurfaceTextureBound(fractalShader);

        for (int sample = 0; sample < samples; sample++)
        {
            var jitter = (samples == 1) ? Vector2.Zero : HaltonJitter(sample);

            for (int tile = 0; tile < tiles; tile++)
            {
                int rowOffset = tile * tileRows;
                int rowCount = Math.Min(tileRows, height - rowOffset);

                _renderBuffer.Upload(new[] { BuildRenderParams(
                    width, height, rowOffset, rowCount, frame, camera,
                    lightDir, background, surface, settings, palette, flags, jitter) });
                _renderBuffer.BindBase(4);

                int groupsX = (width + 7) / 8;
                int groupsY = (rowCount + 7) / 8;
                fractalShader.Dispatch(groupsX, groupsY);
                _gl.MemoryBarrier(GlConst.ShaderStorageBarrierBit);
                _gl.Finish();

                unitsDone++;
                progress?.Invoke(unitsDone, totalUnits);
            }
        }

        // Finalize: divide accumulator by sampleCount, pack to RGBA8.
        _accumBuffer.BindBase(5);
        _imageBuffer.BindBase(2);
        _aaParamsBuffer.BindBase(3);
        _finalizeShader.Use();
        _finalizeShader.Dispatch((width + 7) / 8, (height + 7) / 8);
        _gl.MemoryBarrier(GlConst.ShaderStorageBarrierBit);
        _gl.Finish();

        return _imageBuffer.Download();
    }

    private static RenderParamsGpu BuildRenderParams(
        int width, int height, int rowOffset, int rowCount,
        CameraFrame frame, Camera3D camera, Vector3 lightDir,
        Color background, Color surface, RaymarchSettings s,
        PaletteParams palette, int flags, Vector2 jitter)
    {
        return new RenderParamsGpu
        {
            ImageWidth = width, ImageHeight = height,
            RowOffset = rowOffset, RowCount = rowCount,
            CamPos = new Vector4(camera.Position, 0),
            CamForward = new Vector4(frame.Forward, 0),
            CamRight = new Vector4(frame.Right, 0),
            CamUp = new Vector4(frame.Up, 0),
            TanFov = new Vector4(frame.TanFovX, frame.TanFovY, 0, 0),
            LightDir = new Vector4(lightDir, s.LightIntensity),
            Background = GpuSurfaceTextureManager.EncodeBackground(background),
            Surface = GpuSurfaceTextureManager.EncodeSurface(surface),
            MarchA = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, s.ShadowSoftness),
            MarchB = GpuSurfaceTextureManager.EncodeMarchB(s.AOStepDistance, s.AOIntensity),
            MarchI0 = s.MaxSteps, MarchI1 = s.ShadowSteps,
            MarchI2 = s.AOSamples, MarchI3 = flags,
            PalBase = new Vector4(palette.Base, palette.Frequency),
            PalAmp = new Vector4(palette.Amp, palette.TrapScale),
            PalPhase = new Vector4(palette.Phase, palette.ShellMix),
            TrapMix = new Vector4(palette.TrapMix, 0f),
            SubpixelJitter = DomainWarpState.EncodeSubpixelJitter(jitter),
            ReflectParams = new Vector4(
                s.EnableReflections ? 1f : 0f,
                s.ReflectionBounces,
                s.Gloss,
                s.F0),
        };
    }

    /// <summary>Halton(2,3) sub-pixel jitter for sample index. Returns offset
    /// in [-0.5, 0.5] x [-0.5, 0.5]. Low-discrepancy quasi-random; produces
    /// better perceptual quality at low sample counts than uniform random.</summary>
    private static Vector2 HaltonJitter(int sampleIndex)
    {
        int n = sampleIndex + 1;  // Halton(0) is degenerate; use 1-based
        return new Vector2(Halton(n, 2) - 0.5f, Halton(n, 3) - 0.5f);
    }

    private static float Halton(int index, int b)
    {
        float f = 1f;
        float result = 0f;
        while (index > 0)
        {
            f /= b;
            result += f * (index % b);
            index /= b;
        }
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RaymarchPipeline));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_surfaceTexture != 0) _gl.DeleteTexture(_surfaceTexture);
        _clearShader.Dispose();
        _finalizeShader.Dispose();
        _foldBuffer.Dispose();
        _renderBuffer.Dispose();
        _accumBuffer.Dispose();
        _imageBuffer.Dispose();
        _aaParamsBuffer.Dispose();
    }

    private unsafe void EnsureSurfaceTextureBound(ComputeShader fractalShader)
    {
        if (_surfaceTexture == 0)
        {
            _surfaceTexture = _gl.GenTexture();
            _gl.ActiveTexture(GlConst.Texture0);
            _gl.BindTexture(GlConst.Texture2D, _surfaceTexture);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMinFilter, (int)GlConst.Linear);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMagFilter, (int)GlConst.Linear);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapS, (int)GlConst.Repeat);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapT, (int)GlConst.Repeat);
        }

        var snapshot = GpuSurfaceTextureManager.GetSnapshot();
        _gl.ActiveTexture(GlConst.Texture0);
        _gl.BindTexture(GlConst.Texture2D, _surfaceTexture);
        if (_surfaceTextureVersion != snapshot.Version)
        {
            fixed (byte* ptr = snapshot.Bytes)
            {
                _gl.TexImage2D(
                    GlConst.Texture2D, 0, (int)GlConst.Rgba8,
                    snapshot.Width, snapshot.Height, 0,
                    GlConst.Rgba, GlConst.UnsignedByte, (IntPtr)ptr);
            }
            _surfaceTextureVersion = snapshot.Version;
        }

        int loc = fractalShader.UniformLocation("uSurfaceTexture");
        if (loc >= 0) _gl.Uniform1i(loc, 0);
    }

    // ------------------------------------------------------------------------
    // Embedded helper shaders (clear + finalize). Self-contained so the
    // pipeline doesn't need extra embedded resources.
    // ------------------------------------------------------------------------

    private const string ClearShaderSource = @"
#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 5) buffer Accum {
    vec4 colors[];
} accum;

layout(std430, binding = 3) readonly buffer AAParams {
    int width;
    int height;
    int sampleCount;
    int pad0;
} aa;

void main() {
    int px = int(gl_GlobalInvocationID.x);
    int py = int(gl_GlobalInvocationID.y);
    if (px >= aa.width || py >= aa.height) return;
    accum.colors[py * aa.width + px] = vec4(0.0);
}
";

    private const string FinalizeShaderSource = @"
#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 5) readonly buffer Accum {
    vec4 colors[];
} accum;

layout(std430, binding = 2) writeonly buffer Output {
    uint pixels[];
} img;

layout(std430, binding = 3) readonly buffer AAParams {
    int width;
    int height;
    int sampleCount;
    int pad0;
} aa;

void main() {
    int px = int(gl_GlobalInvocationID.x);
    int py = int(gl_GlobalInvocationID.y);
    if (px >= aa.width || py >= aa.height) return;
    int idx = py * aa.width + px;
    vec4 c = accum.colors[idx] / float(aa.sampleCount);
    uvec3 q = uvec3(clamp(c.rgb, 0.0, 1.0) * 255.0 + 0.5);
    img.pixels[idx] = (255u << 24) | (q.b << 16) | (q.g << 8) | q.r;
}
";
}
