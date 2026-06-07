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

Six projects in the solution, layered bottom-up:

- **Parsec.Core** -- pure math: IFS transforms, attractors, geometry primitives. No GPU or UI dependencies.
- **Parsec.Rendering** -- CPU-side rendering abstractions: `Camera3D`, raymarching settings, `BinaryFixed` (arbitrary-precision for deep zoom), `ReferenceOrbit`, `ImageOutput` (PNG via SkiaSharp). Depends on Core.
- **Parsec.Rendering.Gpu** -- OpenGL 4.3 compute shader pipeline. Each fractal has a `Gpu*Renderer` class + a `*_core.glsl` shader (distance estimator). `RaymarchPipeline` owns shared SSBOs and the clear/finalize shaders; renderers own only their per-fractal compute shader. `DeepZoomPipeline` handles 2D perturbation rendering. `ShaderLoader` reads shaders from embedded resources and strips non-ASCII (NVIDIA's GLSL compiler rejects it). Depends on Core, Rendering.
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

The current priority is a focused **macOS-native 3D-only build**. The first milestone is not feature parity. It is to get one existing fp32 3D raymarched fractal rendering natively on macOS through a Metal backend while preserving the current OpenGL backend for Windows/Linux.

Treat the current OpenGL compute renderer as the source implementation. Study it, mirror its data flow where practical, and avoid destabilizing it. The initial target should be one representative 3D shader, preferably Mandelbox, because `GpuMandelboxRenderer` has a compact parameter pack and uses the shared `RaymarchPipeline` shape without deep-zoom fp64 requirements.

## Current Milestone

Build a one-fractal Metal spike:

- add a small backend abstraction at the 3D raymarch output boundary, not a full renderer rewrite
- port or translate `mandelbox_core.glsl` plus the needed `raymarch_main.glsl` path to Metal Shading Language
- run the Metal compute path offscreen into a packed RGBA8 buffer
- display that buffer in Avalonia by the simplest working path first
- measure preview performance before adding a native Metal view or porting more shaders

Success means a usable macOS build can show one fp32 3D fractal interactively enough to validate the backend, shader path, and presentation path.

## Deferred

- audio reactivity and audio-driven modulation
- deep-zoom parity on macOS
- fp64 shader support or double-float deep-zoom redesign
- full parity across all 3D shaders
- polished packaging, notarization, and installer work
- synth/audio-generation features

Existing audio branch code can remain, but do not expand it until the macOS 3D renderer milestone is working.

## Technical Strategy

- **Metal backend:** prefer a native Metal compute backend for macOS. SharpMetal is the most direct C# candidate to spike because its NuGet package is net9-compatible; do not add it until the backend shape is decided.
- **OpenGL preservation:** keep `Parsec.Rendering.Gpu` as the existing backend. Do not rewrite `RaymarchPipeline`, `ComputeShader`, or every `Gpu*Renderer` just to make room for Metal.
- **Backend seam:** start near the existing `uint[] RenderToBuffer(...)` / packed RGBA8 output contract. A minimal interface such as a 3D render backend that accepts typed Mandelbox parameters, camera, raymarch settings, palette, lighting, and dimensions is likely enough for the first spike.
- **Shader translation:** evaluate GLSL -> SPIR-V -> MSL via SPIRV-Cross against one composite shader only. If bindings, std430 layout, workgroup sizes, image writes, barriers, or math semantics become brittle, manually port Mandelbox to MSL for the spike and document the differences.
- **Avalonia presentation:** first render offscreen and display the resulting pixels through the existing UI path or a simple bitmap path. If CPU readback/upload is too slow for interactive previews, the next milestone is a Metal-backed/native-view presentation path.
- **Deep zoom:** do not chase Metal deep zoom now. Metal's shader model does not support the existing fp64 path in the way OpenGL does, so hide, stub, or disable deep zoom for the first macOS build if needed.

## Do Not Do Yet

- Do not resume audio-reactive feature work.
- Do not build a synth engine.
- Do not port every shader before Mandelbox proves the backend.
- Do not implement macOS deep-zoom parity in the first milestone.
- Do not refactor renderer internals broadly before the one-fractal Metal spike.
- Do not change Windows/Linux OpenGL behavior unless the change is required by a narrow backend seam and can be validated.
- Do not over-engineer Avalonia integration before measuring the simplest offscreen display path.

`AGENTS.md` holds the fuller engineering rules and context-loading order. `docs/macos-3d-only-build-plan.md` is the current implementation plan.
