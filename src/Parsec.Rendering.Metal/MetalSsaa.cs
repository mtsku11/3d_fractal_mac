using System.Numerics;

namespace Parsec.Rendering.Metal;

/// <summary>
/// CPU-side supersampling accumulator for Metal renderers.
/// Dispatches N Halton-jittered single-sample renders and averages the results.
/// Apple Silicon unified memory makes readback essentially free, so N GPU round-trips
/// is the correct approach (no need for a dedicated finalize kernel).
/// </summary>
internal static class MetalSsaa
{
    /// <summary>
    /// Render <paramref name="sampleCount"/> Halton-jittered samples via
    /// <paramref name="renderOneSample"/> (which returns a float4-per-pixel HDR buffer,
    /// stride 4: R,G,B,A), accumulate as float, and return the averaged float4 buffer.
    /// When sampleCount == 1 the lambda is called once with Vector2.Zero and returned as-is.
    /// The caller is responsible for applying the post-process pass on the result.
    /// </summary>
    internal static float[] AccumulateHdr(
        int sampleCount,
        int width,
        int height,
        Func<Vector2, float[]> renderOneSample)
    {
        int n = Math.Max(1, sampleCount);
        int pixelCount = width * height;

        if (n == 1)
            return renderOneSample(Vector2.Zero);

        var accumR = new float[pixelCount];
        var accumG = new float[pixelCount];
        var accumB = new float[pixelCount];

        for (int s = 0; s < n; s++)
        {
            var jitter = HaltonJitter(s);
            var pixels = renderOneSample(jitter);
            for (int i = 0; i < pixelCount; i++)
            {
                accumR[i] += pixels[i * 4 + 0];
                accumG[i] += pixels[i * 4 + 1];
                accumB[i] += pixels[i * 4 + 2];
            }
        }

        float invN = 1f / n;
        var result = new float[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            result[i * 4 + 0] = accumR[i] * invN;
            result[i * 4 + 1] = accumG[i] * invN;
            result[i * 4 + 2] = accumB[i] * invN;
            result[i * 4 + 3] = 1f;
        }
        return result;
    }

    /// <summary>
    /// RGBA8 variant used only by <see cref="MetalDeepZoomRenderer"/>, which packs
    /// RGBA8 inside its kernel and does not go through the HDR post-process path.
    /// </summary>
    internal static uint[] Accumulate(
        int sampleCount,
        int width,
        int height,
        Func<Vector2, uint[]> renderOneSample)
    {
        int n = Math.Max(1, sampleCount);
        int count = width * height;

        if (n == 1)
            return renderOneSample(Vector2.Zero);

        var accumR = new float[count];
        var accumG = new float[count];
        var accumB = new float[count];

        for (int s = 0; s < n; s++)
        {
            var jitter = HaltonJitter(s);
            var pixels = renderOneSample(jitter);
            for (int i = 0; i < count; i++)
            {
                uint p = pixels[i];
                accumR[i] += p & 0xFFu;
                accumG[i] += (p >> 8) & 0xFFu;
                accumB[i] += (p >> 16) & 0xFFu;
            }
        }

        float invN = 1f / n;
        var result = new uint[count];
        for (int i = 0; i < count; i++)
        {
            uint r = Math.Min(255u, (uint)(accumR[i] * invN + 0.5f));
            uint g = Math.Min(255u, (uint)(accumG[i] * invN + 0.5f));
            uint b = Math.Min(255u, (uint)(accumB[i] * invN + 0.5f));
            result[i] = (255u << 24) | (b << 16) | (g << 8) | r;
        }
        return result;
    }

    /// <summary>Halton(2,3) sub-pixel jitter. Returns offset in [-0.5, 0.5]².</summary>
    internal static Vector2 HaltonJitter(int sampleIndex)
    {
        int n = sampleIndex + 1;
        return new Vector2(Halton(n, 2) - 0.5f, Halton(n, 3) - 0.5f);
    }

    private static float Halton(int index, int b)
    {
        float f = 1f, result = 0f;
        while (index > 0)
        {
            f /= b;
            result += f * (index % b);
            index /= b;
        }
        return result;
    }
}
