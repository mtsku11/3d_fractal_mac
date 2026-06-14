using System.Collections.Generic;
using System.Numerics;
using Parsec.Audio.Sonification;

namespace Parsec.Audio.Midi;

/// <summary>
/// Translates a <see cref="FractalSonicFrame"/> into MIDI on a virtual source so the fractal
/// view becomes a control surface for external instruments (image ↔ sound). Everything is on
/// one channel (<see cref="Channel"/>); CCs and notes never collide.
///
/// Continuous CCs (slewed, quantised, sent only on change):
///   M1  CC20 Size       HitRatio              CC21 Proximity  exp(-depth/k)   CC22 Complexity NormalVariance
///   M2  CC23 Expansion  d(size)/dt (signed)
///   M3  CC24 Colour     on-screen hue (real frame colour; palette base hue as fallback)
///   I1  CC35 Saturation on-screen saturation   CC36 Brightness on-screen value/luma
///   M4  CC25 Layering   DepthVariance         CC26 Haze       StepMean
///       CC27 Verticality NormalMean.y (signed) CC28 Speed     CameraSpeed     CC29 Dolly      ZoomVelocity (signed)
///       CC30 PositionX  energy centroid (signed) CC31 PositionY energy centroid (signed)
///       CC32 Dispersion spatial spread          CC33 Structure TrapMean.x      CC34 Heterogeneity TrapVariance
///
/// Note events (velocity = magnitude, hysteresis + refractory + auto note-off):
///   M2  60 Expand · 62 Contract · 64 Fold · 65 Simplify
///   M4  67 Enclose (enter tunnel/cavern) · 69 Emerge (open space) · 71 Shimmer (detail burst)
///   M4  36–51  spatial "object" notes — one per 4×4 cell; fires when that screen region lights up.
/// </summary>
public sealed class MidiOutputController
{
    private readonly MidiOutputSession _midi;

    // M1–M3 continuous CCs
    public const int CcSize = 20;
    public const int CcProximity = 21;
    public const int CcComplexity = 22;
    public const int CcExpansion = 23;     // signed
    public const int CcColor = 24;
    // M4 continuous CCs (index order matches _s/_last arrays below)
    public const int CcLayering = 25;
    public const int CcHaze = 26;
    public const int CcVerticality = 27;   // signed
    public const int CcSpeed = 28;
    public const int CcDolly = 29;         // signed
    public const int CcPositionX = 30;     // signed
    public const int CcPositionY = 31;     // signed
    public const int CcDispersion = 32;
    public const int CcStructure = 33;
    public const int CcHeterogeneity = 34;
    // Improvement 1 — real on-screen colour (from the rendered frame buffer, not palette base).
    public const int CcSaturation = 35;
    public const int CcBrightness = 36;

    /// <summary>MIDI channel 0–15 (0 = channel 1).</summary>
    public int Channel { get; set; }
    public float ProximityK { get; set; } = 2.5f;
    public float ComplexityScale { get; set; } = 8f;
    public float Smoothing { get; set; } = 0.25f;

    // --- Event notes (on Channel) ---
    public int ExpandNote { get; set; } = 60;
    public int ContractNote { get; set; } = 62;
    public int FoldNote { get; set; } = 64;
    public int SimplifyNote { get; set; } = 65;
    public int EncloseNote { get; set; } = 67;
    public int EmergeNote { get; set; } = 69;
    public int ShimmerNote { get; set; } = 71;
    /// <summary>Base note for the 16 spatial cells (cell i → SpatialNoteBase + i).</summary>
    public int SpatialNoteBase { get; set; } = 36;

    public float ExpansionFullScaleRate { get; set; } = 1.0f;
    public float NoteGateSec { get; set; } = 0.18f;
    public float Refractory { get; set; } = 0.12f;

    // Hysteresis thresholds.
    public float SizeRateHigh { get; set; } = 0.30f;
    public float SizeRateLow { get; set; } = 0.08f;
    public float ComplexRateHigh { get; set; } = 0.25f;
    public float ComplexRateLow { get; set; } = 0.06f;
    public float FoldHigh { get; set; } = 0.045f;
    public float FoldLow { get; set; } = 0.015f;
    public float EncloseHigh { get; set; } = 0.72f;
    public float EncloseLow { get; set; } = 0.55f;
    public float EmergeLow { get; set; } = 0.28f;
    public float EmergeHigh { get; set; } = 0.45f;
    public float ShimmerHigh { get; set; } = 0.70f;
    public float ShimmerLow { get; set; } = 0.45f;
    public float RegionHigh { get; set; } = 0.12f;
    public float RegionLow { get; set; } = 0.05f;
    public float RegionGate { get; set; } = 0.35f;   // spatial notes ring a bit longer

    // Normalisation gains for the M4 CCs (geometry units → 0..1 / ±1). Tunable; the smoke
    // test exercises the pipeline, real tuning happens against live flying.
    public float LayeringGain { get; set; } = 2.0f;
    public float HazeFullSteps { get; set; } = 64f;
    public float VerticalityGain { get; set; } = 1.5f;
    public float SpeedFull { get; set; } = 2.0f;
    public float DollyFull { get; set; } = 2.0f;
    public float StructureFull { get; set; } = 2.0f;
    public float HeterogeneityFull { get; set; } = 2.0f;

    // M1 smoothed signals + dedup
    private float _sSize, _sProx, _sComplex, _sSizeRate, _sColor;
    private bool _init, _colorInit;
    private int _lastColor = -1;
    // Improvement 1 — saturation/brightness of the real on-screen colour.
    private float _sSat, _sVal;
    private bool _svInit;
    private int _lastSat = -1, _lastVal = -1;
    private double _prevTime;
    private float _prevSize, _prevComplex;
    private int _lastSize = -1, _lastProx = -1, _lastComplex = -1, _lastExpansion = -1;

    // M4 CCs: parallel smoothed + last-sent arrays (index = CC# - 25)
    private const int NCC = 10;
    private readonly float[] _s = new float[NCC];
    private readonly int[] _lastCc = new int[NCC];
    private bool _ccInit;

    // Gesture-event arm/fire state.
    private bool _expandArmed = true, _contractArmed = true, _foldArmed = true, _simplifyArmed = true;
    private bool _encloseArmed = true, _emergeArmed = true, _shimmerArmed = true;
    private double _expandFired, _contractFired, _foldFired, _simplifyFired, _encloseFired, _emergeFired, _shimmerFired;

    // Spatial region arm/fire state (16 cells).
    private const int NCells = 16;
    private readonly bool[] _regionArmed = new bool[NCells];
    private readonly double[] _regionFired = new double[NCells];
    private bool _spatialInit;
    private int _activeRegions;

    // Notes currently sounding, with the time their Note Off is due.
    private readonly Dictionary<int, double> _noteOffDue = new();
    private readonly List<int> _offScratch = new();

    public string Monitor { get; private set; } = "size – · prox – · cplx –";
    public string LastEvent { get; private set; } = "";
    public double LastEventTime { get; private set; } = double.NegativeInfinity;

    public MidiOutputController(MidiOutputSession midi)
    {
        _midi = midi;
        for (int i = 0; i < NCC; i++) _lastCc[i] = -1;
    }

    /// <param name="colorHue01">Optional colour hue (0–1, wraps) → CC24. When the caller has a
    /// rendered frame buffer this is the real on-screen hue; otherwise the palette base hue.
    /// Null leaves CC24 untouched.</param>
    /// <param name="colorSat01">Optional on-screen saturation (0–1) → CC35. Null leaves it untouched.</param>
    /// <param name="colorVal01">Optional on-screen brightness/value (0–1) → CC36. Null leaves it untouched.</param>
    public void Update(FractalSonicFrame f, float? colorHue01 = null, float? colorSat01 = null, float? colorVal01 = null)
    {
        if (_midi is null || !_midi.IsAvailable || f is null) return;

        float a = Math.Clamp(Smoothing, 0.01f, 1f);
        double now = f.Time;

        float size       = Clamp01(f.HitRatio);
        float prox       = Clamp01(MathF.Exp(-f.MeanDepth / MathF.Max(0.01f, ProximityK)));
        float complexity = Clamp01(f.NormalVariance * ComplexityScale);

        if (!_init)
        {
            _sSize = size; _sProx = prox; _sComplex = complexity;
            _prevSize = size; _prevComplex = complexity; _prevTime = now;
            _init = true;
        }
        else
        {
            _sSize    += (size - _sSize) * a;
            _sProx    += (prox - _sProx) * a;
            _sComplex += (complexity - _sComplex) * a;
        }

        SendCc(CcSize, _sSize, ref _lastSize);
        SendCc(CcProximity, _sProx, ref _lastProx);
        SendCc(CcComplexity, _sComplex, ref _lastComplex);

        // --- M2 derivatives ---
        float dt = Math.Clamp((float)(now - _prevTime), 1f / 240f, 0.5f);
        float sizeRate    = (_sSize - _prevSize) / dt;
        float complexRate = (_sComplex - _prevComplex) / dt;
        _prevSize = _sSize; _prevComplex = _sComplex; _prevTime = now;

        _sSizeRate += (sizeRate - _sSizeRate) * a;
        float expNorm = Math.Clamp(_sSizeRate / MathF.Max(1e-4f, ExpansionFullScaleRate), -1f, 1f);
        SendCcRaw(CcExpansion, 64 + (int)MathF.Round(expNorm * 63f), ref _lastExpansion);

        ReleaseDueNotes(now);

        DetectEvent(now, sizeRate >= SizeRateHigh, sizeRate <= SizeRateLow,
                    ExpandNote, sizeRate / MathF.Max(1e-4f, SizeRateHigh * 4f),
                    ref _expandArmed, ref _expandFired, "expand", NoteGateSec, true);
        DetectEvent(now, sizeRate <= -SizeRateHigh, sizeRate >= -SizeRateLow,
                    ContractNote, -sizeRate / MathF.Max(1e-4f, SizeRateHigh * 4f),
                    ref _contractArmed, ref _contractFired, "contract", NoteGateSec, true);
        float fold = MathF.Abs(f.ParameterVelocity);
        DetectEvent(now, fold >= FoldHigh, fold <= FoldLow,
                    FoldNote, fold / MathF.Max(1e-4f, FoldHigh * 4f),
                    ref _foldArmed, ref _foldFired, "fold", NoteGateSec, true);
        DetectEvent(now, complexRate <= -ComplexRateHigh, complexRate >= -ComplexRateLow,
                    SimplifyNote, -complexRate / MathF.Max(1e-4f, ComplexRateHigh * 4f),
                    ref _simplifyArmed, ref _simplifyFired, "simplify", NoteGateSec, true);

        // --- M3 colour ---
        if (colorHue01 is float hue)
        {
            hue = Wrap01(hue);
            if (!_colorInit) { _sColor = hue; _colorInit = true; }
            else
            {
                float d = hue - _sColor;
                if (d > 0.5f) d -= 1f; else if (d < -0.5f) d += 1f;
                _sColor = Wrap01(_sColor + d * a);
            }
            SendCcRaw(CcColor, (int)MathF.Round(_sColor * 127f), ref _lastColor);
        }

        // Improvement 1 — real on-screen saturation + brightness (linear slew, not circular).
        if (colorSat01 is float sat01 && colorVal01 is float val01)
        {
            sat01 = Clamp01(sat01); val01 = Clamp01(val01);
            if (!_svInit) { _sSat = sat01; _sVal = val01; _svInit = true; }
            else { _sSat += (sat01 - _sSat) * a; _sVal += (val01 - _sVal) * a; }
            SendCc(CcSaturation, _sSat, ref _lastSat);
            SendCc(CcBrightness, _sVal, ref _lastVal);
        }

        // --- M4 motion / structure / spatial ---
        // Spatial aggregates from the 4×4 cell grid.
        float cx = 0f, cy = 0f, dispersion = 0f;
        var cells = f.Cells;
        bool haveCells = cells != null && cells.Length == NCells;
        if (haveCells)
        {
            float wsum = 0f;
            for (int i = 0; i < NCells; i++)
            {
                float e = MathF.Max(0f, cells![i].Energy);
                float colN = (i % 4) / 1.5f - 1f;      // -1, -0.333, 0.333, 1
                float rowN = 1f - (i / 4) / 1.5f;      // top +1 → bottom -1
                cx += e * colN; cy += e * rowN; wsum += e;
            }
            if (wsum > 1e-5f)
            {
                cx /= wsum; cy /= wsum;
                float varSum = 0f;
                for (int i = 0; i < NCells; i++)
                {
                    float e = MathF.Max(0f, cells![i].Energy);
                    float colN = (i % 4) / 1.5f - 1f;
                    float rowN = 1f - (i / 4) / 1.5f;
                    varSum += e * ((colN - cx) * (colN - cx) + (rowN - cy) * (rowN - cy));
                }
                dispersion = Clamp01(MathF.Sqrt(varSum / wsum) / 1.414f);
            }
        }

        float haze = Clamp01(f.StepMean / MathF.Max(1f, HazeFullSteps));
        float layering = Clamp01(MathF.Sqrt(MathF.Max(0f, f.DepthVariance)) / MathF.Max(0.01f, f.MeanDepth) * LayeringGain);

        EmitCc(0, CcLayering,      layering,                                                false, a);
        EmitCc(1, CcHaze,          haze,                                                    false, a);
        EmitCc(2, CcVerticality,   Clamp(f.NormalMean.Y * VerticalityGain, -1f, 1f),        true,  a);
        EmitCc(3, CcSpeed,         Clamp01(f.CameraSpeed / MathF.Max(1e-3f, SpeedFull)),    false, a);
        EmitCc(4, CcDolly,         Clamp(f.ZoomVelocity / MathF.Max(1e-3f, DollyFull), -1f, 1f), true, a);
        EmitCc(5, CcPositionX,     Clamp(cx, -1f, 1f),                                       true,  a);
        EmitCc(6, CcPositionY,     Clamp(cy, -1f, 1f),                                       true,  a);
        EmitCc(7, CcDispersion,    dispersion,                                               false, a);
        EmitCc(8, CcStructure,     Clamp01(f.TrapMean.X / MathF.Max(1e-3f, StructureFull)),  false, a);
        EmitCc(9, CcHeterogeneity, Clamp01(f.TrapVariance.Length() / MathF.Max(1e-3f, HeterogeneityFull)), false, a);
        _ccInit = true;

        // Gesture notes: enclosure / emergence / shimmer.
        DetectEvent(now, size >= EncloseHigh, size <= EncloseLow, EncloseNote, size,
                    ref _encloseArmed, ref _encloseFired, "enclose", NoteGateSec, true);
        DetectEvent(now, size <= EmergeLow, size >= EmergeHigh, EmergeNote, 1f - size,
                    ref _emergeArmed, ref _emergeFired, "emerge", NoteGateSec, true);
        DetectEvent(now, haze >= ShimmerHigh, haze <= ShimmerLow, ShimmerNote, haze,
                    ref _shimmerArmed, ref _shimmerFired, "shimmer", NoteGateSec, true);

        // Spatial "object" notes — one per cell, fired on energy onset.
        if (haveCells)
        {
            if (!_spatialInit)
            {
                // Don't blast a 16-note chord for regions already lit when MIDI is enabled;
                // arm only the cells that are currently quiet (they'll fire when they light up).
                for (int i = 0; i < NCells; i++)
                    _regionArmed[i] = cells![i].Energy <= RegionLow;
                _spatialInit = true;
            }
            int active = 0;
            for (int i = 0; i < NCells; i++)
            {
                float e = MathF.Max(0f, cells![i].Energy);
                if (e >= RegionHigh) active++;
                DetectEvent(now, e >= RegionHigh, e <= RegionLow,
                            SpatialNoteBase + i, e / MathF.Max(1e-3f, RegionHigh * 4f),
                            ref _regionArmed[i], ref _regionFired[i], "object", RegionGate, false);
            }
            _activeRegions = active;
        }

        Monitor = $"size {_lastSize,3} · prox {_lastProx,3} · cplx {_lastComplex,3} · exp {_lastExpansion,3} · col {_lastColor,3}"
                + $" · pos {_lastCc[5],3},{_lastCc[6],3} · spd {_lastCc[3],3} · obj {_activeRegions,2}";
    }

    public void Reset()
    {
        _init = false; _colorInit = false; _ccInit = false; _spatialInit = false;
        _svInit = false;
        _lastSize = _lastProx = _lastComplex = -1;
        _lastExpansion = -1; _lastColor = -1; _lastSat = -1; _lastVal = -1;
        _sSizeRate = 0f; _activeRegions = 0;
        for (int i = 0; i < NCC; i++) { _s[i] = 0f; _lastCc[i] = -1; }
        _expandArmed = _contractArmed = _foldArmed = _simplifyArmed = true;
        _encloseArmed = _emergeArmed = _shimmerArmed = true;
        _expandFired = _contractFired = _foldFired = _simplifyFired = 0;
        _encloseFired = _emergeFired = _shimmerFired = 0;
        for (int i = 0; i < NCells; i++) { _regionArmed[i] = true; _regionFired[i] = 0; }
        foreach (var note in _noteOffDue.Keys) _midi.SendNoteOff(Channel, note);
        _noteOffDue.Clear();
        LastEvent = ""; LastEventTime = double.NegativeInfinity;
    }

    // Smooth then emit a continuous CC. signed → mapped around 64; else 0..127.
    private void EmitCc(int idx, int cc, float raw, bool signed, float a)
    {
        _s[idx] = _ccInit ? _s[idx] + (raw - _s[idx]) * a : raw;
        int q = signed
            ? 64 + (int)MathF.Round(Clamp(_s[idx], -1f, 1f) * 63f)
            : (int)MathF.Round(Clamp01(_s[idx]) * 127f);
        SendCcRaw(cc, Math.Clamp(q, 0, 127), ref _lastCc[idx]);
    }

    private void DetectEvent(double now, bool over, bool under, int note, float magnitude01,
                             ref bool armed, ref double lastFire, string tag, float gate, bool flash)
    {
        if (over && armed && (now - lastFire) >= Refractory)
        {
            int vel = 1 + (int)MathF.Round(Clamp01(magnitude01) * 126f);
            if (_noteOffDue.ContainsKey(note)) _midi.SendNoteOff(Channel, note);
            _midi.SendNoteOn(Channel, note, vel);
            _noteOffDue[note] = now + gate;
            armed = false;
            lastFire = now;
            if (flash) { LastEvent = tag; LastEventTime = now; }
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
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Wrap01(float v)
    {
        v %= 1f;
        return v < 0f ? v + 1f : v;
    }

    /// <summary>HSV hue (0–1) of an RGB triple — the "what colour is it" scalar for CC24.</summary>
    public static float Hue01(float r, float g, float b) => RgbToHsv(r, g, b).h;

    /// <summary>RGB (0–1) → HSV (hue 0–1 wraps, sat 0–1, val 0–1).</summary>
    public static (float h, float s, float v) RgbToHsv(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float c = max - min;
        float h = 0f;
        if (c >= 1e-6f)
        {
            if (max == r)      h = ((g - b) / c) % 6f;
            else if (max == g) h = (b - r) / c + 2f;
            else               h = (r - g) / c + 4f;
            h /= 6f;
            if (h < 0f) h += 1f;
        }
        float s = max <= 1e-6f ? 0f : c / max;
        return (h, s, max);
    }

    /// <summary>
    /// Coverage-masked mean colour of a rendered RGBA8 frame buffer, as HSV. Background pixels
    /// (all channels below <paramref name="bgThreshold"/>) are skipped so the void doesn't wash
    /// the average out; the rest are sampled every <paramref name="step"/>th pixel (≪1 ms).
    /// Returns (0,0,0) when the frame is empty or fully background. uint layout is RGBA
    /// little-endian (R = low byte), matching the Metal renderers' output.
    /// </summary>
    public static (float h, float s, float v) MeanScreenColorHsv(
        uint[] pixels, int width, int height, int step = 4, int bgThreshold = 12)
    {
        if (pixels == null || pixels.Length == 0) return (0f, 0f, 0f);
        if (step < 1) step = 1;
        int total = width * height;
        if (total <= 0 || total > pixels.Length) total = pixels.Length;
        long sr = 0, sg = 0, sb = 0, n = 0;
        for (int i = 0; i < total; i += step)
        {
            uint p = pixels[i];
            int r = (int)(p & 0xFF);
            int g = (int)((p >> 8) & 0xFF);
            int b = (int)((p >> 16) & 0xFF);
            if (r < bgThreshold && g < bgThreshold && b < bgThreshold) continue;
            sr += r; sg += g; sb += b; n++;
        }
        if (n == 0) return (0f, 0f, 0f);
        return RgbToHsv(sr / (255f * n), sg / (255f * n), sb / (255f * n));
    }
}
