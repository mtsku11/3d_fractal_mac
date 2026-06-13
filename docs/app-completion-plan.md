# App Completion Plan

**Authoritative roadmap to a "completely finished" macOS build.** Written 2026-06-13 by
Sonnet 4.6 after a full feature audit. This is the single source of truth for *what is left*;
the per-feature mechanics live in `docs/macos-3d-only-build-plan.md`,
`docs/surface-texture-handoff.md`, and `docs/fractal-sonification-plan.md`.

When you finish a phase, tick it here and update the cross-referenced doc.

---

## 1. Audit (2026-06-13)

### Done and solid — do not reopen

| Area | State |
|------|-------|
| 20 fp32 3D Metal fractals (M1–M7) | Complete, validated via CLI smoke |
| 2D deep zoom on Metal (M10) | Complete (float-float, ~1e-12) |
| HDR grade post-process (M12) | Complete (grade pass runs) |
| In-app 16× SSAA (M8) | Complete |
| Fractal sonification (M0–M9e, Track D) | Complete — 8 of 20 fractals |
| Audio-reactive modulation (Phases 1–5) | Complete |
| Triplanar surface texture (all 20 + OpenGL) | Complete, committed |
| In-app texture feedback loop | Complete, committed |
| `MetalBufferIO` seam + `RequireContents` | Effectively complete (all renderers route through it) |

### Unfinished, thin, or inconsistent — this plan closes these

| # | Gap | Evidence | Phase |
|---|-----|----------|-------|
| 1 | **Projection ComboBox is a stub** — 1 item, not wired to `mode`; orbit trap unreachable in-app | `MainWindow.axaml:146`, `MainWindow.axaml.cs:451` (tooltip only) | 3 |
| 2 | **Orbit trap stranded on BurningShip** — only shader with `outTrapUv` | `grep outTrapUv` → 1 file | 3 |
| 3 | **Texture-source sprawl** — 6 overlapping CLI-only demos; app exposes only Image + Feedback | `metal-{oracle,closeup-hq,closeup-oracle,cross-fractal-texture,fractal-feedback,burning-video-texture}` | 2 |
| 4 | **Domain warp uncommitted**; Metal warps primary march + normal only, **not** shadow/AO (OpenGL does) | working tree; `MetalSurfaceTextureShaderInjector.cs:205–227` vs `raymarch_main.glsl:93,108` | 1 |
| 5 | **HDR tanh toggle not in UI** — `HdrEnabled` is a field only | `PostProcessState.cs:17` | 4 |
| 6 | **Attractor has no Metal renderer** — dark placeholder on macOS | no `MetalAttractor*`; `HasMetalPreviewRenderer` false | 5 |
| 7 | **No golden-frame regression** anywhere | — | 6 |
| 8 | **M11 packaging / notarization** not started | — | 7 |

### Organizing insight

Gaps 1–3 are the same problem wearing three hats. The CLI demos (`cross-fractal-texture`
= Mandelbrot-as-texture, `burning-video-texture` = video-as-texture, `fractal-feedback` =
previous-frame-as-texture, `oracle`/`closeup-*` = preset combinations) prove a set of
**texture sources** and **projection modes** that the app does not yet expose as first-class
controls. The biggest "finished app" win is to **unify them into one in-app Texture Source
system** (Phase 2) and then make projection a real choice (Phase 3), rather than leaving the
interesting behaviour locked in single-purpose CLI commands.

---

## 2. Design principle — unify texture sources

Today the surface-texture panel has: Enable, Load Image, Clear, Blend, Scale, Projection
(stub), Feedback Loop. The feedback checkbox is a *second* source bolted next to an image
source. Generalize to one orthogonal model:

- **Texture Source** (dropdown): `None · Image · Video · Mandelbrot Zoom · Feedback`
- **Projection** (dropdown): `Triplanar · Orbit Trap` (Orbit Trap greyed unless the active
  shader supports it)
- **Blend**, **Scale** (existing)

`MetalSurfaceTextureManager` already accepts `SetImage`/`UpdateImage` + `SetControls(…, mode)`.
A new `SurfaceTextureSource` enum in `FractalView` drives **what** fills the texture each frame;
the manager stays the **how**. This subsumes `cross-fractal-texture` (Mandelbrot Zoom source),
`burning-video-texture` (Video source), and the feedback checkbox (Feedback source) into one
coherent control surface. `oracle` / `closeup-*` become *render presets* (camera path + source
+ morph), not bespoke code paths.

---

## 3. Phased roadmap

Each phase is independently shippable and ends green (build + its acceptance check).
Recommended order is by value × dependency; Phases 1–4 are the core of "finished".

### Phase 1 — Commit and finish Domain Warp

**Goal:** the uncommitted domain-warp feature reaches parity between backends and is durable.

Tasks:
1. Commit the current working-tree domain-warp set (source files only) — it builds clean.
2. Extend the **Metal** injector so soft-shadow and AO sampling march the *warped* DE, matching
   the OpenGL path (`raymarch_main.glsl` already warps shadow/AO at lines 93/108). Today the
   Metal injector patches only the 3 primary-march `estimate()` sites + `estimateNormal`.
3. Add an animated **phase/time** input so the warp field can flow (currently static per frame).

Acceptance:
- `metal-domain-warp-mp4` still reports >95% changed pixels on frame 0.
- A shadowed Mandelbox frame shows the shadow boundary moving *with* the warp (eyeball or
  changed-pixel A/B between warp on/off in the shadowed region).
- Build clean; committed.

Cross-ref: `docs/macos-3d-only-build-plan.md` → "Render Feature: Domain Warp".

### Phase 2 — Unified Texture Source system (in-app)

**Goal:** every texture behaviour proven in the CLI is reachable in the app through one model.

Tasks:
1. Add `SurfaceTextureSource { None, Image, Video, MandelbrotZoom, Feedback }` to `FractalView`;
   replace the standalone Feedback checkbox with the dropdown.
2. **Image** — existing path.
3. **Feedback** — existing path (already correct: in-app uses preview resolution for both render
   and texture, so no resolution-mismatch hack — unlike the CLI demo).
4. **Mandelbrot Zoom** — instantiate a `MetalDeepZoomRenderer`, render a low-res Mandelbrot frame
   each tick into the texture (reuse `metal-cross-fractal-texture` logic), with a zoom-rate slider.
5. **Video** — decode frames with the `metal-burning-video-texture` pipeline; `UpdateImage` per
   tick. Add Load Video + loop.
6. Keep the export path (hero + render-to-video) source-aware so what you see previews is what
   exports.

Acceptance:
- Each source visibly works in the live preview on a real Mac.
- Hero still and render-to-video honour the selected source.
- Build clean; committed.

Cross-ref: `docs/surface-texture-handoff.md`.

### Phase 3 — Projection modes + orbit-trap rollout

**Goal:** the Projection dropdown is real, and orbit trap works on more than one shader.

Tasks:
1. Wire `SurfaceTextureProjectionSelector` → `MetalSurfaceTextureManager.SetControls(mode:)`
   (Triplanar=0, Orbit Trap=1). Disable Orbit Trap for shaders without `outTrapUv`.
2. Extend orbit trap to a prioritized set (Mandelbox, Mandelbulb, KIFS, Menger first): per shader,
   ~10-line `estimateFull` change to emit `outTrapUv` + switch its renderer `Inject` →
   `InjectOrbitTrap`. After each, sweep `/tmp/metalprobe` (note: probe uses `Inject()` logic, so
   orbit-trap shaders are expected to "fail" the probe's standard-inject test).
3. Expand the ComboBox to list both modes; `UpdateSurfaceTextureUi(type)` greys Orbit Trap when
   unsupported.

Acceptance:
- Toggling Triplanar↔Orbit Trap in-app changes the projection on a supported shader.
- ≥4 shaders support orbit trap; all 20 still compile.
- Build clean; committed.

Cross-ref: `docs/surface-texture-handoff.md` → "Extending orbit trap".

### Phase 4 — Post-process and small UI gaps

**Goal:** no dangling half-wired controls.

Tasks:
1. Add an **HDR tone-map (tanh)** checkbox wired to `PostProcessState.HdrEnabled` (today a field
   with no UI).
2. Audit every control added this cycle for a tooltip + a sensible default; confirm each disables
   correctly for Deep Zoom / Attractor.
3. Confirm domain-warp + texture-source + projection controls all round-trip through Save/Load Anim
   if they are animatable params (or are explicitly excluded by design).

Acceptance:
- HDR toggle changes the grade live.
- No control is present-but-inert. Build clean; committed.

### Phase 5 — Attractor: port to Metal or formally cut

**Goal:** remove the one "preview unavailable" placeholder, by decision.

Attractor is not a closed-form DE — `GpuAttractorRenderer` sphere-traces a prebuilt
`AttractorHash` (trajectory + spatial hash) over SSBOs at bindings 6/7/8, so a Metal port needs
those data buffers, not just a kernel translation. Two acceptable endings:

- **(a) Port:** add `MetalAttractorRenderer` that uploads the hash/trajectory buffers and
  sphere-traces them; flip `HasMetalPreviewRenderer` true for Attractor.
- **(b) Cut:** remove `FractalType.Attractor` from the dropdown on macOS (and document it as a
  Windows/Linux-only fractal), so there is no dead placeholder.

Decision needed from the user before starting. Default if unanswered in an autonomous run: **(b) Cut**
— it is lower-risk and the audit shows Attractor is the only non-ported selectable type.

Acceptance:
- macOS dropdown has no entry that renders a "preview unavailable" placeholder.
- Build clean; committed.

### Phase 6 — Regression harness (golden frames)

**Goal:** changes to the shared shading tail / injectors can't silently break a fractal.

Tasks:
1. Add a `metal-golden` CLI command that renders a fixed-seed frame for each of: one plain
   fractal, one textured (triplanar), one orbit-trap, one domain-warp, one deep-zoom — at a small
   fixed size — and compares against committed PNG/hash baselines, printing per-frame pass/fail.
2. Commit the baselines under `tests/golden/`.
3. Document running it in `skills.md`.

Acceptance:
- `metal-golden` prints all-pass on a clean tree.
- A deliberate injector tweak makes it fail loudly. Committed.

### Phase 7 — M11 packaging and notarization

**Goal:** a distributable, signed, notarized `.app`.

Tasks:
1. `dotnet publish` a self-contained osx-arm64 app bundle; confirm the Homebrew openal-soft
   `OverridePath` resolves inside the bundle (see memory: macOS-15 OpenAL crash).
2. Code-sign with a Developer ID, build entitlements, notarize + staple.
3. Smoke the stapled bundle on a clean machine/user.

Acceptance:
- Notarized `.app` launches on a second account, renders, plays audio, sonifies.
- Packaging scripts committed.

Cross-ref: `docs/macos-3d-only-build-plan.md` → Milestone 11.

---

## 4. CLI demo command disposition

The experimental render commands are valuable as reproducible demos and as the implementation
reference for Phase 2. **Keep them**, but once Phase 2 lands, mark them in `help` as
"demo (superseded by in-app Texture Source)" so they aren't mistaken for the product surface:

- `metal-fractal-feedback` → demo of Feedback source
- `metal-cross-fractal-texture` → demo of Mandelbrot Zoom source
- `metal-burning-video-texture` → demo of Video source
- `metal-oracle`, `metal-closeup-hq`, `metal-closeup-oracle` → render presets (camera path +
  source + morph); candidates to become saved in-app presets, not code.
- `metal-surface-texture-smoke`, `metal-domain-warp-mp4` → keep as verification commands.

Do **not** add more bespoke combination commands; new behaviour goes through the unified
Texture Source model instead.

---

## 5. Definition of "completely finished"

All of the following true:

1. Phases 1–7 accepted and committed.
2. Every control in `MainWindow` is wired, defaulted, tooltipped, and correctly
   enabled/disabled per fractal.
3. No selectable fractal renders a placeholder on macOS.
4. `metal-golden` passes; build is clean (0 errors).
5. A notarized `.app` runs on a clean machine.
6. The three feature docs and `CLAUDE.md` reflect the shipped state (no "pending" that is
   actually done, no "done" that is actually pending).

---

## 6. Standing rules (unchanged)

- Local commits only; **never push** without the global Post-Push Verification Protocol + ask.
- Keep Metal and OpenGL paths behaviourally in sync where both are implemented.
- Prefer the separate low-res telemetry kernel over editing stabilized render kernels.
- Don't break audio-reactive (`AudioModulationController`) or sonification; they are
  mutually-exclusive modes with the geometry→audio path.
