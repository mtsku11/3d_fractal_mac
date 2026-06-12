using System.Numerics;
using OpenTK.Audio.OpenAL;

namespace Parsec.Audio.Sonification;

/// <summary>
/// Streaming OpenAL source that synthesises a real-time fractal drone from live
/// <see cref="FractalSonicFrame"/> telemetry.  Runs a dedicated background thread
/// that keeps a 4-buffer queue fed; the UI/render thread publishes frames lock-free
/// via a <see cref="Func{FractalSonicFrame}"/> callback.
///
/// DSP mirrors <see cref="FractalDroneSynth"/> (same four distinct algorithms) but
/// is stateful — all oscillator phases, delay buffers, and smoother state persist
/// across buffer boundaries so there are no clicks or phase resets at buffer edges.
///
/// M5: adds 16 OpenAL 3D point sources positioned at spatial telemetry cells.
/// The OpenAL listener is updated each poll tick with the camera position and
/// orientation from <see cref="FractalSonicFrame.CameraForward"/>, so turning the
/// camera changes the spatial sound field without moving the fractal.
///
/// If the renderer stalls and no new frame arrives, the last control targets are
/// held (no glitch).  If the OpenAL queue underruns, the source is restarted
/// automatically.
/// </summary>
public sealed class FractalDroneStream : IDisposable
{
    public const int SampleRate = FractalDroneSynth.DefaultSampleRate;

    private const int NumBuffers      = 4;
    private const int SamplesPerBuffer = 1024;  // ~23 ms per buffer; 4 × 23 ≈ 92 ms latency
    private const int StereoSamplesPerBuffer = SamplesPerBuffer * 2;
    private const int NumSpatialSrcs  = 16;

    private const float SpatialGain = 0.30f;    // max gain per spatial emitter

    private readonly Func<FractalSonicFrame?> _getFrame;
    private FractalVoice _voice;

    // Pre-allocated PCM buffers — never reallocated; zero allocation on the audio thread
    private readonly short[][] _pcmBufs;

    // AL handles
    private int[] _alBufs     = [];
    private int   _alSrc;
    private int[] _spatialSrcs = [];
    private int   _spatialToneBuf;
    private bool  _spatialReady;

    private Thread? _worker;
    private volatile bool _running;
    private bool _disposed;

    // ---- Shared gain smoother ----
    private float _gS  = 0.05f;
    private float _gT  = 0.05f;
    private readonly float _gcf;   // one-pole gain coeff

    // ===========================================================================
    // M7d/M7e: Hybrid synth — wavetable drone + modal resonators (orbit-mags source)
    // Shared by all four voices; reset on SetVoice.
    // ===========================================================================
    private const int WtLen  = 64;
    private const int NCells = 16;
    private JiQuantizer _jiQuantizer;
    private float _voiceRootHz = 110f;     // root Hz for the active voice
    private float _wtPhase = 0f;
    private float _wtPitch = 110f;
    private readonly float[] _currentWt       = new float[WtLen];
    private readonly float[] _targetWt        = new float[WtLen];
    private readonly float[] _blendBuf        = new float[WtLen];
    private readonly float[] _resGain         = new float[NCells];
    private readonly float[] _resDecay        = new float[NCells];
    private readonly float[] _resOscPh        = new float[NCells];
    private readonly float[] _resFreq         = new float[NCells];
    private readonly float[] _prevCellEnergy  = new float[NCells];
    private float _wtMorphCf;
    private float _wtPitchS  = 110f;   // slewed drone pitch (portamento)
    private readonly float _wtPitchSlewCf;

    // Schroeder reverb — shared across all hybrid voices (voices are mutually exclusive).
    private const int KAp0 = 1051, KAp1 = 1567, KAp2 = 2203, KAp3 = 3251;
    private const int KCm0 = 4799, KCm1 = 5399;
    private readonly float[] _ap0buf = new float[KAp0 + 1];
    private readonly float[] _ap1buf = new float[KAp1 + 1];
    private readonly float[] _ap2buf = new float[KAp2 + 1];
    private readonly float[] _ap3buf = new float[KAp3 + 1];
    private readonly float[] _cm0buf = new float[KCm0 + 1];
    private readonly float[] _cm1buf = new float[KCm1 + 1];
    private int   _ap0w, _ap1w, _ap2w, _ap3w, _cm0w, _cm1w;
    private float _cm0lpf, _cm1lpf;

    // ===========================================================================
    // M7f: Shepard–Risset zoom layer — N octave-spaced partials under a Gaussian
    // spectral envelope, all gliding at a rate derived from ZoomVelocity.
    // Positive glide rate = ascending; diving into the fractal sets a negative rate
    // so pitch descends "forever" without arriving.
    // ===========================================================================
    private const int   NSR      = 7;
    private const float SR_LO    = 27.5f;   // A0
    private const float SR_CTR   = 220f;    // A3 (spectral envelope center)
    private const float SR_SIG   = 1.5f;    // Gaussian sigma in octaves
    private static readonly float SR_LOG2   = MathF.Log2(SR_LO / SR_CTR); // = -3
    private static readonly float SR_INV2SG = 1f / (2f * SR_SIG * SR_SIG);
    private static readonly float[] SR_MULT  = [1f, 2f, 4f, 8f, 16f, 32f, 64f];  // pow(2,i)

    private readonly float[] _srPhases = new float[NSR];
    private float _srBase     = 0f;   // current log2 offset within [0,1) — wraps each octave
    private float _srGlideSm  = 0f;   // smoothed glide rate (semitones/s)
    private float _srGlideT   = 0f;   // target glide rate
    private readonly float _srGlideCf;

    // M7h: Waveshaper voice — DE cross-section strip as a transfer curve.
    // A sine at drone pitch is processed through the table lookup; adds geometry-derived harmonics.
    private readonly float[] _wsTableCurr   = new float[WtLen];   // currently playing curve
    private readonly float[] _wsTableTarget = new float[WtLen];   // target from latest telemetry
    private float _wsPhase     = 0f;    // input sine phase [0, 1)
    private bool  _wsHasData   = false; // true once first curve arrives
    private readonly float _wsMorphCf;  // same coeff as _wtMorphCf (τ = 50 ms)

    // M7g: Apollonian voice — timer-based bell trigger state (no cell telemetry)
    private int   _apoTrigCounter = 0;
    private int   _apoNextPitch   = 0;
    // Default Apollonian JI pitches (used when GeometryPitches not yet in frame)
    private static readonly float[] _defaultApoScalePitches =
        [110f, 110f * 35f/32f, 110f * 19f/16f, 165f, 110f * 15f/8f];

    // ===========================================================================
    // M9d: Direct-orbit mode — per-cell state, preallocated in constructor.
    // FillDirectOrbit mirrors DirectOrbitSynth.Synthesize but is stateful across
    // buffer boundaries so there are no phase resets at buffer edges.
    // ===========================================================================
    private const int   OrbtLen        = 128;
    private const int   SamplesPerStep = 12;    // 44100/12 ≈ 3675 orbit steps/s (FSE formula)
    private const int   XfadeSamples  = 220;    // ≈5 ms at 44100 Hz
    private const int   MaxModalModes = 8;
    private const float CellScale     = 0.065f;   // denser 16-voice wall (kept in sync with DirectOrbitSynth)
    private const float MinProjectedRms = 0.30f;
    private const float MaxProjectionBoost = 6.0f;

    // DirectOrbit Freeverb-style mono reverb (20 ms pre-delay).
    // fb/damp/wet are enclosure-driven per buffer (see FillDirectOrbit) — open void
    // gives a short dry tail, deep inside the fractal a long bright bloom.
    private const int    DoRevPreD  = 882;
    private const int    DoRevCm0D  = 2111, DoRevCm1D = 2237, DoRevCm2D = 2381, DoRevCm3D = 2521;
    private const int    DoRevAp0D  = 601,  DoRevAp1D = 441,  DoRevAp2D = 341,  DoRevAp3D = 225;
    private const float  DoRevApG   = 0.50f;

    // Proximity/enclosure macros + fold-event layer (kept in sync with DirectOrbitSynth)
    private const float DoProxRefDist       = 2.5f;
    private const float DoMacroSlewTau      = 0.35f;
    private const int   DoMaxChimesPerFrame = 3;
    private const float DoChimeRefractorySec = 0.25f;
    private const float DoMorphAttackTau    = 0.08f;
    private const float DoMorphReleaseTau   = 1.2f;

    private SonificationMode _mode = SonificationMode.Hybrid;
    private float            _blendAmount = 0f;  // 0 = pure Hybrid, 1 = pure DirectOrbit
    private readonly short[] _blendScratch;       // second-path stereo output; lerped on audio thread
    private readonly short[] _hybridMonoScratch;  // hybrid voices are mono; duplicated into the stereo stream

    // Per-cell persistent state (preallocated, never reallocated on audio thread)
    private readonly Vector3[][] _doPrevSeg;      // previous frame's processed orbit
    private readonly Vector3[][] _doNewSeg;       // current frame's processed orbit (ping-pong)
    private readonly float[]     _doLpfCoeff;    // per-cell LPF coefficient (updated per frame)
    private readonly float[]     _doVoiceGain;   // projection-level gain floor per cell
    private readonly float[]     _doPrevPhase;    // playback phase at end of prev frame
    private readonly float[]     _doDcXL, _doDcXR, _doDcHL, _doDcHR;  // DC blocker
    private readonly float[]     _doLpfL, _doLpfR;                     // one-pole LPF
    private readonly float[]     _doTiltLpfL, _doTiltLpfR;             // spectral-tilt
    private readonly float[]     _doCellSps;                           // per-cell step rate (detune)
    private readonly float[]     _doCellSpsEff;                        // morph-shimmered step rate (per buffer)
    private readonly float[][]   _doModalA1, _doModalA2, _doModalGain; // per-cell/modal resonator coeffs
    private readonly float[][]   _doModalY1L, _doModalY2L, _doModalY1R, _doModalY2R;

    // Fold-event chimes (preallocated; only touched on audio thread)
    private readonly float[] _doChimeEnv, _doChimePh1, _doChimePh2, _doChimeFreq, _doFoldDelta;
    private readonly int[]   _doRefract;

    // Per-fractal DirectOrbit identity (register, lattice, space, chime character)
    private DirectOrbitProfile _doProfile;
    private float _doChimeDecay;

    // Proximity/enclosure macro + morph-bus state
    private float _doProx, _doEncl, _doMorph;
    private float _doMLpfL, _doMLpfR, _doMBassL, _doMBassR;   // master proximity LPF + bass shelf
    private float _doRevFb = 0.70f, _doRevDamp = 0.35f, _doRevWet = 0.10f;
    private float _doModalBlend = 1f;
    private static readonly float _doBassCf = 1f - MathF.Exp(-2f * MathF.PI * 180f / SampleRate);

    // DirectOrbit reverb delay buffers (pre-allocated; never reallocated on audio thread)
    private readonly float[] _doRevPreBuf = new float[DoRevPreD];
    private readonly float[] _doRevCm0buf = new float[DoRevCm0D + 1];
    private readonly float[] _doRevCm1buf = new float[DoRevCm1D + 1];
    private readonly float[] _doRevCm2buf = new float[DoRevCm2D + 1];
    private readonly float[] _doRevCm3buf = new float[DoRevCm3D + 1];
    private readonly float[] _doRevAp0buf = new float[DoRevAp0D + 1];
    private readonly float[] _doRevAp1buf = new float[DoRevAp1D + 1];
    private readonly float[] _doRevAp2buf = new float[DoRevAp2D + 1];
    private readonly float[] _doRevAp3buf = new float[DoRevAp3D + 1];
    private int   _doRevPreW;
    private int   _doRevCm0w, _doRevCm1w, _doRevCm2w, _doRevCm3w;
    private int   _doRevAp0w, _doRevAp1w, _doRevAp2w, _doRevAp3w;
    private float _doRevCm0lpf, _doRevCm1lpf, _doRevCm2lpf, _doRevCm3lpf;

    // Constants derived at startup (same as DirectOrbitSynth, identical values)
    private static readonly float   _doHpfR    = 1f - 2f * MathF.PI * 20f / SampleRate;
    private static readonly float   _doTiltCf  = 1f - MathF.Exp(-2f * MathF.PI * 2000f / SampleRate);
    private static readonly float[] _doRowTilt = [0.35f, 0.12f, -0.12f, -0.35f];
    private static readonly float[] _doPanL;
    private static readonly float[] _doPanR;

    static FractalDroneStream()
    {
        _doPanL = new float[NCells];
        _doPanR = new float[NCells];
        for (int ci = 0; ci < NCells; ci++)
        {
            // Column = coarse screen-horizontal position; small per-row dither (±0.09)
            // spreads the 4 cells sharing a column to 16 distinct positions.
            // Kept in sync with DirectOrbitSynth so live and export use the same 4×4 field.
            float pan = ((ci % 4) + 0.5f) / 4f * 2f - 1f;
            pan = Math.Clamp(pan + (ci / 4 - 1.5f) * 0.06f, -1f, 1f);
            _doPanL[ci] = MathF.Cos((pan + 1f) * MathF.PI / 4f);
            _doPanR[ci] = MathF.Sin((pan + 1f) * MathF.PI / 4f);
        }
    }

    // macOS 15 openal-soft redirect
    private static bool _overrideInstalled;

    // Listener orientation pre-alloc (only touched on audio thread)
    private readonly float[] _listenerOrient = new float[6];

    public FractalDroneStream(Func<FractalSonicFrame?> getFrame,
                              FractalVoice voice = FractalVoice.Mandelbox,
                              SonificationMode mode = SonificationMode.Hybrid)
    {
        _getFrame = getFrame;
        _voice    = voice;
        _mode     = mode;

        static float Coeff(float tc, int sr) => 1f - MathF.Exp(-1f / (tc * sr));
        _gcf = Coeff(0.06f, SampleRate);

        _pcmBufs = new short[NumBuffers][];
        for (int i = 0; i < NumBuffers; i++)
            _pcmBufs[i] = new short[StereoSamplesPerBuffer];
        _blendScratch = new short[StereoSamplesPerBuffer];
        _hybridMonoScratch = new short[SamplesPerBuffer];

        // M9d: preallocate per-cell DirectOrbit state (zero allocation on audio thread)
        _doPrevSeg   = new Vector3[NCells][];
        _doNewSeg    = new Vector3[NCells][];
        for (int ci = 0; ci < NCells; ci++)
        {
            _doPrevSeg[ci] = new Vector3[OrbtLen];
            _doNewSeg[ci]  = new Vector3[OrbtLen];
        }
        _doLpfCoeff   = new float[NCells];
        _doVoiceGain  = new float[NCells];
        _doPrevPhase  = new float[NCells];
        _doDcXL       = new float[NCells]; _doDcXR  = new float[NCells];
        _doDcHL       = new float[NCells]; _doDcHR  = new float[NCells];
        _doLpfL       = new float[NCells]; _doLpfR  = new float[NCells];
        _doTiltLpfL   = new float[NCells]; _doTiltLpfR = new float[NCells];
        _doCellSpsEff = new float[NCells];
        _doModalA1    = new float[NCells][]; _doModalA2   = new float[NCells][];
        _doModalGain  = new float[NCells][];
        _doModalY1L   = new float[NCells][]; _doModalY2L  = new float[NCells][];
        _doModalY1R   = new float[NCells][]; _doModalY2R  = new float[NCells][];
        for (int ci = 0; ci < NCells; ci++)
        {
            _doModalA1[ci]   = new float[MaxModalModes];
            _doModalA2[ci]   = new float[MaxModalModes];
            _doModalGain[ci] = new float[MaxModalModes];
            _doModalY1L[ci]  = new float[MaxModalModes];
            _doModalY2L[ci]  = new float[MaxModalModes];
            _doModalY1R[ci]  = new float[MaxModalModes];
            _doModalY2R[ci]  = new float[MaxModalModes];
        }
        _doChimeEnv   = new float[NCells]; _doChimePh1 = new float[NCells];
        _doChimePh2   = new float[NCells]; _doChimeFreq = new float[NCells];
        _doFoldDelta  = new float[NCells]; _doRefract  = new int[NCells];

        // Per-cell step rates are computed per buffer in FillDirectOrbit from the
        // voice profile + geometry lattice ratio (keep in sync with DirectOrbitSynth).
        _doCellSps = new float[NCells];
        _doProfile    = DirectOrbitProfile.ForVoice(voice);
        _doChimeDecay = MathF.Exp(-1f / (_doProfile.ChimeDecaySec * SampleRate));

        _wtMorphCf       = Coeff(0.05f, SampleRate);
        _wsMorphCf       = Coeff(0.05f, SampleRate);
        _wtPitchSlewCf   = Coeff(0.28f, SampleRate);
        _srGlideCf       = Coeff(0.5f,  SampleRate);
        _jiQuantizer = VoiceQuantizer(voice);
        _voiceRootHz = VoiceRootHz(voice);
        _wtPitch     = _voiceRootHz;
        // Fallback sawtooth so drone sounds before geometry data arrives
        for (int wi = 0; wi < WtLen; wi++)
            _currentWt[wi] = _targetWt[wi] = 2f * wi / (WtLen - 1f) - 1f;
        for (int ci = 0; ci < NCells; ci++)
            _resDecay[ci] = MathF.Exp(-1f / (0.5f * SampleRate));
    }

    /// <summary>
    /// Geometry-driven TemperamentStrength ceiling: 0 = always microtonal,
    /// 1 = full range up to strict JI consonance.
    /// </summary>
    public float TemperamentCeiling { get; set; } = 0.9f;

    private static JiQuantizer VoiceQuantizer(FractalVoice v) => v switch {
        FractalVoice.Mandelbulb  => new JiQuantizer(JiScale.HarmonicSeries, 110f),
        FractalVoice.Kleinian    => new JiQuantizer(JiScale.Dorian,         55f),
        FractalVoice.BurningShip => new JiQuantizer(JiScale.Dorian,         130.81f),
        FractalVoice.Apollonian  => new JiQuantizer(JiScale.Dorian,         110f),  // unused but required
        _                        => new JiQuantizer(JiScale.Dorian,         110f),
    };

    private static float VoiceRootHz(FractalVoice v) => v switch {
        FractalVoice.Kleinian    => 55f,
        FractalVoice.BurningShip => 130.81f,
        _                        => 110f,
    };

    /// <summary>
    /// Active sonification mode.  Safe to set from any thread; takes effect at the next
    /// buffer boundary.  Switching to DirectOrbit resets per-cell crossfade state so
    /// the transition is click-free.
    /// </summary>
    public SonificationMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            _blendAmount = value == SonificationMode.DirectOrbit ? 1f : 0f;
            if (value == SonificationMode.DirectOrbit)
                ResetDirectOrbitState();
        }
    }

    /// <summary>
    /// Blend between Hybrid (0) and DirectOrbit (1).  Safe to set from any thread;
    /// takes effect at the next buffer boundary.
    /// </summary>
    public float BlendAmount
    {
        get => _blendAmount;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            _blendAmount = clamped;
            _mode = clamped >= 0.5f ? SonificationMode.DirectOrbit : SonificationMode.Hybrid;
        }
    }

    /// <summary>
    /// Blend inside the DirectOrbit branch: 0 = raw orbit texture, 1 = profile modal body.
    /// Inert for fractal voices without a modal profile.
    /// </summary>
    public float DirectOrbitModalBlend
    {
        get => _doModalBlend;
        set => _doModalBlend = Math.Clamp(value, 0f, 1f);
    }

    private void ResetDirectOrbitState()
    {
        for (int ci = 0; ci < NCells; ci++)
        {
            Array.Clear(_doPrevSeg[ci]);
            Array.Clear(_doNewSeg[ci]);
        }
        Array.Clear(_doPrevPhase);
        Array.Clear(_doLpfCoeff);
        Array.Clear(_doVoiceGain);
        Array.Clear(_doDcXL); Array.Clear(_doDcXR);
        Array.Clear(_doDcHL); Array.Clear(_doDcHR);
        Array.Clear(_doLpfL); Array.Clear(_doLpfR);
        Array.Clear(_doTiltLpfL); Array.Clear(_doTiltLpfR);
        ClearDirectOrbitModalState();
        Array.Clear(_doRevPreBuf); _doRevPreW = 0;
        Array.Clear(_doRevCm0buf); Array.Clear(_doRevCm1buf);
        Array.Clear(_doRevCm2buf); Array.Clear(_doRevCm3buf);
        _doRevCm0w = _doRevCm1w = _doRevCm2w = _doRevCm3w = 0;
        _doRevCm0lpf = _doRevCm1lpf = _doRevCm2lpf = _doRevCm3lpf = 0f;
        Array.Clear(_doRevAp0buf); Array.Clear(_doRevAp1buf);
        Array.Clear(_doRevAp2buf); Array.Clear(_doRevAp3buf);
        _doRevAp0w = _doRevAp1w = _doRevAp2w = _doRevAp3w = 0;
        ResetDirectOrbitMacroState();
        // Keep Shepard state — glide layer is mode-independent
    }

    private void ResetDirectOrbitMacroState()
    {
        Array.Clear(_doChimeEnv); Array.Clear(_doChimePh1); Array.Clear(_doChimePh2);
        Array.Clear(_doFoldDelta); Array.Clear(_doRefract);
        _doProx = _doEncl = _doMorph = 0f;
        _doMLpfL = _doMLpfR = _doMBassL = _doMBassR = 0f;
        _doRevFb = 0.70f; _doRevDamp = 0.35f; _doRevWet = 0.10f;
    }

    private void ClearDirectOrbitModalState()
    {
        for (int ci = 0; ci < NCells; ci++)
        {
            Array.Clear(_doModalA1[ci]); Array.Clear(_doModalA2[ci]); Array.Clear(_doModalGain[ci]);
            Array.Clear(_doModalY1L[ci]); Array.Clear(_doModalY2L[ci]);
            Array.Clear(_doModalY1R[ci]); Array.Clear(_doModalY2R[ci]);
        }
    }

    /// <summary>
    /// Start the streaming worker.  Returns <c>true</c> if the worker started.
    /// Safe to call from any thread.
    /// </summary>
    public bool Start()
    {
        if (_disposed || _running) return _running;
        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name         = "FractalDroneStream",
            Priority     = ThreadPriority.AboveNormal,
        };
        _worker.Start();
        return true;
    }

    /// <summary>Stop streaming.  Blocks up to one second for the worker to finish.</summary>
    public void Stop()
    {
        _running = false;
        _worker?.Join(1000);
        _worker = null;
    }

    /// <summary>
    /// Switch to a different fractal voice.  Safe to call from any thread; delay buffers
    /// are cleared here so the new voice starts from silence rather than previous state.
    /// </summary>
    public void SetVoice(FractalVoice voice)
    {
        _voice       = voice;
        _jiQuantizer = VoiceQuantizer(voice);
        _voiceRootHz = VoiceRootHz(voice);
        _doProfile    = DirectOrbitProfile.ForVoice(voice);
        _doChimeDecay = MathF.Exp(-1f / (_doProfile.ChimeDecaySec * SampleRate));

        // Clear reverb delay state so the new voice starts from silence
        Array.Clear(_ap0buf); Array.Clear(_ap1buf);
        Array.Clear(_ap2buf); Array.Clear(_ap3buf);
        Array.Clear(_cm0buf); Array.Clear(_cm1buf);
        _ap0w = _ap1w = _ap2w = _ap3w = _cm0w = _cm1w = 0;
        _cm0lpf = _cm1lpf = 0f;

        // Reset hybrid wavetable / resonator state
        for (int wi = 0; wi < WtLen; wi++)
            _currentWt[wi] = _targetWt[wi] = 2f * wi / (WtLen - 1f) - 1f;
        _wtPhase  = 0f;
        _wtPitch  = _voiceRootHz;
        _wtPitchS = _voiceRootHz;
        Array.Clear(_resGain);
        Array.Clear(_resOscPh);
        Array.Clear(_prevCellEnergy);
        for (int ci = 0; ci < NCells; ci++)
            _resDecay[ci] = MathF.Exp(-1f / (0.5f * SampleRate));

        _gS = 0.05f;  // brief fade-in after switch

        // Shepard state: reset base and phases; keep glide smoothed toward 0
        _srBase    = 0f;
        _srGlideSm = 0f;
        _srGlideT  = 0f;
        Array.Clear(_srPhases);

        // M7h waveshaper state
        Array.Clear(_wsTableCurr);
        Array.Clear(_wsTableTarget);
        _wsPhase   = 0f;
        _wsHasData = false;

        // M7g Apollonian trigger state
        _apoTrigCounter = 0;
        _apoNextPitch   = 0;

        // DirectOrbit reverb state
        Array.Clear(_doRevPreBuf); _doRevPreW = 0;
        Array.Clear(_doRevCm0buf); Array.Clear(_doRevCm1buf);
        Array.Clear(_doRevCm2buf); Array.Clear(_doRevCm3buf);
        _doRevCm0w = _doRevCm1w = _doRevCm2w = _doRevCm3w = 0;
        _doRevCm0lpf = _doRevCm1lpf = _doRevCm2lpf = _doRevCm3lpf = 0f;
        Array.Clear(_doRevAp0buf); Array.Clear(_doRevAp1buf);
        Array.Clear(_doRevAp2buf); Array.Clear(_doRevAp3buf);
        _doRevAp0w = _doRevAp1w = _doRevAp2w = _doRevAp3w = 0;
        ClearDirectOrbitModalState();
        ResetDirectOrbitMacroState();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ------------------------------------------------------------------ worker

    private void WorkerLoop()
    {
        EnsureMacOsOverride();

        var device = ALC.OpenDevice(null);
        if (device == ALDevice.Null) return;

        var ctx = ALC.CreateContext(device, new ALContextAttributes
        {
            Frequency    = SampleRate,
            MonoSources  = NumSpatialSrcs + 2,
            StereoSources = 2,
        });
        if (ctx == ALContext.Null)
        {
            ALC.CloseDevice(device);
            return;
        }

        if (!ALC.MakeContextCurrent(ctx))
        {
            ALC.DestroyContext(ctx);
            ALC.CloseDevice(device);
            return;
        }

        // ---- Streaming ambient drone source ----
        _alSrc  = AL.GenSource();
        _alBufs = AL.GenBuffers(NumBuffers);

        for (int i = 0; i < NumBuffers; i++)
        {
            FillBuffer(_pcmBufs[i]);
            AL.BufferData(_alBufs[i], ALFormat.Stereo16, _pcmBufs[i], SampleRate);
            AL.SourceQueueBuffer(_alSrc, _alBufs[i]);
        }

        AL.Source(_alSrc, ALSourceb.SourceRelative, true);
        AL.Source(_alSrc, ALSourcef.Gain, 1.0f);
        AL.DistanceModel(ALDistanceModel.InverseDistanceClamped);
        AL.SourcePlay(_alSrc);

        SetupSpatialSources();

        try
        {
            while (_running)
            {
                AL.GetSource(_alSrc, ALGetSourcei.BuffersProcessed, out int processed);
                while (processed-- > 0)
                {
                    int alBuf = AL.SourceUnqueueBuffer(_alSrc);
                    int slot  = SlotOf(alBuf);
                    try { FillBuffer(_pcmBufs[slot]); }
                    catch { Array.Clear(_pcmBufs[slot]); }   // output silence; keep thread alive
                    AL.BufferData(alBuf, ALFormat.Stereo16, _pcmBufs[slot], SampleRate);
                    AL.SourceQueueBuffer(_alSrc, alBuf);
                }

                AL.GetSource(_alSrc, ALGetSourcei.SourceState, out int st);
                if ((ALSourceState)st == ALSourceState.Stopped && _running)
                    AL.SourcePlay(_alSrc);

                UpdateSpatialSources();

                Thread.Sleep(5);
            }
        }
        finally
        {
            AL.SourceStop(_alSrc);

            AL.GetSource(_alSrc, ALGetSourcei.BuffersQueued, out int queued);
            for (int i = 0; i < queued; i++)
                AL.SourceUnqueueBuffer(_alSrc);

            AL.DeleteSource(_alSrc);
            AL.DeleteBuffers(_alBufs);

            if (_spatialReady)
            {
                foreach (var src in _spatialSrcs)
                    AL.SourceStop(src);
                AL.DeleteSources(_spatialSrcs);
                AL.DeleteBuffer(_spatialToneBuf);
            }

            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(ctx);
            ALC.CloseDevice(device);
        }
    }

    private void SetupSpatialSources()
    {
        var tonePcm = GenerateToneBuffer();
        _spatialToneBuf = AL.GenBuffer();
        AL.BufferData(_spatialToneBuf, ALFormat.Mono16, tonePcm, SampleRate);

        _spatialSrcs = AL.GenSources(NumSpatialSrcs);
        foreach (var src in _spatialSrcs)
        {
            AL.Source(src, ALSourcei.Buffer,         _spatialToneBuf);
            AL.Source(src, ALSourceb.Looping,         true);
            AL.Source(src, ALSourceb.SourceRelative, false);
            AL.Source(src, ALSourcef.ReferenceDistance, 2.0f);
            AL.Source(src, ALSourcef.MaxDistance,       50.0f);
            AL.Source(src, ALSourcef.RolloffFactor,     1.0f);
            AL.Source(src, ALSourcef.Gain,              0.0f);
            AL.SourcePlay(src);
        }
        _spatialReady = true;
    }

    private void UpdateSpatialSources()
    {
        if (!_spatialReady) return;

        var frame = _getFrame();
        if (frame == null || frame.CameraForward == Vector3.Zero) return;

        var cp  = frame.CameraPosition;
        var cf  = frame.CameraForward;
        var cup = frame.CameraUp;
        AL.Listener(ALListener3f.Position, cp.X, cp.Y, cp.Z);
        _listenerOrient[0] = cf.X;  _listenerOrient[1] = cf.Y;  _listenerOrient[2] = cf.Z;
        _listenerOrient[3] = cup.X; _listenerOrient[4] = cup.Y; _listenerOrient[5] = cup.Z;
        AL.Listener(ALListenerfv.Orientation, ref _listenerOrient[0]);

        var cells = frame.Cells;
        for (int i = 0; i < NumSpatialSrcs; i++)
        {
            if (cells != null && i < cells.Length)
            {
                var cell = cells[i];
                var wp   = cell.WorldPosition;
                AL.Source(_spatialSrcs[i], ALSource3f.Position, wp.X, wp.Y, wp.Z);
                AL.Source(_spatialSrcs[i], ALSourcef.Gain,  cell.Energy * SpatialGain);
                float pitch = Math.Clamp(0.5f + cell.TrapMean.X * 0.3f, 0.5f, 2.0f);
                AL.Source(_spatialSrcs[i], ALSourcef.Pitch, pitch);
            }
            else
            {
                AL.Source(_spatialSrcs[i], ALSourcef.Gain, 0.0f);
            }
        }
    }

    private int SlotOf(int alBuf)
    {
        for (int i = 0; i < _alBufs.Length; i++)
            if (_alBufs[i] == alBuf) return i;
        return 0;
    }

    // ------------------------------------------------------------------ DSP dispatch

    private void FillBuffer(short[] buf)
    {
        float blend = _blendAmount;

        if (blend >= 0.99f)
        {
            FillDirectOrbit(buf);
            return;
        }

        if (blend <= 0.01f)
        {
            FillHybridVoiceStereo(buf);
            return;
        }

        // Mid-range blend: run both paths and lerp sample-by-sample.
        // _blendScratch is preallocated — no GC on audio thread.
        FillHybridVoiceStereo(_blendScratch);
        FillDirectOrbit(buf);
        float hybGain = 1f - blend;
        for (int i = 0; i < buf.Length; i++)
            buf[i] = (short)(hybGain * _blendScratch[i] + blend * buf[i]);
    }

    private void FillHybridVoiceStereo(short[] stereoBuf)
    {
        FillHybridVoice(_hybridMonoScratch);
        for (int si = 0, di = 0; si < _hybridMonoScratch.Length && di + 1 < stereoBuf.Length; si++)
        {
            short s = _hybridMonoScratch[si];
            stereoBuf[di++] = s;
            stereoBuf[di++] = s;
        }
    }

    private void FillHybridVoice(short[] buf)
    {
        switch (_voice)
        {
            case FractalVoice.Mandelbulb:  FillMandelbulbHybrid (buf); break;
            case FractalVoice.Kleinian:    FillKleinianHybrid   (buf); break;
            case FractalVoice.BurningShip: FillBurningShipHybrid(buf); break;
            case FractalVoice.Apollonian:  FillApollonianHybrid (buf); break;
            default:                       FillMandelboxHybrid  (buf); break;
        }
    }

    // ----------------------------------------------------------------------- M9d direct-orbit (stateful)

    private void FillDirectOrbit(short[] buf)
    {
        var frame = _getFrame();
        if (frame == null)
        {
            Array.Clear(buf);
            return;
        }

        // Camera basis
        Vector3 camFwd = frame.CameraForward.LengthSquared() > 0.01f
            ? Vector3.Normalize(frame.CameraForward) : -Vector3.UnitZ;
        Vector3 upHint = frame.CameraUp.LengthSquared() > 0.01f
            ? frame.CameraUp : Vector3.UnitY;
        Vector3 camRight = Vector3.Normalize(Vector3.Cross(camFwd, upHint));
        Vector3 camUp    = Vector3.Cross(camRight, camFwd);

        // Preprocess into preallocated _doNewSeg — zero allocation on audio thread
        for (int ci = 0; ci < NCells; ci++)
        {
            FractalSonicCell? cell = (frame.Cells != null && ci < frame.Cells.Length)
                ? frame.Cells[ci] : null;
            DoPreprocessOrbitInPlace(cell?.OrbitTrajectory, _doNewSeg[ci]);
            float depth = DoMeanDepth(_doNewSeg[ci], camFwd);
            float dn    = Math.Clamp((depth + 1f) / 2f, 0f, 1f);
            float lpfHz = 2000f + dn * 6000f;
            _doLpfCoeff[ci] = 1f - MathF.Exp(-2f * MathF.PI * lpfHz / SampleRate);

            float prms = DoProjectedRms(_doNewSeg[ci], camRight, camUp);
            _doVoiceGain[ci] = prms < 1e-6f ? 0f : Math.Clamp(MinProjectedRms / prms, 1f, MaxProjectionBoost);
        }

        _srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

        int n = buf.Length / 2;   // stereo interleaved output matches ALFormat.Stereo16
        float dtFrame = (float)n / SampleRate;

        // ── Proximity/enclosure macros (slewed; kept in sync with DirectOrbitSynth) ──
        // prox: 1 at the surface, →0 far away; gated by HitRatio so an empty view
        // (MeanDepth reported as 0 when no rays hit) never reads as "close".
        float macroCf = 1f - MathF.Exp(-dtFrame / DoMacroSlewTau);
        float proxT = MathF.Exp(-MathF.Max(0f, frame.MeanDepth) / DoProxRefDist)
                    * Math.Min(1f, frame.HitRatio * 5f);
        float enclT = Math.Clamp(frame.HitRatio, 0f, 1f);
        _doProx += macroCf * (proxT - _doProx);
        _doEncl += macroCf * (enclT - _doEncl);
        float mCutHz  = 1200f + 8800f * MathF.Pow(_doProx, 0.7f);
        float mCf     = 1f - MathF.Exp(-2f * MathF.PI * mCutHz / SampleRate);
        float dryGain = 0.45f + 0.55f * _doProx;
        float bassAmt = 0.9f * _doProx;
        _doRevFb   = _doProfile.RevFb0   + (_doProfile.RevFb1   - _doProfile.RevFb0)   * _doEncl;
        _doRevDamp = _doProfile.RevDamp0 + (_doProfile.RevDamp1 - _doProfile.RevDamp0) * _doEncl;
        _doRevWet  = _doProfile.RevWet0  + (_doProfile.RevWet1  - _doProfile.RevWet0)  * _doEncl;

        // ── Morph bus: ParameterVelocity → shimmer + fold sensitivity ──
        float morphT = Math.Clamp(frame.ParameterVelocity * 4f, 0f, 1f);
        float morphCf = morphT > _doMorph
            ? 1f - MathF.Exp(-dtFrame / DoMorphAttackTau)
            : 1f - MathF.Exp(-dtFrame / DoMorphReleaseTau);
        _doMorph += morphCf * (morphT - _doMorph);

        // ── Lattice: geometry-derived generator when present, else profile default.
        // Recomputed per buffer so parameter morphs retune the whole grid live.
        // Phase restarts each buffer with a 5 ms crossfade, so per-buffer step-rate
        // changes (retune + the ±~35-cent morph shimmer below) are click-free.
        // Keep in sync with DirectOrbitSynth.
        float latRatio = frame.LatticeRatio > 1.001f ? frame.LatticeRatio : _doProfile.LatticeRatio;
        int modalModes = Math.Min(_doProfile.ModeRatios?.Length ?? 0, MaxModalModes);
        bool modal = _doProfile.HasModalBody && modalModes > 0;
        float modalBlend = _doModalBlend;
        for (int ci = 0; ci < NCells; ci++)
        {
            float pitchMul = _doProfile.RootDivisor
                           * MathF.Pow(latRatio, ci % 4)        // column root
                           * MathF.Pow(latRatio, 3 - ci / 4);   // row above bottom
            _doCellSps[ci]   = SamplesPerStep / pitchMul;
            _doChimeFreq[ci] = SampleRate * 8f / (_doCellSps[ci] * OrbtLen);
            _doCellSpsEff[ci] = _doCellSps[ci] /
                (1f + _doMorph * 0.02f * MathF.Sin(2f * MathF.PI * (0.6f + 0.37f * ci) * (float)frame.Time));

            if (modal)
            {
                float loopHz = SampleRate / (_doCellSps[ci] * OrbtLen);
                int nModeData = Math.Min(modalModes, Math.Min(_doProfile.ModeGains!.Length, _doProfile.ModeDecaysSec!.Length));
                for (int m = 0; m < nModeData; m++)
                {
                    float fm = Math.Clamp(loopHz * _doProfile.ModeFreqMul * _doProfile.ModeRatios![m],
                                          20f, 0.45f * SampleRate);
                    float w  = 2f * MathF.PI * fm / SampleRate;
                    float R  = MathF.Exp(-6.9078f / (_doProfile.ModeDecaysSec[m] * SampleRate));
                    _doModalA1[ci][m]   = 2f * R * MathF.Cos(w);
                    _doModalA2[ci][m]   = R * R;
                    _doModalGain[ci][m] = _doProfile.ModeGains[m] * MathF.Sin(w)
                                        * MathF.Sqrt(1f - R * R) * _doProfile.ModalDrive;
                }
                modalModes = nModeData;
            }
        }

        // ── Fold-event detection: buffer-to-buffer orbit delta per cell ──
        for (int ci = 0; ci < NCells; ci++)
        {
            if (_doRefract[ci] > 0) _doRefract[ci]--;
            float dSum = 0f, pSum = 0f;
            for (int i = 0; i < OrbtLen; i++)
            {
                dSum += (_doNewSeg[ci][i] - _doPrevSeg[ci][i]).Length();
                pSum += _doPrevSeg[ci][i].LengthSquared();
            }
            // A cell appearing from silence (startup, or entering view) is not a fold
            _doFoldDelta[ci] = pSum < 1e-6f ? 0f : dSum / OrbtLen;
        }
        int refractFrames = (int)MathF.Ceiling(DoChimeRefractorySec / dtFrame);
        float foldThr = 0.45f - 0.25f * _doMorph;
        for (int k = 0; k < DoMaxChimesPerFrame; k++)
        {
            int best = -1; float bestDelta = foldThr;
            for (int ci = 0; ci < NCells; ci++)
                if (_doRefract[ci] == 0 && _doFoldDelta[ci] > bestDelta)
                { best = ci; bestDelta = _doFoldDelta[ci]; }
            if (best < 0) break;
            _doChimeEnv[best] = Math.Min(0.9f, (bestDelta - foldThr) * 1.8f) * 0.6f;
            _doChimePh1[best] = 0f; _doChimePh2[best] = 0f;
            _doRefract[best]  = refractFrames;
            _doFoldDelta[best] = 0f; // exclude from remaining picks this buffer
        }

        for (int si = 0; si < n; si++)
        {
            float sumL = 0f, sumR = 0f;

            for (int ci = 0; ci < NCells; ci++)
            {
                float rawL, rawR;
                if (si < XfadeSamples)
                {
                    float t        = (float)si / XfadeSamples;
                    float prevGain = MathF.Cos(t * MathF.PI / 2f);
                    float newGain  = MathF.Sin(t * MathF.PI / 2f);
                    float prevP    = MathF.Min(_doPrevPhase[ci] + si / _doCellSpsEff[ci],
                                               OrbtLen - 1.001f);
                    var (pL, pR, _) = DoProject(_doPrevSeg[ci], prevP, camRight, camUp, camFwd);
                    float newP = si / _doCellSpsEff[ci];
                    var (nL, nR, _) = DoProject(_doNewSeg[ci], newP, camRight, camUp, camFwd);
                    rawL = prevGain * pL + newGain * nL;
                    rawR = prevGain * pR + newGain * nR;
                }
                else
                {
                    float phase = si / _doCellSpsEff[ci];
                    var (nL, nR, _) = DoProject(_doNewSeg[ci], phase, camRight, camUp, camFwd);
                    rawL = nL; rawR = nR;
                }

                float hl = rawL - _doDcXL[ci] + _doHpfR * _doDcHL[ci];
                float hr = rawR - _doDcXR[ci] + _doHpfR * _doDcHR[ci];
                _doDcXL[ci] = rawL; _doDcXR[ci] = rawR;
                _doDcHL[ci] = hl;   _doDcHR[ci] = hr;

                float cf = _doLpfCoeff[ci];
                _doLpfL[ci] += cf * (hl - _doLpfL[ci]);
                _doLpfR[ci] += cf * (hr - _doLpfR[ci]);
                float vL = _doLpfL[ci], vR = _doLpfR[ci];

                if (modal)
                {
                    float rawOrbitL = vL, rawOrbitR = vR;
                    float bnkL = 0f, bnkR = 0f;
                    var a1 = _doModalA1[ci]; var a2 = _doModalA2[ci]; var gn = _doModalGain[ci];
                    var y1l = _doModalY1L[ci]; var y2l = _doModalY2L[ci];
                    var y1r = _doModalY1R[ci]; var y2r = _doModalY2R[ci];
                    for (int m = 0; m < modalModes; m++)
                    {
                        float yl = a1[m] * y1l[m] - a2[m] * y2l[m] + gn[m] * vL;
                        y2l[m] = y1l[m]; y1l[m] = yl; bnkL += yl;
                        float yr = a1[m] * y1r[m] - a2[m] * y2r[m] + gn[m] * vR;
                        y2r[m] = y1r[m]; y1r[m] = yr; bnkR += yr;
                    }
                    float modalL = bnkL + _doProfile.ExciterBleed * rawOrbitL;
                    float modalR = bnkR + _doProfile.ExciterBleed * rawOrbitR;
                    vL = rawOrbitL + modalBlend * (modalL - rawOrbitL);
                    vR = rawOrbitR + modalBlend * (modalR - rawOrbitR);
                }

                float tilt = _doRowTilt[ci / 4];
                _doTiltLpfL[ci] += _doTiltCf * (vL - _doTiltLpfL[ci]);
                _doTiltLpfR[ci] += _doTiltCf * (vR - _doTiltLpfR[ci]);
                float tiltedL = vL + tilt * (vL - _doTiltLpfL[ci]);
                float tiltedR = vR + tilt * (vR - _doTiltLpfR[ci]);

                float cellGain = CellScale * _doVoiceGain[ci];
                sumL += tiltedL * _doPanL[ci] * cellGain;
                sumR += tiltedR * _doPanR[ci] * cellGain;
            }

            // Fold-event chimes: two-partial decaying sines at the cell's pan position
            for (int ci = 0; ci < NCells; ci++)
            {
                if (_doChimeEnv[ci] < 1e-4f) continue;
                _doChimePh1[ci] += _doChimeFreq[ci] / SampleRate;
                _doChimePh2[ci] += _doChimeFreq[ci] * _doProfile.ChimePartial / SampleRate;
                if (_doChimePh1[ci] >= 1f) _doChimePh1[ci] -= 1f;
                if (_doChimePh2[ci] >= 1f) _doChimePh2[ci] -= 1f;
                float cs = _doChimeEnv[ci] * (MathF.Sin(2f * MathF.PI * _doChimePh1[ci])
                         + 0.35f * MathF.Sin(2f * MathF.PI * _doChimePh2[ci]));
                _doChimeEnv[ci] *= _doChimeDecay;
                sumL += cs * _doPanL[ci] * 0.5f;
                sumR += cs * _doPanR[ci] * 0.5f;
            }

            // Proximity master chain: brightness LPF + bass lift + level (far = dark/thin/quiet)
            _doMLpfL += mCf * (sumL - _doMLpfL);
            _doMLpfR += mCf * (sumR - _doMLpfR);
            _doMBassL += _doBassCf * (_doMLpfL - _doMBassL);
            _doMBassR += _doBassCf * (_doMLpfR - _doMBassR);
            float dryL = (_doMLpfL + bassAmt * _doMBassL) * dryGain;
            float dryR = (_doMLpfR + bassAmt * _doMBassR) * dryGain;

            _srGlideSm += _srGlideCf * (_srGlideT - _srGlideSm);
            _srBase += _srGlideSm / (12f * SampleRate);
            if (_srBase >= 1f) _srBase -= 1f; else if (_srBase < 0f) _srBase += 1f;
            float srBf = SR_LO * MathF.Pow(2f, _srBase);
            float shep = 0f;
            for (int i = 0; i < NSR; i++)
            {
                _srPhases[i] = (_srPhases[i] + srBf * SR_MULT[i] / SampleRate) % 1f;
                float logOct = SR_LOG2 + _srBase + i;
                shep += MathF.Exp(-logOct * logOct * SR_INV2SG) * MathF.Sin(2f * MathF.PI * _srPhases[i]);
            }
            shep = shep / NSR * 0.065f;

            // Mono reverb bus from the stereo dry signal; dry DirectOrbit panning stays stereo.
            // fb/damp/wet are enclosure-driven (set per buffer above).
            float revIn  = (dryL + dryR) * 0.70710678f;
            float preOut = _doRevPreBuf[_doRevPreW];
            _doRevPreBuf[_doRevPreW] = revIn;
            _doRevPreW = (_doRevPreW + 1) % DoRevPreD;
            float cSum = StreamCombLpf(preOut, _doRevCm0buf, ref _doRevCm0w, DoRevCm0D, _doRevFb, ref _doRevCm0lpf, _doRevDamp)
                       + StreamCombLpf(preOut, _doRevCm1buf, ref _doRevCm1w, DoRevCm1D, _doRevFb, ref _doRevCm1lpf, _doRevDamp)
                       + StreamCombLpf(preOut, _doRevCm2buf, ref _doRevCm2w, DoRevCm2D, _doRevFb, ref _doRevCm2lpf, _doRevDamp)
                       + StreamCombLpf(preOut, _doRevCm3buf, ref _doRevCm3w, DoRevCm3D, _doRevFb, ref _doRevCm3lpf, _doRevDamp);
            float revOut = StreamAllPass(cSum * 0.25f, _doRevAp0buf, ref _doRevAp0w, DoRevAp0D, DoRevApG);
            revOut = StreamAllPass(revOut, _doRevAp1buf, ref _doRevAp1w, DoRevAp1D, DoRevApG);
            revOut = StreamAllPass(revOut, _doRevAp2buf, ref _doRevAp2w, DoRevAp2D, DoRevApG);
            revOut = StreamAllPass(revOut, _doRevAp3buf, ref _doRevAp3w, DoRevAp3D, DoRevApG);
            float bloom = revOut * _doRevWet;
            float sigL = 0.85f * (float)Math.Tanh((dryL + bloom) * 1.1f) + shep;
            float sigR = 0.85f * (float)Math.Tanh((dryR + bloom) * 1.1f) + shep;
            int di = si * 2;
            buf[di]     = Clip16(sigL);
            buf[di + 1] = Clip16(sigR);
        }

        // Ping-pong: swap new → prev for next buffer's crossfade
        for (int ci = 0; ci < NCells; ci++)
        {
            Array.Copy(_doNewSeg[ci], _doPrevSeg[ci], OrbtLen);
            _doPrevPhase[ci] = n / _doCellSpsEff[ci];
        }
    }

    // ── DirectOrbit helpers ──

    private static void DoPreprocessOrbitInPlace(Vector4[]? trajectory, Vector3[] result)
    {
        Array.Clear(result);
        if (trajectory == null) return;
        int n = Math.Min(trajectory.Length, OrbtLen);
        Vector3 centroid = Vector3.Zero;
        int cnt = 0;
        for (int i = 0; i < n; i++)
            if (trajectory[i].W > 0.5f)
            { centroid += new Vector3(trajectory[i].X, trajectory[i].Y, trajectory[i].Z); cnt++; }
        if (cnt > 0) centroid /= cnt;
        int bailout = n;
        for (int i = 0; i < n; i++)
        {
            if (trajectory[i].W < 0.5f && bailout == n) bailout = i;
            result[i] = new Vector3(trajectory[i].X, trajectory[i].Y, trajectory[i].Z) - centroid;
        }
        float peak = 0f;
        for (int i = 0; i < bailout; i++) peak = MathF.Max(peak, result[i].Length());
        // Floor + sqrt(bounded fraction): empty cells (bailout==0) silenced (stereo gate);
        // partially-bounded cells kept ≥0.30 so all 16 voices stay audible (wall of voices).
        // Kept in sync with DirectOrbitSynth.PreprocessOrbit.
        float boundedFraction = n > 0 ? (float)bailout / n : 0f;
        float shaped = bailout == 0 ? 0f : 0.30f + 0.70f * MathF.Sqrt(boundedFraction);
        float gain = peak < 1e-6f ? 0f : shaped / peak;
        for (int i = 0; i < n; i++)
            result[i] = i < bailout ? result[i] * gain : Vector3.Zero;
    }

    private static float DoMeanDepth(Vector3[] seg, Vector3 camFwd)
    {
        float sum = 0f;
        for (int i = 0; i < seg.Length; i++) sum += Vector3.Dot(seg[i], camFwd);
        return sum / seg.Length;
    }

    private static float DoProjectedRms(Vector3[] seg, Vector3 camRight, Vector3 camUp)
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

    private static (float L, float R, float Depth) DoProject(
        Vector3[] seg, float phase, Vector3 camRight, Vector3 camUp, Vector3 camFwd)
    {
        int len = seg.Length;
        int i0  = (int)phase % len;
        int i1  = (i0 + 1) % len;
        float f = phase - MathF.Floor(phase);
        float t = 0.5f - 0.5f * MathF.Cos(MathF.PI * f);
        var p  = seg[i0] + t * (seg[i1] - seg[i0]);
        float pr = Vector3.Dot(p, camRight);
        float pu = Vector3.Dot(p, camUp);
        const float Sq2Inv = 0.70710678f;
        return ((pr + pu) * Sq2Inv, (pr - pu) * Sq2Inv, Vector3.Dot(p, camFwd));
    }

    // ----------------------------------------------------------------------- Mandelbox hybrid (M7d)

    private void FillMandelboxHybrid(short[] buf)
    {
        var frame = _getFrame();
        if (frame != null)
        {
            _gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(frame.HitRatio, 0f, 1f));

            float chaos = Math.Clamp(frame.NormalVariance * 2.5f, 0f, 1f);
            _jiQuantizer.TemperamentStrength = Math.Clamp((1f - chaos) * TemperamentCeiling, 0f, 1f);

            if (frame.Cells != null)
            {
                // Blend orbit wavetables from all active cells, weighted by energy
                Array.Clear(_blendBuf);
                float totalW = 0f;
                int nCells = Math.Min(frame.Cells.Length, NCells);
                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    var wt = cell.OrbitWavetable;
                    if (wt != null && wt.Length >= WtLen && cell.Energy > 0.05f)
                    {
                        for (int wi = 0; wi < WtLen; wi++)
                            _blendBuf[wi] += wt[wi] * cell.Energy;
                        totalW += cell.Energy;
                    }
                }
                if (totalW > 0f)
                    for (int wi = 0; wi < WtLen; wi++)
                        _targetWt[wi] = _blendBuf[wi] / totalW;

                // Camera-driven cell voicing: energy rise → strike resonator
                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    float energy = cell.Energy;
                    // Trigger on first activation (inactive→active) or large sudden jump
                    bool wasActive = _prevCellEnergy[ci] >= 0.12f;
                    if (energy >= 0.12f && (!wasActive || energy > _prevCellEnergy[ci] + 0.08f))
                    {
                        float baseHz  = 110f * MathF.Pow(2f, ci / (float)(NCells - 1) * 2f);
                        float rawHz   = baseHz * MathF.Pow(2f, Math.Clamp(cell.TrapMean.X * 0.25f, -0.25f, 0.5f));
                        _resFreq[ci]  = _jiQuantizer.Quantize(rawHz);
                        _resGain[ci]  = Math.Clamp(energy * 1.5f, 0.1f, 1f);
                        float decayS  = 0.4f + Math.Clamp(cell.HitRatio, 0f, 1f) * 1.6f;
                        _resDecay[ci] = MathF.Exp(-1f / (decayS * SampleRate));
                    }
                    _prevCellEnergy[ci] = energy;
                }
            }

            float rawDrone = 110f * MathF.Pow(2f, Math.Clamp(frame.TrapMean.X * 0.3f, -0.2f, 0.8f));
            _wtPitch = _jiQuantizer.Quantize(rawDrone);
            _srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

            if (frame.WaveshaperCurve is { Length: >= WtLen } wsc)
            {
                Array.Copy(wsc, _wsTableTarget, WtLen);
                _wsHasData = true;
            }
        }

        for (int s = 0; s < buf.Length; s++)
        {
            _gS += _gcf * (_gT - _gS);

            // Morph current wavetable toward target (50 ms time constant)
            for (int wi = 0; wi < WtLen; wi++)
                _currentWt[wi] += _wtMorphCf * (_targetWt[wi] - _currentWt[wi]);

            // Wavetable drone: linear-interpolated readback
            _wtPitchS += _wtPitchSlewCf * (_wtPitch - _wtPitchS);
            float droneInc = _wtPitchS * WtLen / SampleRate;
            _wtPhase = (_wtPhase + droneInc) % WtLen;
            int   ti0   = (int)_wtPhase;
            float tfrac = _wtPhase - ti0;
            int   ti1   = (ti0 + 1) % WtLen;
            float droneOut = (_currentWt[ti0] + tfrac * (_currentWt[ti1] - _currentWt[ti0])) * _gS * 0.18f;

            // Bells: 16 decaying sinusoids (0.025 each → 0.40 max when all active)
            float bellMix = 0f;
            for (int ri = 0; ri < NCells; ri++)
            {
                if (_resGain[ri] < 1e-5f) continue;
                bellMix      += _resGain[ri] * MathF.Sin(2f * MathF.PI * _resOscPh[ri]) * 0.025f;
                _resGain[ri]  *= _resDecay[ri];
                _resOscPh[ri]  = (_resOscPh[ri] + _resFreq[ri] / SampleRate) % 1f;
            }

            // Schroeder reverb on bell bus (reuses Kleinian delay buffers — cleared on voice switch)
            float ap = StreamAllPass(bellMix, _ap0buf, ref _ap0w, KAp0, 0.65f);
            ap = StreamAllPass(ap, _ap1buf, ref _ap1w, KAp1, 0.65f);
            ap = StreamAllPass(ap, _ap2buf, ref _ap2w, KAp2, 0.65f);
            ap = StreamAllPass(ap, _ap3buf, ref _ap3w, KAp3, 0.65f);
            float c0 = StreamCombLpf(ap, _cm0buf, ref _cm0w, KCm0, 0.72f, ref _cm0lpf, 0.18f);
            float c1 = StreamCombLpf(ap, _cm1buf, ref _cm1w, KCm1, 0.72f, ref _cm1lpf, 0.18f);
            float reverbOut = (c0 + c1) * 0.28f;

            // Shepard–Risset zoom layer
            _srGlideSm += _srGlideCf * (_srGlideT - _srGlideSm);
            float shepOut = SampleShepard() * 0.13f;

            // DE waveshaper layer (M7h): sine passed through DE cross-section table
            float wsOut = SampleWaveshaper() * 0.07f;

            float sig = 0.85f * (float)Math.Tanh((droneOut + reverbOut) * 1.1f) + shepOut + wsOut;
            buf[s] = Clip16(sig);
        }
    }

    // ----------------------------------------------------------------------- Mandelbulb hybrid (M7e)
    // HarmonicSeries at A2 (110 Hz); bells spread A2–A5 (3 octaves); long decay 1.5–4.5 s.

    private void FillMandelbulbHybrid(short[] buf)
    {
        const float Root = 110f;

        var frame = _getFrame();
        if (frame != null)
        {
            _gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(frame.HitRatio, 0f, 1f));

            float chaos = Math.Clamp(frame.NormalVariance * 2.5f, 0f, 1f);
            _jiQuantizer.TemperamentStrength = Math.Clamp((1f - chaos) * TemperamentCeiling, 0f, 1f);

            if (frame.Cells != null)
            {
                Array.Clear(_blendBuf);
                float totalW = 0f;
                int nCells = Math.Min(frame.Cells.Length, NCells);
                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    var wt = cell.OrbitWavetable;
                    if (wt != null && wt.Length >= WtLen && cell.Energy > 0.05f)
                    {
                        for (int wi = 0; wi < WtLen; wi++) _blendBuf[wi] += wt[wi] * cell.Energy;
                        totalW += cell.Energy;
                    }
                }
                if (totalW > 0f)
                    for (int wi = 0; wi < WtLen; wi++) _targetWt[wi] = _blendBuf[wi] / totalW;

                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    float energy = cell.Energy;
                    bool wasActive = _prevCellEnergy[ci] >= 0.10f;
                    if (energy >= 0.10f && (!wasActive || energy > _prevCellEnergy[ci] + 0.08f))
                    {
                        float baseHz  = Root * MathF.Pow(2f, ci / (float)(NCells - 1) * 3f);  // A2–A5
                        float rawHz   = baseHz * MathF.Pow(2f, Math.Clamp(cell.TrapMean.X * 0.25f, -0.25f, 0.5f));
                        _resFreq[ci]  = _jiQuantizer.Quantize(rawHz);
                        _resGain[ci]  = Math.Clamp(energy * 1.5f, 0.1f, 1f);
                        float decayS  = 1.5f + Math.Clamp(cell.HitRatio, 0f, 1f) * 3.0f;  // 1.5–4.5 s
                        _resDecay[ci] = MathF.Exp(-1f / (decayS * SampleRate));
                    }
                    _prevCellEnergy[ci] = energy;
                }
            }

            float rawDrone = Root * MathF.Pow(2f, Math.Clamp(frame.TrapMean.X * 0.3f, -0.2f, 0.8f));
            _wtPitch = _jiQuantizer.Quantize(rawDrone);
            _srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

            if (frame.WaveshaperCurve is { Length: >= WtLen } wsc)
            {
                Array.Copy(wsc, _wsTableTarget, WtLen);
                _wsHasData = true;
            }
        }

        for (int s = 0; s < buf.Length; s++)
        {
            _gS += _gcf * (_gT - _gS);
            for (int wi = 0; wi < WtLen; wi++) _currentWt[wi] += _wtMorphCf * (_targetWt[wi] - _currentWt[wi]);

            _wtPitchS += _wtPitchSlewCf * (_wtPitch - _wtPitchS);
            float droneInc = _wtPitchS * WtLen / SampleRate;
            _wtPhase = (_wtPhase + droneInc) % WtLen;
            int   ti0   = (int)_wtPhase;
            float tfrac = _wtPhase - ti0;
            float droneOut = (_currentWt[ti0] + tfrac * (_currentWt[(ti0 + 1) % WtLen] - _currentWt[ti0])) * _gS * 0.15f;

            float bellMix = 0f;
            for (int ri = 0; ri < NCells; ri++)
            {
                if (_resGain[ri] < 1e-5f) continue;
                bellMix      += _resGain[ri] * MathF.Sin(2f * MathF.PI * _resOscPh[ri]) * 0.025f;
                _resGain[ri]  *= _resDecay[ri];
                _resOscPh[ri]  = (_resOscPh[ri] + _resFreq[ri] / SampleRate) % 1f;
            }

            float ap = StreamAllPass(bellMix, _ap0buf, ref _ap0w, KAp0, 0.65f);
            ap = StreamAllPass(ap, _ap1buf, ref _ap1w, KAp1, 0.65f);
            ap = StreamAllPass(ap, _ap2buf, ref _ap2w, KAp2, 0.65f);
            ap = StreamAllPass(ap, _ap3buf, ref _ap3w, KAp3, 0.65f);
            float c0 = StreamCombLpf(ap, _cm0buf, ref _cm0w, KCm0, 0.72f, ref _cm0lpf, 0.18f);
            float c1 = StreamCombLpf(ap, _cm1buf, ref _cm1w, KCm1, 0.72f, ref _cm1lpf, 0.18f);

            _srGlideSm += _srGlideCf * (_srGlideT - _srGlideSm);
            float shepOut = SampleShepard() * 0.13f;
            float wsOut   = SampleWaveshaper() * 0.07f;
            float sig = 0.85f * (float)Math.Tanh((droneOut + (c0 + c1) * 0.32f) * 1.1f) + shepOut + wsOut;
            buf[s] = Clip16(sig);
        }
    }

    // ----------------------------------------------------------------------- Kleinian hybrid (M7e)
    // Dorian at A1 (55 Hz); bells spread A1–A3 (2 octaves); long cavernous decay 3.0–9.0 s.

    private void FillKleinianHybrid(short[] buf)
    {
        const float Root = 55f;

        var frame = _getFrame();
        if (frame != null)
        {
            _gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(frame.HitRatio, 0f, 1f));

            float chaos = Math.Clamp(frame.NormalVariance * 2.5f, 0f, 1f);
            _jiQuantizer.TemperamentStrength = Math.Clamp((1f - chaos) * TemperamentCeiling, 0f, 1f);

            if (frame.Cells != null)
            {
                Array.Clear(_blendBuf);
                float totalW = 0f;
                int nCells = Math.Min(frame.Cells.Length, NCells);
                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    var wt = cell.OrbitWavetable;
                    if (wt != null && wt.Length >= WtLen && cell.Energy > 0.05f)
                    {
                        for (int wi = 0; wi < WtLen; wi++) _blendBuf[wi] += wt[wi] * cell.Energy;
                        totalW += cell.Energy;
                    }
                }
                if (totalW > 0f)
                    for (int wi = 0; wi < WtLen; wi++) _targetWt[wi] = _blendBuf[wi] / totalW;

                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    float energy = cell.Energy;
                    bool wasActive = _prevCellEnergy[ci] >= 0.10f;
                    if (energy >= 0.10f && (!wasActive || energy > _prevCellEnergy[ci] + 0.08f))
                    {
                        float baseHz;
                        if (frame.GeometryPitches is { Length: > 0 } gp)
                        {
                            // M7g: use geometry-derived Kleinian scale — no quantizer snap
                            int pi  = ci % gp.Length;
                            int oct = ci / gp.Length;
                            baseHz = gp[pi] * MathF.Pow(2f, oct);
                        }
                        else
                        {
                            baseHz = Root * MathF.Pow(2f, ci / (float)(NCells - 1) * 2f);
                        }
                        float rawHz   = baseHz * MathF.Pow(2f, Math.Clamp(cell.TrapMean.X * 0.15f, -0.2f, 0.4f));
                        _resFreq[ci]  = frame.GeometryPitches != null ? rawHz : _jiQuantizer.Quantize(rawHz);
                        _resGain[ci]  = Math.Clamp(energy * 1.5f, 0.1f, 1f);
                        float decayS  = 3.0f + Math.Clamp(cell.HitRatio, 0f, 1f) * 6.0f;  // 3.0–9.0 s
                        _resDecay[ci] = MathF.Exp(-1f / (decayS * SampleRate));
                    }
                    _prevCellEnergy[ci] = energy;
                }
            }

            float rawDrone = Root * MathF.Pow(2f, Math.Clamp(frame.TrapMean.X * 0.3f, -0.2f, 0.8f));
            _wtPitch = _jiQuantizer.Quantize(rawDrone);
            _srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

            if (frame.WaveshaperCurve is { Length: >= WtLen } wsc)
            {
                Array.Copy(wsc, _wsTableTarget, WtLen);
                _wsHasData = true;
            }
        }

        for (int s = 0; s < buf.Length; s++)
        {
            _gS += _gcf * (_gT - _gS);
            for (int wi = 0; wi < WtLen; wi++) _currentWt[wi] += _wtMorphCf * (_targetWt[wi] - _currentWt[wi]);

            _wtPitchS += _wtPitchSlewCf * (_wtPitch - _wtPitchS);
            float droneInc = _wtPitchS * WtLen / SampleRate;
            _wtPhase = (_wtPhase + droneInc) % WtLen;
            int   ti0   = (int)_wtPhase;
            float tfrac = _wtPhase - ti0;
            float droneOut = (_currentWt[ti0] + tfrac * (_currentWt[(ti0 + 1) % WtLen] - _currentWt[ti0])) * _gS * 0.17f;

            float bellMix = 0f;
            for (int ri = 0; ri < NCells; ri++)
            {
                if (_resGain[ri] < 1e-5f) continue;
                bellMix      += _resGain[ri] * MathF.Sin(2f * MathF.PI * _resOscPh[ri]) * 0.025f;
                _resGain[ri]  *= _resDecay[ri];
                _resOscPh[ri]  = (_resOscPh[ri] + _resFreq[ri] / SampleRate) % 1f;
            }

            float ap = StreamAllPass(bellMix, _ap0buf, ref _ap0w, KAp0, 0.65f);
            ap = StreamAllPass(ap, _ap1buf, ref _ap1w, KAp1, 0.65f);
            ap = StreamAllPass(ap, _ap2buf, ref _ap2w, KAp2, 0.65f);
            ap = StreamAllPass(ap, _ap3buf, ref _ap3w, KAp3, 0.65f);
            float c0 = StreamCombLpf(ap, _cm0buf, ref _cm0w, KCm0, 0.72f, ref _cm0lpf, 0.18f);
            float c1 = StreamCombLpf(ap, _cm1buf, ref _cm1w, KCm1, 0.72f, ref _cm1lpf, 0.18f);

            _srGlideSm += _srGlideCf * (_srGlideT - _srGlideSm);
            float shepOut = SampleShepard() * 0.13f;
            float wsOut   = SampleWaveshaper() * 0.07f;
            float sig = 0.85f * (float)Math.Tanh((droneOut + (c0 + c1) * 0.38f) * 1.1f) + shepOut + wsOut;
            buf[s] = Clip16(sig);
        }
    }

    private static float StreamAllPass(float x, float[] buf, ref int w, int D, float g)
    {
        int len = buf.Length;
        int rp  = (w - D + len) % len;
        float y = -g * x + buf[rp];
        buf[w] = x + g * y;
        w = (w + 1) % len;
        return y;
    }

    private static float StreamCombLpf(float x, float[] buf, ref int w, int D,
                                        float fb, ref float lpf, float damp)
    {
        int len = buf.Length;
        int rp  = (w - D + len) % len;
        float delayed = buf[rp];
        lpf  += damp * (delayed - lpf);
        float y = Math.Clamp(x + fb * lpf, -12f, 12f);
        buf[w] = y;
        w = (w + 1) % len;
        return y;
    }

    // ----------------------------------------------------------------------- Apollonian hybrid (M7g)
    // No cell telemetry needed: bells fire on a camera-speed-gated timer and ring at the
    // integer Apollonian curvature pitches from frame.GeometryPitches (or a static fallback).
    // Pitch set = {1/1, 35/32, 19/16, 3/2, 15/8} × 110 Hz — the canonical JI pentatonic
    // derived from Apollonian gasket seeds {-1,2,2,3}.

    private void FillApollonianHybrid(short[] buf)
    {
        float cameraSpd = 0f;
        float[]? geomPitches = null;

        var frame = _getFrame();
        if (frame != null)
        {
            _gT = 0.55f;  // no HitRatio telemetry — use moderate constant gain
            _srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);
            cameraSpd = frame.CameraSpeed;
            if (frame.GeometryPitches is { Length: > 0 }) geomPitches = frame.GeometryPitches;
        }

        // Bell trigger interval: 0.8 s at rest, ~0.25 s at camera speed ≥ 5 units/s
        int trigInterval = (int)(SampleRate * Math.Clamp(0.8f - cameraSpd * 0.11f, 0.25f, 0.9f));

        for (int s = 0; s < buf.Length; s++)
        {
            _gS += _gcf * (_gT - _gS);
            _apoTrigCounter++;

            if (_apoTrigCounter >= trigInterval)
            {
                _apoTrigCounter = 0;
                float[] pitches = geomPitches ?? _defaultApoScalePitches;
                int pi   = _apoNextPitch % pitches.Length;
                int oct  = (_apoNextPitch / pitches.Length) % 3;
                int slot = _apoNextPitch % NCells;
                _resFreq[slot]  = pitches[pi] * MathF.Pow(2f, oct);
                _resGain[slot]  = 0.82f;
                float decayS    = 1.0f + (pi / (float)(pitches.Length - 1)) * 1.5f;  // 1.0–2.5 s
                _resDecay[slot] = MathF.Exp(-1f / (decayS * SampleRate));
                _resOscPh[slot] = 0f;
                _apoNextPitch++;
            }

            float bellMix = 0f;
            for (int ri = 0; ri < NCells; ri++)
            {
                if (_resGain[ri] < 1e-5f) continue;
                bellMix      += _resGain[ri] * MathF.Sin(2f * MathF.PI * _resOscPh[ri]) * 0.025f;
                _resGain[ri]  *= _resDecay[ri];
                _resOscPh[ri]  = (_resOscPh[ri] + _resFreq[ri] / SampleRate) % 1f;
            }

            float ap = StreamAllPass(bellMix, _ap0buf, ref _ap0w, KAp0, 0.65f);
            ap = StreamAllPass(ap, _ap1buf, ref _ap1w, KAp1, 0.65f);
            ap = StreamAllPass(ap, _ap2buf, ref _ap2w, KAp2, 0.65f);
            ap = StreamAllPass(ap, _ap3buf, ref _ap3w, KAp3, 0.65f);
            float c0 = StreamCombLpf(ap, _cm0buf, ref _cm0w, KCm0, 0.72f, ref _cm0lpf, 0.18f);
            float c1 = StreamCombLpf(ap, _cm1buf, ref _cm1w, KCm1, 0.72f, ref _cm1lpf, 0.18f);
            float reverbOut = (c0 + c1) * 0.35f;

            _srGlideSm += _srGlideCf * (_srGlideT - _srGlideSm);
            float shepOut = SampleShepard() * 0.13f;

            float sig = 0.85f * (float)Math.Tanh(reverbOut * 1.2f) + shepOut;
            buf[s] = Clip16(sig);
        }
    }

    // ----------------------------------------------------------------------- Waveshaper voice (M7h)
    // Called once per sample.  Updates _wsTableCurr toward target, then passes a sine at the
    // slewed drone pitch through the 64-point DE cross-section table lookup.
    // Returns 0 when no waveshaper data has arrived yet (_wsHasData == false).

    private float SampleWaveshaper()
    {
        if (!_wsHasData) return 0f;

        // Morph curve toward target (same τ as wavetable morph)
        for (int wi = 0; wi < WtLen; wi++)
            _wsTableCurr[wi] += _wsMorphCf * (_wsTableTarget[wi] - _wsTableCurr[wi]);

        // Input: sine at slewed drone pitch
        _wsPhase = (_wsPhase + _wtPitchS / SampleRate) % 1f;
        float sineIn = MathF.Sin(2f * MathF.PI * _wsPhase);

        // Table lookup with linear interpolation: map [-1,1] → [0, WtLen-1]
        float idxF = (sineIn + 1f) * 0.5f * (WtLen - 1);
        int   idxI = Math.Clamp((int)idxF, 0, WtLen - 2);
        float frac = idxF - idxI;
        return _wsTableCurr[idxI] + frac * (_wsTableCurr[idxI + 1] - _wsTableCurr[idxI]);
    }

    // ----------------------------------------------------------------------- Shepard–Risset layer (M7f)
    // Call once per sample.  Uses/updates _srBase, _srPhases, _srGlideSm (must be slewed first).

    private float SampleShepard()
    {
        // Advance log2-frequency base by the smoothed glide rate
        _srBase += _srGlideSm / (12f * SampleRate);
        if (_srBase >= 1f) _srBase -= 1f;
        else if (_srBase < 0f) _srBase += 1f;

        // baseFreq is the lowest partial for this fractional octave position
        float baseFreq = SR_LO * MathF.Pow(2f, _srBase);

        float sum = 0f;
        for (int i = 0; i < NSR; i++)
        {
            float freq = baseFreq * SR_MULT[i];
            _srPhases[i] += freq / SampleRate;
            if (_srPhases[i] >= 1f) _srPhases[i] -= 1f;

            float logOct = SR_LOG2 + _srBase + i;   // octaves from SR_CTR
            float gain   = MathF.Exp(-logOct * logOct * SR_INV2SG);
            sum += gain * MathF.Sin(2f * MathF.PI * _srPhases[i]);
        }
        return sum / NSR;
    }

    // ----------------------------------------------------------------------- BurningShip hybrid (M7e)
    // Dorian at C3 (130.81 Hz); bells spread C3–C5 (2 octaves); short percussive decay 0.08–0.45 s.

    private void FillBurningShipHybrid(short[] buf)
    {
        const float Root = 130.81f;

        var frame = _getFrame();
        if (frame != null)
        {
            _gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(frame.HitRatio, 0f, 1f));

            float chaos = Math.Clamp(frame.NormalVariance * 2.5f, 0f, 1f);
            _jiQuantizer.TemperamentStrength = Math.Clamp((1f - chaos) * TemperamentCeiling, 0f, 1f);

            if (frame.Cells != null)
            {
                Array.Clear(_blendBuf);
                float totalW = 0f;
                int nCells = Math.Min(frame.Cells.Length, NCells);
                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    var wt = cell.OrbitWavetable;
                    if (wt != null && wt.Length >= WtLen && cell.Energy > 0.05f)
                    {
                        for (int wi = 0; wi < WtLen; wi++) _blendBuf[wi] += wt[wi] * cell.Energy;
                        totalW += cell.Energy;
                    }
                }
                if (totalW > 0f)
                    for (int wi = 0; wi < WtLen; wi++) _targetWt[wi] = _blendBuf[wi] / totalW;

                for (int ci = 0; ci < nCells; ci++)
                {
                    var cell = frame.Cells[ci];
                    float energy = cell.Energy;
                    bool wasActive = _prevCellEnergy[ci] >= 0.08f;
                    if (energy >= 0.08f && (!wasActive || energy > _prevCellEnergy[ci] + 0.06f))
                    {
                        float baseHz  = Root * MathF.Pow(2f, ci / (float)(NCells - 1) * 2f);  // C3–C5
                        float rawHz   = baseHz * MathF.Pow(2f, Math.Clamp(cell.TrapMean.X * 0.25f, -0.25f, 0.5f));
                        _resFreq[ci]  = _jiQuantizer.Quantize(rawHz);
                        _resGain[ci]  = Math.Clamp(energy * 1.5f, 0.1f, 1f);
                        float decayS  = 0.08f + Math.Clamp(cell.HitRatio, 0f, 1f) * 0.37f;  // 0.08–0.45 s
                        _resDecay[ci] = MathF.Exp(-1f / (decayS * SampleRate));
                    }
                    _prevCellEnergy[ci] = energy;
                }
            }

            float rawDrone = Root * MathF.Pow(2f, Math.Clamp(frame.TrapMean.X * 0.3f, -0.2f, 0.8f));
            _wtPitch = _jiQuantizer.Quantize(rawDrone);
            _srGlideT = Math.Clamp(-frame.ZoomVelocity * 12f, -36f, 36f);

            if (frame.WaveshaperCurve is { Length: >= WtLen } wsc)
            {
                Array.Copy(wsc, _wsTableTarget, WtLen);
                _wsHasData = true;
            }
        }

        for (int s = 0; s < buf.Length; s++)
        {
            _gS += _gcf * (_gT - _gS);
            for (int wi = 0; wi < WtLen; wi++) _currentWt[wi] += _wtMorphCf * (_targetWt[wi] - _currentWt[wi]);

            _wtPitchS += _wtPitchSlewCf * (_wtPitch - _wtPitchS);
            float droneInc = _wtPitchS * WtLen / SampleRate;
            _wtPhase = (_wtPhase + droneInc) % WtLen;
            int   ti0   = (int)_wtPhase;
            float tfrac = _wtPhase - ti0;
            float droneOut = (_currentWt[ti0] + tfrac * (_currentWt[(ti0 + 1) % WtLen] - _currentWt[ti0])) * _gS * 0.19f;

            float bellMix = 0f;
            for (int ri = 0; ri < NCells; ri++)
            {
                if (_resGain[ri] < 1e-5f) continue;
                bellMix      += _resGain[ri] * MathF.Sin(2f * MathF.PI * _resOscPh[ri]) * 0.025f;
                _resGain[ri]  *= _resDecay[ri];
                _resOscPh[ri]  = (_resOscPh[ri] + _resFreq[ri] / SampleRate) % 1f;
            }

            float ap = StreamAllPass(bellMix, _ap0buf, ref _ap0w, KAp0, 0.65f);
            ap = StreamAllPass(ap, _ap1buf, ref _ap1w, KAp1, 0.65f);
            ap = StreamAllPass(ap, _ap2buf, ref _ap2w, KAp2, 0.65f);
            ap = StreamAllPass(ap, _ap3buf, ref _ap3w, KAp3, 0.65f);
            float c0 = StreamCombLpf(ap, _cm0buf, ref _cm0w, KCm0, 0.72f, ref _cm0lpf, 0.18f);
            float c1 = StreamCombLpf(ap, _cm1buf, ref _cm1w, KCm1, 0.72f, ref _cm1lpf, 0.18f);

            _srGlideSm += _srGlideCf * (_srGlideT - _srGlideSm);
            float shepOut = SampleShepard() * 0.13f;
            float wsOut   = SampleWaveshaper() * 0.07f;
            float sig = 0.85f * (float)Math.Tanh((droneOut + (c0 + c1) * 0.18f) * 1.1f) + shepOut + wsOut;
            buf[s] = Clip16(sig);
        }
    }

    // ------------------------------------------------------------------ shared

    private static short Clip16(float s)
        => (short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue);

    // Looped tone for spatial emitters (C2 fundamental + octave)
    private static short[] GenerateToneBuffer()
    {
        int period = (int)Math.Round((double)SampleRate / 65.41);  // C2 ≈ 674 samples
        var buf = new short[period];
        for (int i = 0; i < period; i++)
        {
            float t = 2f * MathF.PI * i / period;
            float s = 0.70f * MathF.Sin(t) + 0.30f * MathF.Sin(2f * t);
            buf[i] = (short)Math.Clamp((int)(s * 14000f), short.MinValue, short.MaxValue);
        }
        return buf;
    }

    // ------------------------------------------------------------------ macOS 15

    private static void EnsureMacOsOverride()
    {
        if (!OperatingSystem.IsMacOS() || Environment.OSVersion.Version.Major < 15 || _overrideInstalled)
            return;

        IEnumerable<string> cellar = Directory.Exists("/opt/homebrew/Cellar/openal-soft")
            ? Directory.GetDirectories("/opt/homebrew/Cellar/openal-soft")
                .Select(v => Path.Combine(v, "lib", "libopenal.dylib"))
            : Enumerable.Empty<string>();

        string[] candidates =
        [
            ..cellar,
            "/opt/homebrew/lib/libopenal.dylib",
            "/usr/local/Cellar/openal-soft/1.25.2/lib/libopenal.dylib",
            "/usr/local/lib/libopenal.dylib",
        ];

        string? softPath = candidates.FirstOrDefault(File.Exists);
        if (softPath != null)
        {
            OpenALLibraryNameContainer.OverridePath = softPath;
            _overrideInstalled = true;
        }
    }
}
