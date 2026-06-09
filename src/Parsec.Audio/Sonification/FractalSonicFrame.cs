using System.Numerics;

namespace Parsec.Audio.Sonification;

public sealed record class FractalSonicFrame(
    double Time,
    float HitRatio,
    float MeanDepth,
    float DepthVariance,
    float StepMean,
    float StepP90,
    Vector3 NormalMean,
    float NormalVariance,
    Vector4 TrapMean,
    Vector4 TrapVariance,
    float CameraSpeed,
    float ParameterVelocity);
