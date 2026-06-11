using SkiaSharp;

namespace Parsec.Rendering.Output;

public static class ImageOutput
{
    /// <summary>
    /// Encodes <paramref name="bitmap"/> as PNG and writes to <paramref name="path"/>.
    /// Creates the directory if needed.
    /// </summary>
    public static void SavePng(SKBitmap bitmap, string path, bool transparentBackground = false)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (transparentBackground)
            KeyBackgroundToTransparent(bitmap);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    private static void KeyBackgroundToTransparent(SKBitmap bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return;
        if (bitmap.ColorType != SKColorType.Rgba8888) return;

        var bg = EstimateBackground(bitmap);
        int byteCount = bitmap.ByteCount;
        var bytes = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, byteCount);

        const int clearTol = 3;
        const int solidTol = 42;

        for (int i = 0; i + 3 < bytes.Length; i += 4)
        {
            int r = bytes[i + 0];
            int g = bytes[i + 1];
            int b = bytes[i + 2];
            int d = Math.Max(Math.Abs(r - bg.Red), Math.Max(Math.Abs(g - bg.Green), Math.Abs(b - bg.Blue)));

            int a = d <= clearTol ? 0 :
                    d >= solidTol ? 255 :
                    (d - clearTol) * 255 / (solidTol - clearTol);

            if (a == 0)
            {
                bytes[i + 0] = bytes[i + 1] = bytes[i + 2] = bytes[i + 3] = 0;
                continue;
            }

            if (a < 255)
            {
                float af = a / 255f;
                bytes[i + 0] = (byte)Math.Clamp((int)MathF.Round(r - bg.Red   * (1f - af)), 0, 255);
                bytes[i + 1] = (byte)Math.Clamp((int)MathF.Round(g - bg.Green * (1f - af)), 0, 255);
                bytes[i + 2] = (byte)Math.Clamp((int)MathF.Round(b - bg.Blue  * (1f - af)), 0, 255);
            }
            bytes[i + 3] = (byte)a;
        }

        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), byteCount);
    }

    private static SKColor EstimateBackground(SKBitmap bitmap)
    {
        var corners = new[]
        {
            bitmap.GetPixel(0, 0),
            bitmap.GetPixel(bitmap.Width - 1, 0),
            bitmap.GetPixel(0, bitmap.Height - 1),
            bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1),
        };

        int best = 0;
        int bestScore = int.MaxValue;
        for (int i = 0; i < corners.Length; i++)
        {
            int score = 0;
            for (int j = 0; j < corners.Length; j++)
            {
                score += Math.Abs(corners[i].Red - corners[j].Red);
                score += Math.Abs(corners[i].Green - corners[j].Green);
                score += Math.Abs(corners[i].Blue - corners[j].Blue);
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return corners[best];
    }
}
