using System.Numerics;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Raymarching;

namespace Parsec.Rendering.Metal;

/// <summary>
/// Stub Metal backend. IsAvailable is false until the compute kernel and
/// SharpMetal wiring are implemented (Milestone 3).
/// </summary>
public sealed class MetalMandelboxRenderer : IThreeDimensionalRenderBackend
{
    public bool IsAvailable => false;

    public uint[] RenderMandelbox(
        MandelboxParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette)
        => throw new NotSupportedException("Metal backend not yet implemented.");

    public void Dispose() { }
}
