namespace Parsec.App;

/// <summary>
/// One tunable parameter, described generically so the UI can build a slider
/// for it without knowing which fractal it belongs to. The getter/setter close
/// over the live parameter object, so moving the slider mutates the real state.
/// </summary>
public sealed class ParamDescriptor
{
    public required string Label { get; init; }
    public required string Group { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    /// <summary>Optional step for the slider's tick/quantization. 0 = continuous.</summary>
    public double Step { get; init; }
    /// <summary>Number of decimal places to show in the numeric readout.</summary>
    public int Decimals { get; init; } = 2;
    public required Func<double> Get { get; init; }
    public required Action<double> Set { get; init; }
    /// <summary>When true, the panel renders a CheckBox (0 = false, 1 = true) instead of a slider.</summary>
    public bool IsToggle { get; init; }
}

/// <summary>
/// A named, ordered collection of parameter descriptors grouped for display.
/// A fractal type produces one of these; the panel renders it.
/// </summary>
public sealed class ParamSchema
{
    public required IReadOnlyList<ParamDescriptor> Parameters { get; init; }

    /// <summary>Group labels in first-seen order.</summary>
    public IEnumerable<string> Groups => Parameters.Select(p => p.Group).Distinct();

    public IEnumerable<ParamDescriptor> InGroup(string group) =>
        Parameters.Where(p => p.Group == group);
}
