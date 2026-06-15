namespace Parsec.Audio.Midi;

/// <summary>The continuous geometry signals the controller can route to MIDI CCs.</summary>
public enum MidiSignal
{
    Size, Proximity, Complexity, Expansion, Colour,
    Layering, Haze, Verticality, Speed, Dolly,
    PositionX, PositionY, Dispersion, Structure, Heterogeneity,
    Saturation, Brightness,
}

/// <summary>
/// One editable signal→CC route (improvement M3b). The signal's value is computed
/// internally (and is intrinsically <see cref="Signed"/> or not); this record lets the
/// user choose where it goes (<see cref="Cc"/>/<see cref="Channel"/>), its output
/// <see cref="OutMin"/>–<see cref="OutMax"/> range, polarity (<see cref="Invert"/>),
/// and whether it transmits at all (<see cref="Enabled"/>).
/// </summary>
public sealed class MidiCcMapping
{
    public MidiSignal Signal { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Intrinsic to the signal: signed values centre at the range midpoint.</summary>
    public bool Signed { get; init; }
    public bool Enabled { get; set; } = true;
    public int Cc { get; set; }
    public int Channel { get; set; }          // 0–15 (0 = MIDI channel 1)
    public int OutMin { get; set; }           // 0–127
    public int OutMax { get; set; } = 127;    // 0–127
    public bool Invert { get; set; }

    /// <summary>Last value transmitted (for change-only dedup). Reset to -1 on (re)config.</summary>
    public int LastSent = -1;
    /// <summary>Per-mapping one-pole smoothing state (used when the caller passes a raw value).</summary>
    public float Smoothed;
    public bool SmoothInit;
}

/// <summary>Channel + enable for one of the note groups (gestures / 4×4 / fine grid).</summary>
public sealed class MidiNoteGroupConfig
{
    public bool Enabled { get; set; } = true;
    public int Channel { get; set; }
}

/// <summary>
/// Editable MIDI mapping table consumed by <see cref="MidiOutputController"/>. Owned by the
/// app so it outlives the lazily-created controller; the controller reads it live each frame.
/// Defaults reproduce the fixed M1–M4 + improvement-1/2a/2b map exactly.
/// </summary>
public sealed class MidiMappingConfig
{
    public MidiCcMapping[] Cc { get; }

    // Note groups. Default channels match the controller's prior fixed behaviour:
    // gestures + 4×4 on channel 1 (index 0), fine 8×6 grid on channel 2 (index 1).
    public MidiNoteGroupConfig Gestures   { get; } = new() { Enabled = true, Channel = 0 };
    public MidiNoteGroupConfig Spatial4x4 { get; } = new() { Enabled = true, Channel = 0 };
    public MidiNoteGroupConfig FineGrid   { get; } = new() { Enabled = true, Channel = 1 };

    public MidiMappingConfig()
    {
        // (signal, label, cc, signed)
        (MidiSignal s, string label, int cc, bool signed)[] defaults =
        {
            (MidiSignal.Size,          "Size",          20, false),
            (MidiSignal.Proximity,     "Proximity",     21, false),
            (MidiSignal.Complexity,    "Complexity",    22, false),
            (MidiSignal.Expansion,     "Expansion",     23, true),
            (MidiSignal.Colour,        "Colour",        24, false),
            (MidiSignal.Layering,      "Layering",      25, false),
            (MidiSignal.Haze,          "Haze",          26, false),
            (MidiSignal.Verticality,   "Verticality",   27, true),
            (MidiSignal.Speed,         "Speed",         28, false),
            (MidiSignal.Dolly,         "Dolly",         29, true),
            (MidiSignal.PositionX,     "Position X",    30, true),
            (MidiSignal.PositionY,     "Position Y",    31, true),
            (MidiSignal.Dispersion,    "Dispersion",    32, false),
            (MidiSignal.Structure,     "Structure",     33, false),
            (MidiSignal.Heterogeneity, "Heterogeneity", 34, false),
            (MidiSignal.Saturation,    "Saturation",    35, false),
            (MidiSignal.Brightness,    "Brightness",    36, false),
        };

        Cc = new MidiCcMapping[defaults.Length];
        for (int i = 0; i < defaults.Length; i++)
        {
            var (s, label, cc, signed) = defaults[i];
            Cc[i] = new MidiCcMapping
            {
                Signal = s, Label = label, Cc = cc, Signed = signed,
                // Signed signals centre at 64 with the prior 1..127 span; unsigned use 0..127.
                OutMin = signed ? 1 : 0, OutMax = 127,
            };
        }
    }

    /// <summary>Mapping for a signal (signals map 1:1 to <see cref="Cc"/> entries).</summary>
    public MidiCcMapping this[MidiSignal s] => Cc[(int)s];
}
