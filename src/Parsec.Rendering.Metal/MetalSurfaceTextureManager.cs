using System.Numerics;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

public static class MetalSurfaceTextureManager
{
    private sealed record TextureSource(byte[] Bytes, int Width, int Height, int RowBytes);

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, MTLTexture> TextureCache = new();
    private static TextureSource? _source;
    private static bool _enabled;
    private static float _blend = 0.7f;
    private static float _scale = 1.25f;
    private static int _mode = 0;  // 0 = triplanar, 1 = orbit trap

    public static void SetImage(byte[] bytes, int width, int height, int rowBytes)
    {
        lock (Gate)
        {
            _source = new TextureSource(bytes, width, height, rowBytes);
            DisposeCache();
        }
    }

    public static void ClearImage()
    {
        lock (Gate)
        {
            _source = null;
            DisposeCache();
        }
    }

    public static void SetControls(bool enabled, float blend, float scale, int mode = 0)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _blend = Math.Clamp(blend, 0f, 1f);
            _scale = Math.Clamp(scale, 0.05f, 16f);
            _mode = mode;
        }
    }

    public static Vector4 EncodeBackground(Color background)
    {
        lock (Gate)
        {
            // 0 = disabled, 1 = triplanar, 2 = orbit trap
            float modeFlag = IsActiveLocked() ? (_mode == 1 ? 2f : 1f) : 0f;
            return new Vector4(background.R, background.G, background.B, modeFlag);
        }
    }

    public static Vector4 EncodeSurface(Color surface)
    {
        lock (Gate)
        {
            return new Vector4(surface.R, surface.G, surface.B, IsActiveLocked() ? _blend : 0f);
        }
    }

    public static Vector4 EncodeMarchB(float aoStepDistance, float aoIntensity)
    {
        lock (Gate)
        {
            float aspect = _source is { Height: > 0 } src ? src.Width / (float)src.Height : 1f;
            return new Vector4(aoStepDistance, aoIntensity, IsActiveLocked() ? _scale : 1f, aspect);
        }
    }

    /// Updates pixel data in already-cached textures in place (no reallocation).
    /// Falls back to full SetImage reset if dimensions differ from the existing source.
    /// Use for video textures: call SetImage once for the first frame, then UpdateImage per frame.
    public static unsafe void UpdateImage(byte[] bytes, int width, int height, int rowBytes)
    {
        lock (Gate)
        {
            if (_source is null || _source.Width != width || _source.Height != height)
            {
                _source = new TextureSource(bytes, width, height, rowBytes);
                DisposeCache();
                return;
            }
            _source = new TextureSource(bytes, width, height, rowBytes);
            var region = new MTLRegion
            {
                origin = new MTLOrigin { x = 0, y = 0, z = 0 },
                size   = new MTLSize   { width = (ulong)width, height = (ulong)height, depth = 1 }
            };
            fixed (byte* ptr = bytes)
                foreach (var tex in TextureCache.Values)
                    tex.ReplaceRegion(region, 0, (IntPtr)ptr, (ulong)rowBytes);
        }
    }

    public static MTLTexture GetTexture(MTLDevice device)
    {
        lock (Gate)
        {
            ulong key = device.RegistryID;
            if (TextureCache.TryGetValue(key, out var existing))
                return existing;

            var texture = CreateTexture(device, _source);
            TextureCache[key] = texture;
            return texture;
        }
    }

    private static bool IsActiveLocked() => _enabled && _source is not null;

    private static unsafe MTLTexture CreateTexture(MTLDevice device, TextureSource? source)
    {
        bool hasSource = source is { Width: > 0, Height: > 0, RowBytes: > 0, Bytes.Length: > 0 };
        int width = hasSource ? source!.Width : 1;
        int height = hasSource ? source!.Height : 1;
        int rowBytes = hasSource ? source!.RowBytes : 4;
        byte[] bytes = hasSource ? source!.Bytes : [255, 255, 255, 255];

        var desc = MTLTextureDescriptor.Texture2DDescriptor(MTLPixelFormat.RGBA8Unorm, (ulong)width, (ulong)height, false);
        desc.StorageMode = MTLStorageMode.Shared;
        desc.Usage = MTLTextureUsage.ShaderRead;

        var texture = device.NewTexture(desc);
        var region = new MTLRegion
        {
            origin = new MTLOrigin { x = 0, y = 0, z = 0 },
            size = new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 }
        };

        fixed (byte* ptr = bytes)
            texture.ReplaceRegion(region, 0, (IntPtr)ptr, (ulong)rowBytes);

        return texture;
    }

    private static void DisposeCache()
    {
        foreach (var texture in TextureCache.Values)
            texture.Dispose();
        TextureCache.Clear();
    }
}
