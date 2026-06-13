# Surface Texture Projection — handoff

**Written 2026-06-12 by Opus. Updated 2026-06-12 by Sonnet 4.6.**
Goal of the feature: let a user-supplied **image act as the surface texture/colour** of the 3D
fractals, instead of (blended over) the current procedural colouring.

Read alongside `docs/macos-3d-only-build-plan.md` → "Render Feature: Surface Texture Projection"
and `skills.md` (Metal porting gotchas).

---

## ⚠️ State: UNCOMMITTED working-tree work

This whole feature is **uncommitted** in the working tree (it is *not* in any commit yet):

- **New files (untracked):**
  `src/Parsec.Rendering.Metal/MetalSurfaceTextureManager.cs`,
  `src/Parsec.Rendering.Metal/MetalSurfaceTextureShaderInjector.cs`,
  `src/Parsec.Rendering.Metal/MetalBufferIO.cs`,
  `src/Parsec.Rendering.Gpu/GpuSurfaceTextureManager.cs`
- **Modified (~30 files):** nearly every `Metal*Renderer.cs`, `RaymarchPipeline.cs`,
  `Shaders/raymarch_main.glsl`, `GlConst.cs`, `MetalPostProcess.cs`, `MetalDeepZoomRenderer.cs`,
  `TelemetryReduction.cs`, `FractalView.cs`, `MainWindow.axaml`+`.cs`, `Program.cs`,
  `docs/macos-3d-only-build-plan.md`.

**It builds** (`dotnet build Parsec.sln -v quiet` → 0 errors; the build already includes these files).

**Attribution caveat:** a parallel sonification session's commit `4c5e9af` committed
`MainWindow.axaml`, `MainWindow.axaml.cs`, and `Program.cs` — files this feature also edits — so a
few in-progress texture lines may already be inside that commit. When you commit the texture work,
**diff these three files carefully** so you don't double-apply or drop lines.

---

## How it works

User picks an image → it tints the fractal surface via one of two projection modes selected at
runtime. The texture is an optional colour layer blended into the existing shading tail, leaving
lighting / HDR post / transparent-export matte untouched.

### Two projection modes

**Mode 0 — Triplanar (world-space):** object-space triplanar projection onto yz/xz/xy planes,
normal-weighted (`pow(abs(normal),4)`), scale + aspect-corrected, `fract`-wrapped. Stable under
camera motion and parameter animation. Available on all 20 Metal shaders and the OpenGL path.

**Mode 1 — Orbit trap (iteration-space):** the DE's iteration loop is re-run at the hit point;
`z.xy` at the minimum-distance orbit step is captured and normalised by `bailout` to give a 2D UV.
This UV is intrinsic to the fractal's iteration structure — it deforms and shifts when parameters
change, unlike the world-space triplanar grid. Currently BurningShip only (see "Extending orbit
trap" below).

### Mode encoding in `RenderParams`

`background.w` encodes the active mode at runtime (no struct layout change):
- `0.0` = texture disabled
- `1.0` = triplanar
- `2.0` = orbit trap

`surface.w` = blend [0,1], `marchB.z` = scale, `marchB.w` = texture aspect (all modes).

### Metal path

- **`MetalSurfaceTextureManager`** (static, per-device): `SetImage(bytes,w,h,rowBytes)`,
  `ClearImage()`, `SetControls(enabled, blend, scale, mode=0)`. Caches one `MTLTexture` per device.
  Packs controls into spare `RenderParams` slots (see above). `mode` 0 = triplanar, 1 = orbit trap.

- **`MetalSurfaceTextureShaderInjector.Inject(src)`** — triplanar injection for all 20 shaders.
  Idempotent (guards on `applySurfaceTexture(`). Injects texture binding, `traceRay` param,
  call-site patch, and helper functions (triplanar-only `applySurfaceTexture`).

- **`MetalSurfaceTextureShaderInjector.InjectOrbitTrap(src)`** — orbit trap + triplanar combined
  injection for BurningShip (and future shaders whose `estimateFull` outputs `outTrapUv`). Must be
  called **instead of** `Inject` for those renderers — after `InjectOrbitTrap` runs, `Inject` is a
  no-op (idempotency guard fires). What it injects:
  1. Replaces the `estimateFull` call in `traceRay` to also capture `float2 trapUv`.
  2. Replaces `h.albedo = albedo;` with a mode-conditional call:
     `applySurfaceTexture(albedo, rp.background.w >= 1.5f ? float3(trapUv, 0) : hitPoint, normal, rp, tex)`
  3. Injects a mode-aware `applySurfaceTexture` that branches on `background.w` — triplanar when
     `< 1.5`, orbit trap when `>= 1.5`.
  4. Standard texture binding + `traceRay` signature patch (same as `Inject`).

- Each renderer: wraps its `LoadEmbeddedMsl(...)` in `Inject(...)` (triplanar) or `InjectOrbitTrap`
  (BurningShip). Binds the texture with `enc.SetTexture(MetalSurfaceTextureManager.GetTexture(_device), 0)`.

### Extending orbit trap to other shaders

Two changes per shader:
1. Modify `estimateFull` to track min-distance point and output `thread float2& outTrapUv`
   (z.xy at min distance, normalised by bailout). Change `estimate()` wrapper to pass a dummy `float2`.
2. Call `InjectOrbitTrap` in that renderer's ctor instead of `Inject`.

See `burningship_raymarch.metal` as the reference implementation.

### OpenGL path
- **`GpuSurfaceTextureManager`** mirrors the Metal manager. Triplanar only — orbit trap is not yet
  ported to the GLSL path.

### MetalBufferIO (separate but bundled)
Shared Metal buffer seam: `CreateSharedBuffer`, `UploadStruct<T>`, `ReadFloat4Buffer`, and
**`RequireContents(buf, label)`** which throws a precise *"no CPU-accessible contents"* error
instead of null-deref when the host can't map `MTLBuffer.Contents`.

### UI (`MainWindow` + `FractalView`)
Controls: `SurfaceTextureEnableCheckBox`, `SurfaceTextureLoadButton`, `SurfaceTextureClearButton`,
`SurfaceTextureBlendSlider`, `SurfaceTextureScaleSlider`, `SurfaceTextureProjectionSelector`
(ComboBox), `SurfaceTexturePathText`. `FractalView` exposes `SurfaceTextureEnabled/Blend/Scale` and
`SupportsSurfaceTexture`; `UpdateSurfaceTextureUi(type)` enables/disables per fractal.

**The `SurfaceTextureProjectionSelector` ComboBox is currently wired to UI only — it does not yet
feed `mode` into `MetalSurfaceTextureManager.SetControls`. Wire it to pass `mode: 0` for Triplanar
and `mode: 1` for Orbit Trap, with Orbit Trap disabled (greyed) for non-BurningShip fractals.**

### CLI verification
- `metal-surface-texture-smoke <imagePath> [w] [h] [outDir]` — triplanar A/B on Mandelbox.
- `gpu-surface-texture-smoke <imagePath> [w] [h] [outDir]` — OpenGL triplanar A/B.
- `metal-burning-texture-mp4 [imagePath] [duration] [out.mp4]` — BurningShip orbit-trap fly-in
  with Power animation (defaults to orbit trap mode, `mode: 1`). Renders at 320×180, 24fps.
  Pass `SetControls(..., mode: 0)` in the CLI to compare triplanar on the same path.

---

## Status (UPDATED 2026-06-12)

Both modes verified rendering on Apple M4 Pro.

- ✅ **Triplanar verified:** `metal-surface-texture-smoke` renders 12,958/76,800 px changed at
  blend 0.85. All 20/20 main shaders inject + compile.
- ✅ **Orbit trap verified:** `metal-burning-texture-mp4` renders 240-frame BurningShip clip with
  Power 1.8→2.8 sweep. Texture UV deforms with fractal geometry (intrinsic to iteration structure,
  distinct from triplanar). BurningShip shader only.
- ✅ Build clean, solution builds 0 errors.
- ✅ `MTLBuffer.Contents` IS mappable on this host.
- ⚠️ OpenGL `gpu-surface-texture-smoke` still unverified (headless GL context stalls in launch services).
- ❌ No golden-frame regression coverage yet.
- ❌ `SurfaceTextureProjectionSelector` ComboBox not yet wired to `mode` parameter (UI placeholder).

### Injector bugs found + fixed (2026-06-12)
Both lived in `MetalSurfaceTextureShaderInjector.Inject()`:
1. **Over-broad signature anchor** also hit `shadeDirect` → 6-arg function called with 5 args.
   **Fix:** anchor on `traceRay`'s unique `int maxSteps,` line.
2. **`menger_raymarch.metal`** non-standard `traceRay` call site missed by the standard patch.
   **Fix:** Menger-specific call-site replacement.

See `skills.md` → "Shader-injection gotchas" and "`computeFunction must not be nil`…".

### Remaining work — now tracked in `docs/app-completion-plan.md`

The surface-texture gaps are folded into the app-wide completion roadmap. Current status of each:

1. **Texture feedback loop is now in-app** (committed `d998ffe`): `FractalView.TextureFeedbackEnabled`
   primes the next frame's texture with the current render via `UpdateImage`. The in-app path uses
   the preview resolution for both render and texture, so it has no resolution-mismatch (unlike the
   `metal-fractal-feedback` CLI demo, which tiles when sizes differ). → Completion-plan **Phase 2**
   generalizes this into a `SurfaceTextureSource` dropdown (None/Image/Video/MandelbrotZoom/Feedback).
2. **`SurfaceTextureProjectionSelector` is still a stub** — 1 ComboBox item ("Triplanar"), wired to a
   tooltip only, NOT to `mode`. Orbit trap remains unreachable from the app. → **Phase 3**.
3. **Orbit trap is still BurningShip-only** — it is the one shader emitting `outTrapUv`. → **Phase 3**
   rolls it out to Mandelbox/Mandelbulb/KIFS/Menger first.
4. **`MetalBufferIO.RequireContents` rollout is effectively complete** — all renderers route shared
   buffers through `MetalBufferIO`; `.Contents` is read directly only inside `MetalBufferIO.cs` itself.
5. **No golden-frame regression yet.** → completion-plan **Phase 6** (`metal-golden`).
6. Headless GL startup stall for `gpu-surface-texture-smoke` — unchanged, low priority.

### Gotchas
- **`background.w` is now a 3-state enum** (0/1/2), not a bool. Any code that checks
  `background.w >= 0.5f` to test "enabled" still works, but code that writes `1f` for enabled and
  `0f` for disabled must now use the mode-aware value from `EncodeBackground()`.
- **Orbit trap UV lives in [0, bailout_normalized] range.** For BurningShip (bailout=2), after the
  `abs()` fold all coordinates are positive, so UV ≈ [0, 0.5]. Use `marchB.z` (scale) to zoom into
  the image; `fract()` wraps at tile boundaries.
- **Orbit trap is re-evaluated at the hit point** (extra `estimateFull` call per hit pixel — the
  same cost as the normal estimation). The iteration count is capped at `fp.iterations`.
- **The injector is exact-string-keyed** (fragile). After ANY injector change, sweep all 20
  `*_raymarch.metal` through `/tmp/metalprobe` to confirm compile + kernel export. Note: the probe
  uses `Inject()` logic, not `InjectOrbitTrap()` — BurningShip will fail the probe with the standard
  inject (expected; the renderer uses `InjectOrbitTrap` directly).
- Controls ride in **spare `RenderParams` lanes**. If other features write `background.w`,
  `surface.w`, `marchB.z/.w`, they'll collide with the texture controls.
- `#define` constants in MSL, never program-scope `const` (silent compile failure).

---

## Standing rules
- Local commits only; **never push** without the global Post-Push Verification Protocol + explicit ask.
- Keep Metal and OpenGL texture paths behaviourally in sync where both are implemented.
- Don't disturb the sonification work (its own handoff is `docs/sonification-handoff-sonnet46.md`).
