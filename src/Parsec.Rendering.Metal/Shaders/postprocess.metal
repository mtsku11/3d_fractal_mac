#include <metal_stdlib>
using namespace metal;

// ============================================================================
// HDR grade pass — clean-room port of Mandelbulber2's CalculatePixel grade order
// (buddhi1980/mandelbulber2, GPL-3.0 design inspiration only, no code copied).
//
// Grade order (Mandelbulber2 cimage.cpp CalculatePixel):
//   0. add screen-space bloom (blurred bright-pass) before grading, so it tonemaps
//   1. brightness multiply
//   2. contrast pivot at 0.5, clamp >= 0
//   3. optional tanh tone map (maps HDR > 1 back to [0,1] with smooth rolloff)
//   4. Rec.601 perceptual saturation
//   5. clamp [0, 1]
//   6. gamma
//
// Bloom is a separate multi-pass producing a blurred bright buffer; the grade
// kernel adds it. When bloom is disabled the caller binds the HDR buffer itself
// as the bloom input with bloomIntensity = 0, so the add is a no-op (identity).
// ============================================================================

struct PostProcessParams {
    int   imageWidth;
    int   imageHeight;
    float brightness;
    float contrast;
    float gamma;
    float saturation;
    int   hdrEnabled;
    float bloomIntensity;   // 0 = no bloom added (identity); replaces former pad lane
};

kernel void postprocess(
    device   float4*             hdr    [[buffer(0)]],
    constant PostProcessParams&  pp     [[buffer(1)]],
    device   uint*               output [[buffer(2)]],
    device   float4*             bloom  [[buffer(3)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= pp.imageWidth || py >= pp.imageHeight) return;

    int idx = py * pp.imageWidth + px;
    float3 c = hdr[idx].rgb;

    // 0. add bloom (blurred bright-pass) in linear HDR before grading
    c += bloom[idx].rgb * pp.bloomIntensity;

    // 1. brightness
    c *= pp.brightness;

    // 2. contrast (pivot at mid-grey 0.5)
    c = (c - 0.5f) * pp.contrast + 0.5f;
    c = max(c, float3(0.0f));

    // 3. optional tanh tone map (HDR rolloff)
    if (pp.hdrEnabled != 0) c = tanh(c);

    // 4. Rec.601 perceptual saturation: V = sqrt(R²·0.299 + G²·0.587 + B²·0.114)
    float V = sqrt(c.r*c.r*0.299f + c.g*c.g*0.587f + c.b*c.b*0.114f);
    c = V + (c - V) * pp.saturation;

    // 5. clamp
    c = clamp(c, float3(0.0f), float3(1.0f));

    // 6. gamma  (guard against divide-by-zero; gamma clamped >= 0.01 on C# side)
    c = pow(c, 1.0f / pp.gamma);

    uint3 q = uint3(c * 255.0f + 0.5f);
    output[idx] = (255u << 24) | (q.b << 16) | (q.g << 8) | q.r;
}

// ============================================================================
// Bloom passes
// ============================================================================

struct BloomParams {
    int   imageWidth;
    int   imageHeight;
    float threshold;   // luma above which a pixel contributes to bloom
    float dirX;        // blur direction (1,0) horizontal then (0,1) vertical
    float dirY;
    int   radius;      // gaussian half-width in pixels
    float sigma;
    int   pad0;
};

// Bright-pass: keep only the over-threshold energy, preserving hue.
kernel void bloom_brightpass(
    device   float4*       hdr    [[buffer(0)]],
    constant BloomParams&  bp     [[buffer(1)]],
    device   float4*       outBuf [[buffer(2)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= bp.imageWidth || py >= bp.imageHeight) return;
    int idx = py * bp.imageWidth + px;

    float3 c = hdr[idx].rgb;
    float luma = dot(c, float3(0.2126f, 0.7152f, 0.0722f));
    float knee = max(luma - bp.threshold, 0.0f);
    float factor = knee / max(luma, 1e-4f);
    outBuf[idx] = float4(c * factor, 1.0f);
}

// Separable gaussian blur. Run once with dir=(1,0) then once with dir=(0,1).
kernel void bloom_blur(
    device   float4*       inBuf  [[buffer(0)]],
    constant BloomParams&  bp     [[buffer(1)]],
    device   float4*       outBuf [[buffer(2)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= bp.imageWidth || py >= bp.imageHeight) return;
    int idx = py * bp.imageWidth + px;

    float invTwoSigmaSq = 1.0f / (2.0f * bp.sigma * bp.sigma);
    float3 sum = float3(0.0f);
    float  wSum = 0.0f;
    for (int i = -bp.radius; i <= bp.radius; i++) {
        int sx = clamp(px + int(bp.dirX) * i, 0, bp.imageWidth  - 1);
        int sy = clamp(py + int(bp.dirY) * i, 0, bp.imageHeight - 1);
        float w = exp(-float(i * i) * invTwoSigmaSq);
        sum  += inBuf[sy * bp.imageWidth + sx].rgb * w;
        wSum += w;
    }
    outBuf[idx] = float4(sum / max(wSum, 1e-4f), 1.0f);
}
