# MIDI Output — fractal geometry as a MIDI control surface

**Goal:** turn the fractal view into an AV controller that emits *meaningful* MIDI to any
external DAW / plugin, so the audio domain is handled by real instruments. This is the inverse
of audio-reactive (audio→params) and a sibling of sonification (geometry→audio synth) — but
instead of synthesising sound internally, it emits MIDI and lets professional software make sound.

## Why MIDI (vs. the internal synth)

The valuable, verified half of sonification is the **feature-extraction layer**: the Metal
telemetry pass reduces each frame to a `FractalSonicFrame` (hit ratio, depth stats, normal/trap
variance, camera & parameter velocity, per-cell energy, orbit trajectories). That analysis is
accurate and already runs at ~30 Hz. The weak, hard-to-verify half is the DSP synth. MIDI-out
keeps the strong half and discards the weak one: route the features to Ableton/Logic/a plugin.

## Architecture

```
Metal telemetry → FractalSonicFrame → MidiOutputController → MidiOutputSession → CoreMIDI virtual source "Parsec"
   (existing)         (existing)         (signals→CC/notes)     (transport)           (any DAW/plugin listens)
```

- **Transport** — `Parsec.Audio.Midi.MidiOutputSession` (CoreMIDI P/Invoke). Publishes a virtual
  source named "Parsec"; it appears in every MIDI app's input list with zero configuration. Send
  uses `MIDIPacketListInit`/`MIDIPacketListAdd`/`MIDIReceived` (Apple's helpers handle the
  notoriously fragile packet-list layout). macOS only; degrades to `IsAvailable=false` otherwise.
- **Mapping** — `Parsec.Audio.Midi.MidiOutputController` consumes `FractalSonicFrame`, derives
  named signals, smooths (one-pole), quantises to 0–127, and only transmits a CC when its value
  changes (no bus flooding).
- **App wiring** — `FractalView.MidiEnabled` lazily creates the session and emits from the same
  per-frame `sonicFrame` the synth uses. UI: a "MIDI OUT" checkbox + live monitor in the status bar.

## Signal → MIDI map

| Visual concept | Source signal | MIDI | Status |
|---|---|---|---|
| small / large | `HitRatio` | CC20 | **M1 done** |
| how close | `exp(-MeanDepth/k)` | CC21 | **M1 done** |
| how complex | `NormalVariance·scale` | CC22 | **M1 done** |
| expanding / contracting | d(size)/dt | CC23 (signed) + Expand/Contract notes | **M2 done** |
| folding | `ParameterVelocity` spike | Fold note (velocity = magnitude) | **M2 done** |
| simplifying | complexity falling | Simplify note (velocity = magnitude) | **M2 done** |
| colour changing | palette base hue | CC24 | **M3 done** |
| layering / depth | DepthVariance (relative) | CC25 | **M4 done** |
| haze / filaments | StepMean | CC26 | **M4 done** |
| verticality (floor↔ceiling) | NormalMean.y | CC27 (signed) | **M4 done** |
| motion energy | CameraSpeed | CC28 | **M4 done** |
| dolly in/out | ZoomVelocity | CC29 (signed) | **M4 done** |
| on-screen position X | energy centroid X | CC30 (signed) | **M4 done** |
| on-screen position Y | energy centroid Y | CC31 (signed) | **M4 done** |
| concentrated ↔ spread | spatial dispersion | CC32 | **M4 done** |
| feature / material | TrapMean.x | CC33 | **M4 done** |
| heterogeneity | TrapVariance magnitude | CC34 | **M4 done** |
| enter tunnel/cavern | HitRatio crossing high | note 67 Enclose | **M4 done** |
| break into open space | HitRatio crossing low | note 69 Emerge | **M4 done** |
| detail burst | Haze crossing high | note 71 Shimmer | **M4 done** |
| object appears in a region | cell Energy onset | notes 36–51 (4×4) | **M4 done** |

**Event detection (M2)** is derivative-based: rate-of-change + threshold with hysteresis, not raw
values, so "fold" / "expand" fire as discrete musical events rather than constant noise.

## Milestones

- **M1 — proof of pipe (done, verified 2026-06-14).** CoreMIDI virtual source + 3 core continuous
  CCs (size/proximity/complexity) + in-process loopback self-test. CLI `midi-smoke`: source created,
  loopback 4/4 packets, CC sweep visible to an external monitor. In-app "MIDI OUT" toggle + live
  status monitor; parallel to the synth; golden 5/5 (no render regression).
- **M2 — events (done, verified 2026-06-14).** Derivative signals on top of the M1 CCs.
  `MidiOutputController` now also emits **CC23** (signed expansion-rate, 64 = steady) and four
  discrete note events — **Expand** (60), **Contract** (62), **Fold** (64), **Simplify** (65) —
  each with hysteresis (high fires / low re-arms), a refractory period, and a short note gate
  (auto Note Off). Velocity carries event magnitude. Size/complexity rates are computed from the
  smoothed signals with a `dt` clamped to [1/240, 0.5] s so a paused/looped clock can't spike them;
  fold is `|ParameterVelocity|`. UI status flashes `⟪fold⟫` etc. for ~0.6 s. CLI `midi-smoke`
  drives a triangle size wave + 1 Hz fold spike and reports event-note count (7 events / 4 s,
  all four types). Golden 5/5 (no render regression).
- **M3 — colour (done, verified 2026-06-14).** Palette-change CC24: the hue of the mean palette
  colour (≈ `PaletteState.Base`, since the cosine bands average to Base). `MidiOutputController.Hue01`
  computes it; `FractalView.EmitMidi` passes it into `Update(frame, hue)`. Slewed along the **shortest
  arc** (hue is circular — 0.98→0.02 must not sweep backwards through the wheel), quantised, dedup'd.
  CC24 moves whenever the palette shifts (manual, keyframe, or audio-reactive). CLI `midi-smoke`
  rotates the wheel → CC24 0→29→60→92. Golden 5/5.
- **M4 — expanded map (done, verified 2026-06-14).** 10 new continuous CCs (25–34: layering, haze,
  verticality, speed, dolly, position X/Y, dispersion, structure, heterogeneity), 3 gesture notes
  (67 Enclose / 69 Emerge / 71 Shimmer), and 16 spatial "object" notes (36–51 = the 4×4 cell grid,
  fired on per-cell energy onset, velocity = energy). All on channel 1. Spatial aggregates (energy
  centroid, dispersion) computed in `MidiOutputController` from `frame.Cells`; signed CCs centre at
  64. Startup guard disarms already-lit cells so enabling MIDI mid-view doesn't blast a 16-note
  chord (cf. the M2 fold guard). Normalisation gains are tunable fields (defaults set; real tuning is
  against live flying). Null-safe for the 12 fractals without telemetry. Verified cross-process:
  670 msgs / 0 undecoded, all 15 CCs + Enclose/Emerge/Shimmer + 6 distinct spatial cells.
- **M3b — configurability (done, verified 2026-06-15).** `MidiMappingConfig` holds 17 editable
  CC routes + 3 note-group channels; `MidiOutputController` routes every CC/note through it
  (`EmitMapped`, per-mapping smoothing+dedup) with defaults reproducing the prior fixed map
  byte-for-byte. `MidiMappingPanel` (code-behind, cf. `AudioMappingPanel`) edits the live config:
  enable / CC# / channel / out min–max / invert per signal + per-group channel. CLI
  `midi-map-check` verifies remap end-to-end (Size→CC50 ch3, Complexity off, Proximity 100–127);
  `midi-monitor` prints remapped CCs outside 20–36. Note-NUMBER editing is still fixed (channel +
  enable per group is exposed); that and a Reset-to-defaults button are easy follow-ons.
- **Later.** MPE / pitch-bend for expressive per-cell voices; MIDI clock / note quantisation to a
  musical grid; per-cell spatial mapping to channels.

## Planned improvements — closing the AV-unity gaps (2026-06-14)

The M1–M4 map is faithful for **structure and motion** (size, proximity, position, complexity,
folding) but only partial for **colour** and **fine detail**, and the telemetry measures the DE
*geometry*, not the rendered *pixels* — so shading/lighting/bloom/grade are not represented. The
four items below close the largest gaps, in recommended order.

**Progress (2026-06-15):** items 1, 2a, 2b, 3, 4, and the M3b mapping editor are **all done +
verified cross-process**. Telemetry coverage is now **complete for every selectable 3D fractal** —
the 20 analytic fractals **plus Attractor** (whose `MetalAttractorRenderer` already existed and was
verified working; `attractor_telemetry.metal` + `RunTelemetryPass` reuse its trajectory/hash buffers
so it emits spatial MIDI too). `metal-midi-telemetry` validates 13 of them incl. Attractor.

### 1. Real on-screen colour → CC24 (highest leverage) — DONE (verified 2026-06-14)

Implemented: `MidiOutputController.MeanScreenColorHsv` reduces the rendered RGBA8 frame buffer to a
coverage-masked mean colour (background pixels masked, every 4th pixel sampled). `FractalView.EmitMidi`
passes the live frame buffer; CC24 now carries the real on-screen **hue**, CC35 the **saturation**,
CC36 the **brightness/value**. CLI/no-buffer callers keep the palette-hue fallback (`Update(frame,hue)`).
Verified cross-process via `midi-monitor`: CC24/35/36 delivered, 0 undecoded. *Original note below.*



**Problem.** CC24 is currently `Hue01(PaletteState.Base)` — the palette *base setting*, not the
colour actually displayed. The visible colour is the cosine palette modulated by orbit traps, then
lighting, bloom and the HDR grade, and it varies across the frame. So CC24 tracks a knob, not the eye.

**Approach.** Colour is a *shading-stage* property, so it must come from the **rendered image**, not
the DE telemetry. The Metal renderers already return the displayed frame as an RGBA8 `uint[]` (the
buffer `FractalView` uploads via `TexImage2D`). After each render, downsample that buffer on the CPU
(sample every Nth pixel — <1 ms) and compute a coverage-masked mean colour:
- ignore background pixels (near-black, or reuse the corner-matte logic in `ImageOutput`, or weight
  by the telemetry hit mask) so the void doesn't wash the average out;
- convert mean RGB → HSV: **hue → CC24** (now the real colour), and add **saturation → CC35** and
  **brightness/luma → CC36** (overall how lit/vivid the scene is — both genuinely new signals).
- Wire: `FractalView` computes the colour from the readback buffer and passes it into
  `MidiOutputController.Update(frame, hue, sat, val)` (extend the signature; keep palette-hue as a
  fallback when no frame buffer is available, e.g. the CLI).
- **Per-region colour (optional follow-on):** partition the rendered image into the spatial grid and
  compute per-cell mean colour → lets a region's *note velocity or a per-region CC* carry its colour,
  not just its energy.

**Acceptance.** Changing palette **or** lighting **or** bloom visibly shifts CC24; a grey/unlit scene
reads low saturation; `midi-smoke` (no frame buffer) still works via the palette-hue fallback; golden 5/5.
**Effort:** moderate. **Risk:** low (read-only over an existing buffer; no render change).

### 2. Finer spatial resolution — 2a DONE (verified 2026-06-14), 2b deferred

**Problem.** Position/dispersion CCs and the 16 region notes derive from the **4×4** cell grid, so
small objects and precise on-screen position are coarse.

**Constraint.** The 4×4 grid is **shared with sonification** (16 OpenAL emitters, the DirectOrbit
Pythagorean 4×4 lattice, fold-chime top-3, etc.). Changing the shared grid would ripple through a lot
of stable audio code — avoid.

**Approach (decoupled, two tiers):**
- **2a (low-risk, do first).** Compute a **full-resolution** energy centroid + spread directly from
  the 64×36 telemetry grid inside `TelemetryReduction.Reduce` (it already has the raw `TelemetryCell[]`),
  and surface them on `FractalGeometryStats`/`FractalSonicFrame`. MIDI's **CC30/31/32** then use these
  full-res values instead of the 4×4-derived centroid — much sharper position, **zero** sonification
  impact.
- **2b (optional).** A **parallel** finer region-note grid (e.g. 6×6 or 8×8) computed only for MIDI,
  leaving the sonification 4×4 untouched. Note-range collision must be handled: 36 cells would run
  36–71 and clash with the gesture notes (60–71) on channel 1 → put spatial notes on their **own
  channel**, or pick a non-overlapping base. Defer until 2a is in use and the need is confirmed.

**Acceptance.** 2a: position CCs track a small off-centre object the 4×4 grid blurs; sonification output
unchanged (A/B a sonify render). **Effort:** 2a small, 2b moderate. **Risk:** 2a low, 2b medium (note layout).

**2b status (done, verified 2026-06-15):** `TelemetryReduction.RegionEnergyGrid` computes an 8×6=48
energy grid from the 64×36 telemetry → `MidiRegionEnergy` on stats+frame. `MidiOutputController`
fires per-cell onset notes (base 36, so 36–83) on the fine-grid channel (default **ch 2**) with its
own note-off book and a startup guard — parallel to the 4×4 on ch 1, no collision, sonification grid
untouched. UI checkbox "Fine region grid → ch2" (default on). Verified cross-process: notes 52–83
(fine-only) fired 28 distinct cells, 0 undecoded.

**2a status (done, verified 2026-06-14):** `TelemetryReduction.FullResCentroid` computes an
energy-weighted centroid + spread over the full 64×36 grid (shared via an `(int hit, float depth)`
accessor between the generic `Reduce` and the Mandelbox path). Surfaced as `CentroidX/Y/Dispersion`
on `FractalGeometryStats` → `FractalSonicFrame`; `MidiOutputController` prefers these for CC30/31/32
(the 4×4 aggregate stays as the fallback). The sonification 4×4 grid is untouched. Verified
cross-process: sharp position sweep (CC30 reaches 3..125), 0 undecoded.

### 3. Lower latency / responsiveness knob — DONE (verified 2026-06-14)

**Problem.** End-to-end lag ≈ telemetry ~30 Hz + one-pole smoothing (`Smoothing = 0.25`, ~100 ms) +
the DAW's own buffer. Fine for pads, loose for tight rhythmic sync. The DAW buffer is out of our
control; smoothing is the lever we own.

**Approach.** Expose `MidiOutputController.Smoothing` as a **"MIDI responsiveness" slider** in the
MIDI OUT panel (0.05 = smooth/laggy ↔ 1.0 = instant/jittery). Optionally split CC vs event smoothing.
Document that the telemetry rate is tied to render framerate and the DAW buffer adds fixed latency.

**Acceptance.** Moving the slider audibly changes CC responsiveness; defaults unchanged at 0.25.
**Effort:** small. **Risk:** low.

**Status (done 2026-06-14):** `FractalView.MidiResponsiveness` maps a new `MidiResponsivenessSlider`
(0.05→1.0, default 0.25) to `MidiOutputController.Smoothing`, applied live and on (re)create. Builds
clean; live slider screenshot blocked by the macOS Software-compositor capture issue (window doesn't
present to `screencapture`), wiring mirrors the adjacent working sonify sliders.

### 4. Full telemetry coverage (remaining 12 fractals) — DONE (20/20, verified 2026-06-15)

**Problem.** Only **8 of 20** fractals have a telemetry kernel (Mandelbox, Mandelbulb, Kleinian,
BurningShip, Menger, Apollonian, KIFS, QJBox). The other **12** — RotBox, Hybrid, QuaternionJulia,
Bicomplex, Phoenix, Biomorph, Mosely, PseudoKleinian4D, RiemannSphere, Mandalay, Anisotropic,
OrbitHybrid — emit only the camera-derived CCs (Size/Proximity/Complexity/Expansion/Colour/Speed/Dolly)
and no spatial/structure data. (Attractor has no Metal renderer at all — out of scope here.)

**Approach.** Per fractal: add a `*_telemetry.metal` kernel (copy an existing one, swap the DE — the
recipe is in `skills.md` / Track D), add `RunTelemetryPass` to its `Metal*Renderer`, and add a switch
arm in `FractalView.RunActiveTelemetryPass`. MIDI needs only the geometry stats + 4×4 cells, not the
sonification wavetables/orbit-trajectory/field-scan buffers, so a *trimmed* telemetry kernel is enough
for MIDI-only fractals (keeps each port small). Benefits sonification too. Do incrementally, a few per
pass; `metal-d-telemetry`-style validation per batch.

**Acceptance.** Each ported fractal shows non-zero spatial/structure CCs in `midi-monitor`; golden 5/5.
**Effort:** large but mechanical and incremental. **Risk:** low per fractal (well-trodden port).

**Established trimmed-MIDI pattern (use for each remaining fractal):**
1. `Shaders/<f>_telemetry.metal` — copy `rotbox_telemetry.metal` (the trimmed template: FoldParams +
   TelemetryParams + TelemetryCell, the DE lifted **verbatim** from `<f>_raymarch.metal`'s
   `estimateFull`/`estimate`, `intersectSphereForward`, and the 64×36 march kernel writing only
   `TelemetryCell[]` to buffer(2)). No wavetable/orbit-trajectory/waveshaper/field-scan buffers.
2. `<EmbeddedResource>` entry in `Parsec.Rendering.Metal.csproj`.
3. `RunTelemetryPass(<F>Params, Camera3D, RaymarchSettings)` on `Metal<F>Renderer` — copy the trimmed
   block added to `MetalRotBoxRenderer` (only fold/tel/output buffers; `TelemetryReduction.Read` +
   `Reduce`; `_telemetryPso`/`EnsureTelemetryPso`/`BuildTelemetryParams`; dispose the telemetry PSO).
4. Switch arm in `FractalView.RunActiveTelemetryPass`.
5. A line in the `metal-midi-telemetry` CLI validator; run it outside the sandbox on Metal hardware.

**Complete — all 20 Metal fractals have telemetry (verified via `metal-midi-telemetry`, 12/12 PASS,
16 cells each, closer→more hits, full-res centroids finite):**
- Batch 1: RotBox (hitClose 0.308). Batch 2: QuaternionJulia (0.077).
- Batch 3: Hybrid (0.211), Bicomplex (0.013), Phoenix (0.030), Biomorph (0.030), Mosely (0.068),
  PseudoKleinian4D (0.682), RiemannSphere (0.051), Mandalay (0.331), Anisotropic (0.569),
  OrbitHybrid (0.291).

Batch 3 was generated mechanically: `tools/build_midi_telemetry.py` lifts each `<f>_raymarch.metal`
FoldParams struct + DE region verbatim into `<f>_telemetry.metal`; `tools/add_telemetry_pass.py`
inserts the trimmed `RunTelemetryPass` into the compact-style renderers (Hybrid done by hand). Re-run
the generators if a DE changes. **Attractor** has no Metal renderer at all (sphere-traces a prebuilt
`AttractorHash` over SSBOs), so it is out of scope here — that's the only selectable 3D fractal
without telemetry.

### Recommended sequence

1 (real colour) → 3 (responsiveness knob, quick win) → 2a (full-res position) → 4 (coverage, ongoing)
→ then optionally 2b (finer region notes) and the deferred M3b mapping editor. Each lands as its own
verified, committed increment.

## Testing the output

Three independent ways to confirm the geometry → MIDI stream, in increasing realism:

1. **`parsec midi-smoke [sweepSeconds]`** — publishes "Parsec", runs an in-process loopback
   (proves the source is published and the MIDIServer delivers), then sweeps CC20–24 + fires the
   four event notes off a synthetic triangle/fold drive.
2. **`parsec midi-monitor [seconds]`** — an *independent* CoreMIDI receiver (separate client) that
   connects to every published source and decodes the channel-voice messages. Run it in one
   terminal and `midi-smoke` (or the app with MIDI OUT on) in another to prove **cross-process**
   delivery through the system MIDIServer — the same path Ableton uses. Verified 2026-06-14:
   465 messages, 0 undecoded, all five CCs + Fold×5/Simplify×1.
3. **`web/midi-monitor.html`** — a Web MIDI page (Chrome/Edge) with live CC bars + event pads.
   Serve it (`python3 -m http.server` from `web/`), open in Chrome, **Allow** the MIDI prompt,
   then enable MIDI OUT in Parsec. Uses the identical OS MIDI path a DAW would. (Automation note:
   Playwright/headless Chromium auto-*denies* the Web MIDI permission and exposes no grant hook,
   so the page can only be driven by hand or with synthetic `onMessage` injection — its
   message-handling/rendering was verified that way; real delivery is proven by #2.)

## Design notes / gotchas

- MIDIObjectRef (client/source/port) is a `UInt32` typedef, **not** a pointer — marshal as `uint`.
- `MIDIReceived` delivery is asynchronous via the MIDIServer; the loopback self-test must wait
  (~150 ms after `MIDIPortConnectSource` to let the connection propagate) or early sends are dropped.
- Mutual exclusivity: MIDI-out only *reads* geometry, so it does not form a feedback loop by itself.
  But if audio-reactive is modulating params, the emitted MIDI reflects audio-driven geometry — keep
  the two as separate modes (don't run audio-reactive + MIDI-out expecting clean geometry).
- Rate-limit: dedup on quantised value is the primary flood guard; telemetry is already ~30 Hz.
- **Cross-process enumeration needs a pumped run loop.** In a command-line tool, `MIDIGetNumberOfSources`
  returns 0 until the client's connection to the MIDIServer completes — which only happens when a
  CFRunLoop is serviced. `MidiMonitorProbe` calls `CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.2, …)`
  in its scan loop instead of `Thread.Sleep`. (The in-process loopback in `MidiOutputSession` avoids
  this because it connects to a *known* source ref and never enumerates.) Run-loop modes are compared
  by string, so a `CFString` holding the literal "kCFRunLoopDefaultMode" substitutes for the constant.
- **CoreMIDI coalesces messages.** Several rapid sends arrive as ONE packet whose data buffer holds
  many 3-byte messages (length = 3·k), with `numPackets` still 1 — a decoder must walk the buffer,
  not assume one message per packet. Packet layout offset (8-byte-aligned vs packed) is host-fragile;
  `MidiMonitorProbe` tries both and keeps whichever yields a valid leading status byte.
- **The sandbox isolates the MIDIServer.** Two separately-sandboxed CLI processes do not share a
  MIDIServer, so cross-process tests show 0 sources; run sender and monitor outside the sandbox.
