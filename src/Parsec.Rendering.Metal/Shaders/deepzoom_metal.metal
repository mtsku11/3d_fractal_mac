#include <metal_stdlib>
using namespace metal;

// ============================================================================
// Deep-zoom 2D escape-time — Metal compute kernel.
//
// Uses float-float (Dekker double-double) arithmetic for ~48-bit precision
// (~14 decimal digits), supporting zoom depths down to ~1e-12 on Apple Silicon.
//
// Two render paths, selected by params.directMode:
//   1 (direct)       — iterate each pixel's orbit directly using float-float
//                      coordinates. Correct for all formulas at shallow zoom
//                      (radius > ~1e-6). The only reliable path for Burning
//                      Ship at wide views (perturbation is unstable when the
//                      delta is large).
//   0 (perturbation) — reference orbit (precomputed on CPU, split to float-float
//                      pairs) plus a float-float delta dz, with rebasing when
//                      dz grows larger than z. Used for deep zoom.
//
// Formulas (params.formula):
//   0  Mandelbrot     z' = z^2 + c                   (parameter plane, seed 0)
//   1  Prospector     X'=Cx+0.25XY, Y'=Cy-3X^2+0.25Y^2   (real 2D, seed 0)
//   2  Julia          z' = z^2 + kappa               (dynamical plane, seed=pixel)
//   3  Burning Ship   x'=x^2-y^2+Cx, y'=2|xy|+Cy    (parameter plane, seed 0)
//
// Reference orbit buffer (binding 1): float4 per point = (re_hi, re_lo, im_hi, im_lo).
// Each double Zref[n] is split on the C# side as:
//   hi = (float)d;  lo = (float)(d - (double)hi);
// which preserves all 53 mantissa bits across the two floats.
//
// Output buffer (binding 2): one uint per pixel, packed RGBA8 little-endian
// (alpha=0xFF high byte, B byte 2, G byte 1, R byte 0 — matches SKColorType.Rgba8888).
// ============================================================================

// ---------------------------------------------------------------------------
// Parameter struct — layout must match DeepZoomMetalParams in C# (Pack = 1).
// float4 palette fields start at offset 96 (16-byte aligned).
// ---------------------------------------------------------------------------

struct DeepZoomParams {
    int   width;
    int   height;
    int   rowOffset;
    int   rowCount;
    int   refCount;
    int   maxIter;
    int   formula;        // 0 Mandelbrot, 1 Prospector, 2 Julia, 3 BurningShip
    int   directMode;     // 1 = direct float-float; 0 = perturbation
    float refDcReHi;
    float refDcReLo;
    float refDcImHi;
    float refDcImLo;
    float spacingHi;
    float spacingLo;
    float jitterX;
    float jitterY;
    float kappaRe;
    float kappaIm;
    float escapeR2;
    float pad0;
    float cdReHi;
    float cdReLo;
    float cdImHi;
    float cdImLo;
    float4 palBase;       // (r,g,b) = cosine-palette offset a; .w = frequency
    float4 palAmp;        // (r,g,b) = amplitude b
    float4 palPhase;      // (r,g,b) = phase d
    float4 bg;            // in-set / background colour
    float palScale;
    float pad1;
    float pad2;
    float pad3;
};

// ---------------------------------------------------------------------------
// Float-float (Dekker double-double) arithmetic.
// Representation: float2 where .x = hi, .y = lo; value = hi + lo.
// Precision: ~48 mantissa bits ≈ 14 decimal digits.
//
// All helpers are written in expanded multi-line form per MSL compiler requirements.
// No global const variables are declared (MSL gotcha: global const causes silent
// shader compilation failure).
// ---------------------------------------------------------------------------

float2 ddTwoSum(float a, float b) {
    float s = a + b;
    float av = s - b;
    float bv = s - av;
    float da = a - av;
    float db = b - bv;
    return float2(s, da + db);
}

float2 ddFastTwoSum(float a, float b) {
    float s = a + b;
    float e = b - (s - a);
    return float2(s, e);
}

float2 ddTwoProduct(float a, float b) {
    float p = a * b;
    float e = fma(a, b, -p);
    return float2(p, e);
}

float2 ddAdd(float2 a, float2 b) {
    float2 s = ddTwoSum(a.x, b.x);
    float e = s.y + a.y + b.y;
    return ddFastTwoSum(s.x, e);
}

float2 ddSub(float2 a, float2 b) {
    return ddAdd(a, float2(-b.x, -b.y));
}

float2 ddMul(float2 a, float2 b) {
    float2 p = ddTwoProduct(a.x, b.x);
    float e = p.y + fma(a.x, b.y, a.y * b.x);
    return ddFastTwoSum(p.x, e);
}

float2 ddSqr(float2 a) {
    float2 p = ddTwoProduct(a.x, a.x);
    float e = p.y + 2.0f * a.x * a.y;
    return ddFastTwoSum(p.x, e);
}

// Multiply float-float by a plain float scalar.
float2 ddScale(float2 a, float s) {
    float2 p = ddTwoProduct(a.x, s);
    float e = p.y + a.y * s;
    return ddFastTwoSum(p.x, e);
}

float ddToF(float2 a) {
    return a.x + a.y;
}

float2 ddFromF(float a) {
    return float2(a, 0.0f);
}

float2 ddFromHiLo(float hi, float lo) {
    return float2(hi, lo);
}

// Approximate |re|^2 + |im|^2 as float (for escape check; escapeR2 is O(1)).
float ddMag2(float2 re, float2 im) {
    float r = ddToF(re);
    float i = ddToF(im);
    return r * r + i * i;
}

// True when re_a^2 + im_a^2 < re_b^2 + im_b^2.
// Float approximation is sufficient for the rebase trigger.
bool ddSqLess(float2 re_a, float2 im_a, float2 re_b, float2 im_b) {
    float ra = ddToF(re_a);
    float ia = ddToF(im_a);
    float rb = ddToF(re_b);
    float ib = ddToF(im_b);
    return (ra * ra + ia * ia) < (rb * rb + ib * ib);
}

// Absolute value of a float-float number.
float2 ddAbs(float2 a) {
    if (a.x < 0.0f) {
        return float2(-a.x, -a.y);
    }
    if (a.x == 0.0f) {
        return float2(0.0f, abs(a.y));
    }
    return a;
}

// Burning Ship helper: exact |c_ff + d| - |c_ff| where c_ff is the reference
// orbit value as float-float (O(1)) and d is the float-float delta.
// Sign determined from c_ff.x (hi part is sign-correct for O(1) doubles).
float2 ddDiffabs(float2 c_ff, float2 d) {
    float2 cpd = ddAdd(c_ff, d);
    float s = ddToF(cpd);
    float c = c_ff.x;
    if (c >= 0.0f) {
        if (s >= 0.0f) {
            return d;
        } else {
            return ddSub(ddScale(d, -1.0f), ddScale(c_ff, 2.0f));
        }
    } else {
        if (s > 0.0f) {
            return ddAdd(ddScale(c_ff, 2.0f), d);
        } else {
            return ddScale(d, -1.0f);
        }
    }
}

// ---------------------------------------------------------------------------
// Smooth iteration count (same formula as the OpenGL pipeline).
// Returns -1 when the orbit did not escape (in-set).
// ---------------------------------------------------------------------------

float smoothMu(int esc, int maxIter, float z2) {
    if (esc >= maxIter) {
        return -1.0f;
    }
    float lz = log(sqrt(z2));
    return (float)esc + 1.0f - log2(max(lz, 1.0e-20f));
}

// ---------------------------------------------------------------------------
// Cosine palette — identical to the OpenGL deep-zoom color pass.
// t in [0,1], a/b/d are colour vectors, c is the frequency scalar.
// ---------------------------------------------------------------------------

float3 cosPalette(float t, float3 a, float3 b, float c, float3 d) {
    return a + b * cos(6.28318530718f * (c * t + d));
}

// ---------------------------------------------------------------------------
// Pack a float RGB colour to RGBA8 (little-endian: R=byte0 A=byte3).
// Matches SKColorType.Rgba8888 used by the rest of the Metal backend.
// ---------------------------------------------------------------------------

uint packRGBA8(float3 col) {
    uint r = (uint)clamp(col.x * 255.0f + 0.5f, 0.0f, 255.0f);
    uint g = (uint)clamp(col.y * 255.0f + 0.5f, 0.0f, 255.0f);
    uint b = (uint)clamp(col.z * 255.0f + 0.5f, 0.0f, 255.0f);
    return (255u << 24) | (b << 16) | (g << 8) | r;
}

// ---------------------------------------------------------------------------
// Main kernel
// ---------------------------------------------------------------------------

kernel void deepzoom_raymarch(
    constant DeepZoomParams& params [[buffer(0)]],
    const device float4*     refOrbit [[buffer(1)]],
    device uint*             outPixels [[buffer(2)]],
    uint2 gid [[thread_position_in_grid]])
{
    uint gx = gid.x;
    uint gy = gid.y;
    if ((int)gx >= params.width || (int)gy >= params.rowCount) {
        return;
    }
    int py = params.rowOffset + (int)gy;
    if (py >= params.height) {
        return;
    }
    int outIdx = py * params.width + (int)gx;

    // Pixel offset from view centre (in complex-units).
    // Y axis is inverted: positive screen-Y = negative imaginary axis.
    float fx = (float)gx - 0.5f * (float)params.width  + params.jitterX;
    float fy = (float)py - 0.5f * (float)params.height + params.jitterY;

    float2 spacing = ddFromHiLo(params.spacingHi, params.spacingLo);

    int   esc    = params.maxIter;
    float z2     = 0.0f;
    int   maxIter = params.maxIter;
    int   formula = params.formula;

    // -----------------------------------------------------------------------
    // Direct path: iterate each pixel's own orbit in float-float.
    // Pixel coordinate c = center + (fx, -fy) * spacing.
    // -----------------------------------------------------------------------
    if (params.directMode == 1) {
        float2 cdRe = ddFromHiLo(params.cdReHi, params.cdReLo);
        float2 cdIm = ddFromHiLo(params.cdImHi, params.cdImLo);
        float2 cx = ddAdd(cdRe, ddScale(spacing, fx));
        float2 cy = ddSub(cdIm, ddScale(spacing, fy));

        if (formula == 2) {
            // Julia: seed = pixel coordinate, constant = kappa.
            float2 kRe = ddFromF(params.kappaRe);
            float2 kIm = ddFromF(params.kappaIm);
            float2 zx  = cx;
            float2 zy  = cy;
            for (int n = 0; n < maxIter; n++) {
                float m2 = ddMag2(zx, zy);
                if (m2 > params.escapeR2) {
                    esc = n;
                    z2  = m2;
                    break;
                }
                float2 nx = ddAdd(ddSub(ddSqr(zx), ddSqr(zy)), kRe);
                float2 ny = ddAdd(ddScale(ddMul(zx, zy), 2.0f), kIm);
                zx = nx;
                zy = ny;
            }
        } else if (formula == 1) {
            // Prospector: X' = Cx + 0.25*X*Y,  Y' = Cy - 3*X^2 + 0.25*Y^2.
            float2 X = ddFromF(0.0f);
            float2 Y = ddFromF(0.0f);
            for (int n = 0; n < maxIter; n++) {
                float m2 = ddMag2(X, Y);
                if (m2 > params.escapeR2) {
                    esc = n;
                    z2  = m2;
                    break;
                }
                float2 nX = ddAdd(cx, ddScale(ddMul(X, Y), 0.25f));
                float2 nY = ddAdd(cy, ddAdd(ddScale(ddSqr(X), -3.0f),
                                            ddScale(ddSqr(Y), 0.25f)));
                X = nX;
                Y = nY;
            }
        } else if (formula == 3) {
            // Burning Ship: x' = x^2 - y^2 + Cx,  y' = 2|xy| + Cy.
            float2 x = ddFromF(0.0f);
            float2 y = ddFromF(0.0f);
            for (int n = 0; n < maxIter; n++) {
                float m2 = ddMag2(x, y);
                if (m2 > params.escapeR2) {
                    esc = n;
                    z2  = m2;
                    break;
                }
                float2 nx  = ddAdd(ddSub(ddSqr(x), ddSqr(y)), cx);
                float2 ny  = ddAdd(ddScale(ddAbs(ddMul(x, y)), 2.0f), cy);
                x = nx;
                y = ny;
            }
        } else {
            // Mandelbrot: z' = z^2 + c.
            float2 zx = ddFromF(0.0f);
            float2 zy = ddFromF(0.0f);
            for (int n = 0; n < maxIter; n++) {
                float m2 = ddMag2(zx, zy);
                if (m2 > params.escapeR2) {
                    esc = n;
                    z2  = m2;
                    break;
                }
                float2 nx = ddAdd(ddSub(ddSqr(zx), ddSqr(zy)), cx);
                float2 ny = ddAdd(ddScale(ddMul(zx, zy), 2.0f), cy);
                zx = nx;
                zy = ny;
            }
        }

    // -----------------------------------------------------------------------
    // Perturbation path: reference orbit + float-float delta dz, with rebase.
    // dc = refDc + pixelOff  (offset from reference to this pixel's coordinate).
    // Julia seeds dz with dc; parameter-plane formulas start dz at 0 and add
    // dc each step.  Rebasing subtracts Zref[0] (Julia-correct; no-op for
    // seed-0 formulas where Zref[0] == 0).
    // -----------------------------------------------------------------------
    } else {
        float4 ref0Buf = refOrbit[0];
        float2 ref0Re  = ddFromHiLo(ref0Buf.x, ref0Buf.y);
        float2 ref0Im  = ddFromHiLo(ref0Buf.z, ref0Buf.w);

        float2 refDcRe = ddFromHiLo(params.refDcReHi, params.refDcReLo);
        float2 refDcIm = ddFromHiLo(params.refDcImHi, params.refDcImLo);
        float2 dcRe    = ddAdd(refDcRe, ddScale(spacing, fx));
        float2 dcIm    = ddSub(refDcIm, ddScale(spacing, fy));

        bool julia     = (formula == 2);
        float2 dzRe    = julia ? dcRe : ddFromF(0.0f);
        float2 dzIm    = julia ? dcIm : ddFromF(0.0f);
        float2 stepRe  = julia ? ddFromF(0.0f) : dcRe;
        float2 stepIm  = julia ? ddFromF(0.0f) : dcIm;

        int m = 0;

        for (int n = 0; n < maxIter; n++) {
            float4 zrBuf = refOrbit[m];
            float2 ZrRe  = ddFromHiLo(zrBuf.x, zrBuf.y);
            float2 ZrIm  = ddFromHiLo(zrBuf.z, zrBuf.w);

            float2 zRe = ddAdd(ZrRe, dzRe);
            float2 zIm = ddAdd(ZrIm, dzIm);

            float az2 = ddMag2(zRe, zIm);
            if (az2 > params.escapeR2) {
                esc = n;
                z2  = az2;
                break;
            }

            // Rebase when |z|^2 < |dz|^2 (dz has grown too large) or
            // reference orbit is exhausted.
            if (ddSqLess(zRe, zIm, dzRe, dzIm) || m >= params.refCount - 1) {
                dzRe = ddSub(zRe, ref0Re);
                dzIm = ddSub(zIm, ref0Im);
                m    = 0;
                ZrRe = ref0Re;
                ZrIm = ref0Im;
            }

            if (formula == 0 || formula == 2) {
                // Mandelbrot / Julia: dz' = 2*Zr*dz + dz^2 + stepDc
                float2 t2ZrDzRe = ddSub(
                    ddScale(ddMul(ZrRe, dzRe), 2.0f),
                    ddScale(ddMul(ZrIm, dzIm), 2.0f));
                float2 t2ZrDzIm = ddAdd(
                    ddScale(ddMul(ZrRe, dzIm), 2.0f),
                    ddScale(ddMul(ZrIm, dzRe), 2.0f));
                float2 dz2Re = ddSub(ddSqr(dzRe), ddSqr(dzIm));
                float2 dz2Im = ddScale(ddMul(dzRe, dzIm), 2.0f);
                dzRe = ddAdd(ddAdd(t2ZrDzRe, dz2Re), stepRe);
                dzIm = ddAdd(ddAdd(t2ZrDzIm, dz2Im), stepIm);

            } else if (formula == 1) {
                // Prospector real 2D map:
                // ndx = stepDc.x + 0.25*(Zr.y*dz.x + Zr.x*dz.y + dz.x*dz.y)
                // ndy = stepDc.y - 3*(2*Zr.x*dz.x + dz.x^2)
                //                + 0.25*(2*Zr.y*dz.y + dz.y^2)
                float ZX = ddToF(ZrRe);
                float ZY = ddToF(ZrIm);
                float2 sumXY = ddAdd(
                    ddAdd(ddScale(dzRe, ZY), ddScale(dzIm, ZX)),
                    ddMul(dzRe, dzIm));
                float2 ndx = ddAdd(stepRe, ddScale(sumXY, 0.25f));
                float2 termA = ddScale(
                    ddAdd(ddScale(dzRe, 2.0f * ZX), ddSqr(dzRe)), -3.0f);
                float2 termB = ddScale(
                    ddAdd(ddScale(dzIm, 2.0f * ZY), ddSqr(dzIm)), 0.25f);
                float2 ndy = ddAdd(stepIm, ddAdd(termA, termB));
                dzRe = ndx;
                dzIm = ndy;

            } else {
                // Burning Ship:
                // da = diffabs(Zr.x, dz.x),  db = diffabs(Zr.y, dz.y)
                // ndx = 2*Zr.x*dz.x - 2*Zr.y*dz.y + dz.x^2 - dz.y^2 + stepDc.x
                // ndy = 2*|Zr.y|*da + 2*|Zr.x|*db + 2*da*db + stepDc.y
                float2 da  = ddDiffabs(ZrRe, dzRe);
                float2 db  = ddDiffabs(ZrIm, dzIm);
                float  ZX  = ddToF(ZrRe);
                float  ZY  = ddToF(ZrIm);
                float2 ndx = ddAdd(
                    ddAdd(
                        ddSub(ddScale(dzRe, 2.0f * ZX), ddScale(dzIm, 2.0f * ZY)),
                        ddSub(ddSqr(dzRe), ddSqr(dzIm))),
                    stepRe);
                float2 ndy = ddAdd(
                    ddAdd(
                        ddAdd(ddScale(da, 2.0f * abs(ZY)),
                              ddScale(db, 2.0f * abs(ZX))),
                        ddScale(ddMul(da, db), 2.0f)),
                    stepIm);
                dzRe = ndx;
                dzIm = ndy;
            }

            m++;
        }
    }

    // -----------------------------------------------------------------------
    // Colour mapping: cosine palette or in-set background.
    // -----------------------------------------------------------------------
    float mu = smoothMu(esc, maxIter, z2);
    float3 col;
    if (mu < 0.0f) {
        col = params.bg.rgb;
    } else {
        float t = fract(mu * params.palScale);
        col = cosPalette(t,
            params.palBase.rgb,
            params.palAmp.rgb,
            params.palBase.w,
            params.palPhase.rgb);
    }
    outPixels[outIdx] = packRGBA8(col);
}
