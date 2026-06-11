#include <metal_stdlib>
using namespace metal;

// Number of samples per single-cycle wavetable (M7e).
#define WVTBL 64
#define ORBTRAJ 128

struct FoldParams {
    int   iterations;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;    // (power, bailout, _, _)
    float4 surfParams;
    float4 juliaC;
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
    float4 march;        // (hitEpsilon, maxDistance, normalEpsilon, 0)
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

// Per-tile wavetable: orbitMags = length(z) at each inner iteration, normalised on CPU.
// raySteps is zeroed (not captured for this fractal).
struct WavetableCell {
    float raySteps[WVTBL];
    float orbitMags[WVTBL];
};

// ============================================================================
// Mandelbulb distance estimator
// ============================================================================

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float power   = fp.boxParams.x;
    float bailout = fp.boxParams.y;

    float3 z   = p;
    float  ldr = 0.0f;
    float  r   = 0.0f;
    outTrap = float4(1e20f);

    for (int i = 0; i < fp.iterations; i++) {
        r = length(z);
        if (r > bailout) break;

        float theta = acos(clamp(z.z / max(r, 1e-12f), -1.0f, 1.0f));
        float phi   = atan2(z.y, z.x);

        float x = ldr + log(power) + (power - 1.0f) * log(max(r, 1e-12f));
        ldr = x + log(1.0f + exp(-x));

        float zr = pow(r, power);
        theta   *= power;
        phi     *= power;
        z = zr * float3(sin(theta) * cos(phi),
                        sin(theta) * sin(phi),
                        cos(theta)) + p;

        outTrap.x = min(outTrap.x, length(z));
        outTrap.y = min(outTrap.y, abs(z.x));
        outTrap.z = min(outTrap.z, length(z.xy));
        outTrap.w = min(outTrap.w, abs(length(z) - 1.0f));
    }

    return 0.5f * log(max(r, 1e-12f)) * r * exp(-ldr);
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
// M7e: orbit wavetable capture (camera-independent; raySteps zeroed)
// ============================================================================

void captureOrbitWavetable(float3 seed, constant FoldParams& fp, device WavetableCell& wt) {
    float power   = fp.boxParams.x;
    float bailout = fp.boxParams.y;
    for (int i = 0; i < WVTBL; i++) wt.raySteps[i] = 0.0f;
    float3 c = (fp.juliaMode == 1) ? fp.juliaC.xyz : seed;
    float3 z = float3(0.0f);
    for (int i = 0; i < WVTBL; i++) {
        float r = length(z);
        if (r > bailout) {
            wt.orbitMags[i] = 1.0f;
        } else {
            float theta = acos(clamp(z.z / max(r, 1e-12f), -1.0f, 1.0f));
            float phi   = atan2(z.y, z.x);
            float zr    = pow(r, power);
            theta *= power;
            phi   *= power;
            z = zr * float3(sin(theta) * cos(phi),
                            sin(theta) * sin(phi),
                            cos(theta)) + c;
            wt.orbitMags[i] = r / (1.0f + r);
        }
    }
}

// ============================================================================
// M9c: Orbit trajectory capture — ORBTRAJ raw 3-D orbit points.
// Stores float4(clamp(z,-4,4), bounded): w=1 while bounded, 0 after bailout.
// ============================================================================

void captureOrbitTrajectory(float3 seed, constant FoldParams& fp, device float4* out) {
    float power   = fp.boxParams.x;
    float bailout = fp.boxParams.y;
    float3 c = (fp.juliaMode == 1) ? fp.juliaC.xyz : seed;
    float3 z = float3(0.0f);
    bool escaped = false;

    for (int i = 0; i < ORBTRAJ; i++) {
        if (!escaped) {
            float r = length(z);
            if (r > bailout) {
                escaped = true;
            } else {
                float theta = acos(clamp(z.z / max(r, 1e-12f), -1.0f, 1.0f));
                float phi   = atan2(z.y, z.x);
                float zr    = pow(r, power);
                theta *= power;
                phi   *= power;
                z = zr * float3(sin(theta) * cos(phi),
                                sin(theta) * sin(phi),
                                cos(theta)) + c;
            }
        }
        out[i] = escaped ? float4(0.0f) : float4(clamp(z, -4.0f, 4.0f), 1.0f);
    }
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

kernel void mandelbulb_telemetry(
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
