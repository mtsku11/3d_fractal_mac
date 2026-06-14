#include <metal_stdlib>
using namespace metal;

// Trimmed MIDI-only telemetry kernel for Quaternion Julia: one TelemetryCell per
// low-res ray. No sonification wavetable/orbit-trajectory/field-scan buffers.

struct FoldParams {
    int   iterations;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;    // (wslice, planeOffset, bailout, cutEnabled)
    float4 surfParams;   // (planeNormal.xyz, _)
    float4 juliaC;       // quaternion constant c
    float4 rot;          // (stereoMode, stereoK, stereoR, fudge)
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
// Quaternion Julia distance estimator (verbatim from quaternianjulia_raymarch.metal)
// ============================================================================

float4 qjMul(float4 p, float4 q) {
    return float4(
        p.x*q.x - p.y*q.y - p.z*q.z - p.w*q.w,
        p.x*q.y + p.y*q.x + p.z*q.w - p.w*q.z,
        p.x*q.z - p.y*q.w + p.z*q.x + p.w*q.y,
        p.x*q.w + p.y*q.z - p.z*q.y + p.w*q.x);
}

float4 qjSq(float4 q) {
    return float4(
        q.x*q.x - q.y*q.y - q.z*q.z - q.w*q.w,
        2.0f*q.x*q.y,
        2.0f*q.x*q.z,
        2.0f*q.x*q.w);
}

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float wslice     = fp.boxParams.x;
    float planeOff   = fp.boxParams.y;
    float bailout    = fp.boxParams.z;
    bool  cutEnabled = fp.boxParams.w > 0.5f;
    float4 c         = fp.juliaC;

    float stereoMode = fp.rot.x;
    float kIn        = fp.rot.y;
    float Rsph       = fp.rot.z;

    float4 z;
    float deScale = 1.0f;
    if (stereoMode > 0.5f) {
        float3 pk = p * kIn;
        float s = dot(pk, pk);
        z = Rsph * float4(2.0f * pk, s - 1.0f) / (s + 1.0f);
        deScale = (1.0f + s) / max(2.0f * kIn * Rsph, 1e-9f);
    } else {
        z = float4(p, wslice);
    }

    float4 zp = float4(1.0f, 0.0f, 0.0f, 0.0f);
    float r = 0.0f;
    outTrap = float4(1e20f);

    for (int i = 0; i < fp.iterations; i++) {
        r = length(z);
        if (r > bailout) break;
        zp = 2.0f * qjMul(z, zp);
        z  = qjSq(z) + c;
        outTrap.x = min(outTrap.x, length(z));
        outTrap.y = min(outTrap.y, abs(z.x));
        outTrap.z = min(outTrap.z, length(z.xy));
        outTrap.w = min(outTrap.w, abs(length(z) - 1.0f));
    }

    r = length(z);
    float dz = length(zp);
    float de = (dz < 1e-12f) ? 0.0f : 0.5f * log(max(r, 1e-12f)) * r / dz;
    de *= deScale;

    if (cutEnabled) {
        float3 n = normalize(fp.surfParams.xyz);
        float plane = dot(p, n) - planeOff;
        de = max(de, plane);
    }
    return de;
}

float estimate(float3 p, constant FoldParams& fp) {
    float4 dummy;
    return estimateFull(p, fp, dummy);
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

kernel void quaternianjulia_telemetry(
    constant FoldParams&      fp    [[buffer(0)]],
    constant TelemetryParams& tp    [[buffer(1)]],
    device   TelemetryCell*   cells [[buffer(2)]],
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
        float d = estimate(p, fp) * fudge;
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
    estimateFull(hitPoint, fp, trap);

    float2 k = float2(1.0f, -1.0f);
    float3 n =
        k.xyy * estimate(hitPoint + k.xyy * normEps, fp) +
        k.yyx * estimate(hitPoint + k.yyx * normEps, fp) +
        k.yxy * estimate(hitPoint + k.yxy * normEps, fp) +
        k.xxx * estimate(hitPoint + k.xxx * normEps, fp);
    float nLen = length(n);
    float3 normal = (nLen > 1e-6f) ? n / nLen : float3(0.0f);

    cell.hit = 1; cell.steps = i; cell.depth = t;
    cell.normal = float4(normal, 0.0f); cell.trap = trap;
    cells[idx] = cell;
}
