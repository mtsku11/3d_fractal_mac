using System;
using System.Numerics;

namespace Parsec.Rendering.Fluid;

/// <summary>Post-process tunables for the deep-sea emissive layer + water grade.</summary>
public sealed record DeepSeaParams(
    float Bloom = 0.95f,
    float Rim = 1.35f,
    float PhotophoreBrightness = 1.0f,
    float GradeBrightness = 1.0f,
    float GodRays = 0.6f)
{
    public static DeepSeaParams Default { get; } = new();
}

/// <summary>
/// Curated, slider-backed parameters for the live "deep sea mode". Owned by the view; the UI sets
/// the fields and the render path reads them. <see cref="Phase"/> is the internal warp animation
/// accumulator (advanced by <see cref="WarpRate"/> each frame).
/// </summary>
public sealed class DeepSeaState
{
    public bool Enabled;

    // Particles / flow.
    public int ParticleCount = 1600;
    public float FlowStrength = 1.0f;     // scales curl/swirl/advect
    public float Falloff = 0.40f;         // near-field influence distance

    // Emissive look (toned down from the original neon defaults).
    public float Bloom = 0.55f;
    public float Rim = 0.7f;
    public int PhotophoreCount = 90;
    public float PhotophoreGlow = 1.0f;

    // Membrane warp (geometry undulation).
    public float WarpStrength = 0.022f;
    public float WarpScale = 16.8f;
    public float WarpRate = 6.0f;          // temporal ripple rate

    // Water.
    public float Murk = 0.5f;              // 0 = clear/bright, 1 = dark deep water
    public float GodRays = 0.6f;           // volumetric light-shaft strength
    public int SnowCount = 1400;           // drifting "marine snow" detritus motes
    public float BreathDepth = 0.5f;       // slow inhale/exhale on the warp amplitude

    public float Phase;                    // accumulated warp phase (render-driven)

    /// <summary>
    /// Gentle neutral-buoyancy bob/sway offset applied to BOTH camera position and target (a pure
    /// translation — no z-dolly, so framing/scale never change), making the creature float in the
    /// current against the fixed god rays + vignette. Low, incommensurate frequencies so it never
    /// looks like a loop.
    /// </summary>
    public static Vector3 BuoyancyOffset(float t, float amount = 1f)
    {
        float x = 0.045f * MathF.Sin(2f * MathF.PI * 0.05f * t + 1.3f);   // sway
        float y = 0.060f * MathF.Sin(2f * MathF.PI * 0.08f * t);          // bob
        return new Vector3(x, y, 0f) * amount;
    }

    public DeepSeaParams ToParams() => new(
        Bloom: Bloom,
        Rim: Rim,
        PhotophoreBrightness: PhotophoreGlow,
        GradeBrightness: 1.0f - 0.6f * System.Math.Clamp(Murk, 0f, 1f),
        GodRays: GodRays);
}
