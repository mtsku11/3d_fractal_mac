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
Requires an active display on macOS — will fail with `activeDisplays=0` error in headless environments.

**Run the CLI**
```
dotnet run --project src/Parsec.Cli/Parsec.Cli.csproj -- <command>
```
Commands: `metal-smoke [w] [h]`, `metal-bulb-smoke [w] [h]`, `gpu-smoke`, `gpu-render <name>`, `gpu-de-validate`, `attractor-stats`, `all`, `list`.

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

**Measured timings at 640×480 on Apple Silicon (Release build):**
- Mandelbox GPU compute: ~5 ms
- Mandelbulb GPU compute: ~7 ms (log-space trig is heavier than fold math)
- CPU readback: <1 ms for both
- GL `TexImage2D` upload: measure via status bar in the live app

**MSL port from GLSL: key differences.**
- No global variables → pass orbit trap as `thread float4& outTrap` parameter
- `inout` → `thread T&`
- `mat3` column construction is identical: `float3x3(col0, col1, col2)`
- Address spaces: `constant` for read-only params (`[[buffer(0)]]`), `device` for output buffer
- Thread ID: `uint2 gid [[thread_position_in_grid]]`
- **`atan(y, x)` in GLSL → `atan2(y, x)` in MSL.** MSL's `atan` is single-arg only. The two-arg overload silently compiles but gives wrong results. Use `atan2` everywhere you mean "azimuthal angle from x-axis."

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

## Porting a second (and subsequent) fp32 3D fractal to Metal

The Mandelbox MSL shader is the template. For each new fractal:

1. Copy `mandelbox_raymarch.metal` → `<fractal>_raymarch.metal`. Replace only the DE section (the helper functions and `estimateFull`/`estimate`). Everything from `estimateNormal` down to the end of the kernel is identical across all fp32 3D fractals and should not be modified.
2. Rename the kernel function: `kernel void <fractal>_raymarch(...)`.
3. Update `FoldParams` comments to match the new fractal's parameter usage (the layout is identical for all fractals that reuse `FoldParamsGpu`).
4. Create `Metal<Fractal>Renderer.cs` as a standalone class (not implementing `IThreeDimensionalRenderBackend` — that interface only declares `RenderMandelbox`). Copy `MetalMandelboxRenderer.cs`, rename, and change `BuildFoldParams` to pack the new fractal's params.
5. Add `<EmbeddedResource Include="Shaders\<fractal>_raymarch.metal" />` to `Parsec.Rendering.Metal.csproj`.
6. In `FractalView.cs`: add field, init in macOS block, `when` guard arm before the GL fallback, matching status string, dispose + null. Add `RenderWithMetal<Fractal>` method.
7. Add `metal-<fractal>-smoke` CLI command in `Parsec.Cli/Program.cs`.

**Camera position matters.** Mandelbox: `(0, 3, 12)` looking at origin. Mandelbulb: `(0, 0, 4)` looking at origin (the bulb fits in a ~1.3 unit sphere; pulling back to 12 renders it tiny).

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
