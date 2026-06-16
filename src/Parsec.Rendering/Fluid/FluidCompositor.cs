using System.Numerics;
using System.Threading.Tasks;
using Parsec.Core.Fluid;
using Parsec.Rendering.Raymarching;

namespace Parsec.Rendering.Fluid;

/// <summary>
/// Composites the "underwater" look onto a rendered RGBA8 frame: a teal water grade with caustic
/// shimmer + vignette, then additive glowing particles projected from the <see cref="FluidParticleField"/>.
/// uint layout is RGBA little-endian (R = low byte), matching the Metal renderers.
/// </summary>
public static class FluidCompositor
{
    public static void Apply(uint[] px, int w, int h, FluidParticleField field, Camera3D cam, float fovY, float time,
                             Func<Vector3, float>? de = null, bool deepSea = false, float foldEnv = 0f,
                             float excitement = 0f, SurfacePhotophores? photophores = null,
                             DeepSeaParams? dsp = null)
    {
        var p_ = dsp ?? DeepSeaParams.Default;
        float gradeB = p_.GradeBrightness;
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
                r *= vig * gradeB; g *= vig * gradeB; b *= vig * gradeB;   // Murk darkens the water

                px[idx] = Pack(r, g, b);
            }
        });

        // 1b. "Living creature" emissive layer (subsurface bloom + bioluminescent rim/photophores),
        //     applied over the water-graded body but under the particles so it glows through.
        if (deepSea) DeepSeaPost.ApplyEmissive(px, w, h, time, foldEnv, excitement, p_.Bloom, p_.Rim);

        // Surface-anchored bioluminescent photophores (3-D, occluded, riding the skin).
        if (deepSea && photophores != null && de != null)
            DrawSurfacePhotophores(px, w, h, cam, fovY, de, photophores, time, excitement, foldEnv, p_.PhotophoreBrightness);

        // 2. Additive particle splats (single-threaded — splats overlap in the buffer).
        var posCam = cam.Position;
        var fwd = Vector3.Normalize(cam.LookAt - cam.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, cam.Up));
        var upL = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(fovY * 0.5f);
        float tanX = tanY * cam.AspectRatio;
        float lifeSec = field.LifeSeconds;
        var parts = field.Particles;

        // Occlusion: sphere-trace the DE from camera toward each particle; if it hits the surface
        // before reaching the particle, the mote is behind the fractal and is hidden. Computed in
        // parallel (read-only) so the additive splat below can stay single-threaded.
        bool[]? occluded = null;
        if (de != null)
        {
            occluded = new bool[parts.Length];
            Parallel.For(0, parts.Length, i =>
            {
                Vector3 toP = parts[i].Pos - posCam;
                float dist = toP.Length();
                if (dist < 1e-3f) { occluded[i] = true; return; }
                Vector3 dir = toP / dist;
                float tt = 0.04f;
                for (int s = 0; s < 32 && tt < dist - 0.05f; s++)
                {
                    float dd = de(posCam + dir * tt);
                    if (dd < 1.8e-3f) { occluded[i] = true; return; }
                    tt += MathF.Max(dd, 1.2e-3f);
                }
            });
        }

        for (int pi = 0; pi < parts.Length; pi++)
        {
            if (occluded != null && occluded[pi]) continue;
            ref readonly var part = ref parts[pi];
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

    // 3-D photophores anchored to the surface: project, occlusion-test against the DE, draw a
    // pulsing glow disc. They ride the deforming skin and hide on the creature's far side.
    private static void DrawSurfacePhotophores(uint[] px, int w, int h, Camera3D cam, float fovY,
        Func<Vector3, float> de, SurfacePhotophores ph, float time, float excitement, float foldEnv, float glow)
    {
        var posCam = cam.Position;
        var fwd = Vector3.Normalize(cam.LookAt - cam.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, cam.Up));
        var upL = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(fovY * 0.5f);
        float tanX = tanY * cam.AspectRatio;
        float foldBoost = 1f + 3.0f * foldEnv;
        float pulseRate = 2.2f * (1f + 1.4f * excitement);

        for (int i = 0; i < ph.Pos.Length; i++)
        {
            Vector3 a = ph.Pos[i];
            Vector3 v = a - posCam;
            float zc = Vector3.Dot(v, fwd);
            if (zc <= 0.08f) continue;
            float sx = Vector3.Dot(v, right) / (zc * tanX);
            float sy = Vector3.Dot(v, upL) / (zc * tanY);
            if (MathF.Abs(sx) > 1.05f || MathF.Abs(sy) > 1.05f) continue;

            // Occlusion: march camera→anchor; if it hits the surface before (almost) reaching the
            // anchor, this photophore is on the creature's far side → hidden.
            float dist = v.Length();
            Vector3 dir = v / dist;
            bool occ = false;
            float tt = 0.04f;
            for (int s = 0; s < 40 && tt < dist - 0.06f; s++)
            {
                float dd = de(posCam + dir * tt);
                if (dd < 1.8e-3f) { occ = true; break; }
                tt += MathF.Max(dd, 1.2e-3f);
            }
            if (occ) continue;

            float fpx = (sx * 0.5f + 0.5f) * w;
            float fpy = (0.5f - sy * 0.5f) * h;
            // Embed in the shading: scale by the surface brightness at this spot, so the node
            // brightens on the lit side and fades on the shadowed/grazing side (a light IN the skin,
            // not over it). Where the surface is dark/void, it's killed.
            int cx = Math.Clamp((int)fpx, 0, w - 1), cy = Math.Clamp((int)fpy, 0, h - 1);
            uint cpix = px[cy * w + cx];
            float clum = (0.3f * (cpix & 0xFF) + 0.6f * ((cpix >> 8) & 0xFF) + 0.1f * ((cpix >> 16) & 0xFF)) / 255f;
            float surf = Clamp01((clum - 0.06f) / 0.34f); surf *= surf * (3f - 2f * surf);
            if (surf < 0.05f) continue;
            float fog = MathF.Exp(-zc * 0.2f);
            float pulse = 0.4f + 0.6f * MathF.Sin(time * pulseRate + ph.Phase[i]);
            float inten = pulse * foldBoost * 1.5f * fog * surf * glow;   // embedded, not floating
            bool magenta = ph.Magenta[i];
            float cr = magenta ? 1.15f * inten : 0.30f * inten;
            float cg = magenta ? 0.30f * inten : 1.10f * inten;
            float cb = magenta ? 1.15f * inten : 1.30f * inten;

            int rad = 4;
            int x0 = Math.Max(0, (int)(fpx - rad)), x1 = Math.Min(w - 1, (int)(fpx + rad));
            int y0 = Math.Max(0, (int)(fpy - rad)), y1 = Math.Min(h - 1, (int)(fpy + rad));
            float r2 = rad * rad;
            for (int yy = y0; yy <= y1; yy++)
            for (int xx = x0; xx <= x1; xx++)
            {
                float dx = xx - fpx, dy = yy - fpy;
                float dd = dx * dx + dy * dy;
                if (dd > r2) continue;
                float fall = 1f - MathF.Sqrt(dd) / rad; fall *= fall;
                int idx = yy * w + xx;
                uint q = px[idx];
                int nr = Math.Min(255, (int)(q & 0xFF) + (int)(cr * fall * 255f));
                int ng = Math.Min(255, (int)((q >> 8) & 0xFF) + (int)(cg * fall * 255f));
                int nb = Math.Min(255, (int)((q >> 16) & 0xFF) + (int)(cb * fall * 255f));
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
