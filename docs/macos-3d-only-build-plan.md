# macOS 3D-Only Build Plan

## Direction

> **For the path to a *completely finished* app, see `docs/app-completion-plan.md`** — the
> authoritative phased roadmap (audited 2026-06-13). This document remains the per-feature
> mechanics reference for the render track; the completion plan is the prioritized to-do.

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
- Transparent export option: the live preview still renders against the normal dark background, but
  `Transparent BG` on the render panel passes `transparentBackground: true` to hero and animation
  PNG export. `ImageOutput.SavePng(..., transparentBackground: true)` estimates the matte from the
  image corners and keys matching background pixels to alpha. Render-to-Video uses MOV/ProRes 4444
  (`prores_ks`, `yuva444p10le`) in transparent mode; normal mode remains MP4/H.264. This is an
  export-time matte key, not native hit-mask alpha from every shader.

That packed RGBA8 output is the lowest-risk first backend seam.

## Render Feature: Surface Texture Projection

The feature is implemented across both render backends used by the desktop app: Metal-backed 3D fractals on macOS and the shared OpenGL 3D path on Windows/Linux. A user-supplied bitmap can tint and texture the fractal surface through the existing preview, hero-still, and video-export paths.

**Status (2026-06-12): the Metal path is verified rendering.** `metal-surface-texture-smoke` produces baseline + textured Mandelbox PNGs on Apple M4 Pro — 12,958/76,800 px changed at blend 0.85, the test pattern visibly triplanar-projected; all 20 main shaders inject + compile (verified via a standalone `/tmp/metalprobe` sweep). Getting there required fixing two bugs in `MetalSurfaceTextureShaderInjector` (see "Bugs fixed" below). The OpenGL `gpu-surface-texture-smoke` is still unverified on this host (headless GL context stalls in launch services). Full handoff: `docs/surface-texture-handoff.md`; Metal gotchas: `skills.md`.

Why this shape:

- Most of the 3D fractals here do not have meaningful UVs, so classic mesh texturing does not apply.
- The lowest-risk path is to sample a 2D bitmap directly in the raymarch shading stage.
- The first useful mapping is **triplanar projection** in object/world space, because it survives arbitrary fractal geometry better than planar UV guesses.

Implemented in the first pass:

1. Added a compact render-state/UI block in `MainWindow` / `FractalView` with `Enable`, `Image`, `Blend`, `Scale`, and `Projection Mode`.
2. Added retained texture paths for both render backends: the selected image is decoded once on CPU, packed to tight RGBA, cached per Metal device in `MetalSurfaceTextureManager`, and bound into the shared OpenGL compute path via `GpuSurfaceTextureManager`.
3. Extended both shared raymarch shading tails with stable object-space **triplanar projection** and albedo blending.
4. Kept the feature inside the existing 3D render paths so interactive preview, hero stills, and animation export all use the same textured shading without changing the transparent-export matte path.
5. Added deterministic CLI A/B verification commands in `Parsec.Cli`: `metal-surface-texture-smoke` and `gpu-surface-texture-smoke`. They render baseline vs textured Mandelbox frames from the same image input, save PNGs, and print changed-pixel stats.
6. Added `MetalBufferIO` as a shared Metal seam for struct upload, float/uint uploads, shared-buffer allocation, and explicit readback diagnostics. Main render/postprocess/deep-zoom paths now fail with a precise “no CPU-accessible contents” error instead of null pointer crashes when the host cannot map `MTLBuffer.Contents`.

Bugs fixed (2026-06-12) — both in `MetalSurfaceTextureShaderInjector.Inject()`, together they aborted *all* Metal rendering at pipeline creation (every renderer ctor compiles its main shader), which masqueraded as a host outage and as a (false) "non-mappable `MTLBuffer.Contents` readback seam":

1. The signature `Replace` anchored on `"…FoldParams& fp, constant RenderParams& rp)"` matched both `traceRay` *and* `shadeDirect`, appending the texture param to both but patching only `traceRay`'s call site → `shadeDirect` 6-arg called with 5 → `no matching function`. Fix: anchor on `traceRay`'s unique `int maxSteps,` line.
2. `menger_raymarch.metal` calls `traceRay` with inline `rp.marchA.*`/`rp.marchI0` args (not the `hitEps/...` locals), so the call-site patch missed it. Fix: a Menger-specific call-site replacement.

Diagnosed with a standalone SharpMetal probe (`/tmp/metalprobe`) that compiled a trivial kernel (host fine) then the real injected shaders (printed the actual `program_source` compile error). `MTLBuffer.Contents` is mappable here — the smoke reads back its `uint[]` fine.

Still pending — **now tracked in `docs/app-completion-plan.md`** (audited 2026-06-13):

1. Golden regression frame → completion-plan **Phase 6** (`metal-golden`).
2. In-app preview eyeball + Blend/Scale defaults → folded into **Phase 2** (unified Texture Source).
3. `MetalBufferIO.RequireContents` rollout → effectively complete (all renderers route through
   `MetalBufferIO`; `.Contents` read directly only inside `MetalBufferIO.cs`).
4. Wire the `Projection` ComboBox to real modes (Triplanar / Orbit Trap) → completion-plan **Phase 3**;
   the ComboBox is currently a 1-item stub wired to a tooltip only, and orbit trap is BurningShip-only.
5. Headless OpenGL startup stall for `gpu-surface-texture-smoke` — unchanged, low priority.

The texture **feedback loop** is now an in-app feature (commit `d998ffe`), and a family of CLI
texture-source demos exist (`metal-cross-fractal-texture`, `metal-burning-video-texture`,
`metal-oracle`, `metal-closeup-hq`, `metal-closeup-oracle`). Completion-plan Phase 2 unifies these
CLI-proven sources into one in-app `SurfaceTextureSource` dropdown.

## Render Feature: Domain Warp

The first experimental domain-warp pass is implemented for the desktop 3D render path. It is designed to produce more abstract footage by bending the 3D sample coordinates before the distance estimator evaluates the fractal, instead of only changing surface colour after the hit.

Implemented (2026-06-13):

1. Added `DOMAIN WARP` controls in `MainWindow` / `FractalView`: `Enable`, `Strength`, and `Scale`.
2. Added `DomainWarpState`, which encodes warp strength/scale into the unused `.z/.w` lanes of `subpixelJitter`; no Metal/OpenGL render-param layout change required.
3. Added a clamped procedural nested sine/cosine warp field. Low scale bends broad masses; high scale creates denser tearing/striation.
4. OpenGL shared raymarch path applies the warp to primary marching, normal estimation, soft shadows, AO, and orbit-trap colour capture.
5. Metal raymarch path injects `domainWarp()` through `MetalSurfaceTextureShaderInjector`; first pass applies it to primary marching plus hit-point trap/normal sampling across the Metal 3D raymarchers, including the BurningShip orbit-trap texture mode.
6. Deep Zoom and Attractor are excluded because they do not use the same fp32 3D DE raymarch path.
7. Added `metal-domain-warp-mp4 [duration] [out.mp4] [w] [h]`, a deterministic CLI verifier that renders a Mandelbox domain-warp clip and reports baseline-vs-warp changed-pixel count on frame 0.

Verification:

- `dotnet build src/Parsec.App/Parsec.App.csproj --no-restore -m:1 /nodeReuse:false -v minimal` passes with the existing nullable warnings in `FractalView.cs`.
- `dotnet build src/Parsec.Cli/Parsec.Cli.csproj --no-restore -m:1 /nodeReuse:false -v minimal` passes cleanly.
- Sandboxed Metal commands can return a nil SharpMetal device wrapper (`NativePtr=0`, `RegistryID=0`), which then fails at `MTLBuffer.Contents`. Running the CLI outside the sandbox gives real Metal access: `metal-smoke 64 48` renders successfully.
- `dotnet src/Parsec.Cli/bin/Debug/net9.0/parsec.dll metal-domain-warp-mp4 4 artifacts/domain_warp/domain_warp_phone_bright_verify.mp4 720 406` renders a verified H.264 clip: 4.0s, 96 frames, 720x406, 12 MB. Baseline vs first warped frame: 290,686/292,320 pixels changed (99.44%). First-frame average luma (`signalstats.YAVG`) is 44.63, materially brighter than the earlier dark test render.

Next steps — **now tracked in `docs/app-completion-plan.md` Phase 1** (audited 2026-06-13):

1. **Commit the feature** — it is currently uncommitted in the working tree (builds clean).
2. **Make Metal shadow/AO march the warped DE** — the Metal injector today patches only the 3
   primary-march `estimate()` sites + `estimateNormal` (`MetalSurfaceTextureShaderInjector.cs:205–227`);
   the OpenGL path already warps shadow/AO (`raymarch_main.glsl:93,108`). This asymmetry is the main
   correctness gap.
3. Add a time/phase input for flowing animated warp.
4. Eyeball in-app preview on a real Mac; tune default `Strength` / `Scale`.
5. Golden frames → completion-plan **Phase 6**. Image/video-driven warp source → optional, after Phase 2.

Surface-texture first-pass constraints:

- No per-fractal UV authoring or unwrapping.
- No texture-driven displacement or geometry modification.
- No camera-space projection; the mapping must stay stable as the camera moves.
- Keep the existing lighting, HDR post-process, and transparent-export behaviour intact; the texture is an optional colour/detail layer, not a replacement shading model.

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

### 9. Fix solution file — add Parsec.App to Parsec.sln ✓ DONE

`Parsec.App` was not listed in `Parsec.sln`. Fixed by adding the project entry and configuration blocks.

Files touched: `Parsec.sln`. Commit: c09ce80.

### 10. Deep zoom on macOS ✓ DONE

`DeepZoomPipeline` requires OpenGL 4.3 + fp64, neither available on macOS. Replaced with `MetalDeepZoomRenderer` (`deepzoom_metal.metal`) using Dekker float-float (double-double) arithmetic in MSL.

**Implementation:**
- Float-float library in MSL: `ddAdd`, `ddSub`, `ddMul`, `ddSqr`, `ddScale`, `ddDiffabs` (Dekker/Knuth error-free transformations using `fma`).
- **Direct path** (radius > 1e-6): each pixel's orbit iterated in float-float coordinates. Correct for all 4 formulas at shallow zoom; required for Burning Ship (perturbation is unstable at large delta).
- **Perturbation path** (radius ≤ 1e-6): CPU `ReferenceOrbit` (BigInteger, unchanged) split to float-float pairs for GPU upload; GPU tracks float-float delta dz with rebasing. Supports zoom to ~1e-12.
- All 4 formulas: Mandelbrot, Prospector, Julia, Burning Ship.
- Reference-orbit caching logic mirrors `DeepZoomPipeline.EnsureReference`.
- Hero SSAA via `MetalSsaa.Accumulate`.

**Precision note:** float-float gives ~48 mantissa bits (~1e-12 max depth). The OpenGL floatexp path reaches 1e-147. Not bit-identical at extreme depth, but beyond any practical need — verified via `metal-deepzoom-mp4` CLI (Seahorse Valley, 1280×720, 4× SSAA).

Files touched: `MetalDeepZoomRenderer.cs` (new), `deepzoom_metal.metal` (new), `Parsec.Rendering.Metal.csproj`, `FractalView.cs`, `MainWindow.axaml.cs`. Commits: 859e03c, 0e3d5e9.

### 11. Package and notarize

Goal: macOS distribution polish after renderer functionality exists.

Expected files touched: packaging scripts, entitlements/signing/notarization files.

Acceptance criteria: deferred until the app has a useful macOS 3D render path.

### 12. HDR post-processing pipeline ✓ COMPLETE

Goal: add a final tone-mapping / color-grading stage so fractals look cinematic rather than like clamped shader output, and expose grade parameters as cheap per-frame audio-modulation targets.

**Reference model (Mandelbulber2, GPL-3.0 — design inspiration only, no code copied).**
`cImage::CalculatePixel` grade order (ported verbatim to MSL):
1. `c *= brightness`
2. `c = (c - 0.5) * contrast + 0.5`; `c = max(c, 0)`
3. `if (hdrEnabled) c = tanh(c)` — tanh tone map (not ACES)
4. `V = sqrt(R²·0.299 + G²·0.587 + B²·0.114); c = V + (c - V) * saturation` — Rec.601 luma saturation
5. `clamp(c, 0, 1)`
6. `pow(c, 1/gamma)`

**Implementation:**

New files:
- `src/Parsec.Rendering.Metal/Shaders/postprocess.metal` — MSL grade kernel. buffer(0) = `float4[]` HDR input, buffer(1) = `GpuPostProcessParams`, buffer(2) = `uint[]` RGBA8 output. 32-byte GPU struct: `{ int imageWidth; int imageHeight; float brightness; float contrast; float gamma; float saturation; int hdrEnabled; int pad0; }`.
- `src/Parsec.Rendering.Metal/MetalPostProcess.cs` — static class with lazy-compiled PSO (double-checked lock). `internal static uint[] Apply(device, queue, float[] hdrPixels, w, h, PostProcessParams?)`. Null params → identity (no-op). Gamma clamped to ≥ 0.01f.
- `src/Parsec.Rendering.Metal/PostProcessParams.cs` — public struct `{ Brightness=1, Contrast=1, Gamma=1, Saturation=1, HdrEnabled=false }`. Default is identity → output identical to pre-M12.
- `src/Parsec.App/PostProcessState.cs` — mirrors `PaletteState`. `BuildSchema()` exposes 4 float sliders in group "Post: grade" (Brightness 0–4, Contrast 0–4, Saturation 0–3, Gamma 0.1–4). `HdrEnabled` is a bool field, not a slider (toggle not yet wired to UI).

Modified files:
- All 20 `*_raymarch.metal` (not `deepzoom_metal.metal`): `device uint* output` → `device float4* output`; removed RGBA8 pack; last line is `output[idx] = float4(color, 1.0f)`.
- `MetalSsaa.cs`: `AccumulateHdr(int, int, int, Func<Vector2, float[]>) → float[]` added — accumulates in float space, returns stride-4 float[] (R,G,B,A per pixel). Old `Accumulate(Func<Vector2, uint[]>)` retained — used only by `MetalDeepZoomRenderer` (2D path, still packs RGBA8 in kernel).
- All 20 `Metal*Renderer.cs`: `MetalSsaa.Accumulate` → `MetalSsaa.AccumulateHdr`; buffer size `sizeof(uint)` → `4 * sizeof(float)`; `ReadUintBuffer` → `ReadFloat4Buffer` (returns `float[count*4]`); call `MetalPostProcess.Apply(...)` after accumulation.
- `FractalView.cs`: `PostProcess` property (`PostProcessState`), schema wired in `BuildActiveSchema` (macOS-only), all 21 `RenderWithMetal*` and `RenderActiveTo` call sites pass `PostProcess.ToParams()`.
- `Parsec.Rendering.Metal.csproj`: `postprocess.metal` added as `EmbeddedResource`.
- `Program.cs`: `metal-m12-stills` CLI command — renders Mandelbox (identity), Mandelbulb (brightness+saturation), Phoenix (tanh tone map), KIFS (hi-contrast desaturated) at 512×512; saved to `outputs/m12-*.png`.

**CLI validation:** `metal-new-smoke` — 14/14 pass. `metal-m12-stills` — all four stills render with visibly distinct grade effects.

**Note on `MetalDeepZoomRenderer` exclusion:** 2D deep-zoom path packs RGBA8 in its kernel and uses the old `MetalSsaa.Accumulate`. This is intentional — deep zoom has no 3D shading to grade, and the HDR contract change does not apply to it.

**3D Phoenix shape note:** the Phoenix fractal's 3D extension (Mandelbulb-style spherical lifting of the memory-term formula) produces a wrinkled spheroid, not the feathery tentacles of the 2D Julia-Phoenix slice. The default `Cut=true` with `PlaneOffset=0` shows a cross-section through the center; use `Cut=false` to see the full 3D surface.

Risks / notes:
- The HDR target adds memory and one extra pass; on unified memory this should stay cheap, but measure post-pass time at 1080p before adding bloom blur passes.
- `tanh` tone map matches the reference; ACES is the film-standard alternative if the highlight rolloff looks wrong.
- Mandelbulber2 and Fragmentarium are GPL-3.0; this project derives from GPL-3.0 upstream, so license direction is compatible, but the work is a clean-room port of the *algorithm*, not a code copy.

---

## Audio-Reactive Feature

**Context:** The upstream `zoomacroom-games/Parsec` has no audio features. This is an entirely new capability being added to this project. Development started in a separate `fractal_audio` branch/fork. The work below describes porting what's already done there into this repo, then completing the phases that haven't been started anywhere yet.

### Source of truth for existing audio work

All audio code written so far lives in `~/projects/fractal_audio`. That repo is a fork of the upstream original with a `Parsec.Audio` project and UI wiring added. **The `3d_fractal_mac` repo has none of this yet.**

### Audio Phase 1 — Port existing transport + UI (done in fractal_audio, not yet here)

The following are complete in `fractal_audio` and need to be brought into this repo:

**New project `src/Parsec.Audio/`:**
- `AudioTransportController.cs` — backend-neutral play/pause/seek facade
- `IAudioPlaybackBackend.cs` / `IAudioPlaybackSession.cs` — abstraction seam
- `OpenALAudioPlaybackBackend.cs` / `OpenALAudioPlaybackSession.cs` — OpenAL + WAV decode backend
- `UnavailableAudioPlaybackBackend.cs` — stub for platforms without audio
- `WavePcmData.cs` / `WavePcmDecoder.cs` — managed WAV decode
- `AudioPlaybackStatus.cs`, `AudioTransportState.cs`

**UI wiring in this repo:**
- `src/Parsec.App/AudioTransportPanel.cs` (new) — file/play/pause/seek control panel
- `src/Parsec.App/MainWindow.axaml` — add `<ContentControl Name="AudioHost" Margin="0,12,0,0" />`
- `src/Parsec.App/MainWindow.axaml.cs` — add `_audioHost`/`_audioTransport` fields, init, `OnWindowClosed` disposal
- `Parsec.sln` and `Parsec.App.csproj` — add `Parsec.Audio` project reference

Acceptance criteria: app launches, audio panel is visible, user can load and play a WAV file, fractal rendering is unaffected when audio is idle.

### Audio Phase 2 — Port existing offline analysis (done in fractal_audio, not yet here)

Also complete in `fractal_audio`, needs porting:

- `src/Parsec.Audio/IAudioAnalyzer.cs`
- `src/Parsec.Audio/AudioFeatureFrame.cs` — per-frame RMS, band energies, onset, spectral centroid
- `src/Parsec.Audio/AudioFeatureTrack.cs` — full-track feature data with `SampleAt(t)` interpolation
- `src/Parsec.Audio/WaveAudioAnalyzer.cs` — offline RMS/FFT analysis (WAV-only)
- `src/Parsec.Audio/AudioAnalysisOptions.cs`

CLI validation path (already in this repo's `Parsec.Cli`): `parsec audio-analyze /path/to/file.wav 500`.

Acceptance criteria: full-track analysis runs for a WAV file; feature values can be sampled at arbitrary timestamps off the render path.

### Audio Phase 3 — Audio-reactive modulation ✓ DONE

Drives live visual parameters from audio feature values. `src/Parsec.App/AudioModulationController.cs` samples features per frame and applies additive offsets to fractal/camera/palette `ParamDescriptor`s; `AudioModulationMapping.cs` / `AudioFeatureSource.cs` model the source → target + depth + smoothing. Modulation can be toggled, and timeline playback still works with it active. (Implemented in `Parsec.App`, not `Parsec.Audio` as originally sketched.)

### Audio Phase 4 — Mapping UI ✓ DONE

`src/Parsec.App/AudioMappingPanel.cs` is the in-app mapping editor (feature source, parameter target, depth, enable). Wired into `MainWindow` via the `AudioMappingHost` content control; schema refreshed on fractal change.

### Audio Phase 5 — Timeline + export integration ✓ DONE

`AudioModulationController.ApplyAtTime(t)` performs deterministic feature sampling at export timestamps matching live playback; the Render-to-Video path attaches audio via ffmpeg mux.

---

## Fractal Sonification (geometry → audio)

This is the **inverse** of the audio-reactive feature above: sound is generated *from* the fractal geometry rather than geometry being driven from sound. It is a distinct, newly-requested feature (2026-06-09) with its own milestones M0–M6.

The full plan — architecture, the three-layer separation (Metal telemetry / C# DSP synth / OpenAL spatialization), real-time audio-safety constraints, the telemetry-pass options, the reuse-vs-build matrix, milestones, and pitfalls — lives in **`docs/fractal-sonification-plan.md`**. It was verified against the code on 2026-06-09 (file pointers and the `mandelbox_raymarch.metal` telemetry claims confirmed).

Key cross-cutting constraints to keep in mind here:
- New namespace `Parsec.Audio.Sonification`; new `FractalSonicFrame` type — must not collide with the existing `AudioFeatureFrame`.
- Sonification and the Phase 3–5 reactive modulation must be **mutually exclusive modes**, or the sonifier reads base/keyframe geometry, to avoid a fractal → sound → params → fractal feedback loop.
- OpenAL does spatialization only; the DSP/synthesis layer (oscillators, filters, granular, one-pole smoothers) is new C# and does not exist yet.
- Offline-first (deterministic WAV + existing ffmpeg mux) before live streaming; Mandelbox first before rolling out to other fractals.

---

## Explicit Non-Goals For First Milestone

- audio-reactive visuals (tracked above as Audio Phases 1–5)
- synthesizer or visual-to-sound work
- 2D deep zoom on Metal (tracked as M10)
- fp64 shader parity
- all 3D shader ports (now done — M7)
- native Metal presentation before offscreen display is measured
- installer, notarization, or release packaging (tracked as M11)

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
- Mandelbulber2 (M12 post-processing reference, GPL-3.0): https://github.com/buddhi1980/mandelbulber2 — `mandelbulber2/src/cimage.cpp`, `cimage.hpp`, `image_adjustments.h` (`CalculatePixel`, `CompileImage`, float-buffer chain)
- Fragmentarium (M12 two-pass structural reference): https://github.com/Syntopia/Fragmentarium
- Apple Metal HDR post-processing sample code: https://developer.apple.com/metal/sample-code/
