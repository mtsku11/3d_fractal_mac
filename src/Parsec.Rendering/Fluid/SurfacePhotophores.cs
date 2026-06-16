using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Parsec.Rendering.Fluid;

/// <summary>
/// Bioluminescent photophores that live ON the creature's surface (not in screen space), so they
/// ride the rippling/morphing skin and deform with it. Each is a 3-D point re-projected onto the
/// DE surface every frame (Newton steps along the gradient) with a slow tangential wander.
/// </summary>
public sealed class SurfacePhotophores
{
    public readonly Vector3[] Pos;
    public readonly float[] Phase;
    public readonly bool[] Magenta;
    public float DriftSpeed = 0.05f;

    public SurfacePhotophores(int count, int seed = 99)
    {
        var rng = new Random(seed);
        Pos = new Vector3[count];
        Phase = new float[count];
        Magenta = new bool[count];
        for (int i = 0; i < count; i++)
        {
            Pos[i] = RandUnit(rng) * 1.2f;
            Phase[i] = (float)rng.NextDouble() * 6.2832f;
            Magenta[i] = rng.Next(2) == 0;
        }
    }

    /// <summary>Re-project every photophore onto the current surface and let it wander slowly.</summary>
    public void Update(Func<Vector3, float> de, float dt, float time)
    {
        Parallel.For(0, Pos.Length, i =>
        {
            Vector3 p = Pos[i];
            // Project onto the surface: a few Newton steps along the (outward) gradient.
            for (int it = 0; it < 4; it++)
            {
                float d = de(p);
                Vector3 g = Grad(de, p);
                float gl = g.Length();
                if (gl < 1e-5f) break;
                p -= (g / gl) * d;
            }
            // Slow tangential wander across the skin (so they aren't perfectly fixed).
            Vector3 grad = Grad(de, p);
            float gl2 = grad.Length();
            if (gl2 > 1e-5f)
            {
                grad /= gl2;
                Vector3 t1 = Vector3.Cross(grad, Vector3.UnitY);
                if (t1.LengthSquared() < 1e-4f) t1 = Vector3.Cross(grad, Vector3.UnitX);
                t1 = Vector3.Normalize(t1);
                Vector3 t2 = Vector3.Cross(grad, t1);
                float a1 = MathF.Sin(time * 0.5f + Phase[i]);
                float a2 = MathF.Cos(time * 0.37f + Phase[i] * 1.3f);
                p += (t1 * a1 + t2 * a2) * DriftSpeed * dt;
                p -= grad * de(p);   // re-stick to the surface after wandering
            }
            float r = p.Length();
            if (float.IsNaN(r) || float.IsInfinity(r) || r > 3f || r < 0.3f)
                p = (r > 1e-3f ? Vector3.Normalize(Pos[i]) : Vector3.UnitX) * 1.2f;
            Pos[i] = p;
        });
    }

    private static Vector3 Grad(Func<Vector3, float> de, Vector3 p)
    {
        const float e = 0.01f;
        return new Vector3(
            de(p + new Vector3(e, 0, 0)) - de(p - new Vector3(e, 0, 0)),
            de(p + new Vector3(0, e, 0)) - de(p - new Vector3(0, e, 0)),
            de(p + new Vector3(0, 0, e)) - de(p - new Vector3(0, 0, e)));
    }

    private static Vector3 RandUnit(Random rng)
    {
        Vector3 v;
        do { v = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1); }
        while (v.LengthSquared() < 1e-3f || v.LengthSquared() > 1f);
        return Vector3.Normalize(v);
    }
}
