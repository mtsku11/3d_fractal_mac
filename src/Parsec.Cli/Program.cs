using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Cli.Examples;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Metal;
using Parsec.Rendering.Output;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var examples = new IExample[]
        {
            new DiamondExample(),
            new DiamondConstructionExample(),
            new CarpetExample(),
            new TriangleExample(),
            new Sanity3DExample(),
            new TetrahedronExample(),
            new TrefoilExample(),
            new TwistedTetrahedronExample(),
        };

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(examples);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] is "list" or "--list")
        {
            foreach (var ex in examples)
                Console.WriteLine($"  {ex.Name,-24} {ex.Description}");
            return 0;
        }

        if (args[0] is "all")
        {
            int failures = 0;
            foreach (var ex in examples)
                if (!RunExample(ex)) failures++;
            return failures == 0 ? 0 : 1;
        }

        if (args[0] is "gpu-smoke")
        {
            try
            {
                Parsec.Rendering.Gpu.SmokeTest.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU smoke test FAILED: {ex.Message}");
                if (ex.StackTrace is not null)
                    Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "gpu-de-validate")
        {
            try
            {
                return Parsec.Rendering.Gpu.DeValidation.Run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU DE validation FAILED: {ex.Message}");
                if (ex.StackTrace is not null)
                    Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "attractor-stats")
        {
            // Stage A check: integrate the Thomas attractor (canonical by default,
            // or enhanced with flags) and print point count + bounds, so the C#
            // integrator can be sanity-checked against the Python/Unity numbers.
            //   parsec attractor-stats [steps] [enhanced]
            try
            {
                int steps = args.Length > 1 ? int.Parse(args[1]) : 60_000;
                bool enhanced = args.Length > 2 && args[2] is "enhanced" or "true" or "1";

                var p = new Parsec.Core.Attractors.AttractorParams
                {
                    NumSteps = steps,
                    UseParameterDrift = enhanced,
                    UsePhaseModulation = enhanced,
                    UseNonlinearCoupling = enhanced,
                    UseMultiSeed = enhanced,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pts = Parsec.Core.Attractors.ThomasAttractor.Generate(p);
                sw.Stop();

                var lo = new System.Numerics.Vector3(float.MaxValue);
                var hi = new System.Numerics.Vector3(float.MinValue);
                foreach (var tp in pts)
                {
                    lo = System.Numerics.Vector3.Min(lo, tp.Position);
                    hi = System.Numerics.Vector3.Max(hi, tp.Position);
                }
                Console.WriteLine($"Thomas attractor ({(enhanced ? "enhanced" : "canonical")}): "
                    + $"{pts.Count} points in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"  bounds x[{lo.X:F2},{hi.X:F2}] y[{lo.Y:F2},{hi.Y:F2}] z[{lo.Z:F2},{hi.Z:F2}]");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"attractor-stats FAILED: {ex.Message}");
                return 1;
            }
        }

        if (args[0] is "metal-smoke")
        {
            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("metal-smoke requires macOS.");
                return 1;
            }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 64;
                int h = args.Length > 2 ? int.Parse(args[2]) : w;
                Console.WriteLine($"Metal smoke test — rendering Mandelbox at {w}x{h}...");

                using var renderer = new MetalMandelboxRenderer();
                Console.WriteLine($"  IsAvailable: {renderer.IsAvailable}");
                if (!renderer.IsAvailable)
                {
                    Console.Error.WriteLine("Metal backend not available.");
                    return 1;
                }

                var camera = new Camera3D(
                    new Vector3(0f, 3f, 12f),
                    new Vector3(0f, 0f, 0f),
                    Vector3.UnitY,
                    MathF.PI / 4f,
                    (float)w / h);

                var mb = new MandelboxParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f,  0.6f,  0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint[] pixels = renderer.RenderMandelbox(mb, camera, w, h, settings, bg, sf, light, palette);
                sw.Stop();

                // Pack background color for comparison
                uint bgR = (uint)(bg.R * 255f + 0.5f);
                uint bgG = (uint)(bg.G * 255f + 0.5f);
                uint bgB = (uint)(bg.B * 255f + 0.5f);
                uint bgPacked = (255u << 24) | (bgB << 16) | (bgG << 8) | bgR;
                int nonBg = pixels.Count(p => p != bgPacked);

                Console.WriteLine($"  {w*h} pixels rendered in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"    compute:  {renderer.LastComputeMs} ms");
                Console.WriteLine($"    readback: {renderer.LastReadbackMs} ms");
                Console.WriteLine($"  Background packed: 0x{bgPacked:X8}");
                Console.WriteLine($"  Non-background pixels: {nonBg}");
                if (w <= 16)
                {
                    Console.WriteLine("  Pixel dump (RGBA hex):");
                    for (int row = 0; row < h; row++)
                    {
                        Console.Write("   ");
                        for (int col = 0; col < w; col++)
                            Console.Write($" {pixels[row * w + col]:X8}");
                        Console.WriteLine();
                    }
                }

                // Save PNG for visual inspection
                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var bytes = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);

                var outPath = ResolveOutputPath("metal-mandelbox.png");
                ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}");

                return nonBg > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"metal-smoke FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "metal-bulb-smoke")
        {
            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("metal-bulb-smoke requires macOS.");
                return 1;
            }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 64;
                int h = args.Length > 2 ? int.Parse(args[2]) : w;
                Console.WriteLine($"Metal Mandelbulb smoke test — rendering at {w}x{h}...");

                using var renderer = new MetalMandelbulbRenderer();
                Console.WriteLine($"  IsAvailable: {renderer.IsAvailable}");
                if (!renderer.IsAvailable)
                {
                    Console.Error.WriteLine("Metal backend not available.");
                    return 1;
                }

                var camera = new Camera3D(
                    new Vector3(0f, 0f, 4f),
                    new Vector3(0f, 0f, 0f),
                    Vector3.UnitY,
                    MathF.PI / 4f,
                    (float)w / h);

                var mb = new MandelbulbParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f,  0.6f,  0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint[] pixels = renderer.RenderMandelbulb(mb, camera, w, h, settings, bg, sf, light, palette);
                sw.Stop();

                uint bgR = (uint)(bg.R * 255f + 0.5f);
                uint bgG = (uint)(bg.G * 255f + 0.5f);
                uint bgB = (uint)(bg.B * 255f + 0.5f);
                uint bgPacked = (255u << 24) | (bgB << 16) | (bgG << 8) | bgR;
                int nonBg = pixels.Count(p => p != bgPacked);

                Console.WriteLine($"  {w*h} pixels rendered in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"    compute:  {renderer.LastComputeMs} ms");
                Console.WriteLine($"    readback: {renderer.LastReadbackMs} ms");
                Console.WriteLine($"  Background packed: 0x{bgPacked:X8}");
                Console.WriteLine($"  Non-background pixels: {nonBg}");
                if (w <= 16)
                {
                    Console.WriteLine("  Pixel dump (RGBA hex):");
                    for (int row = 0; row < h; row++)
                    {
                        Console.Write("   ");
                        for (int col = 0; col < w; col++)
                            Console.Write($" {pixels[row * w + col]:X8}");
                        Console.WriteLine();
                    }
                }

                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var bytes = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);

                var outPath = ResolveOutputPath("metal-mandelbulb.png");
                ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}");

                return nonBg > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"metal-bulb-smoke FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "gpu-render")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: parsec gpu-render <name> [width] [height]");
                Console.Error.WriteLine($"Available: {string.Join(", ", Parsec.Rendering.Gpu.GpuRenders.All.Select(s => s.Name))}");
                return 2;
            }
            try
            {
                string name = args[1];
                int width = args.Length > 2 ? int.Parse(args[2]) : 900;
                int height = args.Length > 3 ? int.Parse(args[3]) : width;

                using var ctx = new Parsec.Rendering.Gpu.HeadlessGLContext();
                Console.WriteLine($"GPU render '{name}' at {width}x{height}");
                Console.WriteLine(ctx.Info());

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var bmp = Parsec.Rendering.Gpu.GpuRenders.RenderByName(
                    ctx.Gl, name, width, height,
                    (tile, tiles) =>
                    {
                        Console.Write($"\r  tile {tile}/{tiles}   ");
                        if (tile == tiles) Console.WriteLine();
                    });
                sw.Stop();

                var outPath = ResolveOutputPath($"gpu-{name}.png");
                Parsec.Rendering.Output.ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}  ({sw.ElapsedMilliseconds} ms)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU render FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        var matches = examples.Where(e => e.Name == args[0]).ToList();
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Unknown example: '{args[0]}'");
            Console.Error.WriteLine();
            PrintUsage(examples);
            return 2;
        }

        return RunExample(matches[0]) ? 0 : 1;
    }

    private static bool RunExample(IExample example)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var bitmap = example.Render();
            stopwatch.Stop();

            if (bitmap is null)
            {
                Console.WriteLine($"  {example.Name,-24} (no image)  ({stopwatch.ElapsedMilliseconds} ms)");
                return true;
            }

            var outputPath = ResolveOutputPath($"{example.Name}.png");
            ImageOutput.SavePng(bitmap, outputPath);

            Console.WriteLine($"  {example.Name,-24} -> {outputPath}  ({stopwatch.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {example.Name,-24} FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Output PNGs land next to the CLI executable, in an <c>outputs/</c> subdirectory.
    /// </summary>
    private static string ResolveOutputPath(string filename)
    {
        var exeDir = AppContext.BaseDirectory;
        var outputDir = Path.Combine(exeDir, "outputs");
        return Path.Combine(outputDir, filename);
    }

    private static void PrintUsage(IExample[] examples)
    {
        Console.WriteLine("Parsec CLI — IFS rendering spike");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  parsec <example>      Render a single example");
        Console.WriteLine("  parsec all            Render every example");
        Console.WriteLine("  parsec list           List available examples");
        Console.WriteLine("  parsec gpu-smoke      Phase-1 GPU plumbing test");
        Console.WriteLine("  parsec gpu-de-validate  Phase-2a GPU DE validation");
        Console.WriteLine("  parsec gpu-render <name> [w] [h]   GPU raymarch render");
        Console.WriteLine("  parsec metal-smoke [w] [h]         Metal Mandelbox spike test (macOS only)");
        Console.WriteLine("  parsec metal-bulb-smoke [w] [h]   Metal Mandelbulb smoke test (macOS only)");
        Console.WriteLine("  parsec help           Show this help");
        Console.WriteLine();
        Console.WriteLine("Available examples:");
        foreach (var ex in examples)
            Console.WriteLine($"  {ex.Name,-24} {ex.Description}");
        Console.WriteLine();
        Console.WriteLine("Output goes to <exe-dir>/outputs/<example>.png");
    }
}
