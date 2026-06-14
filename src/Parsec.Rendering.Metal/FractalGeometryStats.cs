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
    MetalSpatialCell[]? Cells = null,
    // M7h: 64-sample DE cross-section strip — values normalised [-1,1].
    // Null if the telemetry PSO failed or the fractal has no telemetry pass.
    float[]? WaveshaperCurve = null,
    // M8-spatial: 4-corner 64-sample field-scan waveforms, AC-coupled and normalised [-1,1].
    // TL/TR are above centre (positive camUp, spectrally bright); BL/BR below centre (dark).
    // TL+BL -> left drone; TR+BR -> right drone.  Null if field-scan PSO failed.
    float[]? FieldScanWaveformTL = null,
    float[]? FieldScanWaveformTR = null,
    float[]? FieldScanWaveformBL = null,
    float[]? FieldScanWaveformBR = null,
    // Improvement 2a: full-resolution (64×36) energy centroid + spread in normalised screen
    // space ([-1,1]; X: left→right, Y: bottom→top). Sharper than the 4×4-derived centroid.
    // 0,0 = centred / no hits.
    float CentroidX = 0f,
    float CentroidY = 0f,
    float Dispersion = 0f);
