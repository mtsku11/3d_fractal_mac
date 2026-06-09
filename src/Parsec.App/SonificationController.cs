using System;
using System.Collections.Generic;
using System.Numerics;
using Parsec.Audio.Sonification;

namespace Parsec.App;

/// <summary>
/// Produces <see cref="FractalSonicFrame"/>s from live camera and parameter state.
/// M1: populates CameraSpeed and ParameterVelocity from CPU state; geometry fields
/// are stubs (zero) until M2 adds the Metal telemetry pass.
/// </summary>
public sealed class SonificationController
{
    private IReadOnlyList<ParamDescriptor>? _descriptors;

    private Vector3 _prevCamPos;
    private double _prevCamTime = -1;
    private double[]? _prevParamValues;
    private double _prevParamTime = -1;

    private FractalSonicFrame _latestFrame =
        new(0, 0, 0, 0, 0, 0, Vector3.Zero, 0, Vector4.Zero, Vector4.Zero, 0, 0);

    public FractalSonicFrame LatestFrame => _latestFrame;

    public void SetDescriptors(IReadOnlyList<ParamDescriptor> descriptors)
    {
        _descriptors = descriptors;
        _prevParamValues = null;
        _prevParamTime = -1;
    }

    public FractalSonicFrame Update(double nowSeconds, Vector3 cameraPos)
    {
        float camSpeed = ComputeCameraSpeed(nowSeconds, cameraPos);
        float paramVelocity = ComputeParamVelocity(nowSeconds);

        var frame = new FractalSonicFrame(
            Time: nowSeconds,
            HitRatio: 0f,
            MeanDepth: 0f,
            DepthVariance: 0f,
            StepMean: 0f,
            StepP90: 0f,
            NormalMean: Vector3.Zero,
            NormalVariance: 0f,
            TrapMean: Vector4.Zero,
            TrapVariance: Vector4.Zero,
            CameraSpeed: camSpeed,
            ParameterVelocity: paramVelocity);

        _latestFrame = frame;
        return frame;
    }

    private float ComputeCameraSpeed(double nowSeconds, Vector3 cameraPos)
    {
        float speed = 0f;
        if (_prevCamTime >= 0)
        {
            double dt = nowSeconds - _prevCamTime;
            if (dt > 0)
                speed = (cameraPos - _prevCamPos).Length() / (float)dt;
        }
        _prevCamPos = cameraPos;
        _prevCamTime = nowSeconds;
        return speed;
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
}
