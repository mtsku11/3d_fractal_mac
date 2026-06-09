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

    public PostProcessParams ToParams() => new PostProcessParams
    {
        Brightness = Brightness,
        Contrast   = Contrast,
        Gamma      = Gamma,
        Saturation = Saturation,
        HdrEnabled = HdrEnabled,
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
        },
    };
}
