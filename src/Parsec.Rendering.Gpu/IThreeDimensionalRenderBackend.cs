using System.Numerics;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// Backend-neutral contract for rendering a single 3D fractal to a packed RGBA8 buffer.
/// Implementations: <see cref="GpuMandelboxRenderer"/> (OpenGL), MetalMandelboxRenderer (Metal, stub).
/// </summary>
public interface IThreeDimensionalRenderBackend : IDisposable
{
    /// <summary>True when the backend can be used at runtime on this machine.</summary>
    bool IsAvailable { get; }

    uint[] RenderMandelbox(
        MandelboxParams fractal,
        Camera3D camera,
        int width,
        int height,
        RaymarchSettings settings,
        Color background,
        Color surface,
        Vector3 lightDirection,
        PaletteParams palette);
}
