# macOS 3D-Only Build Plan

## Direction

The current priority is a macOS-native 3D-only build. The first milestone is one existing fp32 3D raymarched fractal rendered through Metal and shown in Avalonia. Deep zoom, audio reactivity, full shader parity, and packaging polish are later work.

The existing OpenGL compute renderer is the source implementation. The Metal work should add a parallel backend path, not destabilize `Parsec.Rendering.Gpu`.

## Repository Audit

Renderer entry points:

- `src/Parsec.App/FractalView.cs`: Avalonia `OpenGlControlBase`, active fractal state, render dispatch, preview texture upload, hero render, and animation frame rendering.
- `src/Parsec.Rendering.Gpu/RaymarchPipeline.cs`: shared OpenGL compute path for fp32 3D raymarchers. It owns SSBOs, clear/finalize compute shaders, AA accumulation, tiled dispatch, and packed RGBA8 download.
- `src/Parsec.Rendering.Gpu/GpuMandelboxRenderer.cs`: per-fractal Mandelbox renderer. It packs `MandelboxParams` into `FoldParamsGpu`, loads `mandelbox_core.glsl` plus `raymarch_main.glsl`, and delegates to `RaymarchPipeline`.
- `src/Parsec.Rendering.Gpu/GpuMandelbulbRenderer.cs`: similarly compact fp32 renderer, useful as a second candidate after Mandelbox.
- `src/Parsec.Rendering.Gpu/GpuRenders.cs`: headless sample render helpers for CLI-style GPU renders.

OpenGL-specific classes:

- `src/Parsec.Rendering.Gpu/Gl.cs`: OpenGL proc-address wrapper, including compute dispatch, buffers, textures, framebuffer, draw, and shader compilation helpers.
- `src/Parsec.Rendering.Gpu/GlConst.cs`: OpenGL constants.
- `src/Parsec.Rendering.Gpu/ComputeShader.cs`: GLSL compute shader compile/link/dispatch wrapper.
- `src/Parsec.Rendering.Gpu/StorageBuffer.cs`: typed SSBO upload/download/binding wrapper.
- `src/Parsec.Rendering.Gpu/RaymarchPipeline.cs`: OpenGL SSBO and compute-dispatch orchestration.
- `src/Parsec.Rendering.Gpu/DeepZoomPipeline.cs`: OpenGL fp64/floatexp 2D deep-zoom pipeline. This is explicitly out of scope for the first macOS build.
- `src/Parsec.Rendering.Gpu/HeadlessGLContext.cs`: OpenTK hidden-window GL context for headless OpenGL probes.

Shader files:

- `src/Parsec.Rendering.Gpu/Shaders/mandelbox_core.glsl`: best first Metal candidate. It is fp32, compact, stackless, and uses one parameter block at binding 1.
- `src/Parsec.Rendering.Gpu/Shaders/raymarch_main.glsl`: shared raymarch/shading entry point. The Metal spike needs a subset or port of this path.
- `src/Parsec.Rendering.Gpu/Shaders/mandelbulb_core.glsl`: good second candidate after Mandelbox because the parameter pack is even smaller but the math uses spherical powers/trig.
- `src/Parsec.Rendering.Gpu/Shaders/deepzoom_delta.glsl`, `deepzoom_delta_fe.glsl`, and `deepzoom_color.glsl`: defer; they are for the 2D deep-zoom pipeline.

UI and presentation layer:

- `src/Parsec.App/MainWindow.axaml`: hosts `<app:FractalView Name="FractalView" />` as the main visual surface.
- `src/Parsec.App/FractalView.cs`: currently computes into a `uint[]`, uploads it into an OpenGL texture with `TexImage2D`, and blits that texture to the Avalonia framebuffer with a small GL vertex/fragment shader.
- `src/Parsec.App/MacDisplayPreflight.cs`: catches headless macOS display states before Avalonia initializes. This is separate from the Metal backend work.

Current output contract:

- Interactive preview path: `RenderToBuffer(...)` returns packed RGBA8 as `uint[]`.
- Hero/export path: `Render(...)` wraps packed RGBA8 into `SKBitmap`, then `ImageOutput.SavePng(...)` writes PNGs.

That packed RGBA8 output is the lowest-risk first backend seam.

## Backend Abstraction Seam

Do not start with `IGpuBuffer`, `IComputePipeline`, and a full cross-API renderer model. The existing code is not structured that way, and forcing it now would touch too much.

Start with a small 3D render-output interface, for example:

```csharp
public interface IThreeDimensionalRenderBackend : IDisposable
{
    bool IsAvailable { get; }
    uint[] RenderMandelbox(
        MandelboxParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette);
}
```

This is intentionally narrow:

- it proves backend selection without abstracting every GPU primitive
- it maps to the existing `RenderToBuffer(...)` return type
- it lets `FractalView` keep the same presentation path for the first spike
- it avoids changing all `Gpu*Renderer` classes before Metal is proven

After the first Metal render works, extract common request/response types such as `RaymarchRenderRequest`, `PackedRgbaImage`, or a `RenderTarget` wrapper if duplication becomes real.

## One-Shader Metal Spike

Recommended first shader: Mandelbox.

Reasons:

- fp32 only
- no deep-zoom fp64 dependency
- compact parameter shape in `MandelboxParams`
- one OpenGL renderer class and one distance-estimator shader
- representative use of the shared raymarch path, SSBO-style parameter blocks, tiled dispatch, accumulation, and packed output

Candidate C# binding:

- SharpMetal: its NuGet metadata currently shows net9 compatibility and broad Metal API coverage, making it the first binding to spike. Do not add the dependency until the backend project shape is ready.
- Veldrid Metal bindings are older and likely less direct for a focused native Metal compute spike.
- A native Objective-C/Swift shim remains an option if C# bindings are too thin, but that increases build complexity.

Suggested project shape:

- Keep `src/Parsec.Rendering.Gpu` as the OpenGL backend.
- Add a separate macOS-only backend project only when implementation starts, likely `src/Parsec.Rendering.Metal`.
- Keep shared renderer-neutral request/parameter types in `src/Parsec.Rendering` or a very small abstraction file if needed.

## Shader Translation Plan ✓ RESOLVED

SPIRV-Cross was not used — manual port was chosen because resource layout differences and struct alignment were easier to control by hand. See `skills.md` for the full recipe.

Resolved issues from the spike:

- **SSBO layout vs Metal buffer alignment:** `[StructLayout(Sequential, Pack=1)]` with `System.Numerics.Vector4` satisfies MSL 16-byte `float4` alignment as long as the int fields before the first Vector4 sum to a multiple of 16 (4 ints = 16 bytes ✓). No explicit padding needed for the current structs.
- **Workgroup size:** `layout(local_size_x = 8, local_size_y = 8)` maps directly to `MTLSize { width=8, height=8, depth=1 }` threadgroup size; threadgroup count is `ceil(w/8) × ceil(h/8)`.
- **Output strategy:** buffer writes. `device uint* output [[buffer(2)]]`, direct RGBA8 pack at end of kernel. No texture writes needed.
- **Barriers/synchronization:** not needed. Metal command buffer ordering (`cmd.Commit(); cmd.WaitUntilCompleted()`) serializes compute and readback.
- **Vector/matrix constructors:** identical between GLSL and MSL — both use column-major `float3x3(col0, col1, col2)`.
- **RGBA8 byte order:** `(255u << 24) | (b << 16) | (g << 8) | r` — little-endian, alpha is high byte, matches `SKColorType.Rgba8888`.
- **Precision:** no observable differences in fp32 math between GLSL and MSL at preview quality.
- **Intrinsics:** all used GLSL intrinsics (`clamp`, `mix`, `smoothstep`, `reflect`, `normalize`, `dot`, `cross`, `length`, `sign`, `abs`, `max`, `min`, `mod`, `floor`, `fract`, `pow`, `exp`, `sqrt`) have direct MSL equivalents with identical signatures.

## Avalonia Display Path

First path:

- Metal computes offscreen into a CPU-readable packed RGBA8 buffer.
- `FractalView` or a small presentation adapter displays the resulting pixels using the simplest available Avalonia path.
- Measure frame time for preview dimensions before changing the UI surface.

Likely first display choices:

- reuse the current packed `uint[]` path and upload into the existing GL texture where OpenGL is available
- or display via an Avalonia/Skia bitmap surface if the app is running without the OpenGL control

If readback/upload is too slow:

- move to a native Metal-backed view or `CAMetalLayer` host
- keep that as the next milestone, not a prerequisite for proving the Mandelbox compute kernel

## Milestones

### 1. Repository audit ✓ COMPLETE

Goal: complete and keep this plan current.

### 2. Backend abstraction seam ✓ COMPLETE

`IThreeDimensionalRenderBackend` added to `Parsec.Rendering.Gpu`. `src/Parsec.Rendering.Metal/` project created with SharpMetal 1.1.0 reference. OpenGL Mandelbox renders unchanged.

### 3. One-shader Metal spike ✓ COMPLETE

`mandelbox_raymarch.metal` — full manual MSL port of `mandelbox_core.glsl` + `raymarch_main.glsl`. `MetalMandelboxRenderer` compiles it at startup via SharpMetal, dispatches 8×8 threadgroup compute pass, returns `uint[]` RGBA8. CLI `metal-smoke` command verifies headlessly. 5 ms GPU compute at 640×480 on Apple Silicon; 51,223 non-background pixels at that size.

### 4. Avalonia display path ✓ COMPLETE

**4A:** `MetalMandelboxRenderer` wired into `FractalView`. On macOS, Mandelbox automatically routes to Metal via a `when _metalRenderer?.IsAvailable == true` guard in the `ActiveType switch`. Packed `uint[]` uploads into the existing GL texture via `TexImage2D` — no change to the blit path.

**4B:** Per-phase timing exposed. `MetalMandelboxRenderer.LastComputeMs` / `LastReadbackMs` set internally after `WaitUntilCompleted` and `MemoryCopy`. `FractalView` adds `TexImage2D` and total-frame stopwatches. Status bar shows: `compute N ms · readback N ms · upload N ms · total N ms`.

### 5. Performance measurement ✓ COMPLETE

**Measured on Apple Silicon M4 Pro (Release build):**

| Size | Compute | Readback | TexImage2D upload | Total |
|------|---------|----------|-------------------|-------|
| 640×480 | 5 ms | 0 ms | 0 ms | ~5 ms |
| 1280×720 | 7 ms | 0 ms | 0 ms | ~7 ms |
| 1920×1080 | 12 ms | 1 ms | 1 ms | ~14 ms |

**Decision:** `TexImage2D` upload is negligible on unified memory. The current Metal→CPU readback→`TexImage2D` presentation path is acceptable for interactive preview at all sizes. `CAMetalLayer` is not needed.

**Findings from Milestone 5 work (bugs fixed):**
- macOS caps OpenGL at 4.1 — `glDispatchCompute`/`glMemoryBarrier` are absent. Fixed in `Gl.cs` (optional load via `TryLoad`, `SupportsCompute` property). `RaymarchPipeline` and all `Gpu*Renderer` construction now skipped on macOS in `FractalView.OnOpenGlInit`.
- Blit shaders lowered from `#version 430 core` to `#version 330 core` — the shaders use no 4.3 features and the macOS GL 4.1 driver rejected the higher version directive.
- Avalonia's Metal UI renderer crashes on HDMI dummy plugs (`gr_backendrendertarget_new_metal` null drawable). Fixed by adding `AvaloniaNativePlatformOptions { RenderingMode = [OpenGl, Software] }` in `Program.cs`.

### 6. Port remaining fp32 3D shaders ✓ COMPLETE

Goal: expand only after Mandelbox proves the stack.

**Mandelbulb ✓** — `mandelbulb_raymarch.metal` + `MetalMandelbulbRenderer`. Log-space derivative accumulation, `atan2`. 7 ms at 640×480.
**RotBox ✓** — `rotbox_raymarch.metal` + `MetalRotBoxRenderer`. Mandelbox + per-iteration Euler rotation. 6 ms at 640×480.
**KIFS ✓** — `kifs_raymarch.metal` + `MetalKifsRenderer`. Pre/post rotation, sphere fold, pivot scale. 5 ms at 640×480.
**Kleinian ✓** — `kleinian_raymarch.metal` + `MetalKleinianRenderer`. Numerical-gradient DE (7 potential calls/estimate). 23 ms at 640×480.
**Hybrid ✓** — `hybrid_raymarch.metal` + `MetalHybridRenderer`. Mandelbox + Mandelbulb per-iteration, `atan2` fix. 11 ms at 640×480.

### 7. Port all remaining fp32 3D fractals ✓ COMPLETE

All 14 remaining fractals ported to Metal (commit b96f21c). All 20 fp32 3D fractals now render on macOS via Metal. AmazingBox reuses MetalMandelboxRenderer (Mode=1).

**BurningShip ✓** — same FoldParams layout as Mandelbulb; abs() fold after power step; y-up spherical convention.
**Menger ✓** — sort-based IFS (largest component to z); Euler rotation each iteration; box SDF at exit.
**QuaternionJulia ✓** — 4D quaternion iteration; flat or stereographic slice; optional half-cut plane; stereo mode sets BoundSphere.w = 1e6f to disable fast-skip.
**QJBox ✓** — Mandelbox fold + quaternion-square hybrid; 4D z; cut-axis flag in rot.z.
**Apollonian ✓** — inversive inversion through 5 spheres; logScale accumulation; gTrap set after each inversion.
**Bicomplex ✓** — tessarine square with per-axis mul/add scalings; Hubbard-Douady DE; half-cut flag in rot.y.
**Phoenix ✓** — Mandelbulb-style square + memory term p_mem × z_{n-1}; two-step derivative tracking.
**Biomorph ✓** — Mandelbulb-style square; L∞ (componentwise) escape; optional half-cut.
**Mosely ✓** — [111]-frame IFS; twist rotation; wedge kaleidoscope fold; exact box SDF / dz.
**PseudoKleinian4D ✓** — 4D z (w0 slice); box fold + one-sided sphere inversion; slab-tube DE.
**RiemannSphere ✓** — stereographic projection; sine-fold; variable-exponent radial power; approx scalar-dr DE.
**Mandalay ✓** — darkbeam fold (SDF min/max per axis); parallel or sequential mode; |z|/dr DE.
**Anisotropic ✓** — delta-DE: 4 orbits per pixel, finite-difference Jacobian; Frobenius or sigma_max norm.
**OrbitHybrid ✓** — KIFS + Mandelbox schedule; shared sphere fold; kifsCount/mboxCount packed in mode/juliaMode slots.

**Key MSL gotcha discovered:** global `const` variables at program scope (scalar or vector) cause silent shader compilation failure — `NewFunction` returns nil even though `NewLibrary` appears to succeed. All shaders must use local variables or inline literals. Documented in `skills.md`.

CLI validation: `parsec metal-new-smoke` — all 14/14 pass.

Files touched per port: `Shaders/<fractal>_raymarch.metal`, `Metal<Fractal>Renderer.cs`, `Parsec.Rendering.Metal.csproj`, `FractalView.cs` (field/init/switch/status/dispose/RenderWithMetal*), `Program.cs` (smoke command).

### 8. In-app 16× SSAA for hero stills ✓ COMPLETE

Goal: honour `HeroSampleCount` (1/4/9/16×) on the Metal path, matching what `RaymarchPipeline` already does on OpenGL.

**Implementation:** `MetalSsaa.cs` — a single shared static helper:
- `Accumulate(int sampleCount, int width, int height, Func<Vector2, uint[]> renderOneSample)` — calls the lambda N times with Halton(2,3)-jittered sub-pixel offsets, accumulates R/G/B channels as `float[]`, averages, repacks to RGBA8 `uint[]`. When `sampleCount == 1` the lambda is called once with `Vector2.Zero` — identical behaviour to before.
- `HaltonJitter(int sampleIndex)` — Halton(2,3) quasi-random sequence in `[-0.5, 0.5]²`.

All 20 Metal renderers updated to call `MetalSsaa.Accumulate(settings.HeroSamples, ...)` in their public `RenderX()` entry point. The `settings.HeroSamples` value is set by `FractalView.HeroSettings()` from `HeroSampleCount`, which is already bound to the `HeroSamplesSelector` ComboBox in the UI — no UI changes needed.

Apple Silicon unified memory means N GPU round-trips cost negligible extra transfer time (readback is a pointer copy, <1 ms per pass). The bottleneck remains GPU compute, which scales linearly with N.

**CLI morph path preserved:** `MetalMandelbulbRenderer.RenderMandelbulb` retains its `Vector2 subpixelJitter = default` overload. Non-zero explicit jitter (the CLI's RGSS loop) goes directly to `DispatchOneSample` and bypasses the internal SSAA loop. In-app calls arrive with no jitter, so the SSAA loop fires normally.

Files touched: `MetalSsaa.cs` (new), all 20 `Metal*Renderer.cs` files.
Commit: 9bc7a2b.

### 9. Fix solution file — add Parsec.App to Parsec.sln

`Parsec.App` is not listed in `Parsec.sln`. `dotnet build Parsec.sln` silently skips the desktop app — only `dotnet build src/Parsec.App/Parsec.App.csproj` builds it.

Files touched: `Parsec.sln` (add `Parsec.App` project entry).

Acceptance criteria: `dotnet build Parsec.sln` compiles the full solution including the desktop app.

### 10. Port Parsec.Audio project

`Parsec.Audio` is entirely absent from the Mac rewrite. The original contains:

- `AudioTransportController.cs` — backend-neutral playback facade
- `IAudioPlaybackBackend.cs` / `IAudioPlaybackSession.cs` — OpenAL abstraction seam
- `OpenALAudioPlaybackBackend.cs` / `OpenALAudioPlaybackSession.cs` — OpenAL + WAV decode backend
- `UnavailableAudioPlaybackBackend.cs` — stub for platforms without audio
- `WavePcmData.cs` / `WavePcmDecoder.cs` — managed WAV decode
- `AudioFeatureFrame.cs` / `AudioFeatureTrack.cs` — feature data structures
- `IAudioAnalyzer.cs` / `WaveAudioAnalyzer.cs` — offline RMS/FFT analysis
- `AudioAnalysisOptions.cs`, `AudioPlaybackStatus.cs`, `AudioTransportState.cs`

Files touched: new `src/Parsec.Audio/` directory and `Parsec.Audio.csproj`, `Parsec.sln`, `src/Parsec.App/Parsec.App.csproj` (add project reference).

Acceptance criteria: `dotnet build` succeeds; `parsec audio-analyze /path/to/file.wav 500` headless CLI command works.

### 11. Restore audio transport UI

Three concrete things are missing from the Mac rewrite versus the original:

1. `src/Parsec.App/AudioTransportPanel.cs` — the file/play/pause/seek control panel
2. `src/Parsec.App/MainWindow.axaml` — missing `<ContentControl Name="AudioHost" Margin="0,12,0,0" />`
3. `src/Parsec.App/MainWindow.axaml.cs` — missing `_audioHost` / `_audioTransport` fields, their init in the constructor, and `OnWindowClosed` async disposal

Files touched: `AudioTransportPanel.cs` (new), `MainWindow.axaml`, `MainWindow.axaml.cs`.

Acceptance criteria: app launches, audio host panel is visible, user can load and play a WAV file; existing fractal rendering is unaffected when audio is idle.

### 12. Audio-reactive modulation (Phase 3 of original roadmap)

Drive existing visual parameters from audio feature values (RMS, band energy). This phase is not started in either repo — it requires new files:

- `src/Parsec.Audio/ModulationMapping.cs` — feature → parameter descriptor target + depth
- `src/Parsec.Audio/AudioReactiveController.cs` — per-frame sampling and application to live state

Files touched: above new files, `FractalView.cs` (apply modulation before render), `MainWindow.axaml.cs` (connect controller to transport clock).

Acceptance criteria: at least one feature (RMS) drives at least one parameter; modulation can be toggled; timeline playback still works when modulation is active.

### 13. Audio mapping UI (Phase 4 of original roadmap)

Expose a minimal mapping editor: feature source, parameter target, depth slider, enable toggle.

Files touched: `MainWindow.axaml`, `MainWindow.axaml.cs`.

### 14. Timeline + export integration (Phase 5 of original roadmap)

Exported frames must use deterministic feature sampling at the same timestamps as live playback. Generated ffmpeg mux instructions must account for audio attachment.

Files touched: `AudioFeatureTrack.cs`, `AudioReactiveController.cs`, `MainWindow.axaml.cs`, `FractalView.cs`.

### 15. Deep zoom on macOS

`DeepZoomPipeline` requires OpenGL 4.3 compute shaders and fp64 paths — neither is available on macOS. The `_deepPipeline` field is null on macOS; selecting `FractalType.DeepZoom` produces no output.

Options (in ascending cost):
- **A (minimal):** hide or disable the Deep Zoom entry in the fractal selector on macOS; show a clear "not available on macOS" message.
- **B (fp32 approximation):** port the Mandelbrot/Julia perturbation renderer as a Metal compute kernel using fp32 arithmetic — loses precision below ~1e-7 zoom depth but works for shallow exploration.
- **C (full parity):** implement floatexp (mantissa + int exponent) arithmetic in MSL — significant shader complexity, restores deep zoom parity with the Windows/Linux path.

Acceptance criteria (option A, minimum viable): macOS users get a clear message instead of a black frame when Deep Zoom is selected.

### 16. Package and notarize

Goal: macOS distribution polish after renderer functionality exists.

Expected files touched: packaging scripts, entitlements/signing/notarization files.

Acceptance criteria: deferred until the app has a useful macOS 3D render path.

## Explicit Non-Goals For First Milestone

- audio-reactive visuals (now tracked as M12–M14)
- synthesizer or visual-to-sound work
- 2D deep zoom on Metal (tracked as M15)
- fp64 shader parity
- all 3D shader ports (now done — M7)
- native Metal presentation before offscreen display is measured
- installer, notarization, or release packaging (tracked as M16)

## Major Risks And Unknowns

- The app currently derives from `OpenGlControlBase`, so a true Metal UI path may need a different Avalonia/native-view host after the offscreen spike.
- The current shared raymarch pipeline is GL-specific down to SSBO bindings, dispatch, barriers, and `TexImage2D` presentation.
- Shader translation may fail on resource layout or MSL semantics even if the GLSL is valid OpenGL compute code.
- Metal does not support the existing deep-zoom shader double path, so macOS 3D-only mode must avoid presenting deep zoom as supported.
- CPU readback from Metal plus Avalonia bitmap upload may be too slow for interactive previews, requiring a later native Metal presentation path.

## References Checked

- SharpMetal NuGet metadata: https://www.nuget.org/packages/SharpMetal/
- SPIRV-Cross project: https://github.com/KhronosGroup/SPIRV-Cross
- Apple Metal resources and specifications: https://developer.apple.com/metal/resources/
