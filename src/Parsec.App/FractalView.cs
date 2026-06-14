using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using AvDrawingContext = Avalonia.Media.DrawingContext;
using Avalonia.Threading;
using Parsec.Rendering;
using Parsec.Rendering.DeepZoom;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Metal;
using Parsec.Rendering.Raymarching;

namespace Parsec.App;

public enum SurfaceTextureSource { None, Image, Feedback, MandelbrotZoom, Video }

/// <summary>
/// Stage 2: a live free-fly view of the AmazingBox. WASD moves (with Q/E for
/// vertical), hold left-drag to mouse-look. Movement speed scales with the
/// distance estimate at the camera so it feels right at every scale. Renders
/// on change only — idle draws nothing.
/// </summary>
public enum FractalType { AmazingBox, Mandelbox, Kifs, Kleinian, Attractor, Mandelbulb, QuaternionJulia, RotBox, Hybrid, QJBox, Menger, Bicomplex, Apollonian, Phoenix, Biomorph, Mosely, PseudoKleinian4D, RiemannSphere, Mandalay, Anisotropic, OrbitHybrid, BurningShip, DeepZoom }

public sealed class FractalView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    private Gl? _gl;
    private RaymarchPipeline? _pipeline;
    private GpuMandelboxRenderer? _boxRenderer;
    private MetalMandelboxRenderer?  _metalRenderer;             // null on non-macOS or when Metal unavailable
    private MetalMandelbulbRenderer? _metalMandelbulbRenderer;   // null on non-macOS or when Metal unavailable
    private MetalRotBoxRenderer?     _metalRotBoxRenderer;        // null on non-macOS or when Metal unavailable
    private MetalKifsRenderer?      _metalKifsRenderer;          // null on non-macOS or when Metal unavailable
    private MetalKleinianRenderer?  _metalKleinianRenderer;      // null on non-macOS or when Metal unavailable
    private MetalHybridRenderer?          _metalHybridRenderer;           // null on non-macOS or when Metal unavailable
    private MetalBurningShipRenderer?     _metalBurningShipRenderer;
    private MetalMengerRenderer?          _metalMengerRenderer;
    private MetalQuaternionJuliaRenderer? _metalQuaternionJuliaRenderer;
    private MetalQJBoxRenderer?           _metalQJBoxRenderer;
    private MetalApollonianRenderer?      _metalApollonianRenderer;
    private MetalBicomplexRenderer?       _metalBicomplexRenderer;
    private MetalPhoenixRenderer?         _metalPhoenixRenderer;
    private MetalBiomorphRenderer?        _metalBiomorphRenderer;
    private MetalMoselyRenderer?          _metalMoselyRenderer;
    private MetalPseudoKleinian4DRenderer? _metalPK4DRenderer;
    private MetalRiemannSphereRenderer?   _metalRiemannSphereRenderer;
    private MetalMandalayRenderer?        _metalMandalayRenderer;
    private MetalAnisotropicRenderer?     _metalAnisotropicRenderer;
    private MetalOrbitHybridRenderer?     _metalOrbitHybridRenderer;
    private MetalDeepZoomRenderer?        _metalDeepZoomRenderer;
    private MetalAttractorRenderer?       _metalAttractorRenderer;
    private long _metalComputeMs;
    private long _metalReadbackMs;
    private long _texUploadMs;
    private long _totalFrameMs;
    private GpuKifsRenderer? _kifsRenderer;
    private GpuKleinianRenderer? _kleinianRenderer;
    private GpuAttractorRenderer? _attractorRenderer;
    private GpuMandelbulbRenderer? _mandelbulbRenderer;
    private GpuQuaternionJuliaRenderer? _qjuliaRenderer;
    private GpuRotBoxRenderer? _rotboxRenderer;
    private GpuHybridRenderer? _hybridRenderer;
    private GpuQJBoxRenderer? _qjboxRenderer;
    private GpuMengerRenderer? _mengerRenderer;
    private GpuBicomplexRenderer? _bicomplexRenderer;
    private GpuApollonianRenderer? _apollonianRenderer;
    private GpuPhoenixRenderer? _phoenixRenderer;
    private GpuBiomorphRenderer? _biomorphRenderer;
    private GpuMoselyRenderer? _moselyRenderer;
    private GpuPseudoKleinian4DRenderer? _pk4dRenderer;
    private GpuRiemannSphereRenderer? _riemannRenderer;
    private GpuMandalayRenderer? _mandalayRenderer;
    private GpuAnisotropicRenderer? _anisoRenderer;
    private GpuOrbitHybridRenderer? _orbitHybridRenderer;
    private GpuBurningShipRenderer? _burningShipRenderer;
    private DeepZoomPipeline? _deepPipeline;
    private Parsec.Core.Attractors.AttractorHash? _attractorHash;
    private bool _attractorNeedsRegen = true;
    private uint _texture, _vao, _blitProgram;
    private int _samplerLocation;
    private bool _ready;
    private bool _dirty = true;
    private readonly System.Diagnostics.Stopwatch _sonicClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Diagnostics.Stopwatch _telemetryThrottle = System.Diagnostics.Stopwatch.StartNew();
    private Parsec.Rendering.Metal.FractalGeometryStats? _lastTelemetry;

    /// <summary>Optional sonification controller. Set by the host (MainWindow) when sonification is enabled.</summary>
    public SonificationController? Sonification { get; set; }

    // --- MIDI output (geometry → MIDI CC, parallel to the internal synth) ---
    private Parsec.Audio.Midi.MidiOutputSession? _midiSession;
    private Parsec.Audio.Midi.MidiOutputController? _midiController;
    private bool _midiEnabled;

    /// <summary>When true, the live telemetry frame is translated to MIDI CCs on the
    /// virtual "Parsec" source each frame. Lazily creates the CoreMIDI session.</summary>
    public bool MidiEnabled
    {
        get => _midiEnabled;
        set
        {
            if (value && _midiSession == null)
            {
                _midiSession = new Parsec.Audio.Midi.MidiOutputSession("Parsec");
                _midiController = new Parsec.Audio.Midi.MidiOutputController(_midiSession);
                _midiController.Smoothing = _midiSmoothing;
            }
            _midiEnabled = value && (_midiSession?.IsAvailable ?? false);
            _midiController?.Reset();
        }
    }

    private float _midiSmoothing = 0.25f;

    /// <summary>MIDI responsiveness: one-pole smoothing factor (0.05 = smooth/laggy →
    /// 1.0 = instant/jittery). Applied live to the controller when present.</summary>
    public float MidiResponsiveness
    {
        get => _midiSmoothing;
        set
        {
            _midiSmoothing = Math.Clamp(value, 0.05f, 1f);
            if (_midiController != null) _midiController.Smoothing = _midiSmoothing;
        }
    }

    /// <summary>Null when no MIDI session has been created; otherwise its availability reason.</summary>
    public string MidiStatus => _midiSession == null
        ? "MIDI off"
        : _midiSession.IsAvailable ? $"MIDI: Parsec · {_midiController?.Monitor}" : $"MIDI unavailable: {_midiSession.UnavailableReason}";

    private void EmitMidi(Audio.Sonification.FractalSonicFrame? frame, uint[]? pixels = null, int width = 0, int height = 0)
    {
        if (_midiEnabled && frame != null)
        {
            _lastSonicTime = frame.Time;
            if (pixels != null && width > 0 && height > 0)
            {
                // Real on-screen colour: coverage-masked mean of the rendered frame buffer
                // (palette × orbit traps × lighting × bloom × grade), not just the palette knob.
                var (h, s, v) = Parsec.Audio.Midi.MidiOutputController.MeanScreenColorHsv(pixels, width, height);
                _midiController?.Update(frame, h, s, v);
            }
            else
            {
                // Fallback (e.g. CLI, no frame buffer): mean palette colour ≈ Base.
                float hue = Parsec.Audio.Midi.MidiOutputController.Hue01(Palette.BaseR, Palette.BaseG, Palette.BaseB);
                _midiController?.Update(frame, hue);
            }
        }
    }

    private string MidiSuffix()
    {
        if (!_midiEnabled || _midiController == null) return "";
        // Flash the most recent fold/expand/contract/simplify event for ~0.6 s.
        string ev = "";
        if (!string.IsNullOrEmpty(_midiController.LastEvent) &&
            _midiController.LastEventTime > double.NegativeInfinity)
        {
            double age = (_lastSonicTime - _midiController.LastEventTime);
            if (age >= 0 && age < 0.6) ev = $" ⟪{_midiController.LastEvent}⟫";
        }
        return $"  ·  MIDI {_midiController.Monitor}{ev}";
    }
    private double _lastSonicTime;

    // Software-blit fallback for macOS when the Avalonia compositor runs in
    // Software mode (no GL context for OpenGlControlBase). Metal renderers
    // produce uint[], which we copy into a WriteableBitmap and draw via
    // Avalonia's DrawingContext. No OpenGL involved.
    private bool _softwareMode;
    private WriteableBitmap? _softBitmap;
    private bool _metalInitDone;
    private DispatcherTimer? _softTimer;

    // OpenGlControlBase hides InvalidateVisual() with `new` and redirects it
    // to the compositor pipeline (which is dead without a GL context). In
    // software mode we need the REAL Visual.InvalidateVisual() to trigger our
    // Render(DrawingContext) override.
    private void SoftInvalidate() => ((Avalonia.Visual)this).InvalidateVisual();

    // Live fractal parameters, shared with the parameter panel.
    public AmazingBoxState Fractal { get; } = new();
    public MandelboxState Mandelbox { get; } = new();
    public KifsState Kifs { get; } = new();
    public KleinianState Kleinian { get; } = new();
    public AttractorState Attractor { get; } = new();
    public MandelbulbState Mandelbulb { get; } = new();
    public QuaternionJuliaState QuaternionJulia { get; } = new();
    public RotBoxState RotBox { get; } = new();
    public HybridState Hybrid { get; } = new();
    public QJBoxState QJBox { get; } = new();
    public MengerState Menger { get; } = new();
    public BicomplexState Bicomplex { get; } = new();
    public ApollonianState Apollonian { get; } = new();
    public PhoenixState Phoenix { get; } = new();
    public BiomorphState Biomorph { get; } = new();
    public MoselyState Mosely { get; } = new();
    public PseudoKleinian4DState PseudoKleinian4D { get; } = new();
    public RiemannSphereState RiemannSphere { get; } = new();
    public MandalayState Mandalay { get; } = new();
    public AnisotropicState Anisotropic { get; } = new();
    public OrbitHybridState OrbitHybrid { get; } = new();
    public BurningShipState BurningShip { get; } = new();

    /// <summary>Orbit-trap palette, shared across all fractals.</summary>
    public PaletteState Palette { get; } = new();

    /// <summary>HDR grade parameters for the Metal post-process pass, shared across all 3D fractals.</summary>
    public PostProcessState PostProcess { get; } = new();

    /// <summary>Glossy-reflection material controls, shared across all fractals.</summary>
    public ReflectionState Reflection { get; } = new();

    /// <summary>Key-light direction + intensity, shared across all fractals.</summary>
    public LightState Light { get; } = new();

    /// <summary>Which fractal is currently displayed.</summary>
    public FractalType ActiveType { get; private set; } = FractalType.Kifs;

    /// <summary>Super-sampling AA factor for hero renders (1/4/9/16). Bound from
    /// the UI; preview always renders at 1x regardless of this value.</summary>
    public int HeroSampleCount { get; set; } = 1;

    private SurfaceTextureSource _textureSource = SurfaceTextureSource.None;
    private float _surfaceTextureBlend = 0.7f;
    private float _surfaceTextureScale = 1.25f;
    private string? _surfaceTexturePath;
    private bool _feedbackBootstrapped;
    private byte[]? _feedbackBytes;
    private byte[]? _mandelbrotZoomBytes;
    private readonly DeepZoomView _mandelbrotZoomDeepView = new();
    private List<(byte[] bytes, int w, int h, int rowBytes)>? _videoFrames;
    private int _videoFrameIndex;
    private int _surfaceTextureProjection; // 0 = triplanar, 1 = orbit trap
    private bool _domainWarpEnabled;
    private float _domainWarpStrength = 0.15f;
    private float _domainWarpScale = 1.5f;
    private bool _glowEnabled;
    private float _glowStrength = 2.5f;
    private float _glowFalloff = 12f;

    public bool SurfaceTextureEnabled => _textureSource != SurfaceTextureSource.None;

    public SurfaceTextureSource TextureSource
    {
        get => _textureSource;
        set
        {
            if (_textureSource == value) return;
            if (_textureSource == SurfaceTextureSource.Feedback)
            {
                _feedbackBootstrapped = false;
                _feedbackBytes = null;
                if (!HasSurfaceTextureImage) MetalSurfaceTextureManager.ClearImage();
            }
            else if (_textureSource is SurfaceTextureSource.MandelbrotZoom or SurfaceTextureSource.Video)
            {
                _mandelbrotZoomBytes = null;
                if (!HasSurfaceTextureImage) MetalSurfaceTextureManager.ClearImage();
            }
            _textureSource = value;
            SyncSurfaceTextureState();
            MarkDirty();
        }
    }

    public float SurfaceTextureBlend
    {
        get => _surfaceTextureBlend;
        set
        {
            _surfaceTextureBlend = Math.Clamp(value, 0f, 1f);
            SyncSurfaceTextureState();
            MarkDirty();
        }
    }

    public float SurfaceTextureScale
    {
        get => _surfaceTextureScale;
        set
        {
            _surfaceTextureScale = Math.Clamp(value, 0.05f, 16f);
            SyncSurfaceTextureState();
            MarkDirty();
        }
    }

    // 0 = triplanar, 1 = orbit trap (only for fractals with InjectOrbitTrap)
    public int SurfaceTextureProjection
    {
        get => _surfaceTextureProjection;
        set
        {
            _surfaceTextureProjection = value;
            SyncSurfaceTextureState();
            MarkDirty();
        }
    }

    // Orbit trap is only supported for fractals whose shaders run InjectOrbitTrap.
    public bool SupportsOrbitTrap => ActiveType is FractalType.BurningShip or FractalType.Mandelbox
        or FractalType.Mandelbulb or FractalType.Kifs or FractalType.Menger;

    public bool DomainWarpEnabled
    {
        get => _domainWarpEnabled;
        set
        {
            _domainWarpEnabled = value;
            SyncDomainWarpState();
            MarkDirty();
        }
    }

    public float DomainWarpStrength
    {
        get => _domainWarpStrength;
        set
        {
            _domainWarpStrength = Math.Clamp(value, 0f, 0.75f);
            SyncDomainWarpState();
            MarkDirty();
        }
    }

    public float DomainWarpScale
    {
        get => _domainWarpScale;
        set
        {
            _domainWarpScale = Math.Clamp(value, 0.05f, 12f);
            SyncDomainWarpState();
            MarkDirty();
        }
    }

    public bool GlowEnabled
    {
        get => _glowEnabled;
        set { _glowEnabled = value; SyncGlowState(); MarkDirty(); }
    }

    public float GlowStrength
    {
        get => _glowStrength;
        set { _glowStrength = Math.Clamp(value, 0f, 8f); SyncGlowState(); MarkDirty(); }
    }

    public float GlowFalloff
    {
        get => _glowFalloff;
        set { _glowFalloff = Math.Clamp(value, 0.1f, 500f); SyncGlowState(); MarkDirty(); }
    }

    // Step-glow is implemented in the flagship raymarch shaders (same set as orbit
    // trap). Other 3D fractals ignore the glow lanes until their kernels are updated.
    public bool SupportsGlow => ActiveType is FractalType.Mandelbox or FractalType.Mandelbulb
        or FractalType.Kleinian or FractalType.BurningShip or FractalType.Kifs or FractalType.Menger;

    public bool SupportsSurfaceTexture => ActiveType != FractalType.DeepZoom && ActiveType != FractalType.Attractor;

    public string SurfaceTextureLabel => _surfaceTexturePath is { Length: > 0 }
        ? Path.GetFileName(_surfaceTexturePath)
        : "No image selected";

    public bool HasSurfaceTextureImage => !string.IsNullOrWhiteSpace(_surfaceTexturePath);

    public void SetActiveType(FractalType type)
    {
        ActiveType = type;
        SyncSurfaceTextureState();
        SyncDomainWarpState();
        SyncGlowState();
        MarkDirty();
    }

    public string? SetSurfaceTextureImage(string path)
    {
        using var codec = SkiaSharp.SKCodec.Create(path);
        if (codec == null)
            return "Failed to decode image file.";

        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SkiaSharp.SKCodecResult.Success && result != SkiaSharp.SKCodecResult.IncompleteInput)
            return $"Image decode failed ({result}).";

        int sourceByteCount = checked(bitmap.RowBytes * bitmap.Height);
        var sourceBytes = new byte[sourceByteCount];
        Marshal.Copy(bitmap.GetPixels(), sourceBytes, 0, sourceByteCount);

        int packedRowBytes = checked(bitmap.Width * 4);
        var bytes = new byte[checked(packedRowBytes * bitmap.Height)];
        if (bitmap.RowBytes == packedRowBytes)
        {
            Buffer.BlockCopy(sourceBytes, 0, bytes, 0, bytes.Length);
        }
        else
        {
            for (int y = 0; y < bitmap.Height; y++)
                Buffer.BlockCopy(sourceBytes, y * bitmap.RowBytes, bytes, y * packedRowBytes, packedRowBytes);
        }

        _surfaceTexturePath = path;
        MetalSurfaceTextureManager.SetImage(bytes, bitmap.Width, bitmap.Height, packedRowBytes);
        GpuSurfaceTextureManager.SetImage(bytes, bitmap.Width, bitmap.Height, packedRowBytes);
        SyncSurfaceTextureState();
        MarkDirty();
        return null;
    }

    public void ClearSurfaceTextureImage()
    {
        _surfaceTexturePath = null;
        MetalSurfaceTextureManager.ClearImage();
        GpuSurfaceTextureManager.ClearImage();
        SyncSurfaceTextureState();
        MarkDirty();
    }

    private void SyncSurfaceTextureState()
    {
        bool supportsTexture = SupportsSurfaceTexture;
        bool hasTexture = _textureSource switch
        {
            SurfaceTextureSource.Image        => HasSurfaceTextureImage,
            SurfaceTextureSource.Feedback     => _feedbackBootstrapped,
            SurfaceTextureSource.MandelbrotZoom => _mandelbrotZoomBytes != null,
            SurfaceTextureSource.Video        => _videoFrames?.Count > 0,
            _                                 => false,
        };
        int mode = (SupportsOrbitTrap && _surfaceTextureProjection == 1) ? 1 : 0;
        MetalSurfaceTextureManager.SetControls(
            enabled: supportsTexture && hasTexture,
            blend: _surfaceTextureBlend,
            scale: _surfaceTextureScale,
            mode: mode);
        GpuSurfaceTextureManager.SetControls(
            enabled: supportsTexture && _textureSource == SurfaceTextureSource.Image && HasSurfaceTextureImage,
            blend: _surfaceTextureBlend,
            scale: _surfaceTextureScale);
    }

    private void SyncDomainWarpState()
    {
        DomainWarpState.SetControls(
            enabled: ActiveType != FractalType.DeepZoom && ActiveType != FractalType.Attractor && _domainWarpEnabled,
            strength: _domainWarpStrength,
            scale: _domainWarpScale);
    }

    private void SyncGlowState()
    {
        GlowState.SetControls(
            enabled: SupportsGlow && _glowEnabled,
            strength: _glowStrength,
            falloff: _glowFalloff);
    }

    private void UpdateFeedbackTexture(uint[] pixels, int rw, int rh)
    {
        int rowBytes = rw * 4;
        int byteLen = pixels.Length * 4;
        if (_feedbackBytes is null || _feedbackBytes.Length != byteLen)
            _feedbackBytes = new byte[byteLen];
        Buffer.BlockCopy(pixels, 0, _feedbackBytes, 0, byteLen);
        if (!_feedbackBootstrapped)
        {
            MetalSurfaceTextureManager.SetImage(_feedbackBytes, rw, rh, rowBytes);
            _feedbackBootstrapped = true;
            SyncSurfaceTextureState();
        }
        else
        {
            MetalSurfaceTextureManager.UpdateImage(_feedbackBytes, rw, rh, rowBytes);
        }
    }

    private void UpdateMandelbrotZoomTexture()
    {
        if (_metalDeepZoomRenderer?.IsAvailable != true) return;
        const int tw = 128, th = 128;
        _mandelbrotZoomDeepView.ZoomTowardPixel(0.992, tw / 2.0, th / 2.0, tw, th);
        try
        {
            var pixels = _metalDeepZoomRenderer.Render(
                _mandelbrotZoomDeepView, tw, th,
                Palette.ToParams(), new Color(0.02f, 0.03f, 0.07f),
                PreviewSettings(), interactive: true);
            int rowBytes = tw * 4;
            int byteLen = pixels.Length * 4;
            if (_mandelbrotZoomBytes is null || _mandelbrotZoomBytes.Length != byteLen)
                _mandelbrotZoomBytes = new byte[byteLen];
            Buffer.BlockCopy(pixels, 0, _mandelbrotZoomBytes, 0, byteLen);
            MetalSurfaceTextureManager.SetImage(_mandelbrotZoomBytes, tw, th, rowBytes);
            SyncSurfaceTextureState();
        }
        catch { /* best-effort */ }
    }

    private void UpdateVideoTexture()
    {
        if (_videoFrames == null || _videoFrames.Count == 0) return;
        var (bytes, w, h, rowBytes) = _videoFrames[_videoFrameIndex];
        _videoFrameIndex = (_videoFrameIndex + 1) % _videoFrames.Count;
        MetalSurfaceTextureManager.SetImage(bytes, w, h, rowBytes);
        SyncSurfaceTextureState();
    }

    public string? LoadVideo(string path)
    {
        string tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"parsec_vid_{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg",
                $"-i \"{path}\" -vf \"fps=12,scale=128:128\" \"{tempDir}/frame%05d.png\" -y")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(30_000);

            var frames = new List<(byte[], int, int, int)>();
            foreach (var file in System.IO.Directory.EnumerateFiles(tempDir, "*.png")
                         .OrderBy(f => f))
            {
                using var codec = SkiaSharp.SKCodec.Create(file);
                if (codec == null) continue;
                var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height,
                    SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                using var bmp = new SkiaSharp.SKBitmap(info);
                if (codec.GetPixels(info, bmp.GetPixels()) != SkiaSharp.SKCodecResult.Success) continue;
                int rowBytes = bmp.Width * 4;
                var bytes = new byte[rowBytes * bmp.Height];
                Marshal.Copy(bmp.GetPixels(), bytes, 0, bytes.Length);
                frames.Add((bytes, bmp.Width, bmp.Height, rowBytes));
            }

            if (frames.Count == 0)
                return "No frames extracted from video.";

            _videoFrames = frames;
            _videoFrameIndex = 0;
            SyncSurfaceTextureState();
            return null;
        }
        catch (Exception ex)
        {
            return $"Video load failed: {ex.Message}";
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Build the combined schema for the active fractal plus the shared palette,
    /// so the panel shows fractal params followed by colour controls.
    /// </summary>
    public ParamSchema BuildActiveSchema()
    {
        // Deep-zoom is a 2D escape-time mode: no DE-based fractal params, and no
        // 3D reflection/light. Expose the shared palette only (pan/zoom drive the
        // view via the mouse).
        if (ActiveType == FractalType.DeepZoom)
        {
            // Formula is chosen by the FORMULA dropdown (see SetDeepFormula), not a
            // slider. The panel exposes the palette plus the Julia constant kappa --
            // keyframeable, so a kappa sweep morphs the Julia set in an animation.
            var deepParams = new List<ParamDescriptor>
            {
                new ParamDescriptor {
                    Label = "kappa re (Julia)", Group = "Julia",
                    Min = -2.0, Max = 2.0, Step = 0.0001, Decimals = 4,
                    Get = () => _deepView.KappaRe,
                    Set = v => _deepView.KappaRe = v },
                new ParamDescriptor {
                    Label = "kappa im (Julia)", Group = "Julia",
                    Min = -2.0, Max = 2.0, Step = 0.0001, Decimals = 4,
                    Get = () => _deepView.KappaIm,
                    Set = v => _deepView.KappaIm = v },
            };
            deepParams.AddRange(Palette.BuildSchema().Parameters);
            return new ParamSchema { Parameters = deepParams };
        }

        var fractalSchema = ActiveType switch
        {
            FractalType.AmazingBox => Fractal.BuildSchema(),
            FractalType.Mandelbox => Mandelbox.BuildSchema(),
            FractalType.Kifs => Kifs.BuildSchema(),
            FractalType.Kleinian => Kleinian.BuildSchema(),
            FractalType.Attractor => Attractor.BuildSchema(),
            FractalType.Mandelbulb => Mandelbulb.BuildSchema(),
            FractalType.QuaternionJulia => QuaternionJulia.BuildSchema(),
            FractalType.RotBox => RotBox.BuildSchema(),
            FractalType.Hybrid => Hybrid.BuildSchema(),
            FractalType.QJBox => QJBox.BuildSchema(),
            FractalType.Menger => Menger.BuildSchema(),
            FractalType.Bicomplex => Bicomplex.BuildSchema(),
            FractalType.Apollonian => Apollonian.BuildSchema(),
            FractalType.Phoenix => Phoenix.BuildSchema(),
            FractalType.Biomorph => Biomorph.BuildSchema(),
            FractalType.Mosely => Mosely.BuildSchema(),
            FractalType.PseudoKleinian4D => PseudoKleinian4D.BuildSchema(),
            FractalType.RiemannSphere => RiemannSphere.BuildSchema(),
            FractalType.Mandalay => Mandalay.BuildSchema(),
            FractalType.Anisotropic => Anisotropic.BuildSchema(),
            FractalType.OrbitHybrid => OrbitHybrid.BuildSchema(),
            FractalType.BurningShip => BurningShip.BuildSchema(),
            _ => Kifs.BuildSchema(),
        };
        var combined = new List<ParamDescriptor>(fractalSchema.Parameters);
        combined.AddRange(Palette.BuildSchema().Parameters);
        combined.AddRange(Reflection.BuildSchema().Parameters);
        combined.AddRange(Light.BuildSchema().Parameters);
        if (OperatingSystem.IsMacOS())
            combined.AddRange(PostProcess.BuildSchema().Parameters);
        combined.AddRange(BuildCameraSchema());
        return new ParamSchema { Parameters = combined };
    }

    private IReadOnlyList<ParamDescriptor> BuildCameraSchema() => new[]
    {
        new ParamDescriptor {
            Label = "Azimuth", Group = "Camera", Min = -Math.PI, Max = Math.PI, Decimals = 3,
            Get = () => _orbitAzimuth,
            Set = v => { _orbitAzimuth = (float)v; ApplyOrbitToCamera(); } },
        new ParamDescriptor {
            Label = "Elevation", Group = "Camera", Min = -1.55, Max = 1.55, Decimals = 3,
            Get = () => _orbitElevation,
            Set = v => { _orbitElevation = (float)v; ApplyOrbitToCamera(); } },
        new ParamDescriptor {
            Label = "Distance", Group = "Camera", Min = 0.1, Max = 30.0, Decimals = 2,
            Get = () => _orbitDistance,
            Set = v => { _orbitDistance = (float)v; ApplyOrbitToCamera(); } },
        new ParamDescriptor {
            Label = "Target X", Group = "Camera", Min = -20.0, Max = 20.0, Decimals = 3,
            Get = () => _orbitTarget.X,
            Set = v => { _orbitTarget.X = (float)v; ApplyOrbitToCamera(); } },
        new ParamDescriptor {
            Label = "Target Y", Group = "Camera", Min = -20.0, Max = 20.0, Decimals = 3,
            Get = () => _orbitTarget.Y,
            Set = v => { _orbitTarget.Y = (float)v; ApplyOrbitToCamera(); } },
        new ParamDescriptor {
            Label = "Target Z", Group = "Camera", Min = -20.0, Max = 20.0, Decimals = 3,
            Get = () => _orbitTarget.Z,
            Set = v => { _orbitTarget.Z = (float)v; ApplyOrbitToCamera(); } },
        new ParamDescriptor {
            Label = "FoV", Group = "Camera", Min = 0.1, Max = 1.5, Decimals = 3,
            Get = () => _cam.FovRadians,
            Set = v => _cam.FovRadians = (float)v },
    };

    /// <summary>Request a re-render (e.g. after a parameter change from the panel).</summary>
    public void MarkDirty()
    {
        _dirty = true;
        if (_softwareMode)
            SoftInvalidate();
        else
            RequestNextFrameRendering();
    }

    /// <summary>
    /// Flag that the attractor's GENERATION params changed, so the trajectory +
    /// spatial hash must be rebuilt before the next attractor render. The
    /// expensive rebuild itself happens on the GL thread (it uploads SSBOs), so
    /// here we just set the flag and request a frame. Camera/colour/tube changes
    /// do NOT call this -- they go through MarkDirty for an immediate re-render.
    /// </summary>
    public void RequestAttractorRegen()
    {
        _attractorNeedsRegen = true;
        _dirty = true;
        if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
    }

    // --- hero render (high-res still to PNG) ---
    private bool _heroPending;
    private string? _heroPath;
    private int _heroWidth, _heroHeight;
    private bool _heroTransparentBackground;

    // --- Animation batch render ---
    private bool _animPending;
    private string? _animDir;
    private int _animWidth, _animHeight, _animFrameCount;
    private double _animFps;
    private bool _animTransparentBackground;
    private Action<double>? _animApplyAtTime;   // applies interpolated state at time t (seconds)

    /// <summary>Fired (on UI thread) when an animation batch render finishes.</summary>
    public event Action<string>? AnimationRenderComplete;
    /// <summary>Fired (on UI thread) periodically during a batch render with (frame, total).</summary>
    public event Action<int, int>? AnimationProgress;

    /// <summary>
    /// Render an animation: <paramref name="applyAtTime"/> sets the live params for
    /// a given playback time (seconds); we sample it at each frame, render at the
    /// target resolution, and save frame_NNNNN.png into <paramref name="dir"/>.
    /// Runs synchronously on the next GL callback (the UI will freeze for the
    /// duration -- acceptable for a test render). Camera is whatever it is now
    /// (camera animation is a later phase).
    /// </summary>
    public void RequestAnimationRender(string dir, int width, int height,
        double fps, double durationSeconds, Action<double> applyAtTime,
        bool transparentBackground = false)
    {
        _animDir = dir;
        _animWidth = width;
        _animHeight = height;
        _animFps = fps;
        _animFrameCount = Math.Max(1, (int)Math.Round(durationSeconds * fps));
        _animTransparentBackground = transparentBackground;
        _animApplyAtTime = applyAtTime;
        _animPending = true;
        if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
    }

    /// <summary>Raised after a hero render finishes (or fails), with a status string.</summary>
    public event Action<string>? HeroRenderComplete;

    /// <summary>
    /// Queue a high-resolution render of the CURRENT view, params, and palette to
    /// a PNG at <paramref name="path"/>. The render runs on the next GL frame
    /// (the only time the context is current) via the TDR-safe tiled path.
    /// </summary>
    public void RequestHeroRender(string path, int width, int height, bool transparentBackground = false)
    {
        _heroPath = path;
        _heroWidth = width;
        _heroHeight = height;
        _heroTransparentBackground = transparentBackground;
        _heroPending = true;
        if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
    }

    private const int PreviewWidth = 640;
    private const int PreviewHeight = 480;

    // The texture's current pixel dimensions. 3D modes render to the fixed
    // preview buffer (then upscale -- soft is fine for raymarching); deep-zoom
    // renders at native control resolution for crisp 1:1 detail. This tracks
    // whichever was last used so the blit can letterbox by the right aspect.
    private int _texW = PreviewWidth, _texH = PreviewHeight;

    // --- camera + input state ---
    private readonly FlyCamera _cam = new(
        position: new Vector3(4.0f, 3.0f, 4.0f),
        yaw: -MathF.PI / 4f,   // faces back toward the origin from (4,3,4)
        pitch: -0.53f);

    // Orbit camera state: derived from _cam position relative to the target.
    // SyncOrbitFromCamera() recomputes these after any WASD move.
    private Vector3 _orbitTarget = Vector3.Zero;
    private float _orbitAzimuth;
    private float _orbitElevation;
    private float _orbitDistance;

    // 2D deep-zoom "camera": high-precision center + radius (see DeepZoomView).
    private readonly DeepZoomView _deepView = new();

    /// <summary>Current deep-zoom formula (0 Mandelbrot, 1 Prospector, 2 Julia,
    /// 3 Burning Ship). Driven by the FORMULA dropdown in the main window.</summary>
    public int DeepFormula => _deepView.Formula;

    /// <summary>Switch the deep-zoom formula and reframe to its home view. The
    /// formula is a mode (not a keyframeable slider), so this lives outside the
    /// parameter schema; changing it reframes and triggers a redraw.</summary>
    public void SetDeepFormula(int formula)
    {
        if (formula == _deepView.Formula) return;
        _deepView.Formula = formula;
        _deepView.ApplyFormulaHome();
        MarkDirty();
    }

    private readonly HashSet<Key> _keysDown = new();
    private bool _looking;
    private Point _lastPointer;
    private DateTime _lastTick = DateTime.UtcNow;

    private DispatcherTimer? _moveTimer;

    // Deep-zoom adaptive resolution: while panning/zooming we render at a reduced
    // scale (targeting a known-interactive pixel budget) for responsiveness, then
    // render once at full native resolution after the interaction settles. The
    // move timer drives the settle check.
    private DateTime _lastDeepInteraction = DateTime.MinValue;
    // True while the user is actively panning/zooming the deep view -> the render
    // path draws low-res for responsiveness. Set by the interaction handlers and
    // cleared by the settle timer (OnMoveTick). Deliberately NOT recomputed from
    // wall-clock at render time: deep frames are slow, so a backlogged render
    // would see the stamp as already-stale and flip to the expensive native pass
    // mid-scroll, spiraling into a hang.
    private bool _deepInteracting;
    private const double DeepSettleMs = 500;
    // Interactive preview budget, in iteration*pixels. Held roughly constant and
    // spent on iterations FIRST (so the structure you're navigating into is
    // visible at depth, where it takes many thousands of iterations to escape),
    // then on resolution. ~ the old 640x480 x 3000 cost that scrolled smoothly.
    private const double DeepInteractiveCost = 640.0 * 480.0 * 3000.0;
    // Don't let the preview drop below this fraction of native per axis, even if
    // it means capping iterations -- a single block isn't navigable either.
    private const double DeepPreviewMinScale = 0.08;
    private int _deepPreviewIter = 1000;   // iteration count chosen for the last preview

    private const float BaseMoveSpeed = 1.5f;   // multiplied by DE-at-camera per second
    private const float LookSensitivity = 0.005f; // radians per pixel
    private const float RollSpeed = 1.2f;        // radians per second for Z/C bank

    public event Action<string>? StatusChanged;
    private void Status(string text) =>
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(text));

    // Runs the active fractal's low-res telemetry pass (null for fractals without one).
    private Parsec.Rendering.Metal.FractalGeometryStats? RunActiveTelemetryPass(
        Camera3D camera, RaymarchSettings settings) => ActiveType switch
    {
        FractalType.Mandelbox  when _metalRenderer?.IsAvailable == true
            => _metalRenderer.RunTelemetryPass(Mandelbox.ToParams(), camera, settings),
        FractalType.Mandelbulb when _metalMandelbulbRenderer?.IsAvailable == true
            => _metalMandelbulbRenderer.RunTelemetryPass(Mandelbulb.ToParams(), camera, settings),
        FractalType.Kleinian   when _metalKleinianRenderer?.IsAvailable == true
            => _metalKleinianRenderer.RunTelemetryPass(Kleinian.ToParams(), camera, settings),
        FractalType.BurningShip when _metalBurningShipRenderer?.IsAvailable == true
            => _metalBurningShipRenderer.RunTelemetryPass(BurningShip.ToParams(), camera, settings),
        FractalType.Menger     when _metalMengerRenderer?.IsAvailable == true
            => _metalMengerRenderer.RunTelemetryPass(Menger.ToParams(), camera, settings),
        FractalType.Apollonian when _metalApollonianRenderer?.IsAvailable == true
            => _metalApollonianRenderer.RunTelemetryPass(Apollonian.ToParams(), camera, settings),
        FractalType.Kifs       when _metalKifsRenderer?.IsAvailable == true
            => _metalKifsRenderer.RunTelemetryPass(Kifs.ToParams(), camera, settings),
        FractalType.QJBox      when _metalQJBoxRenderer?.IsAvailable == true
            => _metalQJBoxRenderer.RunTelemetryPass(QJBox.ToParams(), camera, settings),
        FractalType.RotBox     when _metalRotBoxRenderer?.IsAvailable == true
            => _metalRotBoxRenderer.RunTelemetryPass(RotBox.ToParams(), camera, settings),
        FractalType.QuaternionJulia when _metalQuaternionJuliaRenderer?.IsAvailable == true
            => _metalQuaternionJuliaRenderer.RunTelemetryPass(QuaternionJulia.ToParams(), camera, settings),
        _ => null,
    };

    /// <summary>
    /// Capture one deterministic sonic frame for offline export: runs the active
    /// fractal's telemetry pass with the current (already-applied) params and camera,
    /// then folds it through <paramref name="controller"/> at timeline time <paramref name="t"/>.
    /// Call from inside a RequestAnimationRender applyAtTime callback, after the
    /// timeline state has been applied for this frame.
    /// </summary>
    public Audio.Sonification.FractalSonicFrame CaptureSonicFrame(double t, SonificationController controller)
    {
        var camera = _cam.ToCamera(PreviewWidth, PreviewHeight);
        Parsec.Rendering.Metal.FractalGeometryStats? telemetry = null;
        try { telemetry = RunActiveTelemetryPass(camera, PreviewSettings()); }
        catch { /* telemetry is best-effort; frame falls back to camera-only fields */ }
        return controller.Update(t, _cam.Position, _cam.Forward, _cam.UpLocal, telemetry, ComputeGeometryPitches(), ComputeLatticeRatio());
    }

    // M7g: compute geometry-native pitch set for the current fractal type.
    // Returns null for fractals without a geometry-derived scale.
    private float[]? ComputeGeometryPitches() => ActiveType switch
    {
        FractalType.Apollonian => Audio.Sonification.GeometryScale.Apollonian(110f),
        FractalType.Kleinian   => Audio.Sonification.GeometryScale.Kleinian(55f, Kleinian.FixedRadius, Kleinian.MinRadius, Kleinian.Scale),
        _                      => null,
    };

    // DirectOrbit profiles: geometry-derived lattice generator (0 = use voice default).
    // Live parameter changes retune the 4×4 grid through these mappings.
    private float ComputeLatticeRatio() => ActiveType switch
    {
        FractalType.Kleinian   => Audio.Sonification.GeometryScale.KleinianLatticeRatio(Kleinian.FixedRadius, Kleinian.MinRadius, Kleinian.Scale),
        FractalType.Mandelbulb => Audio.Sonification.GeometryScale.MandelbulbLatticeRatio(Mandelbulb.Power),
        FractalType.Menger     => Audio.Sonification.GeometryScale.FoldScaleLatticeRatio(Menger.Scale),
        FractalType.Kifs       => Audio.Sonification.GeometryScale.FoldScaleLatticeRatio(Kifs.Scale),
        FractalType.QJBox      => Audio.Sonification.GeometryScale.FoldScaleLatticeRatio(QJBox.Scale),
        _                      => 0f,
    };

    private static string SonicDebugSuffix(Audio.Sonification.FractalSonicFrame? frame)
    {
        if (frame == null) return string.Empty;
        return $" · son: camSpd={frame.CameraSpeed:F2} pVel={frame.ParameterVelocity:F4}" +
               $" hit={frame.HitRatio:F2} depth={frame.MeanDepth:F1} steps={frame.StepMean:F0}" +
               $" nVar={frame.NormalVariance:F2} trap=({frame.TrapMean.X:F2},{frame.TrapMean.Y:F2},{frame.TrapMean.Z:F2},{frame.TrapMean.W:F2})";
    }

    public FractalView()
    {
        Focusable = true;
        IsHitTestVisible = true;
        // A timer integrates movement while keys are held and requests redraws.
        _moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _moveTimer.Tick += OnMoveTick;
        _moveTimer.Start();
        SyncOrbitFromCamera();
    }

    /// <summary>Recompute orbit fields from the current camera position and stored target.</summary>
    private void SyncOrbitFromCamera()
    {
        var d = _cam.Position - _orbitTarget;
        _orbitDistance = Math.Max(d.Length(), 0.01f);
        _orbitElevation = MathF.Asin(Math.Clamp(d.Y / _orbitDistance, -1f, 1f));
        _orbitAzimuth = MathF.Atan2(d.X, d.Z);
    }

    /// <summary>Update camera position and orientation from stored orbit params.</summary>
    private void ApplyOrbitToCamera()
    {
        float cosEl = MathF.Cos(_orbitElevation);
        _cam.Position = _orbitTarget + new Vector3(
            _orbitDistance * cosEl * MathF.Sin(_orbitAzimuth),
            _orbitDistance * MathF.Sin(_orbitElevation),
            _orbitDistance * cosEl * MathF.Cos(_orbitAzimuth));
        _cam.LookAt(_orbitTarget);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);

        if (OperatingSystem.IsMacOS())
        {
            Dispatcher.UIThread.Post(() =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_ready)
                    {
                        _softwareMode = true;
                        _dirty = true;
                        StartSoftwareRenderTimer();
                        SoftInvalidate();
                        if (Environment.GetEnvironmentVariable("PARSEC_DISABLE_METAL_PREVIEW") != "1")
                        {
                            InitMetalRenderers();
                            _dirty = true;
                            SoftInvalidate();
                        }
                    }
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
        }
    }

    private void StartSoftwareRenderTimer()
    {
        if (_softTimer != null)
        {
            _softTimer.Start();
            return;
        }

        _softTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _softTimer.Tick += (_, _) =>
        {
            if (_dirty) SoftInvalidate();
        };
        _softTimer.Start();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _softwareMode)
            SoftInvalidate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _softTimer?.Stop();
        _softTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void InitMetalRenderers()
    {
        if (_metalInitDone) return;
        _metalInitDone = true;
        try
        {
            _metalRenderer = new MetalMandelboxRenderer();
            _metalMandelbulbRenderer = new MetalMandelbulbRenderer();
            _metalRotBoxRenderer = new MetalRotBoxRenderer();
            _metalKifsRenderer = new MetalKifsRenderer();
            _metalKleinianRenderer = new MetalKleinianRenderer();
            _metalHybridRenderer = new MetalHybridRenderer();
            _metalBurningShipRenderer = new MetalBurningShipRenderer();
            _metalMengerRenderer = new MetalMengerRenderer();
            _metalQuaternionJuliaRenderer = new MetalQuaternionJuliaRenderer();
            _metalQJBoxRenderer = new MetalQJBoxRenderer();
            _metalApollonianRenderer = new MetalApollonianRenderer();
            _metalBicomplexRenderer = new MetalBicomplexRenderer();
            _metalPhoenixRenderer = new MetalPhoenixRenderer();
            _metalBiomorphRenderer = new MetalBiomorphRenderer();
            _metalMoselyRenderer = new MetalMoselyRenderer();
            _metalPK4DRenderer = new MetalPseudoKleinian4DRenderer();
            _metalRiemannSphereRenderer = new MetalRiemannSphereRenderer();
            _metalMandalayRenderer = new MetalMandalayRenderer();
            _metalAnisotropicRenderer = new MetalAnisotropicRenderer();
            _metalOrbitHybridRenderer = new MetalOrbitHybridRenderer();
            _metalDeepZoomRenderer = new MetalDeepZoomRenderer();
            _metalAttractorRenderer = new MetalAttractorRenderer();
            Status("Metal renderers ready (software blit)");
        }
        catch (Exception ex)
        {
            Status($"Metal init failed: {ex.Message}");
        }
    }

    // OpenGlControlBase derives from Control, which has no Background and so is
    // invisible to pointer hit-testing (it renders via a GPU surface with no
    // hit-testable content). Implementing ICustomHitTest claims our full bounds
    // for hit-testing, so pointer events actually reach us. Without this,
    // keyboard works (focus is separate) but mouse events never arrive.
    public bool HitTest(Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height;

    // ----------------------------------------------------------------- input
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        bool left = props.IsLeftButtonPressed
                    || props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        if (left)
        {
            _looking = true;
            _lastPointer = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_looking) return;
        var pos = e.GetPosition(this);
        var dx = (float)(pos.X - _lastPointer.X);
        var dy = (float)(pos.Y - _lastPointer.Y);
        _lastPointer = pos;
        if (ActiveType == FractalType.DeepZoom)
        {
            // Drag-pan the complex plane. Height is the displayed control height,
            // so the pan tracks the cursor as a fraction of the view regardless of
            // the fixed preview resolution.
            _deepView.PanPixels(dx, dy, Math.Max(1, (int)Bounds.Height));
            _lastDeepInteraction = DateTime.UtcNow;
            _deepInteracting = true;
            _dirty = true;
            if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
            return;
        }
        // Drag right -> look right (yaw+); drag up -> look up (pitch+).
        _cam.Look(dx * LookSensitivity, -dy * LookSensitivity);
        _dirty = true;
        if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_looking)
        {
            _looking = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ActiveType != FractalType.DeepZoom) return;
        // Stepped zoom toward the cursor: each notch scales the radius and shifts
        // the center so the point under the pointer stays put. A radius change
        // triggers a reference-orbit recompute in the pipeline (per the design).
        var pos = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 0.8 : 1.25;   // up = zoom in
        _deepView.ZoomTowardPixel(factor, pos.X, pos.Y,
            Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        _lastDeepInteraction = DateTime.UtcNow;
        _deepInteracting = true;
        _dirty = true;
        if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _keysDown.Add(e.Key);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keysDown.Remove(e.Key);
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        if (ActiveType == FractalType.DeepZoom)
        {
            // Once panning/zooming has paused, upgrade the last low-res frame to a
            // full native render (one shot -- clearing _deepInteracting guards repeats).
            if (_deepInteracting &&
                (DateTime.UtcNow - _lastDeepInteraction).TotalMilliseconds >= DeepSettleMs)
            {
                _deepInteracting = false;   // settled -> next render is the crisp native pass
                _dirty = true;
                if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
            }
            return;   // 2D: mouse pan/zoom, no WASD fly
        }

        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0f || dt > 0.1f) dt = 0.016f; // clamp hitches

        Vector3 local = Vector3.Zero;
        if (_keysDown.Contains(Key.W)) local.Z += 1;
        if (_keysDown.Contains(Key.S)) local.Z -= 1;
        if (_keysDown.Contains(Key.D)) local.X += 1;
        if (_keysDown.Contains(Key.A)) local.X -= 1;
        if (_keysDown.Contains(Key.E)) local.Y += 1;
        if (_keysDown.Contains(Key.Q)) local.Y -= 1;

        // Roll / bank about the forward axis (Z/C). Independent of translation,
        // so it works while stationary too.
        float roll = 0f;
        if (_keysDown.Contains(Key.Z)) roll -= 1f;   // bank counter-clockwise
        if (_keysDown.Contains(Key.C)) roll += 1f;   // bank clockwise

        if (local == Vector3.Zero && roll == 0f) return;

        if (roll != 0f)
            _cam.RollBy(roll * RollSpeed * dt);

        if (local != Vector3.Zero)
        {
            local = Vector3.Normalize(local);

            // Speed scales with distance to the surface: glide slowly near detail,
            // travel fast in open space. Clamp so we never freeze or rocket.
            float de = ActiveType switch
            {
                FractalType.Kleinian => KleinianDE.Estimate(_cam.Position, Kleinian.ToParams()),
                FractalType.Kifs => KifsDE.Estimate(_cam.Position, Kifs.ToParams()),
                // The attractor has no cheap closed-form DE (it lives in the hash),
                // and is viewed from outside, so a steady mid-speed glide is fine.
                FractalType.Attractor => 1.0f,
                FractalType.Mandelbulb => MandelbulbDE.Estimate(_cam.Position, Mandelbulb.ToParams()),
                FractalType.QuaternionJulia => QuaternionJuliaDE.Estimate(_cam.Position, QuaternionJulia.ToParams()),
                FractalType.RotBox => RotBoxDE.Estimate(_cam.Position, RotBox.ToParams()),
                FractalType.Hybrid => HybridDE.Estimate(_cam.Position, Hybrid.ToParams()),
                FractalType.QJBox => QJBoxDE.Estimate(_cam.Position, QJBox.ToParams()),
                FractalType.Menger => MengerDE.Estimate(_cam.Position, Menger.ToParams()),
                FractalType.Bicomplex => BicomplexDE.Estimate(_cam.Position, Bicomplex.ToParams()),
                FractalType.Apollonian => 1.0f,
                // Phoenix: same situation as Apollonian -- numerical-gradient DE only,
                // no cheap closed-form CPU DE. Could port one later if needed.
                FractalType.Phoenix => 1.0f,
                // Biomorph: same Mandelbulb-style scalar-derivative DE as Phoenix,
                // no closed-form CPU DE port done.
                FractalType.Biomorph => 1.0f,
                // Mosely: exact GPU DE, but no CPU port yet -> steady glide.
                FractalType.Mosely => 1.0f,
                FractalType.PseudoKleinian4D => 1.0f,
            FractalType.RiemannSphere => 1.0f,
            FractalType.Mandalay => 1.0f,
            FractalType.Anisotropic => 1.0f,
            FractalType.OrbitHybrid => 1.0f,
            FractalType.BurningShip => 1.0f,
                FractalType.Mandelbox => MandelboxDE.Estimate(_cam.Position, Mandelbox.ToParams()),
                _ => MandelboxDE.Estimate(_cam.Position, Fractal.ToParams()),
            };
            float speed = BaseMoveSpeed * Math.Clamp(de, 0.02f, 4.0f);
            _cam.Move(local, speed * dt);
            SyncOrbitFromCamera();
        }

        _dirty = true;
        if (_softwareMode) SoftInvalidate(); else RequestNextFrameRendering();
    }

    // ------------------------------------------------------------------- GL
    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new Gl(gl.GetProcAddress);
            Status($"GL {_gl.GetString(GlConst.Version)} | {_gl.GetString(GlConst.Renderer)}");

            // The GL compute pipeline (OpenGL 4.3) is unavailable on macOS, which
            // caps at 4.1. On macOS all 3D rendering goes through the Metal path.
            if (!OperatingSystem.IsMacOS())
            {
                _pipeline = new RaymarchPipeline(_gl);
                _boxRenderer = new GpuMandelboxRenderer(_gl, _pipeline);
                _kifsRenderer = new GpuKifsRenderer(_gl, _pipeline);
                _kleinianRenderer = new GpuKleinianRenderer(_gl, _pipeline);
                _attractorRenderer = new GpuAttractorRenderer(_gl, _pipeline);
                _mandelbulbRenderer = new GpuMandelbulbRenderer(_gl, _pipeline);
                _qjuliaRenderer = new GpuQuaternionJuliaRenderer(_gl, _pipeline);
                _rotboxRenderer = new GpuRotBoxRenderer(_gl, _pipeline);
                _hybridRenderer = new GpuHybridRenderer(_gl, _pipeline);
                _qjboxRenderer = new GpuQJBoxRenderer(_gl, _pipeline);
                _mengerRenderer = new GpuMengerRenderer(_gl, _pipeline);
                _bicomplexRenderer = new GpuBicomplexRenderer(_gl, _pipeline);
                _apollonianRenderer = new GpuApollonianRenderer(_gl, _pipeline);
                _phoenixRenderer = new GpuPhoenixRenderer(_gl, _pipeline);
                _biomorphRenderer = new GpuBiomorphRenderer(_gl, _pipeline);
                _moselyRenderer = new GpuMoselyRenderer(_gl, _pipeline);
                _pk4dRenderer = new GpuPseudoKleinian4DRenderer(_gl, _pipeline);
                _riemannRenderer = new GpuRiemannSphereRenderer(_gl, _pipeline);
                _mandalayRenderer = new GpuMandalayRenderer(_gl, _pipeline);
                _anisoRenderer = new GpuAnisotropicRenderer(_gl, _pipeline);
                _orbitHybridRenderer = new GpuOrbitHybridRenderer(_gl, _pipeline);
                _burningShipRenderer = new GpuBurningShipRenderer(_gl, _pipeline);
                _deepPipeline = new DeepZoomPipeline(_gl);
            }

            if (OperatingSystem.IsMacOS())
            {
                InitMetalRenderers();
            }

            _texture = _gl.GenTexture();
            _gl.BindTexture(GlConst.Texture2D, _texture);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMinFilter, (int)GlConst.Linear);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMagFilter, (int)GlConst.Linear);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapS, (int)GlConst.ClampToEdge);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapT, (int)GlConst.ClampToEdge);
            _gl.BindTexture(GlConst.Texture2D, 0);

            _vao = _gl.GenVertexArray();
            _blitProgram = _gl.CreateGraphicsProgram(BlitVertexSrc, BlitFragmentSrc);
            _samplerLocation = _gl.GetUniformLocation(_blitProgram, "uTex");
            _ready = true;
        }
        catch (Exception ex)
        {
            _ready = false;
            Status($"init failed: {ex.Message}");
        }
    }

    public override void Render(AvDrawingContext context)
    {
        if (!_softwareMode)
        {
            if (!OperatingSystem.IsMacOS())
                base.Render(context);
            return;
        }

        // Hero render in software mode.
        if (_heroPending && _heroPath != null)
        {
            try
            {
                var bmp = RenderActiveTo(_heroWidth, _heroHeight);
                Parsec.Rendering.Output.ImageOutput.SavePng(bmp, _heroPath, _heroTransparentBackground);
                bmp.Dispose();
                string savedTo = _heroPath;
                Dispatcher.UIThread.Post(() =>
                    HeroRenderComplete?.Invoke($"Saved {_heroWidth}x{_heroHeight} render to {savedTo}"));
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.UIThread.Post(() =>
                    HeroRenderComplete?.Invoke($"Hero render failed: {msg}"));
            }
            finally { _heroPending = false; _heroPath = null; _heroTransparentBackground = false; _dirty = true; }
        }

        // Animation batch render in software mode.
        if (_animPending && _animDir != null && _animApplyAtTime != null)
        {
            int total = _animFrameCount;
            string dir = _animDir;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                for (int frame = 0; frame < total; frame++)
                {
                    double t = frame / _animFps;
                    _animApplyAtTime(t);
                    using var bmp = RenderActiveTo(_animWidth, _animHeight);
                    string path = System.IO.Path.Combine(dir, $"frame_{frame:D5}.png");
                    Parsec.Rendering.Output.ImageOutput.SavePng(bmp, path, _animTransparentBackground);
                    int done = frame + 1;
                    if (done == total || done % 5 == 0)
                        Dispatcher.UIThread.Post(() => AnimationProgress?.Invoke(done, total));
                }
                Dispatcher.UIThread.Post(() =>
                    AnimationRenderComplete?.Invoke($"Rendered {total} frames to {dir}"));
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.UIThread.Post(() =>
                    AnimationRenderComplete?.Invoke($"Animation render failed: {msg}"));
            }
            finally { _animPending = false; _animDir = null; _animApplyAtTime = null; _animTransparentBackground = false; _dirty = true; }
        }

        if (_dirty)
        {
            if (Bounds.Width < 1 || Bounds.Height < 1)
            {
                SoftInvalidate();
                return;
            }

            if (_domainWarpEnabled)
                DomainWarpState.SetPhase((float)_sonicClock.Elapsed.TotalSeconds * 0.25f);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var camera = _cam.ToCamera(PreviewWidth, PreviewHeight);
            int rw = PreviewWidth, rh = PreviewHeight;
            uint[] pixels;
            bool renderedPreview = HasMetalPreviewRenderer();
            try
            {
                pixels = RenderActivePreviewPixels(camera, rw, rh);
            }
            catch (Exception ex)
            {
                pixels = SolidPixels(rw, rh, new Color(0.02f, 0.03f, 0.07f));
                renderedPreview = false;
                Status($"Preview failed: {ex.Message}");
            }
            _totalFrameMs = sw.ElapsedMilliseconds;

            // Update texture from live source (Feedback / MandelbrotZoom / Video).
            if (_textureSource != SurfaceTextureSource.None && SupportsSurfaceTexture
                && OperatingSystem.IsMacOS() && renderedPreview)
            {
                switch (_textureSource)
                {
                    case SurfaceTextureSource.Feedback:
                        UpdateFeedbackTexture(pixels, rw, rh);
                        MarkDirty();
                        break;
                    case SurfaceTextureSource.MandelbrotZoom:
                        UpdateMandelbrotZoomTexture();
                        MarkDirty();
                        break;
                    case SurfaceTextureSource.Video:
                        UpdateVideoTexture();
                        MarkDirty();
                        break;
                }
            }
            _texW = rw; _texH = rh;
            _dirty = false;

            if (_domainWarpEnabled)
                MarkDirty();

            if (_softBitmap == null
                || _softBitmap.PixelSize.Width != rw
                || _softBitmap.PixelSize.Height != rh)
            {
                _softBitmap = new WriteableBitmap(
                    new PixelSize(rw, rh),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Rgba8888,
                    AlphaFormat.Opaque);
            }

            using (var fb = _softBitmap.Lock())
            {
                unsafe
                {
                    fixed (uint* src = pixels)
                    {
                        byte* srcBytes = (byte*)src;
                        byte* dstBytes = (byte*)fb.Address;
                        int srcRowBytes = rw * 4;
                        for (int y = 0; y < rh; y++)
                        {
                            Buffer.MemoryCopy(
                                srcBytes + y * srcRowBytes,
                                dstBytes + y * fb.RowBytes,
                                fb.RowBytes,
                                srcRowBytes);
                        }
                    }
                }
            }

            if (renderedPreview)
            {
                // Run telemetry pass for supported fractals (~30 Hz, best-effort)
                if (Sonification != null && _telemetryThrottle.ElapsedMilliseconds >= 33)
                {
                    _telemetryThrottle.Restart();
                    var settings = PreviewSettings();
                    try { _lastTelemetry = RunActiveTelemetryPass(camera, settings); }
                    catch { _lastTelemetry = null; }
                }

                var sonicFrame = Sonification?.Update(_sonicClock.Elapsed.TotalSeconds, _cam.Position, _cam.Forward, _cam.UpLocal, _lastTelemetry, ComputeGeometryPitches(), ComputeLatticeRatio());
                EmitMidi(sonicFrame, pixels, rw, rh);
                Status($"Metal {ActiveType} · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{SonicDebugSuffix(sonicFrame)}{MidiSuffix()}");
            }
        }

        if (_softBitmap != null)
        {
            var size = Bounds.Size;
            float previewAspect = (float)_texW / _texH;
            float viewAspect = (float)(size.Width / size.Height);
            double vpW, vpH, vpX, vpY;
            if (viewAspect > previewAspect)
            {
                vpH = size.Height;
                vpW = size.Height * previewAspect;
                vpX = (size.Width - vpW) / 2;
                vpY = 0;
            }
            else
            {
                vpW = size.Width;
                vpH = size.Width / previewAspect;
                vpX = 0;
                vpY = (size.Height - vpH) / 2;
            }
            context.DrawImage(_softBitmap,
                new Rect(0, 0, _texW, _texH),
                new Rect(vpX, vpY, vpW, vpH));
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        bool notReady = !_ready || _gl == null
            || (OperatingSystem.IsMacOS()
                ? _metalRenderer == null && _metalMandelbulbRenderer == null && _metalRotBoxRenderer == null && _metalKifsRenderer == null && _metalKleinianRenderer == null && _metalHybridRenderer == null
                : _pipeline == null || _boxRenderer == null || _kifsRenderer == null || _kleinianRenderer == null || _attractorRenderer == null || _mandelbulbRenderer == null || _qjuliaRenderer == null || _rotboxRenderer == null || _hybridRenderer == null || _qjboxRenderer == null || _mengerRenderer == null || _bicomplexRenderer == null || _apollonianRenderer == null || _phoenixRenderer == null || _biomorphRenderer == null || _moselyRenderer == null || _pk4dRenderer == null || _riemannRenderer == null || _mandalayRenderer == null || _anisoRenderer == null || _orbitHybridRenderer == null || _burningShipRenderer == null || _deepPipeline == null);
        if (notReady)
        {
            _gl?.BindFramebuffer(GlConst.Framebuffer, (uint)fb);
            _gl?.ClearColor(0.1f, 0.1f, 0.1f, 1f);
            _gl?.Clear(GlConst.ColorBufferBit);
            return;
        }

        // Hero render: the GL context is current here, so do the high-res tiled
        // render now, save it, and fall through to a normal preview render so the
        // on-screen view (and the preview-size buffers) are restored afterward.
        if (_heroPending && _heroPath != null)
        {
            try
            {
                SkiaSharp.SKBitmap bmp = RenderActiveTo(_heroWidth, _heroHeight);
                Parsec.Rendering.Output.ImageOutput.SavePng(bmp, _heroPath, _heroTransparentBackground);
                bmp.Dispose();

                string savedTo = _heroPath;
                Dispatcher.UIThread.Post(() =>
                    HeroRenderComplete?.Invoke($"Saved {_heroWidth}x{_heroHeight} render to {savedTo}"));
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.UIThread.Post(() =>
                    HeroRenderComplete?.Invoke($"Hero render failed: {msg}"));
            }
            finally
            {
                _heroPending = false;
                _heroPath = null;
                _heroTransparentBackground = false;
                _dirty = true;   // restore the preview-size view next
            }
        }

        // Animation batch render: run the whole frame loop here (GL context is
        // current). Synchronous -- the UI freezes until done (fine for a test).
        if (_animPending && _animDir != null && _animApplyAtTime != null)
        {
            int total = _animFrameCount;
            string dir = _animDir;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                for (int frame = 0; frame < total; frame++)
                {
                    double t = frame / _animFps;
                    _animApplyAtTime(t);                       // set live params for this time
                    using var bmp = RenderActiveTo(_animWidth, _animHeight);
                    string path = System.IO.Path.Combine(dir, $"frame_{frame:D5}.png");
                    Parsec.Rendering.Output.ImageOutput.SavePng(bmp, path, _animTransparentBackground);

                    int done = frame + 1;
                    if (done == total || done % 5 == 0)
                        Dispatcher.UIThread.Post(() => AnimationProgress?.Invoke(done, total));
                }
                Dispatcher.UIThread.Post(() =>
                    AnimationRenderComplete?.Invoke($"Rendered {total} frames to {dir}"));
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.UIThread.Post(() =>
                    AnimationRenderComplete?.Invoke($"Animation render failed: {msg}"));
            }
            finally
            {
                _animPending = false;
                _animDir = null;
                _animApplyAtTime = null;
                _animTransparentBackground = false;
                _dirty = true;
            }
        }

        // Attractor (re)generation: the expensive integrate + hash + SSBO upload.
        // Only runs when generation params changed (the "Generate" action), never
        // per frame. The GL context is current here, which SetAttractor needs.
        if (ActiveType == FractalType.Attractor && _attractorNeedsRegen)
        {
            try
            {
                var ap = Attractor.ToParams();
                var traj = Parsec.Core.Attractors.ThomasAttractor.Generate(ap);
                _attractorHash = Parsec.Core.Attractors.AttractorHash.Build(traj, gridSize: 96);
                _attractorRenderer?.SetAttractor(_attractorHash);
                _metalAttractorRenderer?.SetAttractor(_attractorHash);

                // Frame the camera to the cloud bounds on (re)generation so the
                // new shape is in view; the user can then fly around freely.
                var lo = _attractorHash.BoundsMin;
                var hi = _attractorHash.BoundsMax;
                var center = (lo + hi) * 0.5f;
                float span = (hi - lo).Length();
                _cam.Position = center + new Vector3(0.2f, 0.35f, 1.0f) * span * 0.8f;
                _cam.LookAt(center);

                _attractorNeedsRegen = false;
                _dirty = true;
                Status($"Generated attractor: {traj.Count} points");
            }
            catch (Exception ex)
            {
                _attractorNeedsRegen = false;
                Status($"Attractor generate failed: {ex.Message}");
            }
        }

        if (_dirty && !(ActiveType == FractalType.Attractor && _attractorHash == null))
        {
            var totalFrameSw = System.Diagnostics.Stopwatch.StartNew();
            var camera = _cam.ToCamera(PreviewWidth, PreviewHeight);
            // 3D raymarch previews use the fixed preview buffer; deep-zoom renders
            // at native resolution so the 2D filigree stays crisp at 1:1 -- but
            // while interacting it renders at a reduced scale for responsiveness
            // and snaps to native once settled (see OnMoveTick).
            int rw = PreviewWidth, rh = PreviewHeight;
            if (ActiveType == FractalType.DeepZoom)
            {
                var (nw, nh) = GetPixelSize();
                // Resolution follows the interaction FLAG, not the wall-clock: a
                // render that ran late must still honor the scroll that requested
                // it as low-res, or it spirals into back-to-back native renders.
                if (_deepInteracting)
                {
                    // Spend a fixed compute budget on iterations first, then on
                    // resolution. At depth the structure needs many thousands of
                    // iterations to appear, so try the full depth-scaled count and
                    // let resolution shrink to fit the budget; only if that would
                    // drop below the floor do we pin resolution and cap iterations.
                    int effIter = _deepView.IterationsForDepth();
                    double native = Math.Max(1, (double)nw * nh);
                    _deepPreviewIter = effIter;
                    double s = Math.Sqrt((DeepInteractiveCost / _deepPreviewIter) / native);
                    if (s < DeepPreviewMinScale)
                    {
                        s = DeepPreviewMinScale;
                        _deepPreviewIter = Math.Max(1000,
                            (int)(DeepInteractiveCost / (native * s * s)));
                    }
                    s = Math.Min(s, 1.0);
                    rw = Math.Max(1, (int)(nw * s));
                    rh = Math.Max(1, (int)(nh * s));
                }
                else
                {
                    rw = nw; rh = nh;
                }
            }
            uint[] pixels = RenderActivePreviewPixels(camera, rw, rh);

            _gl.BindTexture(GlConst.Texture2D, _texture);
            var texUploadSw = System.Diagnostics.Stopwatch.StartNew();
            unsafe
            {
                fixed (uint* p = pixels)
                {
                    _gl.TexImage2D(GlConst.Texture2D, 0, (int)GlConst.Rgba8,
                        rw, rh, 0,
                        GlConst.Rgba, GlConst.UnsignedByte, (IntPtr)p);
                }
            }
            _texUploadMs = texUploadSw.ElapsedMilliseconds;
            _totalFrameMs = totalFrameSw.ElapsedMilliseconds;
            _texW = rw; _texH = rh;
            _dirty = false;
            bool atMaxDepth = ActiveType == FractalType.DeepZoom
                && _deepView.Radius <= DeepZoomView.MinRadius * 1.05;
            {
                var sonicFrame = Sonification?.Update(_sonicClock.Elapsed.TotalSeconds, _cam.Position, _cam.Forward, _cam.UpLocal, _lastTelemetry, ComputeGeometryPitches(), ComputeLatticeRatio());
                EmitMidi(sonicFrame, pixels, rw, rh);
                string sonicSuffix = SonicDebugSuffix(sonicFrame);
                Status(ActiveType == FractalType.DeepZoom
                    ? $"Deep Zoom 2D · {(_deepView.Formula switch { 1 => "Prospector", 2 => "Julia", 3 => "Burning Ship", _ => "Mandelbrot" })} · radius {_deepView.Radius:e2}{(atMaxDepth ? " · max depth" : "")} · {rw}x{rh} · drag pan · scroll zoom"
                    : ActiveType == FractalType.Mandelbox && _metalRenderer?.IsAvailable == true
                    ? $"Metal Mandelbox · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : ActiveType == FractalType.Mandelbulb && _metalMandelbulbRenderer?.IsAvailable == true
                    ? $"Metal Mandelbulb · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : ActiveType == FractalType.RotBox && _metalRotBoxRenderer?.IsAvailable == true
                    ? $"Metal RotBox · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : ActiveType == FractalType.Kifs && _metalKifsRenderer?.IsAvailable == true
                    ? $"Metal KIFS · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : ActiveType == FractalType.Kleinian && _metalKleinianRenderer?.IsAvailable == true
                    ? $"Metal Kleinian · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : ActiveType == FractalType.Hybrid && _metalHybridRenderer?.IsAvailable == true
                    ? $"Metal Hybrid · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : OperatingSystem.IsMacOS() && _metalBurningShipRenderer?.IsAvailable == true && (
                        ActiveType == FractalType.BurningShip || ActiveType == FractalType.QuaternionJulia ||
                        ActiveType == FractalType.QJBox || ActiveType == FractalType.Menger ||
                        ActiveType == FractalType.Bicomplex || ActiveType == FractalType.Apollonian ||
                        ActiveType == FractalType.Phoenix || ActiveType == FractalType.Biomorph ||
                        ActiveType == FractalType.Mosely || ActiveType == FractalType.PseudoKleinian4D ||
                        ActiveType == FractalType.RiemannSphere || ActiveType == FractalType.Mandalay ||
                        ActiveType == FractalType.Anisotropic || ActiveType == FractalType.OrbitHybrid ||
                        ActiveType == FractalType.AmazingBox)
                    ? $"Metal {ActiveType} · {rw}x{rh} · compute {_metalComputeMs} ms · readback {_metalReadbackMs} ms · upload {_texUploadMs} ms · total {_totalFrameMs} ms  ·  WASD+QE move · drag to look{sonicSuffix}"
                    : $"pos ({_cam.Position.X:F2}, {_cam.Position.Y:F2}, {_cam.Position.Z:F2})  ·  WASD+QE move · drag to look{sonicSuffix}");
            }
        }

        _gl.BindFramebuffer(GlConst.Framebuffer, (uint)fb);
        var size = GetPixelSize();
        // Clear the whole control to the surround color first.
        _gl.Viewport(0, 0, size.w, size.h);
        _gl.ClearColor(0.02f, 0.03f, 0.07f, 1f);  // match render bg (dark blue)
        _gl.Clear(GlConst.ColorBufferBit);

        // Letterbox: fit the preview's aspect ratio inside the control, centered,
        // so the fractal isn't stretched when the view area isn't 4:3.
        float previewAspect = (float)_texW / _texH;
        float viewAspect = (float)size.w / size.h;
        int vpW, vpH, vpX, vpY;
        if (viewAspect > previewAspect)
        {
            // Control is wider than preview: bars on left/right.
            vpH = size.h;
            vpW = (int)(size.h * previewAspect);
            vpX = (size.w - vpW) / 2;
            vpY = 0;
        }
        else
        {
            // Control is taller than preview: bars on top/bottom.
            vpW = size.w;
            vpH = (int)(size.w / previewAspect);
            vpX = 0;
            vpY = (size.h - vpH) / 2;
        }
        _gl.Viewport(vpX, vpY, vpW, vpH);

        _gl.UseProgram(_blitProgram);
        _gl.ActiveTexture(GlConst.Texture0);
        _gl.BindTexture(GlConst.Texture2D, _texture);
        _gl.Uniform1i(_samplerLocation, 0);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(GlConst.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _moveTimer?.Stop();
        _moveTimer = null;
        _softTimer?.Stop();
        _softTimer = null;
        if (_gl == null) return;
        if (_texture != 0) _gl.DeleteTexture(_texture);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_blitProgram != 0) _gl.DeleteProgram(_blitProgram);
        _boxRenderer?.Dispose();
        _kifsRenderer?.Dispose();
        _kleinianRenderer?.Dispose();
        _attractorRenderer?.Dispose();
        _mandelbulbRenderer?.Dispose();
        _qjuliaRenderer?.Dispose();
        _rotboxRenderer?.Dispose();
        _hybridRenderer?.Dispose();
        _qjboxRenderer?.Dispose();
        _mengerRenderer?.Dispose();
        _bicomplexRenderer?.Dispose();
        _apollonianRenderer?.Dispose();
        _phoenixRenderer?.Dispose();
        _biomorphRenderer?.Dispose();
        _moselyRenderer?.Dispose();
        _pk4dRenderer?.Dispose();
        _riemannRenderer?.Dispose();
        _mandalayRenderer?.Dispose();
        _anisoRenderer?.Dispose();
        _orbitHybridRenderer?.Dispose();
        _burningShipRenderer?.Dispose();
        _deepPipeline?.Dispose();
        _pipeline?.Dispose();
        _metalRenderer?.Dispose();
        _metalMandelbulbRenderer?.Dispose();
        _metalRotBoxRenderer?.Dispose();
        _metalKifsRenderer?.Dispose();
        _metalKleinianRenderer?.Dispose();
        _metalHybridRenderer?.Dispose();
        _metalBurningShipRenderer?.Dispose();
        _metalMengerRenderer?.Dispose();
        _metalQuaternionJuliaRenderer?.Dispose();
        _metalQJBoxRenderer?.Dispose();
        _metalApollonianRenderer?.Dispose();
        _metalBicomplexRenderer?.Dispose();
        _metalPhoenixRenderer?.Dispose();
        _metalBiomorphRenderer?.Dispose();
        _metalMoselyRenderer?.Dispose();
        _metalPK4DRenderer?.Dispose();
        _metalRiemannSphereRenderer?.Dispose();
        _metalMandalayRenderer?.Dispose();
        _metalAnisotropicRenderer?.Dispose();
        _metalOrbitHybridRenderer?.Dispose();
        _metalDeepZoomRenderer?.Dispose();
        _texture = _vao = _blitProgram = 0;
        _boxRenderer = null;
        _metalRenderer = null;
        _metalMandelbulbRenderer = null;
        _metalRotBoxRenderer = null;
        _metalKifsRenderer = null;
        _metalKleinianRenderer = null;
        _metalHybridRenderer = null;
        _metalBurningShipRenderer = null;
        _metalMengerRenderer = null;
        _metalQuaternionJuliaRenderer = null;
        _metalQJBoxRenderer = null;
        _metalApollonianRenderer = null;
        _metalBicomplexRenderer = null;
        _metalPhoenixRenderer = null;
        _metalBiomorphRenderer = null;
        _metalMoselyRenderer = null;
        _metalPK4DRenderer = null;
        _metalRiemannSphereRenderer = null;
        _metalMandalayRenderer = null;
        _metalAnisotropicRenderer = null;
        _metalOrbitHybridRenderer = null;
        _metalDeepZoomRenderer = null;
        _kifsRenderer = null;
        _kleinianRenderer = null;
        _attractorRenderer = null;
        _mandelbulbRenderer = null;
        _qjuliaRenderer = null;
        _rotboxRenderer = null;
        _hybridRenderer = null;
        _qjboxRenderer = null;
        _mengerRenderer = null;
        _bicomplexRenderer = null;
        _apollonianRenderer = null;
        _phoenixRenderer = null;
        _biomorphRenderer = null;
        _moselyRenderer = null;
        _pk4dRenderer = null;
        _riemannRenderer = null;
        _mandalayRenderer = null;
        _anisoRenderer = null;
        _orbitHybridRenderer = null;
        _burningShipRenderer = null;
        _deepPipeline = null;
        _pipeline = null;
        _gl = null;
        _ready = false;
    }

    private (int w, int h) GetPixelSize()
    {
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling));
        int h = Math.Max(1, (int)(Bounds.Height * scaling));
        return (w, h);
    }

    private uint[] RenderActivePreviewPixels(Camera3D camera, int rw, int rh)
    {
        if (OperatingSystem.IsMacOS() && !HasMetalPreviewRenderer())
        {
            Status($"{ActiveType} preview unavailable on macOS without an OpenGL compute context");
            return SolidPixels(rw, rh, new Color(0.02f, 0.03f, 0.07f));
        }

        return ActiveType switch
    {
        FractalType.DeepZoom when _metalDeepZoomRenderer?.IsAvailable == true =>
            RenderWithMetalDeepZoom(rw, rh),
        FractalType.DeepZoom when _deepPipeline != null => _deepPipeline.Render(_deepView,
            rw, rh, Palette.ToParams(),
            new Color(0.02f, 0.03f, 0.07f), heroSamples: 1, tileRows: 64,
            interactive: _deepInteracting, interactiveIter: _deepPreviewIter),
        FractalType.DeepZoom => new uint[rw * rh],
        FractalType.Mandelbulb when _metalMandelbulbRenderer?.IsAvailable == true =>
            RenderWithMetalMandelbulb(camera, rw, rh),
        FractalType.Mandelbulb => _mandelbulbRenderer!.RenderToBuffer(Mandelbulb.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(210, 175, 140),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.BurningShip when _metalBurningShipRenderer?.IsAvailable == true =>
            RenderWithMetalBurningShip(camera, rw, rh),
        FractalType.BurningShip => _burningShipRenderer!.RenderToBuffer(BurningShip.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(225, 140, 90),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.QuaternionJulia when _metalQuaternionJuliaRenderer?.IsAvailable == true =>
            RenderWithMetalQuaternionJulia(camera, rw, rh),
        FractalType.QuaternionJulia => _qjuliaRenderer!.RenderToBuffer(QuaternionJulia.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(210, 180, 150),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.RotBox when _metalRotBoxRenderer?.IsAvailable == true =>
            RenderWithMetalRotBox(camera, rw, rh),
        FractalType.RotBox => _rotboxRenderer!.RenderToBuffer(RotBox.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(190, 175, 155),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Hybrid when _metalHybridRenderer?.IsAvailable == true =>
            RenderWithMetalHybrid(camera, rw, rh),
        FractalType.Hybrid => _hybridRenderer!.RenderToBuffer(Hybrid.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(190, 170, 145),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.QJBox when _metalQJBoxRenderer?.IsAvailable == true =>
            RenderWithMetalQJBox(camera, rw, rh),
        FractalType.QJBox => _qjboxRenderer!.RenderToBuffer(QJBox.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(195, 170, 145),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Menger when _metalMengerRenderer?.IsAvailable == true =>
            RenderWithMetalMenger(camera, rw, rh),
        FractalType.Menger => _mengerRenderer!.RenderToBuffer(Menger.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(195, 170, 145),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Bicomplex when _metalBicomplexRenderer?.IsAvailable == true =>
            RenderWithMetalBicomplex(camera, rw, rh),
        FractalType.Bicomplex => _bicomplexRenderer!.RenderToBuffer(Bicomplex.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(200, 175, 150),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Apollonian when _metalApollonianRenderer?.IsAvailable == true =>
            RenderWithMetalApollonian(camera, rw, rh),
        FractalType.Apollonian => _apollonianRenderer!.RenderToBuffer(Apollonian.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(200, 175, 150),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Phoenix when _metalPhoenixRenderer?.IsAvailable == true =>
            RenderWithMetalPhoenix(camera, rw, rh),
        FractalType.Phoenix => _phoenixRenderer!.RenderToBuffer(Phoenix.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(210, 180, 150),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Biomorph when _metalBiomorphRenderer?.IsAvailable == true =>
            RenderWithMetalBiomorph(camera, rw, rh),
        FractalType.Biomorph => _biomorphRenderer!.RenderToBuffer(Biomorph.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(220, 180, 140),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Attractor when _metalAttractorRenderer?.IsAvailable == true && _attractorHash != null =>
            RenderWithMetalAttractor(camera, rw, rh),
        FractalType.Attractor when _attractorRenderer != null => _attractorRenderer.RenderToBuffer(Attractor.ToRenderParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(230, 120, 70),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Attractor => SolidPixels(rw, rh, new Color(0.02f, 0.03f, 0.07f)),
        FractalType.Kleinian when _metalKleinianRenderer?.IsAvailable == true =>
            RenderWithMetalKleinian(camera, rw, rh),
        FractalType.Kleinian => _kleinianRenderer!.RenderToBuffer(Kleinian.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(150, 125, 100),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Kifs when _metalKifsRenderer?.IsAvailable == true =>
            RenderWithMetalKifs(camera, rw, rh),
        FractalType.Kifs => _kifsRenderer!.RenderToBuffer(Kifs.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(150, 125, 100),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Mosely when _metalMoselyRenderer?.IsAvailable == true =>
            RenderWithMetalMosely(camera, rw, rh),
        FractalType.Mosely => _moselyRenderer!.RenderToBuffer(Mosely.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(170, 150, 130),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.PseudoKleinian4D when _metalPK4DRenderer?.IsAvailable == true =>
            RenderWithMetalPseudoKleinian4D(camera, rw, rh),
        FractalType.PseudoKleinian4D => _pk4dRenderer!.RenderToBuffer(PseudoKleinian4D.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(165, 150, 130),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.RiemannSphere when _metalRiemannSphereRenderer?.IsAvailable == true =>
            RenderWithMetalRiemannSphere(camera, rw, rh),
        FractalType.RiemannSphere => _riemannRenderer!.RenderToBuffer(RiemannSphere.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(205, 160, 135),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Mandalay when _metalMandalayRenderer?.IsAvailable == true =>
            RenderWithMetalMandalay(camera, rw, rh),
        FractalType.Mandalay => _mandalayRenderer!.RenderToBuffer(Mandalay.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(175, 165, 150),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.Anisotropic when _metalAnisotropicRenderer?.IsAvailable == true =>
            RenderWithMetalAnisotropic(camera, rw, rh),
        FractalType.Anisotropic => _anisoRenderer!.RenderToBuffer(Anisotropic.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(160, 158, 170),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.OrbitHybrid when _metalOrbitHybridRenderer?.IsAvailable == true =>
            RenderWithMetalOrbitHybrid(camera, rw, rh),
        FractalType.OrbitHybrid => _orbitHybridRenderer!.RenderToBuffer(OrbitHybrid.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(195, 170, 135),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        FractalType.AmazingBox when _metalRenderer?.IsAvailable == true =>
            RenderWithMetalAmazingBox(camera, rw, rh),
        FractalType.Mandelbox when _metalRenderer?.IsAvailable == true =>
            RenderWithMetal(camera, rw, rh),
        FractalType.Mandelbox => _boxRenderer!.RenderToBuffer(Mandelbox.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(170, 150, 130),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
        _ => _boxRenderer!.RenderToBuffer(Fractal.ToParams(), camera,
            PreviewWidth, PreviewHeight, PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f), surface: Color.Rgb(150, 125, 100),
            lightDirection: Light.ToDirection(), palette: Palette.ToParams(), tileRows: 64),
    };
    }

    private bool HasMetalPreviewRenderer() => ActiveType switch
    {
        FractalType.DeepZoom => _metalDeepZoomRenderer?.IsAvailable == true,
        FractalType.Mandelbulb => _metalMandelbulbRenderer?.IsAvailable == true,
        FractalType.BurningShip => _metalBurningShipRenderer?.IsAvailable == true,
        FractalType.QuaternionJulia => _metalQuaternionJuliaRenderer?.IsAvailable == true,
        FractalType.RotBox => _metalRotBoxRenderer?.IsAvailable == true,
        FractalType.Hybrid => _metalHybridRenderer?.IsAvailable == true,
        FractalType.QJBox => _metalQJBoxRenderer?.IsAvailable == true,
        FractalType.Menger => _metalMengerRenderer?.IsAvailable == true,
        FractalType.Bicomplex => _metalBicomplexRenderer?.IsAvailable == true,
        FractalType.Apollonian => _metalApollonianRenderer?.IsAvailable == true,
        FractalType.Phoenix => _metalPhoenixRenderer?.IsAvailable == true,
        FractalType.Biomorph => _metalBiomorphRenderer?.IsAvailable == true,
        FractalType.Kleinian => _metalKleinianRenderer?.IsAvailable == true,
        FractalType.Kifs => _metalKifsRenderer?.IsAvailable == true,
        FractalType.Mosely => _metalMoselyRenderer?.IsAvailable == true,
        FractalType.PseudoKleinian4D => _metalPK4DRenderer?.IsAvailable == true,
        FractalType.RiemannSphere => _metalRiemannSphereRenderer?.IsAvailable == true,
        FractalType.Mandalay => _metalMandalayRenderer?.IsAvailable == true,
        FractalType.Anisotropic => _metalAnisotropicRenderer?.IsAvailable == true,
        FractalType.OrbitHybrid => _metalOrbitHybridRenderer?.IsAvailable == true,
        FractalType.AmazingBox or FractalType.Mandelbox => _metalRenderer?.IsAvailable == true,
        FractalType.Attractor => _metalAttractorRenderer?.IsAvailable == true && _attractorHash != null,
        _ => false,
    };

    private static uint[] SolidPixels(int width, int height, Color color)
    {
        uint r = (uint)(Math.Clamp(color.R, 0f, 1f) * 255f + 0.5f);
        uint g = (uint)(Math.Clamp(color.G, 0f, 1f) * 255f + 0.5f);
        uint b = (uint)(Math.Clamp(color.B, 0f, 1f) * 255f + 0.5f);
        uint packed = (255u << 24) | (b << 16) | (g << 8) | r;
        var pixels = new uint[width * height];
        Array.Fill(pixels, packed);
        return pixels;
    }

    private uint[] RenderWithMetalAttractor(Camera3D camera, int width, int height)
    {
        var pixels = _metalAttractorRenderer!.Render(
            Attractor.ToRenderParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(230, 120, 70),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(),
            hash: _attractorHash!,
            postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalAttractorRenderer!.LastComputeMs;
        _metalReadbackMs = _metalAttractorRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetal(Camera3D camera, int width, int height)
    {
        var pixels = _metalRenderer!.RenderMandelbox(
            Mandelbox.ToParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(170, 150, 130),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(), postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalRenderer!.LastComputeMs;
        _metalReadbackMs = _metalRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalMandelbulb(Camera3D camera, int width, int height)
    {
        var pixels = _metalMandelbulbRenderer!.RenderMandelbulb(
            Mandelbulb.ToParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(210, 175, 140),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(), postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalMandelbulbRenderer!.LastComputeMs;
        _metalReadbackMs = _metalMandelbulbRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalRotBox(Camera3D camera, int width, int height)
    {
        var pixels = _metalRotBoxRenderer!.RenderRotBox(
            RotBox.ToParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(190, 175, 155),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(), postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalRotBoxRenderer!.LastComputeMs;
        _metalReadbackMs = _metalRotBoxRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalKifs(Camera3D camera, int width, int height)
    {
        var pixels = _metalKifsRenderer!.RenderKifs(
            Kifs.ToParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(150, 125, 100),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(), postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalKifsRenderer!.LastComputeMs;
        _metalReadbackMs = _metalKifsRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalKleinian(Camera3D camera, int width, int height)
    {
        var pixels = _metalKleinianRenderer!.RenderKleinian(
            Kleinian.ToParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(150, 125, 100),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(), postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalKleinianRenderer!.LastComputeMs;
        _metalReadbackMs = _metalKleinianRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalHybrid(Camera3D camera, int width, int height)
    {
        var pixels = _metalHybridRenderer!.RenderHybrid(
            Hybrid.ToParams(), camera, width, height,
            PreviewSettings(),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(190, 170, 145),
            lightDirection: Light.ToDirection(),
            palette: Palette.ToParams(), postProcess: PostProcess.ToParams());
        _metalComputeMs  = _metalHybridRenderer!.LastComputeMs;
        _metalReadbackMs = _metalHybridRenderer!.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalBurningShip(Camera3D camera, int width, int height) {
        var pixels = _metalBurningShipRenderer!.RenderBurningShip(BurningShip.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(225, 140, 90), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalBurningShipRenderer!.LastComputeMs; _metalReadbackMs = _metalBurningShipRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalQuaternionJulia(Camera3D camera, int width, int height) {
        var pixels = _metalQuaternionJuliaRenderer!.RenderQuaternionJulia(QuaternionJulia.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(210, 180, 150), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalQuaternionJuliaRenderer!.LastComputeMs; _metalReadbackMs = _metalQuaternionJuliaRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalQJBox(Camera3D camera, int width, int height) {
        var pixels = _metalQJBoxRenderer!.RenderQJBox(QJBox.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(195, 170, 145), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalQJBoxRenderer!.LastComputeMs; _metalReadbackMs = _metalQJBoxRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalMenger(Camera3D camera, int width, int height) {
        var pixels = _metalMengerRenderer!.RenderMenger(Menger.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(195, 170, 145), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalMengerRenderer!.LastComputeMs; _metalReadbackMs = _metalMengerRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalBicomplex(Camera3D camera, int width, int height) {
        var pixels = _metalBicomplexRenderer!.RenderBicomplex(Bicomplex.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(200, 175, 150), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalBicomplexRenderer!.LastComputeMs; _metalReadbackMs = _metalBicomplexRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalApollonian(Camera3D camera, int width, int height) {
        var pixels = _metalApollonianRenderer!.RenderApollonian(Apollonian.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(200, 175, 150), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalApollonianRenderer!.LastComputeMs; _metalReadbackMs = _metalApollonianRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalPhoenix(Camera3D camera, int width, int height) {
        var pixels = _metalPhoenixRenderer!.RenderPhoenix(Phoenix.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(210, 180, 150), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalPhoenixRenderer!.LastComputeMs; _metalReadbackMs = _metalPhoenixRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalBiomorph(Camera3D camera, int width, int height) {
        var pixels = _metalBiomorphRenderer!.RenderBiomorph(Biomorph.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(220, 180, 140), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalBiomorphRenderer!.LastComputeMs; _metalReadbackMs = _metalBiomorphRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalMosely(Camera3D camera, int width, int height) {
        var pixels = _metalMoselyRenderer!.RenderMosely(Mosely.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(170, 150, 130), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalMoselyRenderer!.LastComputeMs; _metalReadbackMs = _metalMoselyRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalPseudoKleinian4D(Camera3D camera, int width, int height) {
        var pixels = _metalPK4DRenderer!.RenderPseudoKleinian4D(PseudoKleinian4D.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(165, 150, 130), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalPK4DRenderer!.LastComputeMs; _metalReadbackMs = _metalPK4DRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalRiemannSphere(Camera3D camera, int width, int height) {
        var pixels = _metalRiemannSphereRenderer!.RenderRiemannSphere(RiemannSphere.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(205, 160, 135), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalRiemannSphereRenderer!.LastComputeMs; _metalReadbackMs = _metalRiemannSphereRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalMandalay(Camera3D camera, int width, int height) {
        var pixels = _metalMandalayRenderer!.RenderMandalay(Mandalay.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(175, 165, 150), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalMandalayRenderer!.LastComputeMs; _metalReadbackMs = _metalMandalayRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalAnisotropic(Camera3D camera, int width, int height) {
        var pixels = _metalAnisotropicRenderer!.RenderAnisotropic(Anisotropic.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(160, 158, 170), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalAnisotropicRenderer!.LastComputeMs; _metalReadbackMs = _metalAnisotropicRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalOrbitHybrid(Camera3D camera, int width, int height) {
        var pixels = _metalOrbitHybridRenderer!.RenderOrbitHybrid(OrbitHybrid.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(195, 170, 135), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalOrbitHybridRenderer!.LastComputeMs; _metalReadbackMs = _metalOrbitHybridRenderer!.LastReadbackMs; return pixels; }

    private uint[] RenderWithMetalDeepZoom(int width, int height)
    {
        var pixels = _metalDeepZoomRenderer!.Render(
            _deepView, width, height, Palette.ToParams(),
            new Color(0.02f, 0.03f, 0.07f), PreviewSettings(),
            interactive: _deepInteracting, interactiveIter: _deepPreviewIter);
        _metalComputeMs  = _metalDeepZoomRenderer.LastComputeMs;
        _metalReadbackMs = _metalDeepZoomRenderer.LastReadbackMs;
        return pixels;
    }

    private uint[] RenderWithMetalAmazingBox(Camera3D camera, int width, int height) {
        var pixels = _metalRenderer!.RenderMandelbox(Fractal.ToParams(), camera, width, height, PreviewSettings(), new Color(0.02f, 0.03f, 0.07f), Color.Rgb(150, 125, 100), Light.ToDirection(), Palette.ToParams(), PostProcess.ToParams());
        _metalComputeMs = _metalRenderer!.LastComputeMs; _metalReadbackMs = _metalRenderer!.LastReadbackMs; return pixels; }

    private RaymarchSettings PreviewSettings() => new(
        MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
        EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 1.0f,
        HeroSamples: 1,
        EnableReflections: Reflection.Bounces > 0,
        ReflectionBounces: Reflection.Bounces,
        Gloss: Reflection.Gloss,
        F0: Reflection.F0,
        LightIntensity: Light.Intensity);

    // High quality for the hero still: more march steps, finer hit/normal
    // epsilon, soft shadows on, more AO samples. The tiled path keeps it
    // TDR-safe even at 4K (each tile is width x 32 px with a Finish() between).
    /// <summary>
    /// Render the active fractal at the given resolution from the current camera
    /// and live params, using hero-quality settings. Shared by the single hero
    /// render and the animation batch. Must be called with the GL context current.
    /// </summary>
    private SkiaSharp.SKBitmap RenderActiveTo(int width, int height)
    {
        var cam = _cam.ToCamera(width, height);
        var bg = new Color(0.02f, 0.03f, 0.07f);
        var light = Light.ToDirection();
        var pal = Palette.ToParams();
        return ActiveType switch
        {
            FractalType.DeepZoom when OperatingSystem.IsMacOS() && _metalDeepZoomRenderer?.IsAvailable == true =>
                PixelsToSkBitmap(_metalDeepZoomRenderer.Render(
                    _deepView, width, height, Palette.ToParams(), bg, HeroSettings()), width, height),
            FractalType.DeepZoom => DeepZoomBitmap(width, height),
            FractalType.Mandelbulb => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalMandelbulbRenderer!.RenderMandelbulb(Mandelbulb.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(210, 175, 140), light, pal, postProcess: PostProcess.ToParams()), width, height)
                : _mandelbulbRenderer!.Render(Mandelbulb.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(210, 175, 140), light, pal, tileRows: 32),
            FractalType.BurningShip => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalBurningShipRenderer!.RenderBurningShip(BurningShip.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(225, 140, 90), light, pal, PostProcess.ToParams()), width, height)
                : _burningShipRenderer!.Render(BurningShip.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(225, 140, 90), light, pal, tileRows: 32),
            FractalType.QuaternionJulia => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalQuaternionJuliaRenderer!.RenderQuaternionJulia(QuaternionJulia.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(210, 180, 150), light, pal, PostProcess.ToParams()), width, height)
                : _qjuliaRenderer!.Render(QuaternionJulia.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(210, 180, 150), light, pal, tileRows: 32),
            FractalType.RotBox => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalRotBoxRenderer!.RenderRotBox(RotBox.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(190, 175, 155), light, pal, PostProcess.ToParams()), width, height)
                : _rotboxRenderer!.Render(RotBox.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(190, 175, 155), light, pal, tileRows: 32),
            FractalType.Hybrid => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalHybridRenderer!.RenderHybrid(Hybrid.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(190, 170, 145), light, pal, PostProcess.ToParams()), width, height)
                : _hybridRenderer!.Render(Hybrid.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(190, 170, 145), light, pal, tileRows: 32),
            FractalType.QJBox => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalQJBoxRenderer!.RenderQJBox(QJBox.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(195, 170, 145), light, pal, PostProcess.ToParams()), width, height)
                : _qjboxRenderer!.Render(QJBox.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(195, 170, 145), light, pal, tileRows: 32),
            FractalType.Menger => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalMengerRenderer!.RenderMenger(Menger.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(195, 170, 145), light, pal, PostProcess.ToParams()), width, height)
                : _mengerRenderer!.Render(Menger.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(195, 170, 145), light, pal, tileRows: 32),
            FractalType.Bicomplex => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalBicomplexRenderer!.RenderBicomplex(Bicomplex.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(200, 175, 150), light, pal, PostProcess.ToParams()), width, height)
                : _bicomplexRenderer!.Render(Bicomplex.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(200, 175, 150), light, pal, tileRows: 32),
            FractalType.Apollonian => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalApollonianRenderer!.RenderApollonian(Apollonian.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(200, 175, 150), light, pal, PostProcess.ToParams()), width, height)
                : _apollonianRenderer!.Render(Apollonian.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(200, 175, 150), light, pal, tileRows: 32),
            FractalType.Phoenix => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalPhoenixRenderer!.RenderPhoenix(Phoenix.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(210, 180, 150), light, pal, PostProcess.ToParams()), width, height)
                : _phoenixRenderer!.Render(Phoenix.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(210, 180, 150), light, pal, tileRows: 32),
            FractalType.Biomorph => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalBiomorphRenderer!.RenderBiomorph(Biomorph.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(220, 180, 140), light, pal, PostProcess.ToParams()), width, height)
                : _biomorphRenderer!.Render(Biomorph.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(220, 180, 140), light, pal, tileRows: 32),
            FractalType.Attractor => _attractorRenderer!.Render(Attractor.ToRenderParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(230, 120, 70), light, pal, tileRows: 32),
            FractalType.Kleinian => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalKleinianRenderer!.RenderKleinian(Kleinian.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, PostProcess.ToParams()), width, height)
                : _kleinianRenderer!.Render(Kleinian.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
            FractalType.Kifs => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalKifsRenderer!.RenderKifs(Kifs.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, PostProcess.ToParams()), width, height)
                : _kifsRenderer!.Render(Kifs.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
            FractalType.Mosely => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalMoselyRenderer!.RenderMosely(Mosely.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(170, 150, 130), light, pal, PostProcess.ToParams()), width, height)
                : _moselyRenderer!.Render(Mosely.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(170, 150, 130), light, pal, tileRows: 32),
            FractalType.PseudoKleinian4D => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalPK4DRenderer!.RenderPseudoKleinian4D(PseudoKleinian4D.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(165, 150, 130), light, pal, PostProcess.ToParams()), width, height)
                : _pk4dRenderer!.Render(PseudoKleinian4D.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(165, 150, 130), light, pal, tileRows: 32),
            FractalType.RiemannSphere => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalRiemannSphereRenderer!.RenderRiemannSphere(RiemannSphere.ToParams(), cam, width, height, HeroSettings(1.5e-3f), bg, Color.Rgb(205, 160, 135), light, pal, PostProcess.ToParams()), width, height)
                : _riemannRenderer!.Render(RiemannSphere.ToParams(), cam, width, height, HeroSettings(1.5e-3f), bg, Color.Rgb(205, 160, 135), light, pal, tileRows: 32),
            FractalType.Mandalay => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalMandalayRenderer!.RenderMandalay(Mandalay.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(175, 165, 150), light, pal, PostProcess.ToParams()), width, height)
                : _mandalayRenderer!.Render(Mandalay.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(175, 165, 150), light, pal, tileRows: 32),
            FractalType.Anisotropic => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalAnisotropicRenderer!.RenderAnisotropic(Anisotropic.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(160, 158, 170), light, pal, PostProcess.ToParams()), width, height)
                : _anisoRenderer!.Render(Anisotropic.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(160, 158, 170), light, pal, tileRows: 32),
            FractalType.OrbitHybrid => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalOrbitHybridRenderer!.RenderOrbitHybrid(OrbitHybrid.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(195, 170, 135), light, pal, PostProcess.ToParams()), width, height)
                : _orbitHybridRenderer!.Render(OrbitHybrid.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(195, 170, 135), light, pal, tileRows: 32),
            FractalType.AmazingBox => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalRenderer!.RenderMandelbox(Fractal.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, PostProcess.ToParams()), width, height)
                : _boxRenderer!.Render(Fractal.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
            FractalType.Mandelbox => OperatingSystem.IsMacOS()
                ? PixelsToSkBitmap(_metalRenderer!.RenderMandelbox(Mandelbox.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(170, 150, 130), light, pal, PostProcess.ToParams()), width, height)
                : _boxRenderer!.Render(Mandelbox.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(170, 150, 130), light, pal, tileRows: 32),
            _ => _boxRenderer!.Render(Fractal.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
        };
    }

    // Deep-zoom renders straight to a packed RGBA8 buffer (no SKBitmap-returning
    // renderer), so wrap it the same way GpuMandelbulbRenderer.Render does.
    private SkiaSharp.SKBitmap DeepZoomBitmap(int width, int height)
    {
        uint[] pixels = _deepPipeline!.Render(_deepView, width, height,
            Palette.ToParams(), new Color(0.02f, 0.03f, 0.07f),
            heroSamples: Math.Max(1, HeroSampleCount), tileRows: 32);
        return PixelsToSkBitmap(pixels, width, height);
    }

    private static SkiaSharp.SKBitmap PixelsToSkBitmap(uint[] pixels, int width, int height)
    {
        var info = new SkiaSharp.SKImageInfo(width, height,
            SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        var bitmap = new SkiaSharp.SKBitmap(info);
        var bytes = new byte[pixels.Length * 4];
        System.Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    private RaymarchSettings HeroSettings() => HeroSettings(6e-7f);

    // Hero settings with an explicit hit/normal epsilon. Exact-DE chapters use the
    // razor-fine default (6e-7). Chapters whose DE is approximate and spiky need a
    // much coarser epsilon: the Riemann Sphere's effective power reaches ~72, so the
    // orbit escapes almost discontinuously and the surface has no sub-micron shell to
    // home into -- at 6e-7 the marcher oversteps and misses it (full in the coarse
    // preview, sparse at hero). A coarse epsilon catches the same surface the preview
    // does, and the DE can't resolve finer than that anyway.
    private RaymarchSettings HeroSettings(float eps) => new(
        MaxSteps: 400, HitEpsilon: eps, MaxDistance: 50f, NormalEpsilon: eps,
        EnableSoftShadows: true, ShadowSteps: 64, ShadowSoftness: 14f,
        EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.0f,
        HeroSamples: Math.Max(1, HeroSampleCount),
        EnableReflections: Reflection.Bounces > 0,
        ReflectionBounces: Reflection.Bounces,
        Gloss: Reflection.Gloss,
        F0: Reflection.F0,
        LightIntensity: Light.Intensity);

    private const string BlitVertexSrc = @"#version 330 core
out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    private const string BlitFragmentSrc = @"#version 330 core
in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex;
void main() {
    fragColor = texture(uTex, vec2(vUv.x, 1.0 - vUv.y));
}";
}
