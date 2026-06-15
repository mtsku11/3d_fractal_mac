#include <metal_stdlib>
using namespace metal;

// Trimmed MIDI-only telemetry kernel for the Thomas attractor. Unlike the analytic
// fractals, the DE walks a prebuilt spatial hash, so this kernel also binds the
// trajectory/hash/sorted-index buffers (buffers 3/4/5, same layout as
// attractor_raymarch.metal). Writes one TelemetryCell per low-res ray; no
// sonification wavetable/orbit/field-scan buffers.

struct FoldParams {
    int   nPoints;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;    // (tubeRadius, gridSize, _, _)
    float4 surfParams;   // (boundsMax.xyz, _)
    float4 juliaC;       // (boundsMin.xyz, _)
    float4 rot;          // (_, _, _, fudge)
    float4 boundSphere;
};

struct TelemetryParams {
    int   gridWidth;
    int   gridHeight;
    int   pad0;
    int   pad1;
    float4 camPos;
    float4 camForward;
    float4 camRight;
    float4 camUp;
    float4 tanFov;
    float4 march;
    int   maxSteps;
    int   pad2;
    int   pad3;
    int   pad4;
};

struct TelemetryCell {
    int   hit;
    int   steps;
    float depth;
    float pad;
    float4 normal;
    float4 trap;
};

// ============================================================================
// Attractor spatial-hash DE (verbatim from attractor_raymarch.metal)
// ============================================================================

int3 cellCoord(float3 p, float3 lo, float3 ext, int n) {
    float3 u = (p - lo) / ext;
    return clamp(int3(u * float(n)), int3(0), int3(n - 1));
}

float distToSegment(float3 p, float3 a, float3 b) {
    float3 ab = b - a;
    float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-12f), 0.0f, 1.0f);
    return length(p - (a + t * ab));
}

float estimateFull(float3 p, constant FoldParams& fp,
                   device float4* traj, device int* hashCells, device int* sortedIdx,
                   thread float4& outTrap) {
    float tubeR  = fp.boxParams.x;
    int   n      = int(fp.boxParams.y);
    float3 lo    = fp.juliaC.xyz;
    float3 hi    = fp.surfParams.xyz;
    float3 ext   = hi - lo;
    float minCell = min(ext.x, min(ext.y, ext.z)) / float(n);

    float3 dBox = max(max(lo - p, p - hi), float3(0.0f));
    float outside = length(dBox);
    if (outside > minCell) {
        outTrap = float4(1e20f);
        return outside;
    }

    int nPts = fp.nPoints;
    int3 c = cellCoord(p, lo, ext, n);

    float best = 1e9f;
    bool found = false;
    outTrap = float4(1e20f);

    for (int dx = -1; dx <= 1; dx++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dz = -1; dz <= 1; dz++) {
        int3 cc = c + int3(dx, dy, dz);
        if (cc.x < 0 || cc.y < 0 || cc.z < 0 ||
            cc.x >= n || cc.y >= n || cc.z >= n) continue;
        int cellIndex = cc.x + cc.y * n + cc.z * n * n;
        int offset = hashCells[2 * cellIndex];
        int count  = hashCells[2 * cellIndex + 1];
        for (int k = 0; k < count; k++) {
            int i = sortedIdx[offset + k];
            if (i >= nPts - 1) continue;
            float3 a = traj[i].xyz;
            float3 b = traj[i + 1].xyz;
            float d = distToSegment(p, a, b);
            if (d < best) {
                best = d;
                found = true;
                outTrap.x = min(outTrap.x, length(p));
                outTrap.y = min(outTrap.y, abs(p.x));
                outTrap.z = min(outTrap.z, length(p.xy));
                outTrap.w = min(outTrap.w, abs(length(p) - 1.0f));
            }
        }
    }

    if (!found) return 0.5f * minCell;
    return min(best - tubeR, 0.5f * minCell);
}

float estimate(float3 p, constant FoldParams& fp,
               device float4* traj, device int* hashCells, device int* sortedIdx) {
    float4 dummy;
    return estimateFull(p, fp, traj, hashCells, sortedIdx, dummy);
}

bool intersectSphereForward(float3 ro, float3 rd, float3 center, float radius, thread float& tOut) {
    float3 oc = ro - center;
    float b = dot(oc, rd);
    float c = dot(oc, oc) - radius * radius;
    if (c <= 0.0f) { tOut = 0.0f; return true; }
    float disc = b * b - c;
    if (disc < 0.0f) { tOut = 0.0f; return false; }
    float sq = sqrt(disc);
    float t0 = -b - sq;
    if (t0 >= 0.0f) { tOut = t0; return true; }
    float t1 = -b + sq;
    if (t1 >= 0.0f) { tOut = t1; return true; }
    tOut = 0.0f;
    return false;
}

// ============================================================================
// Telemetry kernel — 64x36 low-res re-march, one TelemetryCell per ray
// ============================================================================

kernel void attractor_telemetry(
    constant FoldParams&      fp        [[buffer(0)]],
    constant TelemetryParams& tp        [[buffer(1)]],
    device   TelemetryCell*   cells     [[buffer(2)]],
    device   float4*          traj      [[buffer(3)]],
    device   int*             hashCells [[buffer(4)]],
    device   int*             sortedIdx [[buffer(5)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= tp.gridWidth || py >= tp.gridHeight) return;

    int idx = py * tp.gridWidth + px;

    float u = (float(px) + 0.5f) / float(tp.gridWidth);
    float v = 1.0f - (float(py) + 0.5f) / float(tp.gridHeight);
    float x = (2.0f * u - 1.0f) * tp.tanFov.x;
    float y = (2.0f * v - 1.0f) * tp.tanFov.y;
    float3 ro = tp.camPos.xyz;
    float3 rd = normalize(tp.camForward.xyz + x * tp.camRight.xyz + y * tp.camUp.xyz);

    float hitEps   = tp.march.x;
    float maxDist  = tp.march.y;
    float normEps  = tp.march.z;
    int   maxSteps = tp.maxSteps;
    float fudge    = fp.rot.w;

    TelemetryCell cell;
    cell.pad = 0.0f;

    float tEnter;
    if (!intersectSphereForward(ro, rd, fp.boundSphere.xyz, fp.boundSphere.w, tEnter)) {
        cell.hit = 0; cell.steps = 0; cell.depth = maxDist;
        cell.normal = float4(0.0f); cell.trap = float4(0.0f);
        cells[idx] = cell;
        return;
    }

    float t = max(0.0f, tEnter);
    bool  didHit = false;
    int   i = 0;

    for (i = 0; i < maxSteps; i++) {
        float3 p = ro + rd * t;
        float d = estimate(p, fp, traj, hashCells, sortedIdx) * fudge;
        if (d < hitEps) { didHit = true; break; }
        t += d;
        if (t > maxDist) break;
    }

    if (!didHit) {
        cell.hit = 0; cell.steps = i; cell.depth = maxDist;
        cell.normal = float4(0.0f); cell.trap = float4(0.0f);
        cells[idx] = cell;
        return;
    }

    float3 hitPoint = ro + rd * t;

    float4 trap;
    estimateFull(hitPoint, fp, traj, hashCells, sortedIdx, trap);

    float2 k = float2(1.0f, -1.0f);
    float3 nrm =
        k.xyy * estimate(hitPoint + k.xyy * normEps, fp, traj, hashCells, sortedIdx) +
        k.yyx * estimate(hitPoint + k.yyx * normEps, fp, traj, hashCells, sortedIdx) +
        k.yxy * estimate(hitPoint + k.yxy * normEps, fp, traj, hashCells, sortedIdx) +
        k.xxx * estimate(hitPoint + k.xxx * normEps, fp, traj, hashCells, sortedIdx);
    float nLen = length(nrm);
    float3 normal = (nLen > 1e-6f) ? nrm / nLen : float3(0.0f);

    cell.hit = 1; cell.steps = i; cell.depth = t;
    cell.normal = float4(normal, 0.0f); cell.trap = trap;
    cells[idx] = cell;
}
