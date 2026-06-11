# skills.md — Parsec project recipes and gotchas

## Build & run

**Build everything**
```
dotnet build Parsec.sln -v quiet
```

**Run the GUI app**
```
dotnet run --project src/Parsec.App/Parsec.App.csproj -c Debug
```
Requires an **active** display on macOS — will fail with `activeDisplays=0` if no display is powered on. An HDMI dummy plug satisfies this only if macOS treats it as active (not asleep). Go to System Settings → Displays → set display sleep to Never before first use.

**Run the CLI**
```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -- <command>
```
Commands: `metal-smoke [w] [h]`, `metal-bulb-smoke [w] [h]`, `metal-new-smoke` (all 14 new renderers), `metal-m12-stills` (4 fractals at 512×512 with varied grade params), `gpu-smoke`, `gpu-render <name>`, `gpu-de-validate`, `attractor-stats`, `metal-orbit-gif`, `metal-morph-mp4`, `metal-audio-reactive [wav] [startSec] [durationSec] [outMp4]`, `all`, `list`.

---

## Metal compute backend (SharpMetal)

**SharpMetal types are value types (structs), not reference types.**
`MTLDevice`, `MTLCommandQueue`, `MTLComputePipelineState` etc. are structs. Declaring them as `MTLDevice?` creates a `Nullable<MTLDevice>` which doesn't expose Metal methods. Hold them as non-nullable fields and guard usage with a separate `bool _isAvailable` flag.

**`NSError` parameters are `ref`, not `out`.**
```csharp
NSError err = default;
var lib = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref err);
```
Using `out _` or `out NSError err` will not compile.

**C# struct layout for MSL buffers.**
Use `[StructLayout(LayoutKind.Sequential, Pack = 1)]` with `System.Numerics.Vector4`. MSL requires `float4` fields at 16-byte alignment. If you place 4 `int` fields (16 bytes) before the first `Vector4`, alignment is satisfied naturally — no explicit padding needed. Verify offsets in comments; a mismatch causes silent visual corruption, not a crash.

**RGBA8 packing for `SKColorType.Rgba8888`.**
```csharp
// Little-endian layout: bytes are R, G, B, A in memory order
uint packed = (255u << 24) | (b << 16) | (g << 8) | r;
```
Alpha is the high byte. Using `(a << 24) | (r << 16) | (g << 8) | b` (ARGB) produces wrong colors.

**Readback from Metal shared memory is free on Apple Silicon.**
Unified memory means `Buffer.MemoryCopy` from `MTLBuffer.Contents` to a managed `uint[]` costs <1 ms at 640×480. No DMA copy occurs. CPU readback is not a bottleneck; the GPU kernel dispatch (`WaitUntilCompleted`) dominates.

**Measured timings on Apple Silicon M4 Pro (Release build):**

| Size | Compute | Readback | TexImage2D upload |
|------|---------|----------|-------------------|
| Mandelbox 640×480 | 5 ms | 0 ms | 0 ms |
| Mandelbox 1280×720 | 7 ms | 0 ms | 0 ms |
| Mandelbox 1920×1080 | 12 ms | 1 ms | 1 ms |
| Mandelbulb 640×480 | 7 ms | 0 ms | — |
| RotBox 640×480     | 6 ms | 0 ms | — |
| KIFS 640×480       | 5 ms | 0 ms | — |
| Kleinian 640×480   | 23 ms | 0 ms | — |
| Hybrid 640×480     | 11 ms | 0 ms | — |

**Decision (Milestone 5):** `TexImage2D` upload is negligible at all preview sizes on unified memory. Stay with the current Metal→CPU readback→TexImage2D path. No `CAMetalLayer` needed.

**MSL port from GLSL: key differences.**
- No global variables → pass orbit trap as `thread float4& outTrap` parameter
- `inout` → `thread T&`
- `mat3` column construction is identical: `float3x3(col0, col1, col2)`
- Address spaces: `constant` for read-only params (`[[buffer(0)]]`), `device` for output buffer
- Thread ID: `uint2 gid [[thread_position_in_grid]]`
- **`atan(y, x)` in GLSL → `atan2(y, x)` in MSL.** MSL's `atan` is single-arg only. The two-arg overload silently compiles but gives wrong results. Use `atan2` everywhere you mean "azimuthal angle from x-axis."
- **No global `const` variables — ever.** `const float X = 1.0f;` or `const float3 V = float3(...)` at program scope causes silent shader compilation failure: `NewLibrary` returns a library but `NewFunction` returns nil, and `NewComputePipelineState` then fatally asserts `computeFunction must not be nil`. Affects both scalar and vector types. Fix: move constants inside the function as local variables, or inline the literals directly.
- **Compact single-line helper functions can cause MSL compiler failure.** When porting GLSL shaders, write the shading tail (`estimateNormal`, `softShadow`, `ambientOcclusion`, `struct Hit`, `traceRay`, `shadeDirect`) in fully expanded multi-line form — not as single-line one-liners. Compact bodies with `return 0;` (vs `return 0.0f;`), multi-member `struct Hit{float3 a,b,c;};` syntax, and chained assignments can prevent the kernel from being found at runtime.

**Embed the `.metal` file as a resource, compile at runtime.**
```xml
<EmbeddedResource Include="Shaders\mandelbox_raymarch.metal" />
```
```csharp
var src = LoadEmbeddedMsl("mandelbox_raymarch.metal");
NSError err = default;
var lib = dev.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref err);
```
Runtime compilation catches MSL errors with a message; no separate `xcrun metal` step needed during development.

**`CameraFrame.Build` is internal to `Parsec.Rendering.Gpu`.**
Rebuild the camera frame inline in any renderer outside that project:
```csharp
var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
var up    = Vector3.Cross(right, fwd);
float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
float tanX = tanY * ((float)width / height);
```

---

## Metal SSAA + HDR post-processing — M12 pipeline

All 20 3D Metal renderers use the HDR path (M12). The kernel emits `float4`; `AccumulateHdr` accumulates in float space; `MetalPostProcess.Apply` grades and packs to RGBA8.

**Kernel output contract (all 20 `*_raymarch.metal`, not `deepzoom_metal.metal`):**
```metal
device float4* output [[buffer(2)]],
...
output[idx] = float4(color, 1.0f);  // no clamp, no pack — post does it
```

**C# renderer pattern:**
```csharp
public uint[] RenderFoo(FooParams fractal, Camera3D camera, int width, int height,
    RaymarchSettings settings, Color background, Color surface,
    Vector3 lightDirection, PaletteParams palette,
    PostProcessParams? postProcess = null)
{
    ThrowIfDisposed();
    if (!_isAvailable) throw new InvalidOperationException("Metal backend unavailable.");
    var hdr = MetalSsaa.AccumulateHdr(settings.HeroSamples, width, height, jitter =>
    {
        var fb = UploadStruct(_device, BuildFoldParams(fractal));
        var rb = UploadStruct(_device, BuildRenderParams(..., palette, jitter));
        var ob = _device.NewBuffer((ulong)(width * height * 4 * sizeof(float)),
                                   MTLResourceOptions.ResourceStorageModeShared);
        var cmd = _queue.CommandBuffer(); var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_pso!);
        enc.SetBuffer(fb, 0, 0); enc.SetBuffer(rb, 0, 1); enc.SetBuffer(ob, 0, 2);
        enc.DispatchThreadgroups(...); enc.EndEncoding();
        cmd.Commit(); cmd.WaitUntilCompleted();
        return ReadFloat4Buffer(ob, width * height);  // returns float[count*4]
    });
    return MetalPostProcess.Apply(_device, _queue, hdr, width, height, postProcess);
}
```

Key points:
- Output buffer is `4 * sizeof(float)` per pixel (not `sizeof(uint)`).
- `ReadFloat4Buffer` returns `float[count * 4]` (stride 4: R, G, B, A per pixel).
- `AccumulateHdr` short-circuits at `sampleCount == 1`, same as old `Accumulate`.
- `PostProcessParams` defaults are identity — output identical to pre-M12 at default params.
- `MetalSsaa.Accumulate` (old RGBA8 path) retained for `MetalDeepZoomRenderer` only.
- `BuildRenderParams` must accept `Vector2 jitter` and pack it into `SubpixelJitter`.
- **CLI morph exception:** `MetalMandelbulbRenderer` retains the `Vector2 subpixelJitter = default` overload; it also goes through `MetalPostProcess.Apply`.

---

## Porting a second (and subsequent) fp32 3D fractal to Metal

The Mandelbox MSL shader is the template. For each new fractal:

1. Copy `mandelbox_raymarch.metal` → `<fractal>_raymarch.metal`. Replace only the DE section (the helper functions and `estimateFull`/`estimate`). Everything from `estimateNormal` down to the end of the kernel is identical across all fp32 3D fractals and should not be modified. Use the expanded multi-line style for this tail — compact one-liners can fail silently (see MSL gotchas above).
2. Rename the kernel function: `kernel void <fractal>_raymarch(...)`.
3. Update `FoldParams` comments to match the new fractal's parameter usage (the layout is identical for all fractals that reuse `FoldParamsGpu`).
4. Create `Metal<Fractal>Renderer.cs` as a standalone class (not implementing `IThreeDimensionalRenderBackend` — that interface only declares `RenderMandelbox`). Copy `MetalMandelboxRenderer.cs`, rename, and change `BuildFoldParams` to pack the new fractal's params.
5. Add `<EmbeddedResource Include="Shaders\<fractal>_raymarch.metal" />` to `Parsec.Rendering.Metal.csproj`.
6. In `FractalView.cs`: add field, init in macOS block, `when` guard arm before the GL fallback, matching status string, dispose + null. Add `RenderWithMetal<Fractal>` method.
7. Add `metal-<fractal>-smoke` CLI command in `Parsec.Cli/Program.cs`.

**Camera position matters.** Mandelbox: `(0, 3, 12)` looking at origin. Mandelbulb: `(0, 0, 4)` looking at origin (the bulb fits in a ~1.3 unit sphere; pulling back to 12 renders it tiny). RotBox: `(0, 3, 12)` — same as Mandelbox, the default bounding sphere is radius 8.

**RotBox vs Mandelbox parameter order:** RotBox `boxParams = (scale, minRadius, fixedRadius, foldLimit)` — different from Mandelbox `(scale, foldingLimit, minRadius, fixedRadius)`. Also, RotBox Euler angles go in `surfParams.xyz`, not `rot.xyz` (which is where Mandelbox uses its optional rotation). The per-iteration `z = R * z` rotation is what makes RotBox different from a plain Mandelbox.

**KIFS parameter layout:** `boxParams = (scale, _, minRadius, fixedRadius)` — slot .y is unused (zero). Pre-rotation angles in `rot.xyz` (with fudge in `rot.w`); post-rotation angles in `surfParams.xyz`; scale pivot in `juliaC.xyz`. `eulerRotation` takes a `float3`, unlike RotBox/Hybrid which take 3 separate floats.

**Kleinian DE is numerical gradient, not analytic:** `estimate()` calls `kleinianPotential()` 7 times (1 center + 6 axis offsets for central differences). This makes each estimate call ~7× more expensive — Kleinian renders at 23 ms vs 5–11 ms for the other fractals. This is correct and expected; the analytic scalar-derivative DE collapses for inversive systems.

**Hybrid GLSL `atan(y,x)` → MSL `atan2(y,x)`:** MSL's `atan` is single-arg only. Hybrid's Mandelbulb half uses `phi = atan(z.y, z.x)` in GLSL — must be `atan2(z.y, z.x)` in MSL. The same applies to any future fractal using the spherical-coordinates power step.

**The DE shape determines which GLSL helpers are needed.** Mandelbox needs `boxFold`, `sphereFold`, `rotationFromEuler`. Mandelbulb needs only `estimateFull`/`estimate` — no helper functions at all. Remove unused helpers; they add compile time.

---

## Wiring a new compute backend into FractalView

**Pattern used for Metal (Milestone 4A):** add a nullable renderer field, init it inside `OperatingSystem.IsMacOS()` in `OnOpenGlInit`, insert a `when` guard in the `ActiveType switch` expression before the GL fallback arm, dispose and null it in `OnOpenGlDeinit`. The packed `uint[]` return flows into the same `TexImage2D` upload path — no other plumbing changes needed.

**Timing breakdown pattern (Milestone 4B):** expose `LastComputeMs`/`LastReadbackMs` as public properties on the renderer (set internally after `WaitUntilCompleted` and after the readback copy). In `FractalView`, add a `Stopwatch` around `TexImage2D` for upload time and one around the entire dirty block for total frame time. Display all four in the status string.

---

## Smoke testing GPU output

**Count non-background pixels correctly.**
Compute the expected background packed value from the actual `Color` arguments passed to the renderer, not from a hardcoded constant:
```csharp
uint bgPacked = (255u << 24) | ((uint)(bg.B * 255f + 0.5f) << 16)
                             | ((uint)(bg.G * 255f + 0.5f) << 8)
                             |  (uint)(bg.R * 255f + 0.5f);
int nonBg = pixels.Count(p => p != bgPacked);
```
A hardcoded `0xFF000000` will mis-count any non-pure-black background.

**Add a pixel dump for small renders.**
Pass `w <= 16` to trigger a hex dump of every pixel — useful for verifying the fractal structure is actually present vs. a uniform field.

---

## macOS OpenGL constraints (Apple caps GL at 4.1)

**`glDispatchCompute` and `glMemoryBarrier` do not exist on macOS.**
macOS OpenGL support is frozen at 4.1 (deprecated since 10.14). Any code that requires GL 4.3 compute shaders will fail at runtime. In `Gl.cs`, these two entrypoints are loaded with `TryLoad` (null if missing) and `Gl.SupportsCompute` reports whether they are available. The `RaymarchPipeline` and all `Gpu*Renderer` classes must NOT be constructed on macOS — their GLSL shaders use `#version 430 core` which the macOS driver rejects at compile time.

**`FractalView.OnOpenGlInit` skips the compute pipeline on macOS.**
```csharp
if (!OperatingSystem.IsMacOS())
{
    _pipeline = new RaymarchPipeline(_gl);
    _boxRenderer = new GpuMandelboxRenderer(_gl, _pipeline);
    // ... all Gpu*Renderer instances ...
    _deepPipeline = new DeepZoomPipeline(_gl);
}
```
All renderer fields stay null on macOS; the render guard is platform-aware and only checks for Metal renderers on macOS.

**Blit shaders must use `#version 330 core`, not `#version 430 core`.**
The vertex/fragment shaders that blit the Metal-computed texture to the Avalonia framebuffer use only GLSL 3.30 features (`gl_VertexID`, `texture()`). Using `#version 430 core` causes a compile error on the macOS 4.1 context. `330 core` compiles on both macOS and Windows/Linux GL 4.3 contexts.

**Avalonia's Metal UI renderer crashes on HDMI dummy plugs.**
`gr_backendrendertarget_new_metal` gets a null drawable and segfaults (KERN_INVALID_ADDRESS 0x1). Fix: add `AvaloniaNativePlatformOptions` in `Program.cs`:
```csharp
.With(new AvaloniaNativePlatformOptions
{
    RenderingMode = new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }
})
```
This tells Avalonia's own UI renderer to use OpenGL (or Software fallback) instead of Metal, which avoids the crash. Our Metal compute work is unaffected — it bypasses Avalonia's renderer entirely.

**Diagnosing headless macOS app startup with no physical display.**
When running the GUI app headlessly (HDMI dummy, no physical screen), the app window opens on the virtual display but is not visible. Use these techniques:
- `screencapture -x /tmp/screen.png` → captures the virtual framebuffer; read it with the `Read` tool
- `system_profiler SPDisplaysDataType` → shows attached displays and whether each is asleep
- Add `Console.WriteLine` to `OnOpenGlInit`/`OnOpenGlRender` and redirect stdout to a log file to diagnose GL init failures
- `MacDisplayPreflight` checks `CGGetActiveDisplayList` — requires the display to be **active** (not sleeping), not just online

---

## macOS 15+ (Sequoia) app launch blockers

### OpenAL SIGSEGV — system OpenAL is broken on Sequoia

macOS 15 broke the system OpenAL framework. `alcOpenDevice(NULL)` via the system `/System/Library/Frameworks/OpenAL.framework` causes a native SIGSEGV. This kills the entire app if `OpenALAudioPlaybackBackend` is constructed.

**Fix:** redirect OpenTK to Homebrew's `openal-soft` via `OpenALLibraryNameContainer.OverridePath` before any AL/ALC type is touched. The override must be set before the first `ALC.OpenDevice` call.

**Wrong approach — do NOT use `NativeLibrary.SetDllImportResolver`:**
OpenTK 4.9.4's `ALLoader.RegisterDllResolver()` already calls `SetDllImportResolver` internally, and .NET allows only ONE resolver per assembly. Calling it a second time throws `InvalidOperationException`, which surfaces as `TypeInitializationException` for `OpenTK.Audio.OpenAL.ALC`. The correct API is `OpenALLibraryNameContainer.OverridePath` — it's OpenTK's own mechanism, checked before the platform-default library path.

```csharp
// CORRECT — in OpenALAudioPlaybackBackend constructor, before ALC.OpenDevice
OpenALLibraryNameContainer.OverridePath = "/opt/homebrew/lib/libopenal.dylib";
```

Probe Homebrew Cellar versioned paths first (`/opt/homebrew/Cellar/openal-soft/*/lib/libopenal.dylib`), then symlink paths, then Intel-Mac paths.

**Current app startup decision:** do not construct `OpenALAudioPlaybackBackend` from `MainWindow` on macOS while the macOS renderer build is being stabilized. The audio feature is deferred, and startup crash work should not load OpenAL just to show the fractal UI. Use `UnavailableAudioPlaybackBackend` on macOS until audio work is explicitly resumed.

### Avalonia compositor crashes on macOS 15.6

Both the OpenGL and Metal Avalonia compositors crash on macOS 15.6 (Apple Silicon):
- **OpenGL compositor:** `GLDPipelineProgramRec` → SIGSEGV. Avalonia's Skia GL backend hits a driver bug.
- **Metal compositor:** `gr_backendrendertarget_new_metal` → null drawable → SIGSEGV. Skia can't acquire a Metal drawable from Avalonia's surface.

**Fix:** force Software-only rendering mode in `Program.cs`:
```csharp
.With(new AvaloniaNativePlatformOptions
{
    RenderingMode = new[] { AvaloniaNativeRenderingMode.Software }
})
```

This means `OpenGlControlBase` never gets a GL context — `OnOpenGlInit` is never called, `_ready` stays false, and the fractal view shows nothing. Requires the software-blit fallback (see below).

### Software-blit fallback for Metal renderers without GL context

When Avalonia runs in Software-only mode, `OpenGlControlBase` has no GL context. The Metal renderers still work (they produce `uint[]` via CPU-accessible unified memory), but there's no GL texture to upload to. Solution: override `Render(DrawingContext)` and use `WriteableBitmap` + `DrawImage`.

**Critical gotcha — `OpenGlControlBase` hides `InvalidateVisual()`:**
`OpenGlControlBase` declares `public new void InvalidateVisual() => RequestNextFrameRendering();`, which shadows `Visual.InvalidateVisual()`. In software mode, calling `InvalidateVisual()` on a `FractalView` (which extends `OpenGlControlBase`) routes through the compositor pipeline — dead without GL. The `Render(DrawingContext)` override is never triggered.

**Fix:** bypass the hiding with a cast:
```csharp
private void SoftInvalidate() => ((Avalonia.Visual)this).InvalidateVisual();
```
Call `SoftInvalidate()` everywhere in software-mode paths instead of `InvalidateVisual()`. This triggers Avalonia's normal `Render(DrawingContext)` dispatch.

**Detection pattern:** use double-deferred `Dispatcher.UIThread.Post` in `OnAttachedToVisualTree` — if `_ready` is still false after `DispatcherPriority.Background`, GL init failed and software mode is needed:
```csharp
Dispatcher.UIThread.Post(() => {
    Dispatcher.UIThread.Post(() => {
        if (!_ready) {
            InitMetalRenderers();
            _softwareMode = true;
            // start 16ms timer calling SoftInvalidate()
        }
    }, DispatcherPriority.Background);
}, DispatcherPriority.Loaded);
```

### SharpMetal autorelease double-free — do NOT `using`-dispose autoreleased Metal objects

Metal's Objective-C API follows standard naming conventions: methods not starting with `new`, `alloc`, `copy`, or `mutableCopy` return autoreleased objects. In a GUI app, the CFRunLoop drains the autorelease pool at the end of each iteration.

SharpMetal's `Dispose()` calls `objc_msgSend(ptr, sel_release)`. If the object was autoreleased, you get a double-free: `Dispose()` releases it (retain count → 0, freed), then the autorelease pool drain tries to release the freed pointer → `SIGSEGV` in `objc_release` / `AutoreleasePoolPage::releaseUntil`.

**Autoreleased (do NOT use `using`):**
- `_queue.CommandBuffer()` → maps to `-[MTLCommandQueue commandBuffer]`
- `cmd.ComputeCommandEncoder()` → maps to `-[MTLCommandBuffer computeCommandEncoder]`

**Retained (DO use `using` to release):**
- `device.NewBuffer(...)` → starts with `new` → retained
- `device.NewLibrary(...)` → retained
- `device.NewComputePipelineState(...)` → retained

```csharp
// CORRECT — no using on autoreleased objects
var cmd = _queue.CommandBuffer();
var enc = cmd.ComputeCommandEncoder();
// ... dispatch, EndEncoding, Commit, WaitUntilCompleted ...
// cmd and enc released by autorelease pool drain — no manual dispose

// CORRECT — using on retained objects
using var outBuf = _device.NewBuffer((ulong)(pixelCount * sizeof(uint)), MTLResourceOptions.ResourceStorageModeShared);
```

**Diagnosis:** crash report shows `objc_release` → `AutoreleasePoolPage::releaseUntil` → `__CFRunLoopPerCalloutARPEnd` on thread 0. No managed exception — pure native SIGSEGV. The CLI never hits this because it has no CFRunLoop / autorelease pool drain cycle.

### Software Render + Status — must defer StatusChanged updates

Calling `Status()` (which sets `TextBlock.Text` on the status bar) from inside `Render(DrawingContext)` triggers `InvalidateVisual()` on the TextBlock, which Avalonia rejects with `InvalidOperationException: Visual was invalidated during the render pass`. Fix: always `Dispatcher.UIThread.Post()` from `Status()`:

```csharp
private void Status(string text) =>
    Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(text));
```

### Software Render + base.Render — skip on macOS

In Software compositor mode, `base.Render(context)` routes through `OpenGlControlBase.Render()` which may touch dead GL compositor state. Skip it on macOS:

```csharp
if (!_softwareMode)
{
    if (!OperatingSystem.IsMacOS())
        base.Render(context);
    return;
}
```

---

## Fractal sonification CLI commands (M3–M7h)

**Offline synth voice:** `HybridSynth.Synthesize` now accepts an optional `FractalVoice voice` parameter (default `Mandelbox`). Apollonian uses timer-gated bells only (no wavetable). Per-fractal voices (Mandelbox/Mandelbulb/Kleinian/BurningShip/Apollonian) also exist in `FractalDroneStream` for live use.

**Offline WAV** — renders a Mandelbox fly-in, synthesises drone from geometry:
```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-sonify-drone [duration] [out.wav]
```

**M6 telemetry smoke** — validates Mandelbulb, Kleinian, BurningShip telemetry shaders:
```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m6-telemetry
```

**AV clip CLI** — renders video frames + telemetry, synthesises WAV, ffmpeg-muxes to MP4:
```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-sonify-clip [duration] [out.mp4] [w] [h]
```

**M7c/M7d — Mandelbox hybrid synth A/B and spiral:**
```
dotnet run ... -- metal-m7c-orbit [duration]     # ray-steps vs orbit-mags A/B
dotnet run ... -- metal-m7d-orbit [duration]     # 360° orbit
dotnet run ... -- metal-m7d-spiral [duration]    # helical spiral
```

**M7e — per-fractal wavetable showcase (each fractal's geometry drives the offline Mandelbox synth):**
```
dotnet run ... -- metal-m7e-mandelbulb [duration]   # Mandelbulb spiral r=6→3, elev 2→0.3
dotnet run ... -- metal-m7e-kleinian   [duration]   # Kleinian spiral r=8→3, elev 2→0.5
dotnet run ... -- metal-m7e-burningship [duration]  # BurningShip spiral r=6→2, elev 2→0.3
```
Default duration 10 s; outputs `m7e_<fractal>.wav` in the CLI output dir. The `wt?` column in the progress table confirms wavetable capture — must show `yes` throughout. Peak ~−12 to −9 dBFS is normal.

**M7f — Shepard–Risset zoom layer + portamento:**
```
dotnet run ... -- metal-m7f-shepard [duration] [outDir]   # Mandelbox radial zoom-in/out
```
Default duration 15 s; outputs `m7f_shepard.wav`. The `zV` column in the progress table shows signed zoom velocity — must flip sign at the midpoint turnaround. Glide target `gTgt` should peak at ~±8 semitones/s at maximum dive speed. Listen for: descending Shepard glide in first half, ascending in second; smooth drone portamento rather than stepping; bells audible above the drone.

**M7g — geometry-native tuning systems:**
```
dotnet run ... -- metal-m7g-apollonian [duration] [outDir]  # Apollonian JI pentatonic bells (no Metal needed)
dotnet run ... -- metal-m7g-kleinian   [duration] [outDir]  # Kleinian Pythagorean scale bells
```
Default duration 12 s; outputs `m7g_apollonian.wav` / `m7g_kleinian.wav`. Peak ~−13 to −11 dBFS normal. `GeometryPitches` column in progress table should list 5 pitches.

**M7h — DE-as-waveshaper:**
```
dotnet run ... -- metal-m7h-waveshaper [duration] [outDir]  # Mandelbox orbit with DE waveshaper layer
```
Default duration 12 s; outputs `m7h_waveshaper.wav`. `ws-rng` column should read 2.000 (full −1..1 range) for all frames once inside the fractal. Peak ~−11 to −10 dBFS normal. A/B against `m7e_mandelbox.wav` to hear the waveshaper contribution (added timbre from the DE cross-section shape).

**GeometryScale (M7g) — pitch set derivation:**
- `GeometryScale.Apollonian(rootHz)`: seeds {−1,2,2,3} → curvatures {2,3,15,35,38}, normalised to root=2, octave-reduced → ratios {1/1, 35/32, 19/16, 3/2, 15/8}. Five-note JI pentatonic; no quantizer needed.
- `GeometryScale.Kleinian(rootHz, fixedRadius, minRadius, scale)`: eigenvalue ratio r = scale×(1+fixed/min)×0.5, octave-reduced to (1,2). At defaults r=1.5 (P5) → Pythagorean circle-of-fifths (6 pitches). Guard: if r<1.03 or r>1.97, fall back to 1.5 (degenerate when fixed==min or scale==2).
- `ComputeGeometryPitches()` in `FractalView` returns Apollonian or Kleinian pitches based on `ActiveType`; null for all others.
- `GeometryPitches float[]?` is a field on `FractalSonicFrame`; computed on UI thread each frame (safe to allocate); CLI pre-computes once and reuses the reference.

**Apollonian voice design (M7g):**
- No telemetry kernel → no cell energy data. Uses camera-speed-gated timer instead of energy threshold.
- Bell interval: 0.8 s at rest, 0.25 s at speed ≥ 5 units/s (`Clamp(0.8−spd×0.11, 0.25, 0.9)`).
- No wavetable drone (guard: `if (!isApolloVoice)` around wavetable selection in `HybridSynth`).
- `_gT = 0.55f` constant (no hit-ratio data to derive it from).
- Cycles through GeometryPitches or `_defaultApoScalePitches` fallback. Each bell: 1.0–2.5 s decay, Schroeder reverb + Shepard layer same as other hybrid voices.

**DSP architecture (FractalDroneSynth / FractalDroneStream):**
- M3–M6 (legacy): 4 saw partials at C2 (65.41 Hz), one-pole LPF, noise layer. `FractalDroneSynth.Synthesize` still used for `metal-sonify-drone` / `metal-sonify-clip`.
- M7c+ (current): `HybridSynth.Synthesize` — wavetable drone (OrbitMags source) + 16 JI Dorian modal bell resonators + Schroeder reverb + Shepard layer. Geometry-driven TemperamentStrength (`NormalVariance → chaos → TemperamentStrength`). `TemperamentCeiling` (default 0.9) is the UI knob ceiling; geometry scales within it.
- Live streaming: `FractalDroneStream` with per-fractal voices. Mandelbox = metallic Dorian A2; Mandelbulb = even-harmonic HarmonicSeries A2, 1.5–4.5 s airy bells; Kleinian = Dorian A1, 3–9 s cavernous; BurningShip = Dorian C3, 0.08–0.45 s percussive. `TemperamentCeiling` public property wired to `TemperamentCeilingSlider` in `MainWindow.axaml`.

**Drone portamento (M7f):** `_wtPitchS` slews toward `_wtPitch` with τ=0.28 s (`_wtPitchSlewCf`). Radial zoom paths expose frame-rate pitch stepping that circular orbit paths hide — always use the slewed pitch for wavetable increment. Reset `_wtPitchS = _voiceRootHz` on voice switch to avoid cross-voice glitches.

**Sonification mix balance:** Drone gain is intentionally lower than bells+reverb (Mandelbox 0.18, Mandelbulb 0.15, Kleinian 0.17, BurningShip 0.19 × `_gS`). With `HitRatio≈1`, `_gS` approaches 0.95 so a 0.30 drone gain dominated everything. Keep drone ≤0.20 so Shepard layer (0.13 post-tanh) and bells are audible. The tanh limiter threshold is 0.85 (was 0.88); Shepard is added after tanh.

**Wavetable architecture (M7b–M7e):**
`WavetableCell { float raySteps[64]; float orbitMags[64]; }` = 512 bytes/tile, 8 KB for 16 tiles. Each telemetry shader has `captureOrbitWavetable` and a `WavetableCell buffer(3)`. `TelemetryReduction.ReadOrbitWavetables(buf, tileCount)` reads and percentile-normalises (5th/95th clamp, −1..1). Renderers enrich `MetalSpatialCell.OrbitWavetable`.

**DE waveshaper strip (M7h):**
- 64 floats in `buffer(4)` per telemetry dispatch; written by thread(0,0) via `captureWaveshaperStrip()` (identical function in all four telemetry shaders). Samples DE at 64 points along camera-right direction, radius = `boundSphere.w × 0.35`.
- CPU side: `TelemetryReduction.ReadWaveshaperCurve(buf)` → reads + applies `NormalizeWavetable` → `float[64]` in `FractalGeometryStats.WaveshaperCurve` → `FractalSonicFrame.WaveshaperCurve`.
- DSP: sine at slewed drone pitch processed through table lookup (`[-1,1] → [0,63]`, linear interp). Mixed at 0.07 gain after the tanh limiter. All four streaming voices and `HybridSynth.Synthesize` include it; Apollonian is guarded (`isApolloVoice`) since it has no telemetry.
- Morphs at τ=50 ms (same `_wsMorphCf` as wavetable morph). `_wsHasData = false` until first valid curve; voice is silent until then.

**M5 spatial emitters:**
16 OpenAL 3D point sources, one per 4×4 tile of the 64×36 telemetry grid. Each emitter's gain = `cell.Energy × 0.30`; pitch shifted by `cell.TrapMean.X`. Listener position/orientation updated from `FractalSonicFrame.CameraPosition/Forward/Up` each 5 ms poll tick.

---

## Fractal sonification DSP — Schroeder allpass stability

**Standard 1-tap form (stable):**
```csharp
float y = -g * x + buf[rp];
buf[w] = x + g * y;
```
Poles at z = g^(1/D) < 1 for |g| < 1 — unconditionally stable.

**2-tap form (UNSTABLE for g > golden ratio ≈ 0.618):**
```csharp
float y = -g * x + buf[rp] + g * buf[(rp + 1) % len];  // DO NOT USE
```
DC feedback = g(1+g). For g = 0.65: 0.65 × 1.65 = 1.0725 > 1 → buffer grows exponentially, saturates in ~1.25 s once bells fire. RMS locks at −1.1 dBFS flat — not a transient, a hard saturation lock from the comb clamp.

This affected both `AllPass` in `HybridSynth.cs` and `StreamAllPass` in `FractalDroneStream.cs` when M7d first wired the bell bus. The old M7c clips seemed fine only because the trigger never fired (bellMix = 0 → no input to drive the instability).

---

## Audio-reactive CLI rendering (`metal-audio-reactive`)

Renders a fractal animation video with audio feature extraction driving fractal/camera/lighting parameters.

```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-audio-reactive "path.wav" 5.0 5.0
```

**Pipeline:** WAV → `WaveAudioAnalyzer` → `AudioFeatureTrack` → per-frame `Sample(t)` → modulate params → Metal compute → PNG frames → ffmpeg mux with audio → MP4 normally. In the app, `Transparent BG` keeps the same deterministic sampling but keys the PNG frame backgrounds to alpha and muxes MOV/ProRes 4444.

**Transparent export:** in-app `Transparent BG` only affects saved outputs, not the live preview.
Hero and animation PNG frames call `ImageOutput.SavePng(..., transparentBackground: true)`, which
estimates the matte from image corners and keys matching background pixels to alpha. Render-to-Video
uses MP4/H.264 normally; with `Transparent BG`, it writes MOV/ProRes 4444 (`prores_ks`,
`yuva444p10le`). This is export-time matte keying, not native shader alpha.

**Audio features available** (from `AudioFeatureFrame`): `Rms`, `Peak`, `BassEnergy`, `MidEnergy`, `TrebleEnergy`, `SpectrumCentroidHz`, `OnsetStrength`.

**Key technique — normalize to track range:** Raw RMS/energy values are tiny for quiet music (Chopin ≈ 0.01–0.09). Pre-scan the render window to find per-feature maxima, then normalize each feature to [0,1] relative to its own peak. Apply `pow(x, 0.3)` to expand quiet dynamics.

**Key technique — EMA smoothing:** Raw per-frame values cause jitter. Use exponential moving average (α ≈ 0.2) on each smoothed parameter to get organic motion.

**Best organic fractals for audio-reactive work:**
- **QuaternionJulia** — smooth blobby forms, no axis artifacts. `WSlice` (4D cross-section) and `c.y` are great modulation targets.
- **Phoenix** — curling tendrils via `PMem` (memory strength). `PlaneOffset` sweeps the cut plane to reveal internals.
- **Mandelbulb** — dramatic shape changes via `Power`, but has a polar axis artifact that becomes visible under power modulation.
- **Biomorph** — multi-armed Pickover creatures via `Bailout B`.

**Typical multi-band mapping pattern:**
| Feature | Target | Effect |
|---|---|---|
| RMS (normalized) | headline shape param | shape morphs with overall loudness |
| Bass | camera distance | camera breathes in/out |
| Mids | palette frequency | color band density pulses |
| Treble | palette phase / shape param | color rotation or shape shift |
| Onset | light intensity | flash on transients |
| Centroid | light azimuth orbit speed | light sweeps faster when sound is brighter |

---

## Fractal sonification CLI commands (M3–M8)

```bash
# M3 offline drone WAV
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-sonify-drone [duration] [out.wav]

# M7c/d orbit vs spiral A/B comparison (Mandelbox)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7c-orbit [duration] [outDir]
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7c-spiral [duration] [outDir]

# M7d live-stream hybrid voice (Mandelbox)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7d-orbit [duration] [outDir]
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7d-spiral [duration] [outDir]

# M7e per-fractal voices
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7e-mandelbulb [duration] [outDir]
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7e-kleinian [duration] [outDir]
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7e-burningship [duration] [outDir]

# M7f Shepard–Risset zoom layer
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7f-shepard [duration] [outDir]

# M7g geometry-native tuning
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7g-apollonian [duration] [out.wav]
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m7g-kleinian [duration] [out.wav]

# M8 field-scan deep audio (stereo WAV, 4-corner spatial)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m8-fieldscan [kleinian|mandelbox] [duration] [outDir]

# M9a orbit trajectory smoke test (Mandelbox only, macOS)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m9a-orbits

# M9b offline DirectOrbitSynth A/B vs HybridSynth (Mandelbox only)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m9b-direct [duration] [outDir]
# writes m9b_direct.wav + m9b_hybrid.wav for side-by-side A/B

# M9c cross-fractal DirectOrbitSynth (all 4 sonification fractals in one pass)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m9c-direct [duration] [outDir]
# writes m9c_mandelbox_direct.wav / m9c_mandelbulb_direct.wav / m9c_kleinian_direct.wav / m9c_burningship_direct.wav

# M9d export routing smoke test (SonificationMode enum + DirectOrbit export path)
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m9d-check [duration] [outDir]
# writes m9d_export_direct.wav; GUI accept criteria printed at end

# M9d DirectOrbit listening renders with stronger geometry motion
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m9d-animated [duration] [outDir]
# writes m9d_animated_direct.wav; Mandelbox helical camera spiral + Scale modulation
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-m9d-animated-burning [duration] [outDir]
# writes m9d_burningship_animated_direct.wav; 3D Burning Ship helical camera spiral + Power modulation
```

**M8-spatial stereo design:**
- Metal kernel dispatches 256 threads → 4 × 64-sample corner scans (TL/TR/BL/BR).
- TL/TR sweep positive camUp (above centre, spectrally bright); BL/BR sweep negative camUp (dark).
- `HybridSynth` uses 4 independent wavetable oscillators; TL+BL → left, TR+BR → right.
- Spectral-tilt 1-pole shelf at 2 kHz: top corners/rows boost highs; bottom corners/rows cut them.
- Bell elevation: same per-row tilt (`bellRowTilt = [+0.35, +0.12, -0.12, -0.35]` for rows 0–3).
- `WavEncoder.Write` requires `channels: 2` for stereo output.

**Schroeder allpass DC-feedback hazard:**
The 2-tap form `y = -g*x + buf[rp] + g*buf[(rp+1)%len]` has DC gain g(1+g) > 1 for g=0.65 → exponential saturation after ~1.25 s. Always use the standard 1-tap form `y = -g*x + buf[rp]; buf[w] = x + g*y`.

---

## M9a — orbit trajectory capture pattern

**Adding a new telemetry buffer to an existing `*_telemetry.metal` kernel:**
1. `#define ORBTRAJ 128` — must be `#define`, not `const int` (program-scope `const` causes silent shader failure, see MSL gotchas above).
2. New helper function `captureOrbitTrajectory(float3 seed, constant FoldParams& fp, device float4* out)` — mirrors the fold loop of `captureOrbitWavetable` but stores `float4(clamp(z,-4,4), bounded_w)` instead of `length(z)/(1+length(z))`. Use `float4` (not `float3`) to avoid 16-byte stride misalignment on readback.
3. Add `device float4* orbitTraj [[buffer(5)]]` to the kernel signature. Call `captureOrbitTrajectory(seed, fp, orbitTraj + tileIdx * ORBTRAJ)` in all three tile-centre paths (no-sphere-intersect, no-hit, and hit), using `fallbackSeed` or `interiorSeed` to match `captureOrbitWavetable`.
4. Do NOT grow `WavetableCell` — it is marshalled by fixed layout; adding fields breaks `ReadOrbitWavetables`' stride arithmetic. Separate buffer = separate read helper.

**C# readback (no normalization, direct memcopy):**
```csharp
// TelemetryReduction.ReadOrbitTrajectories
const int N = 128;               // ORBTRAJ
const int BytesPerTile = N * 16; // float4 = 16 bytes
for (int i = 0; i < tileCount; i++) {
    var traj = new Vector4[N];
    fixed (Vector4* dst = traj)
        Buffer.MemoryCopy(src + i * BytesPerTile, dst, BytesPerTile, BytesPerTile);
    result[i] = traj;
}
```
`Vector4` in C# is 16 bytes (4 floats), matching MSL `float4` — no stride correction needed.

**M9a smoke-test output (default Mandelbox, camera (0,0,4)):**
Tile 5 [r1 c1] = 128/128 bounded (interior seed deep inside the box-fold set). Edge tiles 29–83/128 bounded. Most-energetic orbit shows alternating x-sign (box-fold): `+1.73, -0.75, +1.28, -1.62, +0.24, -0.70, +0.71, -1.41, …`

---

## M9b — DirectOrbitSynth pattern (orbit-as-oscillator)

**Core synthesis loop (offline, stereo):**

Steps through the 128-point orbit at `SamplesPerStep = 12` samples/pt (≈ 3675 pts/s), cosine-interpolated:
```csharp
float t = 0.5f - 0.5f * MathF.Cos(MathF.PI * frac);  // FSE cosine interp
var p = seg[i0] + t * (seg[i1] - seg[i0]);
```
Projects to stereo: `L = dot(p, camRight)`, `R = dot(p, camUp)`.

**Preprocessing (per segment, per frame):**
1. Compute centroid over bounded points (w > 0.5) and subtract.
2. Peak-normalize over bounded range; if peak < 1e-6, gain = 0 (degenerate orbit → silent).
3. Apply `×EscapeDecay^(i - bailout)` (0.9992/sample) past the first escaped point.

**Crossfade at segment boundaries (5 ms = 220 samples):**
- Equal-power: `prevGain = cos(π/2 · t)`, `newGain = sin(π/2 · t)`, t ∈ [0, 1].
- Clamp prevPhase to `OrbtLen - 1.001f` during crossfade to prevent end-of-orbit wrap click.
- After each frame: `prevPhase[ci] = spf / SamplesPerStep` (≈ 122.5 at 30 Hz).

**Safety chain order:** centroid subtract → peak normalize → escaped decay → DC blocker (HPF 20 Hz) → LPF (depth-modulated 2–8 kHz) → spectral-tilt elevation shelf → column pan → tanh + Shepard.

**Level calibration:** `CellScale = 0.050` gives peak ≈ −6.4 dBFS on the standard Mandelbox fly-in.

**CLI:** `metal-m9b-direct [duration] [outDir]` writes `m9b_direct.wav` + `m9b_hybrid.wav` for A/B.

---

## M9e — DirectOrbit stereo fix + per-cell detuning

**45° projection rotation (stereo balance fix).** Mandelbox orbits are confined mostly to the X
axis, so the original projection `L = dot(p, camRight)`, `R = dot(p, camUp)` silently gives
`R ≈ 0`. Fix: rotate 45° in the R/U plane:
```csharp
float pr = Vector3.Dot(centred, camRight);
float pu = Vector3.Dot(centred, camUp);
float L  = (pr + pu) * 0.70710678f;
float R  = (pr - pu) * 0.70710678f;
```
Apply in both `DoProject()` (streaming) and `Project()` (offline `DirectOrbitSynth`) — they are
separate copies.

**Per-cell detuning for chorus.** All cells at the same base rate play in unison — boring. Spread
each of 16 cells by column (10 ¢/step) and row (5 ¢/step), centred at zero:
```csharp
float detune = col * 10f + row * 5f - 37.5f;   // ±22.5 ¢ total spread
_doCellSps[ci] = (int)Math.Round(baseOrbitSps * Math.Pow(2.0, detune / 1200.0));
```
Recompute `_doCellSps` whenever `baseOrbitSps` changes. The `_doPrevPhase[ci]` accumulator is
already per-cell, so no other state changes needed.

---

## Freeverb-style reverb recipe (DirectOrbit)

Pre-delay (ring buffer) → 4 parallel comb-LPF filters → 4 series allpass filters. Delay values
(samples at 44100 Hz):

| Stage | Delay |
|---|---|
| pre-delay | 882 (≈ 20 ms) |
| comb 0 | 2111 |
| comb 1 | 2237 |
| comb 2 | 2381 |
| comb 3 | 2521 |
| allpass 0 | 601 |
| allpass 1 | 441 |
| allpass 2 | 341 |
| allpass 3 | 225 |

Constants: `fb = 0.86f`, `damp = 0.20f`, `apG = 0.50f`, `wet = 0.25f`.

T60 formula: `T60 = D_avg × log(0.001) / log(fb)`. With D_avg ≈ 47 ms, fb = 0.86 → T60 ≈ 2.15 s.

**Comb-LPF update (one per sample):**
```csharp
int rp  = (w - D + buf.Length) % buf.Length;
lpf    += damp * (buf[rp] - lpf);                 // one-pole lowpass inside feedback
float y = Math.Clamp(x + fb * lpf, -12f, 12f);   // clamp prevents NaN blow-up
buf[w]  = y;
w       = (w + 1) % buf.Length;
return y;
```
Sum 4 comb outputs × 0.25 before feeding the allpass chain.

**Allpass update:** use the **1-tap** form only — the 2-tap form is unstable (see M7d):
```csharp
int rp = (w - D + buf.Length) % buf.Length;
float y = -g * x + buf[rp];
buf[w]  = x + g * y;
w       = (w + 1) % buf.Length;
return y;
```

**Mono-to-stereo:** mix reverb bloom into both channels equally; dry signal stays stereo.
Mix bloom *inside* the tanh: `tanh((dry ± bloom) × 1.1f)` so reverb compresses with the dry.

---

## Audio thread crash protection

Wrap `FillBuffer` in the `WorkerLoop` with a try-catch that outputs silence on exception:
```csharp
try { FillBuffer(_pcmBufs[slot]); }
catch { Array.Clear(_pcmBufs[slot]); }   // silence; keep thread alive
```
This lets parameter-change races, NaN propagation from feedback paths, or transient index errors
produce a short glitch rather than killing the audio thread permanently.

**Corollary: diagnose, don't just silence.** The try-catch is a safety net, not a substitute for
fixing the root cause. Log or count exceptions in debug builds so crashes are surfaced.

---

## DirectOrbit live stereo streaming correctness

Historical bug: `FillDirectOrbit(short[] buf)` originally filled a buffer that OpenAL treated as
`ALFormat.Mono16`. In that contract the buffer has `buf.Length` mono sample slots, so the loop
must write exactly `n = buf.Length` samples:
```csharp
int n = buf.Length;   // NOT buf.Length / 2
for (int si = 0; si < n; si++) {
    buf[si] = Clip16(sig);
}
```
Writing `n = buf.Length / 2` stereo pairs into a mono buffer only fills half the buffer — the other half is stale
zeros. OpenAL delivers all `buf.Length` samples, so the effective audio rate is halved, causing
timing drift → queue starvation → audio thread death after ~30 s of playback.

Current decision: live DirectOrbit now uses `ALFormat.Stereo16`, allocates `2 × monoFrames`, and
`FillDirectOrbit` writes `n = buf.Length / 2` interleaved frames (`L,R`). Hybrid live voices are
still mono DSP internally and are duplicated into the stereo stream before blending with DirectOrbit.
Do not mix mono format with stereo-interleaved writes.

---

## DirectOrbit escaped-orbit zeroing (stereo image + spatial gate)

**The EscapeDecay constant does not create spatial silence.** `EscapeDecay = 0.9992` applied over
128 orbit steps gives `0.9992^128 ≈ 0.90` — an immediately-escaped cell still plays at 90%
amplitude. With 16 cells all similarly loud regardless of whether the fractal is present, L/R
column panning cannot create a spatial image that tracks the fractal's position on screen.

**Fix: zero post-bailout, shape gain with a floor + sqrt(bounded fraction).**
```csharp
float boundedFraction = n > 0 ? (float)bailout / n : 0f;
// bailout==0 (empty space) → silent (stereo gate); any bounded content → 0.30 .. 1.0
float shaped = bailout == 0 ? 0f : 0.30f + 0.70f * MathF.Sqrt(boundedFraction);
float gain   = peak < 1e-6f ? 0f : shaped / peak;
for (int i = 0; i < n; i++)
    result[i] = i < bailout ? result[i] * gain : Vector3.Zero;
```
- The 0.30 **floor** is what builds the "wall of 16 voices". Bare `sqrt(boundedFraction)` made
  surface cells (most of a complex full-screen image, which escape after a moderate number of
  steps) too quiet — you heard only the 1–2 deepest interior cells. The floor keeps every cell
  with *any* bounded content audible while `bailout==0` cells (drifted off-screen) stay silent,
  so the stereo gate and the wall coexist.
- `CellScale = 0.065` (was 0.050) for density — peak ≈ −10 dBFS on the standard fly-in.
- Applied in both `DoPreprocessOrbitInPlace` (streaming, in-place) and `PreprocessOrbit` (offline, returns new array). Keep the two copies in sync.
- Also reduce reverb wet to 0.25 (from 0.40) — a heavy mono bloom adds equal signal to both
  channels and undoes the spatial differentiation the zeroing creates.

**16 pan positions, not 4.** Orbit panning by column alone (`((ci%4)+0.5)/4·2−1`) stacks the 4
cells of each column on one pan position → it sounds like 3–4 voices. Add a small per-row dither
so the 16 cells spread to 16 positions without breaking the left/right screen mapping (rows stay
inside their column band):
```csharp
float pan = ((ci % 4) + 0.5f) / 4f * 2f - 1f;          // column: −0.75 .. +0.75
pan = Math.Clamp(pan + (ci / 4 - 1.5f) * 0.06f, -1f, 1f); // ±0.09 row dither
```

**Shepard + drone follow the fractal (stereo, both offline synths).** The Shepard layer was
mono-centred; pan it to the energy-weighted on-screen column centroid (√2-scaled so the centred
case matches the old full level):
```csharp
float ePanNum=0, ePanDen=0;
foreach cell: ePanNum += e*colCentre(ci); ePanDen += e;   // colCentre = ((ci%4)+0.5)/4·2−1
float shepPan = ePanDen>1e-4f ? ePanNum/ePanDen : 0f;
float shepGL = cos((shepPan+1)·π/4)·√2,  shepGR = sin((shepPan+1)·π/4)·√2;  // apply to L/R
```
`HybridSynth` drone is **effectively mono** in `OrbitMags`/`RaySteps` mode if all 4 corners share
one energy-weighted blend (`L = TL+BL`, `R = TR+BR`, `TL==TR` → `L==R`). Split the blend by column
(cols 0,1 → left corners 0,2; cols 2,3 → right corners 1,3) and add a slewed per-channel balance
gain `min(1.25, sqrt(2·sideW/totW))` so the drone fades on the side the fractal has left. The
`FieldScan` corner path is already stereo — only the cell-blend branch needs splitting.

**Live/export parity:** the DirectOrbit pan-table, cell gain floor, and 16-voice detuning are now
used by both offline export and live `FractalDroneStream`. The live ambient source is
`ALFormat.Stereo16`; hybrid voices are duplicated mono when blended, while DirectOrbit writes real
interleaved L/R samples so the 4×4 field is audible during live playback.

**DirectOrbit Pythagorean grid tuning:** the earlier 150¢/column × 50¢/row map made pitch follow
the horizontal pan more than the screen row, and rows inside a column could still merge
perceptually. The current map replaces cent detuning with strict Pythagorean fifth ratios. Treat the
bottom row as the column root, then stack pure `3/2` fifths upward:
- row 3 bottom = root
- row 2 lower-middle = root × 3/2
- row 1 upper-middle = root × (3/2)^2
- row 0 top = root × (3/2)^3

Columns use related roots by the same strict fifth chain so horizontal occupancy has harmonic
meaning, not only pan. For a C2 base example:
- col 0 root C2
- col 1 root G2 = C2 × 3/2
- col 2 root D3 = C2 × (3/2)^2
- col 3 root A3 = C2 × (3/2)^3

The per-cell playback-rate multiplier is:
```csharp
float columnRoot = MathF.Pow(1.5f, col);
float rowRatio   = MathF.Pow(1.5f, 3 - row); // bottom row 3 is root
float rateRatio  = columnRoot * rowRatio;
cellSps[ci] = SamplesPerStep / rateRatio;
```
This is intentionally not equal-tempered. A full occupied column yields a strict fifth stack
(e.g. C2, G2, D3, A3), and a full screen yields a related Pythagorean fifth lattice. The total
range is wide: col3/top row is `(3/2)^6 ≈ 11.39×` above base; if it is too bright, octave-reduce
either column roots or final `rateRatio`, but preserve pure `3/2` relationships for adjacent steps.

**Projection-level voice floor:** `PreprocessOrbit` normalises 3D orbit length, but the audible
signal is the camera right/up projection. Valid cells whose orbit motion is mostly depth-forward
can become nearly silent after projection. After preprocessing, compute projected L/R RMS per cell
and apply a bounded boost so non-empty cells reach at least `MinProjectedRms = 0.30`:
```csharp
voiceGain = prms < 1e-6f ? 0f : Math.Clamp(0.30f / prms, 1f, 6f);
```
Keep this in sync in `DirectOrbitSynth` and `FractalDroneStream`.

Keep this in sync in `DirectOrbitSynth` and `FractalDroneStream`.

---

## M9d — SonificationMode streaming pattern

**`SonificationMode` is orthogonal to `FractalVoice`.** Voice keys per-fractal telemetry dispatch (which shader runs, what timbre); mode selects DSP algorithm (Hybrid wavetable+bells vs. DirectOrbit). Both must compose — do not add a `FractalVoice.DirectOrbit` or merge the two enums.

**Zero-allocation streaming requirement.** `FillDirectOrbit` runs on the audio thread. Per-cell state must be preallocated in the constructor as ping-pong buffers:
```csharp
_doPrevSeg = new Vector3[NCells][];
_doNewSeg  = new Vector3[NCells][];
for (int ci = 0; ci < NCells; ci++) {
    _doPrevSeg[ci] = new Vector3[OrbtLen];
    _doNewSeg[ci]  = new Vector3[OrbtLen];
}
```
`DoPreprocessOrbitInPlace(trajectory, _doNewSeg[ci])` writes into the preallocated array — never `new Vector3[OrbtLen]` on the audio thread. At the end of each buffer: `Array.Copy(_doNewSeg[ci], _doPrevSeg[ci], OrbtLen)` (ping-pong swap, no allocation).

**Mode switch without restart.** `Mode` property writes `_mode` (read by `FillBuffer` before each dispatch) and calls `ResetDirectOrbitState()` to clear crossfade state. The worker thread sees the new value at the next `FillBuffer` call — no stop/start needed.

**Apollonian fallback.** Apollonian has no telemetry kernel, so it has no orbit trajectories. Guard in `FillBuffer`:
```csharp
if (_mode == SonificationMode.DirectOrbit && _voice != FractalVoice.Apollonian)
    FillDirectOrbit(buf);
else
    // ... existing voice switch ...
```

**Export routing.** Capture `_sonifyMode` into a local `exportSonifyMode` at click time (before the async task captures it), then choose the synth:
```csharp
if (exportSonifyMode == SonificationMode.DirectOrbit && exportVoice != FractalVoice.Apollonian)
    pcm = DirectOrbitSynth.Synthesize(sonicFrames, controlRateHz: RenderFps);
else
    pcm = HybridSynth.Synthesize(sonicFrames, ...);
```
