namespace Parsec.Rendering.Metal;

internal static class MetalSurfaceTextureShaderInjector
{
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
            return src;

        // Capture trapUv alongside gTrap at the hit point.
        src = src.Replace(
            "float4 gTrap; estimateFull(hitPoint, fp, gTrap);",
            "float4 gTrap; float2 trapUv; estimateFull(hitPoint, fp, gTrap, trapUv);");

        // traceRay signature — same anchor as Inject(), uniquely identifies traceRay.
        src = src.Replace(
            "int maxSteps,\n             constant FoldParams& fp, constant RenderParams& rp)",
            "int maxSteps,\n             constant FoldParams& fp, constant RenderParams& rp, texture2d<float> surfaceTexture)");

        // traceRay call site in the kernel.
        src = src.Replace(
            "traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp)",
            "traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps, fp, rp, surfaceTexture)");

        // Texture binding in the kernel signature.
        src = src.Replace(
            "output [[buffer(2)]],",
            "output [[buffer(2)]],\n    texture2d<float>    surfaceTexture [[texture(0)]],");

        // Albedo assignment: pass trap UV when in orbit-trap mode, world hit point otherwise.
        // The ternary runs on the GPU — zero overhead when triplanar mode is active.
        src = src.Replace(
            "h.albedo = albedo;",
            "h.albedo = applySurfaceTexture(albedo, rp.background.w >= 1.5f ? float3(trapUv.x, trapUv.y, 0.0f) : hitPoint, normal, rp, surfaceTexture);");

        int envIndex = src.IndexOf("float3 envGradient(", StringComparison.Ordinal);
        if (envIndex >= 0)
            src = src.Insert(envIndex, OrbitTrapHelpers);

        return src;
    }
}
