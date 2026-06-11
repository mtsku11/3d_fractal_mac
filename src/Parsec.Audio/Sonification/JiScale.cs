namespace Parsec.Audio.Sonification;

/// <summary>
/// A named set of just-intonation frequency ratios within one octave.
/// Ratios are relative to the root, ascending in [1.0, 2.0).
/// </summary>
public sealed class JiScale
{
    public string Name { get; }
    public float[] Ratios { get; }

    public JiScale(string name, float[] ratios)
    {
        Name = name;
        Ratios = ratios;
    }

    // 5-note JI pentatonic — most consonant, fewest clash points; good default for drones
    public static readonly JiScale Pentatonic = new("Pentatonic",
        [1f, 9f/8f, 5f/4f, 3f/2f, 5f/3f]);

    // Ptolemaic (syntonic) just major scale
    public static readonly JiScale Major = new("Major",
        [1f, 9f/8f, 5f/4f, 4f/3f, 3f/2f, 5f/3f, 15f/8f]);

    // JI Dorian — modal, drone-friendly, good for dark/spatial textures
    public static readonly JiScale Dorian = new("Dorian",
        [1f, 9f/8f, 6f/5f, 4f/3f, 3f/2f, 5f/3f, 16f/9f]);

    // Low partials of the harmonic series — physically grounded, used in spectral music
    public static readonly JiScale HarmonicSeries = new("HarmonicSeries",
        [1f, 5f/4f, 3f/2f, 7f/4f]);

    public static readonly JiScale[] All = [Pentatonic, Major, Dorian, HarmonicSeries];
}
