using System.Reflection;
using System.Runtime.InteropServices;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

/// <summary>
/// Shared HDR grade pass for all Metal 3D renderers.
/// Compiles postprocess.metal once (lazy, thread-safe) then exposes Apply().
/// </summary>
internal static class MetalPostProcess
{
    private static MTLComputePipelineState _pso;
    private static MTLComputePipelineState _brightPso;
    private static MTLComputePipelineState _blurPso;
    private static bool _compiled;
    private static readonly object _initLock = new();

    private static void EnsureCompiled(MTLDevice device)
    {
        if (_compiled) return;
        lock (_initLock)
        {
            if (_compiled) return;
            var src = LoadEmbeddedMsl("postprocess.metal");
            NSError libErr = default;
            var lib = device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            NSError psoErr = default;
            _pso       = device.NewComputePipelineState(lib.NewFunction(NSString.String("postprocess")), ref psoErr);
            _brightPso = device.NewComputePipelineState(lib.NewFunction(NSString.String("bloom_brightpass")), ref psoErr);
            _blurPso   = device.NewComputePipelineState(lib.NewFunction(NSString.String("bloom_blur")), ref psoErr);
            _compiled = true;
        }
    }

    /// <summary>
    /// Apply the grade pass to <paramref name="hdrPixels"/> (float4 per pixel, stride 4)
    /// and return a packed RGBA8 uint[] ready for TexImage2D.
    /// </summary>
    internal static uint[] Apply(
        MTLDevice device,
        MTLCommandQueue queue,
        float[] hdrPixels,
        int width,
        int height,
        PostProcessParams? pp)
    {
        EnsureCompiled(device);

        var p = pp ?? new PostProcessParams();
        float gamma = MathF.Max(0.01f, p.Gamma);
        bool bloomOn = p.BloomEnabled && p.BloomIntensity > 0f;

        var gpuParams = new GpuPostProcessParams
        {
            ImageWidth  = width,
            ImageHeight = height,
            Brightness  = p.Brightness,
            Contrast    = p.Contrast,
            Gamma       = gamma,
            Saturation  = p.Saturation,
            HdrEnabled  = p.HdrEnabled ? 1 : 0,
            BloomIntensity = bloomOn ? p.BloomIntensity : 0f,
        };

        int pixelCount = width * height;
        var grid = new MTLSize { width = (ulong)((width + 7) / 8), height = (ulong)((height + 7) / 8), depth = 1 };
        var tg   = new MTLSize { width = 8, height = 8, depth = 1 };

        using var hdrBuf = MetalBufferIO.UploadFloats(device, hdrPixels);
        using var ppBuf  = UploadStruct(device, gpuParams);
        using var outBuf = MetalBufferIO.CreateSharedBuffer(device, (ulong)(pixelCount * sizeof(uint)));

        // Bloom buffers (only allocated when bloom is active). When disabled the grade
        // kernel binds the HDR buffer itself as the bloom input with intensity 0 → no-op.
        MTLBuffer brightBuf = default, tmpBuf = default;
        MTLBuffer bloomInput = hdrBuf;

        if (bloomOn)
        {
            // Radius scales with the smaller image dimension so bloom spread is
            // resolution-independent; sigma half the radius for a soft falloff.
            int minDim = Math.Max(1, Math.Min(width, height));
            int radius = Math.Clamp((int)(p.BloomRadius * minDim * 0.02f), 1, 40);
            var bloomParams = new GpuBloomParams
            {
                ImageWidth = width, ImageHeight = height,
                Threshold = p.BloomThreshold, DirX = 1f, DirY = 0f,
                Radius = radius, Sigma = MathF.Max(1f, radius * 0.5f), Pad0 = 0,
            };

            ulong floatBytes = (ulong)(pixelCount * 4 * sizeof(float));
            brightBuf = MetalBufferIO.CreateSharedBuffer(device, floatBytes);
            tmpBuf    = MetalBufferIO.CreateSharedBuffer(device, floatBytes);

            using var bpH = UploadStruct(device, bloomParams);
            var bloomParamsV = bloomParams; bloomParamsV.DirX = 0f; bloomParamsV.DirY = 1f;
            using var bpV = UploadStruct(device, bloomParamsV);

            var bcmd = queue.CommandBuffer();
            var benc = bcmd.ComputeCommandEncoder();
            // bright-pass: hdr → brightBuf
            benc.SetComputePipelineState(_brightPso);
            benc.SetBuffer(hdrBuf, 0, 0); benc.SetBuffer(bpH, 0, 1); benc.SetBuffer(brightBuf, 0, 2);
            benc.DispatchThreadgroups(grid, tg);
            // horizontal blur: brightBuf → tmpBuf
            benc.SetComputePipelineState(_blurPso);
            benc.SetBuffer(brightBuf, 0, 0); benc.SetBuffer(bpH, 0, 1); benc.SetBuffer(tmpBuf, 0, 2);
            benc.DispatchThreadgroups(grid, tg);
            // vertical blur: tmpBuf → brightBuf
            benc.SetBuffer(tmpBuf, 0, 0); benc.SetBuffer(bpV, 0, 1); benc.SetBuffer(brightBuf, 0, 2);
            benc.DispatchThreadgroups(grid, tg);
            benc.EndEncoding();
            bcmd.Commit();
            bcmd.WaitUntilCompleted();

            bloomInput = brightBuf;
        }

        var cmd = queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_pso);
        enc.SetBuffer(hdrBuf, 0, 0);
        enc.SetBuffer(ppBuf,  0, 1);
        enc.SetBuffer(outBuf, 0, 2);
        enc.SetBuffer(bloomInput, 0, 3);
        enc.DispatchThreadgroups(grid, tg);
        enc.EndEncoding();
        cmd.Commit();
        cmd.WaitUntilCompleted();

        var result = ReadUintBuffer(outBuf, pixelCount);
        if (bloomOn) { brightBuf.Dispose(); tmpBuf.Dispose(); }
        return result;
    }

    private static MTLBuffer UploadStruct<T>(MTLDevice device, T value) where T : struct
        => MetalBufferIO.UploadStruct(device, value);

    private static uint[] ReadUintBuffer(MTLBuffer buf, int count)
        => MetalBufferIO.ReadUIntBuffer(buf, count);

    private static string LoadEmbeddedMsl(string filename)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"Parsec.Rendering.Metal.Shaders.{filename}";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuPostProcessParams
    {
        public int   ImageWidth;
        public int   ImageHeight;
        public float Brightness;
        public float Contrast;
        public float Gamma;
        public float Saturation;
        public int   HdrEnabled;
        public float BloomIntensity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuBloomParams
    {
        public int   ImageWidth;
        public int   ImageHeight;
        public float Threshold;
        public float DirX;
        public float DirY;
        public int   Radius;
        public float Sigma;
        public int   Pad0;
    }
}
