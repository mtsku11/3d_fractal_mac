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

### 6. Port remaining fp32 3D shaders (in progress)

Goal: expand only after Mandelbox proves the stack.

**Mandelbulb ✓ COMPLETE** — `mandelbulb_raymarch.metal` + `MetalMandelbulbRenderer`. Key difference from Mandelbox: log-space derivative accumulation (no helper folds), `atan2` instead of `atan(y,x)`, camera at `(0,0,4)` not `(0,3,12)`. Measured: 7 ms GPU compute at 640×480. CLI: `metal-bulb-smoke [w] [h]`.

**RotBox ✓ COMPLETE** — `rotbox_raymarch.metal` + `MetalRotBoxRenderer`. Standard Mandelbox + per-iteration Euler rotation (`z = R * z` before folds). Key differences: Euler angles in `surfParams.xyz` (not `rot.xyz`); `boxParams` order `(scale, minRadius, fixedRadius, foldLimit)` differs from Mandelbox `(scale, foldingLimit, minRadius, fixedRadius)`. Measured: 6 ms GPU compute at 640×480. CLI: `metal-rotbox-smoke [w] [h]`.

Remaining fp32 3D shaders to port (candidates in rough priority order based on parameter pack complexity):
- GpuMandelbulbRenderer ✓ done
- GpuRotBoxRenderer ✓ done
- GpuKifsRenderer
- GpuKleinianRenderer
- GpuHybridRenderer
- others as needed

Expected files touched per port:

- `src/Parsec.Rendering.Metal/Shaders/<fractal>_raymarch.metal`
- `src/Parsec.Rendering.Metal/Metal<Fractal>Renderer.cs`
- `src/Parsec.Rendering.Metal/Parsec.Rendering.Metal.csproj` (EmbeddedResource)
- `src/Parsec.App/FractalView.cs` (field, init, switch arm, status, dispose)
- `src/Parsec.Cli/Program.cs` (smoke command)

Acceptance criteria:

- each port has visual smoke validation (`metal-*-smoke` CLI command)
- parameter packing differences are documented
- no deep-zoom parity implied

### 7. Package and notarize later

Goal: macOS distribution polish after renderer functionality exists.

Expected files touched:

- packaging scripts
- entitlements/signing/notarization files

Acceptance criteria:

- deferred until the app has a useful macOS 3D render path

## Explicit Non-Goals For First Milestone

- audio-reactive visuals
- synthesizer or visual-to-sound work
- 2D deep zoom on Metal
- fp64 shader parity
- all 3D shader ports
- native Metal presentation before offscreen display is measured
- installer, notarization, or release packaging

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
