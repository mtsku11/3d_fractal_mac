#include <metal_stdlib>
using namespace metal;

#define WVTBL 64
#define ORBTRAJ 128

struct FoldParams {
    int   iterations;
    int   mode;       // unused
    int   juliaMode;  // unused
    int   pad0;
    float4 boxParams;    // (scale, _, minRadius, fixedRadius)
    float4 surfParams;   // (postRotX, postRotY, postRotZ, _)
    float4 juliaC;       // (pivotX, pivotY, pivotZ, _)
    float4 rot;          // (preRotX, preRotY, preRotZ, fudge)
    float4 boundSphere;  // (cx, cy, cz, r)
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

struct WavetableCell {
    float raySteps[WVTBL];
    float orbitMags[WVTBL];
};

// ============================================================================
// KIFS (Kaleidoscopic IFS) distance estimator
// ============================================================================

void sphereFold(thread float3& z, thread float& dr, float minR2, float fixedR2) {
    float r2 = dot(z, z);
    if (r2 < minR2) {
        float t = fixedR2 / minR2;
        z *= t; dr *= t;
    } else if (r2 < fixedR2) {
        float t = fixedR2 / r2;
        z *= t; dr *= t;
    }
}

float3x3 eulerRotation(float3 r) {
    float cx = cos(r.x), sx = sin(r.x);
    float cy = cos(r.y), sy = sin(r.y);
    float cz = cos(r.z), sz = sin(r.z);
    float3x3 rx = float3x3(float3(1,0,0),    float3(0,cx,-sx),  float3(0,sx,cx));
    float3x3 ry = float3x3(float3(cy,0,sy),  float3(0,1,0),     float3(-sy,0,cy));
    float3x3 rz = float3x3(float3(cz,-sz,0), float3(sz,cz,0),   float3(0,0,1));
    return rz * ry * rx;
}

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float scale  = fp.boxParams.x;
    float minR   = fp.boxParams.z;
    float fixedR = fp.boxParams.w;
    float minR2  = minR * minR;
    float fixedR2 = fixedR * fixedR;

    float3 pivot = fp.juliaC.xyz;

    float3x3 preR  = eulerRotation(fp.rot.xyz);
    float3x3 postR = eulerRotation(fp.surfParams.xyz);
    bool usePre  = (fp.rot.x != 0.0f || fp.rot.y != 0.0f || fp.rot.z != 0.0f);
    bool usePost = (fp.surfParams.x != 0.0f || fp.surfParams.y != 0.0f || fp.surfParams.z != 0.0f);

    float3 z = p;
    float dr = 1.0f;
    outTrap = float4(1e20f);
    const float BAILOUT2 = 1000.0f;

    for (int i = 0; i < fp.iterations; i++) {
        if (usePre)  z = preR  * z;
        z = abs(z);
        if (usePost) z = postR * z;
        sphereFold(z, dr, minR2, fixedR2);
        z = scale * z - (scale - 1.0f) * pivot;
        dr = dr * abs(scale) + 1.0f;

        float rz = length(z);
        outTrap.x = min(outTrap.x, rz);
        outTrap.y = min(outTrap.y, abs(z.x));
        outTrap.z = min(outTrap.z, length(z.xy));
        outTrap.w = min(outTrap.w, abs(rz - 1.0f));

        if (dot(z, z) > BAILOUT2) break;
    }

    return length(z) / abs(dr);
}

float estimate(float3 p, constant FoldParams& fp) {
    float4 dummy;
    return estimateFull(p, fp, dummy);
}

// ============================================================================
// Shared helpers
// ============================================================================

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
// Orbit wavetable capture using the KIFS fold iteration (z = seed)
// ============================================================================

void captureOrbitWavetable(float3 seed, constant FoldParams& fp, device WavetableCell& wt) {
    float scale  = fp.boxParams.x;
    float minR2  = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    float3 pivot = fp.juliaC.xyz;
    float3x3 preR  = eulerRotation(fp.rot.xyz);
    float3x3 postR = eulerRotation(fp.surfParams.xyz);
    bool usePre  = (fp.rot.x != 0.0f || fp.rot.y != 0.0f || fp.rot.z != 0.0f);
    bool usePost = (fp.surfParams.x != 0.0f || fp.surfParams.y != 0.0f || fp.surfParams.z != 0.0f);

    for (int i = 0; i < WVTBL; i++) wt.raySteps[i] = 0.0f;
    float3 z = seed;
    float dr = 1.0f;
    for (int i = 0; i < WVTBL; i++) {
        if (usePre)  z = preR  * z;
        z = abs(z);
        if (usePost) z = postR * z;
        sphereFold(z, dr, minR2, fixedR2);
        z = scale * z - (scale - 1.0f) * pivot;
        dr = dr * abs(scale) + 1.0f;
        float mag = length(z);
        wt.orbitMags[i] = mag / (1.0f + mag);
    }
}

// ============================================================================
// Orbit trajectory capture — ORBTRAJ raw 3-D orbit points.
// Stores float4(clamp(z,-4,4), bounded): w=1 while bounded, 0 after escape.
// ============================================================================

void captureOrbitTrajectory(float3 seed, constant FoldParams& fp, device float4* out) {
    float scale  = fp.boxParams.x;
    float minR2  = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    float3 pivot = fp.juliaC.xyz;
    float3x3 preR  = eulerRotation(fp.rot.xyz);
    float3x3 postR = eulerRotation(fp.surfParams.xyz);
    bool usePre  = (fp.rot.x != 0.0f || fp.rot.y != 0.0f || fp.rot.z != 0.0f);
    bool usePost = (fp.surfParams.x != 0.0f || fp.surfParams.y != 0.0f || fp.surfParams.z != 0.0f);

    const float BAILOUT2 = 1000.0f;
    float3 z = seed;
    float dr = 1.0f;
    bool escaped = false;

    for (int i = 0; i < ORBTRAJ; i++) {
        if (!escaped) {
            if (usePre)  z = preR  * z;
            z = abs(z);
            if (usePost) z = postR * z;
            sphereFold(z, dr, minR2, fixedR2);
            z = scale * z - (scale - 1.0f) * pivot;
            dr = dr * abs(scale) + 1.0f;
            escaped = (dot(z, z) > BAILOUT2);
        }
        out[i] = escaped ? float4(0.0f) : float4(clamp(z, -4.0f, 4.0f), 1.0f);
    }
}

// ============================================================================
// Waveshaper strip — 64 DE samples along camera-right; CPU normalises.
// ============================================================================

void captureWaveshaperStrip(
    constant FoldParams& fp,
    constant TelemetryParams& tp,
    device float* ws)
{
    float scanRadius = fp.boundSphere.w * 0.35f;
    float3 center = tp.camPos.xyz;
    float3 axis   = tp.camRight.xyz;
    for (int i = 0; i < WVTBL; i++) {
        float t = (float(i) / float(WVTBL - 1)) * 2.0f - 1.0f;
        float3 p = center + axis * (t * scanRadius);
        float4 dummy;
        ws[i] = estimateFull(p, fp, dummy);
    }
}

// ============================================================================
// Telemetry kernel — 64x36 low-res re-march, one TelemetryCell per ray
// ============================================================================

kernel void kifs_telemetry(
    constant FoldParams&      fp         [[buffer(0)]],
    constant TelemetryParams& tp         [[buffer(1)]],
    device   TelemetryCell*   cells      [[buffer(2)]],
    device   WavetableCell*   wavetables [[buffer(3)]],
    device   float*           waveshaper [[buffer(4)]],
    device   float4*          orbitTraj  [[buffer(5)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= tp.gridWidth || py >= tp.gridHeight) return;

    if (px == 0 && py == 0)
        captureWaveshaperStrip(fp, tp, waveshaper);

    int idx = py * tp.gridWidth + px;

    int tileW = tp.gridWidth  / 4;
    int tileH = tp.gridHeight / 4;
    int tileX = px / tileW;
    int tileY = py / tileH;
    bool isTileCenter = (px == tileX * tileW + tileW / 2) &&
                        (py == tileY * tileH + tileH / 2);
    int tileIdx = tileY * 4 + tileX;

    float3 fallbackSeed = fp.boundSphere.xyz + float3(fp.boundSphere.w * 0.35f, 0.0f, 0.0f);

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
        cell.hit    = 0;
        cell.steps  = 0;
        cell.depth  = maxDist;
        cell.normal = float4(0.0f);
        cell.trap   = float4(0.0f);
        cells[idx]  = cell;
        if (isTileCenter) {
            captureOrbitWavetable(fallbackSeed, fp, wavetables[tileIdx]);
            captureOrbitTrajectory(fallbackSeed, fp, orbitTraj + tileIdx * ORBTRAJ);
        }
        return;
    }

    float t   = max(0.0f, tEnter);
    bool  didHit = false;
    int   i      = 0;

    for (i = 0; i < maxSteps; i++) {
        float3 p = ro + rd * t;
        float d = estimate(p, fp) * fudge;
        if (d < hitEps) {
            didHit = true;
            break;
        }
        t += d;
        if (t > maxDist) break;
    }

    if (!didHit) {
        cell.hit    = 0;
        cell.steps  = i;
        cell.depth  = maxDist;
        cell.normal = float4(0.0f);
        cell.trap   = float4(0.0f);
        cells[idx]  = cell;
        if (isTileCenter) {
            captureOrbitWavetable(fallbackSeed, fp, wavetables[tileIdx]);
            captureOrbitTrajectory(fallbackSeed, fp, orbitTraj + tileIdx * ORBTRAJ);
        }
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

    cell.hit    = 1;
    cell.steps  = i;
    cell.depth  = t;
    cell.normal = float4(normal, 0.0f);
    cell.trap   = trap;
    cells[idx]  = cell;

    if (isTileCenter) {
        float3 interiorSeed = hitPoint - normal * normEps * 8.0f;
        captureOrbitWavetable(interiorSeed, fp, wavetables[tileIdx]);
        captureOrbitTrajectory(interiorSeed, fp, orbitTraj + tileIdx * ORBTRAJ);
    }
}
