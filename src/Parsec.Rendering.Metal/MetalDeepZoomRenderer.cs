using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Rendering.DeepZoom;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Raymarching;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Parsec.Rendering.Metal;

// Params struct uploaded to buffer 0.  Must match DeepZoomParams in deepzoom_metal.metal.
// Pack=1 so that Vector4 fields land at their natural byte offsets (96, 112, 128, 144)
// without the runtime inserting padding — all four Vector4s sit at 16-byte-aligned
// positions naturally because the preceding fields total 96 bytes.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DeepZoomMetalParams
{
    public int Width, Height, RowOffset, RowCount;
    public int RefCount, MaxIter, Formula, DirectMode;
    public float RefDcReHi, RefDcReLo, RefDcImHi, RefDcImLo;
    public float SpacingHi, SpacingLo, JitterX, JitterY;
    public float KappaRe, KappaIm, EscapeR2, Pad0;
    public float CdReHi, CdReLo, CdImHi, CdImLo;
    public Vector4 PalBase;     // (r,g,b) = cosine offset a; .w = frequency
    public Vector4 PalAmp;      // (r,g,b) = amplitude b
    public Vector4 PalPhase;    // (r,g,b) = phase d
    public Vector4 Bg;
    public float PalScale, Pad1, Pad2, Pad3;
}

/// <summary>
/// Metal compute backend for the 2D deep-zoom pipeline. Replaces
/// <c>DeepZoomPipeline</c> on macOS (which requires OpenGL 4.3 + fp64).
///
/// Precision: float-float (Dekker) arithmetic in the MSL shader gives ~48
/// mantissa bits ≈ 14 decimal digits — sufficient for zoom depths to ~1e-12.
///
/// Reference orbit management mirrors DeepZoomPipeline.EnsureReference: the
/// CPU computes one high-precision orbit per unique (center, formula, kappa)
/// combination; the GPU kernel uses it as the perturbation base for every pixel.
/// </summary>
public sealed class MetalDeepZoomRenderer : IDisposable
{
    private MTLDevice   _device;
    private MTLCommandQueue _queue;
    private MTLComputePipelineState _pso;
    private bool _isAvailable;
    private bool _disposed;

    // Reference orbit cache (mirrors DeepZoomPipeline.EnsureReference).
    private ReferenceOrbit? _ref;
    private string _refRe = "";
    private string _refIm = "";
    private double _refRadius = double.NaN;
    private int    _refP      = -1;
    private int    _refFormula = -1;
    private double _refKappaRe = double.NaN;
    private double _refKappaIm = double.NaN;

    // While interacting, reuse the settled reference orbit until the view
    // centre has drifted past this fraction of the cached radius (~45-bit margin).
    private const double ReuseRadiusFloor = 7.3e-12;

    public bool IsAvailable => _isAvailable;
    public long LastComputeMs  { get; private set; }
    public long LastReadbackMs { get; private set; }

    public MetalDeepZoomRenderer()
    {
        try
        {
            _device = MTLDevice.CreateSystemDefaultDevice();
            _queue  = _device.NewCommandQueue();

            string src = LoadEmbeddedMsl("deepzoom_metal.metal");
            NSError err = default;
            var lib = _device.NewLibrary(NSString.String(src), new MTLCompileOptions(), ref err);
            if (lib.NativePtr == IntPtr.Zero)
                throw new InvalidOperationException($"MSL compile failed: {err.LocalizedDescription}");

            var fn = lib.NewFunction(NSString.String("deepzoom_raymarch"));
            if (fn.NativePtr == IntPtr.Zero)
                throw new InvalidOperationException("deepzoom_raymarch function not found in compiled library");

            NSError psoErr = default;
            _pso = _device.NewComputePipelineState(fn, ref psoErr);
            if (_pso.NativePtr == IntPtr.Zero)
                throw new InvalidOperationException($"PSO creation failed: {psoErr.LocalizedDescription}");

            _isAvailable = true;
        }
        catch
        {
            _isAvailable = false;
        }
    }

    /// <summary>
    /// Render a deep-zoom frame. Returns packed RGBA8 pixels (same contract as
    /// all other Metal renderers). Hero SSAA is handled by <see cref="MetalSsaa"/>.
    /// </summary>
    public uint[] Render(
        DeepZoomView view, int width, int height,
        PaletteParams palette, Color background,
        RaymarchSettings settings,
        bool interactive = false, int interactiveIter = 0)
    {
        ThrowIfDisposed();
        if (!_isAvailable)
            throw new InvalidOperationException("Metal deep-zoom backend unavailable.");

        int effIter = view.IterationsForDepth();
        EnsureReference(view, interactive, effIter);

        int shaderIter = (interactive && interactiveIter > 0)
            ? Math.Clamp(interactiveIter, 1, effIter)
            : effIter;

        bool direct  = view.UseDirectPath;
        int  P       = view.PrecisionBits();
        var (refDcRe, refDcIm) = BinaryFixed.OffsetToDouble(
            _refRe, _refIm, view.CenterRe, view.CenterIm, P);

        double spacing = view.SpacingFor(height);
        double cdRe    = double.Parse(view.CenterRe,
            System.Globalization.CultureInfo.InvariantCulture);
        double cdIm    = double.Parse(view.CenterIm,
            System.Globalization.CultureInfo.InvariantCulture);

        // For hero SSAA, the kernel is called once per sample with a different
        // jitter; MetalSsaa.Accumulate manages the per-sample loop and averaging.
        return MetalSsaa.Accumulate(settings.HeroSamples, width, height, jitter =>
            RenderOneSample(view, width, height, palette, background,
                shaderIter, direct, refDcRe, refDcIm, spacing, cdRe, cdIm,
                jitter.X, jitter.Y));
    }

    private unsafe uint[] RenderOneSample(
        DeepZoomView view, int width, int height,
        PaletteParams palette, Color background,
        int maxIter, bool direct,
        double refDcRe, double refDcIm,
        double spacing, double cdRe, double cdIm,
        float jitterX, float jitterY)
    {
        int pixelCount = width * height;
        var computeSw  = System.Diagnostics.Stopwatch.StartNew();

        // Upload reference orbit as float4 per point (re_hi, re_lo, im_hi, im_lo).
        int refCount = _ref!.Count;
        int refWords = refCount * 4;
        using var refBuf = _device.NewBuffer(
            (ulong)(refWords * sizeof(float)),
            MTLResourceOptions.ResourceStorageModeShared);
        var refSpan = new Span<float>((float*)refBuf.Contents, refWords);
        for (int i = 0; i < refCount; i++)
        {
            (float reHi, float reLo) = SplitToFF(_ref.Re[i]);
            (float imHi, float imLo) = SplitToFF(_ref.Im[i]);
            refSpan[i * 4 + 0] = reHi;
            refSpan[i * 4 + 1] = reLo;
            refSpan[i * 4 + 2] = imHi;
            refSpan[i * 4 + 3] = imLo;
        }

        // Upload output buffer.
        using var outBuf = _device.NewBuffer(
            (ulong)(pixelCount * sizeof(uint)),
            MTLResourceOptions.ResourceStorageModeShared);

        // Upload params.
        var p = BuildParams(view, width, height, 0, height, refCount, maxIter,
            direct, refDcRe, refDcIm, spacing, cdRe, cdIm,
            jitterX + 0.5f, jitterY + 0.5f, palette, background);
        using var paramBuf = UploadStruct(_device, p);

        // Dispatch.
        var cmd = _queue.CommandBuffer();
        var enc = cmd.ComputeCommandEncoder();
        enc.SetComputePipelineState(_pso);
        enc.SetBuffer(paramBuf, 0, 0);
        enc.SetBuffer(refBuf,   0, 1);
        enc.SetBuffer(outBuf,   0, 2);

        var tgSize  = new MTLSize { width = 8, height = 8, depth = 1 };
        var gridSize = new MTLSize
        {
            width  = (ulong)((width  + 7) / 8),
            height = (ulong)((height + 7) / 8),
            depth  = 1
        };
        enc.DispatchThreadgroups(gridSize, tgSize);
        enc.EndEncoding();
        cmd.Commit();
        cmd.WaitUntilCompleted();

        computeSw.Stop();
        LastComputeMs = computeSw.ElapsedMilliseconds;

        var readSw = System.Diagnostics.Stopwatch.StartNew();
        var result = new uint[pixelCount];
        var src = new ReadOnlySpan<uint>((uint*)outBuf.Contents, pixelCount);
        src.CopyTo(result);
        readSw.Stop();
        LastReadbackMs = readSw.ElapsedMilliseconds;

        return result;
    }

    private static DeepZoomMetalParams BuildParams(
        DeepZoomView view,
        int width, int height, int rowOffset, int rowCount,
        int refCount, int maxIter,
        bool direct,
        double refDcRe, double refDcIm,
        double spacing, double cdRe, double cdIm,
        float jitterX, float jitterY,
        PaletteParams palette, Color background)
    {
        (float rDcReHi, float rDcReLo) = SplitToFF(refDcRe);
        (float rDcImHi, float rDcImLo) = SplitToFF(refDcIm);
        (float spHi,    float spLo)    = SplitToFF(spacing);
        (float cdReHi,  float cdReLo)  = SplitToFF(cdRe);
        (float cdImHi,  float cdImLo)  = SplitToFF(cdIm);

        float escapeR2 = view.Formula == 1 ? 1.0e6f : 4.0f;

        return new DeepZoomMetalParams
        {
            Width = width, Height = height,
            RowOffset = rowOffset, RowCount = rowCount,
            RefCount = refCount, MaxIter = maxIter,
            Formula = view.Formula,
            DirectMode = direct ? 1 : 0,
            RefDcReHi = rDcReHi, RefDcReLo = rDcReLo,
            RefDcImHi = rDcImHi, RefDcImLo = rDcImLo,
            SpacingHi = spHi, SpacingLo = spLo,
            JitterX = jitterX, JitterY = jitterY,
            KappaRe = (float)view.KappaRe, KappaIm = (float)view.KappaIm,
            EscapeR2 = escapeR2, Pad0 = 0f,
            CdReHi = cdReHi, CdReLo = cdReLo,
            CdImHi = cdImHi, CdImLo = cdImLo,
            PalBase  = new Vector4(palette.Base,  palette.Frequency),
            PalAmp   = new Vector4(palette.Amp,   palette.TrapScale),
            PalPhase = new Vector4(palette.Phase, palette.ShellMix),
            Bg       = new Vector4(background.R, background.G, background.B, 1f),
            PalScale = 0.0125f,
            Pad1 = 0f, Pad2 = 0f, Pad3 = 0f,
        };
    }

    // Dekker split: decomposes a double into two floats whose sum equals the
    // double exactly (within float rounding).  Preserves all 53 mantissa bits.
    private static (float hi, float lo) SplitToFF(double d)
    {
        float hi = (float)d;
        float lo = (float)(d - (double)hi);
        return (hi, lo);
    }

    private void EnsureReference(DeepZoomView view, bool interactive, int iterations)
    {
        // During interaction, skip expensive bignum recompute and reuse the
        // settled reference.  It remains a valid perturbation base until the
        // centre drifts further than ReuseRadiusFloor × _refRadius from the
        // cached position.
        if (interactive && _ref != null
            && _refFormula == view.Formula
            && _refKappaRe == view.KappaRe && _refKappaIm == view.KappaIm
            && view.Radius >= _refRadius * ReuseRadiusFloor)
        {
            return;
        }

        int P    = view.PrecisionBits();
        bool need = _ref == null
            || view.Radius  != _refRadius
            || P            != _refP
            || view.Formula != _refFormula
            || _refKappaRe  != view.KappaRe
            || _refKappaIm  != view.KappaIm;

        if (!need)
        {
            var (dx, dy) = BinaryFixed.OffsetToDouble(
                _refRe, _refIm, view.CenterRe, view.CenterIm, P);
            if (Math.Sqrt(dx * dx + dy * dy) > view.Radius)
                need = true;
        }

        if (need)
        {
            _ref = ReferenceOrbit.Compute(
                view.CenterRe, view.CenterIm, P, iterations,
                view.Formula, view.KappaRe, view.KappaIm);
            _refRe      = view.CenterRe;
            _refIm      = view.CenterIm;
            _refRadius  = view.Radius;
            _refP       = P;
            _refFormula = view.Formula;
            _refKappaRe = view.KappaRe;
            _refKappaIm = view.KappaIm;
        }
    }

    private static unsafe MTLBuffer UploadStruct<T>(MTLDevice device, T value)
        where T : unmanaged
    {
        var buf = device.NewBuffer(
            (ulong)sizeof(T), MTLResourceOptions.ResourceStorageModeShared);
        *(T*)buf.Contents = value;
        return buf;
    }

    private static string LoadEmbeddedMsl(string name)
    {
        var asm  = typeof(MetalDeepZoomRenderer).Assembly;
        var rns  = asm.GetManifestResourceNames();
        var full = Array.Find(rns, n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                          $"Embedded resource '{name}' not found. Available: {string.Join(", ", rns)}");
        using var stream = asm.GetManifestResourceStream(full)!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetalDeepZoomRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
