#include <metal_stdlib>
using namespace metal;

#define WVTBL 64
#define ORBTRAJ 128

struct FoldParams {
    int   iterations;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;    // (scale, cell, minRadius, fixedRadius)
    float4 surfParams;
    float4 juliaC;       // (offsetX, offsetY, offsetZ, _)
    float4 rot;          // (_, _, _, fudge)
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
// Kleinian distance estimator (numerical gradient)
// ============================================================================

float kleinianPotential(float3 p, constant FoldParams& fp) {
    float scale   = fp.boxParams.x;
    float cell    = fp.boxParams.y;
    float minR2   = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    float3 c = fp.juliaC.xyz;

    float3 z = p;
    for (int i = 0; i < fp.iterations; i++) {
        float r2 = dot(z, z);
        if (r2 < minR2)        z *= (fixedR2 / minR2);
        else if (r2 < fixedR2) z *= (fixedR2 / r2);
        z = clamp(z, -cell, cell) * 2.0f - z;
        z = z * scale + c;
    }
    return log(max(length(z), 1e-12f));
}

float estimateDE(float3 p, constant FoldParams& fp) {
    float EPS = 1e-4f;
    float v  = kleinianPotential(p, fp);
    float vx = kleinianPotential(p + float3(EPS, 0.0f, 0.0f), fp)
             - kleinianPotential(p - float3(EPS, 0.0f, 0.0f), fp);
    float vy = kleinianPotential(p + float3(0.0f, EPS, 0.0f), fp)
             - kleinianPotential(p - float3(0.0f, EPS, 0.0f), fp);
    float vz = kleinianPotential(p + float3(0.0f, 0.0f, EPS), fp)
             - kleinianPotential(p - float3(0.0f, 0.0f, EPS), fp);
    float3 grad = float3(vx, vy, vz) / (2.0f * EPS);
    float g = length(grad);
    if (g < 1e-12f) return 1e3f;
    return abs(v) / g;
}

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float de = estimateDE(p, fp);

    float scale   = fp.boxParams.x;
    float cell    = fp.boxParams.y;
    float minR2   = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    float3 c = fp.juliaC.xyz;
    float3 z = p;
    outTrap = float4(1e20f);
    for (int i = 0; i < fp.iterations; i++) {
        float r2 = dot(z, z);
        if (r2 < minR2)        z *= (fixedR2 / minR2);
        else if (r2 < fixedR2) z *= (fixedR2 / r2);
        z = clamp(z, -cell, cell) * 2.0f - z;
        z = z * scale + c;
        float rz = length(z);
        outTrap.x = min(outTrap.x, rz);
        outTrap.y = min(outTrap.y, abs(z.x));
        outTrap.z = min(outTrap.z, length(z.xy));
        outTrap.w = min(outTrap.w, abs(rz - 1.0f));
    }
    return de;
}

float estimate(float3 p, constant FoldParams& fp) {
    return estimateDE(p, fp);
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
// M7e: orbit wavetable capture using the Kleinian folding iteration
// ============================================================================

void captureOrbitWavetable(float3 seed, constant FoldParams& fp, device WavetableCell& wt) {
    float scale   = fp.boxParams.x;
    float cell    = fp.boxParams.y;
    float minR2   = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    float3 c = fp.juliaC.xyz;
    for (int i = 0; i < WVTBL; i++) wt.raySteps[i] = 0.0f;
    float3 z = seed;
    for (int i = 0; i < WVTBL; i++) {
        float r2 = dot(z, z);
        if (r2 < minR2)        z *= (fixedR2 / minR2);
        else if (r2 < fixedR2) z *= (fixedR2 / r2);
        z = clamp(z, -cell, cell) * 2.0f - z;
        z = z * scale + c;
        float mag = length(z);
        wt.orbitMags[i] = mag / (1.0f + mag);
    }
}

// M9c: Orbit trajectory capture — ORBTRAJ raw 3-D orbit points.
// Stores float4(clamp(z,-4,4), bounded): w=1 while bounded, 0 after escape.
// ============================================================================

void captureOrbitTrajectory(float3 seed, constant FoldParams& fp, device float4* out) {
    float scale   = fp.boxParams.x;
    float cell    = fp.boxParams.y;
    float minR2   = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    float3 c = fp.juliaC.xyz;
    float3 z = seed;
    const float BAILOUT2 = 1000.0f;
    bool escaped = false;

    for (int i = 0; i < ORBTRAJ; i++) {
        if (!escaped) {
            float r2 = dot(z, z);
            if (r2 < minR2)        z *= (fixedR2 / minR2);
            else if (r2 < fixedR2) z *= (fixedR2 / r2);
            z = clamp(z, -cell, cell) * 2.0f - z;
            z = z * scale + c;
            escaped = (dot(z, z) > BAILOUT2);
        }
        out[i] = escaped ? float4(0.0f) : float4(clamp(z, -4.0f, 4.0f), 1.0f);
    }
}

// Camera-position-dependent: record DE values along the tile-centre ray.
// Produces a wavetable that changes continuously as the camera orbits the lattice.
void captureRayWavetable(
    float3 ro, float3 rd,
    constant FoldParams& fp,
    constant TelemetryParams& tp,
    device WavetableCell& wt)
{
    float hitEps  = tp.march.x;
    float maxDist = tp.march.y;
    float fudge   = fp.rot.w;
    float t = 0.0f;
    float lastDe = maxDist;
    int   i = 0;
    for (; i < WVTBL; i++) {
        float3 p = ro + rd * t;
        float de = estimate(p, fp);
        wt.raySteps[i] = de;
        lastDe = de;
        if (de < hitEps || t > maxDist) { i++; break; }
        t += max(de * fudge, hitEps * 2.0f);
    }
    for (; i < WVTBL; i++)
        wt.raySteps[i] = lastDe;
}

// ============================================================================
// M7h: Waveshaper strip — 64 DE samples along camera-right; CPU normalises.
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

kernel void kleinian_telemetry(
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
            captureRayWavetable(ro, rd, fp, tp, wavetables[tileIdx]);
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
            captureRayWavetable(ro, rd, fp, tp, wavetables[tileIdx]);
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
        captureRayWavetable(ro, rd, fp, tp, wavetables[tileIdx]);
        captureOrbitWavetable(interiorSeed, fp, wavetables[tileIdx]);
        captureOrbitTrajectory(interiorSeed, fp, orbitTraj + tileIdx * ORBTRAJ);
    }
}

// ============================================================================
// M8-spatial: Kleinian variant of the 4-corner field-scan.
// Same geometry as Mandelbox version; only kernel name and estimate() differ.
// 1-D dispatch of 4*WVTBL threads:
//   gid   0.. 63 = TL  gid  64..127 = TR  gid 128..191 = BL  gid 192..255 = BR
// ============================================================================
kernel void kleinian_fieldscan(
    constant FoldParams&       fp  [[buffer(0)]],
    constant TelemetryParams&  tp  [[buffer(1)]],
    device   float*            out [[buffer(2)]],
    uint gid [[thread_position_in_grid]])
{
    if (int(gid) >= 4 * WVTBL) return;
    uint  quadrant = gid / uint(WVTBL);
    uint  localGid = gid % uint(WVTBL);
    float xSign    = (quadrant == 0 || quadrant == 2) ? -1.0f : 1.0f;
    float ySign    = (quadrant < 2)                   ?  1.0f : -1.0f;
    float scanR    = fp.boundSphere.w * 0.30f;
    float y        = (float(localGid) / float(WVTBL - 1)) * scanR * ySign;
    float3 p = tp.camPos.xyz
             + tp.camRight.xyz * (xSign * scanR * 0.40f)
             + tp.camUp.xyz    * y;
    out[gid] = estimate(p, fp);
}
