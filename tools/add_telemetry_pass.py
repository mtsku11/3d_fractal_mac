#!/usr/bin/env python3
"""Insert a trimmed MIDI-only RunTelemetryPass into the compact-style
Metal*Renderer.cs files (those whose Dispose is the single-line form). Replaces
the Dispose one-liner with the telemetry block + a Dispose that also disposes the
telemetry PSO. Idempotent: skips a file that already has RunTelemetryPass."""
import os

METAL = os.path.join("src", "Parsec.Rendering.Metal")

# (file stem, ParamsType, kernel/shader base name)
TARGETS = [
    ("Bicomplex",        "BicomplexParams",        "bicomplex"),
    ("Phoenix",          "PhoenixParams",          "phoenix"),
    ("Biomorph",         "BiomorphParams",         "biomorph"),
    ("Mosely",           "MoselyParams",           "mosely"),
    ("PseudoKleinian4D", "PseudoKleinian4DParams", "pseudokleinian4d"),
    ("RiemannSphere",    "RiemannSphereParams",    "riemannsphere"),
    ("Mandalay",         "MandalayParams",         "mandalay"),
    ("Anisotropic",      "AnisotropicParams",      "anisotropic"),
    ("OrbitHybrid",      "OrbitHybridParams",      "orbithybrid"),
]

DISPOSE_OLD = ("    public void Dispose() { if (_disposed) return; _disposed = true; "
               "if (_isAvailable) { _pso.Dispose(); _queue.Dispose(); _device.Dispose(); } }")

BLOCK = """    // Trimmed MIDI-only telemetry pass — geometry stats + 4x4 cells + full-res centroid.
    private MTLComputePipelineState _telemetryPso;
    private bool _telemetryPsoReady, _telemetryPsoAvailable;

    public FractalGeometryStats? RunTelemetryPass({params} fractal, Camera3D camera, RaymarchSettings settings)
    {{
        if (!_isAvailable) return null;
        ThrowIfDisposed();
        if (!EnsureTelemetryPso()) return null;

        int gridW = 64, gridH = 36;
        int cellCount = gridW * gridH;
        int cellSize = Marshal.SizeOf<TelemetryCell>();

        using var foldBuf   = UploadStruct(_device, BuildFoldParams(fractal));
        using var telBuf    = UploadStruct(_device, BuildTelemetryParams(camera, gridW, gridH, settings));
        using var outputBuf = _device.NewBuffer((ulong)(cellCount * cellSize), MTLResourceOptions.ResourceStorageModeShared);

        var cmd = _queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_telemetryPso);
        enc.SetBuffer(foldBuf, 0, 0); enc.SetBuffer(telBuf, 0, 1); enc.SetBuffer(outputBuf, 0, 2);
        enc.DispatchThreadgroups(
            new MTLSize {{ width = (ulong)((gridW + 7) / 8), height = (ulong)((gridH + 7) / 8), depth = 1 }},
            new MTLSize {{ width = 8, height = 8, depth = 1 }});
        enc.EndEncoding();
        cmd.Commit(); cmd.WaitUntilCompleted();

        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * camera.AspectRatio;

        var cells = TelemetryReduction.Read(outputBuf, cellCount);
        return TelemetryReduction.Reduce(cells, gridW, gridH,
            camera.Position, fwd, right, up, tanX, tanY, settings.MaxDistance);
    }}

    private bool EnsureTelemetryPso()
    {{
        if (_telemetryPsoReady) return _telemetryPsoAvailable;
        _telemetryPsoReady = true;
        try
        {{
            var src = LoadEmbeddedMsl("{kernel}_telemetry.metal");
            NSError libErr = default;
            var library  = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref libErr);
            var function = library.NewFunction(NSString.String("{kernel}_telemetry"));
            NSError psoErr = default;
            _telemetryPso = _device.NewComputePipelineState(function, ref psoErr);
            _telemetryPsoAvailable = true;
        }}
        catch {{ /* leave _telemetryPsoAvailable = false */ }}
        return _telemetryPsoAvailable;
    }}

    private static MetalTelemetryParams BuildTelemetryParams(Camera3D camera, int gridW, int gridH, RaymarchSettings s)
    {{
        var fwd   = Vector3.Normalize(camera.LookAt - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(fwd, camera.Up));
        var up    = Vector3.Cross(right, fwd);
        float tanY = MathF.Tan(camera.VerticalFovRadians * 0.5f);
        float tanX = tanY * camera.AspectRatio;
        return new MetalTelemetryParams
        {{
            GridWidth = gridW, GridHeight = gridH, Pad0 = 0, Pad1 = 0,
            CamPos = new Vector4(camera.Position, 0f), CamForward = new Vector4(fwd, 0f),
            CamRight = new Vector4(right, 0f), CamUp = new Vector4(up, 0f),
            TanFov = new Vector4(tanX, tanY, 0f, 0f),
            March = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, 0f),
            MaxSteps = s.MaxSteps, Pad2 = 0, Pad3 = 0, Pad4 = 0,
        }};
    }}

    public void Dispose() {{ if (_disposed) return; _disposed = true; if (_isAvailable) {{ _pso.Dispose(); if (_telemetryPsoAvailable) _telemetryPso.Dispose(); _queue.Dispose(); _device.Dispose(); }} }}"""


for stem, params, kernel in TARGETS:
    path = os.path.join(METAL, f"Metal{stem}Renderer.cs")
    with open(path) as fh:
        text = fh.read()
    if "RunTelemetryPass" in text:
        print(f"  skip {stem} (already has RunTelemetryPass)")
        continue
    if DISPOSE_OLD not in text:
        print(f"  !! {stem}: Dispose one-liner not found — needs manual handling")
        continue
    text = text.replace(DISPOSE_OLD, BLOCK.format(params=params, kernel=kernel))
    with open(path, "w") as fh:
        fh.write(text)
    print(f"  patched {stem}")
