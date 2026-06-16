using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Parsec.Core.Fluid;

/// <summary>
/// A lightweight "underwater" particle field that floats around a fractal. The fractal is supplied
/// as a distance-estimator delegate, so this is fractal-agnostic. Each particle's velocity is the
/// sum of four forces:
///   • surface repulsion  — pushed off the surface along the DE gradient (stays in the "water"),
///   • tangential swirl    — drifts around the shape (gradient × up),
///   • curl noise          — divergence-free 3-D noise for organic, incompressible flow,
///   • motion advection    — the rate of change of the DE field (∂DE/∂t) shoves particles, so a
///                           morphing/expanding fractal drives the current.
/// Pure CPU math; no rendering. Deterministic for a given seed.
/// </summary>
public sealed class FluidParticleField
{
    public struct Particle { public Vector3 Pos; public Vector3 Vel; public float Life; }

    public readonly Particle[] Particles;
    public float SpawnInner = 1.1f, SpawnOuter = 3.0f, KillRadius = 4.5f;
    public float RepelStrength = 1.3f, RepelBand = 0.7f;
    public float SwirlStrength = 0.30f;
    public float CurlStrength = 0.40f, CurlScale = 1.0f, CurlFlow = 0.12f;
    public float AdvectStrength = 7.0f;
    // Deep-sea: forces fall off sharply with distance from the surface, so particles far out are
    // nearly static (held by "pressure") while ones hugging the fractal move much more. CurlFloor is
    // the faint residual drift the far field keeps. High drag + low MaxSpeed = viscous, settled water.
    public float InfluenceDist = 0.40f, CurlFloor = 0.05f;
    // Fold "yank": a fold event briefly shoves the very nearest particles outward.
    public float YankStrength = 11f, YankDist = 0.30f;
    // Gentle constant sink (marine snow drifts downward under gravity); 0 for the main field.
    public float DownDrift = 0f;
    public float Drag = 0.86f, MaxSpeed = 1.3f, LifeSeconds = 14f;

    private readonly Random _rng;

    public FluidParticleField(int count, int seed = 12345)
    {
        _rng = new Random(seed);
        Particles = new Particle[count];
        for (int i = 0; i < count; i++) Respawn(ref Particles[i], initial: true);
    }

    private void Respawn(ref Particle p, bool initial)
    {
        // Uniform-ish point in a spherical shell around the origin.
        Vector3 dir = RandUnit();
        float r = MathF.Cbrt(Lerp(SpawnInner * SpawnInner * SpawnInner,
                                  SpawnOuter * SpawnOuter * SpawnOuter, (float)_rng.NextDouble()));
        p.Pos = dir * r;
        p.Vel = RandUnit() * 0.02f;
        p.Life = (initial ? (float)_rng.NextDouble() : 1f) * LifeSeconds;
    }

    /// <param name="de">distance estimator at the current time.</param>
    /// <param name="dePrev">distance estimator at the previous frame (for ∂DE/∂t advection).</param>
    /// <param name="foldImpulse">0–1 envelope; on a fold event the very nearest particles get yanked.</param>
    public void Step(float dt, Func<Vector3, float> de, Func<Vector3, float> dePrev, float time, float foldImpulse = 0f)
    {
        if (dt <= 0f) return;
        float eps = 0.012f;

        Parallel.For(0, Particles.Length, i =>
        {
            ref Particle p = ref Particles[i];
            Vector3 pos = p.Pos;

            float d = de(pos);
            // DE gradient via 4-tap tetrahedron (same trick the shaders use for normals).
            Vector2 k = new(1f, -1f);
            Vector3 g =
                new Vector3(k.X, k.Y, k.Y) * de(pos + new Vector3(k.X, k.Y, k.Y) * eps) +
                new Vector3(k.Y, k.Y, k.X) * de(pos + new Vector3(k.Y, k.Y, k.X) * eps) +
                new Vector3(k.Y, k.X, k.Y) * de(pos + new Vector3(k.Y, k.X, k.Y) * eps) +
                new Vector3(k.X, k.X, k.X) * de(pos + new Vector3(k.X, k.X, k.X) * eps);
            float gl = g.Length();
            Vector3 grad = gl > 1e-5f ? g / gl : RandUnitDet(i);

            // Proximity weight: 1 at the surface → ~0 far away. The fractal only stirs the water
            // near it; out in the deep the particles are held nearly static by "pressure".
            float prox = MathF.Exp(-MathF.Max(0f, d) / InfluenceDist);

            // 1. Surface repulsion — keep particles out of the solid, in the "water" band.
            float band = MathF.Max(0f, (RepelBand - d) / RepelBand);
            Vector3 repel = grad * (band * band) * RepelStrength;

            // 2. Tangential swirl — drift around the shape, near-field only.
            Vector3 swirl = Vector3.Cross(grad, Vector3.UnitY) * SwirlStrength * prox;

            // 3. Curl noise — organic incompressible flow; mostly near the fractal, faint floor far out.
            Vector3 curl = CurlNoise(pos * CurlScale + new Vector3(0f, time * CurlFlow, 0f))
                           * CurlStrength * (CurlFloor + (1f - CurlFloor) * prox);

            // 4. Motion advection — when the fractal morphs/expands (∂DE/∂t < 0) it shoves nearby
            //    water outward. Gated by proximity so only particles near the surface are driven.
            float ddt = (d - dePrev(pos)) / dt;
            Vector3 advect = grad * MathF.Max(0f, -ddt) * AdvectStrength * prox;

            // 5. Fold yank — a brief, sharp outward shove on the very nearest particles when the
            //    fractal folds. Tighter gate than the others, with a little tangential scatter.
            float proxYank = foldImpulse > 1e-3f ? MathF.Exp(-MathF.Max(0f, d) / YankDist) : 0f;
            Vector3 yank = (grad + Vector3.Cross(grad, Vector3.UnitX) * 0.4f)
                           * foldImpulse * YankStrength * proxYank;

            Vector3 acc = repel + swirl + curl + advect + yank;
            acc.Y -= DownDrift;   // marine-snow sink (0 for the main field)
            p.Vel = p.Vel * Drag + acc * dt;
            // Yanked particles may transiently exceed the resting max speed.
            float maxSp = MaxSpeed * (1f + 3f * foldImpulse * proxYank);
            float sp = p.Vel.Length();
            if (sp > maxSp) p.Vel *= maxSp / sp;
            p.Pos = pos + p.Vel * dt;
            p.Life -= dt;

            float rr = p.Pos.Length();
            bool bad = p.Life <= 0f || rr > KillRadius || d < -0.25f ||
                       float.IsNaN(rr) || float.IsInfinity(rr);
            if (bad) RespawnDet(ref p, i);
        });
    }

    // --- deterministic per-particle respawn (thread-safe; avoids sharing _rng across threads) ---
    private void RespawnDet(ref Particle p, int i)
    {
        uint s = (uint)(i * 2654435761u) ^ (uint)BitConverter.SingleToInt32Bits(p.Pos.X + p.Vel.Y);
        Vector3 dir = Vector3.Normalize(new Vector3(H(ref s) - 0.5f, H(ref s) - 0.5f, H(ref s) - 0.5f) + new Vector3(1e-4f));
        float r = MathF.Cbrt(Lerp(SpawnInner * SpawnInner * SpawnInner, SpawnOuter * SpawnOuter * SpawnOuter, H(ref s)));
        p.Pos = dir * r;
        p.Vel = Vector3.Zero;
        p.Life = (0.5f + 0.5f * H(ref s)) * LifeSeconds;
    }

    private Vector3 RandUnit()
    {
        Vector3 v;
        do { v = new Vector3((float)_rng.NextDouble() * 2 - 1, (float)_rng.NextDouble() * 2 - 1, (float)_rng.NextDouble() * 2 - 1); }
        while (v.LengthSquared() < 1e-4f || v.LengthSquared() > 1f);
        return Vector3.Normalize(v);
    }

    private static Vector3 RandUnitDet(int i)
    {
        uint s = (uint)(i * 747796405u + 2891336453u);
        return Vector3.Normalize(new Vector3(H(ref s) - 0.5f, H(ref s) - 0.5f, H(ref s) - 0.5f) + new Vector3(1e-4f));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float H(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / (float)0xFFFFFF; }

    // ---- curl of a 3-component value-noise potential (divergence-free) ----
    private static Vector3 CurlNoise(Vector3 p)
    {
        const float e = 0.18f;
        Vector3 dx = new(e, 0, 0), dy = new(0, e, 0), dz = new(0, 0, e);
        // Potential components are noise sampled at offset lattices.
        float p_x_y1 = Pot(p + dy, 0), p_x_y0 = Pot(p - dy, 0);
        float p_x_z1 = Pot(p + dz, 0), p_x_z0 = Pot(p - dz, 0);
        float p_y_x1 = Pot(p + dx, 1), p_y_x0 = Pot(p - dx, 1);
        float p_y_z1 = Pot(p + dz, 1), p_y_z0 = Pot(p - dz, 1);
        float p_z_x1 = Pot(p + dx, 2), p_z_x0 = Pot(p - dx, 2);
        float p_z_y1 = Pot(p + dy, 2), p_z_y0 = Pot(p - dy, 2);
        float cx = (p_z_y1 - p_z_y0) - (p_y_z1 - p_y_z0);
        float cy = (p_x_z1 - p_x_z0) - (p_z_x1 - p_z_x0);
        float cz = (p_y_x1 - p_y_x0) - (p_x_y1 - p_x_y0);
        return new Vector3(cx, cy, cz) / (2f * e);
    }

    private static float Pot(Vector3 p, int comp)
    {
        Vector3 o = comp switch { 0 => new(0f, 0f, 0f), 1 => new(31.4f, 17.0f, 9.2f), _ => new(-13.7f, 41.3f, -5.1f) };
        return Noise(p + o);
    }

    // Smooth value noise on an integer lattice, output ~[-1,1].
    private static float Noise(Vector3 p)
    {
        Vector3 i = new(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        Vector3 f = p - i;
        Vector3 u = f * f * (new Vector3(3f) - 2f * f);
        float n000 = Hash3(i + new Vector3(0, 0, 0)), n100 = Hash3(i + new Vector3(1, 0, 0));
        float n010 = Hash3(i + new Vector3(0, 1, 0)), n110 = Hash3(i + new Vector3(1, 1, 0));
        float n001 = Hash3(i + new Vector3(0, 0, 1)), n101 = Hash3(i + new Vector3(1, 0, 1));
        float n011 = Hash3(i + new Vector3(0, 1, 1)), n111 = Hash3(i + new Vector3(1, 1, 1));
        float nx00 = Lerp(n000, n100, u.X), nx10 = Lerp(n010, n110, u.X);
        float nx01 = Lerp(n001, n101, u.X), nx11 = Lerp(n011, n111, u.X);
        float nxy0 = Lerp(nx00, nx10, u.Y), nxy1 = Lerp(nx01, nx11, u.Y);
        return Lerp(nxy0, nxy1, u.Z) * 2f - 1f;
    }

    private static float Hash3(Vector3 p)
    {
        uint x = (uint)(int)MathF.Round(p.X), y = (uint)(int)MathF.Round(p.Y), z = (uint)(int)MathF.Round(p.Z);
        uint h = x * 374761393u + y * 668265263u + z * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }
}
