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
Commands: `metal-smoke [w] [h]`, `gpu-smoke`, `gpu-render <name>`, `gpu-de-validate`, `attractor-stats`, `all`, `list`.

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
- GPU compute: ~5 ms
- CPU readback: <1 ms
- GL `TexImage2D` upload: measure via status bar in the live app

**MSL port from GLSL: key differences.**
- No global variables → pass orbit trap as `thread float4& outTrap` parameter
- `inout` → `thread T&`
- `mat3` column construction is identical: `float3x3(col0, col1, col2)`
- Address spaces: `constant` for read-only params (`[[buffer(0)]]`), `device` for output buffer
- Thread ID: `uint2 gid [[thread_position_in_grid]]`

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
