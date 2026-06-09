#include <metal_stdlib>
using namespace metal;

// ============================================================================
// HDR grade pass — clean-room port of Mandelbulber2's CalculatePixel grade order
// (buddhi1980/mandelbulber2, GPL-3.0 design inspiration only, no code copied).
//
// Grade order (Mandelbulber2 cimage.cpp CalculatePixel):
//   1. brightness multiply
//   2. contrast pivot at 0.5, clamp >= 0
//   3. optional tanh tone map (maps HDR > 1 back to [0,1] with smooth rolloff)
//   4. Rec.601 perceptual saturation
//   5. clamp [0, 1]
//   6. gamma
// ============================================================================

struct PostProcessParams {
    int   imageWidth;
    int   imageHeight;
    float brightness;
    float contrast;
    float gamma;
    float saturation;
    int   hdrEnabled;
    int   pad0;
};

kernel void postprocess(
    device   float4*             hdr    [[buffer(0)]],
    constant PostProcessParams&  pp     [[buffer(1)]],
    device   uint*               output [[buffer(2)]],
    uint2 gid [[thread_position_in_grid]])
{
    int px = int(gid.x);
    int py = int(gid.y);
    if (px >= pp.imageWidth || py >= pp.imageHeight) return;

    int idx = py * pp.imageWidth + px;
    float3 c = hdr[idx].rgb;

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
