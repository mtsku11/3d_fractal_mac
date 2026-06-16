using System;
using System.Numerics;

namespace Parsec.Rendering.Fluid;

/// <summary>
/// Lightweight CPU distance estimators used by the deep-sea particle/photophore sim and occlusion
/// (the GPU raymarch can't be queried per-point on the CPU). Approximates the rendered shape closely
/// enough for the fluid layer; not a pixel-exact match.
/// </summary>
public static class CpuDistanceEstimators
{
    /// <summary>Canonical Mandelbulb distance estimate.</summary>
    public static float Mandelbulb(Vector3 pos, float power, int iterations = 8)
    {
        var z = pos;
        float dr = 1f, r = 0f;
        for (int it = 0; it < iterations; it++)
        {
            r = z.Length();
            if (r > 2f) break;
            float theta = MathF.Acos(Math.Clamp(z.Z / MathF.Max(r, 1e-9f), -1f, 1f));
            float phi = MathF.Atan2(z.Y, z.X);
            dr = MathF.Pow(r, power - 1f) * power * dr + 1f;
            float zr = MathF.Pow(r, power);
            theta *= power; phi *= power;
            z = zr * new Vector3(MathF.Sin(theta) * MathF.Cos(phi),
                                 MathF.Sin(theta) * MathF.Sin(phi),
                                 MathF.Cos(theta)) + pos;
        }
        return 0.5f * MathF.Log(MathF.Max(r, 1e-9f)) * r / MathF.Max(dr, 1e-9f);
    }
}
