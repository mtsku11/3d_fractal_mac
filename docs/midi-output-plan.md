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
- **M3b — configurability (deferred by the user's "fixed defaults, editor later" choice).** An
  editable mapping panel (signal → CC/note/channel/range), modelled on the existing
  `AudioMappingPanel`. Not started — confirm before building.
- **Later.** MPE / pitch-bend for expressive per-cell voices; MIDI clock / note quantisation to a
  musical grid; per-cell spatial mapping to channels.

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
