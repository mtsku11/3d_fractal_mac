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

    // Proximity/enclosure macros (kept in sync with FractalDroneStream.FillDirectOrbit):
    //   proximity = exp(−MeanDepth/ProxRefDist) gated by HitRatio → master brightness/bass/level
    //   enclosure = HitRatio → reverb feedback/damp/wet (open void = short dry, inside = long bloom)
    private const float ProxRefDist  = 2.5f;   // world units — fractals are roughly unit-scale
    private const float MacroSlewTau = 0.35f;  // s — one-pole slew on prox/encl per control frame

    // Fold-event layer: per-cell orbit delta between consecutive frames fires a chime.
    private const int   MaxChimesPerFrame = 3;
    private const float ChimeRefractorySec = 0.25f;
    private const float MorphAttackTau  = 0.08f;
    private const float MorphReleaseTau = 1.2f;

    public static short[] Synthesize(
        IReadOnlyList<FractalSonicFrame> frames,
        double controlRateHz = 30.0,
        int sampleRate       = DefaultSampleRate,
        FractalVoice voice   = FractalVoice.Mandelbox)
    {
        if (frames.Count == 0) return [];
        var profile = DirectOrbitProfile.ForVoice(voice);

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

        // Fold-event chimes: decaying two-partial sines at 8× each cell's orbit fundamental
        var chimeEnv = new float[NCells];
        var chimePh1 = new float[NCells];
        var chimePh2 = new float[NCells];
        var chimeFreq = new float[NCells];
        var foldDelta = new float[NCells];
        var refract   = new int[NCells];
        float chimeDecay = MathF.Exp(-1f / (profile.ChimeDecaySec * sampleRate));
        int refractFrames = (int)MathF.Ceiling(ChimeRefractorySec * (float)controlRateHz);

        // Proximity/enclosure macro state + morph bus
        float dtFrame  = 1f / (float)controlRateHz;
        float macroCf  = 1f - MathF.Exp(-dtFrame / MacroSlewTau);
        float morphAtkCf = 1f - MathF.Exp(-dtFrame / MorphAttackTau);
        float morphRelCf = 1f - MathF.Exp(-dtFrame / MorphReleaseTau);
        float prox = 0f, encl = 0f, morph = 0f;
        float mLpfL = 0f, mLpfR = 0f, mBassL = 0f, mBassR = 0f;
        float bassCf = 1f - MathF.Exp(-2f * MathF.PI * 180f / sampleRate);
        var cellSpsEff = new float[NCells];

        // Per-cell step rates: bottom row is each column's root; rows and columns ascend
        // by the profile/geometry lattice generator. Recomputed per frame so a
        // geometry-derived LatticeRatio retunes the grid live (see frame loop).
        var cellSps = new float[NCells];

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
        const float  RevApG = 0.50f;
        // fb/damp/wet are enclosure-driven per frame: open void → short dry tail,
        // deep inside the fractal → long bright bloom (T60 ≈ 0.8 s → ~5 s).
        float revFb = 0.70f, revDamp = 0.35f, revWet = 0.10f;
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

            // ── Proximity/enclosure macros (slewed) ──
            // prox: 1 at the surface, →0 far away; gated by HitRatio so an empty view
            // (MeanDepth reported as 0 when no rays hit) never reads as "close".
            float proxT = MathF.Exp(-MathF.Max(0f, frame.MeanDepth) / ProxRefDist)
                        * Math.Min(1f, frame.HitRatio * 5f);
            float enclT = Math.Clamp(frame.HitRatio, 0f, 1f);
            prox += macroCf * (proxT - prox);
            encl += macroCf * (enclT - encl);
            float mCutHz   = 1200f + 8800f * MathF.Pow(prox, 0.7f);
            float mCf      = 1f - MathF.Exp(-2f * MathF.PI * mCutHz / sampleRate);
            float dryGain  = 0.45f + 0.55f * prox;
            float bassAmt  = 0.9f * prox;
            revFb   = profile.RevFb0   + (profile.RevFb1   - profile.RevFb0)   * encl;
            revDamp = profile.RevDamp0 + (profile.RevDamp1 - profile.RevDamp0) * encl;
            revWet  = profile.RevWet0  + (profile.RevWet1  - profile.RevWet0)  * encl;

            // ── Morph bus: ParameterVelocity → shimmer + fold sensitivity ──
            float morphT = Math.Clamp(frame.ParameterVelocity * 4f, 0f, 1f);
            morph += (morphT > morph ? morphAtkCf : morphRelCf) * (morphT - morph);

            // ── Lattice: geometry-derived generator when present, else profile default.
            // Recomputed per frame so parameter morphs retune the whole grid live.
            // Phase restarts each frame with a 5 ms crossfade, so per-frame step-rate
            // changes (retune + the ±~35-cent morph shimmer below) are click-free.
            float ratio = frame.LatticeRatio > 1.001f ? frame.LatticeRatio : profile.LatticeRatio;
            for (int ci = 0; ci < NCells; ci++)
            {
                float pitchMul = profile.RootDivisor
                               * MathF.Pow(ratio, ci % 4)        // column root
                               * MathF.Pow(ratio, 3 - ci / 4);   // row above bottom
                cellSps[ci]   = SamplesPerStep / pitchMul;
                chimeFreq[ci] = sampleRate * 8f / (cellSps[ci] * OrbtLen);
                cellSpsEff[ci] = cellSps[ci] /
                    (1f + morph * 0.02f * MathF.Sin(2f * MathF.PI * (0.6f + 0.37f * ci) * (float)frame.Time));
            }

            // ── Fold-event detection: frame-to-frame orbit delta per cell ──
            // Both segments are peak-normalised, so the mean pointwise distance is a
            // scale-free measure of how much that cell's geometry just changed.
            for (int ci = 0; ci < NCells; ci++)
            {
                if (refract[ci] > 0) refract[ci]--;
                float dSum = 0f, pSum = 0f;
                for (int i = 0; i < OrbtLen; i++)
                {
                    dSum += (newSeg[ci][i] - prevSeg[ci][i]).Length();
                    pSum += prevSeg[ci][i].LengthSquared();
                }
                // A cell appearing from silence (startup, or entering view) is not a fold
                foldDelta[ci] = pSum < 1e-6f ? 0f : dSum / OrbtLen;
            }
            float foldThr = 0.45f - 0.25f * morph;
            for (int k = 0; k < MaxChimesPerFrame; k++)
            {
                int best = -1; float bestDelta = foldThr;
                for (int ci = 0; ci < NCells; ci++)
                    if (refract[ci] == 0 && foldDelta[ci] > bestDelta)
                    { best = ci; bestDelta = foldDelta[ci]; }
                if (best < 0) break;
                chimeEnv[best] = Math.Min(0.9f, (bestDelta - foldThr) * 1.8f) * 0.6f;
                chimePh1[best] = 0f; chimePh2[best] = 0f;
                refract[best]  = refractFrames;
                foldDelta[best] = 0f; // exclude from remaining picks this frame
            }

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
                        float prevP = MathF.Min(prevPhase[ci] + si / cellSpsEff[ci],
                                                OrbtLen - 1.001f);
                        var (pL, pR, _) = Project(prevSeg[ci], prevP, camRight, camUp, camFwd);

                        float newP = si / cellSpsEff[ci];
                        var (nL, nR, _) = Project(newSeg[ci], newP, camRight, camUp, camFwd);

                        rawL = prevGain * pL + newGain * nL;
                        rawR = prevGain * pR + newGain * nR;
                    }
                    else
                    {
                        float phase = si / cellSpsEff[ci];
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

                // Fold-event chimes: two-partial decaying sines at the cell's pan position
                for (int ci = 0; ci < NCells; ci++)
                {
                    if (chimeEnv[ci] < 1e-4f) continue;
                    chimePh1[ci] += chimeFreq[ci] / sampleRate;
                    chimePh2[ci] += chimeFreq[ci] * profile.ChimePartial / sampleRate;
                    if (chimePh1[ci] >= 1f) chimePh1[ci] -= 1f;
                    if (chimePh2[ci] >= 1f) chimePh2[ci] -= 1f;
                    float cs = chimeEnv[ci] * (MathF.Sin(2f * MathF.PI * chimePh1[ci])
                             + 0.35f * MathF.Sin(2f * MathF.PI * chimePh2[ci]));
                    chimeEnv[ci] *= chimeDecay;
                    sumL += cs * panL[ci] * 0.5f;
                    sumR += cs * panR[ci] * 0.5f;
                }

                // Proximity master chain: brightness LPF + bass lift + level (far = dark/thin/quiet)
                mLpfL += mCf * (sumL - mLpfL);
                mLpfR += mCf * (sumR - mLpfR);
                mBassL += bassCf * (mLpfL - mBassL);
                mBassR += bassCf * (mLpfR - mBassR);
                float dryL = (mLpfL + bassAmt * mBassL) * dryGain;
                float dryR = (mLpfR + bassAmt * mBassR) * dryGain;

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

                // Freeverb reverb: mono input → pre-delay → 4 comb-LPF → 4 allpass → bloom.
                // fb/damp/wet are enclosure-driven (set per control frame above).
                float revIn  = (dryL + dryR) * 0.70710678f;
                float preOut = revPre[revPreW]; revPre[revPreW] = revIn;
                revPreW = (revPreW + 1) % RevPreD;
                float cSum = RevCombLpf(preOut, revCm0, ref revCm0w, RevCm0D, revFb, ref revCm0lpf, revDamp)
                           + RevCombLpf(preOut, revCm1, ref revCm1w, RevCm1D, revFb, ref revCm1lpf, revDamp)
                           + RevCombLpf(preOut, revCm2, ref revCm2w, RevCm2D, revFb, ref revCm2lpf, revDamp)
                           + RevCombLpf(preOut, revCm3, ref revCm3w, RevCm3D, revFb, ref revCm3lpf, revDamp);
                float rOut = RevAllPass(cSum * 0.25f, revAp0, ref revAp0w, RevAp0D, RevApG);
                rOut = RevAllPass(rOut, revAp1, ref revAp1w, RevAp1D, RevApG);
                rOut = RevAllPass(rOut, revAp2, ref revAp2w, RevAp2D, RevApG);
                rOut = RevAllPass(rOut, revAp3, ref revAp3w, RevAp3D, RevApG);
                float bloom = rOut * revWet;
                // Master bus: tanh limiter with reverb bloom, Shepard panned to the fractal
                float sigL = 0.85f * (float)Math.Tanh((dryL + bloom) * 1.1f) + shep * shepGL;
                float sigR = 0.85f * (float)Math.Tanh((dryR + bloom) * 1.1f) + shep * shepGR;
                output[idx++] = Clip16(sigL);
                output[idx++] = Clip16(sigR);
                si++;
            }

            // Carry prev state forward for next frame's crossfade
            for (int ci = 0; ci < NCells; ci++)
            {
                prevSeg[ci]   = newSeg[ci];
                prevPhase[ci] = spf / cellSpsEff[ci];
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
