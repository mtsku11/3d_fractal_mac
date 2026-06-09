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

## Audio-reactive CLI rendering (`metal-audio-reactive`)

Renders a fractal animation video with audio feature extraction driving fractal/camera/lighting parameters.

```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -c Release -- metal-audio-reactive "path.wav" 5.0 5.0
```

**Pipeline:** WAV → `WaveAudioAnalyzer` → `AudioFeatureTrack` → per-frame `Sample(t)` → modulate params → Metal compute → PNG frames → ffmpeg mux with audio → MP4.

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
