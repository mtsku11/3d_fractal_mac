using System.Numerics;

namespace Parsec.Rendering.Metal;

/// <summary>
/// Reduced telemetry statistics from a low-res DE march pass.
/// Produced by <see cref="MetalMandelboxRenderer.RunTelemetryPass"/> and fed
/// to <c>SonificationController</c> to populate the geometry fields of
/// <see cref="Parsec.Audio.Sonification.FractalSonicFrame"/>.
/// </summary>
public readonly record struct FractalGeometryStats(
    float HitRatio,
    float MeanDepth,
    float DepthVariance,
    float StepMean,
    float StepP90,
    Vector3 NormalMean,
    float NormalVariance,
    Vector4 TrapMean,
    Vector4 TrapVariance,
    MetalSpatialCell[]? Cells = null);
