# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

All projects target **net9.0**. Install with `brew install dotnet@9` on macOS if missing.

```bash
# Build everything
dotnet build

# Run the desktop app (Release is strongly recommended -- renderer is compute-heavy)
dotnet run --project src/Parsec.App/Parsec.App.csproj -c Release

# Run the CLI (IFS examples)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release
```

**Only `Parsec.App` is runnable as the desktop app.** The other projects are libraries, except `Parsec.Cli`, which is useful for headless probes and sample renders. There is no formal test suite -- `SmokeTest.cs` in `Parsec.Rendering.Gpu` is a headless GL context sanity check, not a test runner.

Shaders are embedded resources listed explicitly in `Parsec.Rendering.Gpu.csproj`. When adding a new shader file, you must add an `<EmbeddedResource>` entry to that csproj or it won't be found at runtime.

## Architecture

Seven projects in the solution, layered bottom-up:

- **Parsec.Core** -- pure math: IFS transforms, attractors, geometry primitives. No GPU or UI dependencies.
- **Parsec.Rendering** -- CPU-side rendering abstractions: `Camera3D`, raymarching settings, `BinaryFixed` (arbitrary-precision for deep zoom), `ReferenceOrbit`, `ImageOutput` (PNG via SkiaSharp). Depends on Core.
- **Parsec.Rendering.Gpu** -- OpenGL 4.3 compute shader pipeline. Each fractal has a `Gpu*Renderer` class + a `*_core.glsl` shader (distance estimator). `RaymarchPipeline` owns shared SSBOs and the clear/finalize shaders; renderers own only their per-fractal compute shader. `DeepZoomPipeline` handles 2D perturbation rendering. `ShaderLoader` reads shaders from embedded resources and strips non-ASCII (NVIDIA's GLSL compiler rejects it). Depends on Core, Rendering.
- **Parsec.Rendering.Metal** -- macOS-only Metal compute backend. All 20 fp32 3D fractals have a `Metal*Renderer` class that compiles the corresponding `*_raymarch.metal` embedded resource at startup via SharpMetal 1.1.0, dispatches an 8×8 threadgroup compute pass, and returns packed RGBA8 `uint[]`. Kernels emit HDR `float4` (no in-shader RGBA8 pack); `MetalSsaa.AccumulateHdr` accumulates in float space; `MetalPostProcess.Apply` runs the grade pass (brightness → contrast → optional tanh → Rec.601 saturation → gamma → RGBA8 pack). `MetalSsaa.Accumulate` (old RGBA8 path) is retained for `MetalDeepZoomRenderer` only. Exposes `LastComputeMs`/`LastReadbackMs` for frame-time diagnostics. Depends on Core, Rendering, Rendering.Gpu (for `IThreeDimensionalRenderBackend`).
- **Parsec.Audio** -- audio playback and analysis. `AudioTransportController` is the backend-neutral playback facade. Currently OpenAL + managed WAV decode only (`OpenALAudioPlaybackSession`). Feature extraction types (`AudioFeatureFrame`, `AudioFeatureTrack`, `IAudioAnalyzer`) exist for the audio-reactive pipeline. Depends on OpenTK.Audio.OpenAL.
- **Parsec.App** -- Avalonia desktop UI. `FractalView` (OpenGL control) dispatches to the active renderer. `MainWindow` orchestrates timeline, playback, hero renders, animation export. `AudioTransportPanel` is the audio UI. No MVVM framework -- controls are built in code-behind.
- **Parsec.Cli** -- headless CLI with IFS rendering examples. Separate entry point.

### Key patterns

**Fractal state classes** (`MandelboxState`, `KifsState`, etc.) hold mutable parameters as public fields. Each exposes `ToParams()` (converts to a GPU-facing struct) and `BuildSchema()` (returns a `ParamSchema` -- a list of `ParamDescriptor` objects with getter/setter closures over the live fields). The UI's `ParameterPanel` renders sliders from the schema generically.

**Shared visual state**: `PaletteState`, `LightState`, `ReflectionState` are cross-fractal and live on `FractalView` alongside all per-fractal state objects.

**Animation**: `Timeline` and `KeyframeBank` operate over `ParamDescriptor` lists, not fractal-specific types. Keyframe interpolation is generic.

**Shader composition**: `ShaderLoader.LoadComposite(coreFile, entryFile)` concatenates a `*_core.glsl` (the DE function) with `raymarch_main.glsl` (the shared marcher), prepending `#version 430 core`. Individual shader files must not contain a `#version` directive.

**OpenGL context**: `Program.cs` explicitly requests OpenGL 4.3 core on Windows (WGL) and GLX on Linux. macOS has a startup guard (`MacDisplayPreflight`) for Avalonia's native render-timer/display availability issue.

### GPU requirements

The existing Windows/Linux backend requires OpenGL 4.3+ with compute shaders. The 3D fp32 raymarchers use compute shaders and SSBOs; the 2D deep-zoom pipeline additionally depends on fp64/floatexp shader paths and should not block the macOS 3D-only milestone.

## Current project direction

The macOS-native 3D-only build is underway. Milestones 1–10 are complete:

- **Milestone 1–2 (done):** repository audit, `IThreeDimensionalRenderBackend` seam added to `Parsec.Rendering.Gpu`.
- **Milestone 3 (done):** `MetalMandelboxRenderer` with full MSL compute kernel (`mandelbox_raymarch.metal`). Manual port of `mandelbox_core.glsl` + `raymarch_main.glsl`. Renders correctly; 5 ms GPU compute at 640×480 on Apple Silicon.
- **Milestone 4A (done):** `MetalMandelboxRenderer` wired into `FractalView`. Selecting Mandelbox on macOS uses Metal automatically; all other fractals and non-macOS platforms use the OpenGL path unchanged.
- **Milestone 4B (done):** per-phase timing in the status bar — `compute N ms · readback N ms · upload N ms · total N ms`. CPU readback from unified memory is <1 ms.
- **Milestone 5 (done):** `TexImage2D` upload measured: 0 ms at 640×480/1280×720, 1 ms at 1920×1080. Decision: stay with current `TexImage2D` path. Also fixed: macOS GL 4.1 cap (`glDispatchCompute` optional, compute pipeline skipped on macOS, blit shaders at `#version 330`, Avalonia Metal UI renderer crash on HDMI dummy plugs).
- **Milestone 6 (done):** All priority fp32 3D shaders ported to Metal. Mandelbox (5 ms), Mandelbulb (7 ms), RotBox (6 ms), KIFS (5 ms), Kleinian (23 ms — numerical-gradient DE), Hybrid (11 ms). All wired into `FractalView` and validated via CLI smoke tests.
- **Milestone 7 (done):** All remaining 14 fp32 3D fractals ported to Metal: BurningShip, Menger, QuaternionJulia, QJBox, Apollonian, Bicomplex, Phoenix, Biomorph, Mosely, PseudoKleinian4D, RiemannSphere, Mandalay, Anisotropic, OrbitHybrid. AmazingBox routes through MetalMandelboxRenderer (Mode=1). All 20 fractals now render on macOS via Metal. CLI: `metal-new-smoke` validates all 14. Key MSL gotcha: global `const` variables at program scope cause silent shader failure — see `skills.md`.
- **Milestone 8 (done):** In-app 16× SSAA for hero stills. `MetalSsaa.cs` added — shared `Accumulate(n, w, h, Func<Vector2, uint[]>)` helper runs N Halton(2,3)-jittered samples, accumulates per-channel as float, averages, and repacks to RGBA8. All 20 Metal renderers updated to call `MetalSsaa.Accumulate(settings.HeroSamples, ...)`. The UI `HeroSamplesSelector` ComboBox (1/4/9/16×) now takes effect on macOS. CLI morph path in `MetalMandelbulbRenderer` preserved — explicit non-zero jitter bypasses the loop.
- **Milestone 9 (done):** `Parsec.App` added to `Parsec.sln`. `dotnet build Parsec.sln` now builds the full solution including the desktop app.
- **Milestone 10 (done):** 2D deep zoom on macOS via `MetalDeepZoomRenderer` (`deepzoom_metal.metal`). Float-float (Dekker double-double) arithmetic in MSL gives ~48-bit precision, supporting zoom to ~1e-12 depth. Supports all four formulas (Mandelbrot, Prospector, Julia, Burning Ship) with direct path (radius > 1e-6) and perturbation path (radius ≤ 1e-6) using the existing CPU `ReferenceOrbit`. Uses `MetalSsaa.Accumulate` for hero SSAA. CLI: `metal-deepzoom-mp4` renders a Seahorse Valley zoom video. **Precision note:** float-float reaches ~1e-12; the OpenGL floatexp path reaches 1e-147 — not identical at extreme depths but beyond any practical need.
- **Milestone 12 (done):** HDR post-processing pipeline. All 20 3D Metal kernels emit HDR `float4` (no in-kernel RGBA8 pack). `MetalSsaa.AccumulateHdr` accumulates in float space; old `Accumulate` retained for `MetalDeepZoomRenderer`. `MetalPostProcess.cs` (lazy-compiled PSO) applies Mandelbulber2-style grade: brightness → contrast → optional tanh tone map → Rec.601 luma saturation → gamma → RGBA8. `PostProcessParams` public struct (identity default), `PostProcessState` (4 schema sliders, macOS-only). `FractalView` wires `PostProcess.ToParams()` into all Metal render call sites. CLI: `metal-m12-stills`.

## Current Milestone

Milestones 1–10 and 12 are complete. All 20 fp32 3D fractals and the 2D deep-zoom pipeline render via Metal on macOS, with HDR grade post-processing.

**Remaining macOS parity work:**
- **M11:** Packaging and notarization.
- **Attractor has no Metal renderer.** It's the one selectable 3D fractal (`FractalType.Attractor`) with no `Metal*Renderer` — `HasMetalPreviewRenderer()` returns false for it, so on macOS it shows a dark placeholder + "preview unavailable" status. It's not a closed-form DE: `GpuAttractorRenderer` sphere-traces a prebuilt `AttractorHash` (trajectory + spatial hash) over SSBOs at bindings 6/7/8, so a Metal port needs those data buffers, not just a kernel translation. The "all 20 fp32 3D fractals render via Metal" phrasing counts the 20 ported renderers and excludes Attractor. (Octonion/Triball/IFS have OpenGL renderers but are not in the `FractalType` dropdown.)

**Audio-reactive feature (audio → visuals; new — not in upstream):**
The upstream `zoomacroom-games/Parsec` has no audio features. This is entirely new work. Development started in the `fractal_audio` fork at `~/projects/fractal_audio`.
- **Audio Phase 1–2 (done):** `Parsec.Audio` project ported here (transport, WAV decode, offline RMS/FFT analysis, AudioFeatureFrame/Track), `AudioTransportPanel`, MainWindow wiring. OpenAL playback re-enabled on macOS via Homebrew openal-soft `OverridePath`.
- **Audio Phase 3–4 (done):** in-app audio-reactive modulation. `AudioModulationController` samples WAV features per frame and applies them as additive offsets to live fractal/camera/palette `ParamDescriptor`s; `AudioMappingPanel` (mapping editor: feature source → target + depth + smoothing) is wired into `MainWindow` via `AudioMappingHost`. `metal-audio-reactive` CLI command renders the same offline.
- **Audio Phase 5 (done):** deterministic export — `AudioModulationController.ApplyAtTime(t)` mirrors timeline sampling at export timestamps, with ffmpeg audio mux on the Render-to-Video path.

**Fractal sonification feature (geometry → audio; NEW, requested 2026-06-09):**
The *inverse* of the audio-reactive system above: generate sound *from* the 3D fractal geometry (the camera is the listener; the fractal is a spatial resonant object). A Metal telemetry pass reduces the DE march into a `FractalSonicFrame` (hit ratio, depth/step stats, normal/trap variance, camera & parameter velocity); a new C# DSP layer synthesizes PCM; OpenAL (or later AVAudioEngine) only places/spatializes the sound. New namespace `Parsec.Audio.Sonification`; new type `FractalSonicFrame` (must not collide with the existing `AudioFeatureFrame`). Offline-first (deterministic WAV + ffmpeg mux), then live streaming, then spatial emitters. Plan and milestones M0–M6 in `docs/fractal-sonification-plan.md`. **Feedback-loop hazard:** sonification (geometry→audio) and reactive modulation (audio→params) must be mutually exclusive, or the sonifier must read base/keyframe geometry rather than audio-modulated state.
- **M0 (done):** `FractalSonicFrame` immutable `record class` in `Parsec.Audio.Sonification`. Solution builds; no impact on existing code.
- **M1 (done):** `SonificationController` in `Parsec.App` computes `CameraSpeed` and `ParameterVelocity` from live camera position and param descriptors each frame. Debug suffix ` · son: camSpd=N.NN pVel=N.NNNN` appended to status bar. Geometry fields are stubs (zero) until M2.

See `skills.md` for Metal porting recipes and gotchas. See `docs/macos-3d-only-build-plan.md` for the macOS render track and `docs/fractal-sonification-plan.md` for the sonification feature.

## Deferred

- polished packaging, notarization, and installer work (M11)

The audio-reactive in-app UI (mapping panel + timeline/export integration) is now built, not deferred. Fractal sonification (geometry → audio) is now requested — see `docs/fractal-sonification-plan.md`. Do not expand audio work beyond what is explicitly requested.

## Technical Strategy

- **Metal backend:** SharpMetal 1.1.0 (net9-compatible) is in use. Types are value-type structs — see `skills.md` for SharpMetal-specific gotchas.
- **OpenGL preservation:** `Parsec.Rendering.Gpu` is the existing backend. Do not rewrite `RaymarchPipeline`, `ComputeShader`, or `Gpu*Renderer` classes.
- **Backend seam:** `IThreeDimensionalRenderBackend` in `Parsec.Rendering.Gpu` is the dispatch interface. `FractalView` uses a `when` guard on the switch arm to route Mandelbox to Metal; all other fractals fall through to OpenGL.
- **Shader translation:** SPIRV-Cross was not used. The Mandelbox shader was manually ported to MSL (`mandelbox_raymarch.metal`). For additional shaders, use the Mandelbox MSL as the template — see `skills.md`.
- **Avalonia presentation:** Metal renders offscreen into `uint[]`, uploaded into the existing GL texture via `TexImage2D`. CPU readback from unified memory is <1 ms. If GL upload proves too slow, the next step is `CAMetalLayer`.
- **Deep zoom:** `MetalDeepZoomRenderer` (`deepzoom_metal.metal`) implements Dekker float-float arithmetic in MSL, giving ~48-bit precision (~1e-12 max depth). The OpenGL path uses fp64/floatexp to reach 1e-147; the Metal path is not bit-identical at extreme depths but covers all practical use. The CPU `ReferenceOrbit` (BigInteger) and `DeepZoomView` are shared with the OpenGL path unchanged.
- **Post-processing (M12, planned):** Change the Metal output contract from in-kernel RGBA8 pack to an HDR float target, accumulate HDR in `MetalSsaa`, and add a final `postprocess.metal` grade pass. The grade is applied at the float→display step (not baked into the render), so adjusting it re-runs only the post pass, not the fractal. Reference: Mandelbulber2 `cImage::CalculatePixel` / `CompileImage` (GPL-3.0 — design inspiration only, no code copied); Fragmentarium buffer-shader two-pass structure. This is the one part of M12 that touches all 20 shaders (the shared shading tail), so treat it as a deliberate contract change, not a per-fractal port.

## Do Not Do Yet

- Sonification synthesis is now in scope (the DSP layer in `docs/fractal-sonification-plan.md` M3), but build it as a backend-neutral PCM synth layer driven by `FractalSonicFrame` telemetry — not a general-purpose music synth, and not on the UI/render or audio thread (see plan §2 real-time audio safety).
- Do not break or repurpose the existing audio-reactive modulation (`AudioModulationController`/`AudioMappingPanel`); sonification is a separate, mutually-exclusive mode.
- Do not refactor renderer internals broadly. Prefer the separate low-res telemetry kernel (plan §3 Option B) over modifying the still-stabilizing render kernels.
- Do not change Windows/Linux OpenGL behavior unless the change is required by a narrow backend seam and can be validated.

`AGENTS.md` holds the fuller engineering rules and context-loading order. `docs/macos-3d-only-build-plan.md` is the current implementation plan.
