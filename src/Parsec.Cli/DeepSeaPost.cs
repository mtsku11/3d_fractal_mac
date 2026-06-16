using System;
using System.Threading.Tasks;

namespace Parsec.Cli;

/// <summary>
/// "Living creature" emissive layer applied to an already water-graded RGBA8 frame: a soft
/// subsurface bloom (gelatinous glow), a bioluminescent fresnel rim on the silhouette, and
/// sparse bioluminescent photophores on the brightest detail that pulse — brighter on fold
/// events. All additive so it glows through the water. uint layout RGBA (R = low byte).
/// </summary>
internal static class DeepSeaPost
{
    public static void ApplyEmissive(uint[] px, int w, int h, float time, float foldEnv, float excitement = 0f)
    {
        int n = w * h;

        // --- brightness mask of the body (after water grade) ---
        var bright = new float[n];
        Parallel.For(0, n, i =>
        {
            uint p = px[i];
            float r = (p & 0xFF) / 255f, g = ((p >> 8) & 0xFF) / 255f, b = ((p >> 16) & 0xFF) / 255f;
            float luma = 0.3f * r + 0.6f * g + 0.1f * b;
            bright[i] = Smoothstep(0.10f, 0.42f, luma);   // low thresholds: the body is dark now
        });

        // --- subsurface bloom: blur the brightness, add back tinted (gelatinous halo) ---
        var bloom = BoxBlur(bright, w, h, 7);
        bloom = BoxBlur(bloom, w, h, 7);
        Parallel.For(0, n, i =>
        {
            float bl = bloom[i] * 0.95f;            // gelatinous subsurface halo (eased so the dots read)
            if (bl < 0.002f) return;
            AddRgb(px, i, 0.18f * bl, 0.62f * bl, 0.70f * bl);
        });

        // --- bioluminescent fresnel rim on the silhouette (bold cyan edge) ---
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (bright[i] < 0.14f) continue;
                bool nearEdge = false;
                for (int s = 0; s < 8 && !nearEdge; s++)
                {
                    int nx = x + Off8X[s] * 4, ny = y + Off8Y[s] * 4;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) { nearEdge = true; break; }
                    if (bright[ny * w + nx] < 0.05f) nearEdge = true;
                }
                if (!nearEdge) continue;
                float rim = 1.35f * bright[i];
                AddRgb(px, i, 0.12f * rim, 0.85f * rim, 1.15f * rim);
            }
        });

        // NOTE: the bioluminescent photophores are NOT drawn here any more — they are 3-D points
        // anchored to the surface (see SurfacePhotophores / FluidCompositor.DrawSurfacePhotophores)
        // so they ride the rippling/morphing skin instead of being pasted on in screen space.
    }

    private static readonly int[] Off8X = { 1, -1, 0, 0, 1, -1, 1, -1 };
    private static readonly int[] Off8Y = { 0, 0, 1, -1, 1, -1, -1, 1 };

    private static void AddRgb(uint[] px, int i, float r, float g, float b)
    {
        uint q = px[i];
        int nr = Math.Min(255, (int)(q & 0xFF) + (int)(r * 255f));
        int ng = Math.Min(255, (int)((q >> 8) & 0xFF) + (int)(g * 255f));
        int nb = Math.Min(255, (int)((q >> 16) & 0xFF) + (int)(b * 255f));
        px[i] = (255u << 24) | ((uint)nb << 16) | ((uint)ng << 8) | (uint)nr;
    }

    // Separable box blur on a float buffer (two-pass via running sum).
    private static float[] BoxBlur(float[] src, int w, int h, int radius)
    {
        var tmp = new float[w * h];
        var dst = new float[w * h];
        float norm = 1f / (2 * radius + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float acc = 0;
            for (int x = -radius; x <= radius; x++) acc += src[row + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = acc * norm;
                int add = Math.Clamp(x + radius + 1, 0, w - 1);
                int sub = Math.Clamp(x - radius, 0, w - 1);
                acc += src[row + add] - src[row + sub];
            }
        });
        Parallel.For(0, w, x =>
        {
            float acc = 0;
            for (int y = -radius; y <= radius; y++) acc += tmp[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = acc * norm;
                int add = Math.Clamp(y + radius + 1, 0, h - 1);
                int sub = Math.Clamp(y - radius, 0, h - 1);
                acc += tmp[add * w + x] - tmp[sub * w + x];
            }
        });
        return dst;
    }

    private static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
