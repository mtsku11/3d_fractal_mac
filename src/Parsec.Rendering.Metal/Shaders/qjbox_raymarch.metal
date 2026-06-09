#include <metal_stdlib>
using namespace metal;

struct FoldParams {
    int iterations; int mode; int juliaMode; int pad0;
    float4 boxParams;  // (scale, minRadius, fixedRadius, foldLimit)
    float4 surfParams; // (rotX, rotY, rotZ, _)
    float4 juliaC;     // quaternion constant c
    float4 rot;        // (wslice, planeOffset, cutAxisFlag, fudge)
    float4 boundSphere;
};
struct RenderParams {
    int imageWidth; int imageHeight; int rowOffset; int rowCount;
    float4 camPos; float4 camForward; float4 camRight; float4 camUp; float4 tanFov;
    float4 lightDir; float4 background; float4 surface;
    float4 marchA; float4 marchB;
    int marchI0; int marchI1; int marchI2; int marchI3;
    float4 palBase; float4 palAmp; float4 palPhase; float4 trapMix;
    float4 subpixelJitter; float4 reflectParams;
};

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
        float d = estimate(origin + dir * t, fp);
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
        float d = estimate(p + normal * stepLen, fp);
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
    float3 col = cosPalette(t, rp.palBase.xyz, rp.palAmp.xyz, float3(rp.palBase.w), rp.palPhase.xyz);
    float shell = 1.0f - clamp(trap.w * 2.0f, 0.0f, 1.0f);
    return mix(col, float3(0.95f, 0.93f, 0.88f), shell * rp.palPhase.w);
}

float3 envGradient(float3 rd, constant RenderParams& rp) {
    float h = clamp(rd.y * 0.5f + 0.5f, 0.0f, 1.0f);
    float3 env = mix(float3(0.30f, 0.22f, 0.16f), float3(0.12f, 0.16f, 0.24f), h);
    float sun = max(0.0f, dot(rd, rp.lightDir.xyz));
    float intensity = rp.lightDir.w <= 0.0f ? 1.0f : rp.lightDir.w;
    env += float3(0.60f, 0.50f, 0.40f) * pow(sun, 48.0f) * intensity;
    return env;
}

struct Hit {
    bool   hit;
    float3 pos;
    float3 normal;
    float3 albedo;
    float  t;
};

Hit traceRay(float3 ro, float3 rd,
             float hitEps, float maxDist, float normalEps, int maxSteps,
             constant FoldParams& fp, constant RenderParams& rp) {
    Hit h;
    h.hit = false; h.pos = float3(0); h.normal = float3(0);
    h.albedo = float3(0); h.t = maxDist;

    float tEnter;
    if (!intersectSphereForward(ro, rd, fp.boundSphere.xyz, fp.boundSphere.w, tEnter)) return h;

    float t = max(0.0f, tEnter);
    bool  didHit = false;
    float fudge  = fp.rot.w;
    int   i      = 0;
    float lastD  = 1e9f;

    for (i = 0; i < maxSteps; i++) {
        float3 pt = ro + rd * t;
        float d = estimate(pt, fp) * fudge;
        float pixelWorld = (2.0f * rp.tanFov.y / float(rp.imageHeight)) * t;
        float effectiveEps = max(hitEps, 0.5f * pixelWorld);
        if (d < effectiveEps) { didHit = true; break; }
        lastD = d;
        t += d;
        if (t > maxDist) break;
    }
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
    return h.albedo * clamp(lighting, 0.0f, 1.0f);
}

kernel void qjbox_raymarch(
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

    for (int bounce = 0; bounce <= maxBounces; bounce++) {
        Hit h = traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp);

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

    int idx = py * rp.imageWidth + px;
    output[idx] = float4(color, 1.0f);
}
