#include <metal_stdlib>
using namespace metal;

// FoldParams reuse for attractor:
//   iterations  = trajectory point count
//   boxParams   = (tubeRadius, gridSize, _, _)
//   surfParams  = (boundsMax.xyz, _)
//   juliaC      = (boundsMin.xyz, _)
//   rot.w       = DE fudge
//   boundSphere = (cx, cy, cz, r)
struct FoldParams {
    int   nPoints;
    int   mode;
    int   juliaMode;
    int   pad0;
    float4 boxParams;
    float4 surfParams;
    float4 juliaC;
    float4 rot;
    float4 boundSphere;
};

struct RenderParams {
    int   imageWidth; int imageHeight; int rowOffset; int rowCount;
    float4 camPos; float4 camForward; float4 camRight; float4 camUp; float4 tanFov;
    float4 lightDir; float4 background; float4 surface;
    float4 marchA; float4 marchB;
    int marchI0; int marchI1; int marchI2; int marchI3;
    float4 palBase; float4 palAmp; float4 palPhase; float4 trapMix;
    float4 subpixelJitter; float4 reflectParams;
};

// ============================================================================
// Attractor spatial-hash distance estimator
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

    // Fast skip if outside the cloud AABB by more than one cell.
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

// ============================================================================
// Shading helpers
// ============================================================================

float3 estimateNormal(float3 p, float eps, float3 viewDir, thread bool& degenerate,
                      constant FoldParams& fp,
                      device float4* traj, device int* hashCells, device int* sortedIdx) {
    float2 k = float2(1.0f, -1.0f);
    float3 n =
        k.xyy * estimate(p + k.xyy * eps, fp, traj, hashCells, sortedIdx) +
        k.yyx * estimate(p + k.yyx * eps, fp, traj, hashCells, sortedIdx) +
        k.yxy * estimate(p + k.yxy * eps, fp, traj, hashCells, sortedIdx) +
        k.xxx * estimate(p + k.xxx * eps, fp, traj, hashCells, sortedIdx);
    float len = length(n);
    degenerate = !(len > 1e-6f);
    return degenerate ? -viewDir : n / len;
}

float softShadow(float3 origin, float3 dir, float hitEps, float maxDist,
                 int steps, float softness,
                 constant FoldParams& fp,
                 device float4* traj, device int* hashCells, device int* sortedIdx) {
    float result = 1.0f; float t = hitEps * 4.0f;
    for (int i = 0; i < steps; i++) {
        float d = estimate(origin + dir * t, fp, traj, hashCells, sortedIdx);
        if (d < hitEps) return 0.0f;
        result = min(result, softness * d / t); t += d;
        if (t > maxDist) break;
    }
    return max(0.0f, result);
}

float ambientOcclusion(float3 p, float3 normal, float stepDist, float intensity,
                       int samples,
                       constant FoldParams& fp,
                       device float4* traj, device int* hashCells, device int* sortedIdx) {
    float occ = 0.0f; float weight = 1.0f;
    for (int i = 1; i <= samples; i++) {
        float stepLen = float(i) * stepDist;
        float d = estimate(p + normal * stepLen, fp, traj, hashCells, sortedIdx);
        occ += (stepLen - d) * weight; weight *= 0.5f;
    }
    return clamp(1.0f - intensity * max(0.0f, occ), 0.0f, 1.0f);
}

bool intersectSphereForward(float3 ro, float3 rd, float3 center, float radius, thread float& tOut) {
    float3 oc = ro - center; float b = dot(oc, rd);
    float c = dot(oc, oc) - radius * radius;
    if (c <= 0.0f) { tOut = 0.0f; return true; }
    float disc = b * b - c;
    if (disc < 0.0f) { tOut = 0.0f; return false; }
    float sq = sqrt(disc);
    float t0 = -b - sq; if (t0 >= 0.0f) { tOut = t0; return true; }
    float t1 = -b + sq; if (t1 >= 0.0f) { tOut = t1; return true; }
    tOut = 0.0f; return false;
}

float3 cosPalette(float t, float3 a, float3 b, float3 c, float3 d) {
    return a + b * cos(6.28318530718f * (c * t + d));
}

float3 trapAlbedo(float4 trap, constant RenderParams& rp) {
    float tIn = rp.trapMix.x * trap.x + rp.trapMix.y * trap.z + rp.trapMix.z * trap.y;
    float t = fract(tIn * rp.palAmp.w);
    float3 col = cosPalette(t, rp.palBase.xyz, rp.palAmp.xyz, float3(rp.palBase.w), rp.palPhase.xyz);
    float shell = 1.0f - clamp(trap.w * 2.0f, 0.0f, 1.0f);
    return mix(col, float3(0.95f, 0.93f, 0.88f), shell * rp.palPhase.w);
}

float3 envGradient(float3 rd, constant RenderParams& rp) {
    float h = clamp(rd.y * 0.5f + 0.5f, 0.0f, 1.0f);
    float3 env = mix(float3(0.22f, 0.15f, 0.10f), float3(0.08f, 0.12f, 0.20f), h);
    float sun = max(0.0f, dot(rd, rp.lightDir.xyz));
    float intensity = rp.lightDir.w <= 0.0f ? 1.0f : rp.lightDir.w;
    return env + float3(0.55f, 0.40f, 0.28f) * pow(sun, 48.0f) * intensity;
}

struct Hit { bool hit; float3 pos; float3 normal; float3 albedo; float t; };

Hit traceRay(float3 ro, float3 rd,
             float hitEps, float maxDist, float normalEps, int maxSteps,
             constant FoldParams& fp, constant RenderParams& rp,
             device float4* traj, device int* hashCells, device int* sortedIdx) {
    Hit h; h.hit = false; h.pos = float3(0); h.normal = float3(0); h.albedo = float3(0); h.t = maxDist;
    float tEnter;
    if (!intersectSphereForward(ro, rd, fp.boundSphere.xyz, fp.boundSphere.w, tEnter)) return h;
    float t = max(0.0f, tEnter);
    bool didHit = false; float fudge = fp.rot.w; int i = 0; float lastD = 1e9f;
    for (i = 0; i < maxSteps; i++) {
        float d = estimate(ro + rd * t, fp, traj, hashCells, sortedIdx) * fudge;
        float effectiveEps = max(hitEps, 0.5f * (2.0f * rp.tanFov.y / float(rp.imageHeight)) * t);
        if (d < effectiveEps) { didHit = true; break; }
        lastD = d; t += d;
        if (t > maxDist) break;
    }
    if (!didHit && i >= maxSteps && t <= maxDist && lastD < hitEps * 4.0f) didHit = true;
    if (!didHit) return h;
    float3 hitPoint = ro + rd * t;
    float4 gTrap; estimateFull(hitPoint, fp, traj, hashCells, sortedIdx, gTrap);
    float pixelWorldHit = (2.0f * rp.tanFov.y / float(rp.imageHeight)) * t;
    float nEps = max(normalEps, 0.5f * pixelWorldHit);
    bool degenerate;
    h.hit = true; h.pos = hitPoint;
    h.normal = estimateNormal(hitPoint, nEps, rd, degenerate, fp, traj, hashCells, sortedIdx);
    h.albedo = trapAlbedo(gTrap, rp); h.t = t;
    return h;
}

float3 shadeDirect(Hit h, float hitEps, float maxDist,
                   constant FoldParams& fp, constant RenderParams& rp,
                   device float4* traj, device int* hashCells, device int* sortedIdx) {
    int flags = rp.marchI3; bool softShadowsOn = (flags & 1) != 0; bool aoOn = (flags & 2) != 0;
    float3 off = h.pos + h.normal * hitEps * 4.0f;
    float lambert = clamp(dot(h.normal, rp.lightDir.xyz), 0.0f, 1.0f);
    float shadow = 1.0f;
    if (softShadowsOn && lambert > 0.0f)
        shadow = softShadow(off, rp.lightDir.xyz, hitEps, maxDist, rp.marchI1, rp.marchA.w, fp, traj, hashCells, sortedIdx);
    float ao = 1.0f;
    if (aoOn) ao = ambientOcclusion(off, h.normal, rp.marchB.x, rp.marchB.y, rp.marchI2, fp, traj, hashCells, sortedIdx);
    float intensity = rp.lightDir.w <= 0.0f ? 1.0f : rp.lightDir.w;
    float lighting = 0.25f * ao + 0.75f * lambert * shadow * ao * intensity;
    return h.albedo * clamp(lighting, 0.0f, 1.0f);
}

kernel void attractor_raymarch(
    constant FoldParams&   fp         [[buffer(0)]],
    constant RenderParams& rp         [[buffer(1)]],
    device   float4*       output     [[buffer(2)]],
    device   float4*       traj       [[buffer(3)]],
    device   int*          hashCells  [[buffer(4)]],
    device   int*          sortedIdx  [[buffer(5)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x); int py = int(gid.y);
    if (px >= rp.imageWidth || py >= rp.imageHeight) return;
    float u = (float(px) + 0.5f + rp.subpixelJitter.x) / float(rp.imageWidth);
    float v = 1.0f - (float(py) + 0.5f + rp.subpixelJitter.y) / float(rp.imageHeight);
    float x = (2.0f * u - 1.0f) * rp.tanFov.x;
    float y = (2.0f * v - 1.0f) * rp.tanFov.y;
    float3 ro = rp.camPos.xyz;
    float3 rd = normalize(rp.camForward.xyz + x * rp.camRight.xyz + y * rp.camUp.xyz);
    bool reflectOn = rp.reflectParams.x > 0.5f;
    int maxBounces = int(rp.reflectParams.y);
    float gloss = rp.reflectParams.z; float F0 = rp.reflectParams.w;
    float3 color = float3(0); float3 throughput = float3(1);
    for (int bounce = 0; bounce <= maxBounces; bounce++) {
        Hit h = traceRay(ro, rd, rp.marchA.x, rp.marchA.y, rp.marchA.z, rp.marchI0,
                         fp, rp, traj, hashCells, sortedIdx);
        if (!h.hit) { color += throughput * (bounce == 0 ? rp.background.xyz : envGradient(rd, rp)); break; }
        float3 direct = shadeDirect(h, rp.marchA.x, rp.marchA.y, fp, rp, traj, hashCells, sortedIdx);
        if (!reflectOn || bounce == maxBounces) { color += throughput * direct; break; }
        float cosTheta = clamp(dot(-rd, h.normal), 0.0f, 1.0f);
        float fresnel = F0 + (1.0f - F0) * pow(1.0f - cosTheta, 5.0f);
        float reflectWeight = clamp(gloss * fresnel, 0.0f, 1.0f);
        color += throughput * (1.0f - reflectWeight) * direct;
        throughput *= reflectWeight;
        ro = h.pos + h.normal * rp.marchA.x * 4.0f;
        rd = reflect(rd, h.normal);
        if (max(throughput.r, max(throughput.g, throughput.b)) < 0.01f) break;
    }
    int idx = py * rp.imageWidth + px;
    output[idx] = float4(color, 1.0f);
}
