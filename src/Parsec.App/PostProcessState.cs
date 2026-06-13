using Parsec.Rendering.Gpu;
using Parsec.Rendering.Metal;

namespace Parsec.App;

/// <summary>
/// Mutable HDR grade state, shared across all Metal 3D fractals.
/// Exposes a <see cref="ParamSchema"/> for the generic ParameterPanel.
/// Consumed as an immutable <see cref="PostProcessParams"/> via <see cref="ToParams"/>.
/// </summary>
public sealed class PostProcessState
{
    public float Brightness = 1.0f;
    public float Contrast   = 1.0f;
    public float Gamma      = 1.0f;
    public float Saturation = 1.0f;
    public bool  HdrEnabled = false;
    public bool  BloomEnabled   = false;
    public float BloomThreshold = 1.0f;
    public float BloomIntensity = 0.6f;
    public float BloomRadius    = 1.0f;

    public PostProcessParams ToParams() => new PostProcessParams
    {
        Brightness = Brightness,
        Contrast   = Contrast,
        Gamma      = Gamma,
        Saturation = Saturation,
        HdrEnabled = HdrEnabled,
        BloomEnabled   = BloomEnabled,
        BloomThreshold = BloomThreshold,
        BloomIntensity = BloomIntensity,
        BloomRadius    = BloomRadius,
    };

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "Brightness", Group = "Post: grade",
                Min = 0.0, Max = 4.0, Decimals = 2,
                Get = () => Brightness, Set = v => Brightness = (float)v },
            new ParamDescriptor {
                Label = "Contrast", Group = "Post: grade",
                Min = 0.0, Max = 4.0, Decimals = 2,
                Get = () => Contrast, Set = v => Contrast = (float)v },
            new ParamDescriptor {
                Label = "Saturation", Group = "Post: grade",
                Min = 0.0, Max = 3.0, Decimals = 2,
                Get = () => Saturation, Set = v => Saturation = (float)v },
            new ParamDescriptor {
                Label = "Gamma", Group = "Post: grade",
                Min = 0.1, Max = 4.0, Decimals = 2,
                Get = () => Gamma, Set = v => Gamma = (float)v },
            new ParamDescriptor {
                Label = "HDR Tone-map (tanh)", Group = "Post: grade",
                Min = 0.0, Max = 1.0, Decimals = 0, IsToggle = true,
                Get = () => HdrEnabled ? 1.0 : 0.0, Set = v => HdrEnabled = v >= 0.5 },
            new ParamDescriptor {
                Label = "Bloom", Group = "Post: bloom",
                Min = 0.0, Max = 1.0, Decimals = 0, IsToggle = true,
                Get = () => BloomEnabled ? 1.0 : 0.0, Set = v => BloomEnabled = v >= 0.5 },
            new ParamDescriptor {
                Label = "Bloom Threshold", Group = "Post: bloom",
                Min = 0.0, Max = 2.0, Decimals = 2,
                Get = () => BloomThreshold, Set = v => BloomThreshold = (float)v },
            new ParamDescriptor {
                Label = "Bloom Intensity", Group = "Post: bloom",
                Min = 0.0, Max = 3.0, Decimals = 2,
                Get = () => BloomIntensity, Set = v => BloomIntensity = (float)v },
            new ParamDescriptor {
                Label = "Bloom Size", Group = "Post: bloom",
                Min = 0.25, Max = 3.0, Decimals = 2,
                Get = () => BloomRadius, Set = v => BloomRadius = (float)v },
        },
    };
}
