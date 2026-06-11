using System.Numerics;

namespace Parsec.Audio.Sonification;

/// <summary>
/// One spatial region of fractal geometry, derived from a 4×4 partition of the
/// telemetry march.  Each cell drives an independent OpenAL 3D point source in the
/// M5 spatial emitter array.
/// </summary>
public sealed record class FractalSonicCell(
    Vector3  WorldPosition,
    float    HitRatio,
    float    MeanDepth,
    float    StepComplexity,
    Vector3  NormalMean,
    Vector4  TrapMean,
    float    Energy,
    float[]? RayWavetable    = null,   // M7b: 64-sample ray DE-step wavetable, normalised [-1,1]
    float[]? OrbitWavetable  = null,   // M7b: 64-sample inner-iteration orbit magnitude wavetable
    Vector4[]? OrbitTrajectory = null); // M9a: 128-point orbit trajectory (xyz=point clamped [-4,4], w=1 bounded/0 escaped)
