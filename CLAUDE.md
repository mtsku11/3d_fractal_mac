# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **History lives in the plan docs, not here.** Completed-milestone detail, DSP specs, and hard-won gotchas are in `docs/macos-3d-only-build-plan.md`, `docs/fractal-sonification-plan.md`, `docs/midi-output-plan.md`, and `skills.md` (Metal porting recipes + gotchas). This file is the operating contract — what you need to work in the repo *now*. `AGENTS.md` holds the fuller engineering rules and context-loading order.

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

## Current state

The macOS-native 3D-only build is the active track. **All 20 fp32 3D fractals and the 2D deep-zoom pipeline render via Metal on macOS, with HDR grade post-processing** (Milestones 1–10 and 12 complete; full milestone detail in `docs/macos-3d-only-build-plan.md`). Two audio features and a MIDI-output feature are built on top of the renderer (see *Audio & MIDI features* below).

> **The authoritative to-do for a *completely finished* app is `docs/app-completion-plan.md`** (phased roadmap, audited 2026-06-13): Phase 1 finish domain warp (Metal shadow/AO must march the warped DE), Phase 2 unified in-app Texture Source (None/Image/Video/MandelbrotZoom/Feedback), Phase 3 wire the Projection ComboBox + roll orbit trap beyond BurningShip, Phase 4 HDR-toggle + UI gaps, Phase 5 Attractor port-or-cut, Phase 6 golden-frame regression, Phase 7 M11 packaging. The items below are the per-feature detail.

### Known limitations / active work

- **Attractor has no Metal renderer.** It's the one selectable 3D fractal (`FractalType.Attractor`) with no `Metal*Renderer` — on macOS it shows a dark placeholder + "preview unavailable". It's not a closed-form DE: `GpuAttractorRenderer` sphere-traces a prebuilt `AttractorHash` (trajectory + spatial hash) over SSBOs at bindings 6/7/8, so a Metal port needs those data buffers, not just a kernel translation. "All 20 render via Metal" counts the 20 ported renderers and excludes Attractor. (Octonion/Triball/IFS have OpenGL renderers but aren't in the `FractalType` dropdown.)
- **Surface texture — two modes.** The `SURFACE TEXTURE` block decodes an image on CPU and routes it to both backends. **Mode 0 Triplanar:** object-space, normal-weighted, stable under camera motion — all 20 Metal shaders + OpenGL via `MetalSurfaceTextureShaderInjector.Inject()`. **Mode 1 Orbit trap:** the DE is re-run at each hit point, `z.xy` at the min-distance step is the UV (normalised by bailout) — deforms with geometry; BurningShip only, via `InjectOrbitTrap()`. Mode is encoded into `background.w` (0=off, 1=triplanar, 2=orbit trap). The `SurfaceTextureProjectionSelector` ComboBox is **not yet wired** to the mode param. Extending orbit trap = ~10-line `estimateFull` change per shader + `Inject`→`InjectOrbitTrap`. See `docs/surface-texture-handoff.md`.
- **Domain warp — experimental.** The `DOMAIN WARP` block bends sample coordinates before DE evaluation via a clamped (`Strength ≤ 0.75`) nested sine/cosine field; encoded into the unused `.z/.w` lanes of `subpixelJitter` so the render-param layout is unchanged. Metal applies it through `InjectDomainWarp()`. Next: Metal normals should sample the derivative of the *warped* DE rather than only the warped hit point; add time/image-driven warp source; capture golden frames once Metal readback is reliable on-host. Deep Zoom + Attractor are disabled for this effect.
- **Metal buffer seam hardening — in progress.** `MetalBufferIO` centralizes shared-buffer allocation, upload, and readback diagnostics; main preview paths route through it and report a precise "no CPU-accessible contents" error instead of null-pointer crashes. Remaining: migrate the telemetry/readback helpers, then a real SharpMetal/macOS mapping fix rather than per-renderer patching.
- **M11:** packaging and notarization (deferred).

### Audio & MIDI features (built on the renderer)

Three systems that read from the renderer. Audio-reactive, sonification, and MIDI are **mutually exclusive** — running geometry→audio/MIDI together with audio→visuals creates a feedback loop (the sonifier/emitter must read base/keyframe geometry, not audio-modulated state).

- **Audio-reactive (audio → visuals).** `AudioModulationController` samples WAV features per frame and applies them as additive offsets to live fractal/camera/palette `ParamDescriptor`s; `AudioMappingPanel` (mapping editor) and deterministic export (`ApplyAtTime`) are built. Entirely new vs. upstream `zoomacroom-games/Parsec`.
- **Fractal sonification (geometry → audio).** The inverse: a Metal telemetry pass reduces the DE march into a `FractalSonicFrame`; a C# DSP layer (`Parsec.Audio.Sonification`) synthesizes PCM. Complete through **M0–M9e + Track D** — full direct-orbit pipeline, live streaming with a continuous Hybrid↔DirectOrbit blend, Freeverb reverb, hardened audio thread, proximity/enclosure/morph macros, fold-event chimes, and per-fractal `DirectOrbitProfile`s with geometry-derived lattice ratios. **8 of 20 fractals sonify** (Mandelbox, Mandelbulb, Kleinian, BurningShip, Menger, Apollonian, KIFS, QJBox). Full spec, gotchas, and next-phase plan in `docs/fractal-sonification-plan.md`.
- **MIDI output (geometry → MIDI).** Emits MIDI to external DAWs/VSTs (virtual CoreMIDI source "Parsec") so the fractal is an A/V control surface; namespace `Parsec.Audio.Midi`. M1–M4 done + on `main`: 15 continuous CCs (20–34), gesture notes (60/62/64/65/67/69/71), 16 spatial object-notes (36–51), all channel 1. Planned-improvement progress (**all done + verified cross-process**): real on-screen colour → CC24 hue/CC35 sat/CC36 bright; responsiveness/smoothing slider; full-res energy centroid → CC30/31/32; MIDI-only telemetry for all fractals (**20/20**, only Attractor excluded: no Metal renderer); 2b finer 8×6 region grid on ch2 (notes 36–83); M3b editable mapping panel (`MidiMappingConfig` + `MidiMappingPanel`: per-signal enable/CC#/channel/range/invert + per-note-group channel). Batch-3 telemetry kernels were generated by `tools/build_midi_telemetry.py` + `tools/add_telemetry_pass.py`; validators: `metal-midi-telemetry`, `midi-map-check`, `midi-smoke`/`midi-monitor`. Spec in `docs/midi-output-plan.md`. **Remaining MIDI-adjacent work: a Metal renderer for the Attractor fractal** (no closed-form DE — needs the `AttractorHash` trajectory+spatial-hash ported to Metal SSBOs).

## Technical Strategy

- **Metal backend:** SharpMetal 1.1.0 (net9-compatible). Types are value-type structs — see `skills.md` for SharpMetal-specific gotchas.
- **OpenGL preservation:** `Parsec.Rendering.Gpu` is the existing backend. Do not rewrite `RaymarchPipeline`, `ComputeShader`, or `Gpu*Renderer` classes.
- **Backend seam:** `IThreeDimensionalRenderBackend` in `Parsec.Rendering.Gpu` is the dispatch interface. `FractalView` uses a `when` guard on the switch arm to route fractals to Metal; others fall through to OpenGL.
- **Shader translation:** SPIRV-Cross is not used. Shaders are manually ported to MSL — use `mandelbox_raymarch.metal` as the template (see `skills.md`).
- **Avalonia presentation:** Metal renders offscreen into `uint[]`, uploaded into the existing GL texture via `TexImage2D`; CPU readback from unified memory is <1 ms. If GL upload proves too slow, the next step is `CAMetalLayer`.
- **Deep zoom:** `MetalDeepZoomRenderer` (`deepzoom_metal.metal`) uses Dekker float-float arithmetic in MSL (~48-bit precision, ~1e-12 depth); the OpenGL fp64/floatexp path reaches 1e-147 — not bit-identical at extreme depths but covers all practical use. The CPU `ReferenceOrbit` (BigInteger) and `DeepZoomView` are shared with OpenGL unchanged.
- **HDR output contract:** all 20 3D Metal kernels emit HDR `float4`; `MetalSsaa.AccumulateHdr` accumulates in float; `MetalPostProcess` grades at the float→display step (not baked into the render), so adjusting the grade re-runs only the post pass. This shared shading tail touches all 20 shaders — treat changes to it as a deliberate contract change, not a per-fractal port.

## Do Not Do Yet

- Do not break or repurpose the audio-reactive modulation (`AudioModulationController`/`AudioMappingPanel`); sonification and MIDI output are separate, mutually-exclusive modes (feedback hazard).
- Do not refactor renderer internals broadly. Prefer the separate low-res telemetry kernel over modifying the still-stabilizing render kernels.
- Do not change Windows/Linux OpenGL behavior unless the change is required by a narrow backend seam and can be validated.
- Do not expand audio / sonification / MIDI scope beyond what is explicitly requested.
