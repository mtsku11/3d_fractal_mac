using Avalonia.Threading;
using Parsec.Audio;

namespace Parsec.App;

/// <summary>
/// Runs offline analysis when a WAV loads, then applies audio feature values
/// as additive offsets to fractal parameters on each frame tick.
///
/// Live preview: call Tick() from a UI timer (~60Hz). Returns true when
/// modulation was applied so the caller can MarkDirty().
///
/// Batch export: call ApplyAtTime(t) after timeline.ApplyAtTime(0, t) so the
/// audio offset is layered on top of the keyframe-interpolated base values.
/// </summary>
public sealed class AudioModulationController
{
    private readonly AudioTransportController _transport;
    private readonly WaveAudioAnalyzer _analyzer = new();
    private AudioFeatureTrack? _track;
    private Uri? _analysisSource;
    private CancellationTokenSource? _analysisCts;

    private readonly List<AudioModulationMapping> _mappings = new();

    // Delta-tracking for live preview: last applied offset per descriptor.
    // Lets us recover the base value as: base = current - lastDelta.
    private readonly Dictionary<ParamDescriptor, double> _lastDelta = new();

    public event Action<AudioModulationController>? Changed;

    public bool HasTrack => _track != null;
    public bool IsAnalyzing { get; private set; }
    public string AnalysisStatus { get; private set; } = string.Empty;

    /// <summary>URI of the last successfully analyzed file, for ffmpeg muxing.</summary>
    public Uri? TrackSource { get; private set; }

    public IReadOnlyList<AudioModulationMapping> Mappings => _mappings;

    public AudioModulationController(AudioTransportController transport)
    {
        _transport = transport;
        transport.StateChanged += OnTransportStateChanged;
    }

    public void AddMapping(ParamDescriptor target, AudioFeatureSource source)
    {
        _mappings.Add(new AudioModulationMapping { Target = target, Source = source });
        RaiseChanged();
    }

    public void RemoveMapping(AudioModulationMapping mapping)
    {
        // Reset this param to its unmodulated value before removing.
        if (_lastDelta.TryGetValue(mapping.Target, out double delta) && delta != 0)
        {
            double restored = Math.Clamp(mapping.Target.Get() - delta,
                mapping.Target.Min, mapping.Target.Max);
            mapping.Target.Set(restored);
            _lastDelta.Remove(mapping.Target);
        }
        _mappings.Remove(mapping);
        RaiseChanged();
    }

    public void ClearMappings()
    {
        ResetAllToBase();
        _mappings.Clear();
        _lastDelta.Clear();
        RaiseChanged();
    }

    /// <summary>
    /// Called when the user manually moves a parameter slider. Clears the
    /// last-applied deltas so the new slider position becomes the new base.
    /// </summary>
    public void OnManualParamChanged() => _lastDelta.Clear();

    /// <summary>
    /// Live-preview tick. Returns true if any modulation was applied (caller
    /// should MarkDirty). If audio is not playing, resets params to base.
    /// </summary>
    public bool Tick()
    {
        if (_mappings.Count == 0) return false;

        var state = _transport.State;
        if (state.PlaybackStatus != AudioPlaybackStatus.Playing)
        {
            if (_lastDelta.Count > 0) ResetAllToBase();
            return false;
        }

        if (_track == null) return false;

        return ApplyDelta(_track.Sample(state.Position));
    }

    /// <summary>
    /// Offline/deterministic apply for batch export. Reads from <paramref name="t"/>
    /// directly (not the live transport position). Assumes the caller has already
    /// set base param values via the timeline interpolator.
    /// </summary>
    public void ApplyAtTime(TimeSpan t)
    {
        if (_track == null || _mappings.Count == 0) return;

        var frame = _track.Sample(t);
        foreach (var m in _mappings)
        {
            if (!m.Enabled) continue;
            // Base value was just written by timeline; no delta tracking needed.
            double baseVal = m.Target.Get();
            double feature = GetFeatureValue(frame, m.Source);
            double range = m.Target.Max - m.Target.Min;
            double delta = feature * m.Depth * range;
            m.Target.Set(Math.Clamp(baseVal + delta, m.Target.Min, m.Target.Max));
        }
    }

    // --- internals ---

    private bool ApplyDelta(AudioFeatureFrame frame)
    {
        bool applied = false;
        foreach (var m in _mappings)
        {
            if (!m.Enabled) continue;

            _lastDelta.TryGetValue(m.Target, out double prevDelta);
            double baseVal = m.Target.Get() - prevDelta;
            double feature = GetFeatureValue(frame, m.Source);
            double range = m.Target.Max - m.Target.Min;
            double delta = feature * m.Depth * range;
            double newVal = Math.Clamp(baseVal + delta, m.Target.Min, m.Target.Max);
            m.Target.Set(newVal);
            _lastDelta[m.Target] = delta;
            applied = true;
        }
        return applied;
    }

    private void ResetAllToBase()
    {
        foreach (var (desc, delta) in _lastDelta)
        {
            if (delta != 0)
                desc.Set(Math.Clamp(desc.Get() - delta, desc.Min, desc.Max));
        }
        _lastDelta.Clear();
    }

    private static double GetFeatureValue(AudioFeatureFrame frame, AudioFeatureSource source) =>
        source switch
        {
            AudioFeatureSource.Rms => frame.Rms,
            AudioFeatureSource.BassEnergy => frame.BassEnergy,
            AudioFeatureSource.MidEnergy => frame.MidEnergy,
            AudioFeatureSource.TrebleEnergy => frame.TrebleEnergy,
            AudioFeatureSource.OnsetStrength => Math.Min(frame.OnsetStrength, 1.0),
            AudioFeatureSource.SpectrumCentroid => frame.SpectrumCentroidHz > 0
                ? Math.Min(frame.SpectrumCentroidHz / 10000.0, 1.0) : 0.0,
            _ => 0.0,
        };

    private void OnTransportStateChanged(AudioTransportState state)
    {
        if (state.Source == _analysisSource) return;
        _analysisSource = state.Source;

        if (state.Source == null)
        {
            _track = null;
            TrackSource = null;
            AnalysisStatus = string.Empty;
            RaiseChanged();
            return;
        }

        _ = StartAnalysisAsync(state.Source);
    }

    private async Task StartAnalysisAsync(Uri source)
    {
        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        var cts = _analysisCts;

        _track = null;
        IsAnalyzing = true;
        AnalysisStatus = "Analyzing audio…";
        RaiseChanged();

        try
        {
            var track = await _analyzer.AnalyzeAsync(source, null, cts.Token);
            if (cts.IsCancellationRequested) return;

            _track = track;
            TrackSource = source;
            IsAnalyzing = false;
            AnalysisStatus = $"Ready ({track.Frames.Count} frames)";
            RaiseChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            IsAnalyzing = false;
            AnalysisStatus = $"Analysis failed: {ex.Message}";
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Changed?.Invoke(this);
        else
            Dispatcher.UIThread.Post(() => Changed?.Invoke(this));
    }
}
