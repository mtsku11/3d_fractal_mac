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
- **Parsec.Rendering.Metal** -- macOS-only Metal compute backend. All 20 fp32 3D fractals have a `Metal*Renderer` class that compiles the corresponding `*_raymarch.metal` embedded resource at startup via SharpMetal 1.1.0, dispatches an 8×8 threadgroup compute pass, and returns packed RGBA8 `uint[]` matching the OpenGL backend's output contract. `MetalSsaa.cs` provides a shared Halton-jittered accumulation helper used by every renderer to honour `settings.HeroSamples` for hero still SSAA (up to 16×). Exposes `LastComputeMs`/`LastReadbackMs` for frame-time diagnostics. Depends on Core, Rendering, Rendering.Gpu (for `IThreeDimensionalRenderBackend`).
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

The macOS-native 3D-only build is underway. Milestones 1–7 are complete:

- **Milestone 1–2 (done):** repository audit, `IThreeDimensionalRenderBackend` seam added to `Parsec.Rendering.Gpu`.
- **Milestone 3 (done):** `MetalMandelboxRenderer` with full MSL compute kernel (`mandelbox_raymarch.metal`). Manual port of `mandelbox_core.glsl` + `raymarch_main.glsl`. Renders correctly; 5 ms GPU compute at 640×480 on Apple Silicon.
- **Milestone 4A (done):** `MetalMandelboxRenderer` wired into `FractalView`. Selecting Mandelbox on macOS uses Metal automatically; all other fractals and non-macOS platforms use the OpenGL path unchanged.
- **Milestone 4B (done):** per-phase timing in the status bar — `compute N ms · readback N ms · upload N ms · total N ms`. CPU readback from unified memory is <1 ms.
- **Milestone 5 (done):** `TexImage2D` upload measured: 0 ms at 640×480/1280×720, 1 ms at 1920×1080. Decision: stay with current `TexImage2D` path. Also fixed: macOS GL 4.1 cap (`glDispatchCompute` optional, compute pipeline skipped on macOS, blit shaders at `#version 330`, Avalonia Metal UI renderer crash on HDMI dummy plugs).
- **Milestone 6 (done):** All priority fp32 3D shaders ported to Metal. Mandelbox (5 ms), Mandelbulb (7 ms), RotBox (6 ms), KIFS (5 ms), Kleinian (23 ms — numerical-gradient DE), Hybrid (11 ms). All wired into `FractalView` and validated via CLI smoke tests.
- **Milestone 7 (done):** All remaining 14 fp32 3D fractals ported to Metal: BurningShip, Menger, QuaternionJulia, QJBox, Apollonian, Bicomplex, Phoenix, Biomorph, Mosely, PseudoKleinian4D, RiemannSphere, Mandalay, Anisotropic, OrbitHybrid. AmazingBox routes through MetalMandelboxRenderer (Mode=1). All 20 fractals now render on macOS via Metal. CLI: `metal-new-smoke` validates all 14. Key MSL gotcha: global `const` variables at program scope cause silent shader failure — see `skills.md`.
- **Milestone 8 (done):** In-app 16× SSAA for hero stills. `MetalSsaa.cs` added — shared `Accumulate(n, w, h, Func<Vector2, uint[]>)` helper runs N Halton(2,3)-jittered samples, accumulates per-channel as float, averages, and repacks to RGBA8. All 20 Metal renderers updated to call `MetalSsaa.Accumulate(settings.HeroSamples, ...)`. The UI `HeroSamplesSelector` ComboBox (1/4/9/16×) now takes effect on macOS. CLI morph path in `MetalMandelbulbRenderer` preserved — explicit non-zero jitter bypasses the loop.

## Current Milestone

Milestones 1–8 are complete. All 20 fp32 3D fractals render via Metal on macOS with full hero-still SSAA support. Remaining deferred work: audio-reactive features, packaging/notarization.

See `skills.md` for Metal porting recipes and gotchas. See `docs/macos-3d-only-build-plan.md` for the full milestone breakdown.

## Deferred

- audio reactivity and audio-driven modulation
- deep-zoom parity on macOS
- fp64 shader support or double-float deep-zoom redesign
- polished packaging, notarization, and installer work
- synth/audio-generation features

Existing audio branch code can remain, but do not expand it until explicitly requested.

## Technical Strategy

- **Metal backend:** SharpMetal 1.1.0 (net9-compatible) is in use. Types are value-type structs — see `skills.md` for SharpMetal-specific gotchas.
- **OpenGL preservation:** `Parsec.Rendering.Gpu` is the existing backend. Do not rewrite `RaymarchPipeline`, `ComputeShader`, or `Gpu*Renderer` classes.
- **Backend seam:** `IThreeDimensionalRenderBackend` in `Parsec.Rendering.Gpu` is the dispatch interface. `FractalView` uses a `when` guard on the switch arm to route Mandelbox to Metal; all other fractals fall through to OpenGL.
- **Shader translation:** SPIRV-Cross was not used. The Mandelbox shader was manually ported to MSL (`mandelbox_raymarch.metal`). For additional shaders, use the Mandelbox MSL as the template — see `skills.md`.
- **Avalonia presentation:** Metal renders offscreen into `uint[]`, uploaded into the existing GL texture via `TexImage2D`. CPU readback from unified memory is <1 ms. If GL upload proves too slow, the next step is `CAMetalLayer`.
- **Deep zoom:** disabled on macOS 3D-only path. Metal's shader model does not support the existing fp64 path.

## Do Not Do Yet

- Do not resume audio-reactive feature work.
- Do not build a synth engine.
- Do not resume audio-reactive feature work until explicitly requested.
- Do not implement macOS deep-zoom parity in the first milestone.
- Do not refactor renderer internals broadly before the one-fractal Metal spike.
- Do not change Windows/Linux OpenGL behavior unless the change is required by a narrow backend seam and can be validated.
- Do not over-engineer Avalonia integration before measuring the simplest offscreen display path.

`AGENTS.md` holds the fuller engineering rules and context-loading order. `docs/macos-3d-only-build-plan.md` is the current implementation plan.
