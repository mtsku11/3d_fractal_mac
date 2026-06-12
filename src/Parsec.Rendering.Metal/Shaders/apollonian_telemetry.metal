#include <metal_stdlib>
using namespace metal;

#define WVTBL 64
#define ORBTRAJ 128

struct FoldParams {
    int   iterations;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;   // (tangency, planeOffset, outerRadiusMult, cutEnabled)
    float4 surfParams;  // (planeNormal.xyz, deEnvelope)
    float4 juliaC;      // unused
    float4 rot;         // (_, _, _, fudge)
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
// Apollonian gasket distance estimator (sphere-inversion IFS)
// ============================================================================

float3 apoCenter(int si, float tang) {
    float s = 0.7071067811865475f * tang;
    if (si == 0) return float3( s,  s,  s);
    if (si == 1) return float3( s, -s, -s);
    if (si == 2) return float3(-s,  s, -s);
    return              float3(-s, -s,  s);
}

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float tangency   = fp.boxParams.x;
    float planeOff   = fp.boxParams.y;
    float outerMult  = fp.boxParams.z;
    bool  cutEnabled = fp.boxParams.w > 0.5f;
    float deEnvelope = fp.surfParams.w;

    float r2_in  = 1.0f;   // APO_R_INNER^2 = 1
    float r_out  = 2.2247448713915890f * outerMult;
    float r2_out = r_out * r_out;

    float3 q       = p;
    float  logScale = 0.0f;
    int    lastSph  = -1;

    float trapOrigin = 1e20f;
    float trapAxis   = 1e20f;
    float trapPlane  = 1e20f;
    float trapShell  = 1e20f;

    for (int i = 0; i < fp.iterations; i++) {
        bool inverted = false;

        for (int si = 0; si < 4; si++) {
            float3 c  = apoCenter(si, tangency);
            float3 dv = q - c;
            float  d2 = dot(dv, dv);
            if (d2 < r2_in) {
                float k  = r2_in / max(d2, 1e-30f);
                q        = c + dv * k;
                logScale += log(k);
                lastSph  = si;
                inverted = true;
                break;
            }
        }

        if (!inverted) {
            float d2o = dot(q, q);
            if (d2o > r2_out) {
                float k  = r2_out / max(d2o, 1e-30f);
                q        = q * k;
                logScale += log(k);
                lastSph  = 4;
                inverted = true;
            }
        }

        if (!inverted) break;

        float qlen = length(q);
        trapOrigin = min(trapOrigin, qlen);
        trapAxis   = min(trapAxis,   abs(q.x));
        trapPlane  = min(trapPlane,  length(q.xy));

        float shell = abs(qlen - r_out);
        for (int sj = 0; sj < 4; sj++)
            shell = min(shell, abs(length(q - apoCenter(sj, tangency)) - 1.0f));
        trapShell = min(trapShell, shell);
    }

    float de = deEnvelope * exp(-logScale);
    outTrap = float4(trapOrigin, trapAxis + 0.15f * float(lastSph), trapPlane, trapShell);

    if (cutEnabled) {
        float3 n  = normalize(fp.surfParams.xyz);
        float plane = dot(p, n) - planeOff;
        de = max(de, plane);
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
// Orbit wavetable capture using the Apollonian inversion orbit (q = seed).
// When no inversion applies the orbit has settled — hold the last magnitude.
// ============================================================================

void captureOrbitWavetable(float3 seed, constant FoldParams& fp, device WavetableCell& wt) {
    float tangency  = fp.boxParams.x;
    float outerMult = fp.boxParams.z;
    float r2_in  = 1.0f;
    float r_out  = 2.2247448713915890f * outerMult;
    float r2_out = r_out * r_out;

    for (int i = 0; i < WVTBL; i++) wt.raySteps[i] = 0.0f;
    float3 q = seed;
    bool settled = false;
    for (int i = 0; i < WVTBL; i++) {
        if (!settled) {
            bool inverted = false;
            for (int si = 0; si < 4; si++) {
                float3 c  = apoCenter(si, tangency);
                float3 dv = q - c;
                float  d2 = dot(dv, dv);
                if (d2 < r2_in) {
                    q = c + dv * (r2_in / max(d2, 1e-30f));
                    inverted = true;
                    break;
                }
            }
            if (!inverted) {
                float d2o = dot(q, q);
                if (d2o > r2_out) {
                    q = q * (r2_out / max(d2o, 1e-30f));
                    inverted = true;
                }
            }
            settled = !inverted;
        }
        float mag = length(q);
        wt.orbitMags[i] = settled ? 1.0f : mag / (1.0f + mag);
    }
}

// ============================================================================
// Orbit trajectory capture — ORBTRAJ raw 3-D orbit points.
// Stores float4(clamp(q,-4,4), bounded): w=1 while the inversion orbit is
// still active, 0 once no inversion applies (settled = empty-space escape).
// ============================================================================

void captureOrbitTrajectory(float3 seed, constant FoldParams& fp, device float4* out) {
    float tangency  = fp.boxParams.x;
    float outerMult = fp.boxParams.z;
    float r2_in  = 1.0f;
    float r_out  = 2.2247448713915890f * outerMult;
    float r2_out = r_out * r_out;

    float3 q = seed;
    bool settled = false;

    for (int i = 0; i < ORBTRAJ; i++) {
        if (!settled) {
            bool inverted = false;
            for (int si = 0; si < 4; si++) {
                float3 c  = apoCenter(si, tangency);
                float3 dv = q - c;
                float  d2 = dot(dv, dv);
                if (d2 < r2_in) {
                    q = c + dv * (r2_in / max(d2, 1e-30f));
                    inverted = true;
                    break;
                }
            }
            if (!inverted) {
                float d2o = dot(q, q);
                if (d2o > r2_out) {
                    q = q * (r2_out / max(d2o, 1e-30f));
                    inverted = true;
                }
            }
            settled = !inverted;
        }
        out[i] = settled ? float4(0.0f) : float4(clamp(q, -4.0f, 4.0f), 1.0f);
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

kernel void apollonian_telemetry(
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
