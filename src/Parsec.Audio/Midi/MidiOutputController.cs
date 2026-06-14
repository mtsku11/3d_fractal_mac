using System.Collections.Generic;
using Parsec.Audio.Sonification;

namespace Parsec.Audio.Midi;

/// <summary>
/// Translates a <see cref="FractalSonicFrame"/> into MIDI on a virtual source.
///
/// M1 — three core continuous CCs:
///   CC20 Size       = HitRatio          (fraction of view filled by the fractal)
///   CC21 Proximity  = exp(-MeanDepth/k)  (how close the camera is to the surface)
///   CC22 Complexity = NormalVariance·s   (how intricate the surface is)
///
/// M2 — derivative signals (rate of change), as one signed CC plus discrete note
/// events with hysteresis + refractory so they fire as musical gestures, not noise:
///   CC23 Expansion  = d(size)/dt, signed   (64 = steady, &gt;64 growing, &lt;64 shrinking)
///   Note ExpandNote   on a strong positive size-rate   (growing / opening up)
///   Note ContractNote on a strong negative size-rate   (shrinking / pulling away)
///   Note FoldNote     on a ParameterVelocity spike      (folding / morphing)
///   Note SimplifyNote on a strong negative complexity-rate (detail collapsing)
/// Note velocity carries the event magnitude. Each note is released after a short gate.
///
/// Continuous CCs are slewed (one-pole), quantised to 0–127, and only transmitted on
/// change, so the MIDI bus is not flooded.
/// </summary>
public sealed class MidiOutputController
{
    private readonly MidiOutputSession _midi;

    // M1 continuous CCs
    public const int CcSize = 20;
    public const int CcProximity = 21;
    public const int CcComplexity = 22;
    // M2 signed expansion-rate CC
    public const int CcExpansion = 23;
    // M3 palette colour (hue) CC — moves when the visible colour changes
    public const int CcColor = 24;

    /// <summary>MIDI channel 0–15 (0 = channel 1).</summary>
    public int Channel { get; set; }

    /// <summary>Decay constant for the proximity curve exp(-MeanDepth/k).</summary>
    public float ProximityK { get; set; } = 2.5f;

    /// <summary>Scales NormalVariance into a 0–1 complexity reading before quantising.</summary>
    public float ComplexityScale { get; set; } = 8f;

    /// <summary>One-pole smoothing coefficient (0 = frozen, 1 = no smoothing).</summary>
    public float Smoothing { get; set; } = 0.25f;

    // --- M2 event notes (on Channel) ------------------------------------------
    public int ExpandNote { get; set; } = 60;    // C4 — growing / opening up
    public int ContractNote { get; set; } = 62;  // D4 — shrinking / pulling away
    public int FoldNote { get; set; } = 64;      // E4 — folding / morphing
    public int SimplifyNote { get; set; } = 65;  // F4 — detail collapsing

    /// <summary>Size-rate (per second) that maps to full CC23 deflection from centre.</summary>
    public float ExpansionFullScaleRate { get; set; } = 1.0f;
    /// <summary>How long (s) an event note is held before its Note Off.</summary>
    public float NoteGateSec { get; set; } = 0.18f;
    /// <summary>Minimum seconds between successive fires of the same event.</summary>
    public float Refractory { get; set; } = 0.12f;

    // Hysteresis thresholds (high = fire, low = re-arm), in signal units per second.
    public float SizeRateHigh { get; set; } = 0.30f;
    public float SizeRateLow { get; set; } = 0.08f;
    public float ComplexRateHigh { get; set; } = 0.25f;
    public float ComplexRateLow { get; set; } = 0.06f;
    public float FoldHigh { get; set; } = 0.045f;
    public float FoldLow { get; set; } = 0.015f;

    private float _sSize, _sProx, _sComplex, _sSizeRate, _sColor;
    private bool _init, _colorInit;
    private int _lastColor = -1;
    private double _prevTime;
    private float _prevSize, _prevComplex;
    private int _lastSize = -1, _lastProx = -1, _lastComplex = -1, _lastExpansion = -1;

    // Event arm flags (hysteresis) + last-fire times (refractory).
    private bool _expandArmed = true, _contractArmed = true, _foldArmed = true, _simplifyArmed = true;
    private double _expandFired, _contractFired, _foldFired, _simplifyFired;

    // Notes currently sounding, with the time their Note Off is due.
    private readonly Dictionary<int, double> _noteOffDue = new();
    private readonly List<int> _offScratch = new();

    /// <summary>Human-readable last-sent values, for an on-screen monitor.</summary>
    public string Monitor { get; private set; } = "size – · prox – · cplx –";

    /// <summary>Most recent event tag (e.g. "fold"), for a transient on-screen flash.</summary>
    public string LastEvent { get; private set; } = "";
    /// <summary>Time of the most recent event (frame time, seconds).</summary>
    public double LastEventTime { get; private set; } = double.NegativeInfinity;

    public MidiOutputController(MidiOutputSession midi) => _midi = midi;

    /// <param name="colorHue01">Optional palette hue (0–1, wraps). When supplied, drives CC24
    /// so a DAW hears the visible colour shift; pass null to leave CC24 untouched.</param>
    public void Update(FractalSonicFrame f, float? colorHue01 = null)
    {
        if (_midi is null || !_midi.IsAvailable || f is null) return;

        float size       = Clamp01(f.HitRatio);
        float prox       = Clamp01(MathF.Exp(-f.MeanDepth / MathF.Max(0.01f, ProximityK)));
        float complexity = Clamp01(f.NormalVariance * ComplexityScale);

        float a = Math.Clamp(Smoothing, 0.01f, 1f);
        if (!_init)
        {
            _sSize = size; _sProx = prox; _sComplex = complexity;
            _prevSize = size; _prevComplex = complexity; _prevTime = f.Time;
            _init = true;
        }
        else
        {
            _sSize    += (size - _sSize) * a;
            _sProx    += (prox - _sProx) * a;
            _sComplex += (complexity - _sComplex) * a;
        }

        // --- M1 continuous CCs ---
        SendCc(CcSize, _sSize, ref _lastSize);
        SendCc(CcProximity, _sProx, ref _lastProx);
        SendCc(CcComplexity, _sComplex, ref _lastComplex);

        // --- M2 derivatives ---
        double now = f.Time;
        float dt = (float)(now - _prevTime);
        // Clamp dt to a sane frame window so a paused/looped clock can't blow up rates.
        dt = Math.Clamp(dt, 1f / 240f, 0.5f);

        float sizeRate    = (_sSize - _prevSize) / dt;
        float complexRate = (_sComplex - _prevComplex) / dt;
        _prevSize = _sSize; _prevComplex = _sComplex; _prevTime = now;

        // Signed expansion CC: slew the rate, map ±FullScaleRate to 0..127 around 64.
        _sSizeRate += (sizeRate - _sSizeRate) * a;
        float norm = Math.Clamp(_sSizeRate / MathF.Max(1e-4f, ExpansionFullScaleRate), -1f, 1f);
        int expQ = 64 + (int)MathF.Round(norm * 63f);
        SendCcRaw(CcExpansion, Math.Clamp(expQ, 0, 127), ref _lastExpansion);

        // Release any notes whose gate has elapsed.
        ReleaseDueNotes(now);

        // Expand: strong positive size-rate.
        DetectEvent(now, sizeRate >= SizeRateHigh, sizeRate <= SizeRateLow,
                    ExpandNote, sizeRate / MathF.Max(1e-4f, SizeRateHigh * 4f),
                    ref _expandArmed, ref _expandFired, "expand");

        // Contract: strong negative size-rate.
        DetectEvent(now, sizeRate <= -SizeRateHigh, sizeRate >= -SizeRateLow,
                    ContractNote, -sizeRate / MathF.Max(1e-4f, SizeRateHigh * 4f),
                    ref _contractArmed, ref _contractFired, "contract");

        // Fold: ParameterVelocity spike (morphing the fractal structure).
        float fold = MathF.Abs(f.ParameterVelocity);
        DetectEvent(now, fold >= FoldHigh, fold <= FoldLow,
                    FoldNote, fold / MathF.Max(1e-4f, FoldHigh * 4f),
                    ref _foldArmed, ref _foldFired, "fold");

        // Simplify: complexity falling fast.
        DetectEvent(now, complexRate <= -ComplexRateHigh, complexRate >= -ComplexRateLow,
                    SimplifyNote, -complexRate / MathF.Max(1e-4f, ComplexRateHigh * 4f),
                    ref _simplifyArmed, ref _simplifyFired, "simplify");

        // --- M3 palette colour (CC24) ---
        if (colorHue01 is float hue)
        {
            hue = Wrap01(hue);
            if (!_colorInit) { _sColor = hue; _colorInit = true; }
            else
            {
                // Hue is circular — slew along the shortest arc so 0.98→0.02 doesn't
                // sweep backwards through the whole wheel.
                float d = hue - _sColor;
                if (d > 0.5f) d -= 1f; else if (d < -0.5f) d += 1f;
                _sColor = Wrap01(_sColor + d * a);
            }
            SendCcRaw(CcColor, (int)MathF.Round(_sColor * 127f), ref _lastColor);
        }

        Monitor = $"size {_lastSize,3} · prox {_lastProx,3} · cplx {_lastComplex,3} · exp {_lastExpansion,3} · col {_lastColor,3}";
    }

    /// <summary>Resets dedup + event state so the next Update retransmits everything.</summary>
    public void Reset()
    {
        _init = false;
        _colorInit = false;
        _lastSize = _lastProx = _lastComplex = -1;
        _lastExpansion = -1;
        _lastColor = -1;
        _sSizeRate = 0f;
        _expandArmed = _contractArmed = _foldArmed = _simplifyArmed = true;
        _expandFired = _contractFired = _foldFired = _simplifyFired = 0;
        // Silence anything still gated.
        foreach (var note in _noteOffDue.Keys) _midi.SendNoteOff(Channel, note);
        _noteOffDue.Clear();
        LastEvent = "";
        LastEventTime = double.NegativeInfinity;
    }

    // Fires a note when `over` && armed && past refractory; re-arms when `under`.
    private void DetectEvent(double now, bool over, bool under, int note, float magnitude01,
                             ref bool armed, ref double lastFire, string tag)
    {
        if (over && armed && (now - lastFire) >= Refractory)
        {
            int vel = 1 + (int)MathF.Round(Clamp01(magnitude01) * 126f);
            // Retrigger cleanly if this note is still gated from a prior fire.
            if (_noteOffDue.ContainsKey(note)) _midi.SendNoteOff(Channel, note);
            _midi.SendNoteOn(Channel, note, vel);
            _noteOffDue[note] = now + NoteGateSec;
            armed = false;
            lastFire = now;
            LastEvent = tag;
            LastEventTime = now;
        }
        else if (under)
        {
            armed = true;
        }
    }

    private void ReleaseDueNotes(double now)
    {
        if (_noteOffDue.Count == 0) return;
        _offScratch.Clear();
        foreach (var kv in _noteOffDue)
            if (now >= kv.Value) _offScratch.Add(kv.Key);
        foreach (var note in _offScratch)
        {
            _midi.SendNoteOff(Channel, note);
            _noteOffDue.Remove(note);
        }
    }

    private void SendCc(int cc, float v01, ref int last)
        => SendCcRaw(cc, (int)MathF.Round(Clamp01(v01) * 127f), ref last);

    private void SendCcRaw(int cc, int q, ref int last)
    {
        if (q == last) return;
        if (_midi.SendControlChange(Channel, cc, q)) last = q;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static float Wrap01(float v)
    {
        v %= 1f;
        return v < 0f ? v + 1f : v;
    }

    /// <summary>HSV hue (0–1) of an RGB triple — the "what colour is it" scalar for CC24.
    /// Grey/black returns 0.</summary>
    public static float Hue01(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float c = max - min;
        if (c < 1e-6f) return 0f;
        float h;
        if (max == r)      h = ((g - b) / c) % 6f;
        else if (max == g) h = (b - r) / c + 2f;
        else               h = (r - g) / c + 4f;
        h /= 6f;
        return h < 0f ? h + 1f : h;
    }
}
