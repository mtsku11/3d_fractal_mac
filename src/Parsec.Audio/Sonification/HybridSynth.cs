using System.Numerics;

namespace Parsec.Audio.Sonification;

/// <summary>
/// Which single-cycle wavetable source to use for the drone oscillator.
/// </summary>
public enum WavetableSource
{
    /// <summary>DE distances along the tile-centre ray. Camera-dependent timbre.</summary>
    RaySteps,
    /// <summary>length(z) at each inner-iteration step. Parameter-dependent timbre.</summary>
    OrbitMags,
    /// <summary>M8: 64-sample Lissajous 3:2:1 DE field scan. Uses frame.FieldScanWaveform directly.</summary>
    FieldScan,
}

/// <summary>
/// M7c offline hybrid synth: wavetable drone (timbre from fractal geometry) +
/// struck modal resonators per spatial cell (JI-tuned bell voices).
///
/// Drone: a single-cycle wavetable oscillator whose waveform morphs each frame
/// from the most energetic spatial cell. Two sources for A/B comparison:
///   RaySteps  — DE distances along the centre ray; timbre changes with camera.
///   OrbitMags — inner-iteration orbit magnitudes; timbre changes with parameters.
///
/// Bells: 16 exponentially-decaying sinusoids (one per spatial cell). Triggered
/// when a cell's energy rises. Pitch snapped to JI Dorian at A2 (110 Hz).
///
/// Temperament: NormalVariance → TemperamentStrength — smooth geometry yields
/// consonant JI tuning; chaotic geometry drifts microtonal. Ceiling is a global
/// knob passed at synthesis time.
///
/// Mix: drone (dry) + Schroeder-reverb'd bell bus.
/// </summary>
public static class HybridSynth
{
    public const int DefaultSampleRate = 44100;

    private const int WtLen  = 64;
    private const int NCells = 16;

    public static short[] Synthesize(
        IReadOnlyList<FractalSonicFrame> frames,
        WavetableSource wavetableSource  = WavetableSource.OrbitMags,
        float temperamentCeiling         = 0.9f,
        double controlRateHz             = 30.0,
        int sampleRate                   = DefaultSampleRate,
        bool droneOnly                   = false,
        // M7g: route Apollonian frames through the geometry-scale bell voice
        FractalVoice voice               = FractalVoice.Mandelbox)
    {
        if (frames.Count == 0) return [];

        int spf = (int)Math.Round(sampleRate / controlRateHz);
        // Stereo interleaved output: [L0, R0, L1, R1, ...]
        var output = new short[frames.Count * spf * 2];

        var quantizer = new JiQuantizer(JiScale.Dorian, rootHz: 110f);

        // --- Drone oscillator (4-corner stereo: TL=0, TR=1, BL=2, BR=3) ---
        float dronePhase  = 0f;
        float dronePitch  = 110f;
        float dronePitchS = 110f;   // slewed pitch (portamento)
        float pitchSlewCf = Coeff(0.28f, sampleRate);
        var currentWt = new float[4][];
        var targetWt  = new float[4][];
        for (int q = 0; q < 4; q++)
        {
            currentWt[q] = new float[WtLen];
            targetWt[q]  = new float[WtLen];
            for (int wi = 0; wi < WtLen; wi++)
                currentWt[q][wi] = targetWt[q][wi] = 2f * wi / (WtLen - 1f) - 1f;
        }
        // Spectral-tilt shelf: TL/TR (top, above centre) = bright; BL/BR (bottom) = dark.
        // 1-pole shelf at ~2 kHz: boost highs for positive tilt, cut for negative.
        var droneShelfLpf = new float[4];
        float tiltShelfCf = 1f - MathF.Exp(-2f * MathF.PI * 2000f / sampleRate);
        // tiltAmt[q]: TL=+0.35, TR=+0.35, BL=-0.35, BR=-0.35
        float[] droneTiltAmt = [0.35f, 0.35f, -0.35f, -0.35f];

        // Stereo drone: left half of the screen (columns 0,1) feeds the left corners (TL,BL),
        // right half (columns 2,3) feeds the right corners (TR,BR). Per-channel balance gain
        // tracks where the fractal's energy sits, so the drone follows it across the field
        // instead of sitting mono. dBalL/dBalR are slewed to avoid zipper noise on changes.
        var leftBlend  = new float[WtLen];
        var rightBlend = new float[WtLen];
        float dBalL = 1f, dBalR = 1f;            // slewed balance gains
        float dBalLT = 1f, dBalRT = 1f;          // per-frame targets
        float balSlewCf = Coeff(0.05f, sampleRate);

        // --- Modal resonators (one per spatial cell) ---
        var resGain  = new float[NCells];  // instantaneous gain (decays multiplicatively)
        var resDecay = new float[NCells];  // per-sample decay factor
        var resOscPh = new float[NCells];  // oscillator phase [0, 1)
        var resFreq  = new float[NCells];  // Hz
        var resPanL  = new float[NCells];  // constant-power left gain
        var resPanR  = new float[NCells];  // constant-power right gain
        var prevEnergy = new float[NCells];
        // Initialise decay to a slow value so untriggered resonators stay silent
        // Initialise pan to centre
        for (int ci = 0; ci < NCells; ci++)
        {
            resDecay[ci] = MathF.Exp(-1f / (0.5f * sampleRate));
            resPanL[ci]  = resPanR[ci] = 0.707107f; // centre = equal power
        }
        // Spectral-tilt elevation for bells: row 0 (top) = bright, row 3 (bottom) = dark.
        // Shares the same shelf corner frequency as the drone (tiltShelfCf declared above).
        var bellShelfLpf = new float[NCells];
        // bellRowTilt[row]: top row = +0.35, bottom row = -0.35
        float[] bellRowTilt = [0.35f, 0.12f, -0.12f, -0.35f];

        // --- Schroeder reverb for bell bus ---
        int ap0 = ScaleDelay(1051, sampleRate), ap1 = ScaleDelay(1567, sampleRate),
            ap2 = ScaleDelay(2203, sampleRate), ap3 = ScaleDelay(3251, sampleRate);
        int cm0 = ScaleDelay(4799, sampleRate), cm1 = ScaleDelay(5399, sampleRate);
        float[] apB0 = new float[ap0 + 1], apB1 = new float[ap1 + 1],
                apB2 = new float[ap2 + 1], apB3 = new float[ap3 + 1];
        float[] cmB0 = new float[cm0 + 1], cmB1 = new float[cm1 + 1];
        int aw0 = 0, aw1 = 0, aw2 = 0, aw3 = 0, cw0 = 0, cw1 = 0;
        float cmLpf0 = 0f, cmLpf1 = 0f;

        float gS     = 0.05f;
        float gcf    = Coeff(0.08f, sampleRate);
        float morphCf = droneOnly ? Coeff(0.008f, sampleRate) : Coeff(0.05f, sampleRate);

        // M7g: Apollonian voice — timer-gated bell triggers (no cell telemetry)
        float[] apollonianDefault = [110f, 110f * 35f/32f, 110f * 19f/16f, 165f, 110f * 15f/8f];
        bool isApolloVoice = voice == FractalVoice.Apollonian;
        var resAgeFrames  = new int[NCells];   // frames since last trigger per resonator
        int apoCounter   = 0;
        int apoNextPitch = 0;

        // --- M7h waveshaper voice ---
        var wsTableCurr   = new float[WtLen];
        var wsTableTarget = new float[WtLen];
        float wsPhase   = 0f;
        bool  wsHasData = false;
        float wsMorphCf = Coeff(0.05f, sampleRate);

        // --- Shepard–Risset zoom layer (M7f) ---
        const int   NSR      = 7;
        const float SR_LO    = 27.5f;
        const float SR_CTR   = 220f;
        const float SR_SIG   = 1.5f;
        float srLog2    = MathF.Log2(SR_LO / SR_CTR);           // -3
        float srInv2Sg  = 1f / (2f * SR_SIG * SR_SIG);
        float srGlideCf = Coeff(0.5f, sampleRate);
        float[] srMult  = [1f, 2f, 4f, 8f, 16f, 32f, 64f];
        float[] srPhases = new float[NSR];
        float srBase     = 0f;
        float srGlideSm  = 0f;
        float srGlideT   = 0f;

        int idx = 0;
        foreach (var frame in frames)
        {
            float gT = isApolloVoice
                ? 0.55f  // Apollonian: constant gain (no HitRatio telemetry)
                : 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(frame.HitRatio, 0f, 1f));

            // Geometry-driven temperament: smooth surface → JI order; chaos → microtonal
            float chaosNorm = Math.Clamp(frame.NormalVariance * 2.5f, 0f, 1f);
            quantizer.TemperamentStrength = Math.Clamp((1f - chaosNorm) * temperamentCeiling, 0f, 1f);

            // Wavetable source selection
            if (!isApolloVoice && wavetableSource == WavetableSource.FieldScan)
            {
                // M8-spatial: 4-corner field-scan waveforms drive each corner oscillator independently.
                if (frame.FieldScanWaveformTL is { Length: >= WtLen } fsTL) Array.Copy(fsTL, targetWt[0], WtLen);
                if (frame.FieldScanWaveformTR is { Length: >= WtLen } fsTR) Array.Copy(fsTR, targetWt[1], WtLen);
                if (frame.FieldScanWaveformBL is { Length: >= WtLen } fsBL) Array.Copy(fsBL, targetWt[2], WtLen);
                if (frame.FieldScanWaveformBR is { Length: >= WtLen } fsBR) Array.Copy(fsBR, targetWt[3], WtLen);
            }
            else if (!isApolloVoice && frame.Cells != null
                     && wavetableSource != WavetableSource.FieldScan)
            {
                // Column-split energy-weighted blend (M7c/d/e + stereo): left columns (0,1)
                // drive the left corners, right columns (2,3) the right corners.
                Array.Clear(leftBlend);
                Array.Clear(rightBlend);
                float lW = 0f, rW = 0f;
                int nC = Math.Min(frame.Cells.Length, NCells);
                for (int ci = 0; ci < nC; ci++)
                {
                    var bcell = frame.Cells[ci];
                    float[]? bwt = wavetableSource == WavetableSource.RaySteps
                        ? bcell.RayWavetable : bcell.OrbitWavetable;
                    if (bwt == null || bwt.Length < WtLen || bcell.Energy <= 0.05f) continue;
                    if ((ci % 4) < 2)
                    {
                        for (int wi = 0; wi < WtLen; wi++) leftBlend[wi] += bwt[wi] * bcell.Energy;
                        lW += bcell.Energy;
                    }
                    else
                    {
                        for (int wi = 0; wi < WtLen; wi++) rightBlend[wi] += bwt[wi] * bcell.Energy;
                        rW += bcell.Energy;
                    }
                }
                if (lW > 0f)
                    for (int wi = 0; wi < WtLen; wi++)
                    { float v = leftBlend[wi] / lW;  targetWt[0][wi] = v; targetWt[2][wi] = v; }
                if (rW > 0f)
                    for (int wi = 0; wi < WtLen; wi++)
                    { float v = rightBlend[wi] / rW; targetWt[1][wi] = v; targetWt[3][wi] = v; }
                // Balance gain: a fractal sitting on one side fades the other side's drone.
                float totW = lW + rW;
                dBalLT = totW > 1e-4f ? MathF.Min(1.25f, MathF.Sqrt(2f * lW / totW)) : 1f;
                dBalRT = totW > 1e-4f ? MathF.Min(1.25f, MathF.Sqrt(2f * rW / totW)) : 1f;
            }

            float rawDrone = 110f * MathF.Pow(2f, Math.Clamp(frame.TrapMean.X * 0.3f, -0.2f, 0.8f));
            dronePitch = quantizer.Quantize(rawDrone);
            srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

            // Shepard pan: energy-weighted on-screen centroid → drone follows the fractal.
            // √2 scale so the centred case matches the old full-mono level.
            float ePanNum = 0f, ePanDen = 0f;
            if (frame.Cells != null)
                for (int ci = 0; ci < frame.Cells.Length && ci < NCells; ci++)
                {
                    float e = MathF.Max(0f, frame.Cells[ci].Energy);
                    ePanNum += e * (((ci % 4) + 0.5f) / 4f * 2f - 1f);
                    ePanDen += e;
                }
            float shepPan = ePanDen > 1e-4f ? ePanNum / ePanDen : 0f;
            float shepGL  = MathF.Cos((shepPan + 1f) * MathF.PI / 4f) * 1.41421356f;
            float shepGR  = MathF.Sin((shepPan + 1f) * MathF.PI / 4f) * 1.41421356f;

            // M7h: update waveshaper target (Apollonian Hybrid remains timer-bell only)
            if (!isApolloVoice && frame.WaveshaperCurve is { Length: >= WtLen } wsc)
            {
                Array.Copy(wsc, wsTableTarget, WtLen);
                wsHasData = true;
            }

            // Update resonators from cells (or from the Apollonian Hybrid timer-bell voice)
            if (isApolloVoice)
            {
                // Apollonian: trigger interval from camera speed
                float[] ap = frame.GeometryPitches ?? apollonianDefault;
                int trigInterval = (int)(sampleRate *
                    Math.Clamp(0.8f - frame.CameraSpeed * 0.11f, 0.25f, 0.9f));
                apoCounter += spf;
                while (apoCounter >= trigInterval)
                {
                    apoCounter -= trigInterval;
                    int pi   = apoNextPitch % ap.Length;
                    int oct  = (apoNextPitch / ap.Length) % 3;
                    int slot = apoNextPitch % NCells;
                    resFreq[slot]  = ap[pi] * MathF.Pow(2f, oct);
                    resGain[slot]  = 0.82f;
                    float decayS   = 1.0f + (pi / (float)(ap.Length - 1)) * 1.5f;
                    resDecay[slot] = MathF.Exp(-1f / (decayS * sampleRate));
                    apoNextPitch++;
                }
            }
            else if (frame.Cells != null)
            {
                const int RetrigFrames = 105; // retrigger every ~3.5 s at 30 fps
                int nActive = Math.Min(frame.Cells.Length, NCells);
                for (int ci = 0; ci < nActive; ci++)
                {
                    var cell     = frame.Cells[ci];
                    float energy = cell.Energy;
                    bool wasActive  = prevEnergy[ci] >= 0.12f;
                    bool ageRetrig  = resAgeFrames[ci] >= RetrigFrames && energy >= 0.08f;
                    bool firstActivation = energy >= 0.12f && !wasActive;
                    bool suddenJump = wasActive && energy > prevEnergy[ci] + 0.08f;
                    if (firstActivation || suddenJump || ageRetrig)
                    {
                        float baseHz;
                        if (frame.GeometryPitches is { Length: > 0 } gp)
                        {
                            int pi  = ci % gp.Length;
                            int oct = ci / gp.Length;
                            baseHz = gp[pi] * MathF.Pow(2f, oct);
                            float rawPitch = baseHz * MathF.Pow(2f, Math.Clamp(cell.TrapMean.X * 0.15f, -0.2f, 0.4f));
                            resFreq[ci] = rawPitch;
                        }
                        else
                        {
                            baseHz = 110f * MathF.Pow(2f, ci / (float)(NCells - 1) * 2f);
                            float rawPitch = baseHz * MathF.Pow(2f, Math.Clamp(cell.TrapMean.X * 0.25f, -0.25f, 0.5f));
                            resFreq[ci] = quantizer.Quantize(rawPitch);
                        }
                        resGain[ci]  = Math.Clamp(energy * 1.5f, 0.12f, 1f);
                        float decaySecs = voice switch {
                            FractalVoice.BurningShip => 0.08f + Math.Clamp(cell.HitRatio, 0f, 1f) * 0.37f,
                            FractalVoice.Kleinian    => 1.0f  + Math.Clamp(cell.HitRatio, 0f, 1f) * 2.0f,
                            FractalVoice.Mandelbulb  => 1.5f  + Math.Clamp(cell.HitRatio, 0f, 1f) * 3.0f,
                            _                        => 2.5f  + Math.Clamp(cell.HitRatio, 0f, 1f) * 4.5f,
                        };
                        resDecay[ci] = MathF.Exp(-1f / (decaySecs * sampleRate));
                        // Constant-power pan from cell column index (0..3 → L..R)
                        float pan = ((ci % 4) + 0.5f) / 4f * 2f - 1f; // -0.75 to +0.75
                        resPanL[ci] = MathF.Cos((pan + 1f) * MathF.PI / 4f);
                        resPanR[ci] = MathF.Sin((pan + 1f) * MathF.PI / 4f);
                        resAgeFrames[ci] = 0;
                    }
                    else
                    {
                        resAgeFrames[ci]++;
                    }
                    prevEnergy[ci] = energy;
                }
            }

            int end = Math.Min(idx + spf * 2, output.Length);
            while (idx < end)
            {
                gS += gcf * (gT - gS);

                // Morph all 4 corner wavetables toward targets
                for (int q = 0; q < 4; q++)
                    for (int wi = 0; wi < WtLen; wi++)
                        currentWt[q][wi] += morphCf * (targetWt[q][wi] - currentWt[q][wi]);

                // Drone: shared portamento/phase, 4 independent corner oscillators with spectral-tilt shelf.
                // TL(0)+BL(2) → left channel; TR(1)+BR(3) → right channel.
                // Top corners (0,1): boost highs (+tilt); Bottom corners (2,3): cut highs (-tilt).
                dronePitchS += pitchSlewCf * (dronePitch - dronePitchS);
                float droneInc = dronePitchS * WtLen / sampleRate;
                dronePhase = (dronePhase + droneInc) % WtLen;
                int   ti0   = (int)dronePhase;
                float tfrac = dronePhase - ti0;
                int   ti1   = (ti0 + 1) % WtLen;
                float droneOutL = 0f, droneOutR = 0f;
                for (int q = 0; q < 4; q++)
                {
                    float raw = (currentWt[q][ti0] + tfrac * (currentWt[q][ti1] - currentWt[q][ti0])) * gS * 0.18f;
                    droneShelfLpf[q] += tiltShelfCf * (raw - droneShelfLpf[q]);
                    float tilted = raw + droneTiltAmt[q] * (raw - droneShelfLpf[q]);
                    if (q == 0 || q == 2) droneOutL += tilted * 0.5f; // TL + BL
                    else                  droneOutR += tilted * 0.5f; // TR + BR
                }
                // Slewed per-channel balance — drone amplitude tracks the fractal's side
                dBalL += balSlewCf * (dBalLT - dBalL);
                dBalR += balSlewCf * (dBalRT - dBalR);
                droneOutL *= dBalL;
                droneOutR *= dBalR;

                // Bells: stereo-panned per cell column; mono reverb bus
                float bellMixL = 0f, bellMixR = 0f;
                float reverbIn = 0f;
                float reverbOut = 0f;
                if (!droneOnly)
                {
                    for (int ri = 0; ri < NCells; ri++)
                    {
                        if (resGain[ri] < 1e-5f) continue;
                        float bSig = resGain[ri] * MathF.Sin(2f * MathF.PI * resOscPh[ri]) * 0.040f;
                        // Spectral-tilt elevation: top rows bright, bottom rows dark
                        bellShelfLpf[ri] += tiltShelfCf * (bSig - bellShelfLpf[ri]);
                        float bTilted = bSig + bellRowTilt[ri / 4] * (bSig - bellShelfLpf[ri]);
                        bellMixL    += bTilted * resPanL[ri];
                        bellMixR    += bTilted * resPanR[ri];
                        reverbIn    += bSig;   // mono reverb bus uses untilted (preserve room character)
                        resGain[ri]  *= resDecay[ri];
                        resOscPh[ri]  = (resOscPh[ri] + resFreq[ri] / sampleRate) % 1f;
                    }

                    // Schroeder reverb on mono bell bus → spread equally to both channels
                    float ap = AllPass(reverbIn, apB0, ref aw0, ap0, 0.65f);
                    ap = AllPass(ap, apB1, ref aw1, ap1, 0.65f);
                    ap = AllPass(ap, apB2, ref aw2, ap2, 0.65f);
                    ap = AllPass(ap, apB3, ref aw3, ap3, 0.65f);
                    float c0 = CombLpf(ap, cmB0, ref cw0, cm0, 0.72f, ref cmLpf0, 0.18f);
                    float c1 = CombLpf(ap, cmB1, ref cw1, cm1, 0.72f, ref cmLpf1, 0.18f);
                    reverbOut = (c0 + c1) * 0.45f;
                }

                // Shepard–Risset zoom layer (mono centre)
                srGlideSm += srGlideCf * (srGlideT - srGlideSm);
                srBase += srGlideSm / (12f * sampleRate);
                if (srBase >= 1f) srBase -= 1f;
                else if (srBase < 0f) srBase += 1f;
                float srBaseFreq = SR_LO * MathF.Pow(2f, srBase);
                float shepOut = 0f;
                for (int si = 0; si < NSR; si++)
                {
                    float sf = srBaseFreq * srMult[si];
                    srPhases[si] += sf / sampleRate;
                    if (srPhases[si] >= 1f) srPhases[si] -= 1f;
                    float logOct = srLog2 + srBase + si;
                    shepOut += MathF.Exp(-logOct * logOct * srInv2Sg) * MathF.Sin(2f * MathF.PI * srPhases[si]);
                }
                shepOut = shepOut / NSR * 0.065f; // panned by shepGL/shepGR in the final mix

                // M7h: DE waveshaper (mono centre)
                float wsOut = 0f;
                if (wsHasData)
                {
                    for (int wi = 0; wi < WtLen; wi++)
                        wsTableCurr[wi] += wsMorphCf * (wsTableTarget[wi] - wsTableCurr[wi]);
                    wsPhase = (wsPhase + dronePitchS / sampleRate) % 1f;
                    float sineIn = MathF.Sin(2f * MathF.PI * wsPhase);
                    float idxF   = (sineIn + 1f) * 0.5f * (WtLen - 1);
                    int   idxI   = Math.Clamp((int)idxF, 0, WtLen - 2);
                    float frac   = idxF - idxI;
                    wsOut = (wsTableCurr[idxI] + frac * (wsTableCurr[idxI + 1] - wsTableCurr[idxI])) * 0.035f;
                }

                // Stereo mix: L/R drone + panned bells (dry tap) + mono reverb bloom
                // + Shepard panned to the fractal + mono waveshaper centre
                float sigL = 0.85f * (float)Math.Tanh((droneOutL + reverbOut * 0.5f) * 1.1f)
                           + bellMixL * 0.60f + shepOut * shepGL + wsOut;
                float sigR = 0.85f * (float)Math.Tanh((droneOutR + reverbOut * 0.5f) * 1.1f)
                           + bellMixR * 0.60f + shepOut * shepGR + wsOut;
                output[idx++] = Clip16(sigL);
                output[idx++] = Clip16(sigR);
            }
        }
        return output;
    }

    private static float[]? PickWavetable(FractalSonicFrame frame, WavetableSource src)
    {
        if (frame.Cells == null) return null;
        float bestEnergy = -1f;
        float[]? best = null;
        foreach (var cell in frame.Cells)
        {
            float[]? wt = src == WavetableSource.RaySteps ? cell.RayWavetable : cell.OrbitWavetable;
            if (wt != null && wt.Length >= WtLen && cell.Energy > bestEnergy)
            {
                bestEnergy = cell.Energy;
                best = wt;
            }
        }
        return best;
    }

    // ----------------------------------------------------------------------- helpers

    private static float Coeff(float tcSecs, int sr) => 1f - MathF.Exp(-1f / (tcSecs * sr));

    private static short Clip16(float s) => (short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue);

    private static int ScaleDelay(int n44100, int sr) => Math.Max(4, (int)Math.Round((double)n44100 * sr / 44100));

    private static float AllPass(float x, float[] buf, ref int w, int D, float g)
    {
        int len = buf.Length;
        int rp = (w - D + len) % len;
        float y = -g * x + buf[rp];
        buf[w] = x + g * y;
        w = (w + 1) % len;
        return y;
    }

    private static float CombLpf(float x, float[] buf, ref int w, int D,
                                  float fb, ref float lpf, float damp)
    {
        int len = buf.Length;
        int rp = (w - D + len) % len;
        lpf += damp * (buf[rp] - lpf);
        float y = Math.Clamp(x + fb * lpf, -12f, 12f);
        buf[w] = y;
        w = (w + 1) % len;
        return y;
    }
}
