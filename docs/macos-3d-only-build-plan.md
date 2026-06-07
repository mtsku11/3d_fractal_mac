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

## Shader Translation Plan

First evaluate automated translation against a single composite shader:

1. Produce the same composite GLSL source that `ShaderLoader.LoadComposite("mandelbox_core.glsl", "raymarch_main.glsl")` uses.
2. Try GLSL -> SPIR-V -> MSL with `glslangValidator` and SPIRV-Cross.
3. Inspect resource bindings and generated MSL before wiring it into runtime code.
4. If translation is brittle, manually port Mandelbox plus the necessary raymarch path to MSL.

Issues to document during the spike:

- `layout(std430, binding = N)` SSBO layout versus Metal buffer indices and struct alignment
- workgroup size mapping from `layout(local_size_x = 8, local_size_y = 8)` to Metal threadgroup sizing
- output strategy: buffer writes first, texture writes later only if needed
- barriers and synchronization differences between OpenGL `MemoryBarrier` and Metal command-buffer ordering
- vector/matrix constructor ordering and matrix multiplication semantics
- `uint` packed RGBA8 byte order
- differences in precision assumptions and fast-math behavior
- availability of helper intrinsics used by the GLSL path

SPIRV-Cross is relevant because the upstream project describes MSL output support, but Parsec should not assume the generated code is production-ready until Mandelbox compiles and renders correctly.

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

### 1. Repository audit

Goal: complete and keep this plan current.

Expected files touched:

- `CLAUDE.md`
- `AGENTS.md`
- `docs/macos-3d-only-build-plan.md`

Acceptance criteria:

- future agents clearly see macOS 3D-only as the current priority
- first shader candidate and backend seam are identified
- deferred items are explicit

### 2. Backend abstraction seam

Goal: add the smallest 3D render-output abstraction needed to select OpenGL or Metal.

Expected files touched:

- `src/Parsec.Rendering/` for shared request/output contracts if needed
- `src/Parsec.App/FractalView.cs` for selection and dispatch
- optionally a new `src/Parsec.Rendering.Metal/` project

Acceptance criteria:

- OpenGL Mandelbox behavior still builds and renders
- no broad changes to every renderer
- Deep Zoom remains OpenGL-only or disabled on macOS 3D-only mode

### 3. One-shader Metal spike

Goal: render Mandelbox through Metal into packed RGBA8.

Expected files touched:

- new Metal backend project/files
- first `.metal` shader or generated MSL artifact
- solution/project references

Acceptance criteria:

- Mandelbox renders at a fixed preview size on macOS
- CPU-side parameters match existing Mandelbox defaults closely enough for visual comparison
- no attempt to port all 3D shaders yet

### 4. Avalonia display path

Goal: show the Metal output in the app.

Expected files touched:

- `src/Parsec.App/FractalView.cs`
- possibly a new macOS presentation adapter

Acceptance criteria:

- app shows the Metal Mandelbox path on macOS
- OpenGL path remains available where supported
- UI does not expose unsupported deep-zoom assumptions in the macOS 3D-only path

### 5. Performance measurement

Goal: determine whether offscreen readback/display is acceptable for interactive preview.

Expected files touched:

- lightweight timing/logging in the Metal backend or `FractalView`
- docs update with measured frame times

Acceptance criteria:

- preview frame time is measured at representative sizes
- next presentation step is chosen from data

### 6. Port remaining fp32 3D shaders

Goal: expand only after Mandelbox proves the stack.

Expected files touched:

- additional MSL ports or translation artifacts
- per-fractal backend methods
- backend-selection UI/dispatch as needed

Acceptance criteria:

- each port has visual smoke validation
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
