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
    public static void ApplyEmissive(uint[] px, int w, int h, float time, float foldEnv)
    {
        int n = w * h;

        // --- brightness mask of the body (after water grade) ---
        var bright = new float[n];
        Parallel.For(0, n, i =>
        {
            uint p = px[i];
            float r = (p & 0xFF) / 255f, g = ((p >> 8) & 0xFF) / 255f, b = ((p >> 16) & 0xFF) / 255f;
            float luma = 0.3f * r + 0.6f * g + 0.1f * b;
            bright[i] = Smoothstep(0.32f, 0.7f, luma);
        });

        // --- subsurface bloom: blur the brightness, add back tinted (gelatinous halo) ---
        var bloom = BoxBlur(bright, w, h, 7);
        bloom = BoxBlur(bloom, w, h, 7);
        Parallel.For(0, n, i =>
        {
            float bl = bloom[i] * 1.05f;            // gelatinous subsurface halo
            if (bl < 0.002f) return;
            AddRgb(px, i, 0.18f * bl, 0.62f * bl, 0.68f * bl);
        });

        // --- bioluminescent fresnel rim on the silhouette (bold cyan edge) ---
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (bright[i] < 0.22f) continue;
                bool nearEdge = false;
                for (int s = 0; s < 8 && !nearEdge; s++)
                {
                    int nx = x + Off8X[s] * 4, ny = y + Off8Y[s] * 4;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) { nearEdge = true; break; }
                    if (bright[ny * w + nx] < 0.07f) nearEdge = true;
                }
                if (!nearEdge) continue;
                float rim = 1.05f * bright[i];
                AddRgb(px, i, 0.12f * rim, 0.85f * rim, 1.15f * rim);
            }
        });

        // --- bioluminescent photophores: pulsing glow discs on mid-tone "skin" (so they read as
        //     lights emerging from the darker body, not lost in the blown-out highlights). ---
        float foldBoost = 1f + 3.5f * foldEnv;
        const int cellPx = 7;
        for (int y = 0; y < h; y += cellPx)
        {
            for (int x = 0; x < w; x += cellPx)
            {
                int ci = y * w + x;
                // Mid-tone band: on the body but not the brightest tips → high contrast for the dot.
                if (bright[ci] < 0.12f || bright[ci] > 0.55f) continue;
                uint cell = Hash2((uint)(x / cellPx), (uint)(y / cellPx));
                if ((cell & 7u) != 0u) continue;          // ~1 in 8 eligible cells
                float ph = (cell & 0xFFFF) / 65535f * 6.2832f;
                float pulse = 0.35f + 0.65f * MathF.Sin(time * 2.2f + ph);
                float inten = pulse * foldBoost * 2.0f * (0.4f + 0.6f * bright[ci]);
                bool magenta = (cell & 64u) != 0u;
                float cr = magenta ? 1.1f * inten : 0.25f * inten;
                float cg = magenta ? 0.30f * inten : 1.1f * inten;
                float cb = magenta ? 1.1f * inten : 1.25f * inten;
                int rad = 4;
                for (int dy = -rad; dy <= rad; dy++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    float dd = dx * dx + dy * dy; if (dd > rad * rad) continue;
                    float fall = 1f - MathF.Sqrt(dd) / rad; fall *= fall;
                    AddRgb(px, yy * w + xx, cr * fall, cg * fall, cb * fall);
                }
            }
        }
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

    private static uint Hash2(uint x, uint y)
    {
        uint h = x * 374761393u + y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
}
