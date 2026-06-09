# Fractal Sonification — Implementation Plan for Claude Code

**Repo:** `mtsku11/3d_fractal_mac` (Parsec)
**Base branch:** `feature/macos-metal-3d-spike`
**Goal:** Generate sound *from* the 3D fractal geometry (geometry → audio). The camera is the listener; the fractal is a spatial resonant object. This is the **inverse** of the audio-reactive system that already exists in the repo.

---

## 0. Read this before writing any code

This branch already contains an audio subsystem, but it runs the **opposite direction** from what we're building. Do not reuse its data model as the main type, and do not break it.

**Existing (audio → visuals), leave intact:**
- `src/Parsec.Audio/AudioFeatureFrame.cs` — features extracted *from a WAV* (Rms, Peak, Bass/Mid/Treble energy, SpectrumCentroidHz, OnsetStrength).
- `src/Parsec.Audio/IAudioAnalyzer.cs`, `WaveAudioAnalyzer.cs`, `AudioFeatureTrack.cs` — offline WAV analysis.
- `src/Parsec.App/AudioModulationController.cs`, `AudioModulationMapping.cs`, `AudioFeatureSource.cs`, `AudioMappingPanel.cs`, `AudioTransportPanel.cs` — apply WAV features as additive offsets to fractal params at ~60 Hz, with an `ApplyAtTime(t)` batch path + ffmpeg mux for export.
- `src/Parsec.Audio/OpenALAudioPlaybackBackend.cs` / `OpenALAudioPlaybackSession.cs` — **whole-file WAV player only** (decodes a complete WAV into one OpenAL buffer; no streaming/queue).

**Two hazards created by that subsystem:**
1. **Name collision.** There is already an `AudioFeatureFrame`. Our new type must be distinct (`FractalSonicFrame`) and live in a new namespace `Parsec.Audio.Sonification`.
2. **Feedback loop.** The reactive modulation (audio→params) is **already built and live** — `AudioModulationController` is wired into `MainWindow` and applies feature offsets to the live `ParamDescriptor`s every frame. So if sonification (geometry→audio) runs at the same time you get `fractal → sound → params → fractal`. **Decision: a single mode enum on `FractalView` — `AudioMode { Off, Reactive, Sonify }` — not an "either/or."** Reject the "sonifier reads base geometry instead" alternative: modulation applies its offsets *in place* to the live descriptors, so reconstructing the un-modulated base state mid-frame is fiddly and bug-prone. One exclusive mode is simpler and removes the half-modulated-state failure case entirely.

**Verify, don't trust this doc blindly.** File contents may have moved. Before editing, open the files named here and confirm signatures.

---

## 1. Architecture

```
Timeline / camera / fractal params
        │
        ▼
Metal render pass  (existing, per-fractal Metal*Renderer)
        │
        ▼
Metal telemetry pass  (NEW — low-res, e.g. 64×36; reuses the DE march)
        │
        ▼
FractalSonicFrame  (+ FractalSonicCell[] for spatial, later)
        │
        ▼
Mapping + smoothing layer  (audio-rate slew limiting)
        │
        ▼
Synthesis / DSP layer (NEW — generates PCM)   ← OpenAL does NOT do this
        │
        ├── offline: write WAV → reuse existing player + ffmpeg mux
        └── live:    NEW streaming output (OpenAL buffer-queue or AVAudioEngine)
```

**Three distinct layers — keep them separate:**
1. **Telemetry extraction** (Metal): geometry → numbers.
2. **Synthesis/DSP** (C#): numbers → PCM samples. *You write this.* Oscillators, filters, noise/granular, one-pole smoothers.
3. **Output/spatialization**: PCM → speakers. OpenAL (or AVAudioEngine) handles positioning, distance attenuation, Doppler, HRTF. **OpenAL produces no timbre** — it only places sound sources in space.

A common mistake (made twice in the planning so far) is treating OpenAL as the synth. It is not. Granular density, comb filters, additive/vowel timbres, resonant drones — all of that is layer 2, which does not exist yet.

---

## 2. Critical technical constraints

**The geometry data is computed but currently discarded.** In `mandelbox_raymarch.metal`:
- `estimateFull(p, fp, outTrap)` writes a `float4` orbit trap = (orbit radius, axis proximity, plane proximity, shell distance). Confirmed present.
- `traceRay(...)` runs the march loop — step count exists only as the loop counter; hit depth is `t`; the normal comes from `estimateNormal`, whose comment notes it *clobbers* the trap.
- The kernel's output is `device float4* output [[buffer(2)]]` — a color per pixel. Step count, depth, normal, and trap are **not** surfaced.

So the telemetry work is **"capture and reduce data the kernel currently throws away,"** not "read what's already emitted."

**Real-time audio safety (applies the moment you add live output, M4).**
- Telemetry frames are produced on the render/UI thread after a GPU readback. Audio is generated on a real-time callback/worker that **must never block** on the renderer.
- Hand off the latest frame lock-free: publish an immutable `FractalSonicFrame` via `Interlocked.Exchange` (or a triple-buffer); the audio side reads the latest and never waits. **Note:** `Interlocked.Exchange` needs a *reference* type, so `FractalSonicFrame` must be an immutable `record class`, not a `record struct` (see M0). If you want a struct, use a triple-buffer of structs with an atomic write-index instead.
- **Decouple the telemetry control rate from both the render rate and the audio rate.** Define an explicit control rate (target ~20–30 Hz) at which `FractalSonicFrame`s are produced; do **not** tie it to the render frame rate (which ranges ~14–200 Hz across fractals and spikes on the heavy DEs). The audio thread interpolates/slews between the most recent frames at sample rate. This bounds telemetry cost and makes the mapping deterministic regardless of render speed.
- **Smooth every mapped parameter** at audio sample rate (one-pole / slew limit). Without this you get zipper noise on every value and clicks whenever a keyframe cuts a value discontinuously.
- Never allocate on the audio thread. If a frame is stale (renderer stalled — this renderer is compute-heavy and *will* spike), hold the last value; never glitch.

**Readback is cheap, dispatch dominates** (per the repo's own Metal timing: `LastReadbackMs` vs `LastComputeMs`). Implication for the telemetry pass: see §3, option A vs B.

**Determinism for offline export.** Fixed timestep, seed any randomness, identical telemetry → identical PCM. Mirror the existing reactive `ApplyAtTime(t)` pattern.

---

## 3. Telemetry pass: two implementation options

**Option A — extend the existing render kernel.** Add a small telemetry output buffer to `mandelbox_raymarch`; accumulate per-thread (hit flag, depth `t`, step count, normal, trap) and reduce to a low-res grid. Reuses the expensive march. More invasive to a shader that's still being stabilized. **Avoid threadgroup float atomics for the reduction** — `atomic_float` is gated on GPU family / Metal 3 and hurts portability; prefer writing per-thread results to a buffer and reducing afterward (CPU or a follow-up kernel). Note also that Option A inherits the render kernel's SSAA Halton jitter, which would perturb telemetry per sample and break determinism.

**Option B — separate low-res telemetry kernel (recommended to start).** A dedicated kernel that re-marches at 64×36 (≈2,300 rays) purely for telemetry, sharing the same DE/trap helper functions. It's a second dispatch, but at this resolution it's cheap in absolute terms, and it keeps the render kernel untouched. Cleaner to develop and revert.

Either way, read the telemetry buffer back alongside the existing readback (it's already timed and cheap on unified memory). **Reduce the ~2,300-cell grid on the CPU** (trivial, <0.1 ms) into one `FractalSonicFrame` — this is the firm choice, and it sidesteps the GPU float-atomics portability issue above. Option B (no SSAA jitter, render kernel untouched) is the recommended starting point.

**Cost caveat:** a 64×36 re-march duplicates the most expensive part of the frame — the DE evaluations. On the heavy DEs this is not free: Kleinian (~23 ms full-res, numerical-gradient, 7 DE calls/estimate) and Anisotropic (4 orbits/pixel, finite-difference Jacobian) will dominate even at 2,300 rays. Combined with the decoupled ~20–30 Hz control rate (§2), keep the telemetry resolution low and do not run it every rendered frame.

---

## 4. What to reuse vs build new

| Need | Reuse / New |
|---|---|
| Offline WAV synthesis → file | **New** DSP (layer 2) writing PCM; WAV write helper (repo has WAV *decode*; add encode) |
| Offline playback of generated WAV | **Reuse** `OpenALAudioPlaybackBackend.OpenAsync(Uri)` |
| Offline mux audio+video | **Reuse** the ffmpeg mux path from `AudioModulationController` (invert the flow) |
| Timeline / camera / param-velocity sources | **Reuse** `Timeline`, `KeyframeBank`, `FlyCamera` |
| Live streaming output | **New** — existing player can't stream; add OpenAL buffer-queue source or AVAudioEngine |
| Spatial positioning (M5) | **Reuse** OpenAL 3D sources (it's built for exactly this) |

**Engine recommendation:** Do **offline first** — it has no real-time constraints, is deterministic, and reuses the player + mux. Prove the mappings sound good before taking on real-time audio. For live (M4), the decision is **OpenAL streaming source** (buffer queue): OpenTK.Audio.OpenAL is already referenced and the macOS-15 openal-soft fix already exists in `OpenALAudioPlaybackBackend`, so it adds no new dependency or interop surface. **AVAudioEngine is explicitly out of scope** — it would require a Swift/ObjC interop shim that complicates the build and the still-pending M11 notarization. Revisit it only if spatialization quality (M5) proves insufficient with OpenAL, and treat that as a separate, justified decision rather than a default.

---

## 5. Milestones (each independently shippable)

### M0 — Project scaffolding
- New namespace `Parsec.Audio.Sonification` (under `Parsec.Audio` or a new `Parsec.Audio.Sonification` project referenced by `Parsec.App`).
- Define `FractalSonicFrame` as an **immutable `record class`** (not a `record struct` — it's published lock-free via `Interlocked.Exchange`, which needs a reference type; see §2): `Time, HitRatio, MeanDepth, DepthVariance, StepMean, StepP90, NormalMean(Vector3), NormalVariance, TrapMean(Vector4), TrapVariance(Vector4), CameraSpeed, ParameterVelocity`.
- **Accept:** solution builds; no impact on existing audio-reactive code.

### M1 — Telemetry model + debug overlay, no sound
- Populate `CameraSpeed` and `ParameterVelocity` on the CPU. **`FlyCamera` exposes only `Position` (no velocity field)** — the sonification controller must track the previous position + timestamp and compute `‖Δposition‖ / Δt` itself; same pattern for `ParameterVelocity` from `Timeline`/`KeyframeBank` sampled values. Stub the geometry fields.
- Show live values in the status/debug panel, e.g. `hit=0.42 depth=3.1 steps=71 nVar=0.28 trap=(0.12,0.04,0.31,0.80)`.
- **Accept:** values update as the camera moves and animation plays.

### M2 — Metal telemetry pass for Mandelbox only
- Implement Option B (separate low-res kernel) reusing the Mandelbox DE/trap helpers.
- Fill the geometry fields of `FractalSonicFrame` from the reduced grid.
- **Accept:** flying toward folded/detailed regions visibly raises step counts and normal variance and lowers mean depth, in the overlay. (Mandelbox first because its fold/trap structure is musically rich and it's the template renderer.)

### M3 — Offline "fractal drone" + deterministic AV export
- C# DSP core (layer 2): a few oscillators, a low-pass filter, a noise/granular source, one-pole smoothers. Backend-neutral PCM output.
- Mapping: `HitRatio→gain`, `MeanDepth→LPF cutoff/bass`, `StepP90→granular/noise density`, `NormalVariance→brightness/roughness`, `TrapMean→harmonic blend`, `CameraSpeed→motion layer`.
- Render timeline deterministically → synthesize WAV → mux with exported frames via the existing ffmpeg path.
- **Accept:** an exported clip where the audio is audibly tied to the geometry, reproducible byte-for-byte across runs.

### M4 — Live preview (real-time)
- Add a streaming OpenAL output (buffer queue) on a dedicated audio worker thread.
- Lock-free latest-frame handoff; audio-rate smoothing; stale-frame hold; zero allocation on the audio thread.
- Guard the feedback loop (mutually exclusive with reactive modulation, or read base geometry).
- **Accept:** flying around produces click-free, responsive sound; dropped render frames don't glitch the audio.

### M5 — Spatial emitters
- Split telemetry into 16–32 world cells → `FractalSonicCell[]` (WorldPosition, HitRatio, MeanDepth, StepComplexity, NormalMean, TrapMean, Energy).
- Map each cell to an OpenAL 3D source (position, gain, tone color).
- **Accept:** turning the camera changes the spatial sound field, not just overall intensity.

### M6 — Per-fractal sonic identities (rollout)
- Scope: there are **20 Metal renderers** covering 21 selectable 3D fractals (AmazingBox shares the Mandelbox renderer). Add the telemetry kernel to the other Metal shaders (the march/shading tail is largely shared; the DE section differs).
- **`Attractor` is out of scope for sonification** — it is the one selectable 3D fractal with no Metal renderer (it sphere-traces a prebuilt `AttractorHash` over SSBOs, not a closed-form DE) and currently shows a blank placeholder on macOS. Octonion/Triball/IFS have OpenGL renderers but are not exposed in the desktop dropdown, so ignore them too.
- Give each fractal a voice: Mandelbox = metallic folds/combs; Mandelbulb = additive/vowel pads; Quaternion Julia = smooth spatial pads; Burning Ship = brittle crackle/cusp distortion; Menger/Kleinian = hollow cavities/delays; Hybrids = layered/crossfaded.
- **Accept:** at least 3 fractals have distinct, recognizable voices.

---

## 6. Key file pointers (verify before editing)

- `src/Parsec.Rendering.Metal/Shaders/mandelbox_raymarch.metal` — kernel `mandelbox_raymarch`; buffers `FoldParams[[0]]`, `RenderParams[[1]]`, `device float4* output[[2]]`; `estimateFull` writes the float4 trap; `traceRay` has the step loop.
- `src/Parsec.Rendering.Metal/MetalMandelboxRenderer.cs` — implements `IThreeDimensionalRenderBackend`; has `LastComputeMs`/`LastReadbackMs`; `RenderMandelbox(...) → uint[]`. Model the telemetry readback on the existing one.
- `src/Parsec.App/FractalView.cs` — holds every `Metal*Renderer`; `InitMetalRenderers()`. The non-Mandelbox renderers are invoked directly here, **not** through `IThreeDimensionalRenderBackend`.
- `src/Parsec.App/FlyCamera.cs` — camera state for `CameraSpeed`.
- `src/Parsec.App/Timeline.cs`, `KeyframeBank.cs` — for `ParameterVelocity` and deterministic export.
- `src/Parsec.Audio/Parsec.Audio.csproj` — net9.0, `OpenTK.Audio.OpenAL` 4.9.4.
- `src/Parsec.Audio/OpenALAudioPlaybackBackend.cs` — the macOS-15 openal-soft override (reuse this device-init logic for the streaming output).
- `src/Parsec.App/AudioModulationController.cs` — the ffmpeg mux + deterministic `ApplyAtTime(t)` pattern to mirror for export.

---

## 7. Pitfalls

- Do **not** name the new type `FractalAudioFrame` (collides conceptually with `AudioFeatureFrame`).
- Do **not** try to feed live audio through `OpenALAudioPlaybackSession` — it's a whole-file player; live needs a new streaming source.
- Do **not** sonify the packed `uint[]` image — extract from the DE march.
- Do **not** put DSP on the UI/render thread, or block the audio thread on the renderer.
- Do **not** ship all 20 fractals before one sounds genuinely good. Make Mandelbox excellent first.
