using System.Numerics;

namespace Parsec.Rendering.Metal;

/// <summary>
/// One spatial tile produced by partitioning the 64×36 telemetry march into a 4×4 grid.
/// WorldPosition is the mean hit point in world space for that tile.  Feeds M5 spatial
/// OpenAL emitters via <see cref="Parsec.Audio.Sonification.FractalSonicCell"/>.
/// </summary>
public readonly record struct MetalSpatialCell(
    Vector3 WorldPosition,
    float   HitRatio,
    float   MeanDepth,
    float   StepComplexity,
    Vector3 NormalMean,
    Vector4 TrapMean,
    float   Energy);
