namespace Parsec.Audio;

public sealed class AudioFeatureTrack
{
    private readonly IReadOnlyList<AudioFeatureFrame> _frames;

    public AudioFeatureTrack(TimeSpan duration, IReadOnlyList<AudioFeatureFrame> frames)
    {
        Duration = duration;
        _frames = frames.Count == 0
            ? new[] { new AudioFeatureFrame(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0) }
            : frames;
    }

    public TimeSpan Duration { get; }
    public IReadOnlyList<AudioFeatureFrame> Frames => _frames;

    public AudioFeatureFrame Sample(TimeSpan position)
    {
        if (_frames.Count == 1)
            return _frames[0];

        if (position <= _frames[0].Time)
            return _frames[0];

        var lastFrame = _frames[^1];
        if (position >= lastFrame.Time)
            return lastFrame;

        int low = 0;
        int high = _frames.Count - 1;
        while (high - low > 1)
        {
            int mid = low + ((high - low) / 2);
            if (_frames[mid].Time <= position)
                low = mid;
            else
                high = mid;
        }

        AudioFeatureFrame a = _frames[low];
        AudioFeatureFrame b = _frames[high];
        double spanSeconds = (b.Time - a.Time).TotalSeconds;
        if (spanSeconds <= 0)
            return a;

        double t = (position - a.Time).TotalSeconds / spanSeconds;
        return new AudioFeatureFrame(
            Time: position,
            Window: TimeSpan.FromSeconds(Lerp(a.Window.TotalSeconds, b.Window.TotalSeconds, t)),
            Rms: Lerp(a.Rms, b.Rms, t),
            Peak: Lerp(a.Peak, b.Peak, t),
            BassEnergy: Lerp(a.BassEnergy, b.BassEnergy, t),
            MidEnergy: Lerp(a.MidEnergy, b.MidEnergy, t),
            TrebleEnergy: Lerp(a.TrebleEnergy, b.TrebleEnergy, t),
            SpectrumCentroidHz: Lerp(a.SpectrumCentroidHz, b.SpectrumCentroidHz, t),
            OnsetStrength: Lerp(a.OnsetStrength, b.OnsetStrength, t));
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
