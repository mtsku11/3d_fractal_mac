# Parsec Agent Guide

Parsec is a C#/.NET 9 Avalonia desktop application for GPU fractal exploration and rendering. The current branch direction is a macOS-native build: all 20 fp32 3D fractals and the 2D deep-zoom pipeline now render via Metal on macOS, while preserving the OpenGL backend for Windows/Linux. The audio-reactive feature (audio → visuals) is built. The fractal-sonification feature (geometry → audio) is built through M9 (direct-orbit synthesis with per-fractal profiles) — see `docs/fractal-sonification-plan.md`.

## Key Commands

- `git branch --show-current`
- `git remote -v`
- `dotnet build`
- `dotnet build src/Parsec.App/Parsec.App.csproj`
- `dotnet run --project src/Parsec.App/Parsec.App.csproj -c Release`
- `dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release`
- `dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -- audio-analyze /path/to/file.wav 500`
- `brew install dotnet@9` if `dotnet` is missing on macOS

Note: every project targets `net9.0`. If `dotnet` is unavailable, fix the SDK environment before treating build failures as project failures.

## Important Paths

- `Parsec.sln`: solution entry for the desktop app, audio seam, CLI, and rendering projects.
- `src/Parsec.App/Program.cs`: Avalonia desktop entry point.
- `src/Parsec.App/MacDisplayPreflight.cs`: macOS startup guard for the Avalonia native render-timer/display availability issue.
- `src/Parsec.App/MainWindow.axaml.cs`: top-level app orchestration, timeline I/O, hero render, animation export.
- `src/Parsec.App/FractalView.cs`: OpenGL view, renderer dispatch, active fractal state, export rendering loop.
- `src/Parsec.App/ParamSchema.cs`: generic parameter descriptor model shared by UI and animation.
- `src/Parsec.App/Timeline.cs`: keyframe interpolation and timeline serialization.
- `src/Parsec.Rendering.Gpu/RaymarchPipeline.cs`: shared OpenGL compute raymarch pipeline for fp32 3D fractals.
- `src/Parsec.Rendering.Gpu/GpuMandelboxRenderer.cs`: source renderer for the Metal spike; reference for porting other shaders.
- `src/Parsec.Rendering.Gpu/Shaders/mandelbox_core.glsl`: source distance-estimator shader.
- `src/Parsec.Rendering.Gpu/Shaders/raymarch_main.glsl`: shared raymarch/shading compute entry path.
- `src/Parsec.Rendering.Metal/MetalMandelboxRenderer.cs`: Metal compute backend for Mandelbox. Template for porting other fp32 3D shaders.
- `src/Parsec.Rendering.Metal/Shaders/mandelbox_raymarch.metal`: MSL compute kernel; reference for future MSL ports.
- `src/Parsec.Rendering.Metal/MetalMandelbulbRenderer.cs`: Metal compute backend for Mandelbulb. Second example of the porting pattern.
- `src/Parsec.Rendering.Metal/Shaders/mandelbulb_raymarch.metal`: MSL Mandelbulb kernel. Identical shading/raytrace skeleton to Mandelbox; only the DE section differs.
- `src/Parsec.Rendering.Gpu/DeepZoomPipeline.cs`: OpenGL fp64/floatexp 2D deep-zoom path (Windows/Linux only).
- `src/Parsec.Rendering.Metal/MetalDeepZoomRenderer.cs`: Metal 2D deep-zoom backend. Dekker float-float arithmetic in `deepzoom_metal.metal`; all 4 formulas; direct + perturbation paths. Shares `ReferenceOrbit` and `DeepZoomView` with the OpenGL path.
- `src/Parsec.Rendering/Output/ImageOutput.cs`: PNG export helper.
- `docs/macos-3d-only-build-plan.md`: current macOS 3D-only implementation plan.
- `docs/fractal-sonification-plan.md`: implementation plan + milestone log for the geometry → audio sonification feature (M0–M9 and post-M9 refinements, all done). File pointers and Metal-kernel claims were verified against the code on 2026-06-09.
- `src/Parsec.App/AudioModulationController.cs` / `AudioMappingPanel.cs`: existing audio-reactive modulation (audio → visuals). The sonification work must not break these and runs as a mutually-exclusive mode.
- `src/Parsec.App/FlyCamera.cs`: camera state; source for sonification `CameraSpeed`.
- `src/Parsec.Rendering.Metal/Shaders/mandelbox_raymarch.metal`: `estimateFull` writes the float4 orbit trap; `traceRay` holds the step loop. The sonification telemetry pass reduces data this kernel currently discards.

## Engineering Rules

- Build before and after changes when the local environment has a usable .NET 9 SDK.
- Keep the OpenGL renderer working while adding any macOS backend seam.
- Prefer a one-fractal Metal spike over broad renderer abstraction.
- Start from fp32 3D raymarchers; do not let deep zoom block the first macOS build.
- Preserve renderer, UI, timeline, and deferred audio concerns as separate areas.
- Do not implement a synthesizer engine or new audio-reactive architecture in this phase.
- Do not port all shaders until Mandelbox or another single representative fractal proves backend, presentation, and shader translation.
- Keep feature work on the current feature branch or a smaller child branch off it.

## Coding Style Observations

- The app keeps mutable per-fractal state in dedicated `*State` classes with `ToParams()` and `BuildSchema()` methods.
- UI controls are assembled directly in Avalonia code-behind and lightweight view classes rather than through a large MVVM framework.
- Animation logic is generic over `ParamDescriptor` lists, not fractal-specific timeline types.
- Comments tend to explain intent and constraints, especially around GL context use and numerical/rendering tradeoffs.

## Current macOS 3D-Only Scope

Milestones 1–5 complete. Metal Mandelbox and Metal Mandelbulb render on macOS. Measured: Mandelbox 5 ms GPU compute, Mandelbulb 7 ms GPU compute, <1 ms readback, <1 ms TexImage2D upload at all preview sizes on M4 Pro. Decision: keep the TexImage2D presentation path; no CAMetalLayer needed.

Key macOS-specific constraints now handled in the codebase:
- macOS GL caps at 4.1 — `Gl.SupportsCompute` is false; `RaymarchPipeline` and `Gpu*Renderer` are not constructed on macOS
- Blit shaders at `#version 330 core` (was 430, caused compile failure on macOS driver)
- `AvaloniaNativePlatformOptions { RenderingMode = [OpenGl, Software] }` prevents Avalonia Metal UI crash on dummy plugs

Milestone 6 complete: all priority fp32 3D shaders ported to Metal. Mandelbox (5 ms), Mandelbulb (7 ms), RotBox (6 ms), KIFS (5 ms), Kleinian (23 ms — numerical-gradient DE), Hybrid (11 ms). All wired into `FractalView`; CLI smoke tests pass. See `skills.md` for porting recipe and per-fractal parameter notes.

Milestone 7 (shader parity) complete: all remaining 14 fp32 3D fractals ported to Metal on macOS:
BurningShip, Menger, QuaternionJulia, QJBox, Apollonian, Bicomplex, Phoenix, Biomorph, Mosely,
PseudoKleinian4D, RiemannSphere, Mandalay, Anisotropic, OrbitHybrid. AmazingBox routes through
the existing MetalMandelboxRenderer (Mode=1). All 20 fractals now render on macOS via Metal.
CLI smoke test: `parsec metal-new-smoke` verifies all 14 new renderers.
Key Metal gotcha discovered: global `const` variables at program scope (even scalars) cause silent
shader compilation failure — use local constants or inline literals instead.

Milestone 8 (in-app SSAA) complete: `MetalSsaa.cs` added with Halton(2,3) accumulation loop. All
20 Metal renderers now honour `settings.HeroSamples` (1/4/9/16×) set from the UI HeroSamplesSelector
ComboBox. CPU accumulation on unified memory is free — N round-trips cost <1 ms extra. CLI morph path
in `MetalMandelbulbRenderer` preserved via explicit-jitter bypass.

Completed macOS parity milestones (this session):

- **M9 (done):** `Parsec.App` added to `Parsec.sln`. `dotnet build Parsec.sln` now builds the full solution.
- **M10 (done):** 2D deep zoom on macOS via `MetalDeepZoomRenderer`. Dekker float-float in MSL; ~48-bit precision; zoom to ~1e-12. All 4 formulas. CLI: `metal-deepzoom-mp4`.

Remaining macOS parity milestones:

- **M11:** Package and notarize.
- **Attractor is not ported to Metal.** It is the only selectable 3D fractal (`FractalType.Attractor`) without a `Metal*Renderer`; on macOS it renders a blank placeholder. `GpuAttractorRenderer` sphere-traces a prebuilt `AttractorHash` over SSBOs (bindings 6/7/8), so porting it needs those buffers, not just an MSL DE. "All 20 fractals on Metal" excludes it. Octonion/Triball/IFS (`Gpu{Octonion,Triball,Raymarching}Renderer`) have OpenGL renderers but are not exposed in the desktop dropdown.

Audio-reactive feature — audio → visuals (new work, not in upstream `zoomacroom-games/Parsec`): **done.**

- **Audio Phase 1–2 (done):** `Parsec.Audio` project + UI wiring (WAV playback, offline RMS/FFT analysis, `AudioTransportPanel`, `MainWindow` wiring).
- **Audio Phase 3–4 (done):** `AudioModulationController` maps feature values to live fractal/camera/palette params; `AudioMappingPanel` is the in-app mapping editor, wired via `AudioMappingHost`.
- **Audio Phase 5 (done):** deterministic export via `ApplyAtTime(t)` + ffmpeg audio mux on Render-to-Video.
- **Transparent export (done):** render panel `Transparent BG` keys the dark render background out of hero PNGs and animation PNG frames via `ImageOutput.SavePng(..., transparentBackground: true)`. Render-to-Video outputs MP4/H.264 normally, but transparent mode outputs MOV/ProRes 4444 (`prores_ks`, `yuva444p10le`). This is export-time matte keying, not native shader alpha.

Fractal sonification — geometry → audio (requested 2026-06-09): the inverse pipeline. Metal telemetry pass → `FractalSonicFrame` → C# DSP synth → OpenAL spatialization. **M0–M9 plus post-M9 refinements are all done** (2026-06-09 → 2026-06-12): telemetry kernels for Mandelbox/Mandelbulb/Kleinian/BurningShip, hybrid wavetable+bell synthesis, live OpenAL streaming, direct-orbit synthesis (the iteration map as oscillator), continuous Hybrid↔DirectOrbit blend slider, proximity/enclosure/morph macros, fold-event chimes, and per-fractal `DirectOrbitProfile`s with geometry-derived lattice ratios. The detailed milestone log lives in `CLAUDE.md` ("Fractal sonification feature") and `docs/fractal-sonification-plan.md` §5; DSP recipes and gotchas in `skills.md`. Build only what is explicitly requested; keep it mutually exclusive with the reactive modulation above to avoid a fractal→sound→params→fractal feedback loop.

Non-goals (still deferred indefinitely):

- synth engine
- `CAMetalLayer` presentation unless GL upload proves too slow

## Context-Loading Order

1. `AGENTS.md`
2. `README.md`
3. `CLAUDE.md`
4. `skills.md`
5. `docs/macos-3d-only-build-plan.md`
6. `docs/fractal-sonification-plan.md` if touching the geometry → audio sonification feature
7. `docs/audio-reactive/00-repo-inspection.md` only if touching audio-reactive (audio → visuals) code
