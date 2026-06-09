using OpenTK.Audio.OpenAL;

namespace Parsec.Audio.Sonification;

/// <summary>
/// Streaming OpenAL source that synthesises a real-time fractal drone from live
/// <see cref="FractalSonicFrame"/> telemetry.  Runs a dedicated background thread
/// that keeps a 4-buffer queue fed; the UI/render thread publishes frames lock-free
/// via a <see cref="Func{FractalSonicFrame}"/> callback.
///
/// DSP is identical to <see cref="FractalDroneSynth"/> but stateful — oscillator
/// phases and one-pole smoother state persist across buffer boundaries so there are
/// no clicks or phase resets at buffer edges.
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
    private const int NumSpatialSrcs  = 16;

    private const float BaseFundHz  = 65.41f;   // C2 — matches FractalDroneSynth
    private const float MasterGain  = 0.22f;
    private const float SpatialGain = 0.30f;    // max gain per spatial emitter

    private readonly Func<FractalSonicFrame?> _getFrame;

    // Pre-allocated PCM buffers — never reallocated; zero allocation on the audio thread
    private readonly short[][] _pcmBufs;

    // AL handles — only valid while the worker thread is alive
    private int[] _alBufs     = [];
    private int   _alSrc;
    private int[] _spatialSrcs = [];
    private int   _spatialToneBuf;
    private bool  _spatialReady;

    private Thread? _worker;
    private volatile bool _running;
    private bool _disposed;

    // ---- DSP state (persists across buffer fills) ----
    private float _gS, _cS = 1200f, _nS, _bS, _pS, _lpfY;
    private readonly float[] _ph = new float[4];
    private uint _rng = 0xABCDEF01u;   // different seed from offline M3 path

    // One-pole smoother coefficients
    private readonly float _gcf, _ccf, _ncf, _bcf, _pcf;

    // Last known good control targets — held when frame is null (renderer stalled)
    private float _gT = 0.05f, _cT = 1200f, _nT, _bT, _pT;

    // Listener orientation array — pre-allocated; only touched on audio thread
    private readonly float[] _listenerOrient = new float[6];

    // macOS 15 openal-soft redirect (idempotent global; same logic as OpenALAudioPlaybackBackend)
    private static bool _overrideInstalled;

    public FractalDroneStream(Func<FractalSonicFrame?> getFrame)
    {
        _getFrame = getFrame;

        _pcmBufs = new short[NumBuffers][];
        for (int i = 0; i < NumBuffers; i++)
            _pcmBufs[i] = new short[SamplesPerBuffer];

        static float Coeff(float tc, int sr) => 1f - MathF.Exp(-1f / (tc * sr));
        _gcf = Coeff(0.05f, SampleRate);
        _ccf = Coeff(0.03f, SampleRate);
        _ncf = Coeff(0.08f, SampleRate);
        _bcf = Coeff(0.10f, SampleRate);
        _pcf = Coeff(0.025f, SampleRate);

        _gS = 0.05f;   // start near-silent; fades in as geometry data arrives
    }

    /// <summary>
    /// Start the streaming worker.  Returns <c>true</c> if the worker started
    /// (OpenAL available); <c>false</c> if the device could not be opened.
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
            AL.BufferData(_alBufs[i], ALFormat.Mono16, _pcmBufs[i], SampleRate);
            AL.SourceQueueBuffer(_alSrc, _alBufs[i]);
        }

        // Ambient source always sits at the listener (ignores 3D distance model)
        AL.Source(_alSrc, ALSourceb.SourceRelative, true);
        AL.Source(_alSrc, ALSourcef.Gain, 1.0f);

        // Distance model applies to the 16 spatial sources
        AL.DistanceModel(ALDistanceModel.InverseDistanceClamped);
        AL.SourcePlay(_alSrc);

        // ---- 16 spatial 3D emitters ----
        SetupSpatialSources();

        try
        {
            while (_running)
            {
                // Refill streaming buffers
                AL.GetSource(_alSrc, ALGetSourcei.BuffersProcessed, out int processed);
                while (processed-- > 0)
                {
                    int alBuf = AL.SourceUnqueueBuffer(_alSrc);
                    int slot  = SlotOf(alBuf);
                    FillBuffer(_pcmBufs[slot]);
                    AL.BufferData(alBuf, ALFormat.Mono16, _pcmBufs[slot], SampleRate);
                    AL.SourceQueueBuffer(_alSrc, alBuf);
                }

                // Underrun recovery
                AL.GetSource(_alSrc, ALGetSourcei.SourceState, out int st);
                if ((ALSourceState)st == ALSourceState.Stopped && _running)
                    AL.SourcePlay(_alSrc);

                // Update listener and spatial emitters from latest frame
                UpdateSpatialSources();

                Thread.Sleep(5);   // 5 ms << 23 ms per buffer → queue stays fed
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
            AL.Source(src, ALSourcei.Buffer,    _spatialToneBuf);
            AL.Source(src, ALSourceb.Looping,   true);
            AL.Source(src, ALSourceb.SourceRelative, false);
            AL.Source(src, ALSourcef.ReferenceDistance, 2.0f);
            AL.Source(src, ALSourcef.MaxDistance,       50.0f);
            AL.Source(src, ALSourcef.RolloffFactor,     1.0f);
            AL.Source(src, ALSourcef.Gain,              0.0f);  // silent until first frame
            AL.SourcePlay(src);
        }
        _spatialReady = true;
    }

    private void UpdateSpatialSources()
    {
        if (!_spatialReady) return;

        var frame = _getFrame();
        if (frame == null || frame.CameraForward == System.Numerics.Vector3.Zero) return;

        // Update OpenAL listener with camera world position and orientation
        var cp  = frame.CameraPosition;
        var cf  = frame.CameraForward;
        var cup = frame.CameraUp;
        AL.Listener(ALListener3f.Position, cp.X, cp.Y, cp.Z);
        _listenerOrient[0] = cf.X;  _listenerOrient[1] = cf.Y;  _listenerOrient[2] = cf.Z;
        _listenerOrient[3] = cup.X; _listenerOrient[4] = cup.Y; _listenerOrient[5] = cup.Z;
        AL.Listener(ALListenerfv.Orientation, ref _listenerOrient[0]);

        // Update each spatial emitter
        var cells = frame.Cells;
        for (int i = 0; i < NumSpatialSrcs; i++)
        {
            if (cells != null && i < cells.Length)
            {
                var cell = cells[i];
                var wp = cell.WorldPosition;
                AL.Source(_spatialSrcs[i], ALSource3f.Position, wp.X, wp.Y, wp.Z);

                float gain  = cell.Energy * SpatialGain;
                AL.Source(_spatialSrcs[i], ALSourcef.Gain, gain);

                // Pitch shifts cell tone by orbit-radius characteristic (~0.5× to 2×)
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

    // ------------------------------------------------------------------ DSP

    private void FillBuffer(short[] buf)
    {
        // Snapshot control targets from the latest published frame.
        // If null (renderer stalled), hold the previous targets — no glitch.
        var frame = _getFrame();
        if (frame != null)
        {
            _gT = 0.05f + 0.95f * MathF.Sqrt(Math.Clamp(frame.HitRatio, 0f, 1f));
            _cT = DepthToCutoff(frame.MeanDepth);
            _nT = Math.Clamp(frame.StepP90 / 120f, 0f, 0.5f);
            _bT = Math.Clamp(frame.NormalVariance * 2f, 0f, 1f);
            _pT = Math.Clamp(frame.CameraSpeed * 0.003f, -0.02f, 0.02f);
        }

        for (int s = 0; s < buf.Length; s++)
        {
            // Advance all one-pole smoothers one sample
            _gS += _gcf * (_gT - _gS);
            _cS += _ccf * (_cT - _cS);
            _nS += _ncf * (_nT - _nS);
            _bS += _bcf * (_bT - _bS);
            _pS += _pcf * (_pT - _pS);

            // Harmonic weights: b=0 → warm/dark, b=1 → bright/buzzy
            float w0   = 1.00f;
            float w1   = 0.60f - 0.45f * _bS;
            float w2   = 0.10f + 0.55f * _bS;
            float w3   = 0.02f + 0.38f * _bS;
            float wSum = w0 + w1 + w2 + w3;

            // 4-partial saw drone
            float pfactor = 1f + _pS;
            float sig = 0f;
            for (int h = 0; h < 4; h++)
            {
                float freq = BaseFundHz * (h + 1) * pfactor;
                float w    = h switch { 0 => w0, 1 => w1, 2 => w2, _ => w3 };
                sig   += (2f * _ph[h] - 1f) * w;
                _ph[h] = (_ph[h] + freq / SampleRate) % 1f;
            }
            sig /= wSum;

            // Noise layer (granular texture)
            _rng  = _rng * 1664525u + 1013904223u;
            float noise = (float)(int)_rng / 2147483648f;
            sig  += noise * MathF.Max(0f, _nS);

            // One-pole LPF
            float alpha = Math.Clamp(1f - MathF.Exp(-2f * MathF.PI * _cS / SampleRate), 0f, 1f);
            _lpfY += alpha * (sig - _lpfY);

            // Master gain + pack to PCM16
            sig    = _lpfY * _gS * MasterGain;
            buf[s] = (short)Math.Clamp((int)(sig * 32767f), short.MinValue, short.MaxValue);
        }
    }

    private static float DepthToCutoff(float depth)
    {
        float t = Math.Clamp(depth / 20f, 0f, 1f);
        return MathF.Exp(MathF.Log(8000f) + t * (MathF.Log(300f) - MathF.Log(8000f)));
    }

    // Generate a single-period looped tone buffer for the spatial emitters:
    // fundamental + octave sawtooth at C2 (65.41 Hz), both harmonics complete exactly
    // one and two full cycles in `period` samples → seamless loop, no click.
    private static short[] GenerateToneBuffer()
    {
        int period = (int)Math.Round((double)SampleRate / BaseFundHz);  // ≈ 674 samples
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
