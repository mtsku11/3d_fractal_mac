using Parsec.Audio.Sonification;

namespace Parsec.Audio.Midi;

/// <summary>
/// Translates a <see cref="FractalSonicFrame"/> into MIDI control changes on a
/// virtual source. M1 (proof-of-pipe) maps the three core continuous signals:
///
///   CC20 Size       = HitRatio          (fraction of view filled by the fractal)
///   CC21 Proximity  = exp(-MeanDepth/k)  (how close the camera is to the surface)
///   CC22 Complexity = NormalVariance·s   (how intricate the surface is)
///
/// Each signal is lightly slewed (one-pole) to suppress jitter, quantised to 0–127,
/// and only transmitted when its quantised value changes — so the MIDI bus is not
/// flooded with redundant identical CCs.
/// </summary>
public sealed class MidiOutputController
{
    private readonly MidiOutputSession _midi;

    public const int CcSize = 20;
    public const int CcProximity = 21;
    public const int CcComplexity = 22;

    /// <summary>MIDI channel 0–15 (0 = channel 1).</summary>
    public int Channel { get; set; }

    /// <summary>Decay constant for the proximity curve exp(-MeanDepth/k).</summary>
    public float ProximityK { get; set; } = 2.5f;

    /// <summary>Scales NormalVariance into a 0–1 complexity reading before quantising.</summary>
    public float ComplexityScale { get; set; } = 8f;

    /// <summary>One-pole smoothing coefficient (0 = frozen, 1 = no smoothing).</summary>
    public float Smoothing { get; set; } = 0.25f;

    private float _sSize, _sProx, _sComplex;
    private bool _init;
    private int _lastSize = -1, _lastProx = -1, _lastComplex = -1;

    /// <summary>Human-readable last-sent values, for an on-screen monitor.</summary>
    public string Monitor { get; private set; } = "size – · prox – · cplx –";

    public MidiOutputController(MidiOutputSession midi) => _midi = midi;

    public void Update(FractalSonicFrame f)
    {
        if (_midi is null || !_midi.IsAvailable || f is null) return;

        float size       = Clamp01(f.HitRatio);
        float prox       = Clamp01(MathF.Exp(-f.MeanDepth / MathF.Max(0.01f, ProximityK)));
        float complexity = Clamp01(f.NormalVariance * ComplexityScale);

        float a = Math.Clamp(Smoothing, 0.01f, 1f);
        if (!_init) { _sSize = size; _sProx = prox; _sComplex = complexity; _init = true; }
        else
        {
            _sSize    += (size - _sSize) * a;
            _sProx    += (prox - _sProx) * a;
            _sComplex += (complexity - _sComplex) * a;
        }

        SendCc(CcSize, _sSize, ref _lastSize);
        SendCc(CcProximity, _sProx, ref _lastProx);
        SendCc(CcComplexity, _sComplex, ref _lastComplex);

        Monitor = $"size {_lastSize,3} · prox {_lastProx,3} · cplx {_lastComplex,3}";
    }

    /// <summary>Resets dedup state so the next Update retransmits all CCs (use on enable).</summary>
    public void Reset()
    {
        _init = false;
        _lastSize = _lastProx = _lastComplex = -1;
    }

    private void SendCc(int cc, float v01, ref int last)
    {
        int q = (int)MathF.Round(Clamp01(v01) * 127f);
        if (q == last) return;
        if (_midi.SendControlChange(Channel, cc, q)) last = q;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
