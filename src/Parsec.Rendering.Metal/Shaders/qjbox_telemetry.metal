#include <metal_stdlib>
using namespace metal;

#define WVTBL 64
#define ORBTRAJ 128

struct FoldParams {
    int   iterations;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;   // (scale, minRadius, fixedRadius, foldLimit)
    float4 surfParams;  // (rotX, rotY, rotZ, _)
    float4 juliaC;      // quaternion constant c
    float4 rot;         // (wslice, planeOffset, cutAxisFlag, fudge)
    float4 boundSphere; // (cx, cy, cz, r)
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
// QJBox (quaternion Julia × box fold) distance estimator
// ============================================================================

float4 qjbQmul(float4 p, float4 q) {
    return float4(p.x*q.x-p.y*q.y-p.z*q.z-p.w*q.w, p.x*q.y+p.y*q.x+p.z*q.w-p.w*q.z,
                  p.x*q.z-p.y*q.w+p.z*q.x+p.w*q.y,  p.x*q.w+p.y*q.z-p.z*q.y+p.w*q.x);
}
float4 qjbQsq(float4 q) {
    return float4(q.x*q.x-q.y*q.y-q.z*q.z-q.w*q.w, 2*q.x*q.y, 2*q.x*q.z, 2*q.x*q.w);
}
float3x3 qjbEulerRot(float3 r) {
    float cx=cos(r.x),sx=sin(r.x),cy=cos(r.y),sy=sin(r.y),cz=cos(r.z),sz=sin(r.z);
    float3x3 rx=float3x3(float3(1,0,0),float3(0,cx,-sx),float3(0,sx,cx));
    float3x3 ry=float3x3(float3(cy,0,sy),float3(0,1,0),float3(-sy,0,cy));
    float3x3 rz=float3x3(float3(cz,-sz,0),float3(sz,cz,0),float3(0,0,1));
    return rz*ry*rx;
}

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float scale  = fp.boxParams.x, minR = fp.boxParams.y, fixedR = fp.boxParams.z, foldLim = fp.boxParams.w;
    float minR2 = minR*minR, fixedR2 = fixedR*fixedR;
    float wslice = fp.rot.x;
    float4 c     = fp.juliaC;
    float3x3 R   = qjbEulerRot(fp.surfParams.xyz);

    float4 z  = float4(p, wslice);
    float4 zp = float4(1,0,0,0);
    float r = 0.0f;
    outTrap = float4(1e20f);

    for (int i = 0; i < fp.iterations; i++) {
        r = length(z);
        if (r > 4.0f) break;

        float3 z3 = R * z.xyz;
        z = float4(z3, z.w);

        z3 = clamp(z.xyz, -foldLim, foldLim) * 2.0f - z.xyz;
        float r2 = dot(z3, z3);
        if (r2 < minR2)  { float f = fixedR2/minR2; z3 *= f; zp *= f; }
        else if (r2 < fixedR2) { float f = fixedR2/r2; z3 *= f; zp *= f; }
        z3 = z3 * scale + p;
        zp = zp * abs(scale) + float4(1,0,0,0);
        z  = float4(z3, z.w);

        zp = 2.0f * qjbQmul(z, zp);
        z  = qjbQsq(z) + c;

        outTrap.x = min(outTrap.x, length(z));
        outTrap.y = min(outTrap.y, abs(z.x));
        outTrap.z = min(outTrap.z, length(z.xy));
        outTrap.w = min(outTrap.w, abs(length(z) - 1.0f));
    }

    r = length(z); float dz = length(zp);
    float de = (dz < 1e-12f) ? 0.0f : 0.25f * log(max(r, 1e-12f)) * r / dz;

    int cutFlag = int(round(fp.rot.z));
    if (cutFlag > 0) {
        float pn = (cutFlag == 1) ? p.x : (cutFlag == 2) ? p.y : p.z;
        de = max(de, pn - fp.rot.y);
    }
    return de;
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
// Orbit wavetable capture using the QJBox iteration (z = (seed, wslice))
// ============================================================================

void captureOrbitWavetable(float3 seed, constant FoldParams& fp, device WavetableCell& wt) {
    float scale  = fp.boxParams.x, minR = fp.boxParams.y, fixedR = fp.boxParams.z, foldLim = fp.boxParams.w;
    float minR2 = minR*minR, fixedR2 = fixedR*fixedR;
    float wslice = fp.rot.x;
    float4 c     = fp.juliaC;
    float3x3 R   = qjbEulerRot(fp.surfParams.xyz);

    for (int i = 0; i < WVTBL; i++) wt.raySteps[i] = 0.0f;
    float4 z = float4(seed, wslice);
    for (int i = 0; i < WVTBL; i++) {
        float r = length(z);
        if (r > 4.0f) {
            wt.orbitMags[i] = 1.0f;
        } else {
            float3 z3 = R * z.xyz;
            z = float4(z3, z.w);
            z3 = clamp(z.xyz, -foldLim, foldLim) * 2.0f - z.xyz;
            float r2 = dot(z3, z3);
            if (r2 < minR2)  { z3 *= fixedR2/minR2; }
            else if (r2 < fixedR2) { z3 *= fixedR2/r2; }
            z3 = z3 * scale + seed;
            z  = float4(z3, z.w);
            z  = qjbQsq(z) + c;
            float mag = length(z);
            wt.orbitMags[i] = mag / (1.0f + mag);
        }
    }
}

// ============================================================================
// Orbit trajectory capture — ORBTRAJ raw 3-D orbit points (z.xyz of the
// quaternion orbit). Stores float4(clamp(z.xyz,-4,4), bounded).
// ============================================================================

void captureOrbitTrajectory(float3 seed, constant FoldParams& fp, device float4* out) {
    float scale  = fp.boxParams.x, minR = fp.boxParams.y, fixedR = fp.boxParams.z, foldLim = fp.boxParams.w;
    float minR2 = minR*minR, fixedR2 = fixedR*fixedR;
    float wslice = fp.rot.x;
    float4 c     = fp.juliaC;
    float3x3 R   = qjbEulerRot(fp.surfParams.xyz);

    float4 z = float4(seed, wslice);
    bool escaped = false;

    for (int i = 0; i < ORBTRAJ; i++) {
        if (!escaped) {
            float r = length(z);
            if (r > 4.0f) {
                escaped = true;
            } else {
                float3 z3 = R * z.xyz;
                z = float4(z3, z.w);
                z3 = clamp(z.xyz, -foldLim, foldLim) * 2.0f - z.xyz;
                float r2 = dot(z3, z3);
                if (r2 < minR2)  { z3 *= fixedR2/minR2; }
                else if (r2 < fixedR2) { z3 *= fixedR2/r2; }
                z3 = z3 * scale + seed;
                z  = float4(z3, z.w);
                z  = qjbQsq(z) + c;
            }
        }
        out[i] = escaped ? float4(0.0f) : float4(clamp(z.xyz, -4.0f, 4.0f), 1.0f);
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

kernel void qjbox_telemetry(
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
