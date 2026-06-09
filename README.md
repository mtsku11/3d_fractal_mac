# Parsec

A GPU-accelerated explorer for 2D and 3D fractals — built in C# with [Avalonia](https://avaloniaui.net/).

On **macOS**, all 20 3D fractals render via Metal compute shaders (Apple Silicon and Intel). On **Windows and Linux**, the backend uses OpenGL 4.3 compute shaders. 2D deep zoom uses perturbation theory on all platforms.

<img width="1870" height="1870" alt="Screenshot 2026-05-30 125912" src="https://github.com/user-attachments/assets/51c61924-0efb-472c-8a2d-b915ea5258d7" />

---

## Building & running

> [!IMPORTANT]
> **Build and run `src/Parsec.App/Parsec.App.csproj`.** The solution contains several projects, but `Parsec.App` is the only runnable application — the others are libraries it references.

Requires **.NET 9 SDK** — install with `brew install dotnet@9` on macOS if missing.

```bash
git clone https://github.com/mtsku11/3d_fractal_mac
cd 3d_fractal_mac

# Run the desktop app (Release is strongly recommended — the renderer is compute-heavy):
dotnet run --project src/Parsec.App/Parsec.App.csproj -c Release
```

### GPU requirements

| Platform | Requirement |
|---|---|
| macOS | Metal-capable GPU (Apple Silicon or Intel Mac with Metal support) |
| Windows / Linux | OpenGL 4.3+ with compute shaders and fp64 |

**ffmpeg** *(optional)* — the **Render to Video** button calls ffmpeg automatically if it is on your PATH or at a common Homebrew path (`/opt/homebrew/bin/ffmpeg`). Without it, frames are saved to disk and you can stitch them manually.

---

## What's inside

### 3D fractals (raymarched, distance-estimated)

Mandelbox · AmazingBox · Rotated Mandelbox · Mandelbulb · Quaternion Julia · Amazing IFS (KIFS) · Kleinian · Pseudo-Kleinian · Pseudo-Kleinian 4D · Folded Menger · Bicomplex Julia · Apollonian Gasket · Phoenix · Biomorph · Mosely Snowflake · Riemann Sphere · Mandalay Fold · Anisotropic Fold · 3D Burning Ship · Hybrid (box+bulb) · QJulia × Box · Orbit Hybrid (KIFS+Mbox)

Each is a GPU distance estimator with orbit-trap coloring, configurable lighting and reflection, and an orbit camera with adaptive speed (azimuth, elevation, distance, and target exposed as keyframeable sliders alongside the fractal parameters).

### 2D deep zoom

A perturbation-theory escape-time explorer with four formulas — **Mandelbrot, Julia, Burning Ship, Prospector** — selectable from a dropdown. Highlights:

- **Arbitrary-depth zoom** via a high-precision reference orbit (binary fixed-point) plus per-pixel delta iteration, validated against an mpmath oracle.
- Three render paths chosen automatically by depth: **direct fp64** (shallow), **fp64 perturbation** (mid), and **floatexp perturbation** (deep) on Windows/Linux, reaching radii down to ~1e-147. On macOS the Metal path uses Dekker float-float arithmetic (~1e-12).
- Julia mode exposes a keyframeable constant **κ**, so a κ sweep morphs the set in an animation.
- Drag to pan, scroll to zoom.

### Rendering & post-processing

- **HDR post-processing** (macOS Metal): brightness, contrast, tanh tone-mapping, Rec.601 luma saturation, and gamma — all adjustable as real-time sliders without re-running the fractal.
- **Hero stills** with up to 16× SSAA at resolutions up to 12K.
- **Keyframe timeline** with playback (Space to play/pause) and per-fractal animation save/load.
- **Render to Video** — exports an animation frame sequence and stitches it to MP4 via ffmpeg, with optional audio sync if a WAV file is loaded.

### Audio

Load a WAV file via the audio transport bar. The engine performs offline multi-band analysis (RMS, bass, mid, treble, onset, spectral centroid) that drives fractal, camera, and palette parameters — both in CLI audio-reactive renders and in the app via the mapping panel (feature source → parameter, with depth and smoothing). Render to Video samples features deterministically at export timestamps and muxes the audio into the MP4.

---

## Using it

1. Pick a fractal from the **FRACTAL** dropdown (top-right). For Deep Zoom 2D, a **FORMULA** dropdown appears.
2. **3D:** fly with the mouse + keyboard, or tune the Camera parameter group (azimuth, elevation, distance, target, FoV). **2D deep zoom:** drag to pan, scroll to zoom.
3. Tune parameters in the side panel.
4. Set keyframes in the timeline; hit **Space** to preview the animation.
5. **Save Hero Render** for a high-res still, or **Render to Video** to export an MP4.

---

## Known limitations

Parsec is a personal project shared in the hope it is useful — these are the rough edges to expect:

- **Deep-zoom Burning Ship at very wide views.** Perturbation is unreliable for the abs-fold map when the delta is large; the renderer uses direct fp64 at shallow zoom to compensate, but extreme wide framings can still show boundary noise. Zoom in for clean results.
- **Fly-camera speed near some 3D fractals.** A few fractals lack a CPU distance-estimate mirror, so the camera glides at a constant speed near them instead of slowing into detail. Purely a navigation nicety, not a render issue.
- **Deep-zoom precision ceiling.** Windows/Linux: ~1e-147 (floatexp path). macOS: ~1e-12 (Metal Dekker float-float). The Metal path is not bit-identical at extreme depths but covers all practical use.
- **Strange Attractor on macOS.** The Attractor fractal has no Metal renderer yet (it raymarches a precomputed trajectory hash rather than a closed-form distance estimator), so on macOS it shows a blank placeholder. It renders normally on Windows/Linux via OpenGL.
- **Hardware.** No software fallback — a Metal-capable or OpenGL 4.3+ GPU is required depending on platform.

---

## Contributing

Contributions are welcome — issues and pull requests both. This is a labor of love maintained alongside other projects, so please be patient with review times; there is no SLA.

## License

Parsec is released under the **GNU General Public License v3.0 or later**. See `LICENSE` for the full text.

## Acknowledgments

Parsec stands on decades of work from the fractal community — Tom Lowe's Mandelbox, the Mandelbulb collaboration, and the formula research shared on fractalforums and in Mandelbulber, among many others. Special thank you to Inigo Quilez for his work on fractals.

Pseudo Kleinian 4D, Mandalay Fold, and Riemann Sphere fractal formulas based approximately on formulas found within Mandelbulber.
