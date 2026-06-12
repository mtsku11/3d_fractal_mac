using System.Runtime.InteropServices;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

internal static class MetalBufferIO
{
    public static MTLBuffer CreateSharedBuffer(MTLDevice device, ulong length)
    {
        var buffer = device.NewBuffer(length, MTLResourceOptions.ResourceStorageModeShared);
        PrepareCpuAccess(buffer);
        return buffer;
    }

    public static unsafe MTLBuffer UploadStruct<T>(MTLDevice device, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr scratch = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, scratch, false);
            var buffer = device.NewBuffer(scratch, (ulong)size, MTLResourceOptions.ResourceStorageModeShared);
            PrepareCpuAccess(buffer);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(scratch);
        }
    }

    public static unsafe MTLBuffer UploadFloats(MTLDevice device, float[] values)
    {
        fixed (float* ptr = values)
        {
            var buffer = device.NewBuffer((IntPtr)ptr, (ulong)(values.Length * sizeof(float)), MTLResourceOptions.ResourceStorageModeShared);
            PrepareCpuAccess(buffer);
            return buffer;
        }
    }

    public static unsafe MTLBuffer UploadUInts(MTLDevice device, uint[] values)
    {
        fixed (uint* ptr = values)
        {
            var buffer = device.NewBuffer((IntPtr)ptr, (ulong)(values.Length * sizeof(uint)), MTLResourceOptions.ResourceStorageModeShared);
            PrepareCpuAccess(buffer);
            return buffer;
        }
    }

    public static unsafe void WriteFloats(MTLBuffer buffer, float[] values)
    {
        IntPtr ptr = RequireContents(buffer, "float upload");
        fixed (float* src = values)
        {
            Buffer.MemoryCopy(src, (void*)ptr, (long)values.Length * sizeof(float), (long)values.Length * sizeof(float));
        }
    }

    public static unsafe float[] ReadFloat4Buffer(MTLBuffer buffer, int pixelCount)
    {
        var result = new float[pixelCount * 4];
        IntPtr ptr = RequireContents(buffer, "float4 readback");
        fixed (float* dst = result)
        {
            Buffer.MemoryCopy((void*)ptr, dst, (long)result.Length * sizeof(float), (long)result.Length * sizeof(float));
        }
        return result;
    }

    public static unsafe uint[] ReadUIntBuffer(MTLBuffer buffer, int count)
    {
        var result = new uint[count];
        IntPtr ptr = RequireContents(buffer, "uint readback");
        fixed (uint* dst = result)
        {
            Buffer.MemoryCopy((void*)ptr, dst, (long)count * sizeof(uint), (long)count * sizeof(uint));
        }
        return result;
    }

    public static IntPtr RequireContents(MTLBuffer buffer, string operation)
    {
        PrepareCpuAccess(buffer);
        IntPtr ptr = buffer.Contents;
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Metal shared buffer has no CPU-accessible contents for {operation}. " +
                "This host is exposing a non-mappable SharpMetal buffer, so render verification cannot complete here.");
        }
        return ptr;
    }

    private static void PrepareCpuAccess(MTLBuffer buffer)
    {
        _ = buffer;
    }
}
