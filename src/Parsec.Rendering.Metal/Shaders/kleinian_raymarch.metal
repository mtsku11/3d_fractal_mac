#include <metal_stdlib>
using namespace metal;

// ============================================================================
// Parameter structs — layout must match MetalFoldParams / MetalRenderParams
// ============================================================================

struct FoldParams {
    int   iterations;
    int   mode;       // unused
    int   juliaMode;  // unused
    int   pad0;
    float4 boxParams;    // (scale, cell, minRadius, fixedRadius)
    float4 surfParams;   // unused
    float4 juliaC;       // (offsetX, offsetY, offsetZ, _)
    float4 rot;          // (_, _, _, fudge)
    float4 boundSphere;  // (cx, cy, cz, r)
};

struct RenderParams {
    int   imageWidth;
    int   imageHeight;
    int   rowOffset;
    int   rowCount;
    float4 camPos;
    float4 camForward;
    float4 camRight;
    float4 camUp;
    float4 tanFov;
    float4 lightDir;
    float4 background;
    float4 surface;
    float4 marchA;
    float4 marchB;
    int   marchI0;
    int   marchI1;
    int   marchI2;
    int   marchI3;
    float4 palBase;
    float4 palAmp;
    float4 palPhase;
    float4 trapMix;
    float4 subpixelJitter;
    float4 reflectParams;
};

// ============================================================================
// Pseudo-Kleinian distance estimator (numerical gradient)
// ============================================================================
//
// Sphere inversion + box fold + scale + offset, iterated. The analytic
// scalar-derivative DE collapses for inversive systems, so we use the
// numerical gradient of log(|orbit_end|) instead: DE = |V| / |grad V|.
// Seven calls to kleinianPotential per estimate() call (1 center + 6 offsets).
//
// boxParams: (scale, cell, minRadius, fixedRadius)
// juliaC:    (offsetX, offsetY, offsetZ, _)
// rot.w:     fudge

float kleinianPotential(float3 p, constant FoldParams& fp) {
    float scale  = fp.boxParams.x;
    float cell   = fp.boxParams.y;
    float minR2  = fp.boxParams.z * fp.boxParams.z;
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
    const float EPS = 1e-4f;
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

    float scale  = fp.boxParams.x;
    float cell   = fp.boxParams.y;
    float minR2  = fp.boxParams.z * fp.boxParams.z;
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
// Shading helpers
// ============================================================================

float3 estimateNormal(float3 p, float eps, float3 viewDir, thread bool& degenerate,
                      constant FoldParams& fp) {
    float2 k = float2(1.0f, -1.0f);
    float3 n =
        k.xyy * estimate(p + k.xyy * eps, fp) +
        k.yyx * estimate(p + k.yyx * eps, fp) +
        k.yxy * estimate(p + k.yxy * eps, fp) +
        k.xxx * estimate(p + k.xxx * eps, fp);
    float len = length(n);
    degenerate = !(len > 1e-6f);
    return degenerate ? -viewDir : n / len;
}

float softShadow(float3 origin, float3 dir, float hitEps, float maxDist,
                 int steps, float softness, constant FoldParams& fp) {
    float result = 1.0f;
    float t = hitEps * 4.0f;
    for (int i = 0; i < steps; i++) {
        float3 p = origin + dir * t;
        float d = estimate(p, fp);
        if (d < hitEps) return 0.0f;
        result = min(result, softness * d / t);
        t += d;
        if (t > maxDist) break;
    }
    return max(0.0f, result);
}

float ambientOcclusion(float3 p, float3 normal, float stepDist, float intensity,
                       int samples, constant FoldParams& fp) {
    float occ = 0.0f;
    float weight = 1.0f;
    for (int i = 1; i <= samples; i++) {
        float stepLen = float(i) * stepDist;
        float3 samplePoint = p + normal * stepLen;
        float d = estimate(samplePoint, fp);
        occ += (stepLen - d) * weight;
        weight *= 0.5f;
    }
    return clamp(1.0f - intensity * max(0.0f, occ), 0.0f, 1.0f);
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

float3 cosPalette(float t, float3 a, float3 b, float3 c, float3 d) {
    return a + b * cos(6.28318530718f * (c * t + d));
}

float3 trapAlbedo(float4 trap, constant RenderParams& rp) {
    float tIn = rp.trapMix.x * trap.x
              + rp.trapMix.y * trap.z
              + rp.trapMix.z * trap.y;
    float t = fract(tIn * rp.palAmp.w);
    float freq = rp.palBase.w;
    float3 col = cosPalette(t, rp.palBase.xyz, rp.palAmp.xyz, float3(freq), rp.palPhase.xyz);
    float shell = 1.0f - clamp(trap.w * 2.0f, 0.0f, 1.0f);
    col = mix(col, float3(0.95f, 0.93f, 0.88f), shell * rp.palPhase.w);
    return col;
}

float3 envGradient(float3 rd, constant RenderParams& rp) {
    float h = clamp(rd.y * 0.5f + 0.5f, 0.0f, 1.0f);
    float3 env = mix(float3(0.30f, 0.22f, 0.16f), float3(0.12f, 0.16f, 0.24f), h);
    float sun = max(0.0f, dot(rd, rp.lightDir.xyz));
    float intensity = rp.lightDir.w <= 0.0f ? 1.0f : rp.lightDir.w;
    env += float3(0.60f, 0.50f, 0.40f) * pow(sun, 48.0f) * intensity;
    return env;
}

// ============================================================================
// Ray tracer
// ============================================================================

struct Hit {
    bool   hit;
    float3 pos;
    float3 normal;
    float3 albedo;
    float  t;
    float  glow;
};

Hit traceRay(float3 ro, float3 rd,
             float hitEps, float maxDist, float normalEps, int maxSteps,
             constant FoldParams& fp, constant RenderParams& rp) {
    Hit h;
    h.hit = false; h.pos = float3(0); h.normal = float3(0);
    h.albedo = float3(0); h.t = maxDist; h.glow = 0.0f;

    float tEnter;
    if (!intersectSphereForward(ro, rd, fp.boundSphere.xyz, fp.boundSphere.w, tEnter)) return h;

    float t = max(0.0f, tEnter);
    bool  didHit = false;
    float fudge  = fp.rot.w;
    int   i      = 0;
    float lastD  = 1e9f;

    float glowFalloff = max(rp.tanFov.w, 1e-4f);
    float glowAccum = 0.0f;

    for (i = 0; i < maxSteps; i++) {
        float3 p = ro + rd * t;
        float d = estimate(p, fp) * fudge;
        float pixelWorld = (2.0f * rp.tanFov.y / float(rp.imageHeight)) * t;
        float effectiveEps = max(hitEps, 0.5f * pixelWorld);
        if (d < effectiveEps) { didHit = true; break; }
        glowAccum += 1.0f / (1.0f + d * d * glowFalloff);
        lastD = d;
        t += d;
        if (t > maxDist) break;
    }
    h.glow = glowAccum / float(maxSteps);
    if (!didHit && i >= maxSteps && t <= maxDist && lastD < hitEps * 4.0f) didHit = true;
    if (!didHit) return h;

    float3 hitPoint = ro + rd * t;

    float4 gTrap;
    estimateFull(hitPoint, fp, gTrap);
    float3 albedo = trapAlbedo(gTrap, rp);

    float pixelWorldHit = (2.0f * rp.tanFov.y / float(rp.imageHeight)) * t;
    float nEps = max(normalEps, 0.5f * pixelWorldHit);
    bool degenerate;
    float3 normal = estimateNormal(hitPoint, nEps, rd, degenerate, fp);

    h.hit = true; h.pos = hitPoint; h.normal = normal; h.albedo = albedo; h.t = t;
    return h;
}

float3 shadeDirect(Hit h, float hitEps, float maxDist,
                   constant FoldParams& fp, constant RenderParams& rp) {
    float shadowSoft  = rp.marchA.w;
    float aoStep      = rp.marchB.x;
    float aoIntensity = rp.marchB.y;
    int   shadowSteps = rp.marchI1;
    int   aoSamples   = rp.marchI2;
    int   flags       = rp.marchI3;
    bool  softShadowsOn = (flags & 1) != 0;
    bool  aoOn          = (flags & 2) != 0;

    float3 offsetPoint = h.pos + h.normal * hitEps * 4.0f;
    float lambert = clamp(dot(h.normal, rp.lightDir.xyz), 0.0f, 1.0f);

    float shadow = 1.0f;
    if (softShadowsOn && lambert > 0.0f)
        shadow = softShadow(offsetPoint, rp.lightDir.xyz, hitEps, maxDist,
                            shadowSteps, shadowSoft, fp);

    float ao = 1.0f;
    if (aoOn)
        ao = ambientOcclusion(offsetPoint, h.normal, aoStep, aoIntensity, aoSamples, fp);

    float intensity = rp.lightDir.w <= 0.0f ? 1.0f : rp.lightDir.w;
    const float ambient = 0.25f;
    float lighting = ambient * ao + (1.0f - ambient) * lambert * shadow * ao * intensity;
    lighting = clamp(lighting, 0.0f, 1.0f);
    return h.albedo * lighting;
}

// ============================================================================
// Kernel entry point
// ============================================================================

kernel void kleinian_raymarch(
    constant FoldParams&   fp     [[buffer(0)]],
    constant RenderParams& rp     [[buffer(1)]],
    device   float4*       output [[buffer(2)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= rp.imageWidth || py >= rp.imageHeight) return;

    float hitEps    = rp.marchA.x;
    float maxDist   = rp.marchA.y;
    float normalEps = rp.marchA.z;
    int   maxSteps  = rp.marchI0;

    float u = (float(px) + 0.5f + rp.subpixelJitter.x) / float(rp.imageWidth);
    float v = 1.0f - (float(py) + 0.5f + rp.subpixelJitter.y) / float(rp.imageHeight);
    float x = (2.0f * u - 1.0f) * rp.tanFov.x;
    float y = (2.0f * v - 1.0f) * rp.tanFov.y;
    float3 ro = rp.camPos.xyz;
    float3 rd = normalize(rp.camForward.xyz + x * rp.camRight.xyz + y * rp.camUp.xyz);

    bool  reflectOn  = rp.reflectParams.x > 0.5f;
    int   maxBounces = int(rp.reflectParams.y);
    float gloss      = rp.reflectParams.z;
    float F0         = rp.reflectParams.w;

    float3 color      = float3(0);
    float3 throughput = float3(1);
    float  glowTotal  = 0.0f;

    for (int bounce = 0; bounce <= maxBounces; bounce++) {
        Hit h = traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp);
        glowTotal += dot(throughput, float3(0.3333f)) * h.glow;

        if (!h.hit) {
            color += throughput * (bounce == 0 ? rp.background.xyz : envGradient(rd, rp));
            break;
        }

        float3 direct = shadeDirect(h, hitEps, maxDist, fp, rp);

        if (!reflectOn || bounce == maxBounces) {
            color += throughput * direct;
            break;
        }

        float cosTheta = clamp(dot(-rd, h.normal), 0.0f, 1.0f);
        float fresnel = F0 + (1.0f - F0) * pow(1.0f - cosTheta, 5.0f);
        float reflectWeight = clamp(gloss * fresnel, 0.0f, 1.0f);

        color += throughput * (1.0f - reflectWeight) * direct;
        throughput *= reflectWeight;

        ro = h.pos + h.normal * hitEps * 4.0f;
        rd = reflect(rd, h.normal);

        if (max(throughput.r, max(throughput.g, throughput.b)) < 0.01f) break;
    }

    float  glowStrength = rp.tanFov.z;
    float3 glowColor    = clamp(rp.palBase.xyz + rp.palAmp.xyz, 0.0f, 2.0f);
    color += glowTotal * glowStrength * glowColor;

    int idx = py * rp.imageWidth + px;
    output[idx] = float4(color, 1.0f);
}
