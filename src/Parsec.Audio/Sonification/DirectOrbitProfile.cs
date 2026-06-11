namespace Parsec.Audio.Sonification;

/// <summary>
/// Per-fractal sonic identity for the DirectOrbit synth — gives each fractal family a
/// distinct register, lattice tuning, acoustic space, and fold-chime character.
/// Used identically by <see cref="DirectOrbitSynth"/> (offline) and
/// <see cref="FractalDroneStream"/> (live); keep both in sync.
///
/// The grid pitch for cell (col, row) is
///   f0 = sampleRate · RootDivisor · ratio^col · ratio^(3−row) / (SamplesPerStep · OrbtLen)
/// where ratio is <see cref="FractalSonicFrame.LatticeRatio"/> when geometry-derived
/// (Kleinian eigenvalue, Mandelbulb (power+1)/power — morphs retune the grid live),
/// falling back to <see cref="LatticeRatio"/>.
///
/// Reverb fields are the enclosure interpolation endpoints (encl = HitRatio, 0 → 1).
/// </summary>
public readonly record struct DirectOrbitProfile(
    float RootDivisor,      // whole-grid transpose: ×1 = Mandelbox baseline (bottom-left ≈ 28.7 Hz)
    float LatticeRatio,     // default grid generator when the frame has no geometry ratio
    float RevFb0,  float RevFb1,    // comb feedback at encl 0 / 1
    float RevDamp0, float RevDamp1, // comb damping at encl 0 / 1
    float RevWet0, float RevWet1,   // wet level at encl 0 / 1
    float ChimeDecaySec,    // fold-chime envelope time constant
    float ChimePartial)     // second chime partial multiple (2.0 = harmonic, higher = clangy)
{
    /// <summary>Mandelbox baseline — the original DirectOrbit character (metallic fifths).</summary>
    public static readonly DirectOrbitProfile Default = new(
        RootDivisor: 1.0f, LatticeRatio: 1.5f,
        RevFb0: 0.70f, RevFb1: 0.94f, RevDamp0: 0.35f, RevDamp1: 0.15f,
        RevWet0: 0.10f, RevWet1: 0.45f,
        ChimeDecaySec: 0.7f, ChimePartial: 2.41f);

    public static DirectOrbitProfile ForVoice(FractalVoice voice) => voice switch
    {
        // Airy mid-register cluster pad: whole-tone-ish (power+1)/power lattice two-plus
        // octaves up, brighter and wetter space, long harmonic bell chimes.
        FractalVoice.Mandelbulb => new(
            RootDivisor: 6.0f, LatticeRatio: 9f / 8f,
            RevFb0: 0.74f, RevFb1: 0.94f, RevDamp0: 0.25f, RevDamp1: 0.10f,
            RevWet0: 0.15f, RevWet1: 0.50f,
            ChimeDecaySec: 1.4f, ChimePartial: 2.0f),

        // Dark cavern: grid a fifth-plus down, vast bright-tailed space (T60 → ~8 s
        // fully enclosed), very long clangorous chimes. Lattice from the eigenvalue ratio.
        FractalVoice.Kleinian => new(
            RootDivisor: 0.667f, LatticeRatio: 1.5f,
            RevFb0: 0.78f, RevFb1: 0.96f, RevDamp0: 0.30f, RevDamp1: 0.10f,
            RevWet0: 0.18f, RevWet1: 0.55f,
            ChimeDecaySec: 2.5f, ChimePartial: 2.76f),

        // Percussive and tight: grid two fifths up (bottom ≈ C2), short dry space,
        // snappy crackle chimes.
        FractalVoice.BurningShip => new(
            RootDivisor: 2.25f, LatticeRatio: 1.5f,
            RevFb0: 0.60f, RevFb1: 0.82f, RevDamp0: 0.50f, RevDamp1: 0.30f,
            RevWet0: 0.08f, RevWet1: 0.25f,
            ChimeDecaySec: 0.22f, ChimePartial: 2.41f),

        // Mandelbox + Apollonian (DirectOrbit falls back to hybrid for Apollonian anyway)
        _ => Default,
    };
}
