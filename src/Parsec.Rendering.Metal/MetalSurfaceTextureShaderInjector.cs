namespace Parsec.Rendering.Metal;

internal static class MetalSurfaceTextureShaderInjector
{
    private const string DomainWarpHelpers = """
float3 domainWarp(float3 p, constant RenderParams& rp) {
    float strength = max(rp.subpixelJitter.z, 0.0f);
    if (strength <= 0.0f) return p;

    float scale = max(rp.subpixelJitter.w, 1e-4f);
    float t = rp.trapMix.w;   // slow phase accumulator packed by DomainWarpState.GetPhase()

    // A POINT-SOURCE ripple, like a stone dropped into a pond: concentric shells emanate from the
    // "front mouth" at the +X end of the body and expand outward across the skin toward the back. The
    // wave argument keys off DISTANCE from the source (not x), so crests are spherical shells whose
    // radius = (t + 2pi*n)/scale grows as t advances -- new rings are continuously born at the mouth
    // and travel front -> back. A wobble perturbs the shells so the rings read as irregular (and the
    // irregular surface breaks them up further). Displacement is radial (a ring lifts a band of skin as
    // it passes); amplitude falls off with distance so the wave clearly emerges from the mouth and
    // dissipates toward the back. The source is locked to the geometry, so it rides the bulb as the
    // camera orbits. strength==0 above keeps this a no-op.
    float3 src  = float3(1.15f, 0.0f, 0.0f);          // front mouth: +X end of the bulb
    float3 d    = p - src;
    float  dist = length(d);
    float  wobble = 0.12f * sin(d.y * 3.1f + t * 0.2f) * sin(d.z * 2.7f - t * 0.15f);
    float  arg  = (dist + wobble) * scale - t;
    float  falloff = 1.0f / (1.0f + 0.7f * dist);     // strongest at the mouth, fades toward the back
    float3 radial = normalize(p + float3(1e-5f));
    float3 ripple = radial * sin(arg) * falloff;

    // A faint, slow organic shimmer so the skin isn't a perfectly regular set of rings.
    float3 q = p * scale * 0.5f;
    float3 organic = float3(
        sin(q.y + sin(q.z * 1.37f) + t * 0.25f),
        sin(q.z + sin(q.x * 1.21f) + t * 0.21f),
        sin(q.x + sin(q.y * 1.11f) + t * 0.29f));

    return p + strength * (0.85f * ripple + 0.15f * organic);
}

""";

    // Helpers for orbit-trap + triplanar combined injection (BurningShip only for now).
    // background.w: 0 = disabled, 1 = triplanar, 2 = orbit trap.
    // posOrTrapUv: world hit point in triplanar mode; orbit-trap UV packed in .xy in orbit mode.
    private const string OrbitTrapHelpers = """
float2 surfaceTextureAspectUv(float2 uv, float aspect) {
    if (aspect > 1.0f) return float2(uv.x, uv.y / aspect);
    if (aspect > 0.0f && aspect < 1.0f) return float2(uv.x * aspect, uv.y);
    return uv;
}

float3 sampleSurfaceTexture(float3 pos, float3 normal,
                            constant RenderParams& rp,
                            texture2d<float> surfaceTexture) {
    constexpr sampler surfaceSampler(coord::normalized, address::repeat, filter::linear);
    float scale = max(rp.marchB.z, 1e-4f);
    float aspect = max(rp.marchB.w, 1e-4f);
    float3 weights = pow(abs(normal), float3(4.0f));
    weights /= max(weights.x + weights.y + weights.z, 1e-5f);
    float2 uvX = surfaceTextureAspectUv(pos.yz * scale, aspect);
    float2 uvY = surfaceTextureAspectUv(pos.xz * scale, aspect);
    float2 uvZ = surfaceTextureAspectUv(pos.xy * scale, aspect);
    float3 tx = surfaceTexture.sample(surfaceSampler, fract(uvX)).xyz;
    float3 ty = surfaceTexture.sample(surfaceSampler, fract(uvY)).xyz;
    float3 tz = surfaceTexture.sample(surfaceSampler, fract(uvZ)).xyz;
    return tx * weights.x + ty * weights.y + tz * weights.z;
}

float3 applySurfaceTexture(float3 baseAlbedo, float3 posOrTrapUv, float3 normal,
                           constant RenderParams& rp,
                           texture2d<float> surfaceTexture) {
    if (rp.background.w < 0.5f) return baseAlbedo;
    float blend = clamp(rp.surface.w, 0.0f, 1.0f);
    if (blend <= 0.0f) return baseAlbedo;
    float3 sampled;
    if (rp.background.w >= 1.5f) {
        // Orbit trap mode: posOrTrapUv.xy is the pre-computed trap UV (normalised by bailout).
        constexpr sampler s(coord::normalized, address::repeat, filter::linear);
        float scale = max(rp.marchB.z, 1e-4f);
        sampled = surfaceTexture.sample(s, fract(posOrTrapUv.xy * scale)).xyz;
    } else {
        // Triplanar mode: posOrTrapUv is world hit point.
        sampled = sampleSurfaceTexture(posOrTrapUv, normal, rp, surfaceTexture);
    }
    return mix(baseAlbedo, sampled, blend);
}

""";

    private const string Helpers = """
float2 surfaceTextureAspectUv(float2 uv, float aspect) {
    if (aspect > 1.0f) return float2(uv.x, uv.y / aspect);
    if (aspect > 0.0f && aspect < 1.0f) return float2(uv.x * aspect, uv.y);
    return uv;
}

float3 sampleSurfaceTexture(float3 pos, float3 normal,
                            constant RenderParams& rp,
                            texture2d<float> surfaceTexture) {
    constexpr sampler surfaceSampler(coord::normalized, address::repeat, filter::linear);
    float scale = max(rp.marchB.z, 1e-4f);
    float aspect = max(rp.marchB.w, 1e-4f);
    float3 weights = pow(abs(normal), float3(4.0f));
    weights /= max(weights.x + weights.y + weights.z, 1e-5f);

    float2 uvX = surfaceTextureAspectUv(pos.yz * scale, aspect);
    float2 uvY = surfaceTextureAspectUv(pos.xz * scale, aspect);
    float2 uvZ = surfaceTextureAspectUv(pos.xy * scale, aspect);

    float3 tx = surfaceTexture.sample(surfaceSampler, fract(uvX)).xyz;
    float3 ty = surfaceTexture.sample(surfaceSampler, fract(uvY)).xyz;
    float3 tz = surfaceTexture.sample(surfaceSampler, fract(uvZ)).xyz;
    return tx * weights.x + ty * weights.y + tz * weights.z;
}

float3 applySurfaceTexture(float3 baseAlbedo, float3 pos, float3 normal,
                           constant RenderParams& rp,
                           texture2d<float> surfaceTexture) {
    if (rp.background.w < 0.5f) return baseAlbedo;
    float blend = clamp(rp.surface.w, 0.0f, 1.0f);
    if (blend <= 0.0f) return baseAlbedo;
    float3 sampled = sampleSurfaceTexture(pos, normal, rp, surfaceTexture);
    return mix(baseAlbedo, sampled, blend);
}

""";

    public static string Inject(string src)
    {
        src = InjectDomainWarp(src);

        if (src.Contains("applySurfaceTexture("))
            return src;

        // Append the texture param to traceRay ONLY. The previous broad match on
        // "...FoldParams& fp, constant RenderParams& rp)" also hit shadeDirect (which
        // shares that suffix), making it a 6-arg function still called with 5 args →
        // "no matching function for call to 'shadeDirect'" and the whole library failed
        // to compile. traceRay is uniquely identified by its "int maxSteps," parameter;
        // it is the only function that sets h.albedo, so it is the only one needing the texture.
        src = src.Replace(
            "int maxSteps,\n             constant FoldParams& fp, constant RenderParams& rp)",
            "int maxSteps,\n             constant FoldParams& fp, constant RenderParams& rp, texture2d<float> surfaceTexture)");

        src = src.Replace(
            "traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp)",
            "traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp, surfaceTexture)");

        // menger_raymarch.metal is the one shader that calls traceRay with the RenderParams
        // fields inline instead of the hitEps/maxDist/... locals, so the standard call-site
        // patch above misses it. Patch its specific call form too.
        src = src.Replace(
            "traceRay(ro, rd, rp.marchA.x, rp.marchA.y, rp.marchA.z, rp.marchI0, fp, rp)",
            "traceRay(ro, rd, rp.marchA.x, rp.marchA.y, rp.marchA.z, rp.marchI0, fp, rp, surfaceTexture)");

        src = src.Replace(
            "output [[buffer(2)]],",
            "output [[buffer(2)]],\n    texture2d<float>    surfaceTexture [[texture(0)]],");

        src = src.Replace(
            "h.albedo = albedo;",
            "h.albedo = applySurfaceTexture(albedo, hitPoint, normal, rp, surfaceTexture);");

        src = src.Replace(
            "h.albedo = trapAlbedo(gTrap, rp);",
            "h.albedo = applySurfaceTexture(trapAlbedo(gTrap, rp), hitPoint, h.normal, rp, surfaceTexture);");

        int envIndex = src.IndexOf("float3 envGradient(", StringComparison.Ordinal);
        if (envIndex >= 0)
            src = src.Insert(envIndex, Helpers);

        return src;
    }

    // Orbit-trap injection for BurningShip (and any future shader that populates outTrapUv
    // in estimateFull). Handles both triplanar and orbit-trap modes in one applySurfaceTexture
    // call; the mode is selected at runtime via background.w (1 = triplanar, 2 = orbit trap).
    // The main Inject() idempotently no-ops after this since applySurfaceTexture( is present.
    public static string InjectOrbitTrap(string src)
    {
        if (src.Contains("applySurfaceTexture("))
            return InjectDomainWarp(src);

        // Capture trapUv alongside gTrap at the hit point.
        // Inline form (Menger, BurningShip): "float4 gTrap; estimateFull(hitPoint, fp, gTrap);"
        src = src.Replace(
            "float4 gTrap; estimateFull(hitPoint, fp, gTrap);",
            "float4 gTrap; float2 trapUv; estimateFull(hitPoint, fp, gTrap, trapUv);");
        // Split form (Mandelbox, Mandelbulb, KIFS): gTrap declaration on its own line.
        src = src.Replace(
            "    float4 gTrap;\n    estimateFull(hitPoint, fp, gTrap);",
            "    float4 gTrap; float2 trapUv; estimateFull(hitPoint, fp, gTrap, trapUv);");

        // traceRay signature — same anchor as Inject(), uniquely identifies traceRay.
        src = src.Replace(
            "int maxSteps,\n             constant FoldParams& fp, constant RenderParams& rp)",
            "int maxSteps,\n             constant FoldParams& fp, constant RenderParams& rp, texture2d<float> surfaceTexture)");

        // traceRay call site in the kernel (local-variable form).
        src = src.Replace(
            "traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp)",
            "traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp, surfaceTexture)");

        // menger_raymarch.metal calls traceRay with inline RenderParams fields rather than
        // local variables, so the standard call-site patch above misses it.
        src = src.Replace(
            "traceRay(ro, rd, rp.marchA.x, rp.marchA.y, rp.marchA.z, rp.marchI0, fp, rp)",
            "traceRay(ro, rd, rp.marchA.x, rp.marchA.y, rp.marchA.z, rp.marchI0, fp, rp, surfaceTexture)");

        // Texture binding in the kernel signature.
        src = src.Replace(
            "output [[buffer(2)]],",
            "output [[buffer(2)]],\n    texture2d<float>    surfaceTexture [[texture(0)]],");

        // Albedo assignment — two forms depending on which shader pattern is in use.
        // Form 1: albedo via local variable (Mandelbox, Mandelbulb, KIFS, BurningShip).
        src = src.Replace(
            "h.albedo = albedo;",
            "h.albedo = applySurfaceTexture(albedo, rp.background.w >= 1.5f ? float3(trapUv.x, trapUv.y, 0.0f) : hitPoint, normal, rp, surfaceTexture);");
        // Form 2: albedo via trapAlbedo inline (Menger). Uses h.normal (set on the line above).
        src = src.Replace(
            "h.albedo = trapAlbedo(gTrap, rp);",
            "h.albedo = applySurfaceTexture(trapAlbedo(gTrap, rp), rp.background.w >= 1.5f ? float3(trapUv.x, trapUv.y, 0.0f) : hitPoint, h.normal, rp, surfaceTexture);");

        int envIndex = src.IndexOf("float3 envGradient(", StringComparison.Ordinal);
        if (envIndex >= 0)
            src = src.Insert(envIndex, OrbitTrapHelpers);

        return InjectDomainWarp(src);
    }

    private static string InjectDomainWarp(string src)
    {
        if (src.Contains("domainWarp("))
            return src;

        // Insert helper before estimateNormal definition
        int normalIndex = src.IndexOf("float3 estimateNormal(", StringComparison.Ordinal);
        if (normalIndex >= 0)
            src = src.Insert(normalIndex, DomainWarpHelpers);

        // --- Primary march ---
        src = src.Replace(
            "float d = estimate(p, fp) * fudge;",
            "float d = estimate(domainWarp(p, rp), fp) * fudge;");
        src = src.Replace(
            "float d = estimate(pt, fp) * fudge;",
            "float d = estimate(domainWarp(pt, rp), fp) * fudge;");
        src = src.Replace(
            "float d = estimate(ro + rd * t, fp) * fudge;",
            "float d = estimate(domainWarp(ro + rd * t, rp), fp) * fudge;");

        // --- estimateFull at hit point ---
        src = src.Replace(
            "estimateFull(hitPoint, fp, gTrap);",
            "estimateFull(domainWarp(hitPoint, rp), fp, gTrap);");
        src = src.Replace(
            "estimateFull(hitPoint, fp, gTrap, trapUv);",
            "estimateFull(domainWarp(hitPoint, rp), fp, gTrap, trapUv);");

        // --- estimateNormal: add rp so it can warp each stencil point ---
        src = src.Replace(
            "float3 estimateNormal(float3 p, float eps, float3 viewDir, thread bool& degenerate,\n                      constant FoldParams& fp) {",
            "float3 estimateNormal(float3 p, float eps, float3 viewDir, thread bool& degenerate,\n                      constant FoldParams& fp, constant RenderParams& rp) {");
        src = src.Replace(
            "        k.xyy * estimate(p + k.xyy * eps, fp) +\n        k.yyx * estimate(p + k.yyx * eps, fp) +\n        k.yxy * estimate(p + k.yxy * eps, fp) +\n        k.xxx * estimate(p + k.xxx * eps, fp);",
            "        k.xyy * estimate(domainWarp(p + k.xyy * eps, rp), fp) +\n        k.yyx * estimate(domainWarp(p + k.yyx * eps, rp), fp) +\n        k.yxy * estimate(domainWarp(p + k.yxy * eps, rp), fp) +\n        k.xxx * estimate(domainWarp(p + k.xxx * eps, rp), fp);");
        // Patch call site — just add rp, do NOT pre-warp hitPoint (warping happens per-stencil inside)
        src = src.Replace(
            "estimateNormal(hitPoint, nEps, rd, degenerate, fp)",
            "estimateNormal(hitPoint, nEps, rd, degenerate, fp, rp)");

        // --- softShadow: add rp and warp each march step ---
        src = src.Replace(
            "float softShadow(float3 origin, float3 dir, float hitEps, float maxDist,\n                 int steps, float softness, constant FoldParams& fp) {",
            "float softShadow(float3 origin, float3 dir, float hitEps, float maxDist,\n                 int steps, float softness, constant FoldParams& fp, constant RenderParams& rp) {");
        src = src.Replace(
            "        float d = estimate(p, fp);\n        if (d < hitEps) return 0.0f;",
            "        float d = estimate(domainWarp(p, rp), fp);\n        if (d < hitEps) return 0.0f;");
        // Call-site patches keyed on unique argument endings (covers all shader variants)
        src = src.Replace("shadowSteps, shadowSoft, fp);", "shadowSteps, shadowSoft, fp, rp);");
        src = src.Replace("rp.marchA.w, fp);", "rp.marchA.w, fp, rp);");  // Menger inline args

        // --- ambientOcclusion: add rp and warp each sample point ---
        src = src.Replace(
            "float ambientOcclusion(float3 p, float3 normal, float stepDist, float intensity,\n                       int samples, constant FoldParams& fp) {",
            "float ambientOcclusion(float3 p, float3 normal, float stepDist, float intensity,\n                       int samples, constant FoldParams& fp, constant RenderParams& rp) {");
        src = src.Replace(
            "        float d = estimate(samplePoint, fp);",
            "        float d = estimate(domainWarp(samplePoint, rp), fp);");
        // Call-site patches keyed on unique argument endings
        src = src.Replace("aoSamples, fp);", "aoSamples, fp, rp);");
        src = src.Replace("rp.marchI2, fp);", "rp.marchI2, fp, rp);");  // Menger inline args

        return src;
    }
}
