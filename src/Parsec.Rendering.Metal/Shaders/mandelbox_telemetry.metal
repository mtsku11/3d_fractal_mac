#include <metal_stdlib>
using namespace metal;

// Number of samples per single-cycle wavetable (M7b).
#define WVTBL 64
// Number of orbit trajectory points per spatial tile (M9a).
#define ORBTRAJ 128

// ============================================================================
// Parameter structs — layout must match MetalMandelboxRenderer internal structs
// ============================================================================

struct FoldParams {
    int   iterations;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;   // (scale, foldingLimit, minRadius, fixedRadius)
    float4 surfParams;  // unused here, kept for layout match
    float4 juliaC;      // (cx, cy, cz, _)
    float4 rot;         // (rotX, rotY, rotZ, fudge)
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
    float4 tanFov;      // (tanFovX, tanFovY, 0, 0)
    float4 march;       // (hitEpsilon, maxDistance, normalEpsilon, 0)
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

// M7b: per-tile single-cycle wavetable data.
// One entry per spatial tile (4×4 = 16 tiles); only the tile-centre thread writes it.
// raySteps  = DE values along the centre ray, normalised to [-1,1] on the CPU.
// orbitMags = length(z) of a representative orbit point at each inner iteration,
//             normalised to [-1,1] on the CPU.
struct WavetableCell {
    float raySteps[WVTBL];
    float orbitMags[WVTBL];
};

// ============================================================================
// Mandelbox distance estimator (mirrors mandelbox_raymarch.metal)
// ============================================================================

float3 boxFold(float3 z, float L) {
    return clamp(z, -L, L) * 2.0f - z;
}

float3 amazingFold(float3 z, float L) {
    z = abs(z);
    return clamp(z, -L, L) * 2.0f - z;
}

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

float3x3 rotationFromEuler(float3 r) {
    float cx = cos(r.x), sx = sin(r.x);
    float cy = cos(r.y), sy = sin(r.y);
    float cz = cos(r.z), sz = sin(r.z);
    float3x3 rx = float3x3(float3(1,0,0),    float3(0,cx,-sx),  float3(0,sx,cx));
    float3x3 ry = float3x3(float3(cy,0,sy),  float3(0,1,0),     float3(-sy,0,cy));
    float3x3 rz = float3x3(float3(cz,-sz,0), float3(sz,cz,0),   float3(0,0,1));
    return rz * ry * rx;
}

float estimateFull(float3 p, constant FoldParams& fp, thread float4& outTrap) {
    float scale        = fp.boxParams.x;
    float foldingLimit = fp.boxParams.y;
    float minRadius    = fp.boxParams.z;
    float fixedRadius  = fp.boxParams.w;

    float minR2   = minRadius * minRadius;
    float fixedR2 = fixedRadius * fixedRadius;

    float3x3 R = rotationFromEuler(fp.rot.xyz);
    bool useRotation = (fp.rot.x != 0.0f || fp.rot.y != 0.0f || fp.rot.z != 0.0f);

    float3 offset = (fp.juliaMode == 1) ? fp.juliaC.xyz : p;
    float3 z = p;
    float dr = 1.0f;

    outTrap = float4(1e20f);

    const float BAILOUT2 = 1000.0f;

    for (int i = 0; i < fp.iterations; i++) {
        if (fp.mode == 1) z = amazingFold(z, foldingLimit);
        else              z = boxFold(z, foldingLimit);

        if (useRotation) z = R * z;

        sphereFold(z, dr, minR2, fixedR2);

        z = scale * z + offset;
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
// M7b wavetable capture helpers (only called by the tile-centre thread)
// ============================================================================

// Record WVTBL orbit-trap accumulators along a ray into wt.raySteps.
// trap.x = min orbit radius seen during DE fold iterations at each march step.
// This varies throughout the whole march (not just near the hit), and captures
// the fractal geometry encountered at each spatial position along the ray.
// CPU side normalises to [-1, 1] with percentile clamping.
void captureRayWavetable(
    float3 ro, float3 rd,
    constant FoldParams& fp,
    constant TelemetryParams& tp,
    device WavetableCell& wt)
{
    float hitEps  = tp.march.x;
    float maxDist = tp.march.y;
    float fudge   = fp.rot.w;

    float tEnter;
    float t = 0.0f;
    if (intersectSphereForward(ro, rd, fp.boundSphere.xyz, fp.boundSphere.w, tEnter))
        t = max(0.0f, tEnter);

    bool  stopped  = false;
    float lastTrap = 0.0f;
    for (int i = 0; i < WVTBL; i++) {
        if (!stopped) {
            float4 trapOut;
            float d = estimateFull(ro + rd * t, fp, trapOut) * fudge;
            wt.raySteps[i] = trapOut.x;   // min orbit radius at this march position
            lastTrap = trapOut.x;
            stopped = (d < hitEps) || (t > maxDist);
            if (!stopped) t += d;
        } else {
            wt.raySteps[i] = lastTrap;
        }
    }
}

// Record WVTBL bounded orbit magnitudes into wt.orbitMags, starting from 'seed'.
// 'seed' should be an interior point (pulled inward from the surface) so the orbit
// stays bounded and oscillates for many iterations, producing varied waveform content.
// Uses length(z)/(1+length(z)) to map [0,∞)→[0,1) — a single escape sample can't
// flatten the remaining 63 in normalization.  Camera-independent: parameter animation
// morphs the waveform; camera motion does not.
void captureOrbitWavetable(
    float3 seed,
    constant FoldParams& fp,
    device WavetableCell& wt)
{
    float scale        = fp.boxParams.x;
    float foldingLimit = fp.boxParams.y;
    float minR2        = fp.boxParams.z * fp.boxParams.z;
    float fixedR2      = fp.boxParams.w * fp.boxParams.w;
    float3x3 R         = rotationFromEuler(fp.rot.xyz);
    bool useRotation   = (fp.rot.x != 0.0f || fp.rot.y != 0.0f || fp.rot.z != 0.0f);

    float3 z      = seed;
    float  dr     = 1.0f;
    float3 offset = (fp.juliaMode == 1) ? fp.juliaC.xyz : seed;

    for (int i = 0; i < WVTBL; i++) {
        if (fp.mode == 1) z = amazingFold(z, foldingLimit);
        else              z = boxFold(z, foldingLimit);
        if (useRotation) z = R * z;
        sphereFold(z, dr, minR2, fixedR2);
        z = scale * z + offset;
        dr = dr * abs(scale) + 1.0f;
        float mag = length(z);
        wt.orbitMags[i] = mag / (1.0f + mag);  // bounded in [0, 1)
    }
}

// ============================================================================
// M9a: Orbit trajectory capture — ORBTRAJ raw 3-D orbit points, one per iteration.
// Stores float4(clamp(z,-4,4), bounded): w=1 while orbit stays bounded, 0 after bailout.
// Uses float4 (not float3) to avoid 16-byte stride misalignment when read back by C#.
// Same interior seed as captureOrbitWavetable; kept in a separate buffer(5) to avoid
// breaking the WavetableCell layout that ReadOrbitWavetables depends on.
// ============================================================================

void captureOrbitTrajectory(
    float3 seed,
    constant FoldParams& fp,
    device float4* out)
{
    float scale        = fp.boxParams.x;
    float foldingLimit = fp.boxParams.y;
    float minR2        = fp.boxParams.z * fp.boxParams.z;
    float fixedR2      = fp.boxParams.w * fp.boxParams.w;
    float3x3 R         = rotationFromEuler(fp.rot.xyz);
    bool useRotation   = (fp.rot.x != 0.0f || fp.rot.y != 0.0f || fp.rot.z != 0.0f);

    const float BAILOUT2 = 1000.0f;
    float3 z      = seed;
    float  dr     = 1.0f;
    float3 offset = (fp.juliaMode == 1) ? fp.juliaC.xyz : seed;
    bool escaped  = false;

    for (int i = 0; i < ORBTRAJ; i++) {
        if (!escaped) {
            if (fp.mode == 1) z = amazingFold(z, foldingLimit);
            else              z = boxFold(z, foldingLimit);
            if (useRotation) z = R * z;
            sphereFold(z, dr, minR2, fixedR2);
            z = scale * z + offset;
            dr = dr * abs(scale) + 1.0f;
            escaped = (dot(z, z) > BAILOUT2);
        }
        if (escaped) {
            out[i] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        } else {
            out[i] = float4(clamp(z, -4.0f, 4.0f), 1.0f);
        }
    }
}

// ============================================================================
// M7h: Waveshaper strip capture — 64 DE samples along the camera-right axis.
// Samples are in [0, ∞); CPU normalises to [-1, 1] (NormalizeWavetable).
// Used as a waveshaping transfer curve: sine input → table lookup → output.
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
// Telemetry kernel — 64x36 low-res re-march, one TelemetryCell per ray.
// One WavetableCell per spatial tile (4×4=16) written by the tile-centre thread.
// Thread (0,0) captures the waveshaper strip (M7h).
// ============================================================================

kernel void mandelbox_telemetry(
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

    // M7h: one thread captures the waveshaper strip (no race — exclusive buffer)
    if (px == 0 && py == 0)
        captureWaveshaperStrip(fp, tp, waveshaper);

    int idx = py * tp.gridWidth + px;

    // Tile-centre detection for M7b wavetable capture.
    // The 64×36 grid is partitioned into a 4×4 array of spatial tiles (16×9 rays each).
    // Only the centre thread of each tile writes to wavetables[tileIdx].
    int tileW  = tp.gridWidth  / 4;   // 16
    int tileH  = tp.gridHeight / 4;   // 9
    int tileX  = px / tileW;
    int tileY  = py / tileH;
    bool isTileCenter = (px == tileX * tileW + tileW / 2) &&
                        (py == tileY * tileH + tileH / 2);
    int tileIdx = tileY * 4 + tileX;

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

    // Fallback orbit seed for tiles with no hit: half-radius along x from bounding sphere centre.
    float3 fallbackSeed = fp.boundSphere.xyz + float3(fp.boundSphere.w * 0.5f, 0.0f, 0.0f);

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

    // Tetrahedron normal estimate
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
        captureRayWavetable(ro, rd, fp, tp, wavetables[tileIdx]);
        // Pull seed inward along the surface normal so the orbit starts in the
        // interior where it stays bounded, producing oscillatory waveform content.
        float3 interiorSeed = hitPoint - normal * normEps * 8.0f;
        captureOrbitWavetable(interiorSeed, fp, wavetables[tileIdx]);
        captureOrbitTrajectory(interiorSeed, fp, orbitTraj + tileIdx * ORBTRAJ);
    }
}

// ============================================================================
// M8-spatial: 4-corner field-scan synthesis.
// 1-D dispatch of 4*WVTBL threads:
//   gid   0.. 63 = TL (xOff=-0.4*scanR, y sweeps 0..+scanR, above centre)
//   gid  64..127 = TR (xOff=+0.4*scanR, y sweeps 0..+scanR, above centre)
//   gid 128..191 = BL (xOff=-0.4*scanR, y sweeps 0..-scanR, below centre)
//   gid 192..255 = BR (xOff=+0.4*scanR, y sweeps 0..-scanR, below centre)
// TL+TR cover positive camUp (above centre, spectrally bright).
// BL+BR cover negative camUp (below centre, spectrally dark).
// CPU AC-couples and normalises each quadrant independently.
// ============================================================================
kernel void mandelbox_fieldscan(
    constant FoldParams&       fp  [[buffer(0)]],
    constant TelemetryParams&  tp  [[buffer(1)]],
    device   float*            out [[buffer(2)]],
    uint gid [[thread_position_in_grid]])
{
    if (int(gid) >= 4 * WVTBL) return;
    uint  quadrant = gid / uint(WVTBL);
    uint  localGid = gid % uint(WVTBL);
    float xSign    = (quadrant == 0 || quadrant == 2) ? -1.0f : 1.0f; // TL,BL=-1; TR,BR=+1
    float ySign    = (quadrant < 2)                   ?  1.0f : -1.0f; // TL,TR=+1; BL,BR=-1
    float scanR    = fp.boundSphere.w * 0.30f;
    float y        = (float(localGid) / float(WVTBL - 1)) * scanR * ySign;
    float3 p = tp.camPos.xyz
             + tp.camRight.xyz * (xSign * scanR * 0.40f)
             + tp.camUp.xyz    * y;
    out[gid] = estimate(p, fp);
}
