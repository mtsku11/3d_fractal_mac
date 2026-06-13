using System.Numerics;

namespace Parsec.Rendering;

public static class DomainWarpState
{
    private static readonly object Gate = new();
    private static bool _enabled;
    private static float _strength = 0.15f;
    private static float _scale = 1.5f;
    private static float _phase;

    public static void SetControls(bool enabled, float strength, float scale)
    {
        lock (Gate)
        {
            _enabled = enabled;
            _strength = Math.Clamp(strength, 0f, 0.75f);
            _scale = Math.Clamp(scale, 0.05f, 12f);
        }
    }

    public static void SetPhase(float phase)
    {
        lock (Gate) { _phase = phase; }
    }

    public static float GetPhase()
    {
        lock (Gate) { return _phase; }
    }

    public static Vector4 EncodeSubpixelJitter(Vector2 jitter)
    {
        lock (Gate)
        {
            return new Vector4(jitter.X, jitter.Y, _enabled ? _strength : 0f, _scale);
        }
    }
}
