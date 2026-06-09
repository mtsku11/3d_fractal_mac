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

    public PostProcessParams()
    {
        Brightness = 1f;
        Contrast   = 1f;
        Gamma      = 1f;
        Saturation = 1f;
        HdrEnabled = false;
    }
}
