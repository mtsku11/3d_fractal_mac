namespace Parsec.Audio.Sonification;

/// <summary>
/// Snaps a raw geometry-derived frequency to the nearest just-intonation pitch,
/// then blends back toward the raw value via <see cref="TemperamentStrength"/>.
///
/// f_out = lerp(f_geometry, f_ji_snapped, TemperamentStrength)
///
/// TemperamentStrength = 1.0  →  strict just intonation (maximally consonant)
/// TemperamentStrength = 0.0  →  raw geometry frequency (maximally faithful / microtonal)
/// Mid-values give microtonally-inflected-but-anchored tunings.
/// </summary>
public sealed class JiQuantizer
{
    private readonly JiScale _scale;
    private readonly float _rootHz;

    private float _temperamentStrength = 1.0f;

    /// <summary>
    /// Blend toward just intonation. Clamped to [0, 1].
    /// </summary>
    public float TemperamentStrength
    {
        get => _temperamentStrength;
        set => _temperamentStrength = Math.Clamp(value, 0f, 1f);
    }

    public JiScale Scale => _scale;
    public float RootHz => _rootHz;

    public JiQuantizer(JiScale scale, float rootHz = 220f)
    {
        _scale = scale;
        _rootHz = rootHz;
    }

    /// <summary>
    /// Quantize a raw frequency toward the scale's nearest JI pitch.
    /// </summary>
    public float Quantize(float fGeometry)
    {
        if (fGeometry <= 0f) return _rootHz;
        if (_temperamentStrength == 0f) return fGeometry;

        float fSnapped = Snap(fGeometry);
        return fGeometry + (fSnapped - fGeometry) * _temperamentStrength;
    }

    /// <summary>
    /// Find the nearest JI pitch to <paramref name="f"/> in any octave.
    /// </summary>
    public float Snap(float f)
    {
        if (f <= 0f) return _rootHz;

        // Express f as root * 2^octave * normalized, normalized ∈ [1, 2)
        double ratioD = (double)f / _rootHz;
        int octave = (int)Math.Floor(Math.Log2(ratioD));
        float normalized = (float)(ratioD / Math.Pow(2.0, octave));

        // Clamp floating-point edge cases to [1, 2)
        if (normalized < 1f) normalized = 1f;
        if (normalized >= 2f) { normalized /= 2f; octave++; }

        // Find nearest scale ratio; also test 2.0 (wraps to octave unison above)
        float bestRatio = _scale.Ratios[0];
        int bestOctave = octave;
        float bestDist = float.MaxValue;

        foreach (float r in _scale.Ratios)
        {
            float d = MathF.Abs(normalized - r);
            if (d < bestDist) { bestDist = d; bestRatio = r; bestOctave = octave; }
        }

        float dOctave = MathF.Abs(normalized - 2f);
        if (dOctave < bestDist) { bestRatio = 1f; bestOctave = octave + 1; }

        return bestRatio * _rootHz * MathF.Pow(2f, bestOctave);
    }
}
