using System.Numerics;
using Parsec.Rendering;

namespace Parsec.Rendering.Gpu;

public static class GpuSurfaceTextureManager
{
    private sealed record TextureSource(byte[] Bytes, int Width, int Height, int RowBytes);

    public readonly record struct TextureSnapshot(
        byte[] Bytes,
        int Width,
        int Height,
        int RowBytes,
        int Version);

    private static readonly object Gate = new();
    private static TextureSource? _source;
    private static int _version;
    private static bool _enabled;
    private static float _blend = 0.7f;
    private static float _scale = 1.25f;

    public static void SetImage(byte[] bytes, int width, int height, int rowBytes)
    {
        lock (Gate)
        {
            _source = new TextureSource(bytes, width, height, rowBytes);
            _version++;
        }
    }

    public static void ClearImage()
    {
        lock (Gate)
        {
            _source = null;
            _version++;
        }
    }

    public static void SetControls(bool enabled, float blend, float scale)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _blend = Math.Clamp(blend, 0f, 1f);
            _scale = Math.Clamp(scale, 0.05f, 16f);
        }
    }

    public static Vector4 EncodeBackground(Color background)
    {
        lock (Gate)
            return new Vector4(background.R, background.G, background.B, IsActiveLocked() ? 1f : 0f);
    }

    public static Vector4 EncodeSurface(Color surface)
    {
        lock (Gate)
            return new Vector4(surface.R, surface.G, surface.B, IsActiveLocked() ? _blend : 0f);
    }

    public static Vector4 EncodeMarchB(float aoStepDistance, float aoIntensity)
    {
        lock (Gate)
        {
            float aspect = _source is { Height: > 0 } src ? src.Width / (float)src.Height : 1f;
            return new Vector4(aoStepDistance, aoIntensity, IsActiveLocked() ? _scale : 1f, aspect);
        }
    }

    public static TextureSnapshot GetSnapshot()
    {
        lock (Gate)
        {
            if (_source is { Width: > 0, Height: > 0, RowBytes: > 0 } src)
                return new TextureSnapshot(src.Bytes, src.Width, src.Height, src.RowBytes, _version);
            return new TextureSnapshot([255, 255, 255, 255], 1, 1, 4, _version);
        }
    }

    private static bool IsActiveLocked() => _enabled && _source is not null;
}
