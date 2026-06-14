using System;
using System.Collections.Generic;
using System.Numerics;
using Parsec.Audio.Sonification;
using Parsec.Rendering.Metal;

namespace Parsec.App;

/// <summary>
/// Produces <see cref="FractalSonicFrame"/>s from live camera and parameter state.
/// M1: populates CameraSpeed and ParameterVelocity from CPU state; geometry fields
/// are stubs (zero) until M2 adds the Metal telemetry pass.
/// M5: propagates spatial cells and camera orientation for OpenAL 3D emitters.
/// </summary>
public sealed class SonificationController
{
    private IReadOnlyList<ParamDescriptor>? _descriptors;

    private Vector3 _prevCamPos;
    private double _prevCamTime = -1;
    private double[]? _prevParamValues;
    private double _prevParamTime = -1;

    // Volatile so the audio worker thread always sees the latest frame without
    // a lock (FractalSonicFrame is a reference type; pointer reads are atomic on x64/ARM64).
    private FractalSonicFrame _latestFrame =
        new(0, 0, 0, 0, 0, 0, Vector3.Zero, 0, Vector4.Zero, Vector4.Zero, 0, 0);

    public FractalSonicFrame LatestFrame => Volatile.Read(ref _latestFrame);

    public void SetDescriptors(IReadOnlyList<ParamDescriptor> descriptors)
    {
        _descriptors = descriptors;
        _prevParamValues = null;
        _prevParamTime = -1;
    }

    public FractalSonicFrame Update(double nowSeconds, Vector3 cameraPos,
        Vector3 cameraForward, Vector3 cameraUp,
        FractalGeometryStats? telemetry = null,
        float[]? geometryPitches = null,
        float latticeRatio = 0f)
    {
        var (camSpeed, zoomVelocity) = ComputeCameraMotion(nowSeconds, cameraPos, cameraForward);
        float paramVelocity = ComputeParamVelocity(nowSeconds);

        var frame = new FractalSonicFrame(
            Time:              nowSeconds,
            HitRatio:          telemetry?.HitRatio          ?? 0f,
            MeanDepth:         telemetry?.MeanDepth          ?? 0f,
            DepthVariance:     telemetry?.DepthVariance      ?? 0f,
            StepMean:          telemetry?.StepMean           ?? 0f,
            StepP90:           telemetry?.StepP90            ?? 0f,
            NormalMean:        telemetry?.NormalMean         ?? Vector3.Zero,
            NormalVariance:    telemetry?.NormalVariance     ?? 0f,
            TrapMean:          telemetry?.TrapMean           ?? Vector4.Zero,
            TrapVariance:      telemetry?.TrapVariance       ?? Vector4.Zero,
            CameraSpeed:       camSpeed,
            ParameterVelocity: paramVelocity,
            CameraPosition:    cameraPos,
            CameraForward:     cameraForward,
            CameraUp:          cameraUp,
            Cells:             ConvertCells(telemetry?.Cells),
            ZoomVelocity:      zoomVelocity,
            GeometryPitches:   geometryPitches,
            WaveshaperCurve:    telemetry?.WaveshaperCurve,
            FieldScanWaveformTL: telemetry?.FieldScanWaveformTL,
            FieldScanWaveformTR: telemetry?.FieldScanWaveformTR,
            FieldScanWaveformBL: telemetry?.FieldScanWaveformBL,
            FieldScanWaveformBR: telemetry?.FieldScanWaveformBR,
            LatticeRatio:        latticeRatio,
            CentroidX:           telemetry?.CentroidX  ?? 0f,
            CentroidY:           telemetry?.CentroidY  ?? 0f,
            Dispersion:          telemetry?.Dispersion ?? 0f);

        Volatile.Write(ref _latestFrame, frame);
        return frame;
    }

    // Returns (totalSpeed, forwardProjectedSpeed).  Positive zoomVelocity = camera moving forward.
    private (float speed, float zoomVel) ComputeCameraMotion(double nowSeconds, Vector3 cameraPos, Vector3 cameraForward)
    {
        float speed = 0f, zoomVel = 0f;
        if (_prevCamTime >= 0)
        {
            double dt = nowSeconds - _prevCamTime;
            if (dt > 0)
            {
                var disp = cameraPos - _prevCamPos;
                float invDt = 1f / (float)dt;
                speed   = disp.Length() * invDt;
                zoomVel = Vector3.Dot(disp, cameraForward) * invDt;
            }
        }
        _prevCamPos  = cameraPos;
        _prevCamTime = nowSeconds;
        return (speed, zoomVel);
    }

    private float ComputeParamVelocity(double nowSeconds)
    {
        if (_descriptors == null) return 0f;

        var current = new double[_descriptors.Count];
        for (int i = 0; i < _descriptors.Count; i++)
            current[i] = _descriptors[i].Get();

        float velocity = 0f;
        if (_prevParamValues != null && _prevParamTime >= 0)
        {
            double dt = nowSeconds - _prevParamTime;
            if (dt > 0)
            {
                double sum = 0;
                int n = Math.Min(current.Length, _prevParamValues.Length);
                for (int i = 0; i < n; i++)
                    sum += Math.Abs(current[i] - _prevParamValues[i]);
                velocity = (float)(sum / (n * dt));
            }
        }
        _prevParamValues = current;
        _prevParamTime = nowSeconds;
        return velocity;
    }

    private static FractalSonicCell[]? ConvertCells(MetalSpatialCell[]? src)
    {
        if (src == null || src.Length == 0) return null;
        var result = new FractalSonicCell[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            ref readonly var c = ref src[i];
            result[i] = new FractalSonicCell(
                c.WorldPosition, c.HitRatio, c.MeanDepth,
                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                c.RayWavetable, c.OrbitWavetable, c.OrbitTrajectory);
        }
        return result;
    }
}
