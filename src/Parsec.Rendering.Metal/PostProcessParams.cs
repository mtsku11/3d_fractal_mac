namespace Parsec.Rendering.Metal;

/// <summary>
/// Grade parameters for the HDR post-process pass (postprocess.metal).
/// Default values produce identity output (no visible change vs. the pre-M12 RGBA8 pack).
/// </summary>
public struct PostProcessParams
{
    public float Brightness;
    public float Contrast;
    public float Gamma;
    public float Saturation;
    /// <summary>When true, applies tanh tone mapping after contrast (maps HDR > 1 back to [0,1]).</summary>
    public bool HdrEnabled;

    /// <summary>Screen-space bloom: blurred bright-pass added back so intricate detail emits light.
    /// When false (or BloomIntensity 0) the post pass is bit-identical to grade-only.</summary>
    public bool BloomEnabled;
    /// <summary>Luma above which a pixel contributes to bloom.</summary>
    public float BloomThreshold;
    /// <summary>Strength of the bloom add (linear HDR, before grade).</summary>
    public float BloomIntensity;
    /// <summary>Bloom spread multiplier (scales the gaussian radius relative to image size).</summary>
    public float BloomRadius;

    public PostProcessParams()
    {
        Brightness = 1f;
        Contrast   = 1f;
        Gamma      = 1f;
        Saturation = 1f;
        HdrEnabled = false;
        BloomEnabled   = false;
        BloomThreshold = 1.0f;
        BloomIntensity = 0.6f;
        BloomRadius    = 1.0f;
    }
}
