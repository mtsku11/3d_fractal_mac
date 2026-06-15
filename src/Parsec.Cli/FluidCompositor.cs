using System.Numerics;
using System.Threading.Tasks;
using Parsec.Core.Fluid;
using Parsec.Rendering.Raymarching;

namespace Parsec.Cli;

/// <summary>
/// Composites the "underwater" look onto a rendered RGBA8 frame: a teal water grade with caustic
/// shimmer + vignette, then additive glowing particles projected from the <see cref="FluidParticleField"/>.
/// uint layout is RGBA little-endian (R = low byte), matching the Metal renderers.
/// </summary>
internal static class FluidCompositor
{
    public static void Apply(uint[] px, int w, int h, FluidParticleField field, Camera3D cam, float fovY, float time)
    {
        // 1. Underwater grade (parallel over rows).
        Parallel.For(0, h, y =>
        {
            float ny = (y / (h - 1f)) * 2f - 1f;
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                uint p = px[idx];
                float r = (p & 0xFF) / 255f, g = ((p >> 8) & 0xFF) / 255f, b = ((p >> 16) & 0xFF) / 255f;

                // Water absorbs red; bias toward teal.
                r *= 0.70f; g *= 0.90f; b *= 1.0f;
                // Caustic shimmer — slow crossing sine ripples.
                float cx = x * 0.016f, cy = y * 0.016f;
                float caustic = 0.90f + 0.12f * (MathF.Sin(cx + time * 0.9f) * MathF.Sin(cy * 1.3f - time * 0.6f)
                                               + 0.5f * MathF.Sin((cx + cy) * 0.7f + time * 1.3f));
                r *= caustic; g *= caustic; b *= caustic;
                // Ambient water glow lifts the blacks toward deep teal.
                r += 0.012f; g += 0.05f; b += 0.085f;
                // Vignette.
                float nx = (x / (w - 1f)) * 2f - 1f;
                float vig = 1f - 0.40f * (nx * nx + ny * ny);
                if (vig < 0f) vig = 0f;
                r *= vig; g *= vig; b *= vig;

                px[idx] = Pack(r, g, b);
            }
        });

        // 2. Additive particle splats (single-threaded — splats overlap in the buffer).
        var posCam = cam.Position;
        var fwd = Vector3.Normalize(cam.LookAt - cam.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, cam.Up));
        var upL = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(fovY * 0.5f);
        float tanX = tanY * cam.AspectRatio;
        float lifeSec = field.LifeSeconds;

        foreach (ref readonly var part in field.Particles.AsSpan())
        {
            Vector3 v = part.Pos - posCam;
            float zc = Vector3.Dot(v, fwd);
            if (zc <= 0.08f) continue;
            float sx = Vector3.Dot(v, right) / (zc * tanX);
            float sy = Vector3.Dot(v, upL) / (zc * tanY);
            if (MathF.Abs(sx) > 1.05f || MathF.Abs(sy) > 1.05f) continue;

            float fpx = (sx * 0.5f + 0.5f) * w;
            float fpy = (0.5f - sy * 0.5f) * h;
            float fog = MathF.Exp(-zc * 0.42f);                 // depth haze
            float speed = part.Vel.Length();
            float fadeIn = Clamp01((lifeSec - part.Life) / 0.5f);
            float fadeOut = Clamp01(part.Life / 0.5f);
            float env = MathF.Min(fadeIn, fadeOut);
            float intensity = fog * (0.55f + 0.55f * Clamp01(speed / 2.2f)) * env * 1.05f;
            if (intensity < 0.004f) continue;
            float rad = 1.5f + 3.8f * fog;

            // Flow-tinted cyan-white; a touch warmer for fast (energetic) motes.
            float cr = (0.6f + 0.3f * Clamp01(speed / 3f)) * intensity;
            float cg = 1.0f * intensity;
            float cb = 1.2f * intensity;

            int x0 = Math.Max(0, (int)(fpx - rad)), x1 = Math.Min(w - 1, (int)(fpx + rad));
            int y0 = Math.Max(0, (int)(fpy - rad)), y1 = Math.Min(h - 1, (int)(fpy + rad));
            float r2 = rad * rad;
            for (int yy = y0; yy <= y1; yy++)
            for (int xx = x0; xx <= x1; xx++)
            {
                float dx = xx - fpx, dy = yy - fpy;
                float dd = dx * dx + dy * dy;
                if (dd > r2) continue;
                float wgt = 1f - MathF.Sqrt(dd) / rad; wgt *= wgt;
                int idx = yy * w + xx;
                uint q = px[idx];
                int nr = Math.Min(255, (int)(q & 0xFF) + (int)(cr * wgt * 255f));
                int ng = Math.Min(255, (int)((q >> 8) & 0xFF) + (int)(cg * wgt * 255f));
                int nb = Math.Min(255, (int)((q >> 16) & 0xFF) + (int)(cb * wgt * 255f));
                px[idx] = (255u << 24) | ((uint)nb << 16) | ((uint)ng << 8) | (uint)nr;
            }
        }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static uint Pack(float r, float g, float b)
    {
        uint ir = (uint)Math.Clamp((int)(r * 255f + 0.5f), 0, 255);
        uint ig = (uint)Math.Clamp((int)(g * 255f + 0.5f), 0, 255);
        uint ib = (uint)Math.Clamp((int)(b * 255f + 0.5f), 0, 255);
        return (255u << 24) | (ib << 16) | (ig << 8) | ir;
    }
}
