using System.Collections.Generic;

namespace Parsec.Audio.Midi;

/// <summary>
/// Offline 8-voice multitimbral synth that plays the EXACT geometry→MIDI stream a
/// <see cref="MidiOutputController"/> emits (captured via the session's observer hooks). Each
/// instrument is driven by a distinct slice of the map, so the audio and the fractal motion stay
/// unified. No external soundfont — every timbre is synthesised here.
///
/// Instruments (← driver):
///   1 Sub bass    ← CC20 Size (gain) · CC21 Proximity (octave)
///   2 Warm pad    ← CC25 Layering (gain) · CC22 Complexity (LPF)
///   3 Lead        ← CC30 PositionX (pitch+pan) · CC36 Brightness (gain)
///   4 Choir       ← CC24 Colour (vowel) · CC35 Saturation (gain)
///   5 Cello drone ← CC21 Proximity (gain) · CC29 Dolly (vibrato)
///   6 Glass bells ← ch2 fine 8×6 region notes (36–83)
///   7 Marimba     ← ch1 4×4 region notes (36–51)
///   8 Perc kit    ← ch1 gesture notes (60 Expand/62 Contract/64 Fold = drums; 67/69/71 = cymbals)
/// </summary>
public static class MidiInstrumentSynth
{
    public const int SampleRate = 44100;

    public enum EvType { Cc, NoteOn, NoteOff }
    public readonly record struct Ev(double Time, EvType Type, int Channel, int D1, int D2);

    // D-minor pentatonic across registers (Hz): D F G A C. Index 0 = D2.
    private static readonly float[] Scale = BuildScale();

    private static float[] BuildScale()
    {
        // semitone offsets of D-minor pentatonic within an octave: D(0) F(3) G(5) A(7) C(10)
        int[] deg = { 0, 3, 5, 7, 10 };
        var list = new List<float>();
        for (int oct = 0; oct < 5; oct++)            // D2..~D6
            foreach (int d in deg)
                list.Add(73.4162f * MathF.Pow(2f, (oct * 12 + d) / 12f)); // 73.42 = D2
        return list.ToArray();
    }

    private static float ScaleHz(int idx) => Scale[Math.Clamp(idx, 0, Scale.Length - 1)];

    public static short[] Synthesize(IReadOnlyList<Ev> events, double duration)
    {
        int n = (int)(duration * SampleRate);
        int total = n * 2;
        var outBuf = new short[total];

        // Continuous CC state (all continuous CCs are on channel 0). Smoothed per sample.
        var cc = new float[128];     // raw 0..1 (signed centred at 0.5)
        var ccS = new float[128];
        // Sensible starting values so the bed isn't silent before the first CC arrives.
        cc[20] = 0.3f; cc[21] = 0.4f; cc[24] = 0.5f; cc[25] = 0.3f; cc[35] = 0.4f; cc[36] = 0.5f;
        cc[30] = 0.5f; cc[29] = 0.5f;
        Array.Copy(cc, ccS, 128);

        // Voice pools for triggered instruments.
        var bells = new Pluck[24];
        var marimba = new Pluck[16];
        var drums = new Drum[24];
        int bellRR = 0, marRR = 0, drumRR = 0;

        // Continuous oscillator phases.
        float pBass = 0, pBass2 = 0;
        float[] pPad = new float[3];
        float pLead = 0, pLeadV = 0;
        float[] pChoir = new float[4];
        float pCello = 0, pCelloV = 0;
        float leadHzS = 220f;
        float padLp = 0, celloLp = 0, hazeLp = 0;
        uint rng = 0x1234567u;

        var rev = new Reverb();
        int ei = 0;

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;

            // Apply due events.
            while (ei < events.Count && events[ei].Time <= t)
            {
                var e = events[ei++];
                if (e.Type == EvType.Cc)
                {
                    if (e.Channel == 0) cc[e.D1] = e.D2 / 127f;
                }
                else if (e.Type == EvType.NoteOn && e.D2 > 0)
                {
                    float vel = e.D2 / 127f;
                    if (e.Channel == 1)                                   // fine grid → glass bells
                    {
                        int idx = e.D1 - 36;                             // 0..47, 8 cols
                        int col = idx % 8, row = idx / 8;
                        float pan = (col / 3.5f - 1f) * 0.85f;           // left→right
                        int sd = (4 - row) * 5 + (col % 5);             // top rows = higher register
                        bells[bellRR++ % bells.Length].Trigger(ScaleHz(sd + 12), vel * 0.5f, pan, 1.8f, glass: true);
                    }
                    else if (e.Channel == 0 && e.D1 >= 36 && e.D1 <= 51) // 4×4 → marimba
                    {
                        int idx = e.D1 - 36; int col = idx % 4, row = idx / 4;
                        float pan = (col / 1.5f - 1f) * 0.7f;
                        int sd = (3 - row) * 5 + (col % 5);
                        marimba[marRR++ % marimba.Length].Trigger(ScaleHz(sd + 6), vel * 0.6f, pan, 0.7f, glass: false);
                    }
                    else if (e.Channel == 0 && e.D1 >= 60)               // gestures → perc kit
                    {
                        switch (e.D1)
                        {
                            case 60: drums[drumRR++ % drums.Length].Trigger(DrumKind.Tom, vel); break;     // Expand
                            case 62: drums[drumRR++ % drums.Length].Trigger(DrumKind.Kick, vel); break;    // Contract
                            case 64: drums[drumRR++ % drums.Length].Trigger(DrumKind.Kick, vel); break;    // Fold
                            case 65: drums[drumRR++ % drums.Length].Trigger(DrumKind.Tom, vel); break;     // Simplify
                            case 67: drums[drumRR++ % drums.Length].Trigger(DrumKind.Crash, vel); break;   // Enclose
                            case 69: drums[drumRR++ % drums.Length].Trigger(DrumKind.Hat, vel); break;     // Emerge
                            case 71: drums[drumRR++ % drums.Length].Trigger(DrumKind.Hat, vel); break;     // Shimmer
                        }
                    }
                }
            }

            // Smooth CCs (one-pole ~30 ms) to avoid zipper noise.
            const float a = 0.0015f;
            for (int c = 20; c <= 36; c++) ccS[c] += (cc[c] - ccS[c]) * a;

            float size = ccS[20], prox = ccS[21], cplx = ccS[22];
            float layering = ccS[25], colour = ccS[24], sat = ccS[35], bright = ccS[36];
            float posX = ccS[30] * 2f - 1f, dolly = ccS[29] * 2f - 1f, haze = ccS[26];

            float dryL = 0, dryR = 0, wetL = 0, wetR = 0;
            float sr = SampleRate;

            // 1. Sub bass — sine + 2nd partial drive; root D, octave by proximity.
            {
                float oct = prox > 0.66f ? 0.5f : 1f;                   // very close → down an octave
                float hz = 73.42f * oct;
                pBass += hz / sr; if (pBass >= 1f) pBass -= 1f;
                pBass2 += 2f * hz / sr; if (pBass2 >= 1f) pBass2 -= 1f;
                float s = MathF.Sin(pBass * 6.2832f) + 0.25f * MathF.Sin(pBass2 * 6.2832f);
                s = MathF.Tanh(s * 1.4f) * (0.18f + 0.42f * size);
                dryL += s; dryR += s;
            }

            // 2. Warm pad — 3 detuned saws (Dm triad: D A F), LPF by complexity.
            {
                float[] hzs = { 146.83f, 220.0f, 174.61f };
                float det = 1.003f;
                float mix = 0;
                for (int k = 0; k < 3; k++)
                {
                    float hz = hzs[k] * (k == 1 ? det : (k == 2 ? 1f / det : 1f));
                    pPad[k] += hz / sr; if (pPad[k] >= 1f) pPad[k] -= 1f;
                    mix += (pPad[k] * 2f - 1f);                         // naive saw
                }
                mix *= 0.16f;
                padLp += (mix - padLp) * (0.04f + 0.55f * cplx);      // brighter when complex
                float g = 0.10f + 0.5f * layering;
                float l = padLp * g, r = padLp * g;
                dryL += l * 0.9f; dryR += r * 0.9f; wetL += l * 0.5f; wetR += r * 0.5f;
            }

            // 3. Lead — pentatonic pitch from PositionX, panned to match; FM-ish.
            {
                int sd = (int)MathF.Round((posX * 0.5f + 0.5f) * 10f) + 14; // register
                float hz = ScaleHz(sd);
                leadHzS += (hz - leadHzS) * 0.0008f;                    // portamento
                pLead += leadHzS / sr; if (pLead >= 1f) pLead -= 1f;
                pLeadV += 5.2f / sr; if (pLeadV >= 1f) pLeadV -= 1f;
                float fm = 0.5f * MathF.Sin(pLeadV * 6.2832f);
                float s = MathF.Sin((pLead + fm) * 6.2832f);
                float g = (0.06f + 0.30f * bright) * (0.4f + 0.6f * size);
                float pan = posX * 0.7f;
                float l = s * g * (1f - MathF.Max(0f, pan));
                float r = s * g * (1f + MathF.Min(0f, pan));
                dryL += l; dryR += r; wetL += l * 0.35f; wetR += r * 0.35f;
            }

            // 4. Choir — 4 partials, colour shifts a formant peak; gain by saturation.
            {
                float root = 220f;
                float[] mul = { 1f, 2f, 3f, 4f };
                float formant = 1.0f + 2.0f * colour;                  // colour → spectral tilt
                float s = 0;
                for (int k = 0; k < 4; k++)
                {
                    pChoir[k] += root * mul[k] / sr; if (pChoir[k] >= 1f) pChoir[k] -= 1f;
                    float w = MathF.Exp(-MathF.Abs(mul[k] - formant) * 0.9f);
                    s += MathF.Sin(pChoir[k] * 6.2832f) * w;
                }
                s *= 0.10f * (0.15f + 0.85f * sat);
                dryL += s; dryR += s; wetL += s * 0.6f; wetR += s * 0.6f;
            }

            // 5. Cello drone — bowed saw on A1, vibrato depth by dolly, gain by proximity.
            {
                float vibHz = 5.5f;
                pCelloV += vibHz / sr; if (pCelloV >= 1f) pCelloV -= 1f;
                float vib = 1f + (0.004f + 0.01f * MathF.Abs(dolly)) * MathF.Sin(pCelloV * 6.2832f);
                float hz = 110f * vib;
                pCello += hz / sr; if (pCello >= 1f) pCello -= 1f;
                float saw = (pCello * 2f - 1f);
                celloLp += (saw - celloLp) * 0.08f;
                float s = celloLp * (0.08f + 0.34f * prox);
                dryL += s; dryR += s; wetL += s * 0.4f; wetR += s * 0.4f;
            }

            // 6/7/8. Triggered pools.
            for (int v = 0; v < bells.Length; v++) bells[v].Render(ref dryL, ref dryR, ref wetL, ref wetR, sr);
            for (int v = 0; v < marimba.Length; v++) marimba[v].Render(ref dryL, ref dryR, ref wetL, ref wetR, sr);
            for (int v = 0; v < drums.Length; v++) drums[v].Render(ref dryL, ref dryR, sr);

            // Haze adds a faint airy shimmer floor.
            if (haze > 0.2f)
            {
                float nz = (Hash(ref rng) - 0.5f);
                hazeLp += (nz - hazeLp) * 0.5f;
                float s = (nz - hazeLp) * 0.05f * (haze - 0.2f);
                wetL += s; wetR += s;
            }

            // Reverb + master.
            rev.Process(wetL, wetR, out float rl, out float rr);
            float ml = dryL + rl * 0.9f;
            float mr = dryR + rr * 0.9f;
            ml = MathF.Tanh(ml * 0.8f);
            mr = MathF.Tanh(mr * 0.8f);
            outBuf[i * 2] = (short)Math.Clamp((int)(ml * 30000f), -32768, 32767);
            outBuf[i * 2 + 1] = (short)Math.Clamp((int)(mr * 30000f), -32768, 32767);
        }

        return outBuf;
    }

    private static float Hash(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / (float)0xFFFFFF; }

    // ====================================================================
    // Voices
    // ====================================================================

    /// <summary>Plucked/struck FM voice with exponential decay — bells (glass) or marimba (woody).</summary>
    private struct Pluck
    {
        float hz, amp, pan, phase, modPhase, env, decay; bool glass; bool on;

        public void Trigger(float frequency, float amplitude, float panPos, float decaySec, bool glass)
        {
            hz = frequency; amp = amplitude; pan = panPos; this.glass = glass;
            phase = 0; modPhase = 0; env = 1f; on = true;
            decay = MathF.Exp(-1f / (decaySec * SampleRate));
        }

        public void Render(ref float dryL, ref float dryR, ref float wetL, ref float wetR, float sr)
        {
            if (!on) return;
            phase += hz / sr; if (phase >= 1f) phase -= 1f;
            float modRatio = glass ? 3.5f : 2.0f;
            modPhase += hz * modRatio / sr; if (modPhase >= 1f) modPhase -= 1f;
            float modIdx = (glass ? 1.6f : 0.9f) * env;
            float s = MathF.Sin((phase + modIdx * MathF.Sin(modPhase * 6.2832f)) * 6.2832f) * env * amp;
            env *= decay;
            if (env < 1e-4f) { on = false; }
            float l = s * (1f - MathF.Max(0f, pan));
            float r = s * (1f + MathF.Min(0f, pan));
            dryL += l; dryR += r;
            float w = glass ? 0.7f : 0.3f;
            wetL += l * w; wetR += r * w;
        }
    }

    private enum DrumKind { Kick, Tom, Hat, Crash }

    private struct Drum
    {
        DrumKind kind; float amp, env, decay, phase, pitchEnv; uint nz; bool on;

        public void Trigger(DrumKind k, float velocity)
        {
            kind = k; amp = velocity; env = 1f; phase = 0; pitchEnv = 1f; on = true;
            nz = 0x9E3779B9u ^ (uint)(velocity * 1000);
            decay = kind switch
            {
                DrumKind.Kick => MathF.Exp(-1f / (0.22f * SampleRate)),
                DrumKind.Tom => MathF.Exp(-1f / (0.30f * SampleRate)),
                DrumKind.Hat => MathF.Exp(-1f / (0.05f * SampleRate)),
                _ => MathF.Exp(-1f / (1.2f * SampleRate)),
            };
        }

        public void Render(ref float dryL, ref float dryR, float sr)
        {
            if (!on) return;
            float s;
            if (kind == DrumKind.Kick || kind == DrumKind.Tom)
            {
                float baseHz = kind == DrumKind.Kick ? 55f : 120f;
                float hz = baseHz * (1f + 2.5f * pitchEnv);
                phase += hz / sr; if (phase >= 1f) phase -= 1f;
                s = MathF.Sin(phase * 6.2832f) * env * amp * 0.8f;
                pitchEnv *= MathF.Exp(-1f / (0.03f * sr));
            }
            else
            {
                float nzv = (Hash(ref nz) - 0.5f);
                if (kind == DrumKind.Hat) { s = nzv * env * amp * 0.4f; }
                else { _crashLp += (nzv - _crashLp) * 0.6f; s = (nzv - _crashLp) * env * amp * 0.5f; }
            }
            env *= decay;
            if (env < 1e-4f) on = false;
            dryL += s; dryR += s;
        }
        private float _crashLp;
    }

    /// <summary>Compact stereo reverb (4 combs + 2 allpass per channel, Schroeder).</summary>
    private sealed class Reverb
    {
        private readonly float[][] _comb = new float[8][];
        private readonly int[] _cp = new int[8];
        private readonly float[] _cs = new float[8];
        private readonly float[][] _ap = new float[4][];
        private readonly int[] _ap_p = new int[4];
        private static readonly int[] CombLen = { 1557, 1617, 1491, 1422, 1277, 1356, 1188, 1116 };
        private static readonly int[] ApLen = { 225, 556, 441, 341 };
        private const float Fb = 0.84f, Damp = 0.2f, ApG = 0.5f;

        public Reverb()
        {
            for (int i = 0; i < 8; i++) _comb[i] = new float[CombLen[i]];
            for (int i = 0; i < 4; i++) _ap[i] = new float[ApLen[i]];
        }

        public void Process(float inL, float inR, out float outL, out float outR)
        {
            outL = Chan(inL, 0, 0); outR = Chan(inR, 4, 2);
        }

        private float Chan(float x, int combOff, int apOff)
        {
            float acc = 0;
            for (int i = 0; i < 4; i++)
            {
                int ci = combOff + i;
                var buf = _comb[ci];
                float y = buf[_cp[ci]];
                _cs[ci] += (y - _cs[ci]) * (1f - Damp);
                buf[_cp[ci]] = x + _cs[ci] * Fb;
                if (++_cp[ci] >= buf.Length) _cp[ci] = 0;
                acc += y;
            }
            acc *= 0.25f;
            for (int i = 0; i < 2; i++)
            {
                int ai = apOff + i;
                var buf = _ap[ai];
                float bufd = buf[_ap_p[ai]];
                float y = -acc + bufd;
                buf[_ap_p[ai]] = acc + bufd * ApG;
                if (++_ap_p[ai] >= buf.Length) _ap_p[ai] = 0;
                acc = y;
            }
            return acc;
        }
    }
}
