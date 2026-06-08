namespace Parsec.App;

public sealed class AudioModulationMapping
{
    public required ParamDescriptor Target { get; init; }
    public AudioFeatureSource Source { get; set; }
    public double Depth { get; set; } = 0.5;   // [0, 1]
    public bool Enabled { get; set; } = true;
}
