using System.Numerics;

namespace Parsec.Audio.Sonification;

/// <summary>
/// Selects the DSP voice applied by <see cref="FractalDroneSynth"/> and
/// <see cref="FractalDroneStream"/> when synthesising geometry telemetry.
/// </summary>
public enum FractalVoice
{
    /// <summary>Metallic comb resonators — delay times from orbit-trap distances.</summary>
    Mandelbox,
    /// <summary>Additive sine pad — spectral peak morphs with TrapMean orbit radius.</summary>
    Mandelbulb,
    /// <summary>Schroeder reverb — HitRatio sets feedback/tail length, cavernous.</summary>
    Kleinian,
    /// <summary>Ring modulation + tanh distortion — StepP90 scales modulator frequency.</summary>
    BurningShip,
    /// <summary>M7g: bells at integer Apollonian curvature pitches, triggered by camera motion.</summary>
    Apollonian,
}

/// <summary>
/// M9d: selects whether live sonification and export use the hybrid wavetable+bells
/// synth (M7/M8) or the direct-orbit mode where the iteration map IS the oscillator (M9).
/// Orthogonal to <see cref="FractalVoice"/> — voice keys per-fractal telemetry dispatch
/// in both modes.
/// </summary>
public enum SonificationMode
{
    Hybrid,
    DirectOrbit,
}

/// <summary>
/// Offline DSP synth: converts a sequence of <see cref="FractalSonicFrame"/>s into
/// mono PCM16 samples. Fully deterministic — given the same frames the output is
/// bit-identical across runs (fixed-seed LCG noise, no Date/Random).
///
/// Each voice uses a distinct synthesis algorithm tied to the fractal's geometry:
///
///   Mandelbox   — 4 comb resonators; TrapMean[0..3] set delay pitches;
///                 HitRatio controls feedback (more geometry = sharper resonance).
///   Mandelbulb  — 8 harmonic sines shaped by a Gaussian spectral envelope;
///                 envelope peak tracks TrapMean.X (orbit radius); smooth pad morph.
///   Kleinian    — Schroeder reverb (4 all-pass + 2 comb) fed by a C2 sine;
///                 HitRatio raises comb feedback → hollow cavity grows deeper.
///   BurningShip — saw at C3 ring-modulated by (110+StepP90×5 Hz), then tanh-shaped
///                 with NormalVariance-scaled drive → inharmonic crackle.
/// </summary>
public static class FractalDroneSynth
{
    public const int DefaultSampleRate = 44100;

    // ----------------------------------------------------------------------- public

    public static short[] Synthesize(
        IReadOnlyList<FractalSonicFrame> frames,
        double controlRateHz = 30.0,
        int sampleRate = DefaultSampleRate,
        FractalVoice voice = FractalVoice.Mandelbox)
    {
        if (frames.Count == 0) return [];
        return voice switch
        {
            FractalVoice.Mandelbulb  => SynthMandelbulb (frames, controlRateHz, sampleRate),
            FractalVoice.Kleinian    => SynthKleinian   (frames, controlRateHz, sampleRate),
            FractalVoice.BurningShip => SynthBurningShip(frames, controlRateHz, sampleRate),
            FractalVoice.Apollonian  => SynthApollonian (frames, controlRateHz, sampleRate),
            _                        => SynthMandelbox  (frames, controlRateHz, sampleRate),
        };
    }

    // ----------------------------------------------------------------------- shared helpers

    private static float Coeff(float tcSecs, int sr)
        => 1f - MathF.Exp(-1f / (tcSecs * sr));

    private static float NextNoise(ref uint rng)
    {
        rng = rng * 1664525u + 1013904223u;
        return (float)(int)rng / 2147483648f;  // ≈ [-1, 1]
    }

    private static short Clip16(float s)
        => (short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue);

    // ----------------------------------------------------------------------- MANDELBOX
    // 4 parallel comb resonators driven by continuous noise.
    // TrapMean[0..3] (orbit/fold distances) set the delay lengths → resonant pitches.
    // HitRatio controls feedback: more geometry → sharper resonance, richer metallic tone.
    // All four combs include a mild 1-pole LPF in the feedback loop for damping.
    private static short[] SynthMandelbox(
        IReadOnlyList<FractalSonicFrame> frames, double crHz, int sr)
    {
        int spf = (int)Math.Round(sr / crHz);
        var output = new short[frames.Count * spf];

        const int MaxD = 1500;  // C1 ≈ 1349 samples — allow for low detuning
        float[][] cbuf = new float[4][];
        for (int k = 0; k < 4; k++) cbuf[k] = new float[MaxD];
        int[]   cw   = new int[4];
        float[] clpf = new float[4];
        // Initial smoothed delay lengths: C2, E3, C3, G3
        float[] dS = new float[] { 674f, 505f, 337f, 250f };

        float gS  = 0.05f;
        float gcf = Coeff(0.06f, sr);
        float dcf = Coeff(0.15f, sr);   // delay smoother — slow pitch glide

        uint rng = 0x12345678u;

        int idx = 0;
        foreach (var f in frames)
        {
            float gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(f.HitRatio, 0f, 1f));
            float fb = 0.88f + 0.09f * Math.Clamp(f.HitRatio, 0f, 1f);

            // Delay targets: each TrapMean component maps to a pitch offset from C2
            // trap.x = orbit radius (0–2), trap.y = |x| (0–0.5), trap.z = |xy| (0–1), trap.w = shell dist (0–0.5)
            float dT0 = sr / MathF.Max(40f, 65.41f * (1.0f + Math.Clamp(f.TrapMean.X, 0f, 2.0f) * 2.0f));
            float dT1 = sr / MathF.Max(40f, 65.41f * (1.5f + Math.Clamp(f.TrapMean.Y, 0f, 1.0f) * 3.0f));
            float dT2 = sr / MathF.Max(40f, 65.41f * (2.0f + Math.Clamp(f.TrapMean.Z, 0f, 1.5f) * 3.0f));
            float dT3 = sr / MathF.Max(40f, 65.41f * (3.0f + Math.Clamp(f.TrapMean.W, 0f, 1.0f) * 4.0f));

            int end = Math.Min(idx + spf, output.Length);
            while (idx < end)
            {
                gS   += gcf * (gT   - gS);
                dS[0] += dcf * (dT0 - dS[0]);
                dS[1] += dcf * (dT1 - dS[1]);
                dS[2] += dcf * (dT2 - dS[2]);
                dS[3] += dcf * (dT3 - dS[3]);

                float noise = NextNoise(ref rng) * gS * 0.28f;
                float sig = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int D  = Math.Clamp((int)dS[k], 50, MaxD - 1);
                    int rp = (cw[k] - D + MaxD) % MaxD;
                    float del = cbuf[k][rp];
                    clpf[k] += 0.5f * (del - clpf[k]);  // damping LPF
                    float y = Math.Clamp(noise + fb * clpf[k], -8f, 8f);
                    cbuf[k][cw[k]] = y;
                    cw[k] = (cw[k] + 1) % MaxD;
                    sig += y;
                }
                sig *= 0.18f;  // normalize 4 combs + headroom
                output[idx++] = Clip16(sig);
            }
        }
        return output;
    }

    // ----------------------------------------------------------------------- MANDELBULB
    // 8 harmonic sine waves (partials 1–8 of C2 = 65.41 Hz).
    // Amplitudes are shaped by a Gaussian spectral envelope that morphs with geometry:
    //   • Envelope peak F = 131 + 260 * TrapMean.X  →  C3 to G4 as orbit radius varies
    //   • Envelope width σ = 50 + 120 * NormalVariance  →  clean pad to breathy cloud
    // Result: a smooth vowel-like pad whose spectral character shifts as you fly in.
    private static short[] SynthMandelbulb(
        IReadOnlyList<FractalSonicFrame> frames, double crHz, int sr)
    {
        const int NPartials = 8;
        const float Fund = 65.41f;  // C2

        int spf = (int)Math.Round(sr / crHz);
        var output = new short[frames.Count * spf];

        var ph = new float[NPartials];   // oscillator phases [0,1)
        float gS   = 0.05f;
        float txS  = 0.5f;   // smoothed TrapMean.X
        float nVS  = 0.2f;   // smoothed NormalVariance
        float gcf  = Coeff(0.08f,  sr);
        float txcf = Coeff(0.20f,  sr);
        float nVcf = Coeff(0.15f,  sr);

        Span<float> weights = stackalloc float[NPartials];

        int idx = 0;
        foreach (var f in frames)
        {
            float gT  = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(f.HitRatio, 0f, 1f));
            float txT = Math.Clamp(f.TrapMean.X, 0f, 1.3f) / 1.3f;  // normalise to [0,1]
            float nVT = Math.Clamp(f.NormalVariance, 0f, 1.0f);

            // Gaussian spectral envelope parameters at frame rate
            float fpeak = 131f + 260f * txT;    // 131–391 Hz — hits harmonics 2–6
            float sigma = 50f  + 120f * nVT;    // narrow (pure) → wide (breathy)

            // Compute harmonic weights for this frame
            float wsum = 0f;
            for (int k = 0; k < NPartials; k++)
            {
                float freq = Fund * (k + 1);
                float dF   = freq - fpeak;
                weights[k] = MathF.Exp(-0.5f * (dF / sigma) * (dF / sigma));
                wsum += weights[k];
            }
            if (wsum < 1e-6f) wsum = 1f;
            for (int k = 0; k < NPartials; k++) weights[k] /= wsum;

            int end = Math.Min(idx + spf, output.Length);
            while (idx < end)
            {
                gS  += gcf  * (gT  - gS);
                txS += txcf * (txT - txS);
                nVS += nVcf * (nVT - nVS);

                float sig = 0f;
                for (int k = 0; k < NPartials; k++)
                {
                    float freq = Fund * (k + 1);
                    sig    += MathF.Sin(2f * MathF.PI * ph[k]) * weights[k];
                    ph[k]   = (ph[k] + freq / sr) % 1f;
                }

                sig *= gS * 0.55f;
                output[idx++] = Clip16(sig);
            }
        }
        return output;
    }

    // ----------------------------------------------------------------------- KLEINIAN
    // Schroeder reverb: 4 all-pass diffusors (series) → 2 parallel combs with LPF damping.
    // Source: a C2 sine wave feeds the reverb continuously.
    // HitRatio raises comb feedback → longer reverb tail → deeper hollow-cavity sound.
    // Very different from the other voices: it's all about the spatial resonance, not timbre.
    private static short[] SynthKleinian(
        IReadOnlyList<FractalSonicFrame> frames, double crHz, int sr)
    {
        int spf = (int)Math.Round(sr / crHz);
        var output = new short[frames.Count * spf];

        // Classic Schroeder delay primes (samples at 44100 Hz)
        int ap0 = ScaleDelay(1051, sr), ap1 = ScaleDelay(1567, sr),
            ap2 = ScaleDelay(2203, sr), ap3 = ScaleDelay(3251, sr);
        int cm0 = ScaleDelay(4799, sr), cm1 = ScaleDelay(5399, sr);

        float[] apB0 = new float[ap0 + 1], apB1 = new float[ap1 + 1],
                apB2 = new float[ap2 + 1], apB3 = new float[ap3 + 1];
        float[] cmB0 = new float[cm0 + 1], cmB1 = new float[cm1 + 1];
        int aw0 = 0, aw1 = 0, aw2 = 0, aw3 = 0, cw0 = 0, cw1 = 0;
        float cmLpf0 = 0f, cmLpf1 = 0f;

        const float ApG = 0.70f;  // all-pass gain (standard Schroeder)
        float gS   = 0.05f;
        float gcf  = Coeff(0.08f, sr);
        float sinePh = 0f;        // C2 source oscillator

        int idx = 0;
        foreach (var f in frames)
        {
            float gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(f.HitRatio, 0f, 1f));
            // More geometry (high HitRatio) → higher feedback → longer reverb tail
            float cFb   = 0.72f + 0.22f * Math.Clamp(f.HitRatio, 0f, 1f);
            // Darker damp when inside geometry (more resonant, less bright)
            float cDamp = 0.18f + 0.12f * (1f - Math.Clamp(f.HitRatio, 0f, 1f));

            int end = Math.Min(idx + spf, output.Length);
            while (idx < end)
            {
                gS += gcf * (gT - gS);

                // C2 sine source
                float src = MathF.Sin(2f * MathF.PI * sinePh) * gS * 0.25f;
                sinePh = (sinePh + 65.41f / sr) % 1f;

                // 4 all-pass in series
                float ap = AllPass(src,  apB0, ref aw0, ap0, ApG);
                ap = AllPass(ap,  apB1, ref aw1, ap1, ApG);
                ap = AllPass(ap,  apB2, ref aw2, ap2, ApG);
                ap = AllPass(ap,  apB3, ref aw3, ap3, ApG);

                // 2 parallel combs with LPF in loop
                float c0 = CombLpf(ap, cmB0, ref cw0, cm0, cFb, ref cmLpf0, cDamp);
                float c1 = CombLpf(ap, cmB1, ref cw1, cm1, cFb, ref cmLpf1, cDamp);
                float wet = (c0 + c1) * 0.5f;

                // Wet-only — the reverb IS the sound for Kleinian (no dry signal)
                float sig = wet * 1.4f;
                output[idx++] = Clip16(sig);
            }
        }
        return output;
    }

    private static int ScaleDelay(int nominalAt44100, int sr)
        => Math.Max(4, (int)Math.Round((double)nominalAt44100 * sr / 44100));

    // All-pass filter: y[n] = -g·x[n] + x[n-D] + g·y[n-D]
    private static float AllPass(float x, float[] buf, ref int w, int D, float g)
    {
        int len = buf.Length;
        int rp  = (w - D + len) % len;
        float yn1 = buf[rp];
        float y   = -g * x + yn1 + g * buf[(rp + 1) % len];
        buf[w] = x + g * y;  // store input + g*output for next read
        w = (w + 1) % len;
        return y;
    }

    // Comb filter with LPF in feedback loop (Schroeder style)
    private static float CombLpf(float x, float[] buf, ref int w, int D,
                                  float fb, ref float lpf, float damp)
    {
        int len = buf.Length;
        int rp  = (w - D + len) % len;
        float delayed = buf[rp];
        lpf  += damp * (delayed - lpf);   // 1-pole LPF applied to delayed signal
        float y = Math.Clamp(x + fb * lpf, -12f, 12f);
        buf[w] = y;
        w = (w + 1) % len;
        return y;
    }

    // ----------------------------------------------------------------------- BURNINGSHIP
    // Sawtooth at C3 (130.81 Hz) ring-modulated by a sine at (110 + StepP90×5) Hz.
    // Sidebands fall at non-harmonic intervals → harsh, inharmonic, crackle quality.
    // NormalVariance drives tanh waveshaping gain (rough surface → more distortion).
    // LPF at ~3 kHz prevents aliasing and controls brightness.
    private static short[] SynthBurningShip(
        IReadOnlyList<FractalSonicFrame> frames, double crHz, int sr)
    {
        int spf = (int)Math.Round(sr / crHz);
        var output = new short[frames.Count * spf];

        const float Fund = 130.81f;  // C3
        float sawPh = 0f, modPh = 0f;
        float lpfY = 0f;
        float gS = 0.05f, stepS = 40f, nVarS = 0.2f;
        float gcf   = Coeff(0.06f, sr);
        float stepcf = Coeff(0.12f, sr);
        float nVcf  = Coeff(0.10f, sr);

        int idx = 0;
        foreach (var f in frames)
        {
            float gT    = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(f.HitRatio, 0f, 1f));
            float stepT = Math.Clamp(f.StepP90, 0f, 100f);
            float nVT   = Math.Clamp(f.NormalVariance, 0f, 1f);

            int end = Math.Min(idx + spf, output.Length);
            while (idx < end)
            {
                gS    += gcf    * (gT    - gS);
                stepS  += stepcf * (stepT  - stepS);
                nVarS  += nVcf  * (nVT   - nVarS);

                // Sawtooth oscillator at C3
                float saw = 2f * sawPh - 1f;
                sawPh = (sawPh + Fund / sr) % 1f;

                // Ring modulation: multiply by sine at StepP90-scaled frequency
                float modFreq = 110f + stepS * 5f;  // ~295–610 Hz for stepP90 37–100
                float mod = MathF.Sin(2f * MathF.PI * modPh);
                modPh = (modPh + modFreq / sr) % 1f;
                float sig = saw * mod;

                // Tanh waveshaping (soft saturation / distortion)
                float drive = 1.5f + nVarS * 7f;  // 1.5 (clean) → 8.5 (heavy distortion)
                float norm  = MathF.Max(1e-6f, (float)Math.Tanh(drive));
                sig = (float)Math.Tanh(sig * drive) / norm;

                // One-pole LPF at ~3 kHz
                float alpha = 1f - MathF.Exp(-2f * MathF.PI * 3000f / sr);
                lpfY += alpha * (sig - lpfY);

                sig = lpfY * gS * 0.50f;
                output[idx++] = Clip16(sig);
            }
        }
        return output;
    }

    // ----------------------------------------------------------------------- APOLLONIAN (M7g)
    // Bells tuned to the integer Apollonian curvature scale (GeometryPitches from frame,
    // or hard-coded canonical JI pentatonic as fallback).  No cell telemetry needed —
    // bells are triggered periodically by a sample counter gated on CameraSpeed.
    // No drone oscillator; the decaying sinusoidal bell cluster IS the sound.
    private static short[] SynthApollonian(
        IReadOnlyList<FractalSonicFrame> frames, double crHz, int sr)
    {
        const int NCells = 16;
        // Canonical Apollonian JI pitches at A2 root
        float[] defaultPitches = [110f, 110f * 35f/32f, 110f * 19f/16f, 165f, 110f * 15f/8f];

        int spf = (int)Math.Round(sr / crHz);
        var output = new short[frames.Count * spf];

        var resGain  = new float[NCells];
        var resDecay = new float[NCells];
        var resOscPh = new float[NCells];
        var resFreq  = new float[NCells];

        // Schroeder reverb
        int ap0 = ScaleDelay(1051, sr), ap1 = ScaleDelay(1567, sr),
            ap2 = ScaleDelay(2203, sr), ap3 = ScaleDelay(3251, sr);
        int cm0 = ScaleDelay(4799, sr), cm1 = ScaleDelay(5399, sr);
        float[] apB0 = new float[ap0+1], apB1 = new float[ap1+1],
                apB2 = new float[ap2+1], apB3 = new float[ap3+1];
        float[] cmB0 = new float[cm0+1], cmB1 = new float[cm1+1];
        int aw0=0, aw1=0, aw2=0, aw3=0, cw0=0, cw1=0;
        float cmLpf0=0f, cmLpf1=0f;

        int apoCounter = 0;
        int apoNextPitch = 0;
        int trigInterval = (int)(sr * 0.7f);  // will update each frame

        int idx = 0;
        foreach (var frame in frames)
        {
            float[] pitches = frame.GeometryPitches ?? defaultPitches;
            float cameraSpd = frame.CameraSpeed;
            trigInterval = (int)(sr * Math.Clamp(0.8f - cameraSpd * 0.11f, 0.25f, 0.9f));

            int end = Math.Min(idx + spf, output.Length);
            while (idx < end)
            {
                apoCounter++;
                if (apoCounter >= trigInterval)
                {
                    apoCounter = 0;
                    int pi   = apoNextPitch % pitches.Length;
                    int oct  = (apoNextPitch / pitches.Length) % 3;
                    int slot = apoNextPitch % NCells;
                    resFreq[slot]  = pitches[pi] * MathF.Pow(2f, oct);
                    resGain[slot]  = 0.82f;
                    float decayS   = 1.0f + (pi / (float)(pitches.Length - 1)) * 1.5f;
                    resDecay[slot] = MathF.Exp(-1f / (decayS * sr));
                    resOscPh[slot] = 0f;
                    apoNextPitch++;
                }

                float bellMix = 0f;
                for (int ri = 0; ri < NCells; ri++)
                {
                    if (resGain[ri] < 1e-5f) continue;
                    bellMix      += resGain[ri] * MathF.Sin(2f * MathF.PI * resOscPh[ri]) * 0.025f;
                    resGain[ri]  *= resDecay[ri];
                    resOscPh[ri]  = (resOscPh[ri] + resFreq[ri] / sr) % 1f;
                }

                float ap = AllPass(bellMix, apB0, ref aw0, ap0, 0.65f);
                ap = AllPass(ap, apB1, ref aw1, ap1, 0.65f);
                ap = AllPass(ap, apB2, ref aw2, ap2, 0.65f);
                ap = AllPass(ap, apB3, ref aw3, ap3, 0.65f);
                float c0 = CombLpf(ap, cmB0, ref cw0, cm0, 0.72f, ref cmLpf0, 0.18f);
                float c1 = CombLpf(ap, cmB1, ref cw1, cm1, 0.72f, ref cmLpf1, 0.18f);
                float reverbOut = (c0 + c1) * 0.35f;

                float sig = 0.85f * (float)Math.Tanh(reverbOut * 1.2f);
                output[idx++] = Clip16(sig);
            }
        }
        return output;
    }
}
