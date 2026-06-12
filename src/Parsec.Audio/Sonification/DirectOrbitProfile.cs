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
///
/// Modal signature (optional): when <see cref="ModeRatios"/> is non-null the orbit signal
/// stops being played raw and instead excites a bank of tuned two-pole resonators — the
/// fractal becomes a struck/bowed body with an instrument-level identity (tube, glass,
/// metal…). Mode m of cell (col, row) resonates at
///   f_m = loopRate(cell) · ModeFreqMul · ModeRatios[m]
/// so the bank stays locked to the lattice and retunes live with geometry morphs.
/// Raw chaotic playback is timbrally a noise drone regardless of fractal; the modal
/// stage is where the distinct-instrument character lives.
/// </summary>
public readonly record struct DirectOrbitProfile(
    float RootDivisor,      // whole-grid transpose: ×1 = Mandelbox baseline (bottom-left ≈ 28.7 Hz)
    float LatticeRatio,     // default grid generator when the frame has no geometry ratio
    float RevFb0,  float RevFb1,    // comb feedback at encl 0 / 1
    float RevDamp0, float RevDamp1, // comb damping at encl 0 / 1
    float RevWet0, float RevWet1,   // wet level at encl 0 / 1
    float ChimeDecaySec,    // fold-chime envelope time constant
    float ChimePartial,     // second chime partial multiple (2.0 = harmonic, higher = clangy)
    float[]? ModeRatios     = null,  // mode frequency multiples of the scaled cell fundamental (null = raw orbit playback)
    float[]? ModeGains      = null,  // per-mode level (same length as ModeRatios)
    float[]? ModeDecaysSec  = null,  // per-mode T60-ish decay (sets resonator Q)
    float ModeFreqMul       = 4f,    // lifts mode 1 above the orbit loop rate into the audible register
    float ModalDrive        = 1f,    // master excitation level into the bank
    float ExciterBleed      = 0.1f)  // how much raw orbit texture leaks past the bank
{
    public bool HasModalBody =>
        ModeRatios is { Length: > 0 }
        && ModeGains is { Length: > 0 }
        && ModeDecaysSec is { Length: > 0 };

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

        // Hollow caverns one octave down: fifth lattice from the fold scale (|scale| 3 → 3/2),
        // damp open space blooming to a vast wet interior, short clangy chimes.
        // Modal body: open-pipe odd-harmonic modes, moderate decay — hollow and woody,
        // mode 1 aligned to a harmonic of the orbit loop so the bank rings strongly tonal.
        FractalVoice.Menger => new(
            RootDivisor: 0.5f, LatticeRatio: 1.5f,
            RevFb0: 0.74f, RevFb1: 0.95f, RevDamp0: 0.45f, RevDamp1: 0.25f,
            RevWet0: 0.12f, RevWet1: 0.50f,
            ChimeDecaySec: 0.4f, ChimePartial: 3.0f,
            ModeRatios:    [1f, 3f, 5f, 7f, 9f],
            ModeGains:     [1f, 0.55f, 0.32f, 0.18f, 0.10f],
            ModeDecaysSec: [0.9f, 0.55f, 0.38f, 0.26f, 0.18f],
            ModeFreqMul: 4f, ModalDrive: 1.5f, ExciterBleed: 0.12f),

        // Glassy bell register two octaves up on the gasket's 19/16 neutral third,
        // bright sparse space, ringing near-harmonic chimes.
        // Modal body: inharmonic high-Q glass modes (long decays). The settled-orbit
        // impulse bursts that read as crackle in raw playback become bell strikes here.
        FractalVoice.Apollonian => new(
            RootDivisor: 4.0f, LatticeRatio: 19f / 16f,
            RevFb0: 0.68f, RevFb1: 0.90f, RevDamp0: 0.20f, RevDamp1: 0.08f,
            RevWet0: 0.12f, RevWet1: 0.40f,
            ChimeDecaySec: 0.9f, ChimePartial: 2.24f,
            ModeRatios:    [1f, 2.32f, 4.25f, 6.63f, 9.38f],
            ModeGains:     [1f, 0.70f, 0.50f, 0.34f, 0.22f],
            ModeDecaysSec: [3.0f, 2.4f, 1.8f, 1.2f, 0.8f],
            ModeFreqMul: 3f, ModalDrive: 4.0f, ExciterBleed: 0.05f),

        // Crystalline: fourth lattice (4/3) high above the baseline, airy bright tail,
        // icy inharmonic chimes.
        FractalVoice.Kifs => new(
            RootDivisor: 3.0f, LatticeRatio: 4f / 3f,
            RevFb0: 0.72f, RevFb1: 0.92f, RevDamp0: 0.15f, RevDamp1: 0.05f,
            RevWet0: 0.14f, RevWet1: 0.45f,
            ChimeDecaySec: 0.6f, ChimePartial: 3.36f),

        // Warm quaternion pad: major-third lattice (5/4) a fifth up, enveloping wet
        // space, long pure harmonic chimes.
        FractalVoice.QJBox => new(
            RootDivisor: 1.5f, LatticeRatio: 5f / 4f,
            RevFb0: 0.76f, RevFb1: 0.93f, RevDamp0: 0.40f, RevDamp1: 0.20f,
            RevWet0: 0.18f, RevWet1: 0.50f,
            ChimeDecaySec: 1.8f, ChimePartial: 2.0f),

        // Mandelbox baseline
        _ => Default,
    };
}
