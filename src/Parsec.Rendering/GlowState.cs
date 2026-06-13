using System.Numerics;

namespace Parsec.Rendering;

/// <summary>
/// Global controls for fake-volumetric "step glow". As a ray marches, near-misses
/// to the surface accumulate light (shaped by <see cref="_falloff"/>); the kernel
/// scales that by <see cref="_strength"/> and tints it with the active palette.
/// Mirrors <see cref="DomainWarpState"/>: a process-wide static read by the Metal
/// render-param builders, so no per-call threading is needed. Packed into the
/// previously-unused z/w lanes of <c>tanFov</c>.
/// </summary>
public static class GlowState
{
    private static readonly object Gate = new();
    private static bool _enabled;
    private static float _strength = 1.0f;
    private static float _falloff = 30f;

    public static void SetControls(bool enabled, float strength, float falloff)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _strength = Math.Clamp(strength, 0f, 8f);
            _falloff = Math.Clamp(falloff, 0.1f, 500f);
        }
    }

    /// <summary>Packs glow into the free z/w lanes of the tanFov vector
    /// (x = tanX, y = tanY, z = strength [0 when disabled], w = falloff).</summary>
    public static Vector4 EncodeTanFov(float tanX, float tanY)
    {
        lock (Gate)
        {
            return new Vector4(tanX, tanY, _enabled ? _strength : 0f, _falloff);
        }
    }
}
