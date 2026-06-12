# Sonification handoff — for Sonnet 4.6

**Written 2026-06-12 by Opus. Branch `feature/fractal-sonification`.**
Read this with `docs/fractal-sonification-plan.md` (full spec) and `skills.md` (Metal recipes/gotchas).
`CLAUDE.md` "Current Milestone" has the durable one-line history.

---

## ⚠️ Environment caveat — READ FIRST

**Metal rendering is currently DOWN in this environment.** As of 2026-06-12 ~19:09 UTC the Metal
compute path hard-crashes at pipeline creation:

```
-[MTLComputePipelineDescriptorInternal setComputeFunction:withType:]:800:
  failed assertion `computeFunction must not be nil.'
```

This is **not a code bug** — the *same* commands (`metal-d-modal`, `metal-m9b-direct`) rendered
cleanly earlier in the same session with the same binaries, and even the pre-Track-D Mandelbox
path (`metal-m9b-direct`) now crashes. The host's Metal device/shader-compiler service has gone
down mid-session (Codex hit the same wall). Implications for you:

- **You cannot verify renders until the environment recovers.** Restart the session / host, or wait,
  then re-run `metal-d-telemetry` (cheapest smoke test) — it must print `4/4 PASS` before you trust
  any audio output.
- The crash is a **native assertion, not a catchable C# exception** — a bad/missing kernel function
  aborts the whole process (the `try/catch` in `EnsureTelemetryPso` cannot save you). If a *new*
  kernel you add returns nil from `NewFunction`, you get this exact crash. Distinguish "my kernel is
  broken" from "the environment is down" by running a known-good command (`metal-m9b-direct 1`)
  first: if *that* crashes too, it's the environment.
- All the metrics quoted below were measured **before** the environment went down; they are real and
  reproducible once Metal is back.

---

## Current state (what's committed, all local — DO NOT PUSH without the global protocol)

6 unpushed commits on `feature/fractal-sonification`:
`87498f4 e52ba46 82a7158 4e35d78 1f120d8` (Track D) + `4c5e9af` (modal bodies).

**Track D (done):** telemetry now covers 8 of 20 fractals — Mandelbox, Mandelbulb, Kleinian,
BurningShip + Menger, Apollonian, KIFS, QJBox. DirectOrbit-first: the 4 new voices have no custom
Hybrid DSP (they fall through `default:` arms to the Mandelbox hybrid fill); distinctness lives in
`DirectOrbitProfile` + `GeometryScale.FoldScaleLatticeRatio`.

**Phase 1 modal bodies (done, commit 4c5e9af):** the user's complaint was that raw 128-point orbit
playback sounds like a noise drone for every fractal (Apollonian especially = "sparse noise"; it's
an impulse-train from settle-zeroing). Fix: the orbit now optionally **excites a per-cell bank of
tuned two-pole modal resonators** so the fractal sounds like a struck/bowed body.

- `DirectOrbitProfile` gained `ModeRatios / ModeGains / ModeDecaysSec / ModeFreqMul / ModalDrive /
  ExciterBleed` and a `HasModalBody` predicate (all three arrays non-empty). `null` ModeRatios =
  raw playback (unchanged behaviour) — that's why the other 6 voices are byte-identical.
- Implemented identically in `DirectOrbitSynth.Synthesize` (offline) and
  `FractalDroneStream.FillDirectOrbit` (live, preallocated, `MaxModalModes=8`, cleared on
  reset/voice-switch — zero audio-thread allocation). **These two MUST stay in sync.**
- Modal frequency = `loopHz · ModeFreqMul · ModeRatios[m]` where `loopHz = sr/(cellSps·OrbtLen)` —
  so the body is locked to the same geometry-derived lattice as the raw orbit and retunes live.
- Resonator math: pole radius `R = exp(−6.9078/(decaySec·sr))` (T60), input gain
  `g = ModeGains[m]·sin(ω)·√(1−R²)·ModalDrive` (normalises Q so long modes don't dominate),
  `y[n] = 2R·cos(ω)·y[n−1] − R²·y[n−2] + g·x[n]`.
- **Raw↔Modal blend:** the branch does NOT hard-switch. It blends `raw + blend·(modal − raw)` after
  excitation, before tilt/master-bus. Exposed as `Synthesize(..., enableModal, modalBodyBlend)`,
  `FractalDroneStream.DirectOrbitModalBlend`, the UI `DirectOrbitModalBlendSlider` (auto-disabled
  for non-modal voices), and export passthrough. `1.0` = full modal (default), `0.0` = old raw.
- Profiles: **Menger** = hollow odd-harmonic tube `[1,3,5,7,9]`, decays `[0.9..0.18]`, FreqMul 4,
  drive 1.5, bleed 0.12. **Apollonian** = inharmonic high-Q glass `[1,2.32,4.25,6.63,9.38]`,
  decays `[3.0..0.8]`, FreqMul 3, drive 4, bleed 0.05.

**Measured improvement (pre-outage):** Menger effective spectral lines 60→16 (noise→chord),
centroid 1206→657 Hz; Apollonian envelope p95/median 1.08→2.53 (static crackle→ringing strikes),
+12.6 dB strike energy. Peaks −3.1 / −13.3 dBFS. `metal-m9b-direct` and `metal-m9d-check` gates pass.

**Open question for the user (awaiting ear gate):** the 6 A/B WAVs
(`d_{menger,apollonian}_{raw,blend,modal}.wav`) were delivered. Mix-level harmonicity stayed
~0.5–0.7 because 16 cells at 16 lattice pitches is inherently polyphonic — metrics confirm the
transformation but whether it reads as *instrument* is the user's call. **Do not start Phase 2
until the user confirms the modal direction is right.**

---

## Verification commands (run once Metal is back)

| Command | Purpose | Expected |
|---|---|---|
| `metal-d-telemetry` | cheapest smoke test, all 4 Track-D kernels | `4/4 PASS`, 16/16 orbit tiles |
| `metal-d-modal [dur] [outDir] [blend]` | raw/blend/modal triplets, Menger+Apollonian | peaks < 0 dBFS, 16/16 orbits |
| `metal-d-direct [dur] [outDir]` | all 4 Track-D DirectOrbit fly-ins | 4 distinct ZCR/peak |
| `metal-m9b-direct` | regression gate (Mandelbox) | **default duration only** (gate is duration-sensitive — reads −5.8 dBFS at 6 s even on clean code) |
| `metal-m9d-check` | export-routing regression | peak < 0 dBFS, enum orthogonality ✓ |

Analysis tools (added this session, `tools/`): `analyze_wavs.py` (flatness/centroid/harmonicity/
activity), `line_concentration.py` (effective spectral lines — the key noise-vs-pitch metric),
`envelope_smoothness.py` (crackle-vs-ring crest). Run as `python3 tools/<x>.py <wav>...`.

---

## Phase 2 — prioritized work (only after user confirms Phase 1)

Full rationale in `docs/fractal-sonification-plan.md` → "Next phase — geometry-conditioned resonance
and stronger macro tone". Keep modal **selective inside DirectOrbit** — do NOT promote it to a
universal standalone mode.

**2a. Geometry-conditioned body (highest value).** Today's modal profiles are family *defaults*.
Make a specific *instance* able to sound like "large mass + fine tingly detail" simultaneously.
Split **body shape (slow)** from **excitation/detail (fast)** so the resonator doesn't chatter:
- Slow/global → deep body: `HitRatio`, `MeanDepth`, low-passed Σ cell `Energy`, bounded-fraction,
  orbit-spread size proxy → drive modal register tilt, decay length, `ModalDrive`, low-mode gain.
- Fast/local → bright upper body: fold-event rate (already computed as `foldDelta`), high-row energy
  skew, orbit-delta variance, `NormalVariance` → trigger/brighten short high modes only.
- Available frame fields are in `src/Parsec.Audio/Sonification/FractalSonicFrame.cs` (read it).
  Implement as per-frame slewed control signals (reuse the existing `MacroSlewTau` one-pole pattern)
  that scale `modGn[]`/decay before the per-sample loop. Apply in BOTH synths.

**2b. Selective modal rollout to KIFS + QJBox.** Add modal signatures to their profiles only if the
geometry fits: KIFS → brittle/crystalline short bright modes; QJBox → warm long mid-register modes.
Mandelbox/BurningShip may stay raw-dominant — don't force bodies everywhere.

**2c. Geometry-owned Shepard layer.** Currently the Shepard–Risset layer is mostly a zoom-glide
carrier (centre A3=220, σ=1.5 oct, pan = energy centroid). Derive its centre / envelope / partial
weighting from whole-fractal geometry (low-passed total energy / bounded mass; brightness from
`NormalVariance`; width/bloom from enclosure/proximity), optionally locking partials to the active
modal body so it reinforces rather than floats. Code is duplicated in `DirectOrbitSynth` (`SampleShepard`
region, ~line 344) and `FractalDroneStream` — keep in sync.

**Acceptance for the phase:** massive filled structure → deeper/longer body modes; filamentary detail
→ short bright high activity on top; Raw↔Modal blend traverses texture↔object without collapsing
loudness or stereo image; Shepard reads as the fractal's tonal field, not a camera-motion garnish.

---

## Standing rules (from global + project CLAUDE.md)

- `/checkpoint` (local commit, source only, no ask) after each verified increment. **Never push**
  from a checkpoint; pushing follows the Post-Push Verification Protocol and needs an explicit ask.
- `/verify` before reporting done: build (`dotnet build Parsec.sln -v quiet`), targeted render gate,
  show evidence not assertions.
- Keep `DirectOrbitSynth` (offline) and `FractalDroneStream.FillDirectOrbit` (live) in lockstep on
  every DSP change — they are intentional duplicates, not a shared helper.
- New shader → add an `<EmbeddedResource>` entry to `Parsec.Rendering.Metal.csproj` or the kernel
  silently never loads (→ the nil-function crash above). `#define` constants in MSL, never
  program-scope `const` (silent compile failure — see skills.md).
- Don't break the audio-reactive path (`AudioModulationController`); sonification is mutually
  exclusive with it.
