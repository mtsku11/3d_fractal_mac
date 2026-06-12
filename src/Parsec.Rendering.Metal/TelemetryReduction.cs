using System.Numerics;
using System.Runtime.InteropServices;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

// Matches TelemetryCell in *_telemetry.metal (48 bytes)
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TelemetryCell
{
    public int   Hit, Steps;
    public float Depth, Pad;
    public Vector4 Normal;
    public Vector4 Trap;
}

// Matches TelemetryParams in *_telemetry.metal (128 bytes)
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MetalTelemetryParams
{
    public int GridWidth, GridHeight, Pad0, Pad1;
    public Vector4 CamPos;
    public Vector4 CamForward;
    public Vector4 CamRight;
    public Vector4 CamUp;
    public Vector4 TanFov;
    public Vector4 March;     // (hitEpsilon, maxDistance, normalEpsilon, 0)
    public int MaxSteps, Pad2, Pad3, Pad4;
}

/// <summary>
/// Shared CPU-side telemetry reduction for all fractal renderers that implement
/// a *_telemetry.metal pass.  All 64×36 telemetry kernels emit the same
/// <see cref="TelemetryCell"/> layout so this logic is fractal-independent.
/// </summary>
internal static class TelemetryReduction
{
    public static unsafe TelemetryCell[] Read(MTLBuffer buf, int count)
    {
        var result = new TelemetryCell[count];
        int size = Marshal.SizeOf<TelemetryCell>();
        IntPtr ptr = MetalBufferIO.RequireContents(buf, "telemetry readback");
        fixed (TelemetryCell* dst = result)
        {
            Buffer.MemoryCopy((void*)ptr, dst,
                (long)count * size, (long)count * size);
        }
        return result;
    }

    public static FractalGeometryStats Reduce(
        TelemetryCell[] cells, int gridW, int gridH,
        Vector3 camPos, Vector3 camFwd, Vector3 camRight, Vector3 camUp,
        float tanFovX, float tanFovY, float maxDist)
    {
        int n = cells.Length;
        int hits = 0;
        double depthSum = 0, depthSumSq = 0, stepSum = 0;
        double nxS = 0, nyS = 0, nzS = 0;
        double txS = 0, tyS = 0, tzS = 0, twS = 0;
        var steps = new int[n];

        for (int i = 0; i < n; i++)
        {
            ref var c = ref cells[i];
            steps[i] = c.Steps;
            stepSum += c.Steps;
            if (c.Hit == 0) continue;
            hits++;
            depthSum   += c.Depth;
            depthSumSq += (double)c.Depth * c.Depth;
            nxS += c.Normal.X; nyS += c.Normal.Y; nzS += c.Normal.Z;
            txS += c.Trap.X;   tyS += c.Trap.Y;
            tzS += c.Trap.Z;   twS += c.Trap.W;
        }

        float hitRatio = (float)hits / n;
        float stepMean = (float)(stepSum / n);
        Array.Sort(steps);
        float stepP90 = steps[Math.Min((int)(n * 0.9), n - 1)];

        var spatialCells = ComputeSpatialCells(cells, gridW, gridH,
            camPos, camFwd, camRight, camUp, tanFovX, tanFovY, maxDist);

        if (hits == 0)
        {
            return new FractalGeometryStats(hitRatio, 0f, 0f, stepMean, stepP90,
                Vector3.Zero, 0f, Vector4.Zero, Vector4.Zero, Cells: spatialCells);
        }

        float meanDepth  = (float)(depthSum / hits);
        float depthVar   = Math.Max(0f, (float)(depthSumSq / hits - (depthSum / hits) * (depthSum / hits)));
        var   normalMean = new Vector3((float)(nxS / hits), (float)(nyS / hits), (float)(nzS / hits));
        var   trapMean   = new Vector4((float)(txS / hits), (float)(tyS / hits),
                                       (float)(tzS / hits), (float)(twS / hits));

        double nVarSum = 0, tvxS = 0, tvyS = 0, tvzS = 0, tvwS = 0;
        for (int i = 0; i < n; i++)
        {
            ref var c = ref cells[i];
            if (c.Hit == 0) continue;
            var dn = new Vector3(c.Normal.X, c.Normal.Y, c.Normal.Z) - normalMean;
            nVarSum += dn.Length();
            tvxS += Math.Abs(c.Trap.X - trapMean.X);
            tvyS += Math.Abs(c.Trap.Y - trapMean.Y);
            tvzS += Math.Abs(c.Trap.Z - trapMean.Z);
            tvwS += Math.Abs(c.Trap.W - trapMean.W);
        }
        float normalVar = (float)(nVarSum / hits);
        var   trapVar   = new Vector4((float)(tvxS / hits), (float)(tvyS / hits),
                                      (float)(tvzS / hits), (float)(tvwS / hits));

        return new FractalGeometryStats(hitRatio, meanDepth, depthVar, stepMean, stepP90,
            normalMean, normalVar, trapMean, trapVar, Cells: spatialCells);
    }

    private static MetalSpatialCell[] ComputeSpatialCells(
        TelemetryCell[] cells, int gridW, int gridH,
        Vector3 camPos, Vector3 camFwd, Vector3 camRight, Vector3 camUp,
        float tanFovX, float tanFovY, float maxDist)
    {
        const int TilesX = 4, TilesY = 4;
        int tileW = gridW / TilesX;
        int tileH = gridH / TilesY;
        var result = new MetalSpatialCell[TilesX * TilesY];

        for (int ty = 0; ty < TilesY; ty++)
        for (int tx = 0; tx < TilesX; tx++)
        {
            int tileHits = 0;
            double depS = 0, nxT = 0, nyT = 0, nzT = 0;
            double txT = 0, tyT = 0, tzT = 0, twT = 0;
            double wxS = 0, wyS = 0, wzS = 0;
            int maxSteps = 0;
            int tileTotal = tileW * tileH;

            for (int py = ty * tileH; py < (ty + 1) * tileH; py++)
            for (int px = tx * tileW; px < (tx + 1) * tileW; px++)
            {
                ref var c = ref cells[py * gridW + px];
                if (c.Steps > maxSteps) maxSteps = c.Steps;
                if (c.Hit == 0) continue;

                tileHits++;
                depS += c.Depth;
                nxT += c.Normal.X; nyT += c.Normal.Y; nzT += c.Normal.Z;
                txT += c.Trap.X;   tyT += c.Trap.Y;
                tzT += c.Trap.Z;   twT += c.Trap.W;

                float u = (px + 0.5f) / gridW;
                float v = 1f - (py + 0.5f) / gridH;
                float rx = (2f * u - 1f) * tanFovX;
                float ry = (2f * v - 1f) * tanFovY;
                var rd = Vector3.Normalize(camFwd + rx * camRight + ry * camUp);
                wxS += camPos.X + rd.X * c.Depth;
                wyS += camPos.Y + rd.Y * c.Depth;
                wzS += camPos.Z + rd.Z * c.Depth;
            }

            int cellIdx = ty * TilesX + tx;
            float hitRatio = (float)tileHits / tileTotal;

            if (tileHits == 0)
            {
                float cu = (tx + 0.5f) / TilesX;
                float cv = 1f - (ty + 0.5f) / TilesY;
                float crx = (2f * cu - 1f) * tanFovX;
                float cry = (2f * cv - 1f) * tanFovY;
                var crd = Vector3.Normalize(camFwd + crx * camRight + cry * camUp);
                result[cellIdx] = new MetalSpatialCell(
                    WorldPosition:  camPos + crd * maxDist,
                    HitRatio:       0f,
                    MeanDepth:      maxDist,
                    StepComplexity: 0f,
                    NormalMean:     Vector3.Zero,
                    TrapMean:       Vector4.Zero,
                    Energy:         0f);
                continue;
            }

            float meanDepth    = (float)(depS / tileHits);
            var   worldPos     = new Vector3((float)(wxS / tileHits), (float)(wyS / tileHits), (float)(wzS / tileHits));
            var   normalMean   = new Vector3((float)(nxT / tileHits), (float)(nyT / tileHits), (float)(nzT / tileHits));
            var   trapMean     = new Vector4((float)(txT / tileHits), (float)(tyT / tileHits),
                                             (float)(tzT / tileHits), (float)(twT / tileHits));
            float stepComplexity = Math.Min(1f, maxSteps / 80f);
            float energy         = hitRatio * MathF.Max(0f, 1f - meanDepth / maxDist);

            result[cellIdx] = new MetalSpatialCell(
                WorldPosition:  worldPos,
                HitRatio:       hitRatio,
                MeanDepth:      meanDepth,
                StepComplexity: stepComplexity,
                NormalMean:     normalMean,
                TrapMean:       trapMean,
                Energy:         energy);
        }
        return result;
    }

    // M8-spatial: Reads 4*n floats (TL/TR/BL/BR layout), AC-couples and normalises each independently.
    public static unsafe (float[] tl, float[] tr, float[] bl, float[] br) ReadFieldScanWaveform(MTLBuffer buf, int n = 64)
    {
        var raw = new float[4 * n];
        IntPtr ptr = MetalBufferIO.RequireContents(buf, "field scan waveform readback");
        fixed (float* dst = raw)
            Buffer.MemoryCopy((void*)ptr, dst, (long)4 * n * 4, (long)4 * n * 4);
        var tl = new float[n]; var tr = new float[n];
        var bl = new float[n]; var br = new float[n];
        Array.Copy(raw, 0 * n, tl, 0, n);
        Array.Copy(raw, 1 * n, tr, 0, n);
        Array.Copy(raw, 2 * n, bl, 0, n);
        Array.Copy(raw, 3 * n, br, 0, n);
        AcNormalize(tl); AcNormalize(tr);
        AcNormalize(bl); AcNormalize(br);
        return (tl, tr, bl, br);
    }

    private static void AcNormalize(float[] data)
    {
        float sum = 0f;
        for (int i = 0; i < data.Length; i++) sum += data[i];
        float mean = sum / data.Length;
        for (int i = 0; i < data.Length; i++) data[i] -= mean;
        NormalizeWavetable(data);
    }

    // M7h: Reads the 64-float waveshaper strip and normalises it in place.
    public static unsafe float[] ReadWaveshaperCurve(MTLBuffer buf, int n = 64)
    {
        var result = new float[n];
        IntPtr ptr = MetalBufferIO.RequireContents(buf, "waveshaper readback");
        fixed (float* dst = result)
            Buffer.MemoryCopy((void*)ptr, dst, (long)n * 4, (long)n * 4);
        NormalizeWavetable(result);
        return result;
    }

    // Reads the orbitMags wavetable from each spatial tile in a WavetableCell buffer.
    // Buffer layout: [raySteps[64], orbitMags[64]] per tile (512 bytes each).
    // raySteps is ignored here — the three non-Mandelbox fractals don't populate it.
    public static unsafe float[][] ReadOrbitWavetables(MTLBuffer buf, int tileCount)
    {
        const int N = 64;
        const int BytesPerArray = N * 4;
        const int BytesPerCell  = BytesPerArray * 2;
        var result = new float[tileCount][];
        var src = (byte*)MetalBufferIO.RequireContents(buf, "orbit wavetable readback");
        for (int i = 0; i < tileCount; i++)
        {
            var orbit = new float[N];
            fixed (float* dstO = orbit)
                Buffer.MemoryCopy(src + i * BytesPerCell + BytesPerArray, dstO, BytesPerArray, BytesPerArray);
            NormalizeWavetable(orbit);
            result[i] = orbit;
        }
        return result;
    }

    // M9a: Reads orbit trajectories for all spatial tiles.
    // Buffer layout: tileCount × ORBTRAJ (128) × float4 (16 bytes) = 32 KB for 16 tiles.
    // float4 = xyz (orbit point clamped [-4,4]) + w (1=bounded, 0=escaped).
    // No normalization — raw coordinates are the synthesis source.
    public static unsafe Vector4[][] ReadOrbitTrajectories(MTLBuffer buf, int tileCount)
    {
        const int N = 128;               // ORBTRAJ
        const int BytesPerTile = N * 16; // float4 = 16 bytes each
        var result = new Vector4[tileCount][];
        var src = (byte*)MetalBufferIO.RequireContents(buf, "orbit trajectory readback");
        for (int i = 0; i < tileCount; i++)
        {
            var traj = new Vector4[N];
            fixed (Vector4* dst = traj)
                Buffer.MemoryCopy(src + i * BytesPerTile, dst, BytesPerTile, BytesPerTile);
            result[i] = traj;
        }
        return result;
    }

    internal static void NormalizeWavetable(float[] data)
    {
        var sorted = (float[])data.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        float lo = sorted[Math.Max(0, (int)(n * 0.05f))];
        float hi = sorted[Math.Min(n - 1, (int)(n * 0.95f))];
        float range = hi - lo;
        if (range < 1e-6f) { Array.Clear(data); return; }
        for (int i = 0; i < n; i++)
            data[i] = Math.Clamp((data[i] - lo) / range * 2f - 1f, -1f, 1f);
    }
}
