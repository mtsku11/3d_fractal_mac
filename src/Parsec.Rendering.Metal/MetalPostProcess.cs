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
            var fn = lib.NewFunction(NSString.String("postprocess"));
            NSError psoErr = default;
            _pso = device.NewComputePipelineState(fn, ref psoErr);
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

        var gpuParams = new GpuPostProcessParams
        {
            ImageWidth  = width,
            ImageHeight = height,
            Brightness  = p.Brightness,
            Contrast    = p.Contrast,
            Gamma       = gamma,
            Saturation  = p.Saturation,
            HdrEnabled  = p.HdrEnabled ? 1 : 0,
            Pad0        = 0,
        };

        int pixelCount = width * height;

        using var hdrBuf = device.NewBuffer(
            (ulong)(hdrPixels.Length * sizeof(float)),
            MTLResourceOptions.ResourceStorageModeShared);
        unsafe
        {
            fixed (float* src = hdrPixels)
                Buffer.MemoryCopy(src, (void*)hdrBuf.Contents,
                    (long)hdrPixels.Length * sizeof(float),
                    (long)hdrPixels.Length * sizeof(float));
        }

        using var ppBuf  = UploadStruct(device, gpuParams);
        using var outBuf = device.NewBuffer(
            (ulong)(pixelCount * sizeof(uint)),
            MTLResourceOptions.ResourceStorageModeShared);

        var cmd = queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_pso);
        enc.SetBuffer(hdrBuf, 0, 0);
        enc.SetBuffer(ppBuf,  0, 1);
        enc.SetBuffer(outBuf, 0, 2);
        enc.DispatchThreadgroups(
            new MTLSize { width = (ulong)((width + 7) / 8), height = (ulong)((height + 7) / 8), depth = 1 },
            new MTLSize { width = 8, height = 8, depth = 1 });
        enc.EndEncoding();
        cmd.Commit();
        cmd.WaitUntilCompleted();

        return ReadUintBuffer(outBuf, pixelCount);
    }

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
            Buffer.MemoryCopy((void*)buf.Contents, dst,
                (long)count * sizeof(uint), (long)count * sizeof(uint));
        return result;
    }

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
        public int   Pad0;
    }
}
