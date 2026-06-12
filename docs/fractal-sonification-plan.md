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

### M0 — Project scaffolding ✓ DONE
- New namespace `Parsec.Audio.Sonification` (under `Parsec.Audio` or a new `Parsec.Audio.Sonification` project referenced by `Parsec.App`).
- Define `FractalSonicFrame` as an **immutable `record class`** (not a `record struct` — it's published lock-free via `Interlocked.Exchange`, which needs a reference type; see §2): `Time, HitRatio, MeanDepth, DepthVariance, StepMean, StepP90, NormalMean(Vector3), NormalVariance, TrapMean(Vector4), TrapVariance(Vector4), CameraSpeed, ParameterVelocity`.
- **Accept:** solution builds; no impact on existing audio-reactive code.

### M1 — Telemetry model + debug overlay, no sound ✓ DONE
- Populate `CameraSpeed` and `ParameterVelocity` on the CPU. **`FlyCamera` exposes only `Position` (no velocity field)** — the sonification controller must track the previous position + timestamp and compute `‖Δposition‖ / Δt` itself; same pattern for `ParameterVelocity` from `Timeline`/`KeyframeBank` sampled values. Stub the geometry fields.
- Show live values in the status/debug panel, e.g. `hit=0.42 depth=3.1 steps=71 nVar=0.28 trap=(0.12,0.04,0.31,0.80)`.
- **Accept:** values update as the camera moves and animation plays.

### M2 — Metal telemetry pass for Mandelbox only ✓ DONE
- Implement Option B (separate low-res kernel) reusing the Mandelbox DE/trap helpers.
- Fill the geometry fields of `FractalSonicFrame` from the reduced grid.
- **Accept:** flying toward folded/detailed regions visibly raises step counts and normal variance and lowers mean depth, in the overlay. (Mandelbox first because its fold/trap structure is musically rich and it's the template renderer.)

### M3 — Offline "fractal drone" + deterministic AV export ✓ DONE
- C# DSP core (layer 2): a few oscillators, a low-pass filter, a noise/granular source, one-pole smoothers. Backend-neutral PCM output.
- Mapping: `HitRatio→gain`, `MeanDepth→LPF cutoff/bass`, `StepP90→granular/noise density`, `NormalVariance→brightness/roughness`, `TrapMean→harmonic blend`, `CameraSpeed→motion layer`.
- Render timeline deterministically → synthesize WAV → mux with exported frames via the existing ffmpeg path. Transparent export mode keeps the same audio path but uses alpha-keyed PNG frames and MOV/ProRes 4444 instead of MP4/H.264.
- **Accept:** an exported clip where the audio is audibly tied to the geometry, reproducible byte-for-byte across runs.

### M4 — Live preview (real-time) ✓ DONE
- Add a streaming OpenAL output (buffer queue) on a dedicated audio worker thread.
- Lock-free latest-frame handoff; audio-rate smoothing; stale-frame hold; zero allocation on the audio thread.
- Guard the feedback loop (mutually exclusive with reactive modulation, or read base geometry).
- **Accept:** flying around produces click-free, responsive sound; dropped render frames don't glitch the audio.

### M5 — Spatial emitters ✓ DONE
- Split telemetry into 16–32 world cells → `FractalSonicCell[]` (WorldPosition, HitRatio, MeanDepth, StepComplexity, NormalMean, TrapMean, Energy).
- Map each cell to an OpenAL 3D source (position, gain, tone color).
- **Accept:** turning the camera changes the spatial sound field, not just overall intensity.

### M6 — Per-fractal sonic identities (rollout) ✓ DONE
- Scope: there are **20 Metal renderers** covering 21 selectable 3D fractals (AmazingBox shares the Mandelbox renderer). Add the telemetry kernel to the other Metal shaders (the march/shading tail is largely shared; the DE section differs).
- **`Attractor` is out of scope for sonification** — it is the one selectable 3D fractal with no Metal renderer (it sphere-traces a prebuilt `AttractorHash` over SSBOs, not a closed-form DE) and currently shows a blank placeholder on macOS. Octonion/Triball/IFS have OpenGL renderers but are not exposed in the desktop dropdown, so ignore them too.
- Give each fractal a voice: Mandelbox = metallic folds/combs; Mandelbulb = additive/vowel pads; Quaternion Julia = smooth spatial pads; Burning Ship = brittle crackle/cusp distortion; Menger/Kleinian = hollow cavities/delays; Hybrids = layered/crossfaded.
- **Accept:** at least 3 fractals have distinct, recognizable voices.

---

## Post-M6 assessment: statistics-based DSP is insufficient

M6 shipped four distinct synthesis algorithms (comb resonators, additive pad, Schroeder reverb,
ring mod + distortion) driven by per-frame aggregate statistics (HitRatio, MeanDepth, StepP90,
NormalVariance, TrapMean). Orbit-animation test clips revealed the fundamental problem:

- Aggregate statistics change slowly and smoothly — they do not carry the fractal's intrinsic
  mathematical texture. Orbiting the Mandelbulb produces nearly constant values (it is near-
  spherically symmetric at observation distance); orbiting the Kleinian drives reverb feedback
  but sounds like a reverb sweep, not geometry.
- The BurningShip ring-mod voice was the only clip that sounded interesting, because StepP90
  varied meaningfully across the orbit. But the timbre is disconnected from the visible fractal.

**Root cause:** the architecture (geometry → 6 statistics → modulate a drone) is the wrong model —
but the obvious fix is also wrong. The video inspiration ("What do Fractals Sound Like? Part II",
oZFySzkF-Ww) demonstrates **raw trajectory synthesis**: the iteration sequence itself becomes the
audio signal (the DE value at every march step, played directly as PCM). This is what CodeParade's
Fractal Sound Explorer does. It is *mathematically* faithful but **inherently noisy** — a fractal
orbit is chaotic and aperiodic, so its spectrum is broadband. Playing it raw sounds harsh and buzzy,
which the project owner has explicitly rejected: the goal is to sit **between** "music as
math/geometry" and "simply beautiful," never sacrificing the audiovisual aesthetic to be
mathematically literal in the sound.

So statistics-based DSP is too smooth and disconnected; raw trajectory synthesis is too noisy.
M7 takes the middle path.

> **Revision 2026-06-11:** after reviewing CodeParade's FractalSoundExplorer directly, the project
> owner has requested direct-orbit synthesis as an **additional, selectable mode** (M9) alongside
> the M7/M8 hybrid voices — with explicit anti-noise safeguards (interpolation, decay envelopes,
> safety filter chain). The rejection above applies to *unfiltered* DE-march-as-PCM; M9 is not that.
> Do not cite this section as a reason to skip M9.

### M7 — Geometry-as-instrument synthesis (planned, revised 2026-06-09)

**Design principle: the geometry decides *what* sounds; a fixed musical framework guarantees it is
*consonant*.** Two jobs, kept separate. The fractal chooses timbre, which voices are active, and how
they evolve; a scale/temperament layer ensures the result is beautiful rather than buzzy. The chaos
becomes structure instead of reaching the ear unfiltered. This preserves the honest "the sound is
derived from the geometry" connection while removing the noise.

**Chosen aesthetic (project owner, 2026-06-09):**
- **Palette: hybrid drone + bells.** A sustained harmonic pad bed (timbre layer) with struck,
  decaying resonant voices on top (harmonic layer). *Fallback:* if this proves not to taste, pivot
  to "tonal but textured" (keep some geometry grit, scale-locked) — design the layers so the bell
  layer can be dialed back toward textured grains without an architecture change.
- **Tuning: just intonation, with a continuous "purity / temperament" knob.** This is the realized
  form of the "between math and beauty" axis. The knob blends per-voice frequency between the raw
  geometry-derived value and its snapped just-intonation pitch:
  `f_out = lerp(f_geometry, f_ji_snapped, temperamentStrength)`.
  At `1.0` → strict JI (maximally consonant). At `0.0` → microtonal, frequencies taken directly from
  geometry (maximally faithful, the "pure" derivation the owner is curious about). Mid-values give
  microtonally-inflected-but-anchored tunings. One knob spans the whole spectrum the owner described.

**Synthesis: two layers, both geometry-driven, both routed through the tuning/temperament layer.**

1. **Timbre layer (drone pad) — wavetable *from* geometry, not waveform.** This reframes the video's
   idea to kill the noise. A geometry-derived trajectory is used as a **single-cycle wavetable** — its
   *shape* is the timbre fingerprint (Mandelbox folds vs. Mandelbulb smoothness give genuinely
   different wavetables) — but it is read out at a **scale-locked pitch**, not at whatever frequency
   the raw sample rate implies. "The waveform is literally the fractal's shape," but pitched musically.
   Sustained, with slow morphing as the wavetable updates between telemetry frames (crossfade to avoid
   clicks). **Two candidate wavetable sources, both captured by the telemetry kernel so they can be
   A/B'd at the M7c gate (added 2026-06-09):**
   - **(a) Ray DE-step sequence** — the march-step values along a representative ray per cell.
     Camera-dependent: moving the camera changes the timbre even when the fractal doesn't.
   - **(b) Inner iteration orbit** — the z-trajectory of a representative point under the fractal map
     itself (the inner loop the DE already runs). Camera-*independent*: the Mandelbox's fold dynamics
     literally become the waveform, and *parameter* animation (not camera motion) morphs it.
     Quasi-periodic orbits give tonal, harmonically rich cycles — the opposite failure mode from raw
     trajectory noise.

2. **Harmonic layer (struck bells) — modal/resonator voices from the spatial cells.** Reuses the M5
   16-cell grid. Each cell maps to a tuned resonator (bell/glass/FM-bell character); geometry sets its
   frequency (from orbit-trap distance / DE periodicity, snapped via the temperament knob) and decay
   time (from convergence rate / hit ratio). The camera defines a geometry-derived *chord*; flying
   through the fractal changes which cells are near/loud → which notes ring → the animation
   *arpeggiates the chord defined by the geometry around you*. This is the "animation drives the
   modulation" the owner wants, expressed musically rather than as a filter sweep.

3. **Beauty infrastructure (not decoration):**
   - **Octave-from-zoom / self-similarity → Shepard–Risset layer (extended 2026-06-09):**
     frequency-doubling is the audible form of scale-invariance. Drive octave transposition from the
     zoom/scale parameter so the sound is self-similar across zoom — and self-similarity in frequency
     *is* octave consonance. Extend this to a full **Shepard–Risset glissando** for camera dives:
     octave-spaced partials under a fixed spectral envelope, glide rate tracking zoom/dive velocity,
     partials sitting on the active JI scale. A pitch that descends forever without arriving — the
     audio illusion matches the visual illusion of endless zoom. Cheap (octave-stacked oscillators
     with crossfading gains). Build as M7f.
   - **Geometry-driven temperament (added 2026-06-09):** the purity knob is not just a user slider.
     Drive `TemperamentStrength` per-voice from a local chaos measure (per-cell `NormalVariance` or
     trap variance): smooth, orderly regions of the fractal sound purely consonant; chaotic, crinkly
     regions slacken toward the raw microtonal geometry pitch. The math/beauty axis becomes *audible
     structure* rather than a static preference. `JiQuantizer` already supports this (mutable
     `TemperamentStrength`, clamped [0,1]); the UI knob (M7e) becomes the ceiling/bias that the
     geometry-driven value scales within, not the value itself.
   - **Global reverb send:** reuse the Schroeder reverb already built for the Kleinian voice.
   - **Portamento / heavy slew** on every pitch and the temperament blend, so discrete changes become
     liquid morphs and the whole thing reads as "designed," not "data."

**What changes vs. what is reused.** This is a *mapping + voicing* change, not an infrastructure
rebuild. Reused unchanged: the 64×36 telemetry kernels, the M5 spatial-cell partition, the OpenAL
buffer-queue streaming worker, the Schroeder reverb, the offline-WAV-then-mux export path, the
lock-free frame handoff. New: the wavetable oscillator, the modal resonator bank, the JI/temperament
quantizer, octave-from-zoom. The telemetry shader gains **two** small per-cell captures (the two
candidate wavetable sources above): a representative-ray DE-step sequence and a representative-point
inner iteration orbit — each a short single-cycle wavetable, e.g. 64–128 samples, *not* the full
`64×36×MaxSteps` dump the old note proposed. Far less data; only the wavetable shapes are needed,
not every ray.

4. **Offline + live:** unchanged two-track approach — offline deterministic WAV first (prove the
   mappings sound good with no real-time constraint), then streaming via the existing OpenAL worker.

**Build order (each independently checkable):**
- **M7a (✓ done):** JI/temperament quantizer + scale module (pure C#, unit-checkable: feed
  frequencies, verify snapping and the lerp knob). No geometry yet. Shipped as `JiScale` /
  `JiQuantizer` in `Parsec.Audio.Sonification`; CLI check `m7a-check`.
- **M7b (✓ done):** Telemetry kernel emits per-cell single-cycle wavetables from **both** candidate sources —
  ray DE-step sequence *and* inner iteration orbit (Mandelbox first). Both are small fixed-size
  buffers; capturing both now is what makes the M7c A/B possible without a second shader pass later.
- **M7c (✓ done):** Offline hybrid synth (`HybridSynth.Synthesize`) — wavetable drone + 16 modal bell
  resonators (JI Dorian at A2 = 110 Hz), scale-locked with **geometry-driven per-voice temperament**
  (`NormalVariance` → `TemperamentStrength`), Schroeder reverb send. A/B: inner-orbit (OrbitMags)
  selected as winner — camera-independent timbre, harmonically rich quasi-periodic orbits. CLI:
  `metal-m7c-orbit` / `metal-m7c-spiral`.
- **M7d (✓ done):** Live streaming — `FractalDroneStream.FillMandelboxHybrid` replaces the old
  comb-resonator Mandelbox voice. Energy-weighted multi-cell wavetable blend, first-activation bell
  triggers (threshold-crossing `energy ≥ 0.12`), JiQuantizer for drone + resonator pitch. Reverb
  reuses Kleinian allpass/comb buffers. **Critical allpass fix:** the 2-tap Schroeder form
  `y = -g*x + buf[rp] + g*buf[(rp+1)%len]` has DC feedback g(1+g) > 1 for g=0.65 → unstable,
  saturates in ~1.25 s. Fix: standard 1-tap `y = -g*x + buf[rp]` applied to both `AllPass`
  (HybridSynth) and `StreamAllPass` (FractalDroneStream). CLI: `metal-m7d-orbit` /
  `metal-m7d-spiral`.
- **M7e (done):** Per-fractal wavetable/decay character for Mandelbulb / Kleinian / BurningShip;
  exposed `TemperamentCeiling` slider in UI. Extended all three telemetry shaders
  (`mandelbulb_telemetry.metal`, `kleinian_telemetry.metal`, `burningship_telemetry.metal`) with
  `captureOrbitWavetable` and a `WavetableCell` buffer(3). `TelemetryReduction.ReadOrbitWavetables`
  shared helper reads and normalises wavetables; all three renderers enrich `MetalSpatialCell` with
  `OrbitWavetable`. `FillKleinianHybrid` (Dorian A1, 3–9 s cavernous bells) and
  `FillBurningShipHybrid` (Dorian C3, 0.08–0.45 s percussive bells) replace the old M6 voices in
  `FractalDroneStream`. `FillMandelbulbHybrid` (HarmonicSeries A2, 1.5–4.5 s airy bells). All four
  voices use the shared hybrid pattern: energy-weighted orbit wavetable blend + JiQuantizer +
  Schroeder reverb. `TemperamentCeiling` public property wires through geometry-driven
  `TemperamentStrength` per-frame. CLI: `metal-m7e-mandelbulb` / `metal-m7e-kleinian` /
  `metal-m7e-burningship`.
- **M7f (✓ done):** Shepard–Risset zoom layer — N=7 octave-spaced sinusoidal partials (A0–A5,
  27.5–1760 Hz) under a Gaussian spectral envelope (σ=1.5 oct, centre A3=220 Hz). Glide rate
  driven by `ZoomVelocity` (new `FractalSonicFrame` field: forward dot camera displacement × control
  rate). `SonificationController.ComputeCameraMotion` returns both scalar speed and signed zoom
  velocity in one pass. `SampleShepard()` in `FractalDroneStream` accumulates `_srBase` (log2-freq
  offset, wraps mod 1 each octave) per sample; Gaussian gain recomputed each sample from `logOct =
  SR_LOG2 + _srBase + i`. Mixed at 0.13 gain after the tanh limiter; added to all four voices
  (`FillMandelboxHybrid`, `FillMandelbulbHybrid`, `FillKleinianHybrid`, `FillBurningShipHybrid`) and
  to `HybridSynth.Synthesize`. CLI: `metal-m7f-shepard [duration] [outDir]`.
  Also shipped as part of M7f: **drone pitch portamento** (`_wtPitchS` one-pole slew τ=0.28 s in
  both streaming and offline paths) — radial zoom paths exposed frame-rate pitch stepping that
  circular orbit paths had not. Drone gain reduced ~40% across all voices to rebalance against bells
  and Shepard layer.
- **M7g (✓ done):** Geometry-native tuning systems. New `GeometryScale.cs` (Parsec.Audio.Sonification):
  - **Apollonian** — canonical integer Apollonian gasket seeds {-1,2,2,3} → curvatures {2,3,15,35,38}
    → 5-note JI pentatonic {1/1, 35/32, 19/16, 3/2, 15/8} at A2 root. New `FractalVoice.Apollonian`
    (no telemetry needed): bells triggered by camera-speed timer (0.8 s→0.25 s as speed increases),
    pitch cycles through JI scale across 3 octaves, Schroeder reverb + Shepard layer.
  - **Kleinian** — eigenvalue ratio `r = scale*(1+fixed/min)/2`; octave-reduced to [1,2) as generator
    interval. Default params (scale=2, fixed=1, min=0.5): r=3/2 → Pythagorean circle-of-fifths scale.
    `FillKleinianHybrid` / `HybridSynth.Synthesize` use `frame.GeometryPitches` when present — no
    JiQuantizer snap, the geometry IS the tuning.
  - `FractalSonicFrame.GeometryPitches float[]?` field; `SonificationController.Update()` accepts
    `geometryPitches` param; `FractalView.ComputeGeometryPitches()` computes from active fractal state;
    `MainWindow.ActiveTypeToVoice` maps Apollonian → FractalVoice.Apollonian.
  - CLI: `metal-m7g-apollonian [dur] [outDir]` / `metal-m7g-kleinian [dur] [outDir]`.
    Apollonian peak −12.8 dBFS; Kleinian peak −11.6 dBFS.
- **Decision gate after M7c:** two decisions — (1) if hybrid drone+bells isn't to taste, pivot the
  bell layer toward "tonal but textured" before investing in M7d–g; (2) pick the winning wavetable
  source (ray-steps vs. inner-orbit, or keep both as a blend) and drop the loser from the telemetry
  readback if it earns nothing.
- **M7h (✓ done):** DE-as-waveshaper. 64-point DE strip captured along camera-right direction in all
  four telemetry shaders (`buffer(4)` — `captureWaveshaperStrip` called from thread(0,0) once per
  dispatch). Strip normalised CPU-side by `TelemetryReduction.ReadWaveshaperCurve`. Passed through
  `FractalGeometryStats.WaveshaperCurve → FractalSonicFrame.WaveshaperCurve`. In the DSP layer, a
  sine at the slewed drone pitch is processed through the 64-sample table lookup (linear interp,
  `-1→0, 1→63`); mixed at 0.07 gain after the tanh limiter. All four streaming voices and offline
  `HybridSynth.Synthesize` include the layer; Apollonian is guarded (`isApolloVoice` — no telemetry).
  Waveshaper morphs at τ=50 ms (same as wavetable). CLI: `metal-m7h-waveshaper [dur] [outDir]`;
  peak −10.6 dBFS on the Mandelbox test orbit.
  - **DE-as-waveshaper (done — see above).**
  - **Event layer / self-similar rhythm:** discrete onsets from cell-energy threshold crossings, with
    a Cantor-set or Nørgård-infinity-series rhythm (literally fractal rhythm) keyed to zoom depth —
    extends self-similarity into *time*. Larger compositional effort and bigger taste risk than the
    others; try last.
  - **Space from geometry:** drive the Schroeder reverb's *parameters* from telemetry — size/pre-delay
    from `MeanDepth`, plus an inside-vs-outside detector (HitRatio near 1 with short depths = camera
    is in a cavity → long cavernous tail). Small step on top of the existing M7c reverb send; high
    immersion payoff; lowest-risk item in this bank.
  - **Explicitly rejected (do not revive without new evidence):** spectral resynthesis of the depth
    buffer (image-as-spectrum). It is the noisy CodeParade failure mode in 3D; rescuing it with
    peak-picking + JI snapping is more machinery than the result deserves given the wavetable path
    already exists.

### M8 — Field-scan synthesis ✓ DONE (stereo offline, 2026-06-10)

Conceptual source: SIREN (Sitzmann et al. 2020, arXiv:2006.09661). The fractal DE is the analytic
field SIREN approximates; no neural network involved.

**What was implemented:**

The original Lissajous orbit (single mono waveform) was replaced with two **vertical scan lines**
that produce independent L and R drone wavetables:

- **Left scan:** `p = camPos + camRight × (−0.4×scanR) + camUp × y_i`, y_i ∈ [−scanR, +scanR], 64 samples top-to-bottom.
- **Right scan:** same but `+0.4×scanR` on camRight.
- `scanR = boundSphere.w × 0.30`. `camRight` is already in `TelemetryParams` for both Mandelbox and Kleinian shaders.
- Metal kernels dispatch 128 threads (0–63 = L, 64–127 = R); output 128 floats AC-coupled and normalised per channel.

**`HybridSynth.Synthesize` is now stereo:**

- Returns interleaved `short[]` (L0, R0, L1, R1, …).
- L drone reads `currentWtL` (from `FieldScanWaveformL`); R drone reads `currentWtR` (from `FieldScanWaveformR`). Same shared pitch/phase.
- When no field-scan data is present (OrbitMags/RaySteps mode), both wavetables receive the same energy-weighted blend → effectively mono drone in a stereo container.
- 16 bell resonators panned by **cell column index** (column ci%4 ∈ {0,1,2,3} → pan ∈ {−0.75, −0.25, +0.25, +0.75}, constant-power law). Right-side cells ring right; left-side ring left.
- Mono Schroeder reverb bus (sum of all bells) applied equally to both channels.
- Shepard–Risset and DE-waveshaper layers remain mono centre.

**Other fixes shipped with M8:**

- Bell dry-tap gain raised 0.40→0.60; per-bell oscillator scale 0.025→0.040 (bells were inaudible).
- Voice-specific bell decay added to `HybridSynth` (was single formula for all voices):
  - Kleinian: 1.0 + HitRatio×2.0 s (1–3 s)
  - Mandelbulb: 1.5 + HitRatio×3.0 s (1.5–4.5 s)
  - BurningShip: 0.08 + HitRatio×0.37 s (0.08–0.45 s)
  - Default (Mandelbox/Apollonian): 2.5 + HitRatio×4.5 s
- Kleinian bell root raised A1 (55 Hz) → A3 (220 Hz); cells previously in sub-bass now ring at 220–1320 Hz.
- `WavEncoder.Write(path, samples, sampleRate, channels=1)` — optional `channels` int added; all `HybridSynth` CLI call sites pass `channels: 2`.
- `FractalGeometryStats` + `FractalSonicFrame`: `FieldScanWaveform` replaced by `FieldScanWaveformL` + `FieldScanWaveformR`.

**CLI:**
```
dotnet run ... -- metal-m8-fieldscan [kleinian|mandelbox] [duration] [out.mp4]
dotnet run ... -- metal-m8-kleinian-deep [duration] [out.mp4]   # slow 0.5-rev descent, 20s default
```
WAV is saved alongside the MP4 (`Path.ChangeExtension(outMp4, ".wav")`) so it survives temp-dir cleanup.

---

### M8-spatial — 4-corner drone scan + Y-axis elevation ✓ DONE (verified 2026-06-11)

**Verified end-to-end 2026-06-11:** 256-thread 4-quadrant kernels in `mandelbox_telemetry.metal`
(`mandelbox_fieldscan`) and `kleinian_telemetry.metal` (`kleinian_fieldscan`); both renderers'
`RunFieldScanPass` return `(tl, tr, bl, br)`; `TelemetryReduction.ReadFieldScanWaveform` AC-couples
and normalises each quadrant independently; `FractalGeometryStats`/`FractalSonicFrame` carry the four
`FieldScanWaveformTL/TR/BL/BR` fields; `HybridSynth` runs 4 corner oscillators (TL+BL → L,
TR+BR → R) with spectral-tilt shelves on drone corners (±0.35) and bell rows (+0.35/+0.12/−0.12/−0.35).
CLI run: 120/120 frames non-trivial field-scan data; muxed MP4 side-channel (L−R) RMS −32 dB —
real stereo decorrelation. **Implementation deviation from the sketch below:** each quadrant sweeps
`y` from 0 → ±scanR on `camUp` (top quadrants up, bottom down) rather than a fixed ±0.4×scanR
camUp offset plus sweep — functionally equivalent 4-corner coverage. Scope was offline-only
(`HybridSynth`); the live `FractalDroneStream` voices still use OrbitMags — live field-scan is
future work, not an M8-spatial gap. Original design sketch follows.

Replace the 2-scan-line drone with 4 scan points at the **corners of the camera's view** — top-left
(TL), top-right (TR), bottom-left (BL), bottom-right (BR) — each at `±0.4×scanR` on both
`camRight` and `camUp`:

```metal
// TL: xSign=-1, ySign=+1 | TR: xSign=+1, ySign=+1
// BL: xSign=-1, ySign=-1 | BR: xSign=+1, ySign=-1
float3 p = tp.camPos.xyz
         + tp.camRight.xyz * (xSign * scanR * 0.40f)
         + tp.camUp.xyz    * (ySign * scanR * 0.40f)
         + tp.camUp.xyz    * y;   // vertical sweep within each corner
```

Metal kernel: 256 threads → 256 floats (TL=0–63, TR=64–127, BL=128–191, BR=192–255).

**In `HybridSynth`:** 4 independent drone oscillators (shared pitch/phase, independent wavetables). Mix:
- Left channel  = blend(TL, BL)
- Right channel = blend(TR, BR)

**Vertical axis — spectral-tilt elevation cue:**

True binaural (HRTF) requires external libraries and is deferred. Instead, use psychoacoustic
spectral tilt: sounds above the listener's eye line are perceived as spectrally **brighter** (pinna
filtering); sounds below are **darker**. A 1-pole shelf filter per source achieves this without any
library:

```csharp
// For each source at normalised vertical position yNorm ∈ [-1, +1]:
// yNorm > 0: boost highs — mix (1-tiltAmt)*x + tiltAmt*highpass(x)
// yNorm < 0: attenuate highs — lowpass(x)
float tiltAmt = Math.Abs(yNorm) * 0.35f;  // max ±35% tilt at full elevation
```

Apply to:
- **Drone voices:** TL/TR tilt = +tilt (above); BL/BR tilt = -tilt (below).
- **Bells:** each of 4 rows (ci/4 ∈ {0,1,2,3}) gets a corresponding shelf — row 0 (top) brightest, row 3 (bottom) darkest.

No additional Metal passes required — only C# DSP changes in `HybridSynth.Synthesize`.

---

### M9 — Direct-orbit synthesis mode ✓ DONE (M9a–M9e + post-M9 refinements, 2026-06-11/12)

**Goal:** a second, selectable sonification mode where **the iteration map itself is the
oscillator**. Inspired by CodeParade's FractalSoundExplorer (FSE,
github.com/HackerPoet/FractalSoundExplorer): FSE iterates the 2D fractal map from a point and
plays the orbit displacement (dx, dy) directly as the L/R waveform — one orbit step per ~12
output samples with cosine interpolation. Pitch and timbre **emerge** from orbit periodicity
instead of being imposed by a synth. M9 is the 3D generalization.

**Why this gives each fractal a unique sound for free** (no per-fractal DSP voicing needed —
the dynamics differ, so the sounds differ):

| Fractal | Iteration structure | Expected emergent character |
|---|---|---|
| Mandelbox | box-fold + sphere inversion + scale | square-ish discontinuous waveforms; metallic, glitchy bursts from the inversion |
| Mandelbulb | triplex power-8 | phase-multiplication tones (≈FM ratio 8); the Power slider becomes an audible harmonicity control |
| Kleinian | Möbius/sphere inversions | loxodromic elements are literally damped complex oscillators — bells emerge from group theory |
| BurningShip | abs() folds | rectified waveforms → strong even harmonics, harsh/scorched |
| QuaternionJulia | rotation in two planes at once | two incommensurate frequencies per orbit → inharmonic, gong-like |

**Relationship to the Post-M6 rejection:** the rejected idea was raw *DE march steps* played
as PCM (broadband noise). M9 plays the *inner iteration orbit* — the same data source the M7b
wavetable already proved is quasi-periodic and tonal — but as a continuously streamed
trajectory rather than a 64-sample normalized cycle. The anti-noise safeguards below are part
of the spec, not optional polish.

**Architecture (all three layers already exist — this extends each):**

1. **Metal (capture):** extend the four telemetry kernels to record the full 3D orbit
   trajectory per spatial cell: `ORBTRAJ = 128` points of `float4` (xyz = orbit point,
   w = 1 while bounded / 0 after bailout), written to a **new separate `buffer(5)`**
   (`device float4* orbitTraj`, indexed `tileIdx * ORBTRAJ + i`, 16×128×16 B = 32 KB total).
   Capture at the **same site** as the existing `captureOrbitWavetable` (same interior-pulled
   seed, same fold loop — e.g. `mandelbox_telemetry.metal:203`), storing raw `z` after each
   iteration instead of `length(z)/(1+length(z))`. Clamp each component to ±4 before store
   (FSE clamps displacement; unbounded floats poison the CPU normalization).
2. **C# DSP (playback):** new `DirectOrbitSynth` (offline, stereo interleaved `short[]`, same
   contract as `HybridSynth.Synthesize`) + a streaming `Fill` variant in `FractalDroneStream`.
   Per cell voice: step through the orbit at a fixed **orbit step rate** (default
   `44100 / 12 = 3675` steps/s) with **cosine interpolation** between consecutive points
   (`t' = 0.5 - 0.5·cos(πt)` — FSE's formula; the interpolation is itself a low-pass and is
   the first anti-noise safeguard).
3. **Projection → stereo (the 3D upgrade over FSE):** world space *is* fractal space here, so
   project the centred orbit through the **camera basis** at synthesis time:
   `L += dot(p − centroid, camRight)`, `R += dot(p − centroid, camUp)`, and
   `dot(p − centroid, camForward)` → per-voice one-pole LPF cutoff (near/far = bright/dark).
   The same orbit sounds different as the camera flies around it — listener-relative geometry,
   consistent with the project thesis. Then pan by cell column exactly as the M8 bells do.

**Segment streaming, not looping (key timing insight):** 128 points × 12 samples/step =
1,536 samples ≈ 34.8 ms at 44.1 kHz, which is ≥ one 30 Hz control-frame interval (33.3 ms).
So each telemetry frame supplies one orbit *segment* that covers the frame interval: play
segments end-to-end with a short equal-power crossfade (~5 ms) between consecutive captures.
Never loop a single captured segment — looping a chaotic 1,536-sample window adds a false
~29 Hz periodicity (buzz) that has nothing to do with the geometry.

**Anti-noise / safety chain (owner-required — these are the caveat mitigations, in order):**
- Per-voice: subtract segment centroid (DC removal), normalize by segment peak with an epsilon
  guard (`if (peak < 1e-6f) gain = 0` — degenerate orbits go silent, not NaN).
- Escaped orbits (w = 0 tail): exponential decay envelope from the bailout index
  (FSE uses ×0.9992/sample; start there) — escapes become transients, not clicks.
- Per-voice DC blocker (one-pole HPF ~20 Hz) then a gentle one-pole LPF (~8 kHz default,
  exposed as a "Brightness" param) to tame chaotic-region harshness.
- Master bus: the existing tanh soft limiter, then the Shepard–Risset layer (keep at 0.13 —
  camera motion is information the orbit can't encode) and M8-spatial spectral-tilt rows.
- Accept that chaotic regions sound like *textured* noise — that is the honest sound of chaos
  and is the point of the mode; the chain above removes artifacts, not character.

**Mode plumbing:** new `SonificationMode { Hybrid, DirectOrbit }` enum — **orthogonal to
`FractalVoice`**, do not add a `FractalVoice.DirectOrbit` (mode × fractal must compose; the
voice enum keys per-fractal telemetry dispatch and must keep working in both modes). UI: a
"Sonify Mode" toggle next to the Live Sonify button. Render-to-Video export already captures
`FractalSonicFrame`s per export frame and synthesizes via the offline synth (wired 2026-06-11
in `MainWindow.OnRenderToVideoClick`) — pass the mode through to select
`DirectOrbitSynth` vs `HybridSynth` there. Track D added telemetry for Apollonian, so
DirectOrbit should run for Apollonian; its Hybrid timer-bell voice remains Hybrid-only.

**Build order:**
- **M9a — Metal trajectory capture (Mandelbox only). ✓ DONE (verified 2026-06-11)**
  `#define ORBTRAJ 128` + `captureOrbitTrajectory()` helper in `mandelbox_telemetry.metal`
  (same fold loop as M7b, stores `float4(clamp(z,-4,4), bounded_w)`). New `buffer(5)`,
  32 KB; `TelemetryReduction.ReadOrbitTrajectories` (direct `Vector4[]` memcopy, no
  normalization); `Vector4[]? OrbitTrajectory` on `MetalSpatialCell` + `FractalSonicCell`;
  passed through `SonificationController.ConvertCells`. CLI `metal-m9a-orbits` passes all
  accept criteria: 16/16 non-null 128-point trajectories; bounded counts 29–128 vary by
  tile (interior vs. edge seeds); most-energetic tile has fully-bounded orbit with
  varied x-values showing Mandelbox box-fold sign alternation.
- **M9b — Offline `DirectOrbitSynth` + decision gate. ✓ DONE (verified 2026-06-11)**
  `src/Parsec.Audio/Sonification/DirectOrbitSynth.cs`: FSE cosine interp at 44100/12 pts/s;
  projects orbit onto camera basis (camRight→L, camUp→R, camFwd→LPF); constant-power column
  pan; anti-noise chain: centroid subtract → peak-normalize (ε=1e-6 guard) → escaped-orbit
  decay ×0.9992/sample → DC blocker ~20 Hz → depth-modulated LPF → spectral-tilt rows →
  5 ms equal-power crossfade at segment boundaries → tanh + Shepard–Risset. CellScale=0.050.
  CLI `metal-m9b-direct [duration] [outDir]` writes `m9b_direct.wav` + `m9b_hybrid.wav`
  (M7c OrbitMags reference) for side-by-side A/B. Accept criteria all pass: deterministic
  (byte-identical) ✓, peak −6.4 dBFS ✓, 16/16 orbit trajectories ✓, Scale ramp 2.0→1.6→2.0
  demonstrates parameter-driven timbre change ✓.
- **M9c — Rollout to Mandelbulb / Kleinian / BurningShip. ✓ DONE (verified 2026-06-11)**
  `buffer(5)` + `captureOrbitTrajectory()` added to `mandelbulb_telemetry.metal`,
  `kleinian_telemetry.metal`, and `burningship_telemetry.metal` (same pattern as M9a
  Mandelbox; only the inner fold loop differs). `MetalMandelbulbRenderer`,
  `MetalKleinianRenderer`, and `MetalBurningShipRenderer` extended with `RunTelemetryPass`
  that allocates the 32 KB trajectory buffer and calls `TelemetryReduction.ReadOrbitTrajectories`.
  CLI `metal-m9c-direct [duration] [outDir]` renders all 4 fractals in a single pass,
  writing `m9c_mandelbox_direct.wav`, `m9c_mandelbulb_direct.wav`, `m9c_kleinian_direct.wav`,
  `m9c_burningship_direct.wav` for cross-fractal A/B. **Accept criteria met:** four fractals
  blind-distinguishable by ear in direct mode ✓.
- **M9d — Live streaming + UI + export passthrough. ✓ DONE (verified 2026-06-11)**
  `SonificationMode { Hybrid, DirectOrbit }` enum added to `FractalDroneSynth.cs` —
  orthogonal to `FractalVoice` (voice keys per-fractal telemetry dispatch; mode selects DSP
  algorithm; both must compose). `FractalDroneStream`: `FillDirectOrbit(short[])` with all
  per-cell state preallocated in constructor (`_doPrevSeg`/`_doNewSeg` ping-pong buffers,
  DC blocker, one-pole LPF, spectral-tilt arrays per cell — zero GC allocation on audio
  thread); `Mode` property switches at the next buffer boundary without restarting the
  worker thread; Apollonian voice falls back to `FillApollonianHybrid` (no telemetry
  kernel, guarded in `FillBuffer`). `MainWindow`: `_sonifyMode` field; `SonifyModeButton`
  next to Live Sonify toggles label between `"Hybrid"` and `"DirectOrbit"` and calls
  `_droneStream.Mode = _sonifyMode` on a live stream; mode is passed to the constructor
  when the stream starts. `OnRenderToVideoClick`: `exportSonifyMode` captured at click
  time; routes to `DirectOrbitSynth.Synthesize` when `DirectOrbit` (Apollonian → Hybrid
  fallback). CLI `metal-m9d-check [duration] [outDir]` writes `m9d_export_direct.wav`;
  verified peak −6.0 dBFS, enum orthogonality ✓, export routing ✓.
  Listening-test CLI variants with more geometry motion:
  `metal-m9d-animated [duration] [outDir]` writes `m9d_animated_direct.wav` using a Mandelbox
  helical camera spiral plus `Scale` modulation; `metal-m9d-animated-burning [duration] [outDir]`
  writes `m9d_burningship_animated_direct.wav` using 3D Burning Ship telemetry, a helical camera
  spiral, and `Power` modulation. Both still synthesize with `DirectOrbitSynth` and the same
  Pythagorean grid tuning.
- **M9e — Per-cell detuning + stereo fix + blend slider + crash fix + DirectOrbit reverb. ✓ DONE**
  - **Stereo balance fix:** original `L = dot(p, camRight)` / `R = dot(p, camUp)` gave near-
    silent R channel for Mandelbox (orbits confined to X axis). Fix: 45° rotation —
    `L = (pr + pu) × 0.70710678`, `R = (pr − pu) × 0.70710678`. Applied to both `DoProject()`
    (streaming) and `Project()` (offline `DirectOrbitSynth`).
  - **Per-cell tuning:** each of 16 cells steps at a unique orbit rate. Current map is a strict
    Pythagorean fifth lattice: bottom row is each column's root, rows above ascend by pure `3/2`
    fifths, and columns use related fifth-chain roots. Stored in preallocated `_doCellSps[]`.
  - **Continuous blend slider:** replaced the binary `SonifyModeButton` toggle with
    `SonifyBlendSlider` (`Hybrid ←→ DirectOrbit`, 0–1) in `MainWindow.axaml`. `FractalDroneStream`
    exposes a `BlendAmount` property; `FillBuffer` dispatches per-sample lerp between hybrid and
    direct-orbit outputs using a preallocated `_blendScratch` buffer (zero GC allocation on audio
    thread). Export: both synths produce full PCM arrays; lerped offline at the captured blend value.
  - **Hybrid streaming crash fix:** `FillDirectOrbit` was using `n = buf.Length / 2`, treating the
    mono buffer as stereo pairs — delivered only 512 effective samples per 1024-sample buffer, halving
    the audio rate and starving the OpenAL queue after ~30 s. Fixed to `n = buf.Length`. Added
    try-catch around `FillBuffer` in `WorkerLoop` (outputs silence on exception; keeps thread alive).
  - **Lush Freeverb reverb on DirectOrbit:** pre-delay (882 samples ≈ 20 ms) → 4 parallel comb-LPF
    filters (delays 2111/2237/2381/2521, fb=0.86, damp=0.20) → 4 series allpass filters
    (601/441/341/225, g=0.50). T60 ≈ 2.15 s, wet=0.25. Bloom mixed inside the tanh limiter for
    natural compression. State preallocated as `_doRev*` fields in `FractalDroneStream`; cleared on
    voice switch and `ResetDirectOrbitState`. Same 1-tap allpass form as Hybrid voice (2-tap is
    unstable — see M7d). Identical reverb added to `DirectOrbitSynth.Synthesize` (offline; local
    state). Static helpers `StreamCombLpf` / `StreamAllPass` (streaming) and `RevCombLpf` /
    `RevAllPass` (offline) implement the topology.
- **DirectOrbit stereo image + 16 distinct voices (post-M9e fixes). ✓ DONE**
  Two bugs diagnosed from playback testing:
  - **Escaped-orbit noise killed stereo:** `EscapeDecay = 0.9992` over 128 steps only reduces
    escaped-cell amplitude to `0.9992^128 ≈ 0.90`, so cells in empty space (fractal absent) were
    almost as loud as cells inside the fractal. When the fractal drifts left, right-column cells
    stayed nearly as loud → flat stereo image. Fix: zero all post-bailout orbit points and weight
    amplitude by `sqrt(bailout/OrbtLen)` (`DoPreprocessOrbitInPlace` / `PreprocessOrbit`). A cell
    that escapes immediately is fully silent; a half-bounded cell is at −3 dB. Reverb wet also
    reduced 0.40→0.25 to reduce mono bloom's interference with L/R differentiation.
  - **Only 3-4 voices heard instead of 16:** ±22.5¢ total detuning is chorus-width — all cells
    within a column sounded like 1 pitched voice, giving 4 spatial positions × 1 pitch = 4 voices.
    Fix: rows and columns now form a strict Pythagorean fifth lattice. Full-screen fractal occupancy
    yields related pure-fifth voices spread across sixteen pan positions.

- **Wall-of-voices + live DirectOrbit stereo (post-M9e). ✓ DONE**
  Follow-up from playback testing:
  - **Wall of 16:** bare `sqrt(boundedFraction)` made surface cells (most of a complex
    full-screen image) too quiet — only 1–2 deep interior cells were audible. Added a floor:
    `bailout==0 ? 0 : 0.30 + 0.70·sqrt(boundedFraction)`. Empty cells still silent (stereo gate),
    every cell with bounded content now audible. `CellScale` 0.050→0.065 (peak −10.4 dBFS).
  - **16 pan positions:** orbit panning was column-only, stacking 4 cells per position (heard as
    3–4 voices). Added a ±0.09 per-row pan dither → 16 distinct positions, left/right mapping intact.
  - **Projection-level voice floor:** 3D orbit normalisation was not enough because some valid cells
    projected mostly onto camera depth and became nearly silent in L/R. `DirectOrbitSynth` and
    `FractalDroneStream` now boost non-empty projected cell RMS up to a bounded floor
    (`MinProjectedRms=0.30`, max 6×).
  - **Shepard + Hybrid drone stereo:** Shepard was mono-centred in both synths → now panned to the
    energy-weighted on-screen fractal centroid (√2-scaled). `HybridSynth` drone was effectively mono
    in `OrbitMags` mode (4 shared corners) → now column-split (cols 0,1→left, 2,3→right) with a
    slewed per-channel balance gain, so the drone follows the fractal across the stereo field.
  - **Live DirectOrbit stereo:** `FractalDroneStream` now queues the ambient source as
    `ALFormat.Stereo16` with doubled interleaved buffers. Hybrid live voices are duplicated mono
    into stereo before blending; DirectOrbit writes real L/R samples so the 4×4 pan field is audible
    during live playback, not only in exported WAV/MP4 audio.

- **DirectOrbit Pythagorean grid tuning (done).**
  The cent-detune map is replaced with strict Pythagorean fifth ratios. The bottom row of each
  column is the root geometry voice; rows above it ascend by pure `3/2` fifths:
  row 3 = root, row 2 = root×3/2, row 1 = root×(3/2)^2, row 0 = root×(3/2)^3.
  Columns should also use related roots in the same fifth chain, e.g. for a C2 base:
  col 0 = C2, col 1 = G2, col 2 = D3, col 3 = A3. A fully occupied column then yields a strict
  fifth stack such as C2/G2/D3/A3, and a full screen yields a related Pythagorean lattice. Keep
  implementation in sync between `DirectOrbitSynth` and `FractalDroneStream`. If the full
  `(3/2)^6` range is too wide, octave-reduce a chosen axis or the final rate ratio while preserving
  pure adjacent fifth relationships.

- **Proximity/enclosure macros + fold-event chimes. ✓ DONE (verified 2026-06-11)**
  Addresses two owner-reported gaps: "sounds the same far away as inside" and "morphing isn't
  audible". DirectOrbit path only; implemented identically in `DirectOrbitSynth.Synthesize` and
  `FractalDroneStream.FillDirectOrbit`.
  - **Proximity macro** `exp(−MeanDepth/2.5) · min(1, HitRatio×5)` — the HitRatio gate is required
    because `MeanDepth` is reported as 0 when no rays hit, which would otherwise read as "at the
    surface". **Enclosure macro** = `HitRatio`. Both slewed with τ = 0.35 s. They drive: master
    brightness LPF (1.2 kHz far → 10 kHz at surface), bass shelf lift (0.9·prox at 180 Hz),
    dry gain (0.45 → 1.0), and the Freeverb fb/damp/wet (now per-frame variables interpolated
    between profile endpoints — open void = short dry tail, fully enclosed = seconds of bloom).
    Measured on `metal-m9d-animated`: −37 dBFS far → −15 dBFS inside.
  - **Morph bus** `clamp(ParameterVelocity × 4, 0, 1)`, attack τ = 0.08 s / release τ = 1.2 s.
    Drives per-cell pitch shimmer (±~35 cents at morph = 1, distinct per-cell LFO rates derived
    from `frame.Time`, applied via `cellSpsEff`) and fold-detection sensitivity.
  - **Fold-event chimes:** per-cell frame-to-frame orbit delta (mean pointwise distance between
    consecutive preprocessed segments — both peak-normalised, so it is scale-free). Top-3 cells
    above threshold (`0.45 − 0.25·morph`) fire two-partial decaying sines (1st partial at 8× the
    cell's orbit fundamental; 2nd partial multiple and decay are per-profile) at the cell's pan
    position; 250 ms refractory per cell. **Gotcha:** a cell appearing from silence (startup, or
    entering view) must not count as a fold — guard with `pSum` (sum of previous-segment
    `LengthSquared`); without it the first frames fire a 16-cell chime cascade that broke the
    m9b −6 dBFS peak gate.

- **Per-fractal DirectOrbit profiles + geometry-derived lattice ratios. ✓ DONE (verified 2026-06-12)**
  Addresses the third owner-reported gap: "sonic palette barely changes across fractal types".
  - **`DirectOrbitProfile`** (`src/Parsec.Audio/Sonification/DirectOrbitProfile.cs`) — readonly
    record struct with `ForVoice()` factory. Fields: `RootDivisor` (whole-grid register —
    Mandelbox 1.0 baseline, Mandelbulb 6.0 airy, Kleinian 0.667 dark, BurningShip 2.25),
    `LatticeRatio` (default grid generator), reverb enclosure endpoints `RevFb0/1`, `RevDamp0/1`,
    `RevWet0/1` (Kleinian T60 → ~8 s fully enclosed; BurningShip dry/tight), and chime character
    `ChimeDecaySec` (0.22–2.5 s) + `ChimePartial` (2.0 harmonic – 2.76 clangy).
  - **Grid pitch:** `pitchMul = RootDivisor · ratio^col · ratio^(3−row)`;
    `cellSps = SamplesPerStep / pitchMul`; chime fundamental = `sampleRate·8/(cellSps·128)`.
    Recomputed per control frame (offline) / per buffer (live) — click-free because phase
    restarts each frame with the 5 ms crossfade.
  - **Geometry-derived lattice ratios:** new `FractalSonicFrame.LatticeRatio` field (0 = use
    profile default; used when > 1.001). `GeometryScale.KleinianLatticeRatio` (eigenvalue ratio
    `scale·(1+fixed/min)/2`, octave-reduced into (1,2), degenerate → 1.5) and
    `GeometryScale.MandelbulbLatticeRatio` (superparticular `(power+1)/power` — power 8 → 9/8
    whole-tone grid; morphing Power slides the whole lattice through JI intervals).
    `FractalView.ComputeLatticeRatio()` feeds all 3 `SonificationController.Update` call sites.
  - **Plumbing:** `DirectOrbitSynth.Synthesize(…, voice)` new parameter — UI export
    (`MainWindow.OnRenderToVideoClick`) and CLI (`metal-m9c-direct`,
    `metal-m9d-animated-burning`) pass it. `FractalDroneStream` sets `_doProfile` in the
    constructor and in `SetVoice` (which also recomputes `_doChimeDecay`).
  - **Validated:** `metal-m9c-direct` four palettes measurably distinct (ZCR ~800 Kleinian-dark →
    ~2100 Mandelbulb-bright; RMS spread −12 → −34 dBFS); `metal-m9b-direct` −6 dBFS gate passes;
    `metal-m9d-check` passes; `metal-m9d-animated` proximity arc intact (−37 → −15 dBFS).

**Gotchas for the implementing agent (read all of these before writing code):**
1. **MSL `float3` in device arrays has 16-byte stride.** Use `float4` (as specced) or
   `packed_float3`; a `float3*` buffer read back into a C# `Vector3[]` via `Marshal` silently
   misaligns every element after the first. This is the most likely silent-corruption bug in M9a.
2. **Do not add fields to the `WavetableCell` struct** for the trajectory — it is marshaled
   CPU-side by layout; growing it breaks `ReadOrbitWavetables`' stride math. Separate
   `buffer(5)`, separate read helper.
3. **Global `const` at MSL program scope causes silent shader failure** (whole kernel produces
   zeros, no compile error). Put `ORBTRAJ` in a `#define`. See `skills.md`.
4. **SharpMetal: never `using`/dispose `MTLCommandBuffer`/`MTLComputeCommandEncoder` in the
   GUI app** — autorelease double-free, SIGSEGV in the CFRunLoop pool drain. Copy the
   dispatch/readback pattern of the existing `RunTelemetryPass` verbatim.
5. **New `.metal` files need an `<EmbeddedResource>` entry** in
   `Parsec.Rendering.Metal.csproj` — but M9 should not need new files; extend the four
   existing telemetry shaders.
6. **Schroeder allpass:** if you add reverb anywhere, copy the **1-tap** form
   (`y = -g*x + buf[rp]`) — the 2-tap form in old commits is unstable (g(1+g) > 1, saturates
   in ~1.25 s; fixed in M7d).
7. **Audio thread discipline** (existing pattern in `FractalDroneStream`): latest frame via
   `Volatile.Read`, never lock, never allocate, hold last values on stale frames. The
   crossfade segment buffers must be preallocated.
8. **Determinism:** no `Random`/`DateTime` in the synth path; the telemetry kernels have no
   SSAA jitter (cell-centre rays are fixed) — keep it that way. Export must be byte-identical
   across runs (existing M3 acceptance still applies).
9. **Normalization epsilon guards everywhere** — a cell whose seed lands exactly on a fixed
   point produces a constant orbit; centroid-subtract then peak-normalize divides by ~0.
10. **`HybridSynth`'s stereo interleaved contract** (L0, R0, L1, R1, …) and
    `WavEncoder.Write(..., channels: 2)` — match it exactly or the WAV plays at half speed.
11. **Mutual exclusion with audio-reactive modulation is already enforced** (live: `_modTimer`
    stop/start in `OnSonifyClick`; export: `sonify` flag suppresses `audioMod.ApplyAtTime`) —
    don't add a second mechanism, and don't break the existing one.
12. **Kleinian's telemetry kernel calls `captureRayWavetable` then `captureOrbitWavetable`,
    and the latter zeroes `raySteps`** — a known harmless quirk (ray wavetables unread for
    Kleinian). Don't "fix" it as a drive-by; it's not in M9 scope.
13. The macOS app runs in **Avalonia software-compositor mode** (GL and Metal compositors
    crash on macOS 15.6) — telemetry dispatch happens inside `FractalView.Render`, and
    `CaptureSonicFrame` is the deterministic export-time entry point. Don't move telemetry
    onto a background thread; Metal dispatch from the render path is the established pattern.
14. **Validate every milestone via its CLI command** (`dotnet run --project
    src/Parsec.Cli/Parsec.Cli.csproj -c Release -- <cmd>`) before claiming completion — the
    GUI can't be exercised headlessly, the CLI can.

### Track D — telemetry rollout to Menger/Apollonian/KIFS/QJBox ✓ DONE (2026-06-12)

Brings sonification coverage from 4 to 8 of the 20 selectable 3D fractals, chosen for
geometric contrast. Scope decision: **DirectOrbit-first** — no custom Hybrid streaming
voices for the new four; in Hybrid mode they fall through to the generic Mandelbox fill
(wavetables are still geometry-driven), and all hand-tuned distinctness lives in
`DirectOrbitProfile` + geometry-derived lattice ratios.

- **Shaders:** `menger_telemetry.metal`, `apollonian_telemetry.metal`, `kifs_telemetry.metal`,
  `qjbox_telemetry.metal` — same structure as `burningship_telemetry.metal` (buffers 0–5:
  FoldParams, TelemetryParams, cells, wavetables, waveshaper strip, orbit trajectories).
  Per-fractal orbit **escape semantics** preserve the DirectOrbit stereo gate (empty space
  silent): Menger/KIFS `dot(z,z) > 1000`; QJBox `length(z) > 4`; Apollonian treats "settled"
  (no inversion applies) as escape — holding the settled point would be DC, killed by the
  centroid subtract, but worse it would defeat the bailout-weighted cell gain.
- **Renderers:** `RunTelemetryPass` + lazy `EnsureTelemetryPso` added to
  `MetalMengerRenderer`, `MetalApollonianRenderer`, `MetalKifsRenderer`, `MetalQJBoxRenderer`
  (verbatim copy of the BurningShip pattern).
- **Voices:** `FractalVoice.Menger/Kifs/QJBox` appended (Apollonian already existed). All
  hybrid-path switches fall through to Mandelbox via their existing `default:`/`_` arms.
- **Apollonian DirectOrbit enabled:** the `canDirect` fallback guards removed
  (`FractalDroneStream.FillBuffer`, `MainWindow` export path) now that it has a real kernel.
  Its M7g timer-bell voice remains its Hybrid-mode identity.
- **Profiles** (register ladder for distinctness): Menger RootDivisor 0.5 (hollow low,
  short clangy chimes), QJBox 1.5 (warm pad, 5/4 lattice, long harmonic chimes),
  KIFS 3.0 (crystalline, 4/3 lattice, icy inharmonic chimes), Apollonian 4.0 (glassy,
  19/16 gasket lattice). Full ladder: Menger 0.5 → Kleinian 0.667 → Mandelbox 1.0 →
  QJBox 1.5 → BurningShip 2.25 → KIFS 3.0 → Apollonian 4.0 → Mandelbulb 6.0.
- **Modal resonator bodies:** Menger and Apollonian profiles now have optional modal signatures
  applied by both `DirectOrbitSynth` and `FractalDroneStream`. The conditioned orbit is the
  exciter; mode frequency is `loopHz * ModeFreqMul * ModeRatios[m]`, keeping the body locked to
  the same lattice as the raw orbit. Menger uses odd-harmonic tube modes (`1,3,5,7,9`); Apollonian
  uses inharmonic high-Q glass modes (`1,2.32,4.25,6.63,9.38`). Live state is preallocated and
  cleared on DirectOrbit reset/voice switch. `DirectOrbitSynth.Synthesize(..., enableModal: false)`
  gives a raw offline reference. CLI `metal-d-modal [duration] [outDir] [modalBlend]` writes
  raw/blend/modal triplets from the same telemetry frames
  (`d_menger_raw.wav`/`d_menger_blend.wav`/`d_menger_modal.wav`,
  `d_apollonian_raw.wav`/`d_apollonian_blend.wav`/`d_apollonian_modal.wav`).
- **Raw Orbit <-> Modal Body blend (done, 2026-06-12):** DirectOrbit now has a second blend axis
  inside the engine. The conditioned orbit can be mixed continuously against the resonant body
  after excitation and before tilt / master-bus processing, instead of hard-switching to the
  modal bank. `DirectOrbitSynth.Synthesize(..., modalBodyBlend)` and
  `FractalDroneStream.DirectOrbitModalBlend` share the same behaviour. The UI exposes a dedicated
  `Raw Orbit` ↔ `Modal Body` slider and disables it for voices that do not yet carry modal
  profiles. `1.0` preserves the previous full-modal behaviour.
- **Lattice ratios:** `GeometryScale.FoldScaleLatticeRatio(scale)` octave-reduces |scale|
  into (1, 2); returns **0 on degenerate** (unison/octave, e.g. KIFS scale 2) so the profile
  default applies — matches `FractalSonicFrame.LatticeRatio` semantics. Menger scale 3 →
  3/2 fifths (geometry-derived); QJBox |−1.8| → 1.8. Wired in
  `FractalView.ComputeLatticeRatio()` for Menger/Kifs/QJBox.
- **Validated:** `metal-d-telemetry` — all 4 kernels PASS (16/16 orbit tiles, 64-pt
  waveshaper each). `metal-d-direct` — four palettes distinct (ZCR 797 Apollonian-glassy-
  sparse → 1120 Menger → 2504 KIFS → 2639 QJBox; peaks −10.1 to −25.9 dBFS). Regressions:
  `metal-m9b-direct` (default duration) and `metal-m9d-check` pass. (Note: the m9b −6 dBFS
  gate is duration-sensitive — at 6 s it reads −5.8 dBFS on pre-Track-D commits too;
  validate at the default duration.)

---

### Next phase — geometry-conditioned resonance and stronger macro tone (planned)

> **Handoff:** a step-by-step continuation brief for the next agent (Sonnet 4.6) lives in
> `docs/sonification-handoff-sonnet46.md` — including an environment caveat (Metal rendering went
> down mid-session 2026-06-12; `computeFunction must not be nil` is a host/device failure, not a
> code bug — verify `metal-d-telemetry` prints `4/4 PASS` before trusting any render). Do not start
> Phase 2 until the user confirms the Phase 1 modal direction passes the ear gate.

The next work should deepen **instance-specific acoustic behaviour**, not add an unrelated third
synth family. The modal resonator remains **selective** inside `DirectOrbit`; it is not promoted
to a universal standalone mode.

Priority order:

1. **Raw Orbit <-> Modal Body blend inside DirectOrbit** (done)
   - Implemented as a continuous per-voice blend after the conditioned orbit stage and before the
     spectral-tilt / master bus, so modal-capable fractals can move between noisy/raw orbit
     texture and instrument-like body resonance.
   - Kept orthogonal to the existing `Hybrid <-> DirectOrbit` blend. One chooses the engine; the
     other chooses how much of the DirectOrbit branch is raw exciter vs modal body.

2. **Geometry-conditioned modal body, not just family-conditioned**
   - The current modal profiles are family defaults. Extend them so a specific *instance* of the
     fractal can sound like "large mass + fine tingly structure" at the same time.
   - Slow, global metrics should steer the deep body:
     `HitRatio`, `MeanDepth`, low-passed cell energy sum, large-scale bounded fraction,
     and/or a low-order size proxy from the orbit spread.
     These should control modal register tilt, decay length, modal drive, and low-mode gain.
   - Fast, local/detail metrics should excite or brighten the upper body:
     fold-event rate, high-row/cell energy skew, orbit delta variance, normal variance,
     and short-time energy bursts.
     These should trigger/brighten short high modes rather than retune the whole body every frame.
   - Design constraint: split **body shape** (slow) from **excitation/detail** (fast) so the
     resonator does not chatter or sound like parameter automation.

3. **Selective modal rollout to KIFS and QJBox**
   - Keep the modal path selective where it fits the geometry:
     KIFS should bias toward brittle/crystalline, short bright upper modes;
     QJBox should bias toward warmer, smoother, longer mid-register body modes.
   - Do not force modal bodies onto every telemetry fractal. Mandelbox / BurningShip may still
     work better with raw-orbit dominance plus chimes/reverb than with a persistent body.

4. **Strengthen the Shepard layer with geometry**
   - The current Shepard layer is structurally useful but tonally weak because it is mostly a
     zoom-glide carrier with limited geometry ownership.
   - Next revision should derive its centre, envelope, and/or partial weighting from whole-fractal
     geometry rather than only `ZoomVelocity` and centroid pan.
   - Candidate controls:
     global resonance estimate from low-passed total cell energy / bounded mass,
     spectral brightness proxy from `NormalVariance` or upper-row activity,
     enclosure/proximity for width and bloom,
     and optional modal locking so the Shepard partial cloud reinforces the active body rather than
     floating independently.
   - Goal: the Shepard layer should read as the fractal's large-scale tonal field, not a generic
     psychoacoustic garnish.

Acceptance direction for this phase:

- A massive filled structure should yield audibly deeper, longer body modes.
- Fine filamentary or folded detail should add short, bright, high-pitched activity on top.
- RawOrbit<->ModalBody blend should clearly traverse "texture" ↔ "object resonance" without
  collapsing loudness or stereo image.
- Shepard should feel harmonically anchored to the current fractal instance, not merely to camera motion.

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
- `src/Parsec.Rendering/Output/ImageOutput.cs` — transparent export matte key for hero/animation PNG frames (`transparentBackground: true`); Render-to-Video uses MOV/ProRes 4444 when `Transparent BG` is enabled.

---

## 7. Pitfalls

- Do **not** name the new type `FractalAudioFrame` (collides conceptually with `AudioFeatureFrame`).
- Do **not** try to feed live audio through `OpenALAudioPlaybackSession` — it's a whole-file player; live needs a new streaming source.
- Do **not** sonify the packed `uint[]` image — extract from the DE march.
- Do **not** put DSP on the UI/render thread, or block the audio thread on the renderer.
- Do **not** ship all 20 fractals before one sounds genuinely good. Make Mandelbox excellent first.
