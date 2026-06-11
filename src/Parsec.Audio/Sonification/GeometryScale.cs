namespace Parsec.Audio.Sonification;

/// <summary>
/// M7g: pitch sets derived directly from the fractal's mathematical parameters.
/// Unlike JiScale (a fixed preset), these scales change with the fractal geometry itself.
///
/// Apollonian: curvatures from the canonical integer Apollonian gasket (seeds {-1,2,2,3})
///   give the first-generation curvature set {2,3,15,35,38}. Normalised to k_root=2 and
///   octave-reduced, these are inherently just ratios — no quantizer snap required.
///   Scale: { 1/1, 35/32, 19/16, 3/2, 15/8 } — root, wide second, neutral third, fifth, M7.
///
/// Kleinian: the fold/inversion composition has an eigenvalue ratio
///   r = scale × (1 + fixedRadius/minRadius) / 2.
///   Octave-reduced and iterated, r generates the "Pythagorean-like" scale natural to the
///   Kleinian group — e.g. default params (scale=2, fixed=1, min=0.5) give r=3/2 → pure fifths.
/// </summary>
public static class GeometryScale
{
    // Apollonian JI pentatonic intervals (from curvature ratios, k_root=2)
    // curvatures {2,3,15,35,38} → octave-reduced ratios:
    //   2/2=1   35/32≈1.094   19/16≈1.188   3/2=1.5   15/8=1.875
    private static readonly float[] ApolRatios = [1f, 35f / 32f, 19f / 16f, 3f / 2f, 15f / 8f];

    /// <summary>
    /// Returns 5 pitches from the canonical integer Apollonian curvature sequence.
    /// Intervals are {unison, 35:32, 19:16, 3:2, 15:8} above rootHz — pure just ratios.
    /// </summary>
    public static float[] Apollonian(float rootHz)
    {
        var pitches = new float[ApolRatios.Length];
        for (int i = 0; i < ApolRatios.Length; i++)
            pitches[i] = rootHz * ApolRatios[i];
        return pitches;
    }

    /// <summary>
    /// Returns 6 pitches derived from the Kleinian group eigenvalue ratio.
    /// The fold/inversion composition stretches space by r = scale*(1+fixed/min)/2;
    /// octave-reduced r defines the fundamental generator interval.
    /// Default params yield r = 3/2 (Pythagorean), producing a circle-of-fifths subset.
    /// </summary>
    public static float[] Kleinian(float rootHz, float fixedRadius, float minRadius, float scale)
    {
        float r = scale * (1f + fixedRadius / MathF.Max(minRadius, 0.01f)) * 0.5f;
        if (!float.IsFinite(r) || r <= 0f) r = 1.5f;  // guard: octave-reduction loops never terminate for r <= 0
        // Reduce to (1, 2) — the octave window
        while (r >= 2f) r /= 2f;
        while (r <= 1f) r *= 2f;
        // Guard degenerate cases (r ≈ 1 = unison loop, r ≈ 2 = octave stack)
        if (r < 1.03f || r > 1.97f) r = 1.5f;

        float rootOctave = rootHz * 2f;
        var pitches = new float[6];
        float p = rootHz;
        for (int i = 0; i < 6; i++)
        {
            pitches[i] = p;
            p *= r;
            while (p >= rootOctave) p /= 2f;
            while (p < rootHz)      p *= 2f;
        }
        Array.Sort(pitches);
        return pitches;
    }
}
