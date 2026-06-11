using System.Numerics;

namespace Parsec.Audio.Sonification;

/// <summary>
/// M9b offline direct-orbit synthesiser: the iteration map IS the oscillator.
///
/// Per-cell voice: steps through the captured 128-point orbit trajectory at
/// 44100/12 ≈ 3675 points/s with cosine interpolation (FSE formula).  The
/// 3D orbit is projected onto the camera basis: dot(p, camRight) → L component,
/// dot(p, camUp) → R component, dot(p, camForward) → per-cell one-pole LPF cutoff.
/// Cells are additionally panned by column index (same constant-power law as M8 bells).
///
/// Anti-noise safety chain (per segment):
///   1. Centroid subtraction (DC removal at the orbit level)
///   2. Peak normalisation with epsilon guard (degenerate orbits → silence)
///   3. Escaped-orbit zeroing + (0.30 floor + sqrt(bounded fraction)) amplitude: empty
///      cells are silenced (stereo gate) while partially-bounded cells stay audible (16-voice wall)
///   4. Per-cell one-pole DC blocker (~20 Hz) then one-pole LPF (depth-modulated)
///   5. Segment-to-segment equal-power crossfade (~5 ms) at every frame boundary
///   6. Master tanh soft limiter + Shepard–Risset layer panned to the on-screen fractal centroid
///
/// Output: stereo interleaved short[] (L0, R0, L1, R1, …), same contract as HybridSynth.
/// Deterministic: no RNG; same orbit captures → identical PCM.
/// </summary>
public static class DirectOrbitSynth
{
    public const int DefaultSampleRate = 44100;

    private const int SamplesPerStep = 12;     // FSE formula: 44100/12 ≈ 3675 steps/s
    private const int OrbtLen       = 128;     // must match ORBTRAJ in mandelbox_telemetry.metal
    private const int NCells        = 16;
    private const int XfadeSamples  = 220;     // ≈5 ms at 44100 Hz
    private const float CellScale   = 0.065f;  // per-cell amplitude — denser 16-voice wall (floor keeps cells active)
    private const float MinProjectedRms = 0.30f;
    private const float MaxProjectionBoost = 6.0f;

    public static short[] Synthesize(
        IReadOnlyList<FractalSonicFrame> frames,
        double controlRateHz = 30.0,
        int sampleRate       = DefaultSampleRate)
    {
        if (frames.Count == 0) return [];

        int spf = (int)Math.Round(sampleRate / controlRateHz);
        var output = new short[frames.Count * spf * 2];

        // Per-cell persistent state across frames
        var prevSeg   = new Vector3[NCells][];
        var prevPhase = new float[NCells];     // playback phase at end of previous frame
        var dcXL      = new float[NCells];     // DC-blocker: x[n-1] for L
        var dcXR      = new float[NCells];     // DC-blocker: x[n-1] for R
        var dcHL      = new float[NCells];     // DC-blocker: y[n-1] for L
        var dcHR      = new float[NCells];     // DC-blocker: y[n-1] for R
        var lpfL      = new float[NCells];     // one-pole LPF state for L
        var lpfR      = new float[NCells];     // one-pole LPF state for R
        var tiltLpfL  = new float[NCells];     // spectral-tilt shelf state for L
        var tiltLpfR  = new float[NCells];     // spectral-tilt shelf state for R
        for (int ci = 0; ci < NCells; ci++)
            prevSeg[ci] = new Vector3[OrbtLen]; // initialise to silence

        // Per-cell step rates: bottom row is each column's root; rows and columns ascend by pure fifths.
        var cellSps = new float[NCells];
        for (int ci = 0; ci < NCells; ci++)
        {
            int col = ci % 4;
            int row = ci / 4;
            float columnRoot = MathF.Pow(1.5f, col);
            float rowRatio   = MathF.Pow(1.5f, 3 - row);
            cellSps[ci] = SamplesPerStep / (columnRoot * rowRatio);
        }

        // DC-blocker pole: R = 1 − 2π·fc/sr  (first-order HPF, fc ≈ 20 Hz)
        float hpfR = 1f - 2f * MathF.PI * 20f / sampleRate;

        // Spectral-tilt shelf (matches HybridSynth bell rows: +0.35 top, −0.35 bottom)
        float tiltCf = 1f - MathF.Exp(-2f * MathF.PI * 2000f / sampleRate);
        float[] rowTilt = [0.35f, 0.12f, -0.12f, -0.35f];

        // Constant-power pan: column sets the coarse screen-horizontal position
        // (−0.75 → +0.75), a small per-row dither (±0.09) spreads the 4 cells that
        // share a column so they occupy 16 distinct positions rather than stacking
        // on 4 — without breaking the left/right mapping (rows stay within their column).
        var panL = new float[NCells];
        var panR = new float[NCells];
        for (int ci = 0; ci < NCells; ci++)
        {
            float pan = ((ci % 4) + 0.5f) / 4f * 2f - 1f; // −0.75 → +0.75
            pan = Math.Clamp(pan + (ci / 4 - 1.5f) * 0.06f, -1f, 1f);
            panL[ci] = MathF.Cos((pan + 1f) * MathF.PI / 4f);
            panR[ci] = MathF.Sin((pan + 1f) * MathF.PI / 4f);
        }

        // Shepard–Risset layer (identical constants to HybridSynth)
        const int   NSR    = 7;
        const float SR_LO  = 27.5f;
        const float SR_CTR = 220f;
        const float SR_SIG = 1.5f;
        float srLog2    = MathF.Log2(SR_LO / SR_CTR);
        float srInv2Sg  = 1f / (2f * SR_SIG * SR_SIG);
        float srGlideCf = 1f - MathF.Exp(-1f / (0.5f * sampleRate));
        float[] srMult   = [1f, 2f, 4f, 8f, 16f, 32f, 64f];
        float[] srPhases = new float[NSR];
        float srBase = 0f, srGlideSm = 0f, srGlideT = 0f;

        // Lush reverb — Freeverb-style mono tail (~2 s decay), added to both stereo channels.
        const int    RevPreD = 882;
        const int    RevCm0D = 2111, RevCm1D = 2237, RevCm2D = 2381, RevCm3D = 2521;
        const int    RevAp0D = 601,  RevAp1D = 441,  RevAp2D = 341,  RevAp3D = 225;
        const float  RevFb   = 0.86f, RevDamp = 0.20f, RevApG = 0.50f, RevWet = 0.25f;
        var revPre  = new float[RevPreD];
        var revCm0  = new float[RevCm0D + 1]; var revCm1 = new float[RevCm1D + 1];
        var revCm2  = new float[RevCm2D + 1]; var revCm3 = new float[RevCm3D + 1];
        var revAp0  = new float[RevAp0D + 1]; var revAp1 = new float[RevAp1D + 1];
        var revAp2  = new float[RevAp2D + 1]; var revAp3 = new float[RevAp3D + 1];
        int revPreW = 0, revCm0w = 0, revCm1w = 0, revCm2w = 0, revCm3w = 0;
        int revAp0w = 0, revAp1w = 0, revAp2w = 0, revAp3w = 0;
        float revCm0lpf = 0f, revCm1lpf = 0f, revCm2lpf = 0f, revCm3lpf = 0f;

        int idx = 0;
        foreach (var frame in frames)
        {
            // Camera basis — guard against uninitialised zero vectors
            Vector3 camFwd = frame.CameraForward.LengthSquared() > 0.01f
                ? Vector3.Normalize(frame.CameraForward) : -Vector3.UnitZ;
            Vector3 upHint = frame.CameraUp.LengthSquared() > 0.01f
                ? frame.CameraUp : Vector3.UnitY;
            Vector3 camRight = Vector3.Normalize(Vector3.Cross(camFwd, upHint));
            Vector3 camUp    = Vector3.Cross(camRight, camFwd);

            // Preprocess orbit segments and compute per-cell LPF cutoff
            var  newSeg    = new Vector3[NCells][];
            var  lpfCoeff  = new float[NCells];
            var  voiceGain = new float[NCells];
            for (int ci = 0; ci < NCells; ci++)
            {
                FractalSonicCell? cell = (frame.Cells != null && ci < frame.Cells.Length)
                    ? frame.Cells[ci] : null;
                newSeg[ci] = PreprocessOrbit(cell?.OrbitTrajectory);
                // Depth-modulated LPF: mean forward projection of orbit → [2 kHz, 8 kHz]
                float depth = MeanDepth(newSeg[ci], camFwd);
                float dn    = Math.Clamp((depth + 1f) / 2f, 0f, 1f);
                float lpfHz = 2000f + dn * 6000f;
                lpfCoeff[ci] = 1f - MathF.Exp(-2f * MathF.PI * lpfHz / sampleRate);

                float prms = ProjectedRms(newSeg[ci], camRight, camUp);
                voiceGain[ci] = prms < 1e-6f ? 0f : Math.Clamp(MinProjectedRms / prms, 1f, MaxProjectionBoost);
            }

            srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

            // Energy-weighted horizontal centroid of the fractal on screen → Shepard pan.
            // Fractal drifting left makes the Shepard drone follow it left, not sit centred.
            float ePanNum = 0f, ePanDen = 0f;
            if (frame.Cells != null)
                for (int ci = 0; ci < frame.Cells.Length && ci < NCells; ci++)
                {
                    float e = MathF.Max(0f, frame.Cells[ci].Energy);
                    ePanNum += e * (((ci % 4) + 0.5f) / 4f * 2f - 1f);
                    ePanDen += e;
                }
            float shepPan = ePanDen > 1e-4f ? ePanNum / ePanDen : 0f;
            // √2 scale so the centre case (gL = gR = 0.707) matches the old full-mono level.
            float shepGL = MathF.Cos((shepPan + 1f) * MathF.PI / 4f) * 1.41421356f;
            float shepGR = MathF.Sin((shepPan + 1f) * MathF.PI / 4f) * 1.41421356f;

            int end = Math.Min(idx + spf * 2, output.Length);
            int si  = 0;
            while (idx < end)
            {
                float sumL = 0f, sumR = 0f;

                for (int ci = 0; ci < NCells; ci++)
                {
                    float rawL, rawR;
                    if (si < XfadeSamples)
                    {
                        float t        = (float)si / XfadeSamples;
                        float prevGain = MathF.Cos(t * MathF.PI / 2f); // 1 → 0
                        float newGain  = MathF.Sin(t * MathF.PI / 2f); // 0 → 1

                        // Prev: continue from where it left off, clamped to avoid end-of-orbit wrap
                        float prevP = MathF.Min(prevPhase[ci] + si / cellSps[ci],
                                                OrbtLen - 1.001f);
                        var (pL, pR, _) = Project(prevSeg[ci], prevP, camRight, camUp, camFwd);

                        float newP = si / cellSps[ci];
                        var (nL, nR, _) = Project(newSeg[ci], newP, camRight, camUp, camFwd);

                        rawL = prevGain * pL + newGain * nL;
                        rawR = prevGain * pR + newGain * nR;
                    }
                    else
                    {
                        float phase = si / cellSps[ci];
                        var (nL, nR, _) = Project(newSeg[ci], phase, camRight, camUp, camFwd);
                        rawL = nL;
                        rawR = nR;
                    }

                    // DC blocker: y[n] = x[n] − x[n−1] + R·y[n−1]
                    float hl = rawL - dcXL[ci] + hpfR * dcHL[ci];
                    float hr = rawR - dcXR[ci] + hpfR * dcHR[ci];
                    dcXL[ci] = rawL; dcXR[ci] = rawR;
                    dcHL[ci] = hl;   dcHR[ci] = hr;

                    // One-pole LPF (depth-modulated per-cell cutoff)
                    float cf = lpfCoeff[ci];
                    lpfL[ci] += cf * (hl - lpfL[ci]);
                    lpfR[ci] += cf * (hr - lpfR[ci]);

                    // Spectral-tilt elevation shelf (top rows bright, bottom rows dark)
                    float tilt = rowTilt[ci / 4];
                    tiltLpfL[ci] += tiltCf * (lpfL[ci] - tiltLpfL[ci]);
                    tiltLpfR[ci] += tiltCf * (lpfR[ci] - tiltLpfR[ci]);
                    float tiltedL = lpfL[ci] + tilt * (lpfL[ci] - tiltLpfL[ci]);
                    float tiltedR = lpfR[ci] + tilt * (lpfR[ci] - tiltLpfR[ci]);

                    // Mix into stereo output with column panning
                    float cellGain = CellScale * voiceGain[ci];
                    sumL += tiltedL * panL[ci] * cellGain;
                    sumR += tiltedR * panR[ci] * cellGain;
                }

                // Shepard–Risset zoom layer (mono centre, 0.065 gain — matches HybridSynth)
                srGlideSm += srGlideCf * (srGlideT - srGlideSm);
                srBase += srGlideSm / (12f * sampleRate);
                if (srBase >= 1f) srBase -= 1f; else if (srBase < 0f) srBase += 1f;
                float srBf = SR_LO * MathF.Pow(2f, srBase);
                float shep = 0f;
                for (int i = 0; i < NSR; i++)
                {
                    srPhases[i] = (srPhases[i] + srBf * srMult[i] / sampleRate) % 1f;
                    float logOct = srLog2 + srBase + i;
                    shep += MathF.Exp(-logOct * logOct * srInv2Sg) * MathF.Sin(2f * MathF.PI * srPhases[i]);
                }
                shep = shep / NSR * 0.065f;

                // Freeverb reverb: mono input → pre-delay → 4 comb-LPF → 4 allpass → bloom
                float revIn  = (sumL + sumR) * 0.70710678f;
                float preOut = revPre[revPreW]; revPre[revPreW] = revIn;
                revPreW = (revPreW + 1) % RevPreD;
                float cSum = RevCombLpf(preOut, revCm0, ref revCm0w, RevCm0D, RevFb, ref revCm0lpf, RevDamp)
                           + RevCombLpf(preOut, revCm1, ref revCm1w, RevCm1D, RevFb, ref revCm1lpf, RevDamp)
                           + RevCombLpf(preOut, revCm2, ref revCm2w, RevCm2D, RevFb, ref revCm2lpf, RevDamp)
                           + RevCombLpf(preOut, revCm3, ref revCm3w, RevCm3D, RevFb, ref revCm3lpf, RevDamp);
                float rOut = RevAllPass(cSum * 0.25f, revAp0, ref revAp0w, RevAp0D, RevApG);
                rOut = RevAllPass(rOut, revAp1, ref revAp1w, RevAp1D, RevApG);
                rOut = RevAllPass(rOut, revAp2, ref revAp2w, RevAp2D, RevApG);
                rOut = RevAllPass(rOut, revAp3, ref revAp3w, RevAp3D, RevApG);
                float bloom = rOut * RevWet;
                // Master bus: tanh limiter with reverb bloom, Shepard panned to the fractal
                float sigL = 0.85f * (float)Math.Tanh((sumL + bloom) * 1.1f) + shep * shepGL;
                float sigR = 0.85f * (float)Math.Tanh((sumR + bloom) * 1.1f) + shep * shepGR;
                output[idx++] = Clip16(sigL);
                output[idx++] = Clip16(sigR);
                si++;
            }

            // Carry prev state forward for next frame's crossfade
            for (int ci = 0; ci < NCells; ci++)
            {
                prevSeg[ci]   = newSeg[ci];
                prevPhase[ci] = spf / cellSps[ci];
            }
        }
        return output;
    }

    // ── Preprocessing ────────────────────────────────────────────────────────

    // Centroid subtract → peak normalise → zero escaped-orbit tail.
    // Amplitude is weighted by sqrt(bounded fraction) so cells in empty space fade
    // proportionally — escaped cells do not contribute noise to the stereo field.
    private static Vector3[] PreprocessOrbit(Vector4[]? trajectory)
    {
        var result = new Vector3[OrbtLen];
        if (trajectory == null) return result;

        int n = Math.Min(trajectory.Length, OrbtLen);

        // Centroid over bounded points (w > 0.5)
        Vector3 centroid = Vector3.Zero;
        int cnt = 0;
        for (int i = 0; i < n; i++)
            if (trajectory[i].W > 0.5f)
            { centroid += new Vector3(trajectory[i].X, trajectory[i].Y, trajectory[i].Z); cnt++; }
        if (cnt > 0) centroid /= cnt;

        // Centroid-subtract and locate first escaped point
        int bailout = n;
        for (int i = 0; i < n; i++)
        {
            if (trajectory[i].W < 0.5f && bailout == n) bailout = i;
            result[i] = new Vector3(trajectory[i].X, trajectory[i].Y, trajectory[i].Z) - centroid;
        }

        // Peak magnitude over bounded segment only
        float peak = 0f;
        for (int i = 0; i < bailout; i++) peak = MathF.Max(peak, result[i].Length());

        // Amplitude shaping: floor + sqrt(bounded fraction).
        //   bailout == 0 (immediately escaped, empty space) → 0   (preserves the stereo gate)
        //   any bounded content                            → 0.30 .. 1.0
        // The 0.30 floor keeps partially-bounded cells (a complex fractal filling the
        // screen has content in most cells) clearly audible so all 16 voices form a wall,
        // while genuinely empty cells stay silent so the field still tracks screen position.
        float boundedFraction = n > 0 ? (float)bailout / n : 0f;
        float shaped = bailout == 0 ? 0f : 0.30f + 0.70f * MathF.Sqrt(boundedFraction);
        float gain = peak < 1e-6f ? 0f : shaped / peak;

        // Apply gain to bounded segment; zero post-bailout tail entirely.
        for (int i = 0; i < n; i++)
            result[i] = i < bailout ? result[i] * gain : Vector3.Zero;
        return result;
    }

    // Mean forward-depth projection (used to compute per-cell LPF cutoff).
    private static float MeanDepth(Vector3[] seg, Vector3 camFwd)
    {
        float sum = 0f;
        for (int i = 0; i < seg.Length; i++) sum += Vector3.Dot(seg[i], camFwd);
        return sum / seg.Length;
    }

    private static float ProjectedRms(Vector3[] seg, Vector3 camRight, Vector3 camUp)
    {
        double sum = 0.0;
        for (int i = 0; i < seg.Length; i++)
        {
            float pr = Vector3.Dot(seg[i], camRight);
            float pu = Vector3.Dot(seg[i], camUp);
            float l = (pr + pu) * 0.70710678f;
            float r = (pr - pu) * 0.70710678f;
            sum += l * l + r * r;
        }
        return (float)Math.Sqrt(sum / Math.Max(1, seg.Length * 2));
    }

    // Cosine-interpolated sample from a preprocessed orbit segment.
    // Returns (L, R, depth) projections onto the camera basis.
    // L/R use a 45° rotation in the camRight–camUp plane so that 1D orbits along
    // camRight (common when seeds land on y=z=0) feed both channels equally; stereo
    // positioning is then determined by the column panning, not the projection alone.
    private static (float L, float R, float Depth) Project(
        Vector3[] seg, float phase, Vector3 camRight, Vector3 camUp, Vector3 camFwd)
    {
        int len = seg.Length;
        int i0  = (int)phase % len;
        int i1  = (i0 + 1) % len;
        float f = phase - MathF.Floor(phase);
        float t = 0.5f - 0.5f * MathF.Cos(MathF.PI * f); // FSE cosine interp
        var p  = seg[i0] + t * (seg[i1] - seg[i0]);
        float pr = Vector3.Dot(p, camRight);
        float pu = Vector3.Dot(p, camUp);
        const float Sq2Inv = 0.70710678f;
        return ((pr + pu) * Sq2Inv, (pr - pu) * Sq2Inv, Vector3.Dot(p, camFwd));
    }

    private static short Clip16(float s) =>
        (short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue);

    private static float RevCombLpf(float x, float[] buf, ref int w, int D,
                                     float fb, ref float lpf, float damp)
    {
        int len = buf.Length;
        int rp  = (w - D + len) % len;
        lpf += damp * (buf[rp] - lpf);
        float y = Math.Clamp(x + fb * lpf, -12f, 12f);
        buf[w] = y;
        w = (w + 1) % len;
        return y;
    }

    private static float RevAllPass(float x, float[] buf, ref int w, int D, float g)
    {
        int len = buf.Length;
        int rp  = (w - D + len) % len;
        float y = -g * x + buf[rp];
        buf[w] = x + g * y;
        w = (w + 1) % len;
        return y;
    }
}
