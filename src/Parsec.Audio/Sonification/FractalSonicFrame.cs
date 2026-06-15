using System.Numerics;

namespace Parsec.Audio.Sonification;

public sealed record class FractalSonicFrame(
    double   Time,
    float    HitRatio,
    float    MeanDepth,
    float    DepthVariance,
    float    StepMean,
    float    StepP90,
    Vector3  NormalMean,
    float    NormalVariance,
    Vector4  TrapMean,
    Vector4  TrapVariance,
    float    CameraSpeed,
    float    ParameterVelocity,
    // M5 spatial fields — optional so existing construction sites need no change
    Vector3  CameraPosition  = default,
    Vector3  CameraForward   = default,
    Vector3  CameraUp        = default,
    FractalSonicCell[]? Cells = null,
    // M7f: signed zoom velocity — positive = diving into fractal; drives Shepard–Risset glide rate
    float    ZoomVelocity    = 0f,
    // M7g: geometry-native pitch set — intervals derived from fractal parameters (Apollonian/Kleinian).
    // Null for fractals with no geometry-derived scale; non-null bypasses JiQuantizer snap.
    float[]? GeometryPitches  = null,
    // M7h: 64-sample DE cross-section strip normalised to [-1,1].
    // Captured along camera-right from the telemetry pass; used as a waveshaping transfer curve.
    // Null when no telemetry pass ran (Apollonian, other non-telemetry fractals).
    float[]? WaveshaperCurve  = null,
    // M8-spatial: 4-corner 64-sample field-scan waveforms, AC-coupled and normalised [-1,1].
    // TL/TR are above centre (positive camUp, spectrally bright); BL/BR below centre (dark).
    // TL+BL -> left drone; TR+BR -> right drone.  Null when no field-scan pass ran.
    float[]? FieldScanWaveformTL = null,
    float[]? FieldScanWaveformTR = null,
    float[]? FieldScanWaveformBL = null,
    float[]? FieldScanWaveformBR = null,
    // DirectOrbit profiles: geometry-derived lattice generator (octave-reduced, (1,2)).
    // 0 = no geometry ratio — the synth falls back to the voice profile's default.
    // Kleinian: eigenvalue ratio; Mandelbulb: (power+1)/power. Morphs retune the grid live.
    float    LatticeRatio = 0f,
    // Improvement 2a: full-resolution (64×36) energy centroid + spread in normalised screen
    // space ([-1,1]; X: left→right, Y: bottom→top). MIDI position/dispersion CCs prefer these
    // over the 4×4-derived centroid. 0,0 = centred / no hits.
    float    CentroidX = 0f,
    float    CentroidY = 0f,
    float    Dispersion = 0f,
    // Improvement 2b: finer (8×6 = 48) energy grid for MIDI region notes only — row-major
    // top→bottom, left→right; per-tile energy in [0,1]. Null when no telemetry ran. The
    // sonification 4×4 Cells grid is unaffected.
    float[]? MidiRegionEnergy = null);
