# Surface Texture Projection — handoff

**Written 2026-06-12 by Opus. Branch `feature/fractal-sonification`.**
Goal of the feature: let a user-supplied **image act as the surface texture/colour** of the 3D
fractals, instead of (blended over) the current procedural colouring.

Read alongside `docs/macos-3d-only-build-plan.md` → "Render Feature: Surface Texture Projection"
(Codex's section — note its status claim is overstated; see "Honest status" below) and `skills.md`
(Metal porting gotchas).

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

User picks an image → it tints the fractal surface via **object-space triplanar projection**
(no UVs — the fractals have none). Same mechanism on both backends; the texture is an optional
colour layer blended into the existing shading tail, leaving lighting / HDR post / transparent-export
matte untouched.

### Metal path
- **`MetalSurfaceTextureManager`** (static, per-device): `SetImage(bytes,w,h,rowBytes)`,
  `ClearImage()`, `SetControls(enabled, blend, scale)`. Decodes/caches one `MTLTexture` per device
  (`GetTexture(device)`). It also **packs the controls into spare slots of `RenderParams`** so no
  struct layout changed:
  - `EncodeBackground(color)` → `background.w` = **enable flag** (≥0.5 = textured)
  - `EncodeSurface(color)` → `surface.w` = **blend** [0,1]
  - `EncodeMarchB(aoStep, aoIntensity)` → `marchB.z` = **scale**, `marchB.w` = **texture aspect**
- **`MetalSurfaceTextureShaderInjector.Inject(src)`** rewrites each `*_raymarch.metal` source string
  *at load time* (idempotent — guards on `applySurfaceTexture(`). It:
  1. appends `texture2d<float> surfaceTexture` to the `traceRay(...)` signature and its call site,
  2. adds `texture2d<float> surfaceTexture [[texture(0)]]` to the kernel signature (after
     `output [[buffer(2)]],`),
  3. wraps `h.albedo = albedo;` and `h.albedo = trapAlbedo(gTrap, rp);` with
     `applySurfaceTexture(...)`,
  4. inserts the helper funcs (`surfaceTextureAspectUv`, `sampleSurfaceTexture`,
     `applySurfaceTexture`) just before `float3 envGradient(`.
  Triplanar: `weights = normalize(pow(abs(normal),4))`; sample yz/xz/xy planes (× scale,
  aspect-corrected, `fract`-wrapped); `mix(baseAlbedo, sampled, blend)`.
- Each renderer: wraps its `LoadEmbeddedMsl(...)` in `Inject(...)`, and binds the texture with
  `enc.SetTexture(MetalSurfaceTextureManager.GetTexture(_device), 0)` after the buffers. (See the
  `MetalMandelboxRenderer.cs` diff for the canonical wiring — main render + both telemetry passes.)

### OpenGL path
- **`GpuSurfaceTextureManager`** mirrors the Metal manager (image source + `TextureSnapshot` with a
  `Version` so the GL path re-uploads only on change). Wired through `RaymarchPipeline` +
  `raymarch_main.glsl` shading tail with the same triplanar + blend.

### MetalBufferIO (separate but bundled)
Shared Metal buffer seam: `CreateSharedBuffer`, `UploadStruct<T>`, `ReadFloat4Buffer`, and
**`RequireContents(buf, label)`** which throws a precise *"no CPU-accessible contents"* error
instead of null-deref when the host can't map `MTLBuffer.Contents`. Renderers were migrated off
direct `buf.Contents` reads onto these. This is what surfaces the host's readback failure cleanly.

### UI (`MainWindow` + `FractalView`)
Controls: `SurfaceTextureEnableCheckBox`, `SurfaceTextureLoadButton`, `SurfaceTextureClearButton`,
`SurfaceTextureBlendSlider`, `SurfaceTextureScaleSlider`, `SurfaceTextureProjectionSelector`
(ComboBox), `SurfaceTexturePathText`. `FractalView` exposes `SurfaceTextureEnabled/Blend/Scale` and
`SupportsSurfaceTexture`; `UpdateSurfaceTextureUi(type)` enables/disables per fractal.

### CLI verification
- `metal-surface-texture-smoke <imagePath> [w] [h] [outDir]` and `gpu-surface-texture-smoke` —
  render baseline vs textured Mandelbox from the same image, save PNGs, print changed-pixel stats.
  `TryLoadSurfaceTextureImage(...)` decodes to tight RGBA.

---

## Honest status (correct the plan doc to this)

Codex's plan section opens *"is now implemented across both render backends"* — that's true at the
**code/build** level but **not verified**. Accurate status:

- ✅ Coded on both backends; solution builds clean.
- ⚠️ **Unverified by automated test on this host.** `metal-surface-texture-smoke` reaches dispatch
  but cannot read the float4 output buffer back (`MTLBuffer.Contents` unmappable on this
  remote/headless macOS box). `gpu-surface-texture-smoke` stalls in launch services before render.
  This is the **same host Metal limitation** behind the `computeFunction must not be nil` crashes
  seen this session — it almost certainly works in the app on a real Mac, just not in CLI/CI here.
- ❌ No golden-frame regression coverage yet.

### Remaining work (priority order)
1. **Verify on a real Mac** (interactive app): load an image, toggle Enable, confirm the fractal
   surface takes the image colour and that triplanar looks stable as the camera moves. This is the
   gate everything else waits on — the host here can't do it.
2. **Projection Mode selector is a placeholder** — the `SurfaceTextureProjectionSelector` ComboBox
   exists in the UI but the shaders implement **triplanar only**. Either wire the selector to real
   modes (axis-locked planar, normal-weighted) or hide it until they exist.
3. **Finish the `MetalBufferIO.RequireContents` rollout** anywhere a renderer still reads
   `buf.Contents` directly (esp. telemetry-only readback helpers across the ported renderers).
4. **Capture golden frames** once a backend renders deterministically somewhere, for regression.
5. Resolve the host's SharpMetal shared-buffer mapping + the headless GL startup stall if CI
   verification is wanted (lower priority — may be unfixable from the remote host).

### Gotchas
- **The injector is exact-string-keyed.** It silently no-ops on any shader whose tail doesn't
  contain `h.albedo = albedo;` / `h.albedo = trapAlbedo(gTrap, rp);` / the exact `traceRay(...)`
  call / `output [[buffer(2)]],` / `float3 envGradient(`. Before claiming "all 20 fractals
  textured," grep each `*_raymarch.metal` for those anchors — any miss = that fractal renders
  untextured with no error.
- Controls ride in **spare `RenderParams` lanes** (`background.w`, `surface.w`, `marchB.z/.w`). If
  you add other features that write those lanes, they'll collide.
- `#define` constants in MSL, never program-scope `const` (silent compile failure — see skills.md).
- New shader files need a csproj `<EmbeddedResource>` entry (none added here — the feature reuses
  existing shaders via injection, so no csproj change was needed).

---

## Standing rules
- Local commits only; **never push** without the global Post-Push Verification Protocol + explicit ask.
- Keep Metal and OpenGL texture paths behaviourally in sync (two implementations of one feature).
- Don't disturb the sonification work (its own handoff is `docs/sonification-handoff-sonnet46.md` —
  a **different, currently-paused** feature; don't conflate the two).
