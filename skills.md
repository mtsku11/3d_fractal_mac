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
Commands: `metal-smoke [w] [h]`, `metal-bulb-smoke [w] [h]`, `metal-new-smoke` (all 14 new renderers), `gpu-smoke`, `gpu-render <name>`, `gpu-de-validate`, `attractor-stats`, `metal-orbit-gif`, `metal-morph-mp4`, `all`, `list`.

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

## Metal SSAA — adding hero-still supersampling to a Metal renderer

All 20 Metal renderers use `MetalSsaa.Accumulate` (in `Parsec.Rendering.Metal/MetalSsaa.cs`) to run N Halton-jittered samples and average the result. The pattern is:

```csharp
public uint[] RenderFoo(FooParams fractal, Camera3D camera, int width, int height,
    RaymarchSettings settings, Color background, Color surface,
    Vector3 lightDirection, PaletteParams palette)
{
    ThrowIfDisposed();
    if (!_isAvailable) throw new InvalidOperationException("Metal backend unavailable.");
    return MetalSsaa.Accumulate(settings.HeroSamples, width, height, jitter =>
    {
        using var fb  = UploadStruct(_device, BuildFoldParams(fractal));
        using var rb  = UploadStruct(_device, BuildRenderParams(..., palette, jitter));
        using var ob  = _device.NewBuffer(...SharedMode...);
        using var cmd = _queue.CommandBuffer();
        using var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_pso!);
        enc.SetBuffer(fb, 0, 0); enc.SetBuffer(rb, 0, 1); enc.SetBuffer(ob, 0, 2);
        enc.DispatchThreadgroups(...); enc.EndEncoding();
        cmd.Commit(); cmd.WaitUntilCompleted();
        return ReadUintBuffer(ob, width * height);
    });
}
```

Key points:
- `BuildRenderParams` must accept `Vector2 jitter` and set `SubpixelJitter = new Vector4(jitter.X, jitter.Y, 0f, 0f)` in the returned struct.
- `MetalSsaa.Accumulate` short-circuits when `sampleCount == 1` — passes `Vector2.Zero` and returns immediately, so preview renders (which always use `HeroSamples: 1`) cost nothing extra.
- CPU accumulation is free on Apple Silicon unified memory: N `Buffer.MemoryCopy` calls each cost <1 ms.
- `settings.HeroSamples` is already set from `FractalView.HeroSampleCount` (the UI ComboBox) via `HeroSettings()` — no call-site changes needed.
- **CLI morph exception:** `MetalMandelbulbRenderer.RenderMandelbulb` keeps a `Vector2 subpixelJitter = default` param. Non-zero explicit jitter bypasses the loop and calls `DispatchOneSample` directly; zero jitter (in-app) runs the SSAA loop.

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
