using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Parsec.Audio;
using Parsec.Audio.Sonification;

namespace Parsec.App;

public partial class MainWindow : Window
{
    private FractalView? _view;
    private Border? _panelHost;
    private Border? _bankHost;
    private Button? _generateButton;
    private AudioTransportController? _audioTransport;
    private AudioModulationController? _audioMod;
    private AudioMappingPanel? _audioMappingPanel;
    private DispatcherTimer? _modTimer;
    private readonly SonificationController _sonification = new();

    private FractalDroneStream? _droneStream;
    private bool _sonifyActive;
    private SonificationMode _sonifyMode  = SonificationMode.Hybrid;
    private float            _sonifyBlend = 0f;   // 0 = Hybrid, 1 = DirectOrbit

    // Animation timeline state.
    private KeyframeBank? _bank;
    private Timeline? _timeline;
    private ParamSchema? _activeSchema;   // shared by panel + timeline this fractal

    // Playback.
    private DispatcherTimer? _playTimer;
    private DateTime _playStart;
    private int _playFromIndex;
    private bool _playing;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _view = this.FindControl<FractalView>("FractalView");
        _panelHost = this.FindControl<Border>("PanelHost");
        _bankHost = this.FindControl<Border>("BankHost");
        var status = this.FindControl<TextBlock>("StatusText");
        var selector = this.FindControl<ComboBox>("FractalSelector");

        if (_view != null && status != null)
            _view.StatusChanged += text => status.Text = text;

        if (_view != null)
            _view.Sonification = _sonification;

        if (selector != null)
        {
            selector.SelectedIndex = 0;   // KIFS, the default ActiveType
            selector.SelectionChanged += OnFractalChanged;
        }

        var heroButton = this.FindControl<Button>("HeroButton");
        if (heroButton != null)
            heroButton.Click += OnHeroClick;

        var formulaSelector = this.FindControl<ComboBox>("FormulaSelector");
        if (formulaSelector != null)
            formulaSelector.SelectionChanged += (_, _) =>
            {
                // Dropdown index == formula int (Mandelbrot 0, Prospector 1,
                // Julia 2, Burning Ship 3).
                if (_view != null && formulaSelector.SelectedIndex >= 0)
                    _view.SetDeepFormula(formulaSelector.SelectedIndex);
            };

        var heroSamplesSelector = this.FindControl<ComboBox>("HeroSamplesSelector");
        if (heroSamplesSelector != null)
            heroSamplesSelector.SelectionChanged += (_, _) =>
            {
                if (_view != null)
                    _view.HeroSampleCount = heroSamplesSelector.SelectedIndex switch
                    {
                        0 => 1, 1 => 4, 2 => 9, 3 => 16, _ => 1,
                    };
            };

        _generateButton = this.FindControl<Button>("GenerateButton");
        if (_generateButton != null)
            _generateButton.Click += OnGenerateClick;

        var testRenderButton = this.FindControl<Button>("TestRenderButton");
        if (testRenderButton != null)
            testRenderButton.Click += OnTestRenderClick;

        var renderToVideoButton = this.FindControl<Button>("RenderToVideoButton");
        if (renderToVideoButton != null)
            renderToVideoButton.Click += OnRenderToVideoClick;

        var saveAnimButton = this.FindControl<Button>("SaveAnimButton");
        if (saveAnimButton != null)
            saveAnimButton.Click += OnSaveAnimClick;

        var loadAnimButton = this.FindControl<Button>("LoadAnimButton");
        if (loadAnimButton != null)
            loadAnimButton.Click += OnLoadAnimClick;

        var sonifyButton = this.FindControl<Button>("SonifyButton");
        if (sonifyButton != null)
            sonifyButton.Click += OnSonifyClick;

        var sonifyBlendSlider = this.FindControl<Slider>("SonifyBlendSlider");
        if (sonifyBlendSlider != null)
            sonifyBlendSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name != nameof(Slider.Value)) return;
                _sonifyBlend = (float)sonifyBlendSlider.Value;
                _sonifyMode  = _sonifyBlend >= 0.5f
                    ? SonificationMode.DirectOrbit : SonificationMode.Hybrid;
                if (_droneStream != null)
                    _droneStream.BlendAmount = _sonifyBlend;
            };

        var temperamentSlider = this.FindControl<Slider>("TemperamentCeilingSlider");
        if (temperamentSlider != null)
            temperamentSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name == nameof(Slider.Value) && _droneStream != null)
                    _droneStream.TemperamentCeiling = (float)(double)e.NewValue!;
            };

        if (_view != null && status != null)
        {
            _view.HeroRenderComplete += text => status.Text = text;
            _view.AnimationProgress += (f, n) => status.Text = $"Rendering frame {f}/{n}...";
            _view.AnimationRenderComplete += text =>
            {
                status.Text = text;
                RefreshPanelValues();   // resync sliders after the batch
            };
        }

        // Build the keyframe bank once; it is re-bound to a new timeline whenever
        // the active fractal changes.
        _bank = new KeyframeBank();
        _bank.CellSelected += OnCellSelected;
        _bank.CellCleared += OnCellCleared;
        if (_bankHost != null) _bankHost.Child = _bank;

        // Space toggles playback. Tunneling handler so it works regardless of
        // focus (the GL view grabs keyboard focus for fly controls).
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _audioTransport = new AudioTransportController(new OpenALAudioPlaybackBackend());
        var audioHost = this.FindControl<ContentControl>("AudioHost");
        if (audioHost != null)
            audioHost.Content = new AudioTransportPanel(_audioTransport);

        _audioMod = new AudioModulationController(_audioTransport);
        _audioMappingPanel = new AudioMappingPanel(_audioMod);
        var audioMappingHost = this.FindControl<ContentControl>("AudioMappingHost");
        if (audioMappingHost != null)
            audioMappingHost.Content = _audioMappingPanel;

        _modTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _modTimer.Tick += (_, _) =>
        {
            if (_audioMod.Tick()) _view?.MarkDirty();
        };
        _modTimer.Start();

        Closed += OnWindowClosed;

        RebuildForActiveFractal();
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        _modTimer?.Stop();
        _droneStream?.Stop();
        _droneStream = null;
        if (_audioTransport != null)
            await _audioTransport.DisposeAsync();
    }

    // ----------------------------------------------------------- sonification toggle

    private void OnSonifyClick(object? sender, RoutedEventArgs e)
    {
        if (_sonifyActive)
        {
            _sonifyActive = false;
            _droneStream?.Stop();
            _droneStream = null;
            _modTimer?.Start();   // resume reactive modulation
            if (sender is Button btn) btn.Content = "Live Sonify: OFF";
            SetStatus("Live sonification stopped.");
        }
        else
        {
            // Mutual exclusion: pause reactive modulation while sonifying
            _modTimer?.Stop();

            var voice = ActiveTypeToVoice(_view?.ActiveType ?? FractalType.Mandelbox);
            _droneStream = new FractalDroneStream(() => _sonification.LatestFrame, voice, _sonifyMode);
            _droneStream.BlendAmount = _sonifyBlend;
            var ceilSlider = this.FindControl<Slider>("TemperamentCeilingSlider");
            if (ceilSlider != null)
                _droneStream.TemperamentCeiling = (float)ceilSlider.Value;
            bool ok = _droneStream.Start();

            if (!ok)
            {
                _droneStream = null;
                _modTimer?.Start();
                SetStatus("Live Sonify: OpenAL unavailable. Install openal-soft (brew install openal-soft).");
                return;
            }

            _sonifyActive = true;
            if (sender is Button btn) btn.Content = "Live Sonify: ON";
            SetStatus($"Live sonification started ({voice} voice). Fly around to hear geometry.");
        }
    }

    // ----------------------------------------------------------- hero / generate
    // 4:3 hero resolution tiers, matching the preview's aspect (640×480) so the
    // hero framing is WYSIWYG. Width-anchored to the DCI 2K/4K/8K convention.
    private (int Width, int Height) HeroResolution() =>
        (this.FindControl<ComboBox>("ResolutionSelector")?.SelectedIndex ?? 1) switch
        {
            0 => (2048, 1536),   // 2K
            1 => (4096, 3072),   // 4K
            2 => (8192, 6144),   // 8K
            3 => (12288, 9216), // 12k
            _ => (4096, 3072),
        };

    private bool TransparentBackgroundEnabled =>
        this.FindControl<CheckBox>("TransparentBackgroundCheckBox")?.IsChecked == true;

    private void OnHeroClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        string fractal = _view.ActiveType switch
        {
            FractalType.Kleinian => "kleinian",
            FractalType.PseudoKleinian4D => "pk4d",
            FractalType.RiemannSphere => "riemann",
            FractalType.Mandalay => "mandalay",
            FractalType.Anisotropic => "anisotropic",
            FractalType.OrbitHybrid => "orbithybrid",
            FractalType.BurningShip => "burningship",
            FractalType.AmazingBox => "amazingbox",
            FractalType.Mandelbox => "mandelbox",
            FractalType.Attractor => "thomas",
            FractalType.Mandelbulb => "mandelbulb",
            FractalType.QuaternionJulia => "qjulia",
            FractalType.RotBox => "rotbox",
            FractalType.Hybrid => "hybrid",
            FractalType.QJBox => "qjbox",
            FractalType.Menger => "menger",
            FractalType.Bicomplex => "bicomplex",
            FractalType.Apollonian => "apollonian",
            FractalType.Phoenix => "phoenix",
            FractalType.Biomorph => "biomorph",
            FractalType.Mosely => "mosely",
            _ => "kifs",
        };
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Parsec");
        string path = System.IO.Path.Combine(dir, $"parsec_{fractal}_{stamp}.png");

        var (w, h) = HeroResolution();
        bool transparent = TransparentBackgroundEnabled;
        SetStatus($"Rendering {w}x{h}{(transparent ? " transparent" : "")}... (window may pause)");
        _view.RequestHeroRender(path, w, h, transparent);
    }

    private void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        SetStatus("Generating attractor... (may pause briefly)");
        _view.RequestAttractorRegen();
    }

    // ----------------------------------------------------------- fractal switch
    private void OnFractalChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_view == null || sender is not ComboBox cb) return;
        StopPlayback();
        var type = cb.SelectedIndex switch
        {
            1 => FractalType.AmazingBox,
            2 => FractalType.Mandelbox,
            3 => FractalType.Kleinian,
            4 => FractalType.PseudoKleinian4D,
            5 => FractalType.Attractor,
            6 => FractalType.Mandelbulb,
            7 => FractalType.QuaternionJulia,
            8 => FractalType.RotBox,
            9 => FractalType.Hybrid,
            10 => FractalType.QJBox,
            11 => FractalType.Menger,
            12 => FractalType.Bicomplex,
            13 => FractalType.Apollonian,
            14 => FractalType.Phoenix,
            15 => FractalType.Biomorph,
            16 => FractalType.Mosely,
            17 => FractalType.RiemannSphere,
            18 => FractalType.Mandalay,
            19 => FractalType.Anisotropic,
            20 => FractalType.OrbitHybrid,
            21 => FractalType.BurningShip,
            22 => FractalType.DeepZoom,
            _ => FractalType.Kifs,
        };
        _view.SetActiveType(type);
        _droneStream?.SetVoice(ActiveTypeToVoice(type));
        if (_generateButton != null)
            _generateButton.IsVisible = type == FractalType.Attractor;

        // The FORMULA dropdown belongs to the Deep Zoom 2D chapter only.
        bool deep = type == FractalType.DeepZoom;
        var formulaSelector = this.FindControl<ComboBox>("FormulaSelector");
        var formulaLabel = this.FindControl<TextBlock>("FormulaLabel");
        if (formulaSelector != null)
        {
            formulaSelector.IsVisible = deep;
            if (deep) formulaSelector.SelectedIndex = _view.DeepFormula;   // index == formula
        }
        if (formulaLabel != null) formulaLabel.IsVisible = deep;

        RebuildForActiveFractal();   // keyframes are per-fractal; reset on switch
    }

    private static FractalVoice ActiveTypeToVoice(FractalType type) => type switch
    {
        FractalType.Mandelbulb  => FractalVoice.Mandelbulb,
        FractalType.Kleinian    => FractalVoice.Kleinian,
        FractalType.BurningShip => FractalVoice.BurningShip,
        FractalType.Apollonian  => FractalVoice.Apollonian,
        _                       => FractalVoice.Mandelbox,
    };

    private void RebuildPanel()
    {
        if (_view == null || _panelHost == null || _activeSchema == null) return;
        var panel = new ParameterPanel(_activeSchema);
        panel.OnChanged += OnParamChanged;
        _panelHost.Child = panel;
    }

    /// <summary>
    /// Build one schema instance for the active fractal and bind BOTH the panel
    /// and the timeline to it, so they share the exact same descriptor objects
    /// (capture/apply and the displayed sliders are guaranteed consistent).
    /// </summary>
    private void RebuildForActiveFractal()
    {
        if (_view == null) return;
        _activeSchema = _view.BuildActiveSchema();
        // Mappings reference descriptor instances from the previous schema; clear
        // them so there are no stale references when the fractal type changes.
        _audioMod?.ClearMappings();
        _audioMappingPanel?.SetSchema(_activeSchema);
        _sonification.SetDescriptors(_activeSchema.Parameters);
        RebuildPanel();
        RebuildTimeline();
    }

    // ----------------------------------------------------------- timeline / bank
    private void RebuildTimeline()
    {
        if (_view == null || _bank == null || _activeSchema == null) return;

        // Animation is disabled for the attractor: interpolating its generation
        // params is meaningless (tweening seed count / drift phase across a
        // chaotic regime morphs between unrelated attractors), and every frame
        // would imply an expensive regenerate.
        bool animatable = _view.ActiveType != FractalType.Attractor;
        _bank.SetEnabled(animatable);

        if (!animatable)
        {
            _timeline = null;
            return;
        }

        // Bind the timeline to the SAME descriptor instances the panel uses (the
        // shared _activeSchema), so capture/apply read and write the live state
        // consistently and Values[] stays positionally aligned for its lifetime.
        _timeline = new Timeline(_activeSchema.Parameters, KindFor);
        _timeline.SchemaTag = _view.ActiveType.ToString();
        _bank.Refresh(_timeline);
    }

    // Palette phase wraps at 2*pi; everything else is linear.
    private static InterpKind KindFor(ParamDescriptor d) =>
        d.Label.Contains("phase", StringComparison.OrdinalIgnoreCase)
            ? InterpKind.AngularWrap : InterpKind.Linear;

    private void OnCellSelected(int index)
    {
        if (_timeline == null) return;
        StopPlayback();
        _timeline.Select(index);
        _bank?.Refresh(_timeline);
        // If the selected slot is already set, restore its values so the user
        // sees that keyframe's look (and edits start from it).
        if (_timeline.IsSet(index))
        {
            _timeline.ApplySlot(index);
            RefreshPanelValues();
            _view?.MarkDirty();
        }
    }

    private void OnCellCleared(int index)
    {
        if (_timeline == null) return;
        StopPlayback();
        if (_timeline.Clear(index))
            _bank?.Refresh(_timeline);
    }

    private void OnParamChanged()
    {
        // A slider moved. First move while a not-yet-set slot is selected sets
        // that keyframe (slot 0 is always set). Editing an already-set slot
        // updates its snapshot. Don't capture during playback (the interpolator
        // is the one writing values then).
        if (_timeline != null && !_playing)
        {
            int sel = _timeline.SelectedIndex;
            bool wasSet = _timeline.IsSet(sel);
            _timeline.CaptureInto(sel);
            if (!wasSet) _bank?.Refresh(_timeline);
        }
        // Clear audio delta tracking so the new slider position becomes the base.
        _audioMod?.OnManualParamChanged();
        _view?.MarkDirty();
    }

    // ----------------------------------------------------------- playback
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            if (_timeline == null) return;
            if (_playing) StopPlayback();
            else StartPlayback();
            e.Handled = true;
        }
    }

    private void StartPlayback()
    {
        if (_timeline == null || _view == null) return;
        if (_timeline.LastSetIndex() <= _timeline.SelectedIndex)
        {
            SetStatus("Playback: need a later keyframe to play toward.");
            return;
        }
        _playFromIndex = _timeline.SelectedIndex;
        _playStart = DateTime.UtcNow;
        _playing = true;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
        SetStatus("Playing... (space to stop)");
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_timeline == null || _view == null) { StopPlayback(); return; }
        double t = (DateTime.UtcNow - _playStart).TotalSeconds;
        bool more = _timeline.ApplyAtTime(_playFromIndex, t);
        _view.MarkDirty();
        if (!more)
        {
            StopPlayback();
            RefreshPanelValues();   // snap sliders to final keyframe values
            SetStatus("Playback complete.");
        }
    }

    private void StopPlayback()
    {
        if (_playTimer != null)
        {
            _playTimer.Stop();
            _playTimer.Tick -= OnPlayTick;
            _playTimer = null;
        }
        _playing = false;
    }

    // Rebuild the panel so slider positions reflect current live values (after
    // restoring/applying a keyframe). Cheap and simple for the MVP.
    private void RefreshPanelValues() => RebuildPanel();

    // ----------------------------------------------------------- animation I/O
    private const double RenderFps = 30.0;
    private const int TestWidth = 1280;
    private const int TestHeight = 720;

    private string AnimDir() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Parsec", "anim");

    private void OnTestRenderClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null || _timeline == null)
        {
            SetStatus("Test render: animation not available for this fractal.");
            return;
        }
        StopPlayback();
        double duration = _timeline.DurationFrom(0);
        if (duration <= 0)
        {
            SetStatus("Test render: set a later keyframe first (need >1 keyframe).");
            return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = System.IO.Path.Combine(AnimDir(), $"render_{stamp}");
        var timeline = _timeline;
        bool transparent = TransparentBackgroundEnabled;

        SetStatus($"Rendering ~{(int)(duration * RenderFps)} frames at {TestWidth}x{TestHeight}{(transparent ? " transparent" : "")}... (window will pause)");

        // Capture locals for the apply-callback (runs on the GL thread per frame).
        var audioMod = _audioMod;
        _view.RequestAnimationRender(dir, TestWidth, TestHeight, RenderFps, duration,
            t =>
            {
                timeline.ApplyAtTime(0, t);
                audioMod?.ApplyAtTime(TimeSpan.FromSeconds(t));
            },
            transparent);

        // Stitch hint: include audio track in ffmpeg command if one is loaded.
        string mp4 = System.IO.Path.Combine(dir, "out.mp4");
        string frames = System.IO.Path.Combine(dir, "frame_%05d.png");
        bool hasAudio = audioMod?.TrackSource?.IsFile == true;
        string audioInput = hasAudio ? $" -i \"{audioMod!.TrackSource!.LocalPath}\"" : string.Empty;
        string audioCodec = hasAudio ? " -c:a aac -shortest" : string.Empty;
        string videoArgs = transparent
            ? " -c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le"
            : " -c:v libx264 -pix_fmt yuv420p";
        string output = transparent ? System.IO.Path.Combine(dir, "out_alpha.mov") : mp4;
        Console.WriteLine(
            $"To stitch: ffmpeg -framerate {RenderFps} -i \"{frames}\"{audioInput}" +
            $"{videoArgs}{audioCodec} \"{output}\"");
    }

    private void OnSaveAnimClick(object? sender, RoutedEventArgs e)
    {
        if (_timeline == null) { SetStatus("Nothing to save for this fractal."); return; }
        try
        {
            string dir = AnimDir();
            System.IO.Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = System.IO.Path.Combine(dir, $"timeline_{stamp}.json");
            System.IO.File.WriteAllText(path, _timeline.ToJson());
            SetStatus($"Saved animation to {path}");
        }
        catch (Exception ex) { SetStatus($"Save failed: {ex.Message}"); }
    }

    private void OnLoadAnimClick(object? sender, RoutedEventArgs e)
    {
        if (_timeline == null) { SetStatus("Load not available for this fractal."); return; }
        try
        {
            string dir = AnimDir();
            if (!System.IO.Directory.Exists(dir))
            {
                SetStatus("No saved animations found.");
                return;
            }
            // MVP: load the most recent timeline_*.json. (A file picker is a
            // later refinement.)
            var files = System.IO.Directory.GetFiles(dir, "timeline_*.json");
            if (files.Length == 0) { SetStatus("No saved animations found."); return; }
            Array.Sort(files);
            string latest = files[^1];
            string json = System.IO.File.ReadAllText(latest);
            if (_timeline.LoadJson(json))
            {
                _bank?.Refresh(_timeline);
                _timeline.ApplySlot(_timeline.SelectedIndex);
                RefreshPanelValues();
                _view?.MarkDirty();
                SetStatus($"Loaded {System.IO.Path.GetFileName(latest)}");
            }
            else
            {
                SetStatus("Load failed: saved animation doesn't match this fractal.");
            }
        }
        catch (Exception ex) { SetStatus($"Load failed: {ex.Message}"); }
    }

    // ----------------------------------------------------------- render to video
    private string VideoDir() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Parsec", "video");

    private void OnRenderToVideoClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null || _timeline == null)
        {
            SetStatus("Render to video: animation not available for this fractal.");
            return;
        }
        StopPlayback();
        double duration = _timeline.DurationFrom(0);
        if (duration <= 0)
        {
            SetStatus("Render to video: set a later keyframe first (need >1 keyframe).");
            return;
        }

        string stamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string framesDir = System.IO.Path.Combine(VideoDir(), $"frames_{stamp}");
        bool transparent = TransparentBackgroundEnabled;
        string outputVideo = System.IO.Path.Combine(VideoDir(), transparent ? $"render_{stamp}_alpha.mov" : $"render_{stamp}.mp4");
        System.IO.Directory.CreateDirectory(VideoDir());

        // Sonification export: when live sonify is on, capture a sonic frame per export
        // frame and synthesize the soundtrack offline. Mutually exclusive with the
        // audio-reactive WAV (reactive modulation is suppressed while sonifying).
        bool sonify = _sonifyActive;
        bool hasAudio  = !sonify && _audioMod?.TrackSource?.IsFile == true;
        string? audioPath = hasAudio ? _audioMod!.TrackSource!.LocalPath : null;

        var timeline = _timeline;
        var audioMod = _audioMod;
        var view = _view;

        List<FractalSonicFrame>? sonicFrames = null;
        SonificationController? exportSon = null;
        FractalVoice exportVoice = ActiveTypeToVoice(_view.ActiveType);
        SonificationMode exportSonifyMode = _sonifyMode;
        float exportBlend   = _sonifyBlend;
        float exportCeiling = (float)(this.FindControl<Slider>("TemperamentCeilingSlider")?.Value ?? 0.9);
        if (sonify)
        {
            sonicFrames = new List<FractalSonicFrame>();
            exportSon = new SonificationController();
            if (_activeSchema != null) exportSon.SetDescriptors(_activeSchema.Parameters);
        }

        SetStatus($"Rendering ~{(int)(duration * RenderFps)} frames at {TestWidth}x{TestHeight}{(transparent ? " transparent" : "")}...");

        // One-shot subscription: fires ffmpeg after frames are written.
        Action<string>? handler = null;
        handler = msg =>
        {
            _view.AnimationRenderComplete -= handler!;
            if (msg.StartsWith("Rendered"))
            {
                Dispatcher.UIThread.Post(() => SetStatus("Stitching video..."));
                _ = Task.Run(async () =>
                {
                    bool mux = hasAudio;
                    string? muxPath = audioPath;
                    if (sonify && sonicFrames!.Count > 0)
                    {
                        try
                        {
                            short[] pcm;
                            int exportSr = HybridSynth.DefaultSampleRate; // both synths use 44100
                            bool canDirect = exportVoice != FractalVoice.Apollonian;

                            if (exportBlend >= 0.99f && canDirect)
                            {
                                pcm = DirectOrbitSynth.Synthesize(sonicFrames,
                                    controlRateHz: RenderFps,
                                    voice: exportVoice);
                            }
                            else if (exportBlend <= 0.01f || !canDirect)
                            {
                                pcm = HybridSynth.Synthesize(sonicFrames,
                                    temperamentCeiling: exportCeiling,
                                    controlRateHz: RenderFps,
                                    voice: exportVoice);
                            }
                            else
                            {
                                // Blend: synthesize both and lerp sample-by-sample
                                var pcmH = HybridSynth.Synthesize(sonicFrames,
                                    temperamentCeiling: exportCeiling,
                                    controlRateHz: RenderFps,
                                    voice: exportVoice);
                                var pcmD = DirectOrbitSynth.Synthesize(sonicFrames,
                                    controlRateHz: RenderFps,
                                    voice: exportVoice);
                                float hybGain = 1f - exportBlend;
                                pcm = new short[Math.Max(pcmH.Length, pcmD.Length)];
                                for (int i = 0; i < pcm.Length; i++)
                                {
                                    float h = i < pcmH.Length ? pcmH[i] : 0f;
                                    float d = i < pcmD.Length ? pcmD[i] : 0f;
                                    pcm[i] = (short)Math.Clamp(
                                        (int)(hybGain * h + exportBlend * d),
                                        short.MinValue, short.MaxValue);
                                }
                            }
                            string wavPath = System.IO.Path.Combine(VideoDir(), $"sonify_{stamp}.wav");
                            WavEncoder.Write(wavPath, pcm, exportSr, channels: 2);
                            mux = true;
                            muxPath = wavPath;
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.UIThread.Post(() =>
                                SetStatus($"Sonification synth failed ({ex.Message}) — stitching silent video..."));
                            mux = false;
                            muxPath = null;
                        }
                    }
                    await RunFfmpegAsync(framesDir, outputVideo, mux, muxPath, transparent);
                });
            }
            else
            {
                Dispatcher.UIThread.Post(() => SetStatus(msg));
            }
        };
        _view.AnimationRenderComplete += handler;

        _view.RequestAnimationRender(framesDir, TestWidth, TestHeight, RenderFps, duration,
            t =>
            {
                timeline.ApplyAtTime(0, t);
                if (sonify)
                    sonicFrames!.Add(view.CaptureSonicFrame(t, exportSon!));
                else
                    audioMod?.ApplyAtTime(TimeSpan.FromSeconds(t));
            },
            transparent);
    }

    private async Task RunFfmpegAsync(string framesDir, string outputVideo, bool hasAudio, string? audioPath, bool transparent)
    {
        string frames     = System.IO.Path.Combine(framesDir, "frame_%05d.png");
        string audioInput = hasAudio && audioPath != null ? $" -i \"{audioPath}\"" : string.Empty;
        string audioCodec = hasAudio && audioPath != null ? " -c:a aac -shortest" : string.Empty;
        string videoCodec = transparent
            ? " -c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le"
            : " -c:v libx264 -crf 18 -pix_fmt yuv420p";
        string args       = $"-framerate {RenderFps} -i \"{frames}\"{audioInput}" +
                            $"{videoCodec}{audioCodec} \"{outputVideo}\"";

        string ffmpeg = FindFfmpeg();
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardError = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
        };

        System.Diagnostics.Process? proc = null;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch
        {
            Dispatcher.UIThread.Post(() => SetStatus($"ffmpeg not found — frames saved to {framesDir}"));
            return;
        }

        if (proc == null)
        {
            Dispatcher.UIThread.Post(() => SetStatus($"ffmpeg not found — frames saved to {framesDir}"));
            return;
        }

        await proc.StandardError.ReadToEndAsync(); // drain stderr so process doesn't block
        await proc.WaitForExitAsync();

        if (proc.ExitCode == 0)
        {
            try { System.IO.Directory.Delete(framesDir, recursive: true); } catch { }
            Dispatcher.UIThread.Post(() => SetStatus($"Video saved to {outputVideo}"));
        }
        else
        {
            Dispatcher.UIThread.Post(() => SetStatus($"ffmpeg failed (code {proc.ExitCode}) — frames in {framesDir}"));
        }
    }

    private static string FindFfmpeg()
    {
        foreach (var candidate in new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" })
            if (System.IO.File.Exists(candidate)) return candidate;
        return "ffmpeg";
    }

    private void SetStatus(string text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status != null) status.Text = text;
    }
}
