using System.Numerics;

namespace Parsec.Audio.Sonification;

/// <summary>
/// Offline DSP synth: converts a sequence of <see cref="FractalSonicFrame"/>s into
/// mono PCM16 samples. Fully deterministic — given the same frames the output is
/// bit-identical across runs (fixed-seed LCG noise, no Date/Random).
/// </summary>
/// <remarks>
/// Voice: 4 saw-wave partials (1x–4x C2 = 65 Hz) filtered through a one-pole LPF,
/// with an additive noise layer. One-pole smoothers interpolate all control parameters
/// at sample rate to eliminate zipper noise between telemetry frames.
///
/// Mappings:
///   HitRatio         → master gain (sqrt-scaled, floor 0.05)
///   MeanDepth        → LPF cutoff (log: depth 0 → 8 kHz, depth 20+ → 300 Hz)
///   StepP90          → noise density (granular texture)
///   NormalVariance   → harmonic brightness (dark at 0, bright/buzzy at 0.5+)
///   CameraSpeed      → slight pitch modulation (Doppler-like)
/// </remarks>
public static class FractalDroneSynth
{
    public const int DefaultSampleRate = 44100;

    private const float BaseFundHz = 65.41f;   // C2
    private const float MasterGain = 0.22f;    // headroom: leaves room for noise without clipping

    /// <summary>
    /// Synthesize mono PCM16 samples from a sequence of telemetry frames.
    /// </summary>
    /// <param name="frames">Frames at a uniform control rate (e.g. 30 Hz from telemetry pass).</param>
    /// <param name="controlRateHz">Rate at which frames were sampled.</param>
    /// <param name="sampleRate">Output sample rate.</param>
    public static short[] Synthesize(
        IReadOnlyList<FractalSonicFrame> frames,
        double controlRateHz = 30.0,
        int sampleRate = DefaultSampleRate)
    {
        if (frames.Count == 0) return [];

        int samplesPerFrame = (int)Math.Round(sampleRate / controlRateHz);
        int totalSamples    = frames.Count * samplesPerFrame;
        var output = new short[totalSamples];

        // One-pole smoother: coeff = 1 - exp(-1 / (tc * sampleRate))
        static float Coeff(float tcSecs, int sr)
            => 1f - MathF.Exp(-1f / (tcSecs * sr));

        float gcf = Coeff(0.05f,  sampleRate);  // gain
        float ccf = Coeff(0.03f,  sampleRate);  // LPF cutoff
        float ncf = Coeff(0.08f,  sampleRate);  // noise density
        float bcf = Coeff(0.10f,  sampleRate);  // harmonic brightness
        float pcf = Coeff(0.025f, sampleRate);  // pitch mod

        // Smoother state (starts at resting values)
        float gS = 0.05f;
        float cS = 1200f;
        float nS = 0f;
        float bS = 0f;
        float pS = 0f;
        float lpfY = 0f;

        // Oscillator phases — 4 partials (1x, 2x, 3x, 4x of fundamental)
        var ph = new float[4];

        // Fixed-seed LCG for determinism (Numerical Recipes constants)
        uint rng = 0x12345678u;
        float NextNoise()
        {
            rng = rng * 1664525u + 1013904223u;
            return (float)(int)rng / 2147483648f;   // range ≈ [-1, 1]
        }

        int idx = 0;
        for (int fi = 0; fi < frames.Count; fi++)
        {
            var f = frames[fi];

            // Compute target values from this frame's telemetry
            float gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(f.HitRatio, 0f, 1f));
            float cT = DepthToCutoff(f.MeanDepth);
            float nT = Math.Clamp(f.StepP90 / 120f, 0f, 0.5f);
            float bT = Math.Clamp(f.NormalVariance * 2f, 0f, 1f);
            float pT = Math.Clamp(f.CameraSpeed * 0.003f, -0.02f, 0.02f);

            int end = Math.Min(idx + samplesPerFrame, totalSamples);
            while (idx < end)
            {
                // Advance smoothers one sample
                gS += gcf * (gT - gS);
                cS += ccf * (cT - cS);
                nS += ncf * (nT - nS);
                bS += bcf * (bT - bS);
                pS += pcf * (pT - pS);

                // Harmonic weights: b=0 → warm/dark, b=1 → bright/buzzy
                float w0 = 1.00f;
                float w1 = 0.60f - 0.45f * bS;
                float w2 = 0.10f + 0.55f * bS;
                float w3 = 0.02f + 0.38f * bS;
                float wSum = w0 + w1 + w2 + w3;

                // Mix 4 saw partials
                float pfactor = 1f + pS;
                float sig = 0f;
                for (int h = 0; h < 4; h++)
                {
                    float freq = BaseFundHz * (h + 1) * pfactor;
                    float w    = h switch { 0 => w0, 1 => w1, 2 => w2, _ => w3 };
                    sig    += (2f * ph[h] - 1f) * w;
                    ph[h]   = (ph[h] + freq / sampleRate) % 1f;
                }
                sig /= wSum;

                // Noise layer (granular texture driven by marching step depth)
                sig += NextNoise() * MathF.Max(0f, nS);

                // One-pole LPF
                float alpha = Math.Clamp(
                    1f - MathF.Exp(-2f * MathF.PI * cS / sampleRate), 0f, 1f);
                lpfY += alpha * (sig - lpfY);

                // Apply gain and master level, pack to PCM16
                sig = lpfY * gS * MasterGain;
                output[idx++] = (short)Math.Clamp(
                    (int)(sig * 32767f), short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    // Log-linear map: depth 0 → 8 kHz (bright), depth ≥ 20 → 300 Hz (dark)
    private static float DepthToCutoff(float depth)
    {
        float t = Math.Clamp(depth / 20f, 0f, 1f);
        float logLow = MathF.Log(8000f), logHigh = MathF.Log(300f);
        return MathF.Exp(logLow + t * (logHigh - logLow));
    }
}
