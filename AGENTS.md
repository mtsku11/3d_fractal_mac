# Parsec Agent Guide

Parsec is a C#/.NET 9 Avalonia desktop application for GPU fractal exploration and rendering. The current branch direction is a macOS-native 3D-only build: get one existing fp32 3D raymarched fractal running through a native Metal backend first, while preserving the current OpenGL backend for Windows/Linux. Audio-reactive visuals, deep-zoom parity, and full shader parity are deferred.

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
- `src/Parsec.Rendering.Gpu/DeepZoomPipeline.cs`: OpenGL fp64/floatexp 2D deep-zoom path; defer for macOS first milestone.
- `src/Parsec.Rendering/Output/ImageOutput.cs`: PNG export helper.
- `docs/macos-3d-only-build-plan.md`: current macOS 3D-only implementation plan.
- `docs/audio-reactive/`: deferred audio-reactive planning and implementation history.

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

Non-goals (still deferred):

- audio-reactive feature expansion
- synth engine
- deep-zoom parity on macOS
- full shader parity before the upload-path decision is made
- `CAMetalLayer` presentation unless GL upload proves too slow

## Context-Loading Order

1. `AGENTS.md`
2. `README.md`
3. `CLAUDE.md`
4. `skills.md`
5. `docs/macos-3d-only-build-plan.md`
6. `docs/audio-reactive/00-repo-inspection.md` only if touching deferred audio code
