using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Audio.Sonification;
using Parsec.Cli.Examples;
using Parsec.Rendering;
using Parsec.Rendering.DeepZoom;
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

        if (args[0] is "metal-surface-texture-smoke")
        {
            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("metal-surface-texture-smoke requires macOS.");
                return 1;
            }
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: parsec metal-surface-texture-smoke <imagePath> [width] [height] [outDir]");
                return 2;
            }
            try
            {
                string imagePath = args[1];
                int w = args.Length > 2 ? int.Parse(args[2]) : 512;
                int h = args.Length > 3 ? int.Parse(args[3]) : w;
                string outDir = args.Length > 4 ? args[4] : Path.Combine(Path.GetDirectoryName(ResolveOutputPath("x"))!, "surface-texture-smoke");
                Directory.CreateDirectory(outDir);

                if (!TryLoadSurfaceTextureImage(imagePath, out var bytes, out int texW, out int texH, out int rowBytes, out var loadError))
                {
                    Console.Error.WriteLine(loadError);
                    return 1;
                }

                Console.WriteLine($"Metal surface-texture smoke — Mandelbox at {w}x{h}");
                Console.WriteLine($"  texture: {Path.GetFileName(imagePath)} ({texW}x{texH})");

                using var renderer = new MetalMandelboxRenderer();
                Console.WriteLine($"  IsAvailable: {renderer.IsAvailable}");
                if (!renderer.IsAvailable)
                {
                    Console.Error.WriteLine("Metal backend not available.");
                    return 1;
                }

                var camera = new Camera3D(
                    new Vector3(0f, 3f, 12f),
                    Vector3.Zero,
                    Vector3.UnitY,
                    MathF.PI / 4f,
                    (float)w / h);

                var fractal = new MandelboxParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f, 0.6f, 0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f);
                uint[] baseline = renderer.RenderMandelbox(fractal, camera, w, h, settings, bg, sf, light, palette);

                MetalSurfaceTextureManager.SetImage(bytes!, texW, texH, rowBytes);
                MetalSurfaceTextureManager.SetControls(enabled: true, blend: 0.85f, scale: 1.25f);
                uint[] textured = renderer.RenderMandelbox(fractal, camera, w, h, settings, bg, sf, light, palette);

                string baselinePath = Path.Combine(outDir, "mandelbox_base.png");
                string texturedPath = Path.Combine(outDir, "mandelbox_textured.png");
                SaveUintPixels(baseline, w, h, baselinePath);
                SaveUintPixels(textured, w, h, texturedPath);

                var stats = ComparePixelBuffers(baseline, textured);
                Console.WriteLine($"  changed pixels: {stats.changedPixels}/{baseline.Length}");
                Console.WriteLine($"  mean abs channel delta: {stats.meanAbsDelta:F2}");
                Console.WriteLine($"  -> {baselinePath}");
                Console.WriteLine($"  -> {texturedPath}");
                return stats.changedPixels > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"metal-surface-texture-smoke FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f);
            }
        }

        if (args[0] is "metal-rotbox-smoke")
        {
            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("metal-rotbox-smoke requires macOS.");
                return 1;
            }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 64;
                int h = args.Length > 2 ? int.Parse(args[2]) : w;
                Console.WriteLine($"Metal RotBox smoke test — rendering at {w}x{h}...");

                using var renderer = new MetalRotBoxRenderer();
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

                var rb = new RotBoxParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f,  0.6f,  0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint[] pixels = renderer.RenderRotBox(rb, camera, w, h, settings, bg, sf, light, palette);
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

                var outPath = ResolveOutputPath("metal-rotbox.png");
                ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}");

                return nonBg > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"metal-rotbox-smoke FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "metal-kifs-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-kifs-smoke requires macOS."); return 1; }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 64;
                int h = args.Length > 2 ? int.Parse(args[2]) : w;
                Console.WriteLine($"Metal KIFS smoke test — rendering at {w}x{h}...");
                using var renderer = new MetalKifsRenderer();
                Console.WriteLine($"  IsAvailable: {renderer.IsAvailable}");
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var camera = new Camera3D(new Vector3(0f, 3f, 12f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)w / h);
                var kf = new KifsParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f, 0.6f, 0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint[] pixels = renderer.RenderKifs(kf, camera, w, h, settings, bg, sf, light, palette);
                sw.Stop();

                uint bgR = (uint)(bg.R * 255f + 0.5f); uint bgG = (uint)(bg.G * 255f + 0.5f); uint bgB = (uint)(bg.B * 255f + 0.5f);
                uint bgPacked = (255u << 24) | (bgB << 16) | (bgG << 8) | bgR;
                int nonBg = pixels.Count(p => p != bgPacked);
                Console.WriteLine($"  {w*h} pixels rendered in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"    compute:  {renderer.LastComputeMs} ms");
                Console.WriteLine($"    readback: {renderer.LastReadbackMs} ms");
                Console.WriteLine($"  Non-background pixels: {nonBg}");

                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var bytes = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                var outPath = ResolveOutputPath("metal-kifs.png");
                ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}");
                return nonBg > 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-kifs-smoke FAILED: {ex.Message}"); return 1; }
        }

        if (args[0] is "metal-kleinian-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-kleinian-smoke requires macOS."); return 1; }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 64;
                int h = args.Length > 2 ? int.Parse(args[2]) : w;
                Console.WriteLine($"Metal Kleinian smoke test — rendering at {w}x{h}...");
                using var renderer = new MetalKleinianRenderer();
                Console.WriteLine($"  IsAvailable: {renderer.IsAvailable}");
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var camera = new Camera3D(new Vector3(0f, 3f, 12f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)w / h);
                var kl = new KleinianParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f, 0.6f, 0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint[] pixels = renderer.RenderKleinian(kl, camera, w, h, settings, bg, sf, light, palette);
                sw.Stop();

                uint bgR = (uint)(bg.R * 255f + 0.5f); uint bgG = (uint)(bg.G * 255f + 0.5f); uint bgB = (uint)(bg.B * 255f + 0.5f);
                uint bgPacked = (255u << 24) | (bgB << 16) | (bgG << 8) | bgR;
                int nonBg = pixels.Count(p => p != bgPacked);
                Console.WriteLine($"  {w*h} pixels rendered in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"    compute:  {renderer.LastComputeMs} ms");
                Console.WriteLine($"    readback: {renderer.LastReadbackMs} ms");
                Console.WriteLine($"  Non-background pixels: {nonBg}");

                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var bytes = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                var outPath = ResolveOutputPath("metal-kleinian.png");
                ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}");
                return nonBg > 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-kleinian-smoke FAILED: {ex.Message}"); return 1; }
        }

        if (args[0] is "metal-hybrid-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-hybrid-smoke requires macOS."); return 1; }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 64;
                int h = args.Length > 2 ? int.Parse(args[2]) : w;
                Console.WriteLine($"Metal Hybrid smoke test — rendering at {w}x{h}...");
                using var renderer = new MetalHybridRenderer();
                Console.WriteLine($"  IsAvailable: {renderer.IsAvailable}");
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var camera = new Camera3D(new Vector3(0f, 2f, 8f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)w / h);
                var hp = new HybridParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f, 0.6f, 0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint[] pixels = renderer.RenderHybrid(hp, camera, w, h, settings, bg, sf, light, palette);
                sw.Stop();

                uint bgR = (uint)(bg.R * 255f + 0.5f); uint bgG = (uint)(bg.G * 255f + 0.5f); uint bgB = (uint)(bg.B * 255f + 0.5f);
                uint bgPacked = (255u << 24) | (bgB << 16) | (bgG << 8) | bgR;
                int nonBg = pixels.Count(p => p != bgPacked);
                Console.WriteLine($"  {w*h} pixels rendered in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"    compute:  {renderer.LastComputeMs} ms");
                Console.WriteLine($"    readback: {renderer.LastReadbackMs} ms");
                Console.WriteLine($"  Non-background pixels: {nonBg}");

                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var bytes = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                var outPath = ResolveOutputPath("metal-hybrid.png");
                ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}");
                return nonBg > 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-hybrid-smoke FAILED: {ex.Message}"); return 1; }
        }

        if (args[0] is "metal-new-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-new-smoke requires macOS."); return 1; }
            try
            {
                int w = 64, h = 64;
                var camera = new Camera3D(new Vector3(0f, 1f, 5f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.02f, 0.03f, 0.07f);
                var sf = new Color(0.6f, 0.6f, 0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));
                uint bgR=(uint)(bg.R*255+0.5f),bgG=(uint)(bg.G*255+0.5f),bgB=(uint)(bg.B*255+0.5f);
                uint bgPacked=(255u<<24)|(bgB<<16)|(bgG<<8)|bgR;

                int pass = 0, fail = 0;
                void Test(string name, Func<uint[]> render) {
                    try {
                        var px = render();
                        int nonBg = px.Count(p => p != bgPacked);
                        Console.WriteLine($"  {name,-28} {w*h} px, {nonBg} non-bg  {(nonBg > 0 ? "PASS" : "WARN (all bg)")}");
                        if (nonBg > 0) pass++; else fail++;
                    } catch (Exception ex) { Console.WriteLine($"  {name,-28} FAIL: {ex.Message}"); fail++; }
                }

                using var r1 = new MetalBurningShipRenderer();
                Test("BurningShip", () => r1.RenderBurningShip(new BurningShipParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r2 = new MetalMengerRenderer();
                Test("Menger", () => r2.RenderMenger(new MengerParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r3 = new MetalQuaternionJuliaRenderer();
                Test("QuaternionJulia", () => r3.RenderQuaternionJulia(new QuaternionJuliaParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r4 = new MetalQJBoxRenderer();
                Test("QJBox", () => r4.RenderQJBox(new QJBoxParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r5 = new MetalApollonianRenderer();
                Test("Apollonian", () => r5.RenderApollonian(new ApollonianParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r6 = new MetalBicomplexRenderer();
                Test("Bicomplex", () => r6.RenderBicomplex(new BicomplexParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r7 = new MetalPhoenixRenderer();
                Test("Phoenix", () => r7.RenderPhoenix(new PhoenixParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r8 = new MetalBiomorphRenderer();
                Test("Biomorph", () => r8.RenderBiomorph(new BiomorphParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r9 = new MetalMoselyRenderer();
                Test("Mosely", () => r9.RenderMosely(new MoselyParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r10 = new MetalPseudoKleinian4DRenderer();
                Test("PseudoKleinian4D", () => r10.RenderPseudoKleinian4D(new PseudoKleinian4DParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r11 = new MetalRiemannSphereRenderer();
                Test("RiemannSphere", () => r11.RenderRiemannSphere(new RiemannSphereParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r12 = new MetalMandalayRenderer();
                Test("Mandalay", () => r12.RenderMandalay(new MandalayParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r13 = new MetalAnisotropicRenderer();
                Test("Anisotropic", () => r13.RenderAnisotropic(new AnisotropicParams(), camera, w, h, settings, bg, sf, light, palette));
                using var r14 = new MetalOrbitHybridRenderer();
                Test("OrbitHybrid", () => r14.RenderOrbitHybrid(new OrbitHybridParams(), camera, w, h, settings, bg, sf, light, palette));

                Console.WriteLine($"\n  {pass} passed, {fail} failed/warned");
                return fail == 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-new-smoke FAILED: {ex.Message}"); return 1; }
        }

        if (args[0] is "metal-morph-mp4")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-morph-mp4 requires macOS."); return 1; }
            try
            {
                int frames    = args.Length > 1 ? int.Parse(args[1]) : 120;
                int w         = args.Length > 2 ? int.Parse(args[2]) : 720;
                int h         = args.Length > 3 ? int.Parse(args[3]) : 720;
                string outMp4 = args.Length > 4 ? args[4] : ResolveOutputPath("morph.mp4");

                // Render at native resolution — MP4 has full colour depth so no 2× trick needed.
                Console.WriteLine($"Metal Mandelbulb power morph MP4 — {frames} frames at {w}x{h}");
                using var renderer = new MetalMandelbulbRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 400, HitEpsilon: 4e-4f, MaxDistance: 40f, NormalEpsilon: 4e-4f,
                    EnableSoftShadows: true,  ShadowSteps: 96, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.1f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.0f);

                // Full-amplitude rainbow palette — produces the indigo/purple/gold bands
                // typical of high-quality fractal imagery.
                var palette = new PaletteParams
                {
                    Base      = new Vector3(0.5f, 0.5f, 0.5f),
                    Amp       = new Vector3(0.5f, 0.5f, 0.5f),
                    Frequency = 1.5f,
                    Phase     = new Vector3(0.0f, 0.33f, 0.67f),
                    TrapScale = 0.75f,
                    TrapMix   = new Vector3(0.6f, 0.5f, 0.2f),
                    ShellMix  = 0.55f,
                };

                var bg     = new Color(0.01f, 0.01f, 0.03f);
                var surface = Color.Rgb(200, 175, 155);
                var light   = Vector3.Normalize(new Vector3(0.8f, 1.6f, 1.0f));

                // Camera close and slightly above, looking at origin — the morphing shape fills the frame.
                var cam = new Camera3D(
                    new Vector3(0.8f, 0.6f, 2.1f), Vector3.Zero, Vector3.UnitY,
                    MathF.PI / 3.5f, (float)w / h);

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-morph-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                // Rotated Grid Supersampling (RGSS) 4× — better edge coverage than axis-aligned 2×2
                Vector2[] jitters =
                [
                    new(-0.375f, -0.125f),
                    new( 0.125f, -0.375f),
                    new( 0.375f,  0.125f),
                    new(-0.125f,  0.375f),
                ];

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    // Ease in-out: power sweeps from 2→12 with a smoothstep envelope
                    float tRaw  = frames > 1 ? (float)i / (frames - 1) : 0f;
                    float t     = tRaw * tRaw * (3f - 2f * tRaw); // smoothstep
                    float power = 2f + (12f - 2f) * t;

                    var fractal = new MandelbulbParams { Power = power, Iterations = 10, Bailout = 2.0f, Fudge = 0.9f, BoundRadius = 1.3f };

                    // 4-pass SSAA: accumulate each channel as float, pack at the end
                    int pixCount = w * h;
                    var sumR = new float[pixCount];
                    var sumG = new float[pixCount];
                    var sumB = new float[pixCount];
                    foreach (var jitter in jitters)
                    {
                        uint[] pass = renderer.RenderMandelbulb(fractal, cam, w, h, settings, bg, surface, light, palette, jitter);
                        for (int p = 0; p < pixCount; p++)
                        {
                            sumR[p] += (pass[p]         & 0xFF);
                            sumG[p] += ((pass[p] >>  8) & 0xFF);
                            sumB[p] += ((pass[p] >> 16) & 0xFF);
                        }
                    }
                    var pixels = new uint[pixCount];
                    for (int p = 0; p < pixCount; p++)
                    {
                        uint r = (uint)(sumR[p] / jitters.Length + 0.5f);
                        uint g = (uint)(sumG[p] / jitters.Length + 0.5f);
                        uint b = (uint)(sumB[p] / jitters.Length + 0.5f);
                        pixels[p] = (255u << 24) | (b << 16) | (g << 8) | r;
                    }

                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));
                    Console.Write($"\r  frame {i + 1}/{frames} power={power:F1} — {renderer.LastComputeMs} ms   ");
                }
                sw.Stop();
                Console.WriteLine($"\nRendered {frames} frames in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / frames} ms avg)");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate 24 -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-c:v libx264 -crf 16 -preset slow -pix_fmt yuv420p \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-morph-mp4 FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // midi-showcase [duration] [out.mp4] [w] [h]
        // Render a Mandelbulb power-morph flythrough and play the EXACT geometry→MIDI stream it
        // emits through the in-repo 8-voice instrument synth, muxed to an audio+video mp4.
        if (args[0] is "midi-showcase")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("midi-showcase requires macOS."); return 1; }
            try
            {
                double duration = args.Length > 1 && double.TryParse(args[1], out var dd) ? dd : 20.0;
                string outMp4   = args.Length > 2 ? args[2] : ResolveOutputPath("midi_showcase.mp4");
                int w = args.Length > 3 && int.TryParse(args[3], out var pw) ? pw : 1280;
                int h = args.Length > 4 && int.TryParse(args[4], out var ph) ? ph : 720;
                if ((w & 1) != 0) w++; if ((h & 1) != 0) h++;
                // Optional morph controls: [morphHz] [powerLo] [powerHi]. Slower Hz + wider range = a
                // big, languid morph. Camera pace scales with morphHz (relative to the 0.18 default).
                float morphHz = args.Length > 5 && float.TryParse(args[5], out var mh) ? mh : 0.18f;
                float powerLo = args.Length > 6 && float.TryParse(args[6], out var pl) ? pl : 2.4f;
                float powerHi = args.Length > 7 && float.TryParse(args[7], out var pH) ? pH : 8.6f;
                float powerMid = (powerLo + powerHi) * 0.5f, powerAmp = (powerHi - powerLo) * 0.5f;
                float pace = morphHz / 0.18f;
                const int fps = 30;
                int nFrames = Math.Max(1, (int)Math.Round(duration * fps));

                Console.WriteLine($"midi-showcase — Mandelbulb power-morph, {duration:F1}s @ {fps}fps ({nFrames} frames), {w}x{h}");
                Console.WriteLine($"  morph {powerLo:F1}→{powerHi:F1} @ {morphHz:F3} Hz (pace ×{pace:F2})");

                using var renderer = new MetalMandelbulbRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                // MIDI controller + recording hook: capture the exact emitted stream.
                using var midi = new Parsec.Audio.Midi.MidiOutputSession("Parsec");
                if (!midi.IsAvailable) { Console.Error.WriteLine($"MIDI unavailable: {midi.UnavailableReason}"); return 1; }
                var cfg = new Parsec.Audio.Midi.MidiMappingConfig();
                var controller = new Parsec.Audio.Midi.MidiOutputController(midi, cfg);
                var events = new List<Parsec.Audio.Midi.MidiInstrumentSynth.Ev>();
                double evTime = 0;
                midi.OnControlChange = (ch, cc, v) => events.Add(new(evTime, Parsec.Audio.Midi.MidiInstrumentSynth.EvType.Cc, ch, cc, v));
                midi.OnNoteOn        = (ch, nn, ve) => events.Add(new(evTime, Parsec.Audio.Midi.MidiInstrumentSynth.EvType.NoteOn, ch, nn, ve));
                midi.OnNoteOff       = (ch, nn) => events.Add(new(evTime, Parsec.Audio.Midi.MidiInstrumentSynth.EvType.NoteOff, ch, nn, 0));

                var telSettings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1e-3f, MaxDistance: 40f, NormalEpsilon: 1.5e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);
                var dispSettings = new RaymarchSettings(
                    MaxSteps: 300, HitEpsilon: 5e-4f, MaxDistance: 40f, NormalEpsilon: 5e-4f,
                    EnableSoftShadows: true, ShadowSteps: 64, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 1.0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.0f);

                var bg = new Color(0.01f, 0.01f, 0.03f);
                var surface = Color.Rgb(200, 175, 155);
                var light = Vector3.Normalize(new Vector3(0.8f, 1.6f, 1.0f));

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-midishow-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var prevPos = Vector3.Zero; float prevPower = 0f; bool havePrev = false;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < nFrames; i++)
                {
                    float t = (float)i / fps;                 // seconds
                    float u = nFrames > 1 ? (float)i / (nFrames - 1) : 0f;
                    // Power morph: single sine over [powerLo,powerHi] at morphHz. A tiny fast wobble
                    // is kept but scaled down with pace so slow renders stay smooth.
                    float power = powerMid + powerAmp * MathF.Sin(2f * MathF.PI * morphHz * t)
                                           + 0.35f * pace * MathF.Sin(2f * MathF.PI * 0.55f * pace * t + 0.6f);
                    // Camera: slow orbit + dolly in/out, all paced with the morph.
                    float ang = 2f * MathF.PI * 0.5f * pace * u + 0.4f;
                    float radius = 2.35f - 0.55f * MathF.Sin(2f * MathF.PI * 0.25f * pace * t);
                    float elev = 0.45f + 0.25f * MathF.Sin(2f * MathF.PI * 0.2f * pace * t);
                    var pos = new Vector3(MathF.Cos(ang) * radius, elev, MathF.Sin(ang) * radius);
                    var fwd = Vector3.Normalize(Vector3.Zero - pos);

                    var fractal = new MandelbulbParams { Power = power, Iterations = 10, Bailout = 2.0f, Fudge = 0.9f, BoundRadius = 1.3f };
                    var telCam  = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, MathF.PI / 3.5f, (float)w / h);
                    var st = renderer.RunTelemetryPass(fractal, telCam, telSettings);

                    float spd = havePrev ? (pos - prevPos).Length() * fps : 0f;
                    float zv  = havePrev ? Vector3.Dot(pos - prevPos, fwd) * fps : 0f;
                    float pv  = havePrev ? MathF.Abs(power - prevPower) * fps : 0f;
                    prevPos = pos; prevPower = power; havePrev = true;

                    FractalSonicCell[]? cells = null;
                    if (st?.Cells is { Length: > 0 } mc)
                    {
                        cells = new FractalSonicCell[mc.Length];
                        for (int ci = 0; ci < mc.Length; ci++)
                            cells[ci] = new FractalSonicCell(mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean, mc[ci].Energy);
                    }

                    var frame = new FractalSonicFrame(
                        Time: t, HitRatio: st?.HitRatio ?? 0f, MeanDepth: st?.MeanDepth ?? 0f,
                        DepthVariance: st?.DepthVariance ?? 0f, StepMean: st?.StepMean ?? 0f, StepP90: st?.StepP90 ?? 0f,
                        NormalMean: st?.NormalMean ?? Vector3.Zero, NormalVariance: st?.NormalVariance ?? 0f,
                        TrapMean: st?.TrapMean ?? Vector4.Zero, TrapVariance: st?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: spd, ParameterVelocity: pv,
                        CameraPosition: pos, CameraForward: fwd, CameraUp: Vector3.UnitY,
                        Cells: cells, ZoomVelocity: zv,
                        CentroidX: st?.CentroidX ?? 0f, CentroidY: st?.CentroidY ?? 0f, Dispersion: st?.Dispersion ?? 0f,
                        MidiRegionEnergy: st?.MidiRegionEnergy);

                    // Animate palette hue slowly so on-screen colour (→ CC24 → choir) actually moves.
                    float hueShift = 0.5f * t / (float)Math.Max(1.0, duration);
                    var palette = new PaletteParams
                    {
                        Base = new Vector3(0.5f, 0.5f, 0.5f), Amp = new Vector3(0.5f, 0.5f, 0.5f),
                        Frequency = 1.4f, Phase = new Vector3(hueShift, 0.33f + hueShift, 0.67f + hueShift),
                        TrapScale = 0.75f, TrapMix = new Vector3(0.6f, 0.5f, 0.2f), ShellMix = 0.55f,
                    };

                    var pixels = renderer.RenderMandelbulb(fractal, telCam, w, h, dispSettings, bg, surface, light, palette);
                    var (hue, hsat, hval) = Parsec.Audio.Midi.MidiOutputController.MeanScreenColorHsv(pixels, w, h);

                    evTime = t;
                    controller.Update(frame, hue, hsat, hval);

                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));
                    bmp.Dispose();
                    if (i % fps == 0 || i == nFrames - 1)
                        Console.Write($"\r  frame {i + 1}/{nFrames}  pow={power:F1} hit={st?.HitRatio ?? 0:F2} events={events.Count}   ");
                }
                sw.Stop();
                Console.WriteLine($"\n  rendered {nFrames} frames in {sw.ElapsedMilliseconds / 1000.0:F1}s; captured {events.Count} MIDI messages");

                // Synthesize the 8-voice instrument audio from the captured MIDI.
                Console.Write("  synthesizing 8-voice instrument mix... ");
                var pcm = Parsec.Audio.Midi.MidiInstrumentSynth.Synthesize(events, duration);
                string wav = Path.Combine(frameDir, "mix.wav");
                WavEncoder.Write(wav, pcm, Parsec.Audio.Midi.MidiInstrumentSynth.SampleRate, channels: 2);
                float pk = 0f; for (int s = 0; s < pcm.Length; s++) pk = MathF.Max(pk, MathF.Abs(pcm[s]) / 32767f);
                Console.WriteLine($"peak {20f * MathF.Log10(pk + 1e-9f):F1} dBFS");

                // Mux frames + audio → mp4.
                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" -i \"{wav}\" " +
                    $"-c:v libx264 -crf 17 -preset medium -pix_fmt yuv420p -c:a aac -b:a 192k -shortest \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"midi-showcase FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // fluid-showcase [duration] [out.mp4] [w] [h] [morphHz] [powerLo] [powerHi] [particles]
        // midi-showcase + an "underwater" curl-noise particle field that floats around the
        // Mandelbulb and is shoved by the fractal's morph (∂DE/∂t advection). Composited with an
        // underwater grade; same captured-MIDI 8-voice audio.
        if (args[0] is "fluid-showcase")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("fluid-showcase requires macOS."); return 1; }
            try
            {
                double duration = args.Length > 1 && double.TryParse(args[1], out var dd) ? dd : 18.0;
                string outMp4   = args.Length > 2 ? args[2] : ResolveOutputPath("fluid_showcase.mp4");
                int w = args.Length > 3 && int.TryParse(args[3], out var pw) ? pw : 1280;
                int h = args.Length > 4 && int.TryParse(args[4], out var ph) ? ph : 720;
                if ((w & 1) != 0) w++; if ((h & 1) != 0) h++;
                float morphHz = args.Length > 5 && float.TryParse(args[5], out var mh) ? mh : 0.07f;
                float powerLo = args.Length > 6 && float.TryParse(args[6], out var pl) ? pl : 3.0f;
                float powerHi = args.Length > 7 && float.TryParse(args[7], out var pH) ? pH : 10.0f;
                int nParticles = args.Length > 8 && int.TryParse(args[8], out var np) ? np : 3500;
                float powerMid = (powerLo + powerHi) * 0.5f, powerAmp = (powerHi - powerLo) * 0.5f;
                float pace = morphHz / 0.18f;
                const int fps = 30;
                int nFrames = Math.Max(1, (int)Math.Round(duration * fps));

                Console.WriteLine($"fluid-showcase — Mandelbulb + {nParticles} underwater particles, {duration:F1}s @ {fps}fps, {w}x{h}");
                Console.WriteLine($"  morph {powerLo:F1}→{powerHi:F1} @ {morphHz:F3} Hz");

                // CPU Mandelbulb DE so particles can sense the surface/gradient.
                static float Mbulb(System.Numerics.Vector3 pos, float power)
                {
                    var z = pos; float dr = 1f, r = 0f;
                    for (int it = 0; it < 8; it++)
                    {
                        r = z.Length(); if (r > 2f) break;
                        float theta = MathF.Acos(Math.Clamp(z.Z / MathF.Max(r, 1e-9f), -1f, 1f));
                        float phi = MathF.Atan2(z.Y, z.X);
                        dr = MathF.Pow(r, power - 1f) * power * dr + 1f;
                        float zr = MathF.Pow(r, power);
                        theta *= power; phi *= power;
                        z = zr * new System.Numerics.Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta)) + pos;
                    }
                    return 0.5f * MathF.Log(MathF.Max(r, 1e-9f)) * r / MathF.Max(dr, 1e-9f);
                }

                using var renderer = new MetalMandelbulbRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                using var midi = new Parsec.Audio.Midi.MidiOutputSession("Parsec");
                if (!midi.IsAvailable) { Console.Error.WriteLine($"MIDI unavailable: {midi.UnavailableReason}"); return 1; }
                var controller = new Parsec.Audio.Midi.MidiOutputController(midi, new Parsec.Audio.Midi.MidiMappingConfig());
                var events = new List<Parsec.Audio.Midi.MidiInstrumentSynth.Ev>();
                double evTime = 0;
                midi.OnControlChange = (ch, cc, v) => events.Add(new(evTime, Parsec.Audio.Midi.MidiInstrumentSynth.EvType.Cc, ch, cc, v));
                midi.OnNoteOn        = (ch, nn, ve) => events.Add(new(evTime, Parsec.Audio.Midi.MidiInstrumentSynth.EvType.NoteOn, ch, nn, ve));
                midi.OnNoteOff       = (ch, nn) => events.Add(new(evTime, Parsec.Audio.Midi.MidiInstrumentSynth.EvType.NoteOff, ch, nn, 0));

                var telSettings = new RaymarchSettings(160, 1e-3f, 40f, 1.5e-3f, false, 0, 0f, false, 0, 0f, 0f, 1, false, 0, 0f, 0f, 1f);
                var dispSettings = new RaymarchSettings(300, 5e-4f, 40f, 5e-4f, true, 64, 12f, true, 5, 0.04f, 1.0f, 1, false, 0, 0f, 0f, 1.0f);
                var bg = new Color(0.005f, 0.02f, 0.03f);
                var surface = Color.Rgb(200, 175, 155);
                var light = Vector3.Normalize(new Vector3(0.8f, 1.6f, 1.0f));

                var field = new Parsec.Core.Fluid.FluidParticleField(nParticles, seed: 7);
                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-fluid-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var prevPos = Vector3.Zero; float prevPower = 0f; bool havePrev = false;
                float fovY = MathF.PI / 3.5f;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < nFrames; i++)
                {
                    float t = (float)i / fps;
                    float u = nFrames > 1 ? (float)i / (nFrames - 1) : 0f;
                    float power = powerMid + powerAmp * MathF.Sin(2f * MathF.PI * morphHz * t)
                                           + 0.3f * pace * MathF.Sin(2f * MathF.PI * 0.55f * pace * t + 0.6f);
                    float ang = 2f * MathF.PI * 0.5f * pace * u + 0.4f;
                    float radius = 2.4f - 0.5f * MathF.Sin(2f * MathF.PI * 0.25f * pace * t);
                    float elev = 0.45f + 0.25f * MathF.Sin(2f * MathF.PI * 0.2f * pace * t);
                    var pos = new Vector3(MathF.Cos(ang) * radius, elev, MathF.Sin(ang) * radius);
                    var fwd = Vector3.Normalize(Vector3.Zero - pos);

                    var fractal = new MandelbulbParams { Power = power, Iterations = 10, Bailout = 2.0f, Fudge = 0.9f, BoundRadius = 1.3f };
                    var cam  = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fovY, (float)w / h);
                    var st = renderer.RunTelemetryPass(fractal, cam, telSettings);

                    float spd = havePrev ? (pos - prevPos).Length() * fps : 0f;
                    float zv  = havePrev ? Vector3.Dot(pos - prevPos, fwd) * fps : 0f;
                    float pv  = havePrev ? MathF.Abs(power - prevPower) * fps : 0f;

                    // Advance the particle field; advection uses this frame's vs last frame's power.
                    float pprev = havePrev ? prevPower : power;
                    field.Step(1f / fps, p => Mbulb(p, power), p => Mbulb(p, pprev), t);
                    prevPos = pos; prevPower = power; havePrev = true;

                    FractalSonicCell[]? cells = null;
                    if (st?.Cells is { Length: > 0 } mc)
                    {
                        cells = new FractalSonicCell[mc.Length];
                        for (int ci = 0; ci < mc.Length; ci++)
                            cells[ci] = new FractalSonicCell(mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean, mc[ci].Energy);
                    }
                    var frame = new FractalSonicFrame(
                        Time: t, HitRatio: st?.HitRatio ?? 0f, MeanDepth: st?.MeanDepth ?? 0f,
                        DepthVariance: st?.DepthVariance ?? 0f, StepMean: st?.StepMean ?? 0f, StepP90: st?.StepP90 ?? 0f,
                        NormalMean: st?.NormalMean ?? Vector3.Zero, NormalVariance: st?.NormalVariance ?? 0f,
                        TrapMean: st?.TrapMean ?? Vector4.Zero, TrapVariance: st?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: spd, ParameterVelocity: pv, CameraPosition: pos, CameraForward: fwd, CameraUp: Vector3.UnitY,
                        Cells: cells, ZoomVelocity: zv, CentroidX: st?.CentroidX ?? 0f, CentroidY: st?.CentroidY ?? 0f,
                        Dispersion: st?.Dispersion ?? 0f, MidiRegionEnergy: st?.MidiRegionEnergy);

                    float hueShift = 0.4f * t / (float)Math.Max(1.0, duration);
                    var palette = new PaletteParams
                    {
                        Base = new Vector3(0.5f, 0.5f, 0.5f), Amp = new Vector3(0.5f, 0.5f, 0.5f),
                        Frequency = 1.3f, Phase = new Vector3(hueShift, 0.33f + hueShift, 0.67f + hueShift),
                        TrapScale = 0.7f, TrapMix = new Vector3(0.5f, 0.55f, 0.35f), ShellMix = 0.5f,
                    };
                    var pixels = renderer.RenderMandelbulb(fractal, cam, w, h, dispSettings, bg, surface, light, palette);
                    var (hue, hsat, hval) = Parsec.Audio.Midi.MidiOutputController.MeanScreenColorHsv(pixels, w, h);
                    evTime = t; controller.Update(frame, hue, hsat, hval);

                    // ---- underwater grade + particle composite ----
                    FluidCompositor.Apply(pixels, w, h, field, cam, fovY, t);

                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));
                    bmp.Dispose();
                    if (i % fps == 0 || i == nFrames - 1)
                        Console.Write($"\r  frame {i + 1}/{nFrames}  pow={power:F1} events={events.Count}   ");
                }
                sw.Stop();
                Console.WriteLine($"\n  rendered {nFrames} frames in {sw.ElapsedMilliseconds / 1000.0:F1}s; {events.Count} MIDI messages");

                Console.Write("  synthesizing 8-voice mix... ");
                var pcm = Parsec.Audio.Midi.MidiInstrumentSynth.Synthesize(events, duration);
                string wav = Path.Combine(frameDir, "mix.wav");
                WavEncoder.Write(wav, pcm, Parsec.Audio.Midi.MidiInstrumentSynth.SampleRate, channels: 2);
                Console.WriteLine("done");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" -i \"{wav}\" " +
                    $"-c:v libx264 -crf 17 -preset medium -pix_fmt yuv420p -c:a aac -b:a 192k -shortest \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd(); proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"fluid-showcase FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-domain-warp-mp4")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-domain-warp-mp4 requires macOS."); return 1; }
            try
            {
                double duration = args.Length > 1 && double.TryParse(args[1], out var d) ? d : 4.0;
                string outMp4 = args.Length > 2 ? args[2] : ResolveOutputPath("domain_warp.mp4");
                int w = args.Length > 3 && int.TryParse(args[3], out var parsedW) ? parsedW : 720;
                int h = args.Length > 4 && int.TryParse(args[4], out var parsedH) ? parsedH : 406;
                if ((w & 1) != 0) w++;
                if ((h & 1) != 0) h++;
                const int fps = 24;
                int frames = Math.Max(1, (int)Math.Ceiling(duration * fps));

                Console.WriteLine($"metal-domain-warp-mp4 - Mandelbox procedural domain warp, {duration:F1}s @ {fps}fps, {w}x{h}");
                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);

                var settings = new RaymarchSettings(
                    MaxSteps: 220, HitEpsilon: 4e-4f, MaxDistance: 35f, NormalEpsilon: 5e-4f,
                    EnableSoftShadows: true, ShadowSteps: 64, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 0.42f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 2.2f);

                var palette = new PaletteParams
                {
                    Base = new Vector3(0.40f, 0.44f, 0.48f),
                    Amp = new Vector3(0.55f, 0.42f, 0.34f),
                    Frequency = 1.65f,
                    Phase = new Vector3(0.02f, 0.31f, 0.68f),
                    TrapScale = 0.92f,
                    TrapMix = new Vector3(0.65f, 0.38f, 0.28f),
                    ShellMix = 0.28f,
                };

                var bg = new Color(0.12f, 0.13f, 0.16f);
                var surface = Color.Rgb(245, 220, 180);
                var light = Vector3.Normalize(new Vector3(1.4f, 2.1f, 0.9f));
                var post = new PostProcessParams
                {
                    Brightness = 1.85f,
                    Contrast = 0.96f,
                    Gamma = 0.72f,
                    Saturation = 1.75f,
                    HdrEnabled = true,
                };

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-domain-warp-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);
                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);

                uint[]? baseline = null;
                uint[]? firstWarp = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float tN = frames > 1 ? (float)i / (frames - 1) : 0f;
                        float orbit = tN * MathF.Tau * 0.72f;
                        float camR = 3.75f - 0.35f * MathF.Sin(tN * MathF.PI);
                        var camera = new Camera3D(
                            new Vector3(camR * MathF.Sin(orbit), 1.15f + 0.45f * MathF.Sin(tN * MathF.Tau), camR * MathF.Cos(orbit)),
                            new Vector3(0f, 0.12f, 0f),
                            Vector3.UnitY,
                            MathF.PI / 4.2f,
                            (float)w / h);

                        float pulse = 0.5f + 0.5f * MathF.Sin(tN * MathF.Tau * 1.35f);
                        float strength = 0.16f + 0.24f * pulse;
                        float scale = 0.85f + 2.8f * tN;
                        var fractal = new MandelboxParams
                        {
                            Scale = 2.05f + 0.18f * MathF.Sin(tN * MathF.Tau * 1.1f),
                            Iterations = 15,
                            FoldingLimit = 1.0f,
                            MinRadius = 0.42f,
                            FixedRadius = 1.0f,
                            Fudge = 0.92f,
                            BoundRadius = 2.4f,
                        };

                        if (i == 0)
                        {
                            DomainWarpState.SetControls(enabled: false, strength: 0f, scale: scale);
                            baseline = renderer.RenderMandelbox(fractal, camera, w, h, settings, bg, surface, light, palette, post);
                        }

                        DomainWarpState.SetControls(enabled: true, strength: strength, scale: scale);
                        uint[] pixels = renderer.RenderMandelbox(fractal, camera, w, h, settings, bg, surface, light, palette, post);
                        if (i == 0) firstWarp = pixels;

                        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        using var bmp = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));

                        Console.Write($"\r  frame {i + 1}/{frames} strength={strength:F2} scale={scale:F2} compute={renderer.LastComputeMs}ms   ");
                    }
                }
                finally
                {
                    DomainWarpState.SetControls(enabled: false, strength: 0f, scale: 1f);
                }
                sw.Stop();

                if (baseline != null && firstWarp != null)
                {
                    int changed = 0;
                    for (int i = 0; i < baseline.Length; i++)
                        if (baseline[i] != firstWarp[i]) changed++;
                    Console.WriteLine($"\n  baseline vs first warped frame: {changed}/{baseline.Length} pixels changed ({changed * 100.0 / baseline.Length:F2}%)");
                }

                Console.WriteLine($"  rendered {frames} frames in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / frames} ms avg)");
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-c:v libx264 -crf 16 -preset slow -pix_fmt yuv420p \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                string ffmpegError = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    Console.Error.WriteLine("ffmpeg failed.");
                    Console.Error.WriteLine(ffmpegError);
                    return 1;
                }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4} ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex)
            {
                DomainWarpState.SetControls(enabled: false, strength: 0f, scale: 1f);
                Console.Error.WriteLine($"metal-domain-warp-mp4 FAILED: {ex.Message}\n{ex.StackTrace}");
                return 1;
            }
        }

        if (args[0] is "metal-cinematic-gif")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-cinematic-gif requires macOS."); return 1; }
            try
            {
                // Renders at 2× internal resolution then downscales via ffmpeg lanczos → natural 4× SS AA.
                int frames    = args.Length > 1 ? int.Parse(args[1]) : 90;
                int outW      = args.Length > 2 ? int.Parse(args[2]) : 480;
                int outH      = args.Length > 3 ? int.Parse(args[3]) : 270;
                string outGif = args.Length > 4 ? args[4] : ResolveOutputPath("cinematic.gif");
                int renderW   = outW * 2;
                int renderH   = outH * 2;

                Console.WriteLine($"Metal Mandelbox cinematic GIF — {frames} frames, render {renderW}x{renderH} → output {outW}x{outH}");
                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 400, HitEpsilon: 5e-4f, MaxDistance: 50f, NormalEpsilon: 5e-4f,
                    EnableSoftShadows: true,  ShadowSteps: 64, ShadowSoftness: 14f,
                    EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.1f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.1f);
                var bg      = new Color(0.01f, 0.02f, 0.06f);
                var surface = Color.Rgb(170, 150, 130);
                var light   = Vector3.Normalize(new Vector3(0.6f, 1.8f, 1.2f));
                var palette = PaletteParams.Default;

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-cinematic-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    // Slow spiral-in: 270° arc, radius 12→5, elevation 3→0.3
                    float t      = (float)i / (frames - 1);
                    float angle  = (270f * t) * MathF.PI / 180f;
                    float radius = 12f + (5f - 12f) * t;
                    float elev   = 3f  + (0.3f - 3f) * t;
                    var   pos    = new Vector3(MathF.Sin(angle) * radius, elev, MathF.Cos(angle) * radius);
                    var   cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)renderW / renderH);

                    uint[] pixels = renderer.RenderMandelbox(fractal, cam, renderW, renderH, settings, bg, surface, light, palette);

                    var info  = new SKImageInfo(renderW, renderH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));
                    Console.Write($"\r  frame {i + 1}/{frames} — {renderer.LastComputeMs} ms   ");
                }
                sw.Stop();
                Console.WriteLine($"\nRendered {frames} frames in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / frames} ms avg)");

                Directory.CreateDirectory(Path.GetDirectoryName(outGif)!);
                // Lanczos downscale + palette-optimised GIF
                string ffArgs = $"-y -framerate 12 -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-vf \"scale={outW}:{outH}:flags=lanczos,fps=12,split[s0][s1];[s0]palettegen=max_colors=256[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5\" \"{outGif}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outGif);
                Console.WriteLine($"  -> {outGif}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-cinematic-gif FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-orbit-gif")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-orbit-gif requires macOS."); return 1; }
            try
            {
                int frames   = args.Length > 1 ? int.Parse(args[1]) : 60;
                int w        = args.Length > 2 ? int.Parse(args[2]) : 480;
                int h        = args.Length > 3 ? int.Parse(args[3]) : 270;
                string outGif = args.Length > 4 ? args[4] : ResolveOutputPath("orbit.gif");

                Console.WriteLine($"Metal Mandelbulb orbit GIF — {frames} frames at {w}x{h}");
                using var renderer = new MetalMandelbulbRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelbulbParams();
                var settings = new RaymarchSettings(MaxSteps: 200, HitEpsilon: 1.5e-3f, MaxDistance: 40f,
                    NormalEpsilon: 2e-3f, EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 1.0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f,
                    LightIntensity: 1.0f);
                var bg      = new Color(0.02f, 0.03f, 0.07f);
                var surface = Color.Rgb(210, 175, 140);
                var light   = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));
                var palette = PaletteParams.Default;

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-orbit-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    float t   = 2f * MathF.PI * i / frames;
                    var   pos = new Vector3(MathF.Sin(t) * 4f, 0.5f, MathF.Cos(t) * 4f);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)w / h);

                    uint[] pixels = renderer.RenderMandelbulb(fractal, cam, w, h, settings, bg, surface, light, palette);

                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));
                    Console.Write($"\r  frame {i + 1}/{frames} — {renderer.LastComputeMs} ms   ");
                }
                sw.Stop();
                Console.WriteLine($"\nRendered {frames} frames in {sw.ElapsedMilliseconds} ms");

                Directory.CreateDirectory(Path.GetDirectoryName(outGif)!);
                string ffArgs = $"-y -framerate 24 -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-vf \"fps=24,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" \"{outGif}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                Console.WriteLine($"  -> {outGif}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-orbit-gif FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-deepzoom-mp4")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-deepzoom-mp4 requires macOS."); return 1; }
            try
            {
                int    frames  = args.Length > 1 ? int.Parse(args[1])  : 90;
                int    w       = args.Length > 2 ? int.Parse(args[2])  : 640;
                int    h       = args.Length > 3 ? int.Parse(args[3])  : 360;
                string outMp4  = args.Length > 4 ? args[4] : ResolveOutputPath("deepzoom.mp4");

                // Seahorse Valley — validated deep-zoom landmark from ReferenceOrbit.cs comments.
                // Zoom from full Mandelbrot view (radius 1.5) to ~1e-8 (8 orders of magnitude).
                string centerRe = "-0.743643887037158704752191506114774";
                string centerIm =  "0.131825904205311970493132056385139";
                double startRadius = 1.5;
                double endRadius   = 1e-8;

                Console.WriteLine($"Metal deep-zoom MP4 — {frames} frames at {w}x{h}");
                Console.WriteLine($"  Target: Seahorse Valley  radius {startRadius} → {endRadius:e1}");

                using var renderer = new MetalDeepZoomRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal deep-zoom backend not available."); return 1; }

                // Rainbow cosine palette — saturated bands suit the 2D escape-time colouring well.
                var palette = new PaletteParams
                {
                    Base      = new Vector3(0.5f, 0.5f, 0.5f),
                    Amp       = new Vector3(0.5f, 0.5f, 0.5f),
                    Frequency = 1.0f,
                    Phase     = new Vector3(0.0f, 0.33f, 0.67f),
                    TrapScale = 1.0f,
                    ShellMix  = 0f,
                };
                var bg       = new Color(0.01f, 0.01f, 0.03f);
                var settings = new Parsec.Rendering.Raymarching.RaymarchSettings(
                    MaxSteps: 0, HitEpsilon: 0, MaxDistance: 0, NormalEpsilon: 0,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                    HeroSamples: 4,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0, F0: 0, LightIntensity: 0);

                // Build the view: center is fixed at the Seahorse Valley; radius decreases each frame.
                double logStart = Math.Log(startRadius);
                double logEnd   = Math.Log(endRadius);

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-deepzoom-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    double t      = frames > 1 ? (double)i / (frames - 1) : 0.0;
                    double radius = Math.Exp(logStart + t * (logEnd - logStart));

                    var view = new Parsec.Rendering.DeepZoom.DeepZoomView
                    {
                        CenterRe = centerRe,
                        CenterIm = centerIm,
                        Radius   = radius,
                        Formula  = 0,   // Mandelbrot
                    };

                    uint[] pixels = renderer.Render(view, w, h, palette, bg, settings);

                    // Write PNG frame.
                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));

                    Console.Write($"\r  frame {i + 1}/{frames}  radius={radius:e2}  ref={renderer.LastComputeMs} ms compute   ");
                }
                sw.Stop();
                Console.WriteLine($"\nRendered {frames} frames in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / frames} ms avg)");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate 24 -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-c:v libx264 -crf 18 -preset slow -pix_fmt yuv444p \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-deepzoom-mp4 FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-audio-reactive")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-audio-reactive requires macOS."); return 1; }
            try
            {
                string wavPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Chopin - Nocturne op.9 No.2 - andrea romano (128k).wav");
                if (!File.Exists(wavPath)) { Console.Error.WriteLine($"WAV not found: {wavPath}"); return 1; }
                double startSec = args.Length > 2 ? double.Parse(args[2]) : 0.0;
                double durationSec = args.Length > 3 ? double.Parse(args[3]) : 5.0;
                string outMp4 = args.Length > 4 ? args[4] : ResolveOutputPath("audio-reactive.mp4");

                int fps = 30, w = 640, h = 480;
                int totalFrames = (int)(durationSec * fps);

                Console.WriteLine($"Audio-reactive render: {totalFrames} frames at {w}x{h}, audio={Path.GetFileName(wavPath)}");

                // 1. Analyze audio
                Console.Write("  Analyzing audio... ");
                var analyzer = new Parsec.Audio.WaveAudioAnalyzer();
                var track = analyzer.AnalyzeAsync(new Uri("file://" + Path.GetFullPath(wavPath)), null, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"{track.Frames.Count} feature frames");

                // 2. Set up renderer — Phoenix with multi-band audio modulation
                //    Curling tendrils via PMem; cut plane reveals internals
                var renderer = new MetalPhoenixRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var bg = new Color(0.02f, 0.02f, 0.05f);
                var surface = Color.Rgb(220, 185, 140);

                // Pre-scan audio for normalization
                double maxRms = 0, maxBass = 0, maxMid = 0, maxTreble = 0, maxOnset = 0, maxCentroid = 0;
                for (int i = 0; i < totalFrames; i++)
                {
                    var f = track.Sample(TimeSpan.FromSeconds(startSec + (double)i / fps));
                    maxRms = Math.Max(maxRms, f.Rms);
                    maxBass = Math.Max(maxBass, f.BassEnergy);
                    maxMid = Math.Max(maxMid, f.MidEnergy);
                    maxTreble = Math.Max(maxTreble, f.TrebleEnergy);
                    maxOnset = Math.Max(maxOnset, f.OnsetStrength);
                    maxCentroid = Math.Max(maxCentroid, f.SpectrumCentroidHz);
                }
                float Norm(double val, double peak) => peak > 0 ? (float)Math.Min(val / peak, 1.0) : 0f;

                // EMA smoothing state
                float smoothPMem = -0.5f, smoothCx = 0.4f, smoothPlaneOff = 0f;
                float smoothDist = 3.5f, smoothFreq = 1.5f, smoothPhaseShift = 0f;
                float smoothIntensity = 1f;
                const float ema = 0.2f;

                // 3. Render frames with multi-band audio modulation
                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-audio-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < totalFrames; i++)
                {
                    double t = startSec + (double)i / fps;
                    var frame = track.Sample(TimeSpan.FromSeconds(t));
                    float progress = (float)i / totalFrames;

                    float nRms     = Norm(frame.Rms, maxRms);
                    float nBass    = Norm(frame.BassEnergy, maxBass);
                    float nMid     = Norm(frame.MidEnergy, maxMid);
                    float nTreble  = Norm(frame.TrebleEnergy, maxTreble);
                    float nOnset   = Norm(frame.OnsetStrength, maxOnset);
                    float nCentroid= Norm(frame.SpectrumCentroidHz, maxCentroid);

                    float rmsExp = MathF.Pow(nRms, 0.3f);

                    // RMS → PMem: tendrils curl/uncurl (-0.8 deep curl → -0.1 smooth)
                    float targetPMem = -0.8f + rmsExp * 0.7f;
                    smoothPMem += ema * (targetPMem - smoothPMem);

                    // Treble → c.x: creature shape shifts (0.2 → 0.6)
                    float targetCx = 0.2f + MathF.Pow(nTreble, 0.4f) * 0.4f;
                    smoothCx += ema * (targetCx - smoothCx);

                    // Bass → PlaneOffset: sweep cut plane to reveal internals (-0.4 → 0.4)
                    float targetPlane = (MathF.Pow(nBass, 0.4f) - 0.5f) * 0.8f;
                    smoothPlaneOff += ema * (targetPlane - smoothPlaneOff);

                    // Bass → camera distance: breathe (2.8 → 4.2)
                    float targetDist = 4.2f - MathF.Pow(nBass, 0.5f) * 1.4f;
                    smoothDist += ema * (targetDist - smoothDist);

                    // Mid → palette frequency (0.8 → 3.0)
                    float targetFreq = 0.8f + MathF.Pow(nMid, 0.4f) * 2.2f;
                    smoothFreq += ema * (targetFreq - smoothFreq);

                    // Mid → palette phase shift (0 → 0.4)
                    float targetPhase = MathF.Pow(nMid, 0.5f) * 0.4f;
                    smoothPhaseShift += ema * (targetPhase - smoothPhaseShift);

                    // Onset → light intensity: flash (1.0 → 2.2)
                    float targetIntensity = 1.0f + MathF.Pow(nOnset, 0.5f) * 1.2f;
                    smoothIntensity += ema * (targetIntensity - smoothIntensity);

                    // Centroid → light azimuth orbit
                    float azSpeed = 0.3f + nCentroid * 0.7f;
                    float azimuth = progress * MathF.Tau * 2.0f * azSpeed;

                    // Camera orbit — wider for Phoenix's extended tendrils
                    float camAngle = progress * MathF.Tau * 0.5f;
                    float camY = 1.0f + MathF.Sin(progress * MathF.PI) * 0.8f;
                    var camPos = new Vector3(
                        MathF.Cos(camAngle) * smoothDist,
                        camY,
                        MathF.Sin(camAngle) * smoothDist);
                    var cam = new Camera3D(camPos, Vector3.Zero, Vector3.UnitY,
                        MathF.PI / 4.0f, (float)w / h);

                    float lightEl = 40f * (MathF.PI / 180f);
                    float lightC = MathF.Cos(lightEl);
                    var light = Vector3.Normalize(new Vector3(
                        lightC * MathF.Cos(azimuth), MathF.Sin(lightEl), lightC * MathF.Sin(azimuth)));

                    var settings = new RaymarchSettings(
                        MaxSteps: 350, HitEpsilon: 5e-4f, MaxDistance: 20f, NormalEpsilon: 5e-4f,
                        EnableSoftShadows: true, ShadowSteps: 80, ShadowSoftness: 14f,
                        EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.1f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                        Gloss: 0f, F0: 0f, LightIntensity: smoothIntensity);

                    var palette = new PaletteParams
                    {
                        Base = new Vector3(0.5f, 0.4f, 0.35f),
                        Amp = new Vector3(0.5f, 0.45f, 0.4f),
                        Frequency = smoothFreq,
                        Phase = new Vector3(
                            0.0f  + smoothPhaseShift,
                            0.15f + smoothPhaseShift,
                            0.40f + smoothPhaseShift),
                        TrapScale = 0.7f,
                        TrapMix = new Vector3(0.6f, 0.5f, 0.2f),
                        ShellMix = 0.45f,
                    };

                    var fractal = new PhoenixParams
                    {
                        Iterations = 16,
                        C = new Vector3(smoothCx, 0.0f, 0.0f),
                        PMem = smoothPMem,
                        Bailout = 4.0f,
                        Cut = true,
                        PlaneNormal = new Vector3(0.3f, 0.5f, 0.8f),
                        PlaneOffset = smoothPlaneOff,
                        Fudge = 0.85f,
                        BoundRadius = 4.0f,
                    };

                    uint[] pixels = renderer.RenderPhoenix(fractal, cam, w, h, settings, bg, surface, light, palette);

                    var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));

                    Console.Write($"\r  frame {i + 1}/{totalFrames} mem={smoothPMem:F2} cx={smoothCx:F2} cut={smoothPlaneOff:F2} light={smoothIntensity:F1} — {renderer.LastComputeMs}ms   ");
                }
                sw.Stop();
                Console.WriteLine($"\nRendered {totalFrames} frames in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / totalFrames}ms avg)");

                // 4. Stitch with ffmpeg including audio
                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-ss {startSec} -t {durationSec} -i \"{Path.GetFullPath(wavPath)}\" " +
                    $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-audio-reactive FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-audio-deepzoom")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-audio-deepzoom requires macOS."); return 1; }
            try
            {
                string wavPath = args.Length > 1 ? args[1]
                    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
                        "Chopin - Nocturne op.9 No.2 - andrea romano (128k).wav");
                if (!File.Exists(wavPath)) { Console.Error.WriteLine($"WAV not found: {wavPath}"); return 1; }
                double startSec    = args.Length > 2 ? double.Parse(args[2]) : 0.0;
                double durationSec = args.Length > 3 ? double.Parse(args[3]) : 10.0;
                string outMp4      = args.Length > 4 ? args[4] : ResolveOutputPath("audio-deepzoom.mp4");

                int fps = 30, w = 640, h = 480;
                int totalFrames = (int)(durationSec * fps);

                // Julia set at shallow zoom — always on the direct fp64 path (Radius >> 1e-6).
                // Kappa starts near the classic hairy-Julia point (-0.7269, 0.1889) and is
                // gently swept by treble.  Radius breathes with bass.  Palette cycles with mids.
                // No reference orbit recompute overhead — EnsureReference at shallow zoom with
                // P≈50 bits and ~1000 iterations costs <1 ms.
                const double baseKappaRe = -0.7269, baseKappaIm = 0.1889;
                const double baseRadius  = 1.1;

                Console.WriteLine($"Audio-reactive deep zoom (Julia, shallow): {totalFrames} frames at {w}x{h}");

                Console.Write("  Analyzing audio... ");
                var analyzer = new Parsec.Audio.WaveAudioAnalyzer();
                var track    = analyzer.AnalyzeAsync(new Uri("file://" + Path.GetFullPath(wavPath)),
                                   null, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"{track.Frames.Count} feature frames");

                double maxRms=0, maxBass=0, maxMid=0, maxTreble=0, maxOnset=0;
                for (int i = 0; i < totalFrames; i++)
                {
                    var f = track.Sample(TimeSpan.FromSeconds(startSec + (double)i / fps));
                    maxRms    = Math.Max(maxRms,    f.Rms);
                    maxBass   = Math.Max(maxBass,   f.BassEnergy);
                    maxMid    = Math.Max(maxMid,    f.MidEnergy);
                    maxTreble = Math.Max(maxTreble, f.TrebleEnergy);
                    maxOnset  = Math.Max(maxOnset,  f.OnsetStrength);
                }
                float Norm(double val, double peak) => peak > 0 ? (float)Math.Min(val / peak, 1.0) : 0f;

                // EMA-smoothed state
                float smoothRadius    = (float)baseRadius;
                float smoothKappaRe   = (float)baseKappaRe;
                float smoothKappaIm   = (float)baseKappaIm;
                float smoothFreq      = 1.0f;
                float smoothPhase     = 0f;
                float smoothBrightness = 1f;
                const float ema = 0.15f;

                using var renderer = new MetalDeepZoomRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal deep-zoom backend not available."); return 1; }

                var bg  = new Color(0.02f, 0.02f, 0.04f);
                var settings = new RaymarchSettings { HeroSamples = 1 };

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-dz-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < totalFrames; i++)
                {
                    double t     = startSec + (double)i / fps;
                    var frame    = track.Sample(TimeSpan.FromSeconds(t));

                    float nRms    = Norm(frame.Rms,    maxRms);
                    float nBass   = Norm(frame.BassEnergy, maxBass);
                    float nMid    = Norm(frame.MidEnergy,  maxMid);
                    float nTreble = Norm(frame.TrebleEnergy, maxTreble);
                    float nOnset  = Norm(frame.OnsetStrength, maxOnset);

                    float rmsExp = MathF.Pow(nRms, 0.35f);

                    // Bass → zoom breathing: radius oscillates ±0.25 around base
                    float targetRadius = (float)baseRadius + (MathF.Pow(nBass, 0.5f) - 0.5f) * 0.5f;
                    smoothRadius += ema * (targetRadius - smoothRadius);

                    // Treble → kappa drift: slowly shifts the Julia shape
                    float targetKRe = (float)baseKappaRe + (MathF.Pow(nTreble, 0.4f) - 0.5f) * 0.12f;
                    float targetKIm = (float)baseKappaIm + rmsExp * 0.08f;
                    smoothKappaRe += ema * (targetKRe - smoothKappaRe);
                    smoothKappaIm += ema * (targetKIm - smoothKappaIm);

                    // Mids → palette frequency (0.6 → 2.5)
                    float targetFreq = 0.6f + MathF.Pow(nMid, 0.4f) * 1.9f;
                    smoothFreq += ema * (targetFreq - smoothFreq);

                    // Treble + time → palette phase rotation
                    float phaseSpeed = 0.004f + nTreble * 0.012f;
                    smoothPhase += phaseSpeed;

                    // Onset → brightness flash
                    float targetBright = 1.0f + MathF.Pow(nOnset, 0.5f) * 0.8f;
                    smoothBrightness += ema * (targetBright - smoothBrightness);

                    var palette = new PaletteParams
                    {
                        Base      = new Vector3(0.5f, 0.45f, 0.4f),
                        Amp       = new Vector3(0.45f, 0.4f, 0.35f),
                        Frequency = smoothFreq,
                        Phase     = new Vector3(smoothPhase, smoothPhase + 0.2f, smoothPhase + 0.45f),
                        TrapScale = 1.0f,
                        TrapMix   = Vector3.Zero,
                        ShellMix  = 0f,
                    };

                    var pp = new PostProcessParams
                    {
                        Brightness = smoothBrightness,
                        Contrast   = 1.05f,
                        Gamma      = 0.9f,
                        Saturation = 1.3f,
                        HdrEnabled = false,
                    };

                    var view = new DeepZoomView
                    {
                        CenterRe      = "0.0",
                        CenterIm      = "0.0",
                        Radius        = Math.Clamp(smoothRadius, 0.6, 1.8),
                        MaxIterations = 600,
                        Formula       = 2,  // Julia
                        KappaRe       = smoothKappaRe,
                        KappaIm       = smoothKappaIm,
                    };

                    uint[] pixels = renderer.RenderGraded(view, w, h, palette, bg, settings, pp);

                    var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp  = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{i:D4}.png"));

                    Console.Write($"\r  frame {i+1}/{totalFrames} r={smoothRadius:F3} k=({smoothKappaRe:F4},{smoothKappaIm:F4}) — {renderer.LastComputeMs}ms   ");
                }
                sw.Stop();
                Console.WriteLine($"\nRendered {totalFrames} frames in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds/totalFrames}ms avg)");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                    $"-ss {startSec} -t {durationSec} -i \"{Path.GetFullPath(wavPath)}\" " +
                    $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-audio-deepzoom FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
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

        if (args[0] is "gpu-surface-texture-smoke")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: parsec gpu-surface-texture-smoke <imagePath> [width] [height] [outDir]");
                return 2;
            }
            try
            {
                string imagePath = args[1];
                int w = args.Length > 2 ? int.Parse(args[2]) : 512;
                int h = args.Length > 3 ? int.Parse(args[3]) : w;
                string outDir = args.Length > 4 ? args[4] : Path.Combine(Path.GetDirectoryName(ResolveOutputPath("x"))!, "gpu-surface-texture-smoke");
                Directory.CreateDirectory(outDir);

                if (!TryLoadSurfaceTextureImage(imagePath, out var bytes, out int texW, out int texH, out int rowBytes, out var loadError))
                {
                    Console.Error.WriteLine(loadError);
                    return 1;
                }

                using var ctx = new HeadlessGLContext();
                using var pipeline = new RaymarchPipeline(ctx.Gl);
                using var renderer = new GpuMandelboxRenderer(ctx.Gl, pipeline);

                Console.WriteLine($"GPU surface-texture smoke — Mandelbox at {w}x{h}");
                Console.WriteLine(ctx.Info());
                Console.WriteLine($"  texture: {Path.GetFileName(imagePath)} ({texW}x{texH})");

                var camera = new Camera3D(
                    new Vector3(0f, 3f, 12f),
                    Vector3.Zero,
                    Vector3.UnitY,
                    MathF.PI / 4f,
                    (float)w / h);

                var fractal = new MandelboxParams();
                var settings = new RaymarchSettings();
                var palette = PaletteParams.Default;
                var bg = new Color(0.05f, 0.05f, 0.08f);
                var sf = new Color(0.6f, 0.6f, 0.6f);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                GpuSurfaceTextureManager.ClearImage();
                GpuSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f);
                uint[] baseline = renderer.RenderToBuffer(fractal, camera, w, h, settings, bg, sf, light, palette);

                GpuSurfaceTextureManager.SetImage(bytes!, texW, texH, rowBytes);
                GpuSurfaceTextureManager.SetControls(enabled: true, blend: 0.85f, scale: 1.25f);
                uint[] textured = renderer.RenderToBuffer(fractal, camera, w, h, settings, bg, sf, light, palette);

                string baselinePath = Path.Combine(outDir, "mandelbox_base.png");
                string texturedPath = Path.Combine(outDir, "mandelbox_textured.png");
                SaveUintPixels(baseline, w, h, baselinePath);
                SaveUintPixels(textured, w, h, texturedPath);

                var stats = ComparePixelBuffers(baseline, textured);
                Console.WriteLine($"  changed pixels: {stats.changedPixels}/{baseline.Length}");
                Console.WriteLine($"  mean abs channel delta: {stats.meanAbsDelta:F2}");
                Console.WriteLine($"  -> {baselinePath}");
                Console.WriteLine($"  -> {texturedPath}");
                return stats.changedPixels > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"gpu-surface-texture-smoke FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                GpuSurfaceTextureManager.ClearImage();
                GpuSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f);
            }
        }

        if (args[0] is "metal-m12-stills")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m12-stills requires macOS."); return 1; }
            try
            {
                const int W = 512, H = 512;
                var settings = new RaymarchSettings { HeroSamples = 1 };
                var bg   = new Color(0.04f, 0.04f, 0.06f);
                var sf   = new Color(0.65f, 0.62f, 0.58f);
                var light = Vector3.Normalize(new Vector3(1.2f, 2f, 1.5f));
                var pal  = PaletteParams.Default;
                var cam3 = new Camera3D(new Vector3(0f, 3f, 12f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);
                var camBulb = new Camera3D(new Vector3(0f, 0f, 2.4f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);
                var camKifs = new Camera3D(new Vector3(4f, 3f, 6f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);
                var camPhoenix = new Camera3D(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);

                // 1. Mandelbox — identity grade (default params)
                {
                    using var r = new MetalMandelboxRenderer();
                    var pp = new PostProcessParams();
                    var pixels = r.RenderMandelbox(new MandelboxParams(), cam3, W, H, settings, bg, sf, light, pal, pp);
                    var path = ResolveOutputPath("m12-mandelbox.png");
                    SaveUintPixels(pixels, W, H, path);
                    Console.WriteLine($"  Mandelbox (identity grade)   -> {path}");
                }

                // 2. Mandelbulb — boosted brightness + saturation
                {
                    using var r = new MetalMandelbulbRenderer();
                    var pp = new PostProcessParams { Brightness = 1.4f, Saturation = 1.6f, Gamma = 0.9f };
                    var pixels = r.RenderMandelbulb(new MandelbulbParams(), camBulb, W, H, settings, bg, sf, light, pal, postProcess: pp);
                    var path = ResolveOutputPath("m12-mandelbulb.png");
                    SaveUintPixels(pixels, W, H, path);
                    Console.WriteLine($"  Mandelbulb (bright+sat)      -> {path}");
                }

                // 3. Phoenix — cut off, pulled back camera to see full 3D surface
                {
                    using var r = new MetalPhoenixRenderer();
                    var ph = new PhoenixParams { Cut = false, BoundRadius = 5f };
                    var camPh2 = new Camera3D(new Vector3(0f, 1.5f, 5f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);
                    var pp = new PostProcessParams { Brightness = 1.2f, HdrEnabled = true };
                    var pixels = r.RenderPhoenix(ph, camPh2, W, H, settings, bg, sf, light, pal, pp);
                    var path = ResolveOutputPath("m12-phoenix.png");
                    SaveUintPixels(pixels, W, H, path);
                    Console.WriteLine($"  Phoenix (no cut, full shape) -> {path}");
                }

                // 4. KIFS — high contrast, desaturated
                {
                    using var r = new MetalKifsRenderer();
                    var pp = new PostProcessParams { Contrast = 1.8f, Saturation = 0.4f, Gamma = 1.3f };
                    var pixels = r.RenderKifs(new KifsParams(), camKifs, W, H, settings, bg, sf, light, pal, pp);
                    var path = ResolveOutputPath("m12-kifs.png");
                    SaveUintPixels(pixels, W, H, path);
                    Console.WriteLine($"  KIFS (hi-contrast desatured)  -> {path}");
                }

                Console.WriteLine("Done.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m12-stills FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-telemetry-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-telemetry-smoke requires macOS."); return 1; }
            try
            {
                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                // Three camera positions: outside (mostly sky), standard view, inside bound sphere.
                // M2 acceptance: closer → higher hit ratio, lower mean depth.
                var positions = new[]
                {
                    ("outside",  new Camera3D(new Vector3(0f,  5f, 30f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f)),
                    ("standard", new Camera3D(new Vector3(0f,  3f, 12f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f)),
                    ("close",    new Camera3D(new Vector3(0f,  0f,  2f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f)),
                };

                Console.WriteLine("metal-telemetry-smoke — Mandelbox telemetry pass at 3 camera positions");
                Console.WriteLine($"  {"pos",-10} {"hit",-6} {"depth",-7} {"dVar",-7} {"stepMn",-8} {"stepP90",-8} {"nVar",-7} {"trap.x",-7} ms");
                Console.WriteLine($"  {new string('-', 75)}");

                var results = new List<(string label, FractalGeometryStats stats, long ms)>();
                foreach (var (label, cam) in positions)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var stats = renderer.RunTelemetryPass(fractal, cam, settings);
                    sw.Stop();

                    if (stats == null)
                    {
                        Console.Error.WriteLine($"  {label,-10} FAIL: RunTelemetryPass returned null");
                        return 1;
                    }

                    var s = stats.Value;
                    Console.WriteLine($"  {label,-10} {s.HitRatio:F3}  {s.MeanDepth,6:F2}  {s.DepthVariance,6:F2}  {s.StepMean,7:F1}  {s.StepP90,7:F0}  {s.NormalVariance,6:F3}  {s.TrapMean.X,6:F3}  {sw.ElapsedMilliseconds}ms");
                    results.Add((label, s, sw.ElapsedMilliseconds));
                }

                // Assertions: closer camera → higher hit ratio and lower mean depth.
                var outside  = results[0].stats;
                var standard = results[1].stats;
                var close    = results[2].stats;

                int failures = 0;
                void Assert(bool cond, string msg)
                {
                    if (!cond) { Console.Error.WriteLine($"  FAIL: {msg}"); failures++; }
                    else         Console.WriteLine($"  PASS: {msg}");
                }

                Console.WriteLine();
                Assert(standard.HitRatio > outside.HitRatio,
                    $"standard.HitRatio ({standard.HitRatio:F3}) > outside.HitRatio ({outside.HitRatio:F3})");
                Assert(close.HitRatio > outside.HitRatio,
                    $"close.HitRatio ({close.HitRatio:F3}) > outside.HitRatio ({outside.HitRatio:F3})");
                Assert(close.MeanDepth < standard.MeanDepth || close.HitRatio > 0.95f,
                    $"close.MeanDepth ({close.MeanDepth:F2}) < standard.MeanDepth ({standard.MeanDepth:F2}) (or close hits >95%)");

                // Sanity: all floats are finite (no NaN/Inf from broken struct readback)
                bool allFinite =
                    float.IsFinite(standard.HitRatio) && float.IsFinite(standard.MeanDepth) &&
                    float.IsFinite(standard.StepMean) && float.IsFinite(standard.NormalVariance) &&
                    float.IsFinite(standard.TrapMean.X);
                Assert(allFinite, "all standard-view stats are finite (no NaN/Inf)");

                Console.WriteLine();
                return failures == 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-telemetry-smoke FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m6-telemetry")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m6-telemetry requires macOS."); return 1; }
            Console.WriteLine("metal-m6-telemetry — telemetry smoke test for Mandelbulb, Kleinian, BurningShip");
            var previewSettings = new RaymarchSettings(
                MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                Gloss: 0f, F0: 0f, LightIntensity: 1f);
            try
            {
                // Mandelbulb
                {
                    using var r = new MetalMandelbulbRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("Mandelbulb Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    var statsN = r.RunTelemetryPass(new MandelbulbParams(), cam, previewSettings);
                    if (statsN == null) { Console.Error.WriteLine("Mandelbulb telemetry returned null"); return 1; }
                    var s1 = statsN.Value;
                    Console.WriteLine($"  Mandelbulb  hit={s1.HitRatio:F3} depth={s1.MeanDepth:F2} stepP90={s1.StepP90:F1} cells={s1.Cells?.Length ?? 0}");
                }
                // Kleinian
                {
                    using var r = new MetalKleinianRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("Kleinian Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 0f, 6f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    var statsN = r.RunTelemetryPass(new KleinianParams(), cam, previewSettings);
                    if (statsN == null) { Console.Error.WriteLine("Kleinian telemetry returned null"); return 1; }
                    var s2 = statsN.Value;
                    Console.WriteLine($"  Kleinian    hit={s2.HitRatio:F3} depth={s2.MeanDepth:F2} stepP90={s2.StepP90:F1} cells={s2.Cells?.Length ?? 0}");
                }
                // BurningShip
                {
                    using var r = new MetalBurningShipRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("BurningShip Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    var statsN = r.RunTelemetryPass(new BurningShipParams(), cam, previewSettings);
                    if (statsN == null) { Console.Error.WriteLine("BurningShip telemetry returned null"); return 1; }
                    var s3 = statsN.Value;
                    Console.WriteLine($"  BurningShip hit={s3.HitRatio:F3} depth={s3.MeanDepth:F2} stepP90={s3.StepP90:F1} cells={s3.Cells?.Length ?? 0}");
                }
                Console.WriteLine("All M6 telemetry shaders compiled and returned stats.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m6-telemetry FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-sonify-drone")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-sonify-drone requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 8.0;
                string outPath  = args.Length >= 3 ? args[2] : ResolveOutputPath("fractal_drone.wav");

                const double controlHz  = 30.0;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-sonify-drone — Mandelbox fly-in {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} telemetry frames)");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                // Fly-in: from far outside to a close-ish view, looking at origin
                var startPos = new Vector3(0f, 5f, 30f);
                var endPos   = new Vector3(0f, 1.5f, 7f);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                var frames = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos = startPos;

                Console.WriteLine($"  {"fi",-5}  {"hit",-6} {"depth",-6} {"sP90",-6} {"nVar",-6} {"spd",-5}");
                Console.WriteLine($"  {new string('-', 45)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    var   pos = Vector3.Lerp(startPos, endPos, t);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats    = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    frames.Add(new FractalSonicFrame(
                        Time:              fi / controlHz,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f));

                    // Print a row approximately every second
                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var f = frames[^1];
                        Console.WriteLine($"  {fi,4}   {f.HitRatio:F3}  {f.MeanDepth,5:F1}  {f.StepP90,4:F0}  {f.NormalVariance:F3}  {f.CameraSpeed:F2}");
                    }
                }

                Console.WriteLine($"\n  Synthesizing {FractalDroneSynth.DefaultSampleRate} Hz WAV ({totalFrames} frames)...");
                var sw  = System.Diagnostics.Stopwatch.StartNew();
                var pcm = FractalDroneSynth.Synthesize(frames, controlHz);
                sw.Stop();

                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WavEncoder.Write(outPath, pcm, FractalDroneSynth.DefaultSampleRate);

                long   fileBytes = new FileInfo(outPath).Length;
                int    peakSamp  = pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 0;
                double peakDb    = 20.0 * Math.Log10(peakSamp / 32767.0 + 1e-10);
                Console.WriteLine($"  synthesis {sw.ElapsedMilliseconds} ms | peak {peakDb:F1} dBFS | {fileBytes / 1024} KB");
                Console.WriteLine($"\nOK → {outPath}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-sonify-drone FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m5-cells")
        {
            // Dump the 4×4 spatial cell array from one frame at a mid-fly-in camera position.
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m5-cells requires macOS."); return 1; }
            try
            {
                var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var fractal = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 80, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0.1f, AOIntensity: 0f,
                    LightIntensity: 1f, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f);

                // Camera mid-fly-in: (0, 2.5, 14) looking at origin
                var cam = new Camera3D(new Vector3(0f, 2.5f, 14f), Vector3.Zero, Vector3.UnitY,
                    MathF.PI / 5f, 640f / 480f);

                var stats = renderer.RunTelemetryPass(fractal, cam, settings);
                if (stats == null) { Console.Error.WriteLine("Telemetry returned null."); return 1; }

                Console.WriteLine($"Global: hit={stats.Value.HitRatio:F3} depth={stats.Value.MeanDepth:F1} nVar={stats.Value.NormalVariance:F3}");
                Console.WriteLine();
                Console.WriteLine("4×4 spatial cells (row-major, ty=0 is top of screen):");
                Console.WriteLine($"  {"idx",-4} {"ty",3} {"tx",3}  {"hit",5}  {"depth",6}  {"energy",7}  {"step%",6}  {"trap.x",7}  WorldPos");
                Console.WriteLine(new string('-', 85));

                var cells = stats.Value.Cells;
                if (cells != null)
                {
                    for (int i = 0; i < cells.Length; i++)
                    {
                        int ty2 = i / 4, tx2 = i % 4;
                        var c = cells[i];
                        Console.WriteLine($"  {i,-4} {ty2,3} {tx2,3}  {c.HitRatio,5:F3}  {c.MeanDepth,6:F2}  {c.Energy,7:F4}  {c.StepComplexity,6:F3}  {c.TrapMean.X,7:F4}  ({c.WorldPosition.X:F2},{c.WorldPosition.Y:F2},{c.WorldPosition.Z:F2})");
                    }
                }
                else
                {
                    Console.WriteLine("  (no cells returned)");
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m5-cells FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m6-orbit")
        {
            // Orbit animation: camera sweeps 360° around each fractal at a fixed radius
            // with a sinusoidal up/down bob.  Produces non-monotonic telemetry values,
            // testing whether the DSP responds interestingly to varied geometry input.
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m6-orbit requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d6) ? d6 : 10.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "m6-orbit");
                Directory.CreateDirectory(outDir);

                const int fps = 30;
                const int w   = 640;
                const int h   = 480;
                int totalFrames = (int)Math.Round(duration * fps);
                var post = new PostProcessParams { Brightness = 1.05f, Contrast = 1.1f, Saturation = 1.2f, Gamma = 2.2f };
                var bg   = new Color(0.02f, 0.02f, 0.06f);
                const float fov    = MathF.PI / 4f;
                const float aspect = (float)w / h;

                Console.WriteLine($"metal-m6-orbit — 360° orbit clips ({duration:F1}s each @ {fps} fps, {w}x{h})");

                // Helper: render one orbit clip
                static int RenderOrbit(
                    string label, FractalVoice voice, string mp4,
                    int fps_, int totalFrames_, int w_, int h_, float fov_, float aspect_,
                    Color bg_, PostProcessParams post_,
                    float orbitR, float yAmp, float yFreqMult,
                    Color surface, Vector3 light, PaletteParams palette,
                    RaymarchSettings settings,
                    Func<int, Vector3, float[], uint[]> render,
                    Func<object, Camera3D, RaymarchSettings, FractalGeometryStats?> telemetry,
                    object fractalParams)
                {
                    var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-orbit-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(frameDir);
                    var sonicFrames = new List<FractalSonicFrame>(totalFrames_);
                    Vector3 prevPos = Vector3.Zero;
                    bool firstFrame = true;

                    for (int fi = 0; fi < totalFrames_; fi++)
                    {
                        float t = fi / (float)totalFrames_;  // 0→1 over clip
                        float theta = t * 2f * MathF.PI;    // full revolution
                        float y = yAmp * MathF.Sin(t * yFreqMult * 2f * MathF.PI);
                        var pos = new Vector3(orbitR * MathF.Sin(theta), y, orbitR * MathF.Cos(theta));
                        var cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov_, aspect_);

                        uint[] pixels = render(fi, pos, new float[0]);
                        var info = new SKImageInfo(w_, h_, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp  = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();

                        var stats   = telemetry(fractalParams, cam, settings);
                        float camSpd = firstFrame ? 0f : (pos - prevPos).Length() * fps_;
                        prevPos = pos; firstFrame = false;

                        sonicFrames.Add(new FractalSonicFrame(Time: fi/(double)fps_,
                            HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                            DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                            StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f, TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero, CameraSpeed: camSpd, ParameterVelocity: 0f));

                        if (fi % fps_ == 0 || fi == totalFrames_ - 1)
                        {
                            var sf = sonicFrames[^1];
                            Console.WriteLine($"  frame {fi,4}/{totalFrames_}  θ={theta * 180f / MathF.PI,6:F1}°  hit={sf.HitRatio:F3}  sP90={sf.StepP90:F0}  trap=({sf.TrapMean.X:F2},{sf.TrapMean.Y:F2},{sf.TrapMean.Z:F2},{sf.TrapMean.W:F2})");
                        }
                        else Console.Write($"\r  frame {fi+1}/{totalFrames_}");
                    }

                    Console.Write($"  Synthesising {label}...");
                    var pcm = FractalDroneSynth.Synthesize(sonicFrames, fps_, voice: voice);
                    string wavPath = Path.Combine(frameDir, "drone.wav");
                    WavEncoder.Write(wavPath, pcm, FractalDroneSynth.DefaultSampleRate);
                    Console.WriteLine(" done.");

                    string ffArgs = $"-y -framerate {fps_} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                    $"-i \"{wavPath}\" -c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{mp4}\"";
                    Console.Write("  ffmpeg...");
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Directory.Delete(frameDir, recursive: true);
                    Console.WriteLine($" OK → {mp4}");
                    return 0;
                }

                // ---- 1. Mandelbulb orbit ----
                Console.WriteLine("\n[1/3] Mandelbulb — orbit r=4, bob 1.5y, 1 revolution");
                {
                    using var r = new MetalMandelbulbRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }
                    var fractal = new MandelbulbParams();
                    var settings = new RaymarchSettings(MaxSteps: 128, HitEpsilon: 1.5e-3f, MaxDistance: 30f, NormalEpsilon: 2e-3f,
                        EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 10f,
                        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 0.9f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);
                    var surface = new Color(0.45f, 0.55f, 0.75f);
                    var light   = Vector3.Normalize(new Vector3(1.0f, 2.0f, 1.5f));
                    var palette = PaletteParams.Default;

                    int res = RenderOrbit("Mandelbulb", FractalVoice.Mandelbulb,
                        Path.Combine(outDir, "mandelbulb_orbit.mp4"),
                        fps, totalFrames, w, h, fov, aspect, bg, post,
                        orbitR: 4.0f, yAmp: 1.5f, yFreqMult: 1.5f,
                        surface, light, palette, settings,
                        (fi, pos, _) => {
                            var cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                            return r.RenderMandelbulb(fractal, cam, w, h, settings, bg, surface, light, palette, postProcess: post);
                        },
                        (fp, cam, s) => r.RunTelemetryPass((MandelbulbParams)fp, cam, s),
                        fractal);
                    if (res != 0) return res;
                }

                // ---- 2. Kleinian orbit ----
                Console.WriteLine("\n[2/3] Kleinian — orbit r=10, bob 3y, 1 revolution");
                {
                    using var r = new MetalKleinianRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }
                    var fractal = new KleinianParams();
                    var settings = new RaymarchSettings(MaxSteps: 200, HitEpsilon: 2e-3f, MaxDistance: 50f, NormalEpsilon: 3e-3f,
                        EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 8f,
                        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.06f, AOIntensity: 0.95f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.1f);
                    var surface = new Color(0.60f, 0.50f, 0.40f);
                    var light   = Vector3.Normalize(new Vector3(0.8f, 1.5f, 1.2f));
                    var palette = PaletteParams.Default;

                    int res = RenderOrbit("Kleinian", FractalVoice.Kleinian,
                        Path.Combine(outDir, "kleinian_orbit.mp4"),
                        fps, totalFrames, w, h, fov, aspect, bg, post,
                        orbitR: 10.0f, yAmp: 3.0f, yFreqMult: 2.0f,
                        surface, light, palette, settings,
                        (fi, pos, _) => {
                            var cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                            return r.RenderKleinian(fractal, cam, w, h, settings, bg, surface, light, palette, post);
                        },
                        (fp, cam, s) => r.RunTelemetryPass((KleinianParams)fp, cam, s),
                        fractal);
                    if (res != 0) return res;
                }

                // ---- 3. BurningShip orbit ----
                Console.WriteLine("\n[3/3] BurningShip — orbit r=5, bob 1y, 1.5 revolutions");
                {
                    using var r = new MetalBurningShipRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }
                    var fractal = new BurningShipParams();
                    var settings = new RaymarchSettings(MaxSteps: 128, HitEpsilon: 1.5e-3f, MaxDistance: 30f, NormalEpsilon: 2e-3f,
                        EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 10f,
                        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 0.9f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);
                    var surface = new Color(0.70f, 0.45f, 0.25f);
                    var light   = Vector3.Normalize(new Vector3(1.5f, 2.0f, 1.0f));
                    var palette = PaletteParams.Default;

                    int res = RenderOrbit("BurningShip", FractalVoice.BurningShip,
                        Path.Combine(outDir, "burningship_orbit.mp4"),
                        fps, totalFrames, w, h, fov, aspect, bg, post,
                        orbitR: 5.0f, yAmp: 1.0f, yFreqMult: 3.0f,
                        surface, light, palette, settings,
                        (fi, pos, _) => {
                            var cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                            return r.RenderBurningShip(fractal, cam, w, h, settings, bg, surface, light, palette, post);
                        },
                        (fp, cam, s) => r.RunTelemetryPass((BurningShipParams)fp, cam, s),
                        fractal);
                    if (res != 0) return res;
                }

                Console.WriteLine($"\nAll 3 orbit clips written to {outDir}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m6-orbit FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m6-clip")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m6-clip requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d6) ? d6 : 6.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "m6-clips");
                Directory.CreateDirectory(outDir);

                const int fps = 30;
                const int w   = 640;
                const int h   = 480;
                int totalFrames = (int)Math.Round(duration * fps);
                var post = new PostProcessParams { Brightness = 1.05f, Contrast = 1.1f, Saturation = 1.2f, Gamma = 2.2f };
                var bg   = new Color(0.02f, 0.02f, 0.06f);
                const float fov    = MathF.PI / 4f;
                const float aspect = (float)w / h;

                // --- clip definition: (name, voice, renderer-factory, render-func, startPos, endPos) ---
                // Each uses its own renderer to avoid interleaved Metal state.

                Console.WriteLine($"metal-m6-clip — rendering 3 fractal voice clips ({duration:F1}s each @ {fps} fps, {w}x{h})");

                // 1. Mandelbulb — additive pad voice
                {
                    Console.WriteLine("\n[1/3] Mandelbulb (FractalVoice.Mandelbulb)");
                    using var r = new MetalMandelbulbRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("  Metal unavailable."); return 1; }
                    var fractal  = new MandelbulbParams();
                    var settings = new RaymarchSettings(MaxSteps: 128, HitEpsilon: 1.5e-3f, MaxDistance: 30f, NormalEpsilon: 2e-3f,
                        EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 10f,
                        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 0.9f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);
                    var surface = new Color(0.45f, 0.55f, 0.75f);
                    var light   = Vector3.Normalize(new Vector3(1.0f, 2.0f, 1.5f));
                    var palette = PaletteParams.Default;
                    var startPos = new Vector3(0f, 0f, 4.0f);
                    var endPos   = new Vector3(0f, 0f, 2.2f);

                    var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m6-bulb-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(frameDir);
                    var sonicFrames = new List<FractalSonicFrame>(totalFrames);
                    Vector3 prevPos = startPos;

                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        var   pos = Vector3.Lerp(startPos, endPos, t);
                        var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                        uint[] pixels = r.RenderMandelbulb(fractal, cam, w, h, settings, bg, surface, light, palette, postProcess: post);
                        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp  = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();
                        var stats   = r.RunTelemetryPass(fractal, cam, settings);
                        float camSpd = (pos - prevPos).Length() * fps;
                        prevPos = pos;
                        sonicFrames.Add(new FractalSonicFrame(Time: fi/(double)fps,
                            HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                            DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                            StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f, TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero, CameraSpeed: camSpd, ParameterVelocity: 0f));
                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  frame {fi,4}/{totalFrames}  hit={sonicFrames[^1].HitRatio:F3}  depth={sonicFrames[^1].MeanDepth:F1}  sP90={sonicFrames[^1].StepP90:F0}");
                        else Console.Write($"\r  frame {fi+1}/{totalFrames}");
                    }

                    Console.Write("  Synthesising Mandelbulb drone...");
                    var pcm = FractalDroneSynth.Synthesize(sonicFrames, fps, voice: FractalVoice.Mandelbulb);
                    string wavPath = Path.Combine(frameDir, "drone.wav");
                    WavEncoder.Write(wavPath, pcm, FractalDroneSynth.DefaultSampleRate);
                    Console.WriteLine(" done.");

                    string mp4 = Path.Combine(outDir, "mandelbulb_voice.mp4");
                    string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                    $"-i \"{wavPath}\" -c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{mp4}\"";
                    Console.Write("  ffmpeg...");
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Directory.Delete(frameDir, recursive: true);
                    Console.WriteLine($" OK → {mp4}");
                }

                // 2. Kleinian — dark cavity voice
                {
                    Console.WriteLine("\n[2/3] Kleinian (FractalVoice.Kleinian)");
                    using var r = new MetalKleinianRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("  Metal unavailable."); return 1; }
                    var fractal  = new KleinianParams();
                    var settings = new RaymarchSettings(MaxSteps: 200, HitEpsilon: 2e-3f, MaxDistance: 50f, NormalEpsilon: 3e-3f,
                        EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 8f,
                        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.06f, AOIntensity: 0.95f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.1f);
                    var surface = new Color(0.60f, 0.50f, 0.40f);
                    var light   = Vector3.Normalize(new Vector3(0.8f, 1.5f, 1.2f));
                    var palette = PaletteParams.Default;
                    var startPos = new Vector3(0f, 0f, 10.0f);
                    var endPos   = new Vector3(0f, 0f, 4.5f);

                    var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m6-kleinian-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(frameDir);
                    var sonicFrames = new List<FractalSonicFrame>(totalFrames);
                    Vector3 prevPos = startPos;

                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        var   pos = Vector3.Lerp(startPos, endPos, t);
                        var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                        uint[] pixels = r.RenderKleinian(fractal, cam, w, h, settings, bg, surface, light, palette, post);
                        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp  = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();
                        var stats   = r.RunTelemetryPass(fractal, cam, settings);
                        float camSpd = (pos - prevPos).Length() * fps;
                        prevPos = pos;
                        sonicFrames.Add(new FractalSonicFrame(Time: fi/(double)fps,
                            HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                            DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                            StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f, TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero, CameraSpeed: camSpd, ParameterVelocity: 0f));
                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  frame {fi,4}/{totalFrames}  hit={sonicFrames[^1].HitRatio:F3}  depth={sonicFrames[^1].MeanDepth:F1}  sP90={sonicFrames[^1].StepP90:F0}");
                        else Console.Write($"\r  frame {fi+1}/{totalFrames}");
                    }

                    Console.Write("  Synthesising Kleinian drone...");
                    var pcm = FractalDroneSynth.Synthesize(sonicFrames, fps, voice: FractalVoice.Kleinian);
                    string wavPath = Path.Combine(frameDir, "drone.wav");
                    WavEncoder.Write(wavPath, pcm, FractalDroneSynth.DefaultSampleRate);
                    Console.WriteLine(" done.");

                    string mp4 = Path.Combine(outDir, "kleinian_voice.mp4");
                    string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                    $"-i \"{wavPath}\" -c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{mp4}\"";
                    Console.Write("  ffmpeg...");
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Directory.Delete(frameDir, recursive: true);
                    Console.WriteLine($" OK → {mp4}");
                }

                // 3. BurningShip — bright crackle voice
                {
                    Console.WriteLine("\n[3/3] BurningShip (FractalVoice.BurningShip)");
                    using var r = new MetalBurningShipRenderer();
                    if (!r.IsAvailable) { Console.Error.WriteLine("  Metal unavailable."); return 1; }
                    var fractal  = new BurningShipParams();
                    var settings = new RaymarchSettings(MaxSteps: 128, HitEpsilon: 1.5e-3f, MaxDistance: 30f, NormalEpsilon: 2e-3f,
                        EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 10f,
                        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 0.9f,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);
                    var surface = new Color(0.70f, 0.45f, 0.25f);
                    var light   = Vector3.Normalize(new Vector3(1.5f, 2.0f, 1.0f));
                    var palette = PaletteParams.Default;
                    var startPos = new Vector3(0f, 0f, 5.0f);
                    var endPos   = new Vector3(0f, 0f, 2.8f);

                    var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m6-ship-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(frameDir);
                    var sonicFrames = new List<FractalSonicFrame>(totalFrames);
                    Vector3 prevPos = startPos;

                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        var   pos = Vector3.Lerp(startPos, endPos, t);
                        var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                        uint[] pixels = r.RenderBurningShip(fractal, cam, w, h, settings, bg, surface, light, palette, post);
                        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp  = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();
                        var stats   = r.RunTelemetryPass(fractal, cam, settings);
                        float camSpd = (pos - prevPos).Length() * fps;
                        prevPos = pos;
                        sonicFrames.Add(new FractalSonicFrame(Time: fi/(double)fps,
                            HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                            DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                            StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f, TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero, CameraSpeed: camSpd, ParameterVelocity: 0f));
                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  frame {fi,4}/{totalFrames}  hit={sonicFrames[^1].HitRatio:F3}  depth={sonicFrames[^1].MeanDepth:F1}  sP90={sonicFrames[^1].StepP90:F0}");
                        else Console.Write($"\r  frame {fi+1}/{totalFrames}");
                    }

                    Console.Write("  Synthesising BurningShip drone...");
                    var pcm = FractalDroneSynth.Synthesize(sonicFrames, fps, voice: FractalVoice.BurningShip);
                    string wavPath = Path.Combine(frameDir, "drone.wav");
                    WavEncoder.Write(wavPath, pcm, FractalDroneSynth.DefaultSampleRate);
                    Console.WriteLine(" done.");

                    string mp4 = Path.Combine(outDir, "burningship_voice.mp4");
                    string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                    $"-i \"{wavPath}\" -c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{mp4}\"";
                    Console.Write("  ffmpeg...");
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Directory.Delete(frameDir, recursive: true);
                    Console.WriteLine($" OK → {mp4}");
                }

                Console.WriteLine($"\nAll 3 clips written to {outDir}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m6-clip FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-sonify-clip")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-sonify-clip requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 8.0;
                string outMp4   = args.Length >= 3 ? args[2] : ResolveOutputPath("fractal_drone.mp4");

                const int    fps    = 30;
                const int    w      = 640;
                const int    h      = 480;
                int totalFrames     = (int)Math.Round(duration * fps);

                Console.WriteLine($"metal-sonify-clip — Mandelbox fly-in {duration:F1}s @ {fps} fps ({totalFrames} frames, {w}x{h})");
                Console.WriteLine("  Renders each frame + telemetry pass; drone audio is synthesised from the geometry.");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: true,  ShadowSteps: 40, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.06f, AOIntensity: 0.9f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.3f);

                var bg      = new Color(0.02f, 0.02f, 0.06f);
                var surface = new Color(0.55f, 0.62f, 0.75f);
                var light   = Vector3.Normalize(new Vector3(1.2f, 2f, 1.5f));
                var palette = PaletteParams.Default;
                var post    = new PostProcessParams { Brightness = 1.05f, Contrast = 1.1f,
                                                    Saturation = 1.2f, Gamma = 2.2f };

                var startPos = new Vector3(0f, 5f, 30f);
                var endPos   = new Vector3(0f, 1.5f, 7f);
                const float fov    = MathF.PI / 4f;
                const float aspect = (float)w / h;

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-sonify-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sonicFrames = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos = startPos;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-6} {"depth",-6} {"sP90",-5} {"nVar",-6} {"spd",-5}  render ms");
                Console.WriteLine($"  {new string('-', 55)}");

                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    var   pos = Vector3.Lerp(startPos, endPos, t);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    // Render frame
                    uint[] pixels = renderer.RenderMandelbox(fractal, cam, w, h, settings, bg, surface, light, palette, post);

                    // Save PNG
                    var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp  = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                    bmp.Dispose();

                    // Telemetry pass (separate low-res kernel — same camera position)
                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * fps;
                    prevPos = pos;

                    sonicFrames.Add(new FractalSonicFrame(
                        Time:              fi / (double)fps,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f));

                    if (fi % fps == 0 || fi == totalFrames - 1)
                    {
                        var sf = sonicFrames[^1];
                        Console.WriteLine($"  {fi,4}   {sf.HitRatio:F3}  {sf.MeanDepth,5:F1}  {sf.StepP90,4:F0}  {sf.NormalVariance:F3}  {sf.CameraSpeed:F2}  {renderer.LastComputeMs}ms");
                    }
                    else
                    {
                        Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }
                }

                totalSw.Stop();
                Console.WriteLine($"\n  {totalFrames} frames in {totalSw.ElapsedMilliseconds} ms ({totalSw.ElapsedMilliseconds / totalFrames} ms avg)");

                // Synthesise WAV from telemetry frames (1 sonic frame per video frame → controlRate = fps)
                Console.Write($"\n  Synthesising {FractalDroneSynth.DefaultSampleRate} Hz WAV from {sonicFrames.Count} geometry frames...");
                var synthSw  = System.Diagnostics.Stopwatch.StartNew();
                var pcm      = FractalDroneSynth.Synthesize(sonicFrames, fps);
                synthSw.Stop();

                string wavPath = Path.Combine(frameDir, "drone.wav");
                WavEncoder.Write(wavPath, pcm, FractalDroneSynth.DefaultSampleRate);
                int peakSamp = pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 0;
                double peakDb = 20.0 * Math.Log10(peakSamp / 32767.0 + 1e-10);
                Console.WriteLine($" {synthSw.ElapsedMilliseconds} ms | peak {peakDb:F1} dBFS");

                // Mux with ffmpeg
                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                $"-i \"{wavPath}\" " +
                                $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                Console.Write("  Running ffmpeg...");
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                var fi2   = new FileInfo(outMp4);
                Console.WriteLine($" done.\nOK → {outMp4}  ({fi2.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-sonify-clip FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-hybrid-hero")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-hybrid-hero requires macOS."); return 1; }
            try
            {
                int w = args.Length > 1 ? int.Parse(args[1]) : 1280;
                int h = args.Length > 2 ? int.Parse(args[2]) : 720;

                // IQ cosine palette: col = Base + Amp * cos(2π*(freq*t + Phase))
                // Derived analytically:
                //   t≈0.0  → copper  (0.82, 0.62, 0.16) — R peaks, B troughs (Phase_B = 0.5)
                //   t≈0.5  → teal    (0.18, 0.41, 0.60) — R troughs, B peaks
                //   Base_R = (copper_R + teal_R)/2 = 0.50, Amp_R = 0.32, Phase_R = 0
                //   Base_G = 0.46,  Amp_G = 0.12, Phase_G = 0.18  (muted centre channel)
                //   Base_B = 0.38,  Amp_B = 0.22, Phase_B = 0.50  (inverted vs R)
                var palCopperTeal = new PaletteParams
                {
                    Base      = new Vector3(0.50f, 0.46f, 0.38f),
                    Amp       = new Vector3(0.32f, 0.12f, 0.22f),
                    Phase     = new Vector3(0.00f, 0.18f, 0.50f),
                    Frequency = 0.28f,
                    TrapScale = 0.50f,
                    TrapMix   = new Vector3(0.80f, 0.25f, 0.10f),
                    ShellMix  = 0.50f,
                };

                var bg    = new Color(0.02f, 0.03f, 0.08f);
                var sf    = new Color(0.55f, 0.52f, 0.48f);
                var light = Vector3.Normalize(new Vector3(-1.2f, 2.0f, 1.8f));

                var settings = new RaymarchSettings(
                    MaxSteps:               256,
                    HitEpsilon:             5e-6f,
                    MaxDistance:            30f,
                    NormalEpsilon:          5e-6f,
                    EnableSoftShadows:      true,
                    ShadowSteps:            48,
                    ShadowSoftness:         12f,
                    EnableAmbientOcclusion: true,
                    AOSamples:              6,
                    AOStepDistance:         0.04f,
                    AOIntensity:            1.2f,
                    HeroSamples:            16,
                    EnableReflections:      false,
                    ReflectionBounces:      0,
                    Gloss:                  0f,
                    F0:                     0f,
                    LightIntensity:         1.0f);

                var pp = new PostProcessParams
                {
                    Brightness = 1.15f,
                    Contrast   = 1.40f,
                    Saturation = 1.35f,
                    Gamma      = 0.90f,
                    HdrEnabled = true,
                };

                using var renderer = new MetalHybridRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                // Shot 1: Power=4 — classical cog patterns, diagonal front view
                {
                    var hp = new HybridParams
                    {
                        Iterations  = 12,
                        Scale       = -1.9f,
                        MinRadius   = 0.45f,
                        FixedRadius = 1.0f,
                        FoldLimit   = 1.0f,
                        Power       = 4.0f,
                        Rotation    = new Vector3(0.14f, 0.10f, 0.06f),
                        Fudge       = 0.55f,
                        BoundRadius = 4.0f,
                    };
                    var cam = new Camera3D(
                        new Vector3(-1.2f, 1.1f, 4.5f),
                        new Vector3(-0.1f, 0.1f, 0f),
                        Vector3.UnitY,
                        MathF.PI / 4.2f,
                        (float)w / h);
                    Console.Write($"Shot 1/3 — Power4 diagonal [{w}×{h} @ 16×SSAA] ...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pixels = renderer.RenderHybrid(hp, cam, w, h, settings, bg, sf, light, palCopperTeal, pp);
                    sw.Stop();
                    Console.WriteLine($" {sw.ElapsedMilliseconds} ms  (compute {renderer.LastComputeMs} ms)");
                    var path = ResolveOutputPath("hybrid-hero-1.png");
                    SaveUintPixels(pixels, w, h, path);
                    Console.WriteLine($"  -> {path}");
                }

                // Shot 2: Power=6 — tighter ring patterns, looking from below
                {
                    var hp = new HybridParams
                    {
                        Iterations  = 10,
                        Scale       = -2.0f,
                        MinRadius   = 0.40f,
                        FixedRadius = 1.0f,
                        FoldLimit   = 1.05f,
                        Power       = 6.0f,
                        Rotation    = new Vector3(0.10f, 0.08f, 0.05f),
                        Fudge       = 0.55f,
                        BoundRadius = 4.0f,
                    };
                    var cam = new Camera3D(
                        new Vector3(0.5f, -1.8f, 4.5f),
                        new Vector3(0.1f, 0.2f, 0f),
                        Vector3.UnitY,
                        MathF.PI / 4f,
                        (float)w / h);
                    Console.Write($"Shot 2/3 — Power6 from-below [{w}×{h} @ 16×SSAA] ...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pixels = renderer.RenderHybrid(hp, cam, w, h, settings, bg, sf, light, palCopperTeal, pp);
                    sw.Stop();
                    Console.WriteLine($" {sw.ElapsedMilliseconds} ms  (compute {renderer.LastComputeMs} ms)");
                    var path = ResolveOutputPath("hybrid-hero-2.png");
                    SaveUintPixels(pixels, w, h, path);
                    Console.WriteLine($"  -> {path}");
                }

                // Shot 3: Power=8 — maximum petal count, tight equatorial macro
                {
                    var hp = new HybridParams
                    {
                        Iterations  = 10,
                        Scale       = -1.85f,
                        MinRadius   = 0.50f,
                        FixedRadius = 1.0f,
                        FoldLimit   = 0.95f,
                        Power       = 8.0f,
                        Rotation    = new Vector3(0.12f, 0.09f, 0.04f),
                        Fudge       = 0.50f,
                        BoundRadius = 4.0f,
                    };
                    var cam = new Camera3D(
                        new Vector3(-1.8f, 0.2f, 3.8f),
                        new Vector3(-0.3f, 0.0f, 0f),
                        Vector3.UnitY,
                        MathF.PI / 5f,
                        (float)w / h);
                    Console.Write($"Shot 3/3 — Power8 tight macro [{w}×{h} @ 16×SSAA] ...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pixels = renderer.RenderHybrid(hp, cam, w, h, settings, bg, sf, light, palCopperTeal, pp);
                    sw.Stop();
                    Console.WriteLine($" {sw.ElapsedMilliseconds} ms  (compute {renderer.LastComputeMs} ms)");
                    var path = ResolveOutputPath("hybrid-hero-3.png");
                    SaveUintPixels(pixels, w, h, path);
                    Console.WriteLine($"  -> {path}");
                }

                Console.WriteLine("Done.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-hybrid-hero FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "m7a-check")
        {
            try
            {
                Console.WriteLine("M7a — JI/temperament quantizer check");
                Console.WriteLine();

                var testFreqs = new float[] { 55f, 82.4f, 110f, 164.8f, 220f, 293.7f, 330f, 440f, 587f, 880f };

                foreach (var scale in JiScale.All)
                {
                    var q = new JiQuantizer(scale, rootHz: 220f);
                    Console.WriteLine($"Scale: {scale.Name}  (root 220 Hz, ratios: {string.Join(" ", scale.Ratios.Select(r => r.ToString("F4")))})");
                    Console.WriteLine($"  {"f_in",8}  {"f_snapped",10}  {"t=1.0",8}  {"t=0.5",8}  {"t=0.0",8}");

                    foreach (float f in testFreqs)
                    {
                        float snapped = q.Snap(f);
                        q.TemperamentStrength = 1.0f; float t1 = q.Quantize(f);
                        q.TemperamentStrength = 0.5f; float t5 = q.Quantize(f);
                        q.TemperamentStrength = 0.0f; float t0 = q.Quantize(f);
                        Console.WriteLine($"  {f,8:F1} Hz  {snapped,8:F2} Hz  {t1,6:F2} Hz  {t5,6:F2} Hz  {t0,6:F2} Hz");
                    }
                    Console.WriteLine();
                }

                // Verify lerp endpoints
                var qv = new JiQuantizer(JiScale.Pentatonic, rootHz: 220f);
                float probe = 300f;
                qv.TemperamentStrength = 1.0f; float atOne = qv.Quantize(probe);
                qv.TemperamentStrength = 0.0f; float atZero = qv.Quantize(probe);
                bool passthrough = MathF.Abs(atZero - probe) < 0.01f;
                bool exactSnap = MathF.Abs(atOne - qv.Snap(probe)) < 0.01f;
                Console.WriteLine($"Lerp check (f={probe} Hz):  t=0.0 → {atZero:F3} Hz (passthrough: {passthrough})");
                Console.WriteLine($"                             t=1.0 → {atOne:F3} Hz (exact snap: {exactSnap})");
                return (passthrough && exactSnap) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"m7a-check FAILED: {ex.Message}\n{ex.StackTrace}");
                return 1;
            }
        }

        if (args[0] is "metal-m7c-synth")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7c-synth requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 8.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;

                const double controlHz = 30.0;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7c-synth — Mandelbox fly-in {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Console.WriteLine("  A: ray-step wavetable (camera-dependent timbre)");
                Console.WriteLine("  B: inner-orbit wavetable (parameter-dependent timbre)");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var startPos = new Vector3(0f, 5f, 30f);
                var endPos   = new Vector3(0f, 1.5f, 7f);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                var frames    = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos = startPos;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-5} {"depth",-5} {"nVar",-5} {"wt/16"}");
                Console.WriteLine($"  {new string('-', 44)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    var   pos = Vector3.Lerp(startPos, endPos, t);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats    = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    // Carry wavetable data from MetalSpatialCell → FractalSonicCell
                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(
                                c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(
                        Time:              fi / controlHz,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f,
                        Cells:             cells));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        int wtCount = cells?.Count(c => c.RayWavetable != null) ?? 0;
                        Console.WriteLine($"  {fi,4}   {sf.HitRatio:F3}  {sf.MeanDepth,5:F1}  {sf.NormalVariance:F3}  {wtCount,2}/16");
                    }
                }

                Directory.CreateDirectory(outDir);

                Console.Write("\n  Synth A (ray-step)...   ");
                var swSynth = System.Diagnostics.Stopwatch.StartNew();
                var pcmRay = HybridSynth.Synthesize(frames, WavetableSource.RaySteps,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                swSynth.Stop();
                string rayPath = Path.Combine(outDir, "m7c_ray.wav");
                WavEncoder.Write(rayPath, pcmRay, HybridSynth.DefaultSampleRate, 2);
                double peakRay = 20.0 * Math.Log10((pcmRay.Length > 0 ? pcmRay.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{swSynth.ElapsedMilliseconds} ms | peak {peakRay:F1} dBFS  → {rayPath}");

                Console.Write("  Synth B (orbit-mags)... ");
                swSynth.Restart();
                var pcmOrbit = HybridSynth.Synthesize(frames, WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                swSynth.Stop();
                string orbitPath = Path.Combine(outDir, "m7c_orbit.wav");
                WavEncoder.Write(orbitPath, pcmOrbit, HybridSynth.DefaultSampleRate, 2);
                double peakOrbit = 20.0 * Math.Log10((pcmOrbit.Length > 0 ? pcmOrbit.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{swSynth.ElapsedMilliseconds} ms | peak {peakOrbit:F1} dBFS  → {orbitPath}");

                Console.WriteLine($"\nOK — A/B pair ready in {outDir}");
                Console.WriteLine("  Play m7c_ray.wav   for ray-step timbre (camera-dependent)");
                Console.WriteLine("  Play m7c_orbit.wav for orbit-mag timbre (parameter-dependent)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7c-synth FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7c-wt-dump")
        {
            // Dump raw wavetable values from both sources for the most-energetic cell.
            // Verifies that GPU wavetables are non-trivial and different.
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7c-wt-dump requires macOS."); return 1; }
            try
            {
                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0, F0: 0, LightIntensity: 1f);

                // Mid fly-in: same camera used in metal-m5-cells
                var cam = new Camera3D(new Vector3(0f, 2.5f, 14f), Vector3.Zero, Vector3.UnitY,
                    MathF.PI / 5f, 640f / 480f);

                var stats = renderer.RunTelemetryPass(fractal, cam, settings);
                if (stats?.Cells == null) { Console.Error.WriteLine("No cells returned."); return 1; }

                // Find most-energetic cell with wavetable data
                int bestIdx = -1; float bestE = -1;
                for (int ci = 0; ci < stats.Value.Cells.Length; ci++)
                {
                    var c = stats.Value.Cells[ci];
                    if (c.RayWavetable != null && c.Energy > bestE) { bestE = c.Energy; bestIdx = ci; }
                }
                if (bestIdx < 0) { Console.Error.WriteLine("No cells have wavetable data."); return 1; }

                var cell = stats.Value.Cells[bestIdx];
                Console.WriteLine($"Most energetic cell: idx={bestIdx}  energy={cell.Energy:F4}  hitRatio={cell.HitRatio:F3}");
                Console.WriteLine();

                Console.WriteLine("RayWavetable (first 16 samples):");
                Console.Write("  [");
                for (int i = 0; i < 16; i++) Console.Write($"{cell.RayWavetable![i]:+0.000;-0.000}{(i<15?", ":"")}");
                Console.WriteLine("]");

                Console.WriteLine("OrbitWavetable (first 16 samples):");
                Console.Write("  [");
                for (int i = 0; i < 16; i++) Console.Write($"{cell.OrbitWavetable![i]:+0.000;-0.000}{(i<15?", ":"")}");
                Console.WriteLine("]");

                Console.WriteLine();
                bool identical = cell.RayWavetable!.SequenceEqual(cell.OrbitWavetable!);
                bool allZeroRay   = cell.RayWavetable!.All(v => v == 0f);
                bool allZeroOrbit = cell.OrbitWavetable!.All(v => v == 0f);
                Console.WriteLine($"All-zero ray:   {allZeroRay}");
                Console.WriteLine($"All-zero orbit: {allZeroOrbit}");
                Console.WriteLine($"Identical:      {identical}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7c-wt-dump FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7c-drone")
        {
            // Drone-only isolation render: no bells, no reverb, fast morph.
            // Use this to hear the raw wavetable timbre difference before committing to a source.
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7c-drone requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 8.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;

                const double controlHz = 30.0;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7c-drone — drone-only A/B, {duration:F1}s Mandelbox fly-in (no bells/reverb, fast morph)");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var startPos = new Vector3(0f, 5f, 30f);
                var endPos   = new Vector3(0f, 1.5f, 7f);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                var frames    = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos = startPos;

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    var   pos = Vector3.Lerp(startPos, endPos, t);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats    = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(
                                c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(
                        Time: fi / controlHz, HitRatio: stats?.HitRatio ?? 0f,
                        MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                        StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                        NormalMean: stats?.NormalMean ?? Vector3.Zero, NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero, TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, Cells: cells));
                }

                Directory.CreateDirectory(outDir);

                Console.Write("  Drone A (ray-step)...   ");
                var pcmRay = HybridSynth.Synthesize(frames, WavetableSource.RaySteps,
                    controlRateHz: controlHz, droneOnly: true);
                string rayPath = Path.Combine(outDir, "m7c_drone_ray.wav");
                WavEncoder.Write(rayPath, pcmRay, HybridSynth.DefaultSampleRate, 2);
                double peakRay = 20.0 * Math.Log10((pcmRay.Length > 0 ? pcmRay.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"peak {peakRay:F1} dBFS  → {rayPath}");

                Console.Write("  Drone B (orbit-mags)... ");
                var pcmOrbit = HybridSynth.Synthesize(frames, WavetableSource.OrbitMags,
                    controlRateHz: controlHz, droneOnly: true);
                string orbitPath = Path.Combine(outDir, "m7c_drone_orbit.wav");
                WavEncoder.Write(orbitPath, pcmOrbit, HybridSynth.DefaultSampleRate, 2);
                double peakOrbit = 20.0 * Math.Log10((pcmOrbit.Length > 0 ? pcmOrbit.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"peak {peakOrbit:F1} dBFS  → {orbitPath}");

                Console.WriteLine($"\nOK — drone-only A/B in {outDir}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7c-drone FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7d-orbit")
        {
            // 360° Mandelbox orbit clip — exercises camera-driven cell voicing (M7d arpeggiation).
            // Camera circles at constant radius/elevation; each cell column activates in turn,
            // triggering modal bell resonators and shifting the energy-weighted wavetable blend.
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7d-orbit requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 12.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;

                const double controlHz   = 30.0;
                const float  orbitRadius = 8.0f;    // HitRatio ~0.35–0.45 at this range
                const float  elevation   = 1.5f;
                const float  fov         = MathF.PI / 4f;
                const float  aspect      = 16f / 9f;

                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7d-orbit — 360° Mandelbox orbit {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Console.WriteLine($"  radius={orbitRadius}, elevation={elevation}, orbit-mags wavetable, full hybrid (bells + reverb)");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames    = new List<FractalSonicFrame>(totalFrames);
                var prevPos   = new Vector3(orbitRadius, elevation, 0f);

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"hit",-5} {"nVar",-5} {"active cells"}");
                Console.WriteLine($"  {new string('-', 54)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float theta = 2f * MathF.PI * fi / (float)totalFrames;
                    var   pos   = new Vector3(MathF.Cos(theta) * orbitRadius, elevation, MathF.Sin(theta) * orbitRadius);
                    var   camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var   cam   = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(
                                c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(
                        Time:              fi / controlHz,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f,
                        CameraPosition:    pos,
                        CameraForward:     camFwd,
                        CameraUp:          Vector3.UnitY,
                        Cells:             cells));

                    // Print progress every second: θ, hit ratio, active cell bitmap
                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI);
                        string activeBitmap = cells != null
                            ? string.Concat(cells.Select(c => c.Energy > 0.10f ? "█" : "·"))
                            : "(no cells)";
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {sf.HitRatio:F3}  {sf.NormalVariance:F3}  {activeBitmap}");
                    }
                }

                Directory.CreateDirectory(outDir);

                Console.Write("\n  Synthesizing (orbit-mags blend, full hybrid)... ");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames,
                    wavetableSource:   WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f,
                    controlRateHz:     controlHz);
                sw.Stop();

                string outPath = Path.Combine(outDir, "m7d_orbit.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");

                Console.WriteLine("\nOK — listen for bell strikes as θ sweeps each spatial cell column.");
                Console.WriteLine("      Drone timbre should shift as the active-cell blend rotates.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7d-orbit FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7d-spiral")
        {
            // Helical inward spiral — camera orbits while closing from radius 12 → 5 and
            // descending from elevation 3 → 0.5.  Cell energies rise progressively as
            // HitRatio climbs, triggering modal bells in sequence rather than all at once.
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7d-spiral requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 12.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;

                const double controlHz    = 30.0;
                const float  radiusStart  = 12.0f;
                const float  radiusEnd    = 5.0f;
                const float  elevStart    = 3.0f;
                const float  elevEnd      = 0.5f;
                const float  turns        = 1.5f;   // 540° — enough to vary all 16 cell columns twice
                const float  fov          = MathF.PI / 4f;
                const float  aspect       = 16f / 9f;

                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7d-spiral — Mandelbox helical spiral {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Console.WriteLine($"  r {radiusStart}→{radiusEnd}, elev {elevStart}→{elevEnd}, {turns} turns, orbit-mags blend + bells + reverb");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(radiusStart, elevStart, 0f);

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"r",-5} {"hit",-5} {"nVar",-5} {"active cells"}");
                Console.WriteLine($"  {new string('-', 62)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float r     = radiusStart  + (radiusEnd  - radiusStart)  * t;
                    float elev  = elevStart    + (elevEnd    - elevStart)    * t;
                    float theta = 2f * MathF.PI * turns * t;

                    var pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(
                                c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(
                        Time:              fi / controlHz,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f,
                        CameraPosition:    pos,
                        CameraForward:     camFwd,
                        CameraUp:          Vector3.UnitY,
                        Cells:             cells));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        string activeBitmap = cells != null
                            ? string.Concat(cells.Select(c => c.Energy > 0.10f ? "█" : "·"))
                            : "(no cells)";
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {r,4:F1}  {sf.HitRatio:F3}  {sf.NormalVariance:F3}  {activeBitmap}");
                    }
                }

                Directory.CreateDirectory(outDir);

                Console.Write("\n  Synthesizing (orbit-mags blend, full hybrid)... ");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames,
                    wavetableSource:    WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f,
                    controlRateHz:      controlHz);
                sw.Stop();

                string outPath = Path.Combine(outDir, "m7d_spiral.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");

                Console.WriteLine("\nOK — bells should activate progressively as radius closes in.");
                Console.WriteLine("      Watch the active-cell bitmap widen from center outward.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7d-spiral FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7e-mandelbulb")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7e-mandelbulb requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 10.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  turns     = 1.5f;
                const float  fov       = MathF.PI / 4f;
                const float  aspect    = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7e-mandelbulb — Mandelbulb helical spiral {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");

                using var renderer = new MetalMandelbulbRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelbulbParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(6f, 2f, 0f);

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"r",-5} {"hit",-5} {"wt?",-4} {"active cells"}");
                Console.WriteLine($"  {new string('-', 68)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float r     = 6f + (3f - 6f) * t;
                    float elev  = 2f + (0.3f - 2f) * t;
                    float theta = 2f * MathF.PI * turns * t;

                    var pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz, HitRatio: stats?.HitRatio ?? 0f,
                        MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                        StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                        NormalMean: stats?.NormalMean ?? Vector3.Zero, NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero, TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY, Cells: cells));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        string activeBitmap = cells != null
                            ? string.Concat(cells.Select(c => c.Energy > 0.10f ? "█" : "·"))
                            : "(no cells)";
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {r,4:F1}  {sf.HitRatio:F3}  {(hasWt ? "yes" : "NO "),-4}  {activeBitmap}");
                    }
                }

                Directory.CreateDirectory(outDir);
                Console.Write("\n  Synthesizing Mandelbulb hybrid voice... ");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7e_mandelbulb.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — verify wt? column shows 'yes' on hit frames; airy bells should activate as radius closes.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7e-mandelbulb FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7e-kleinian")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7e-kleinian requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 10.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  turns     = 1.5f;
                const float  fov       = MathF.PI / 4f;
                const float  aspect    = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7e-kleinian — Kleinian helical spiral {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");

                using var renderer = new MetalKleinianRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new KleinianParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(8f, 2f, 0f);

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"r",-5} {"hit",-5} {"wt?",-4} {"active cells"}");
                Console.WriteLine($"  {new string('-', 68)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float r     = 8f + (3f - 8f) * t;
                    float elev  = 2f + (0.5f - 2f) * t;
                    float theta = 2f * MathF.PI * turns * t;

                    var pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz, HitRatio: stats?.HitRatio ?? 0f,
                        MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                        StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                        NormalMean: stats?.NormalMean ?? Vector3.Zero, NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero, TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY, Cells: cells));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        string activeBitmap = cells != null
                            ? string.Concat(cells.Select(c => c.Energy > 0.10f ? "█" : "·"))
                            : "(no cells)";
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {r,4:F1}  {sf.HitRatio:F3}  {(hasWt ? "yes" : "NO "),-4}  {activeBitmap}");
                    }
                }

                Directory.CreateDirectory(outDir);
                Console.Write("\n  Synthesizing Kleinian hybrid voice... ");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7e_kleinian.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — verify wt? column shows 'yes'; cavernous long bells (3–9 s decay) should ring as the camera closes.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7e-kleinian FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7e-burningship")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7e-burningship requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 10.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  turns     = 1.5f;
                const float  fov       = MathF.PI / 4f;
                const float  aspect    = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7e-burningship — BurningShip helical spiral {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");

                using var renderer = new MetalBurningShipRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new BurningShipParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(6f, 2f, 0f);

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"r",-5} {"hit",-5} {"wt?",-4} {"active cells"}");
                Console.WriteLine($"  {new string('-', 68)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float r     = 6f + (2f - 6f) * t;
                    float elev  = 2f + (0.3f - 2f) * t;
                    float theta = 2f * MathF.PI * turns * t;

                    var pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz, HitRatio: stats?.HitRatio ?? 0f,
                        MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                        StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                        NormalMean: stats?.NormalMean ?? Vector3.Zero, NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero, TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY, Cells: cells));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        string activeBitmap = cells != null
                            ? string.Concat(cells.Select(c => c.Energy > 0.10f ? "█" : "·"))
                            : "(no cells)";
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {r,4:F1}  {sf.HitRatio:F3}  {(hasWt ? "yes" : "NO "),-4}  {activeBitmap}");
                    }
                }

                Directory.CreateDirectory(outDir);
                Console.Write("\n  Synthesizing BurningShip hybrid voice... ");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7e_burningship.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — verify wt? column shows 'yes'; percussive short bells (0.08–0.45 s) with crackle character.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7e-burningship FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7f-shepard")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7f-shepard requires macOS."); return 1; }
            try
            {
                double duration  = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 15.0;
                string outDir    = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  fov       = MathF.PI / 4f;
                const float  aspect    = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                // Radial zoom-in then zoom-out: camera on Z-axis facing origin.
                // First half zooms from r=5 to r=1.6; second half reverses.
                // ZoomVelocity = dot(displacement, forward) * controlHz:
                //   forward = (0,0,1), moving from z=-5 to z=-1.6 → positive ZV → pitch descends.
                const float rFar  = 5.0f;
                const float rNear = 1.6f;

                Console.WriteLine($"metal-m7f-shepard — Mandelbox radial zoom-in/out {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Console.WriteLine($"  Shepard glissando: descends during zoom-in, ascends during zoom-out.");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames   = new List<FractalSonicFrame>(totalFrames);
                var prevPos  = new Vector3(0f, 0f, -rFar);

                Console.WriteLine($"\n  {"fi",-5}  {"r",-6} {"zV",-8} {"gTgt",-8} {"hit",-5} {"wt?"}");
                Console.WriteLine($"  {new string('-', 55)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    // Smooth ease-in/ease-out (cosine): 0→1 in first half, 1→0 in second half
                    float u = t < 0.5f ? t * 2f : (1f - t) * 2f;
                    float ease = 0.5f * (1f - MathF.Cos(MathF.PI * u));
                    float r = rFar + (rNear - rFar) * ease;

                    var pos    = new Vector3(0f, 0f, -r);
                    var camFwd = new Vector3(0f, 0f, 1f);   // always pointing at origin
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd  = (pos - prevPos).Length() * (float)controlHz;
                    float zoomVel = Vector3.Dot(pos - prevPos, camFwd) * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz,
                        HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                        DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                        StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                        NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero,
                        TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY, Cells: cells,
                        ZoomVelocity: zoomVel));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf = frames[^1];
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        float glideTarget = Math.Clamp(-zoomVel * 12f, -36f, 36f);
                        Console.WriteLine($"  {fi,4}   {r,4:F2}  {zoomVel,+7:F3}  {glideTarget,+7:F1}  {sf.HitRatio:F3}  {(hasWt ? "yes" : "NO ")}");
                    }
                }

                Directory.CreateDirectory(outDir);
                Console.Write("\n  Synthesizing Mandelbox + Shepard–Risset layer... ");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7f_shepard.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — first half should sound like descending Shepard glide; second half ascending.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7f-shepard FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7g-apollonian")
        {
            // Pure offline: no Metal renderer needed — geometry pitches are fixed JI ratios.
            // Camera does a slow circular orbit; ZoomVelocity gently oscillates.
            try
            {
                double duration   = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 12.0;
                string outDir     = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  orbitR    = 4.0f;
                const float  turns     = 2.0f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7g-apollonian — parameter-driven JI bells, {duration:F1}s orbit ({totalFrames} frames)");
                Console.WriteLine($"  Scale: {{1/1, 35/32, 19/16, 3/2, 15/8}} × 110 Hz — Apollonian gasket curvature ratios.");

                // Pre-compute geometry pitches once (canonical, tangency=1.0)
                float[] geomPitches = GeometryScale.Apollonian(110f);
                Console.WriteLine($"  Pitches: {string.Join(", ", geomPitches.Select(p => $"{p:F1} Hz"))}");

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(orbitR, 0f, 0f);

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float theta = 2f * MathF.PI * turns * t;
                    var   pos   = new Vector3(MathF.Cos(theta) * orbitR, 0.5f, MathF.Sin(theta) * orbitR);
                    var camFwd  = Vector3.Normalize(Vector3.Zero - pos);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)controlHz;
                    prevPos = pos;

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz,
                        HitRatio: 0f, MeanDepth: 0f, DepthVariance: 0f,
                        StepMean: 0f, StepP90: 0f, NormalMean: Vector3.Zero,
                        NormalVariance: 0f, TrapMean: Vector4.Zero, TrapVariance: Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY,
                        ZoomVelocity: zoomV, GeometryPitches: geomPitches));
                }

                Directory.CreateDirectory(outDir);
                Console.Write("\n  Synthesizing Apollonian geometry-scale bells... ");
                var sw  = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, controlRateHz: controlHz,
                    voice: FractalVoice.Apollonian);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7g_apollonian.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — bells should ring at 110/120.3/130.6/165/206.3 Hz (Apollonian JI pentatonic).");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7g-apollonian FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7g-kleinian")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7g-kleinian requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 12.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  turns     = 1.5f;
                const float  fov       = MathF.PI / 4f;
                const float  aspect    = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                // Default KleinianState params match KleinianParams defaults
                const float kFixed = 1.0f, kMin = 0.5f, kScale = 2.0f;
                float[] geomPitches = GeometryScale.Kleinian(220f, kFixed, kMin, kScale);
                Console.WriteLine($"metal-m7g-kleinian — geometry-native scale {duration:F1}s ({totalFrames} frames)");
                Console.WriteLine($"  Eigenvalue ratio: {kScale*(1f+kFixed/kMin)*0.5f:F3} → P5 → Pythagorean scale.");
                Console.WriteLine($"  Pitches: {string.Join(", ", geomPitches.Select(p => $"{p:F1} Hz"))}");

                using var renderer = new MetalKleinianRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new KleinianParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(8f, 2f, 0f);

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"r",-5} {"hit",-5} {"wt?"}");
                Console.WriteLine($"  {new string('-', 55)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float r     = 8f + (3f - 8f) * t;
                    float elev  = 2f + (0.5f - 2f) * t;
                    float theta = 2f * MathF.PI * turns * t;

                    var pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz,
                        HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                        DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                        StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                        NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero,
                        TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY, Cells: cells,
                        ZoomVelocity: zoomV, GeometryPitches: geomPitches));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf  = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {r,4:F1}  {sf.HitRatio:F3}  {(hasWt ? "yes" : "NO ")}");
                    }
                }

                Directory.CreateDirectory(outDir);
                Console.Write("\n  Synthesizing Kleinian geometry-native scale... ");
                var sw  = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7g_kleinian.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — bells at Pythagorean scale (3/2 generator); compare with m7e_kleinian.wav.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7g-kleinian FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7h-waveshaper")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7h-waveshaper requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 12.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                const float  turns     = 1.5f;
                const float  fov       = MathF.PI / 4f;
                const float  aspect    = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m7h-waveshaper — DE cross-section waveshaper layer {duration:F1}s ({totalFrames} frames)");
                Console.WriteLine("  Mandelbox circular orbit; waveshaper strip sampled along camera-right at each frame.");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames  = new List<FractalSonicFrame>(totalFrames);
                var prevPos = new Vector3(8f, 2f, 0f);
                int wsNonTrivial = 0;

                Console.WriteLine($"\n  {"fi",-5}  {"θ°",-6} {"hit",-5} {"ws-rng",-8} {"wt?"}");
                Console.WriteLine($"  {new string('-', 55)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float r     = 8f + (3f - 8f) * t;
                    float elev  = 2f + (0.5f - 2f) * t;
                    float theta = 2f * MathF.PI * turns * t;

                    var pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)controlHz;
                    prevPos = pos;

                    float[]? ws = stats?.WaveshaperCurve;
                    if (ws != null && ws.Any(v => MathF.Abs(v) > 0.01f)) wsNonTrivial++;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    frames.Add(new FractalSonicFrame(Time: fi / controlHz,
                        HitRatio: stats?.HitRatio ?? 0f, MeanDepth: stats?.MeanDepth ?? 0f,
                        DepthVariance: stats?.DepthVariance ?? 0f, StepMean: stats?.StepMean ?? 0f,
                        StepP90: stats?.StepP90 ?? 0f, NormalMean: stats?.NormalMean ?? Vector3.Zero,
                        NormalVariance: stats?.NormalVariance ?? 0f,
                        TrapMean: stats?.TrapMean ?? Vector4.Zero,
                        TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: camSpd, ParameterVelocity: 0f, CameraPosition: pos,
                        CameraForward: camFwd, CameraUp: Vector3.UnitY, Cells: cells,
                        ZoomVelocity: zoomV, WaveshaperCurve: ws));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                    {
                        var sf  = frames[^1];
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        float wsRange = ws != null && ws.Length > 0 ? ws.Max() - ws.Min() : 0f;
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        Console.WriteLine($"  {fi,4}   {deg,4}°  {sf.HitRatio:F3}  {wsRange:F3}     {(hasWt ? "yes" : "NO ")}");
                    }
                }

                Console.WriteLine($"\n  Non-trivial waveshaper frames: {wsNonTrivial}/{totalFrames}");
                Directory.CreateDirectory(outDir);
                Console.Write("  Synthesizing with DE waveshaper layer... ");
                var sw  = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(frames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: controlHz);
                sw.Stop();
                string outPath = Path.Combine(outDir, "m7h_waveshaper.wav");
                WavEncoder.Write(outPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peak = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS  → {outPath}");
                Console.WriteLine("OK — A/B against m7e_mandelbox.wav to hear the waveshaper contribution.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7h-waveshaper FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7h-clip")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7h-clip requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 8.0;
                string outMp4   = args.Length >= 3 ? args[2] : ResolveOutputPath("m7h_clip.mp4");

                const int    fps    = 30;
                const int    w      = 640;
                const int    h      = 360;
                const float  fov    = MathF.PI / 4f;
                const float  aspect = (float)w / h;
                int totalFrames     = (int)Math.Round(duration * fps);

                Console.WriteLine($"metal-m7h-clip — Mandelbox fly-in {duration:F1}s @ {fps} fps ({totalFrames} frames, {w}×{h})");
                Console.WriteLine("  Renders each frame at full quality; telemetry pass extracts waveshaper strip + cells.");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var fractal  = new MandelboxParams();
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: true,  ShadowSteps: 40, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.06f, AOIntensity: 0.9f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.3f);

                var bg      = new Color(0.02f, 0.02f, 0.06f);
                var surface = new Color(0.55f, 0.62f, 0.75f);
                var light   = Vector3.Normalize(new Vector3(1.2f, 2f, 1.5f));
                var palette = PaletteParams.Default;
                var post    = new PostProcessParams { Brightness = 1.05f, Contrast = 1.1f,
                                                     Saturation = 1.2f, Gamma = 2.2f };

                var startPos = new Vector3(0f, 5f, 30f);
                var endPos   = new Vector3(0f, 1.5f, 7f);

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m7h-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sonicFrames = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos = startPos;
                int wsNonTrivial = 0;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-5} {"dep",-5} {"ws?",-4} {"wt?"}  render ms");
                Console.WriteLine($"  {new string('-', 50)}");

                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    var   pos = Vector3.Lerp(startPos, endPos, t);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                    var   camFwd = Vector3.Normalize(Vector3.Zero - pos);

                    // Render frame
                    uint[] pixels = renderer.RenderMandelbox(fractal, cam, w, h, settings, bg, surface, light, palette, post);

                    // Save PNG
                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                    bmp.Dispose();

                    // Telemetry pass (separate low-res kernel)
                    var stats   = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * fps;
                    float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * fps;
                    prevPos = pos;

                    float[]? ws = stats?.WaveshaperCurve;
                    if (ws != null && ws.Any(v => MathF.Abs(v) > 0.01f)) wsNonTrivial++;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    sonicFrames.Add(new FractalSonicFrame(
                        Time:              fi / (double)fps,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f,
                        CameraPosition:    pos,
                        CameraForward:     camFwd,
                        CameraUp:          Vector3.UnitY,
                        Cells:             cells,
                        ZoomVelocity:      zoomV,
                        WaveshaperCurve:   ws));

                    if (fi % fps == 0 || fi == totalFrames - 1)
                    {
                        var sf = sonicFrames[^1];
                        bool hasWt = cells != null && cells.Any(c => c.OrbitWavetable != null);
                        Console.WriteLine($"  {fi,4}   {sf.HitRatio:F3}  {sf.MeanDepth,4:F1}  {(ws != null ? "Y" : "N")}    {(hasWt ? "yes" : "NO ")}  {renderer.LastComputeMs}ms");
                    }
                    else
                    {
                        Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }
                }

                totalSw.Stop();
                Console.WriteLine($"\n  {totalFrames} frames in {totalSw.ElapsedMilliseconds} ms  |  ws non-trivial: {wsNonTrivial}/{totalFrames}");

                // Synthesise using M7h hybrid synth
                Console.Write($"\n  Synthesising M7h hybrid audio ({sonicFrames.Count} frames @ {fps} Hz control rate)...");
                var synthSw = System.Diagnostics.Stopwatch.StartNew();
                var pcm     = HybridSynth.Synthesize(sonicFrames, wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f, controlRateHz: fps);
                synthSw.Stop();

                string wavPath = Path.Combine(frameDir, "m7h.wav");
                WavEncoder.Write(wavPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peakDb = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($" {synthSw.ElapsedMilliseconds} ms | peak {peakDb:F1} dBFS");

                // Mux with ffmpeg
                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                $"-i \"{wavPath}\" " +
                                $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                Console.Write("  Running ffmpeg...");
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                Console.WriteLine($" done.\nOK → {outMp4}  ({new FileInfo(outMp4).Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7h-clip FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] is "metal-m7h-kleinian")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m7h-kleinian requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 12.0;
                string outMp4   = args.Length >= 3 ? args[2] : ResolveOutputPath("m7h_kleinian.mp4");

                const int   fps    = 30;
                const int   w      = 640;
                const int   h      = 360;
                const float fov    = MathF.PI / 4f;
                const float aspect = (float)w / h;
                int totalFrames    = (int)Math.Round(duration * fps);

                Console.WriteLine($"metal-m7h-kleinian — Kleinian animated morph {duration:F1}s @ {fps} fps ({totalFrames} frames, {w}×{h})");
                Console.WriteLine("  Helical orbit (1.5 revolutions, zoom from r=11 to r=4); Scale/Cell/FixedRadius animate.");
                Console.WriteLine("  Voice: Kleinian (cavernous bells, 3–9 s decay, Dorian A1, geometry-native tuning).");

                using var renderer = new MetalKleinianRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 200, HitEpsilon: 1.2e-3f, MaxDistance: 30f, NormalEpsilon: 1.5e-3f,
                    EnableSoftShadows: true,  ShadowSteps: 40, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.04f, AOIntensity: 1.0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.4f);

                var bg      = new Color(0.008f, 0.010f, 0.025f);
                var surface = new Color(0.30f, 0.36f, 0.52f);
                var light   = Vector3.Normalize(new Vector3(0.8f, 2.5f, 1.2f));
                var palette = PaletteParams.Default;
                var post    = new PostProcessParams {
                    Brightness = 1.1f, Contrast = 1.15f,
                    Saturation = 1.3f, Gamma = 2.2f };

                // Camera: helical spiral 1.5 revolutions, r=11→4.2, h=5→0.6
                const float startR      = 11f,  endR      = 4.2f;
                const float startHeight = 5.0f, endHeight = 0.6f;
                const float totalAngle  = 1.5f * 2f * MathF.PI;

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m7h-kl-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sonicFrames  = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos  = new Vector3(startR, startHeight, 0f);
                int wsNonTrivial = 0;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-5} {"dep",-5} {"scl",-5} {"ws?",-4} render ms");
                Console.WriteLine($"  {new string('-', 55)}");

                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;

                    // Helical camera path
                    float r     = startR + (endR - startR) * t;
                    float hPos  = startHeight + (endHeight - startHeight) * t;
                    float angle = totalAngle * t;
                    var   pos   = new Vector3(r * MathF.Cos(angle), hPos, r * MathF.Sin(angle));
                    var   lookAt = new Vector3(0f, hPos * 0.12f, 0f);   // track slightly above origin as we descend
                    var   camFwd = Vector3.Normalize(lookAt - pos);
                    var   cam    = new Camera3D(pos, lookAt, Vector3.UnitY, fov, aspect);

                    // Fractal parameter animation
                    float scaleAnim = 2.0f - 0.30f * MathF.Sin(MathF.PI * t);           // 2.0 → 1.70 → 2.0
                    float cellAnim  = 1.0f + 0.20f * MathF.Sin(2f * MathF.PI * t + 0.7f); // 0.80 ↔ 1.20
                    float fixedAnim = 1.0f + 0.18f * MathF.Sin(MathF.PI * t * 1.3f);     // 1.00 ↔ 1.18

                    var fractal = new KleinianParams {
                        Iterations  = 10,
                        Scale       = scaleAnim,
                        Cell        = cellAnim,
                        MinRadius   = 0.5f,
                        FixedRadius = fixedAnim,
                        Offset      = new Vector3(0.5f, 0.5f, 1.2f),
                        Fudge       = 0.68f,
                        BoundRadius = 7.0f,
                    };

                    float prevT      = totalFrames > 1 ? (fi - 1) / (float)(totalFrames - 1) : 0f;
                    float prevScale  = fi > 0 ? 2.0f - 0.30f * MathF.Sin(MathF.PI * prevT) : scaleAnim;
                    float paramVel   = MathF.Abs(scaleAnim - prevScale) * fps;

                    // Render frame
                    uint[] pixels = renderer.RenderKleinian(fractal, cam, w, h, settings, bg, surface, light, palette, post);

                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                    bmp.Dispose();

                    // Telemetry
                    var   stats  = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * fps;
                    float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * fps;
                    prevPos = pos;

                    float[]? ws = stats?.WaveshaperCurve;
                    if (ws != null && ws.Any(v => MathF.Abs(v) > 0.01f)) wsNonTrivial++;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } metalCells)
                    {
                        cells = new FractalSonicCell[metalCells.Length];
                        for (int ci = 0; ci < metalCells.Length; ci++)
                        {
                            var c = metalCells[ci];
                            cells[ci] = new FractalSonicCell(c.WorldPosition, c.HitRatio, c.MeanDepth,
                                c.StepComplexity, c.NormalMean, c.TrapMean, c.Energy,
                                c.RayWavetable, c.OrbitWavetable);
                        }
                    }

                    float[] geomPitches = GeometryScale.Kleinian(220f, fractal.FixedRadius, fractal.MinRadius, fractal.Scale);

                    sonicFrames.Add(new FractalSonicFrame(
                        Time:              fi / (double)fps,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: paramVel,
                        CameraPosition:    pos,
                        CameraForward:     camFwd,
                        CameraUp:          Vector3.UnitY,
                        Cells:             cells,
                        ZoomVelocity:      zoomV,
                        GeometryPitches:   geomPitches,
                        WaveshaperCurve:   ws));

                    if (fi % fps == 0 || fi == totalFrames - 1)
                    {
                        var sf = sonicFrames[^1];
                        Console.WriteLine($"  {fi,4}   {sf.HitRatio:F3}  {sf.MeanDepth,4:F1}  {scaleAnim:F2}  {(ws != null ? "Y" : "N")}    {renderer.LastComputeMs}ms");
                    }
                    else
                    {
                        Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }
                }

                totalSw.Stop();
                Console.WriteLine($"\n  {totalFrames} frames in {totalSw.ElapsedMilliseconds} ms  |  ws non-trivial: {wsNonTrivial}/{totalFrames}");

                Console.Write($"\n  Synthesising M7h Kleinian audio ({sonicFrames.Count} frames @ {fps} Hz, voice=Kleinian)...");
                var synthSw = System.Diagnostics.Stopwatch.StartNew();
                var pcm     = HybridSynth.Synthesize(sonicFrames,
                    voice:              FractalVoice.Kleinian,
                    wavetableSource:    WavetableSource.RaySteps,
                    temperamentCeiling: 0.85f,
                    controlRateHz:      fps);
                synthSw.Stop();

                string wavPath = Path.Combine(frameDir, "m7h_kleinian.wav");
                WavEncoder.Write(wavPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peakDb = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($" {synthSw.ElapsedMilliseconds} ms | peak {peakDb:F1} dBFS");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                $"-i \"{wavPath}\" " +
                                $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                Console.Write("  Running ffmpeg...");
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                Console.WriteLine($" done.\nOK → {outMp4}  ({new FileInfo(outMp4).Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m7h-kleinian FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // =====================================================================
        // metal-m8-fieldscan [fractal] [duration] [out.mp4]
        // M8: Lissajous field-scan synthesis.  Diffs against M7e/h by replacing
        // the orbit-wavetable drone with a geometry-sampled Lissajous waveform.
        // Fractals: kleinian (default), mandelbox.
        // =====================================================================
        if (args[0] == "metal-m8-fieldscan")
        {
            try
            {
                string fractalName = args.Length >= 2 ? args[1].ToLower() : "kleinian";
                double duration    = args.Length >= 3 && double.TryParse(args[2], out var d) ? d : 12.0;
                string outMp4      = args.Length >= 4 ? args[3] : ResolveOutputPath($"m8_fieldscan_{fractalName}.mp4");

                const int   fps    = 30;
                const int   w      = 640;
                const int   h      = 360;
                const float fov    = MathF.PI / 4f;
                const float aspect = (float)w / h;
                int totalFrames    = (int)Math.Round(duration * fps);

                bool isKleinian = fractalName != "mandelbox";
                Console.WriteLine($"metal-m8-fieldscan — {(isKleinian ? "Kleinian" : "Mandelbox")} Lissajous field-scan {duration:F1}s @ {fps} fps ({totalFrames} frames, {w}×{h})");
                Console.WriteLine("  Helical orbit (1.5 rev). Voice: field-scan drone + geometry bells + Shepard.");

                var settings = new RaymarchSettings(
                    MaxSteps: 200, HitEpsilon: 1.2e-3f, MaxDistance: 30f, NormalEpsilon: 1.5e-3f,
                    EnableSoftShadows: true,  ShadowSteps: 40, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.04f, AOIntensity: 1.0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.4f);

                var bg      = new Color(0.008f, 0.010f, 0.025f);
                var post    = new PostProcessParams { Brightness = 1.1f, Contrast = 1.15f, Saturation = 1.3f, Gamma = 2.2f };
                var palette = PaletteParams.Default;
                var light   = Vector3.Normalize(new Vector3(0.8f, 2.5f, 1.2f));

                const float startR      = 11f,  endR      = 4.2f;
                const float startHeight = 5.0f, endHeight = 0.6f;
                const float totalAngle  = 1.5f * 2f * MathF.PI;

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m8-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sonicFrames  = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos  = new Vector3(startR, startHeight, 0f);
                int fsNonTrivial = 0;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-5} {"dep",-5} {"scl",-5} {"fs?",-4} render ms");
                Console.WriteLine($"  {new string('-', 55)}");

                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                if (isKleinian)
                {
                    var surface = new Color(0.30f, 0.36f, 0.52f);
                    using var renderer = new MetalKleinianRenderer();
                    if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        float r     = startR + (endR - startR) * t;
                        float hPos  = startHeight + (endHeight - startHeight) * t;
                        float angle = totalAngle * t;
                        var   pos   = new Vector3(r * MathF.Cos(angle), hPos, r * MathF.Sin(angle));
                        var   lookAt = new Vector3(0f, hPos * 0.12f, 0f);
                        var   camFwd = Vector3.Normalize(lookAt - pos);
                        var   cam    = new Camera3D(pos, lookAt, Vector3.UnitY, fov, aspect);

                        float scaleAnim = 2.0f - 0.30f * MathF.Sin(MathF.PI * t);
                        float cellAnim  = 1.0f + 0.20f * MathF.Sin(2f * MathF.PI * t + 0.7f);
                        float fixedAnim = 1.0f + 0.18f * MathF.Sin(MathF.PI * t * 1.3f);

                        var fractal = new KleinianParams {
                            Iterations = 10, Scale = scaleAnim, Cell = cellAnim,
                            MinRadius = 0.5f, FixedRadius = fixedAnim,
                            Offset = new Vector3(0.5f, 0.5f, 1.2f), Fudge = 0.68f, BoundRadius = 7.0f };

                        float prevT     = totalFrames > 1 ? (fi - 1) / (float)(totalFrames - 1) : 0f;
                        float prevScale = fi > 0 ? 2.0f - 0.30f * MathF.Sin(MathF.PI * prevT) : scaleAnim;
                        float paramVel  = MathF.Abs(scaleAnim - prevScale) * fps;

                        uint[] pixels = renderer.RenderKleinian(fractal, cam, w, h, settings, bg, surface, light, palette, post);
                        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp  = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();

                        var   stats                    = renderer.RunTelemetryPass(fractal, cam, settings);
                        var   (fsTL, fsTR, fsBL, fsBR) = renderer.RunFieldScanPass(fractal, cam, settings);
                        float camSpd                   = (pos - prevPos).Length() * fps;
                        float zoomV                    = Vector3.Dot(pos - prevPos, camFwd) * fps;
                        prevPos = pos;

                        if (fsTL != null && fsTL.Any(v => MathF.Abs(v) > 0.01f)) fsNonTrivial++;

                        FractalSonicCell[]? cells = null;
                        if (stats?.Cells is { Length: > 0 } mc)
                        {
                            cells = new FractalSonicCell[mc.Length];
                            for (int ci = 0; ci < mc.Length; ci++)
                                cells[ci] = new FractalSonicCell(mc[ci].WorldPosition, mc[ci].HitRatio,
                                    mc[ci].MeanDepth, mc[ci].StepComplexity, mc[ci].NormalMean,
                                    mc[ci].TrapMean, mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable);
                        }

                        float[] geomPitches = GeometryScale.Kleinian(220f, fractal.FixedRadius, fractal.MinRadius, fractal.Scale);

                        sonicFrames.Add(new FractalSonicFrame(
                            Time:              fi / (double)fps,
                            HitRatio:          stats?.HitRatio       ?? 0f,
                            MeanDepth:         stats?.MeanDepth       ?? 0f,
                            DepthVariance:     stats?.DepthVariance   ?? 0f,
                            StepMean:          stats?.StepMean        ?? 0f,
                            StepP90:           stats?.StepP90         ?? 0f,
                            NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                            NormalVariance:    stats?.NormalVariance   ?? 0f,
                            TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                            TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                            CameraSpeed:       camSpd,
                            ParameterVelocity: paramVel,
                            CameraPosition:    pos,
                            CameraForward:     camFwd,
                            CameraUp:          Vector3.UnitY,
                            Cells:             cells,
                            ZoomVelocity:      zoomV,
                            GeometryPitches:    geomPitches,
                            WaveshaperCurve:    stats?.WaveshaperCurve,
                            FieldScanWaveformTL: fsTL,
                            FieldScanWaveformTR: fsTR,
                            FieldScanWaveformBL: fsBL,
                            FieldScanWaveformBR: fsBR));

                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  {fi,4}   {stats?.HitRatio ?? 0f:F3}  {stats?.MeanDepth ?? 0f,4:F1}  {scaleAnim:F2}  {(fsTL != null ? "Y" : "N")}    {renderer.LastComputeMs}ms");
                        else
                            Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }
                }
                else // mandelbox
                {
                    var surface = new Color(0.55f, 0.42f, 0.28f);
                    using var renderer = new MetalMandelboxRenderer();
                    if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        float r     = startR + (endR - startR) * t;
                        float hPos  = startHeight + (endHeight - startHeight) * t;
                        float angle = totalAngle * t;
                        var   pos   = new Vector3(r * MathF.Cos(angle), hPos, r * MathF.Sin(angle));
                        var   lookAt = new Vector3(0f, hPos * 0.12f, 0f);
                        var   camFwd = Vector3.Normalize(lookAt - pos);
                        var   cam    = new Camera3D(pos, lookAt, Vector3.UnitY, fov, aspect);

                        float scaleAnim = 2.0f - 0.25f * MathF.Sin(MathF.PI * t);
                        float prevT     = totalFrames > 1 ? (fi - 1) / (float)(totalFrames - 1) : 0f;
                        float prevScale = fi > 0 ? 2.0f - 0.25f * MathF.Sin(MathF.PI * prevT) : scaleAnim;
                        float paramVel  = MathF.Abs(scaleAnim - prevScale) * fps;

                        var fractal = new MandelboxParams {
                            Iterations = 14, Scale = scaleAnim, FoldingLimit = 1.0f,
                            MinRadius = 0.5f, FixedRadius = 1.0f, BoundRadius = 5.0f };

                        uint[] pixels = renderer.RenderMandelbox(fractal, cam, w, h, settings,
                            bg, surface, light, palette,
                            new PostProcessParams { Brightness = 1.0f, Contrast = 1.1f, Gamma = 2.2f, Saturation = 1.2f });

                        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp  = new SKBitmap(info);
                        var bytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();

                        var   stats                    = renderer.RunTelemetryPass(fractal, cam, settings);
                        var   (fsTL, fsTR, fsBL, fsBR) = renderer.RunFieldScanPass(fractal, cam, settings);
                        float camSpd                   = (pos - prevPos).Length() * fps;
                        float zoomV                    = Vector3.Dot(pos - prevPos, camFwd) * fps;
                        prevPos = pos;

                        if (fsTL != null && fsTL.Any(v => MathF.Abs(v) > 0.01f)) fsNonTrivial++;

                        FractalSonicCell[]? cells = null;
                        if (stats?.Cells is { Length: > 0 } mc)
                        {
                            cells = new FractalSonicCell[mc.Length];
                            for (int ci = 0; ci < mc.Length; ci++)
                                cells[ci] = new FractalSonicCell(mc[ci].WorldPosition, mc[ci].HitRatio,
                                    mc[ci].MeanDepth, mc[ci].StepComplexity, mc[ci].NormalMean,
                                    mc[ci].TrapMean, mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable);
                        }

                        sonicFrames.Add(new FractalSonicFrame(
                            Time:              fi / (double)fps,
                            HitRatio:          stats?.HitRatio       ?? 0f,
                            MeanDepth:         stats?.MeanDepth       ?? 0f,
                            DepthVariance:     stats?.DepthVariance   ?? 0f,
                            StepMean:          stats?.StepMean        ?? 0f,
                            StepP90:           stats?.StepP90         ?? 0f,
                            NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                            NormalVariance:    stats?.NormalVariance   ?? 0f,
                            TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                            TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                            CameraSpeed:       camSpd,
                            ParameterVelocity: paramVel,
                            CameraPosition:    pos,
                            CameraForward:     camFwd,
                            CameraUp:          Vector3.UnitY,
                            Cells:             cells,
                            ZoomVelocity:       zoomV,
                            WaveshaperCurve:    stats?.WaveshaperCurve,
                            FieldScanWaveformTL: fsTL,
                            FieldScanWaveformTR: fsTR,
                            FieldScanWaveformBL: fsBL,
                            FieldScanWaveformBR: fsBR));

                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  {fi,4}   {stats?.HitRatio ?? 0f:F3}  {stats?.MeanDepth ?? 0f,4:F1}  {scaleAnim:F2}  {(fsTL != null ? "Y" : "N")}    {renderer.LastComputeMs}ms");
                        else
                            Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }
                }

                totalSw.Stop();
                Console.WriteLine($"\n  {totalFrames} frames in {totalSw.ElapsedMilliseconds} ms  |  fs non-trivial: {fsNonTrivial}/{totalFrames}");

                string voiceName = isKleinian ? "Kleinian" : "Mandelbox";
                FractalVoice voice = isKleinian ? FractalVoice.Kleinian : FractalVoice.Mandelbox;
                Console.Write($"\n  Synthesising M8 field-scan audio ({sonicFrames.Count} frames @ {fps} Hz, voice={voiceName})...");
                var synthSw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(sonicFrames,
                    voice:              voice,
                    wavetableSource:    WavetableSource.FieldScan,
                    temperamentCeiling: 0.85f,
                    controlRateHz:      fps);
                synthSw.Stop();

                string wavPath = Path.Combine(frameDir, "m8_fieldscan.wav");
                WavEncoder.Write(wavPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peakDb = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($" {synthSw.ElapsedMilliseconds} ms | peak {peakDb:F1} dBFS");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                $"-i \"{wavPath}\" " +
                                $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                Console.Write("  Running ffmpeg...");
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                Console.WriteLine($" done.\nOK → {outMp4}  ({new FileInfo(outMp4).Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m8-fieldscan FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // =====================================================================
        // metal-m8-kleinian-deep [duration] [out.mp4]
        // Slow (0.5 revolution) deep dive into the Kleinian lattice.
        // r=14→2.5, height 7→-0.5, 20 s default.  M8 field-scan drone + bells.
        // =====================================================================
        if (args[0] == "metal-m8-kleinian-deep")
        {
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 20.0;
                string outMp4   = args.Length >= 3 ? args[2] : ResolveOutputPath("m8_kleinian_deep.mp4");

                const int   fps    = 30;
                const int   w      = 640;
                const int   h      = 360;
                const float fov    = MathF.PI / 4f;
                const float aspect = (float)w / h;
                int totalFrames    = (int)Math.Round(duration * fps);

                Console.WriteLine($"metal-m8-kleinian-deep — Kleinian deep dive {duration:F1}s @ {fps} fps ({totalFrames} frames, {w}×{h})");
                Console.WriteLine("  Slow 0.5-revolution descent, r=14→2.5, h=7→-0.5. M8 field-scan drone + bells.");

                using var renderer = new MetalKleinianRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 220, HitEpsilon: 1.0e-3f, MaxDistance: 35f, NormalEpsilon: 1.5e-3f,
                    EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.04f, AOIntensity: 1.0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.4f);

                var bg      = new Color(0.005f, 0.008f, 0.022f);
                var surface = new Color(0.28f, 0.38f, 0.58f);
                var light   = Vector3.Normalize(new Vector3(1.0f, 3.0f, 0.8f));
                var palette = PaletteParams.Default;
                var post    = new PostProcessParams { Brightness = 1.05f, Contrast = 1.2f, Saturation = 1.4f, Gamma = 2.2f };

                // Slow half-revolution; camera descends from wide shot to close lattice
                const float startR      = 14f,  endR      = 2.5f;
                const float startHeight = 7.0f, endHeight = -0.5f;
                const float totalAngle  = 0.5f * 2f * MathF.PI;   // 0.5 revolutions — very slow

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-m8-deep-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                var sonicFrames  = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos  = new Vector3(startR, startHeight, 0f);
                int fsNonTrivial = 0;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-5} {"dep",-5} {"scl",-5} {"fs?",-4} render ms");
                Console.WriteLine($"  {new string('-', 55)}");

                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t     = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    float tEase = t * t * (3f - 2f * t);   // smoothstep: slow start and end

                    float r     = startR + (endR - startR) * tEase;
                    float hPos  = startHeight + (endHeight - startHeight) * tEase;
                    float angle = totalAngle * tEase;

                    var pos    = new Vector3(r * MathF.Cos(angle), hPos, r * MathF.Sin(angle));
                    var lookAt = new Vector3(0f, hPos * 0.08f, 0f);
                    var camFwd = Vector3.Normalize(lookAt - pos);
                    var cam    = new Camera3D(pos, lookAt, Vector3.UnitY, fov, aspect);

                    // Parameter animation — slower, wider range than the standard clip
                    float scaleAnim = 1.85f - 0.35f * MathF.Sin(MathF.PI * t);           // 1.85→1.50→1.85
                    float cellAnim  = 1.0f  + 0.30f * MathF.Sin(2f * MathF.PI * t + 0.5f); // 0.70↔1.30
                    float fixedAnim = 1.0f  + 0.22f * MathF.Sin(MathF.PI * t * 1.5f);    // 1.00↔1.22

                    var fractal = new KleinianParams {
                        Iterations  = 12,
                        Scale       = scaleAnim,
                        Cell        = cellAnim,
                        MinRadius   = 0.5f,
                        FixedRadius = fixedAnim,
                        Offset      = new Vector3(0.5f, 0.5f, 1.2f),
                        Fudge       = 0.65f,
                        BoundRadius = 8.0f,
                    };

                    float prevT     = totalFrames > 1 ? (fi - 1) / (float)(totalFrames - 1) : 0f;
                    float prevScale = fi > 0 ? 1.85f - 0.35f * MathF.Sin(MathF.PI * prevT) : scaleAnim;
                    float paramVel  = MathF.Abs(scaleAnim - prevScale) * fps;

                    uint[] pixels = renderer.RenderKleinian(fractal, cam, w, h, settings, bg, surface, light, palette, post);
                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                    bmp.Dispose();

                    var   stats                    = renderer.RunTelemetryPass(fractal, cam, settings);
                    var   (fsTL, fsTR, fsBL, fsBR) = renderer.RunFieldScanPass(fractal, cam, settings);
                    float camSpd                   = (pos - prevPos).Length() * fps;
                    float zoomV                    = Vector3.Dot(pos - prevPos, camFwd) * fps;
                    prevPos = pos;

                    if (fsTL != null && fsTL.Any(v => MathF.Abs(v) > 0.01f)) fsNonTrivial++;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } mc)
                    {
                        cells = new FractalSonicCell[mc.Length];
                        for (int ci = 0; ci < mc.Length; ci++)
                            cells[ci] = new FractalSonicCell(mc[ci].WorldPosition, mc[ci].HitRatio,
                                mc[ci].MeanDepth, mc[ci].StepComplexity, mc[ci].NormalMean,
                                mc[ci].TrapMean, mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable);
                    }

                    float[] geomPitches = GeometryScale.Kleinian(220f, fractal.FixedRadius, fractal.MinRadius, fractal.Scale);

                    sonicFrames.Add(new FractalSonicFrame(
                        Time:              fi / (double)fps,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: paramVel,
                        CameraPosition:    pos,
                        CameraForward:     camFwd,
                        CameraUp:          Vector3.UnitY,
                        Cells:             cells,
                        ZoomVelocity:      zoomV,
                        GeometryPitches:    geomPitches,
                        WaveshaperCurve:    stats?.WaveshaperCurve,
                        FieldScanWaveformTL: fsTL,
                        FieldScanWaveformTR: fsTR,
                        FieldScanWaveformBL: fsBL,
                        FieldScanWaveformBR: fsBR));

                    if (fi % fps == 0 || fi == totalFrames - 1)
                        Console.WriteLine($"  {fi,4}   {stats?.HitRatio ?? 0f:F3}  {stats?.MeanDepth ?? 0f,4:F1}  {scaleAnim:F2}  {(fsTL != null ? "Y" : "N")}    {renderer.LastComputeMs}ms");
                    else
                        Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                }

                totalSw.Stop();
                Console.WriteLine($"\n  {totalFrames} frames in {totalSw.ElapsedMilliseconds} ms  |  fs non-trivial: {fsNonTrivial}/{totalFrames}");

                Console.Write($"\n  Synthesising M8 deep audio ({sonicFrames.Count} frames @ {fps} Hz, voice=Kleinian, FieldScan)...");
                var synthSw = System.Diagnostics.Stopwatch.StartNew();
                var pcm = HybridSynth.Synthesize(sonicFrames,
                    voice:              FractalVoice.Kleinian,
                    wavetableSource:    WavetableSource.FieldScan,
                    temperamentCeiling: 0.88f,
                    controlRateHz:      fps);
                synthSw.Stop();

                // Save WAV next to the MP4 so it survives temp-dir cleanup
                string wavPath = Path.ChangeExtension(outMp4, ".wav");
                WavEncoder.Write(wavPath, pcm, HybridSynth.DefaultSampleRate, 2);
                double peakDb = 20.0 * Math.Log10((pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($" {synthSw.ElapsedMilliseconds} ms | peak {peakDb:F1} dBFS");

                Directory.CreateDirectory(Path.GetDirectoryName(outMp4)!);
                string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                $"-i \"{wavPath}\" " +
                                $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p -c:a aac -shortest \"{outMp4}\"";
                Console.Write("  Running ffmpeg...");
                var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.StandardError.ReadToEnd();  // drain to avoid pipe-buffer deadlock
                proc.WaitForExit();

                if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                Directory.Delete(frameDir, recursive: true);
                Console.WriteLine($" done.\nOK → {outMp4}  ({new FileInfo(outMp4).Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m8-kleinian-deep FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        if (args[0] == "metal-m9a-orbits")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m9a-orbits requires macOS."); return 1; }
            Console.WriteLine("metal-m9a-orbits — Mandelbox M9a orbit trajectory capture smoke test");
            var settings = new RaymarchSettings(
                MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                Gloss: 0f, F0: 0f, LightIntensity: 1f);
            try
            {
                using var r = new MetalMandelboxRenderer();
                if (!r.IsAvailable) { Console.Error.WriteLine("Mandelbox Metal unavailable"); return 1; }
                var cam = new Camera3D(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                var statsN = r.RunTelemetryPass(new MandelboxParams(), cam, settings);
                if (statsN == null) { Console.Error.WriteLine("Telemetry returned null"); return 1; }
                var stats = statsN.Value;
                if (stats.Cells == null || stats.Cells.Length == 0)
                    { Console.Error.WriteLine("No spatial cells returned"); return 1; }

                int mostEnergeticIdx = 0;
                float maxEnergy = -1f;
                for (int ti = 0; ti < stats.Cells.Length; ti++)
                {
                    ref readonly var cell = ref stats.Cells[ti];
                    if (cell.Energy > maxEnergy) { maxEnergy = cell.Energy; mostEnergeticIdx = ti; }

                    var traj = cell.OrbitTrajectory;
                    if (traj == null)
                    {
                        Console.Error.WriteLine($"  tile {ti:D2}: OrbitTrajectory is null (BUG — buffer not captured)");
                        return 1;
                    }

                    int bounded = 0;
                    float minMag = float.MaxValue, maxMag = 0f, sumMag = 0f;
                    for (int k = 0; k < traj.Length; k++)
                    {
                        if (traj[k].W < 0.5f) continue;
                        bounded++;
                        float mag = MathF.Sqrt(traj[k].X * traj[k].X + traj[k].Y * traj[k].Y + traj[k].Z * traj[k].Z);
                        if (mag < minMag) minMag = mag;
                        if (mag > maxMag) maxMag = mag;
                        sumMag += mag;
                    }
                    if (bounded == 0) { minMag = 0f; }
                    float meanMag = bounded > 0 ? sumMag / bounded : 0f;
                    Console.WriteLine($"  tile {ti:D2} [r{ti/4} c{ti%4}]: bounded={bounded}/{traj.Length}  mag=[{minMag:F3},{maxMag:F3}] mean={meanMag:F3}  energy={cell.Energy:F3}");
                }

                Console.WriteLine($"\nMost energetic cell: tile {mostEnergeticIdx} [r{mostEnergeticIdx/4} c{mostEnergeticIdx%4}] (energy={maxEnergy:F3})");
                Console.WriteLine("First 8 orbit points (x, y, z, w=bounded):");
                var best = stats.Cells[mostEnergeticIdx].OrbitTrajectory!;
                for (int k = 0; k < Math.Min(8, best.Length); k++)
                    Console.WriteLine($"  [{k}] ({best[k].X,7:F4}, {best[k].Y,7:F4}, {best[k].Z,7:F4}, w={best[k].W:F0})");

                Console.WriteLine("\nAccept criteria:");
                bool allNonNull   = stats.Cells.All(c => c.OrbitTrajectory != null);
                bool allHave128   = stats.Cells.All(c => c.OrbitTrajectory!.Length == 128);
                bool anyBounded   = stats.Cells.Any(c => c.OrbitTrajectory!.Any(p => p.W > 0.5f));
                bool notAllSame   = best.Select(p => p.X).Distinct().Count() > 1;
                Console.WriteLine($"  All 16 cells have non-null trajectory: {allNonNull}");
                Console.WriteLine($"  All trajectories have 128 points:      {allHave128}");
                Console.WriteLine($"  At least one bounded point exists:     {anyBounded}");
                Console.WriteLine($"  Most-energetic orbit has varied x:     {notAllSame}");

                if (!allNonNull || !allHave128 || !anyBounded || !notAllSame)
                    { Console.Error.WriteLine("FAIL — one or more accept criteria not met"); return 1; }
                Console.WriteLine("OK — M9a orbit trajectory capture verified.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m9a-orbits FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-m9b-direct [duration] [outDir]
        // Direct-orbit synthesis (M9b). Camera: same Mandelbox fly-in as metal-m7c-synth for A/B.
        // Scale gently ramps 2.0→1.6→2.0 over duration to demonstrate parameter-driven timbre change.
        // Writes m9b_direct.wav + m9b_hybrid.wav (OrbitMags) for side-by-side listening.
        if (args[0] == "metal-m9b-direct")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m9b-direct requires macOS."); return 1; }
            try
            {
                double duration   = args.Length >= 2 && double.TryParse(args[1], out var d) ? d : 10.0;
                string outDir     = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                int totalFrames   = (int)Math.Ceiling(duration * controlHz);

                Console.WriteLine($"metal-m9b-direct — Mandelbox direct-orbit synthesis, {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Console.WriteLine("  Camera: fly-in (0,5,30)→(0,1.5,7) | Scale ramp 2.0→1.6→2.0 (demonstrates param sensitivity)");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal backend not available."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var startPos = new Vector3(0f, 5f, 30f);
                var endPos   = new Vector3(0f, 1.5f, 7f);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                var frames   = new List<FractalSonicFrame>(totalFrames);
                Vector3 prevPos = startPos;

                Console.WriteLine($"\n  {"fi",-5}  {"hit",-5} {"depth",-5} {"orb/16",-6} {"scale"}");
                Console.WriteLine($"  {new string('-', 44)}");

                for (int fi = 0; fi < totalFrames; fi++)
                {
                    float t   = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                    var   pos = Vector3.Lerp(startPos, endPos, t);
                    var   camFwd = Vector3.Normalize(Vector3.Zero - pos);
                    var   cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                    // Scale ramp: 2.0 → 1.6 (first half) → 2.0 (second half)
                    float scale = t < 0.5f
                        ? 2.0f - 0.4f * (t / 0.5f)
                        : 1.6f + 0.4f * ((t - 0.5f) / 0.5f);
                    var fractal = new MandelboxParams { Scale = scale };

                    var   stats  = renderer.RunTelemetryPass(fractal, cam, settings);
                    float camSpd = (pos - prevPos).Length() * (float)controlHz;
                    float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)controlHz;
                    prevPos = pos;

                    FractalSonicCell[]? cells = null;
                    if (stats?.Cells is { Length: > 0 } mc)
                    {
                        cells = new FractalSonicCell[mc.Length];
                        for (int ci = 0; ci < mc.Length; ci++)
                            cells[ci] = new FractalSonicCell(
                                mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean,
                                mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable,
                                mc[ci].OrbitTrajectory);
                    }

                    int orbCount = cells?.Count(c => c.OrbitTrajectory != null) ?? 0;

                    frames.Add(new FractalSonicFrame(
                        Time:              fi / controlHz,
                        HitRatio:          stats?.HitRatio       ?? 0f,
                        MeanDepth:         stats?.MeanDepth       ?? 0f,
                        DepthVariance:     stats?.DepthVariance   ?? 0f,
                        StepMean:          stats?.StepMean        ?? 0f,
                        StepP90:           stats?.StepP90         ?? 0f,
                        NormalMean:        stats?.NormalMean       ?? Vector3.Zero,
                        NormalVariance:    stats?.NormalVariance   ?? 0f,
                        TrapMean:          stats?.TrapMean         ?? Vector4.Zero,
                        TrapVariance:      stats?.TrapVariance     ?? Vector4.Zero,
                        CameraSpeed:       camSpd,
                        ParameterVelocity: 0f,
                        CameraPosition:    pos,
                        CameraForward:     camFwd,
                        CameraUp:          Vector3.UnitY,
                        Cells:             cells,
                        ZoomVelocity:       zoomV,
                        WaveshaperCurve:    stats?.WaveshaperCurve,
                        FieldScanWaveformTL: stats?.FieldScanWaveformTL,
                        FieldScanWaveformTR: stats?.FieldScanWaveformTR,
                        FieldScanWaveformBL: stats?.FieldScanWaveformBL,
                        FieldScanWaveformBR: stats?.FieldScanWaveformBR));

                    if (fi % (int)controlHz == 0 || fi == totalFrames - 1)
                        Console.WriteLine($"  {fi,4}   {stats?.HitRatio ?? 0f:F3}  {stats?.MeanDepth ?? 0f,5:F1}  {orbCount,2}/16   {scale:F2}");
                    else
                        Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                }

                Console.WriteLine($"\n  {totalFrames} frames collected.");

                Directory.CreateDirectory(outDir);

                // Synthesise — DirectOrbitSynth
                Console.Write("\n  Synth A (direct-orbit)...  ");
                var swA = System.Diagnostics.Stopwatch.StartNew();
                var pcmDirect = DirectOrbitSynth.Synthesize(frames, controlRateHz: controlHz);
                swA.Stop();
                string directPath = Path.Combine(outDir, "m9b_direct.wav");
                WavEncoder.Write(directPath, pcmDirect, DirectOrbitSynth.DefaultSampleRate, 2);
                double peakDirect = 20.0 * Math.Log10(
                    (pcmDirect.Length > 0 ? pcmDirect.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{swA.ElapsedMilliseconds} ms | peak {peakDirect:F1} dBFS  → {directPath}");

                // Synthesise — HybridSynth (OrbitMags) for A/B comparison
                Console.Write("  Synth B (hybrid orbit-mags)...");
                var swB = System.Diagnostics.Stopwatch.StartNew();
                var pcmHybrid = HybridSynth.Synthesize(frames,
                    voice: FractalVoice.Mandelbox,
                    wavetableSource: WavetableSource.OrbitMags,
                    temperamentCeiling: 0.9f,
                    controlRateHz: controlHz);
                swB.Stop();
                string hybridPath = Path.Combine(outDir, "m9b_hybrid.wav");
                WavEncoder.Write(hybridPath, pcmHybrid, HybridSynth.DefaultSampleRate, 2);
                double peakHybrid = 20.0 * Math.Log10(
                    (pcmHybrid.Length > 0 ? pcmHybrid.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                Console.WriteLine($"{swB.ElapsedMilliseconds} ms | peak {peakHybrid:F1} dBFS  → {hybridPath}");

                // Determinism check: synthesise direct-orbit a second time and verify byte-identical
                Console.Write("  Determinism check (re-synthesise)...");
                var pcmDirect2 = DirectOrbitSynth.Synthesize(frames, controlRateHz: controlHz);
                bool isDeterministic = pcmDirect.Length == pcmDirect2.Length &&
                    pcmDirect.SequenceEqual(pcmDirect2);
                Console.WriteLine(isDeterministic ? " OK (byte-identical)" : " FAIL (non-deterministic!)");

                // Accept criteria
                Console.WriteLine("\nAccept criteria:");
                bool peakOk = peakDirect <= -6.0;
                Console.WriteLine($"  Deterministic (two runs byte-identical): {isDeterministic}");
                Console.WriteLine($"  Peak ≤ −6 dBFS: {peakOk} ({peakDirect:F1} dBFS)");
                Console.WriteLine($"  Orbit trajectories present (16/16):      {frames.Count > 0 && (frames[0].Cells?.All(c => c.OrbitTrajectory != null) ?? false)}");

                Console.WriteLine("\nListen and compare:");
                Console.WriteLine($"  Direct-orbit (M9b): {directPath}");
                Console.WriteLine($"  Hybrid (M7c ref):   {hybridPath}");

                if (!isDeterministic)
                { Console.Error.WriteLine("FAIL — non-deterministic output"); return 1; }
                if (!peakOk)
                { Console.Error.WriteLine($"FAIL — peak {peakDirect:F1} dBFS exceeds −6 dBFS limit"); return 1; }

                Console.WriteLine("\nOK — M9b direct-orbit synthesis ready for listening gate.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m9b-direct FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-m9c-direct [duration] [outDir]
        // M9c: DirectOrbitSynth on all four sonification fractals for cross-fractal A/B.
        // Each writes m9c_<fractal>_direct.wav. Accept: four fractals distinguishable by ear.
        if (args[0] == "metal-m9c-direct")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m9c-direct requires macOS."); return 1; }
            try
            {
                double duration    = args.Length >= 2 && double.TryParse(args[1], out var d9c) ? d9c : 10.0;
                string outDir      = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                int totalFrames    = (int)Math.Ceiling(duration * controlHz);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                Console.WriteLine($"metal-m9c-direct — DirectOrbitSynth across 4 fractals, {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Directory.CreateDirectory(outDir);

                static List<FractalSonicFrame> BuildFrames<TParams, TRenderer>(
                    int frames, double hz, float fovR, float asp,
                    Vector3 startPos, Vector3 endPos,
                    TParams fxParams,
                    TRenderer renderer,
                    Func<TRenderer, TParams, Camera3D, RaymarchSettings, FractalGeometryStats?> runPass,
                    RaymarchSettings settings)
                    where TRenderer : class
                {
                    var list = new List<FractalSonicFrame>(frames);
                    var prevPos = startPos;
                    for (int fi = 0; fi < frames; fi++)
                    {
                        float t   = frames > 1 ? fi / (float)(frames - 1) : 0f;
                        var pos   = Vector3.Lerp(startPos, endPos, t);
                        var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                        var cam   = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fovR, asp);
                        var stats = runPass(renderer, fxParams, cam, settings);
                        float camSpd = (pos - prevPos).Length() * (float)hz;
                        float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)hz;
                        prevPos = pos;

                        FractalSonicCell[]? cells = null;
                        if (stats?.Cells is { Length: > 0 } mc)
                        {
                            cells = new FractalSonicCell[mc.Length];
                            for (int ci = 0; ci < mc.Length; ci++)
                                cells[ci] = new FractalSonicCell(
                                    mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                    mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean,
                                    mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable,
                                    mc[ci].OrbitTrajectory);
                        }
                        list.Add(new FractalSonicFrame(
                            Time: fi / hz, HitRatio: stats?.HitRatio ?? 0f,
                            MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                            StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                            NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f,
                            TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                            CameraSpeed: camSpd, ParameterVelocity: 0f,
                            CameraPosition: pos, CameraForward: camFwd, CameraUp: Vector3.UnitY,
                            Cells: cells, ZoomVelocity: zoomV,
                            WaveshaperCurve: stats?.WaveshaperCurve,
                            FieldScanWaveformTL: stats?.FieldScanWaveformTL,
                            FieldScanWaveformTR: stats?.FieldScanWaveformTR,
                            FieldScanWaveformBL: stats?.FieldScanWaveformBL,
                            FieldScanWaveformBR: stats?.FieldScanWaveformBR));
                        if (fi % (int)hz == 0 || fi == frames - 1)
                            Console.Write($"\r    frame {fi + 1}/{frames}  orbs={(cells?.Count(c => c.OrbitTrajectory != null) ?? 0),2}/16");
                    }
                    Console.WriteLine();
                    return list;
                }

                static string SynthAndReport(List<FractalSonicFrame> frames, double hz,
                    string label, string outPath, FractalVoice voice)
                {
                    Console.Write($"  Synth {label}...  ");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pcm = DirectOrbitSynth.Synthesize(frames, controlRateHz: hz, voice: voice);
                    sw.Stop();
                    WavEncoder.Write(outPath, pcm, DirectOrbitSynth.DefaultSampleRate, 2);
                    double peak = 20.0 * Math.Log10(
                        (pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                    bool orbOk = frames.Count > 0 && (frames[0].Cells?.All(c => c.OrbitTrajectory != null) ?? false);
                    Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS | orbs {(orbOk ? "16/16" : "!!")} → {outPath}");
                    return outPath;
                }

                var settingsStd = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);
                var settingsMbx = settingsStd with { MaxSteps = 160, MaxDistance = 40f };

                // ── Mandelbox ───────────────────────────────────────────────────
                Console.WriteLine("\n[1/4] Mandelbox");
                using (var r = new MetalMandelboxRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 5f, 30f), new Vector3(0f, 1.5f, 7f),
                            new MandelboxParams { Scale = 2.0f }, r,
                            (rr, p, c, s) => rr.RunTelemetryPass(p, c, s), settingsMbx);
                        SynthAndReport(fr, controlHz, "mandelbox", Path.Combine(outDir, "m9c_mandelbox_direct.wav"), FractalVoice.Mandelbox);
                    }
                }

                // ── Mandelbulb ──────────────────────────────────────────────────
                Console.WriteLine("[2/4] Mandelbulb");
                using (var r = new MetalMandelbulbRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 2f, 6f), new Vector3(0f, 0.3f, 2f),
                            new MandelbulbParams(), r,
                            (rr, p, c, s) => rr.RunTelemetryPass(p, c, s), settingsStd);
                        SynthAndReport(fr, controlHz, "mandelbulb", Path.Combine(outDir, "m9c_mandelbulb_direct.wav"), FractalVoice.Mandelbulb);
                    }
                }

                // ── Kleinian ────────────────────────────────────────────────────
                Console.WriteLine("[3/4] Kleinian");
                using (var r = new MetalKleinianRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 0.5f, 4f), new Vector3(0f, 0.1f, 1.2f),
                            new KleinianParams(), r,
                            (rr, p, c, s) => rr.RunTelemetryPass(p, c, s), settingsStd);
                        SynthAndReport(fr, controlHz, "kleinian", Path.Combine(outDir, "m9c_kleinian_direct.wav"), FractalVoice.Kleinian);
                    }
                }

                // ── BurningShip ─────────────────────────────────────────────────
                Console.WriteLine("[4/4] BurningShip");
                using (var r = new MetalBurningShipRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 2f, 6f), new Vector3(0f, 0.5f, 2f),
                            new BurningShipParams(), r,
                            (rr, p, c, s) => rr.RunTelemetryPass(p, c, s), settingsStd);
                        SynthAndReport(fr, controlHz, "burningship", Path.Combine(outDir, "m9c_burningship_direct.wav"), FractalVoice.BurningShip);
                    }
                }

                Console.WriteLine($"\nOK — M9c outputs in {outDir}");
                Console.WriteLine("Accept: compare all four WAVs; fractals should be distinguishable by ear in direct mode.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m9c-direct FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-d-telemetry
        // Track D: telemetry kernel smoke test for Menger, Apollonian, KIFS, QJBox.
        // Accept: each kernel compiles, returns non-null stats, and all 16 orbit tiles capture.
        if (args[0] == "metal-d-telemetry")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-d-telemetry requires macOS."); return 1; }
            Console.WriteLine("metal-d-telemetry — telemetry smoke test for Menger, Apollonian, KIFS, QJBox");
            var settingsD = new RaymarchSettings(
                MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                Gloss: 0f, F0: 0f, LightIntensity: 1f);
            try
            {
                int failures = 0;
                void Check(string name, FractalGeometryStats? statsN)
                {
                    if (statsN == null) { Console.Error.WriteLine($"  {name,-11} telemetry returned null"); failures++; return; }
                    var s = statsN.Value;
                    int orbs = s.Cells?.Count(c => c.OrbitTrajectory != null) ?? 0;
                    bool ws  = s.WaveshaperCurve is { Length: 64 };
                    bool ok  = orbs == 16 && ws;
                    Console.WriteLine($"  {name,-11} hit={s.HitRatio:F3} depth={s.MeanDepth:F2} stepP90={s.StepP90:F1} cells={s.Cells?.Length ?? 0} orbs={orbs}/16 ws={(ws ? "64" : "!!")}  {(ok ? "PASS" : "FAIL")}");
                    if (!ok) failures++;
                }

                using (var r = new MetalMengerRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("Menger Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 2f, 8f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    Check("Menger", r.RunTelemetryPass(new MengerParams(), cam, settingsD));
                }
                using (var r = new MetalApollonianRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("Apollonian Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 1f, 6f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    Check("Apollonian", r.RunTelemetryPass(new ApollonianParams(), cam, settingsD));
                }
                using (var r = new MetalKifsRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("KIFS Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 1.5f, 7f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    Check("KIFS", r.RunTelemetryPass(new KifsParams(), cam, settingsD));
                }
                using (var r = new MetalQJBoxRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("QJBox Metal unavailable"); return 1; }
                    var cam = new Camera3D(new Vector3(0f, 1.5f, 7f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1.78f);
                    Check("QJBox", r.RunTelemetryPass(new QJBoxParams(), cam, settingsD));
                }

                if (failures > 0) { Console.Error.WriteLine($"metal-d-telemetry: {failures} kernel(s) failed."); return 1; }
                Console.WriteLine("All Track-D telemetry kernels compiled and returned full stats.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-d-telemetry FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-midi-telemetry — validator for the trimmed MIDI-only telemetry kernels
        // (improvement 4: geometry stats + 4x4 cells + full-res centroid; no sonification buffers).
        if (args[0] == "metal-midi-telemetry")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-midi-telemetry requires macOS."); return 1; }
            Console.WriteLine("metal-midi-telemetry — trimmed telemetry smoke test (MIDI-only fractals)");
            var settingsM = new RaymarchSettings(
                MaxSteps: 128, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                Gloss: 0f, F0: 0f, LightIntensity: 1f);
            var camFar   = new Camera3D(new Vector3(0f, 5f, 30f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f);
            var camClose = new Camera3D(new Vector3(0f, 2f,  8f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f);
            try
            {
                int failures = 0;
                void Check(string name, FractalGeometryStats? far, FractalGeometryStats? close)
                {
                    if (far == null || close == null) { Console.Error.WriteLine($"  {name,-12} telemetry returned null"); failures++; return; }
                    var f = far.Value; var c = close.Value;
                    bool cells   = c.Cells is { Length: 16 };
                    bool finite  = float.IsFinite(c.HitRatio) && float.IsFinite(c.MeanDepth)
                                && float.IsFinite(c.CentroidX) && float.IsFinite(c.CentroidY) && float.IsFinite(c.Dispersion);
                    bool closer  = c.HitRatio > f.HitRatio || c.HitRatio > 0.95f;
                    bool ok = cells && finite && closer && c.HitRatio > 0f;
                    Console.WriteLine($"  {name,-12} hitFar={f.HitRatio:F3} hitClose={c.HitRatio:F3} depth={c.MeanDepth:F2} cells={c.Cells?.Length ?? 0} centroid=({c.CentroidX:+0.00;-0.00},{c.CentroidY:+0.00;-0.00}) disp={c.Dispersion:F2}  {(ok ? "PASS" : "FAIL")}");
                    if (!ok) failures++;
                }

                using (var r = new MetalRotBoxRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("RotBox Metal unavailable"); return 1; }
                    Check("RotBox", r.RunTelemetryPass(new RotBoxParams(), camFar, settingsM),
                                    r.RunTelemetryPass(new RotBoxParams(), camClose, settingsM));
                }
                using (var r = new MetalQuaternionJuliaRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("QuaternionJulia Metal unavailable"); return 1; }
                    var qjFar   = new Camera3D(new Vector3(0f, 3f, 12f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f);
                    var qjClose = new Camera3D(new Vector3(0f, 0f, 3.5f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f);
                    Check("QuaternJulia", r.RunTelemetryPass(new QuaternionJuliaParams(), qjFar, settingsM),
                                          r.RunTelemetryPass(new QuaternionJuliaParams(), qjClose, settingsM));
                }

                // Cameras scaled to each fractal's bound radius (far ~6x, close ~2.2x).
                static Camera3D Cam(float z) => new(new Vector3(0f, z * 0.22f, z), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 64f / 36f);
                using (var r = new MetalHybridRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Hybrid Metal unavailable"); return 1; }
                  Check("Hybrid", r.RunTelemetryPass(new HybridParams(), Cam(24f), settingsM), r.RunTelemetryPass(new HybridParams(), Cam(9f), settingsM)); }
                using (var r = new MetalBicomplexRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Bicomplex Metal unavailable"); return 1; }
                  Check("Bicomplex", r.RunTelemetryPass(new BicomplexParams(), Cam(24f), settingsM), r.RunTelemetryPass(new BicomplexParams(), Cam(9f), settingsM)); }
                using (var r = new MetalPhoenixRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Phoenix Metal unavailable"); return 1; }
                  Check("Phoenix", r.RunTelemetryPass(new PhoenixParams(), Cam(24f), settingsM), r.RunTelemetryPass(new PhoenixParams(), Cam(9f), settingsM)); }
                using (var r = new MetalBiomorphRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Biomorph Metal unavailable"); return 1; }
                  Check("Biomorph", r.RunTelemetryPass(new BiomorphParams(), Cam(18f), settingsM), r.RunTelemetryPass(new BiomorphParams(), Cam(7f), settingsM)); }
                using (var r = new MetalMoselyRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Mosely Metal unavailable"); return 1; }
                  Check("Mosely", r.RunTelemetryPass(new MoselyParams(), Cam(14f), settingsM), r.RunTelemetryPass(new MoselyParams(), Cam(5f), settingsM)); }
                using (var r = new MetalPseudoKleinian4DRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("PseudoKleinian4D Metal unavailable"); return 1; }
                  Check("PseudoKln4D", r.RunTelemetryPass(new PseudoKleinian4DParams(), Cam(32f), settingsM), r.RunTelemetryPass(new PseudoKleinian4DParams(), Cam(16f), settingsM)); }
                using (var r = new MetalRiemannSphereRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("RiemannSphere Metal unavailable"); return 1; }
                  Check("RiemannSph", r.RunTelemetryPass(new RiemannSphereParams(), Cam(18f), settingsM), r.RunTelemetryPass(new RiemannSphereParams(), Cam(7f), settingsM)); }
                using (var r = new MetalMandalayRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Mandalay Metal unavailable"); return 1; }
                  Check("Mandalay", r.RunTelemetryPass(new MandalayParams(), Cam(32f), settingsM), r.RunTelemetryPass(new MandalayParams(), Cam(13f), settingsM)); }
                using (var r = new MetalAnisotropicRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("Anisotropic Metal unavailable"); return 1; }
                  Check("Anisotropic", r.RunTelemetryPass(new AnisotropicParams(), Cam(28f), settingsM), r.RunTelemetryPass(new AnisotropicParams(), Cam(11f), settingsM)); }
                using (var r = new MetalOrbitHybridRenderer())
                { if (!r.IsAvailable) { Console.Error.WriteLine("OrbitHybrid Metal unavailable"); return 1; }
                  Check("OrbitHybrid", r.RunTelemetryPass(new OrbitHybridParams(), Cam(24f), settingsM), r.RunTelemetryPass(new OrbitHybridParams(), Cam(9f), settingsM)); }
                using (var r = new MetalAttractorRenderer())
                {
                    if (!r.IsAvailable) { Console.Error.WriteLine("Attractor Metal unavailable"); return 1; }
                    var traj = Parsec.Core.Attractors.ThomasAttractor.Generate(new Parsec.Core.Attractors.AttractorParams { NumSteps = 50_000 });
                    var hash = Parsec.Core.Attractors.AttractorHash.Build(traj, gridSize: 64);
                    r.SetAttractor(hash);
                    var ctr = (hash.BoundsMin + hash.BoundsMax) * 0.5f;
                    float span = (hash.BoundsMax - hash.BoundsMin).Length();
                    var rp = new Parsec.Rendering.Gpu.AttractorRenderParams { TubeRadius = 0.06f, Fudge = 0.45f };
                    Camera3D acam(float mul) => new(ctr + new Vector3(0f, 0.3f, 1f) * span * mul, ctr, Vector3.UnitY, MathF.PI / 4f, 64f / 36f);
                    Check("Attractor", r.RunTelemetryPass(rp, acam(2.2f), settingsM, hash),
                                       r.RunTelemetryPass(rp, acam(0.9f), settingsM, hash));
                }

                if (failures > 0) { Console.Error.WriteLine($"metal-midi-telemetry: {failures} kernel(s) failed."); return 1; }
                Console.WriteLine("All MIDI-only telemetry kernels compiled and returned valid stats.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-midi-telemetry FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-d-direct [duration] [outDir]
        // Track D: DirectOrbitSynth listening renders for the four new telemetry fractals,
        // each with its own DirectOrbitProfile + geometry-derived lattice ratio.
        // Accept: four palettes distinguishable by ear; peaks below 0 dBFS; 16/16 orbits.
        if (args[0] == "metal-d-direct")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-d-direct requires macOS."); return 1; }
            try
            {
                double duration    = args.Length >= 2 && double.TryParse(args[1], out var dD) ? dD : 10.0;
                string outDir      = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double controlHz = 30.0;
                int totalFrames    = (int)Math.Ceiling(duration * controlHz);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                Console.WriteLine($"metal-d-direct — DirectOrbitSynth across Menger/Apollonian/KIFS/QJBox, {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames)");
                Directory.CreateDirectory(outDir);

                static List<FractalSonicFrame> BuildFrames<TParams, TRenderer>(
                    int frames, double hz, float fovR, float asp,
                    Vector3 startPos, Vector3 endPos,
                    TParams fxParams,
                    TRenderer renderer,
                    Func<TRenderer, TParams, Camera3D, RaymarchSettings, FractalGeometryStats?> runPass,
                    RaymarchSettings settings,
                    float latticeRatio)
                    where TRenderer : class
                {
                    var list = new List<FractalSonicFrame>(frames);
                    var prevPos = startPos;
                    for (int fi = 0; fi < frames; fi++)
                    {
                        float t   = frames > 1 ? fi / (float)(frames - 1) : 0f;
                        var pos   = Vector3.Lerp(startPos, endPos, t);
                        var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                        var cam   = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fovR, asp);
                        var stats = runPass(renderer, fxParams, cam, settings);
                        float camSpd = (pos - prevPos).Length() * (float)hz;
                        float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)hz;
                        prevPos = pos;

                        FractalSonicCell[]? cells = null;
                        if (stats?.Cells is { Length: > 0 } mc)
                        {
                            cells = new FractalSonicCell[mc.Length];
                            for (int ci = 0; ci < mc.Length; ci++)
                                cells[ci] = new FractalSonicCell(
                                    mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                    mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean,
                                    mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable,
                                    mc[ci].OrbitTrajectory);
                        }
                        list.Add(new FractalSonicFrame(
                            Time: fi / hz, HitRatio: stats?.HitRatio ?? 0f,
                            MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                            StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                            NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f,
                            TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                            CameraSpeed: camSpd, ParameterVelocity: 0f,
                            CameraPosition: pos, CameraForward: camFwd, CameraUp: Vector3.UnitY,
                            Cells: cells, ZoomVelocity: zoomV,
                            WaveshaperCurve: stats?.WaveshaperCurve,
                            FieldScanWaveformTL: stats?.FieldScanWaveformTL,
                            FieldScanWaveformTR: stats?.FieldScanWaveformTR,
                            FieldScanWaveformBL: stats?.FieldScanWaveformBL,
                            FieldScanWaveformBR: stats?.FieldScanWaveformBR,
                            LatticeRatio: latticeRatio));
                        if (fi % (int)hz == 0 || fi == frames - 1)
                            Console.Write($"\r    frame {fi + 1}/{frames}  orbs={(cells?.Count(c => c.OrbitTrajectory != null) ?? 0),2}/16");
                    }
                    Console.WriteLine();
                    return list;
                }

                static void SynthAndReport(List<FractalSonicFrame> frames, double hz,
                    string label, string outPath, FractalVoice voice)
                {
                    Console.Write($"  Synth {label}...  ");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pcm = DirectOrbitSynth.Synthesize(frames, controlRateHz: hz, voice: voice);
                    sw.Stop();
                    WavEncoder.Write(outPath, pcm, DirectOrbitSynth.DefaultSampleRate, 2);
                    double peak = 20.0 * Math.Log10(
                        (pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                    long zc = 0;
                    for (int i = 2; i < pcm.Length; i += 2)
                        if ((pcm[i] >= 0) != (pcm[i - 2] >= 0)) zc++;
                    double zcr = zc / Math.Max(1.0, pcm.Length / 2.0 / DirectOrbitSynth.DefaultSampleRate);
                    bool orbOk = frames.Count > 0 && (frames[^1].Cells?.All(c => c.OrbitTrajectory != null) ?? false);
                    Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS | zcr {zcr:F0}/s | orbs {(orbOk ? "16/16" : "!!")} → {outPath}");
                }

                var settingsD = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                // ── Menger ──────────────────────────────────────────────────────
                Console.WriteLine("\n[1/4] Menger");
                using (var r = new MetalMengerRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var p  = new MengerParams();
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 2f, 9f), new Vector3(0f, 0.5f, 2.4f),
                            p, r, (rr, pp, c, s) => rr.RunTelemetryPass(pp, c, s), settingsD,
                            GeometryScale.FoldScaleLatticeRatio(p.Scale));
                        SynthAndReport(fr, controlHz, "menger", Path.Combine(outDir, "d_menger_direct.wav"), FractalVoice.Menger);
                    }
                }

                // ── Apollonian ──────────────────────────────────────────────────
                Console.WriteLine("[2/4] Apollonian");
                using (var r = new MetalApollonianRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 1f, 6f), new Vector3(0f, 0.25f, 1.6f),
                            new ApollonianParams(), r,
                            (rr, pp, c, s) => rr.RunTelemetryPass(pp, c, s), settingsD,
                            0f); // profile default 19/16 (gasket pentatonic third)
                        SynthAndReport(fr, controlHz, "apollonian", Path.Combine(outDir, "d_apollonian_direct.wav"), FractalVoice.Apollonian);
                    }
                }

                // ── KIFS ────────────────────────────────────────────────────────
                Console.WriteLine("[3/4] KIFS");
                using (var r = new MetalKifsRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var p  = new KifsParams();
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 1.5f, 7f), new Vector3(0f, 0.4f, 2.0f),
                            p, r, (rr, pp, c, s) => rr.RunTelemetryPass(pp, c, s), settingsD,
                            GeometryScale.FoldScaleLatticeRatio(p.Scale)); // scale 2 → 0 → profile 4/3
                        SynthAndReport(fr, controlHz, "kifs", Path.Combine(outDir, "d_kifs_direct.wav"), FractalVoice.Kifs);
                    }
                }

                // ── QJBox ───────────────────────────────────────────────────────
                Console.WriteLine("[4/4] QJBox");
                using (var r = new MetalQJBoxRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var p  = new QJBoxParams();
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 1.5f, 7f), new Vector3(0f, 0.4f, 2.0f),
                            p, r, (rr, pp, c, s) => rr.RunTelemetryPass(pp, c, s), settingsD,
                            GeometryScale.FoldScaleLatticeRatio(p.Scale)); // |−1.8| → 1.8 lattice
                        SynthAndReport(fr, controlHz, "qjbox", Path.Combine(outDir, "d_qjbox_direct.wav"), FractalVoice.QJBox);
                    }
                }

                Console.WriteLine($"\nOK — Track-D outputs in {outDir}");
                Console.WriteLine("Accept: compare the four WAVs; registers/spaces/chimes should be clearly distinct.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-d-direct FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-d-modal [duration] [outDir] [modalBlend]
        // Modal-resonator A/B renders: Menger + Apollonian with their DirectOrbitProfile
        // modal signatures (hollow odd-harmonic tube vs inharmonic high-Q glass).
        // Writes raw + blended + modal playback sets from the same telemetry frames.
        // Accept: blended should sit perceptibly between raw texture and full modal body.
        if (args[0] == "metal-d-modal")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-d-modal requires macOS."); return 1; }
            try
            {
                double duration    = args.Length >= 2 && double.TryParse(args[1], out var dD) ? dD : 10.0;
                string outDir      = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                float modalBlend   = args.Length >= 4 && float.TryParse(args[3], out var dBlend)
                    ? Math.Clamp(dBlend, 0f, 1f) : 0.50f;
                const double controlHz = 30.0;
                int totalFrames    = (int)Math.Ceiling(duration * controlHz);
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;

                Console.WriteLine($"metal-d-modal — modal-bank DirectOrbit for Menger/Apollonian, {duration:F1}s @ {controlHz:F0} Hz ({totalFrames} frames), blend {modalBlend:F2}");
                Directory.CreateDirectory(outDir);

                static List<FractalSonicFrame> BuildFrames<TParams, TRenderer>(
                    int frames, double hz, float fovR, float asp,
                    Vector3 startPos, Vector3 endPos,
                    TParams fxParams,
                    TRenderer renderer,
                    Func<TRenderer, TParams, Camera3D, RaymarchSettings, FractalGeometryStats?> runPass,
                    RaymarchSettings settings,
                    float latticeRatio)
                    where TRenderer : class
                {
                    var list = new List<FractalSonicFrame>(frames);
                    var prevPos = startPos;
                    for (int fi = 0; fi < frames; fi++)
                    {
                        float t   = frames > 1 ? fi / (float)(frames - 1) : 0f;
                        var pos   = Vector3.Lerp(startPos, endPos, t);
                        var camFwd = Vector3.Normalize(Vector3.Zero - pos);
                        var cam   = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fovR, asp);
                        var stats = runPass(renderer, fxParams, cam, settings);
                        float camSpd = (pos - prevPos).Length() * (float)hz;
                        float zoomV  = Vector3.Dot(pos - prevPos, camFwd) * (float)hz;
                        prevPos = pos;

                        FractalSonicCell[]? cells = null;
                        if (stats?.Cells is { Length: > 0 } mc)
                        {
                            cells = new FractalSonicCell[mc.Length];
                            for (int ci = 0; ci < mc.Length; ci++)
                                cells[ci] = new FractalSonicCell(
                                    mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                    mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean,
                                    mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable,
                                    mc[ci].OrbitTrajectory);
                        }
                        list.Add(new FractalSonicFrame(
                            Time: fi / hz, HitRatio: stats?.HitRatio ?? 0f,
                            MeanDepth: stats?.MeanDepth ?? 0f, DepthVariance: stats?.DepthVariance ?? 0f,
                            StepMean: stats?.StepMean ?? 0f, StepP90: stats?.StepP90 ?? 0f,
                            NormalMean: stats?.NormalMean ?? Vector3.Zero,
                            NormalVariance: stats?.NormalVariance ?? 0f,
                            TrapMean: stats?.TrapMean ?? Vector4.Zero,
                            TrapVariance: stats?.TrapVariance ?? Vector4.Zero,
                            CameraSpeed: camSpd, ParameterVelocity: 0f,
                            CameraPosition: pos, CameraForward: camFwd, CameraUp: Vector3.UnitY,
                            Cells: cells, ZoomVelocity: zoomV,
                            WaveshaperCurve: stats?.WaveshaperCurve,
                            FieldScanWaveformTL: stats?.FieldScanWaveformTL,
                            FieldScanWaveformTR: stats?.FieldScanWaveformTR,
                            FieldScanWaveformBL: stats?.FieldScanWaveformBL,
                            FieldScanWaveformBR: stats?.FieldScanWaveformBR,
                            LatticeRatio: latticeRatio));
                        if (fi % (int)hz == 0 || fi == frames - 1)
                            Console.Write($"\r    frame {fi + 1}/{frames}  orbs={(cells?.Count(c => c.OrbitTrajectory != null) ?? 0),2}/16");
                    }
                    Console.WriteLine();
                    return list;
                }

                static void SynthAndReport(List<FractalSonicFrame> frames, double hz,
                    string label, string outPath, FractalVoice voice, bool enableModal, float bodyBlend)
                {
                    Console.Write($"  Synth {label}...  ");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var pcm = DirectOrbitSynth.Synthesize(frames, controlRateHz: hz, voice: voice,
                        enableModal: enableModal, modalBodyBlend: bodyBlend);
                    sw.Stop();
                    WavEncoder.Write(outPath, pcm, DirectOrbitSynth.DefaultSampleRate, 2);
                    double peak = 20.0 * Math.Log10(
                        (pcm.Length > 0 ? pcm.Max(s => Math.Abs((int)s)) : 1) / 32767.0 + 1e-10);
                    long zc = 0;
                    for (int i = 2; i < pcm.Length; i += 2)
                        if ((pcm[i] >= 0) != (pcm[i - 2] >= 0)) zc++;
                    double zcr = zc / Math.Max(1.0, pcm.Length / 2.0 / DirectOrbitSynth.DefaultSampleRate);
                    bool orbOk = frames.Count > 0 && (frames[^1].Cells?.All(c => c.OrbitTrajectory != null) ?? false);
                    Console.WriteLine($"{sw.ElapsedMilliseconds} ms | peak {peak:F1} dBFS | zcr {zcr:F0}/s | orbs {(orbOk ? "16/16" : "!!")} → {outPath}");
                }

                var settingsD = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                Console.WriteLine("\n[1/2] Menger (modal: hollow odd-harmonic tube)");
                using (var r = new MetalMengerRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var p  = new MengerParams();
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 2f, 9f), new Vector3(0f, 0.5f, 2.4f),
                            p, r, (rr, pp, c, s) => rr.RunTelemetryPass(pp, c, s), settingsD,
                            GeometryScale.FoldScaleLatticeRatio(p.Scale));
                        SynthAndReport(fr, controlHz, "menger raw", Path.Combine(outDir, "d_menger_raw.wav"), FractalVoice.Menger, enableModal: false, bodyBlend: 0f);
                        SynthAndReport(fr, controlHz, $"menger blend {modalBlend:F2}", Path.Combine(outDir, "d_menger_blend.wav"), FractalVoice.Menger, enableModal: true, bodyBlend: modalBlend);
                        SynthAndReport(fr, controlHz, "menger modal", Path.Combine(outDir, "d_menger_modal.wav"), FractalVoice.Menger, enableModal: true, bodyBlend: 1f);
                    }
                }

                Console.WriteLine("[2/2] Apollonian (modal: inharmonic high-Q glass)");
                using (var r = new MetalApollonianRenderer())
                {
                    if (!r.IsAvailable) Console.WriteLine("  SKIP (renderer unavailable)");
                    else
                    {
                        var fr = BuildFrames(totalFrames, controlHz, fov, aspect,
                            new Vector3(0f, 1f, 6f), new Vector3(0f, 0.25f, 1.6f),
                            new ApollonianParams(), r,
                            (rr, pp, c, s) => rr.RunTelemetryPass(pp, c, s), settingsD,
                            0f); // profile default 19/16 (gasket pentatonic third)
                        SynthAndReport(fr, controlHz, "apollonian raw", Path.Combine(outDir, "d_apollonian_raw.wav"), FractalVoice.Apollonian, enableModal: false, bodyBlend: 0f);
                        SynthAndReport(fr, controlHz, $"apollonian blend {modalBlend:F2}", Path.Combine(outDir, "d_apollonian_blend.wav"), FractalVoice.Apollonian, enableModal: true, bodyBlend: modalBlend);
                        SynthAndReport(fr, controlHz, "apollonian modal", Path.Combine(outDir, "d_apollonian_modal.wav"), FractalVoice.Apollonian, enableModal: true, bodyBlend: 1f);
                    }
                }

                Console.WriteLine($"\nOK — modal A/B outputs in {outDir}");
                Console.WriteLine("Accept: compare d_<fractal>_raw.wav / d_<fractal>_blend.wav / d_<fractal>_modal.wav — blend should sit clearly between texture and body.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-d-modal FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-m9d-animated [duration] [outDir]
        // DirectOrbit listening render with stronger geometry motion than the m9d fly-in:
        // helical camera spiral plus Mandelbox scale modulation.
        if (args[0] == "metal-m9d-animated")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m9d-animated requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d9da) ? d9da : 20.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;

                const double ctlHz       = 30.0;
                const float  radiusStart = 12.0f;
                const float  radiusEnd   = 5.0f;
                const float  elevStart   = 3.0f;
                const float  elevEnd     = 0.5f;
                const float  turns       = 2.25f;
                const float  fov         = MathF.PI / 4f;
                const float  aspect      = 16f / 9f;

                int nFrames = (int)Math.Ceiling(duration * ctlHz);
                Directory.CreateDirectory(outDir);

                Console.WriteLine($"metal-m9d-animated — DirectOrbit Mandelbox helical motion, {duration:F1}s @ {ctlHz:F0} Hz ({nFrames} frames)");
                Console.WriteLine($"  r {radiusStart}→{radiusEnd}, elev {elevStart}→{elevEnd}, {turns} turns, scale modulated 1.65–2.25");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames = new List<FractalSonicFrame>(nFrames);
                var prevPos = new Vector3(radiusStart, elevStart, 0f);
                float prevScale = 2.0f;

                Console.WriteLine($"\n  {"fi",-5} {"theta",-6} {"r",-4} {"scale",-5} {"hit",-5} {"orbs",-5}");
                Console.WriteLine($"  {new string('-', 44)}");

                for (int fi = 0; fi < nFrames; fi++)
                {
                    float t = nFrames > 1 ? fi / (float)(nFrames - 1) : 0f;
                    float smoothT = t * t * (3f - 2f * t);
                    float r = radiusStart + (radiusEnd - radiusStart) * smoothT;
                    float elev = elevStart + (elevEnd - elevStart) * smoothT;
                    float theta = 2f * MathF.PI * turns * t;
                    float scale = 1.95f
                        + 0.22f * MathF.Sin(2f * MathF.PI * 2.0f * t)
                        + 0.08f * MathF.Sin(2f * MathF.PI * 5.0f * t + 0.7f);

                    var pos = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var fwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                    var fp = new MandelboxParams { Scale = scale };
                    var st = renderer.RunTelemetryPass(fp, cam, settings);

                    float spd = (pos - prevPos).Length() * (float)ctlHz;
                    float zv = Vector3.Dot(pos - prevPos, fwd) * (float)ctlHz;
                    float paramVel = MathF.Abs(scale - prevScale) * (float)ctlHz;
                    prevPos = pos;
                    prevScale = scale;

                    FractalSonicCell[]? cells = null;
                    if (st?.Cells is { Length: > 0 } mc)
                    {
                        cells = new FractalSonicCell[mc.Length];
                        for (int ci = 0; ci < mc.Length; ci++)
                            cells[ci] = new FractalSonicCell(
                                mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean,
                                mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable,
                                mc[ci].OrbitTrajectory);
                    }

                    frames.Add(new FractalSonicFrame(
                        Time: fi / ctlHz, HitRatio: st?.HitRatio ?? 0f,
                        MeanDepth: st?.MeanDepth ?? 0f, DepthVariance: st?.DepthVariance ?? 0f,
                        StepMean: st?.StepMean ?? 0f, StepP90: st?.StepP90 ?? 0f,
                        NormalMean: st?.NormalMean ?? Vector3.Zero, NormalVariance: st?.NormalVariance ?? 0f,
                        TrapMean: st?.TrapMean ?? Vector4.Zero, TrapVariance: st?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: spd, ParameterVelocity: paramVel,
                        CameraPosition: pos, CameraForward: fwd, CameraUp: Vector3.UnitY,
                        Cells: cells, ZoomVelocity: zv,
                        WaveshaperCurve: st?.WaveshaperCurve,
                        FieldScanWaveformTL: st?.FieldScanWaveformTL, FieldScanWaveformTR: st?.FieldScanWaveformTR,
                        FieldScanWaveformBL: st?.FieldScanWaveformBL, FieldScanWaveformBR: st?.FieldScanWaveformBR));

                    if (fi % (int)ctlHz == 0 || fi == nFrames - 1)
                    {
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        int orbCount = cells?.Count(c => c.OrbitTrajectory != null) ?? 0;
                        Console.WriteLine($"  {fi,4}  {deg,4}°  {r,4:F1} {scale,5:F2} {st?.HitRatio ?? 0f:F3} {orbCount,2}/16");
                    }
                }

                Console.Write("\n  DirectOrbit export... ");
                var pcm = DirectOrbitSynth.Synthesize(frames, controlRateHz: ctlHz);
                string wav = Path.Combine(outDir, "m9d_animated_direct.wav");
                WavEncoder.Write(wav, pcm, DirectOrbitSynth.DefaultSampleRate, channels: 2);
                float pk = pcm.Max(s => MathF.Abs(s)) / 32767f;
                Console.WriteLine($"peak {20f * MathF.Log10(pk + 1e-9f):F1} dBFS  →  {wav}");

                Console.WriteLine("\nOK — animated DirectOrbit listening render complete.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m9d-animated FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-m9d-animated-burning [duration] [outDir]
        // BurningShip DirectOrbit listening render: helical camera spiral plus Power modulation.
        if (args[0] == "metal-m9d-animated-burning")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m9d-animated-burning requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d9db) ? d9db : 20.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;

                const double ctlHz       = 30.0;
                const float  radiusStart = 6.0f;
                const float  radiusEnd   = 2.0f;
                const float  elevStart   = 2.0f;
                const float  elevEnd     = 0.3f;
                const float  turns       = 2.25f;
                const float  fov         = MathF.PI / 4f;
                const float  aspect      = 16f / 9f;

                int nFrames = (int)Math.Ceiling(duration * ctlHz);
                Directory.CreateDirectory(outDir);

                Console.WriteLine($"metal-m9d-animated-burning — DirectOrbit BurningShip helical motion, {duration:F1}s @ {ctlHz:F0} Hz ({nFrames} frames)");
                Console.WriteLine($"  r {radiusStart}→{radiusEnd}, elev {elevStart}→{elevEnd}, {turns} turns, power modulated 1.75–2.55");

                using var renderer = new MetalBurningShipRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1e-3f, MaxDistance: 20f, NormalEpsilon: 1e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);

                var frames = new List<FractalSonicFrame>(nFrames);
                var prevPos = new Vector3(radiusStart, elevStart, 0f);
                float prevPower = 2.0f;

                Console.WriteLine($"\n  {"fi",-5} {"theta",-6} {"r",-4} {"power",-5} {"hit",-5} {"orbs",-5}");
                Console.WriteLine($"  {new string('-', 44)}");

                for (int fi = 0; fi < nFrames; fi++)
                {
                    float t = nFrames > 1 ? fi / (float)(nFrames - 1) : 0f;
                    float smoothT = t * t * (3f - 2f * t);
                    float r = radiusStart + (radiusEnd - radiusStart) * smoothT;
                    float elev = elevStart + (elevEnd - elevStart) * smoothT;
                    float theta = 2f * MathF.PI * turns * t;
                    float power = 2.15f
                        + 0.30f * MathF.Sin(2f * MathF.PI * 1.5f * t)
                        + 0.10f * MathF.Sin(2f * MathF.PI * 4.0f * t + 0.4f);

                    var pos = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                    var fwd = Vector3.Normalize(Vector3.Zero - pos);
                    var cam = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                    var fp = new BurningShipParams { Power = power };
                    var st = renderer.RunTelemetryPass(fp, cam, settings);

                    float spd = (pos - prevPos).Length() * (float)ctlHz;
                    float zv = Vector3.Dot(pos - prevPos, fwd) * (float)ctlHz;
                    float paramVel = MathF.Abs(power - prevPower) * (float)ctlHz;
                    prevPos = pos;
                    prevPower = power;

                    FractalSonicCell[]? cells = null;
                    if (st?.Cells is { Length: > 0 } mc)
                    {
                        cells = new FractalSonicCell[mc.Length];
                        for (int ci = 0; ci < mc.Length; ci++)
                            cells[ci] = new FractalSonicCell(
                                mc[ci].WorldPosition, mc[ci].HitRatio, mc[ci].MeanDepth,
                                mc[ci].StepComplexity, mc[ci].NormalMean, mc[ci].TrapMean,
                                mc[ci].Energy, mc[ci].RayWavetable, mc[ci].OrbitWavetable,
                                mc[ci].OrbitTrajectory);
                    }

                    frames.Add(new FractalSonicFrame(
                        Time: fi / ctlHz, HitRatio: st?.HitRatio ?? 0f,
                        MeanDepth: st?.MeanDepth ?? 0f, DepthVariance: st?.DepthVariance ?? 0f,
                        StepMean: st?.StepMean ?? 0f, StepP90: st?.StepP90 ?? 0f,
                        NormalMean: st?.NormalMean ?? Vector3.Zero, NormalVariance: st?.NormalVariance ?? 0f,
                        TrapMean: st?.TrapMean ?? Vector4.Zero, TrapVariance: st?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: spd, ParameterVelocity: paramVel,
                        CameraPosition: pos, CameraForward: fwd, CameraUp: Vector3.UnitY,
                        Cells: cells, ZoomVelocity: zv,
                        WaveshaperCurve: st?.WaveshaperCurve,
                        FieldScanWaveformTL: st?.FieldScanWaveformTL, FieldScanWaveformTR: st?.FieldScanWaveformTR,
                        FieldScanWaveformBL: st?.FieldScanWaveformBL, FieldScanWaveformBR: st?.FieldScanWaveformBR));

                    if (fi % (int)ctlHz == 0 || fi == nFrames - 1)
                    {
                        int deg = (int)(theta * 180f / MathF.PI) % 360;
                        int orbCount = cells?.Count(c => c.OrbitTrajectory != null) ?? 0;
                        Console.WriteLine($"  {fi,4}  {deg,4}°  {r,4:F1} {power,5:F2} {st?.HitRatio ?? 0f:F3} {orbCount,2}/16");
                    }
                }

                Console.Write("\n  DirectOrbit export... ");
                var pcm = DirectOrbitSynth.Synthesize(frames, controlRateHz: ctlHz, voice: FractalVoice.BurningShip);
                string wav = Path.Combine(outDir, "m9d_burningship_animated_direct.wav");
                WavEncoder.Write(wav, pcm, DirectOrbitSynth.DefaultSampleRate, channels: 2);
                float pk = pcm.Max(s => MathF.Abs(s)) / 32767f;
                Console.WriteLine($"peak {20f * MathF.Log10(pk + 1e-9f):F1} dBFS  →  {wav}");

                Console.WriteLine("\nOK — animated BurningShip DirectOrbit listening render complete.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m9d-animated-burning FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-burning-texture-mp4 [imagePath] [duration] [outFile]
        // BurningShip helical fly-in with surface texture + Power animation (1.8→2.8 sweep).
        if (args[0] == "metal-burning-texture-mp4")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-burning-texture-mp4 requires macOS."); return 1; }
            try
            {
                string imagePath = args.Length >= 2 ? args[1] : "/tmp/tex-test/testpattern.png";
                double duration  = args.Length >= 3 && double.TryParse(args[2], out var dbt) ? dbt : 10.0;
                string outFile   = args.Length >= 4 ? args[3] : ResolveOutputPath("burning_texture.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int   fps    = 24;
                const int   w      = 320;
                const int   h      = 180;
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * fps);

                if (!TryLoadSurfaceTextureImage(imagePath, out var texBytes, out int texW, out int texH, out int texRowBytes, out var loadError))
                { Console.Error.WriteLine(loadError); return 1; }

                Console.WriteLine($"metal-burning-texture-mp4 — BurningShip surface texture + Power morph, {duration:F1}s @ {fps} fps ({totalFrames} frames, {w}×{h})");
                Console.WriteLine($"  texture: {Path.GetFileName(imagePath)} ({texW}×{texH})  Power: 1.8→2.8 (1.5 cycles)");

                using var renderer = new MetalBurningShipRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1.5e-3f, MaxDistance: 25f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 0.9f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);
                var bg      = new Color(0.05f, 0.05f, 0.08f);
                var surface = new Color(0.70f, 0.45f, 0.25f);
                var light   = Vector3.Normalize(new Vector3(1.5f, 2.0f, 1.0f));
                var palette = PaletteParams.Default;
                var post    = new PostProcessParams { Brightness = 1.05f, Contrast = 1.1f, Saturation = 1.2f, Gamma = 2.2f };

                const float radiusStart = 6.0f;
                const float radiusEnd   = 2.5f;
                const float elevStart   = 2.0f;
                const float elevEnd     = 0.5f;
                const float turns       = 1.5f;

                MetalSurfaceTextureManager.SetImage(texBytes!, texW, texH, texRowBytes);
                MetalSurfaceTextureManager.SetControls(enabled: true, blend: 0.80f, scale: 1.0f, mode: 1);

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-btex-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                try
                {
                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t      = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        float smooth = t * t * (3f - 2f * t);
                        float r      = radiusStart + (radiusEnd - radiusStart) * smooth;
                        float elev   = elevStart   + (elevEnd   - elevStart)   * smooth;
                        float theta  = 2f * MathF.PI * turns * t;
                        var   pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                        var   cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);

                        // Animate Power 1.8→2.8 with 1.5 sinusoidal cycles so the geometry deforms
                        // visibly while the texture stays locked to the surface.
                        float power  = 2.3f + 0.5f * MathF.Sin(2f * MathF.PI * 1.5f * t);
                        var   fractal = new BurningShipParams { Power = power };

                        uint[] pixels = renderer.RenderBurningShip(fractal, cam, w, h, settings, bg, surface, light, palette, post);

                        var bmpInfo = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp     = new SKBitmap(bmpInfo);
                        var bmpBytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bmpBytes, 0, bmpBytes.Length);
                        Marshal.Copy(bmpBytes, 0, bmp.GetPixels(), bmpBytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();

                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  frame {fi + 1,4}/{totalFrames}  r={r:F2}  power={power:F2}");
                        else
                            Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }

                    string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                    $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p \"{outFile}\"";
                    Console.Write("\n  ffmpeg...");
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Console.WriteLine($" OK → {outFile}");
                }
                finally
                {
                    Directory.Delete(frameDir, recursive: true);
                    MetalSurfaceTextureManager.ClearImage();
                    MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                }

                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-burning-texture-mp4 FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-burning-video-texture [videoPath] [duration] [outFile]
        // BurningShip orbit-trap fly-in with a video as the surface texture.
        // If videoPath is omitted, generates a colorful animated test pattern via ffmpeg.
        // Uses UpdateImage (ReplaceRegion) to update texture in-place each frame — no per-frame realloc.
        if (args[0] == "metal-burning-video-texture")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-burning-video-texture requires macOS."); return 1; }
            try
            {
                string videoPath = args.Length >= 2 ? args[1] : "";
                double duration  = args.Length >= 3 && double.TryParse(args[2], out var dvt) ? dvt : 10.0;
                string outFile   = args.Length >= 4 ? args[3] : ResolveOutputPath("burning_video_texture.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int   fps    = 24;
                const int   w      = 320;
                const int   h      = 180;
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;
                int totalFrames = (int)Math.Ceiling(duration * fps);

                // Auto-generate a colorful animated test source if no video supplied.
                if (string.IsNullOrEmpty(videoPath))
                {
                    videoPath = Path.Combine(Path.GetTempPath(), "parsec-vtex-testsrc.mp4");
                    Console.Write("  No video supplied — generating animated test-pattern texture...");
                    var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                        $"-y -f lavfi -i \"testsrc2=size=320x180:rate=24\" -t 5 " +
                        $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p \"{videoPath}\"")
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    gen.StandardError.ReadToEnd();
                    gen.WaitForExit();
                    if (gen.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Console.WriteLine(" done.");
                }

                // Extract video frames as PNGs at render fps, cap texture width at 512 px.
                var texDir = Path.Combine(Path.GetTempPath(), $"parsec-vtex-{Guid.NewGuid():N}");
                Directory.CreateDirectory(texDir);
                Console.Write("  Extracting texture frames... ");
                var extract = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                    $"-y -i \"{videoPath}\" -vf \"fps={fps},scale='min(512,iw)':-2\" " +
                    $"\"{Path.Combine(texDir, "frame_%06d.png")}\"")
                    { RedirectStandardError = true, UseShellExecute = false })!;
                extract.StandardError.ReadToEnd();
                extract.WaitForExit();

                var texPaths = Directory.GetFiles(texDir, "frame_*.png").OrderBy(f => f).ToArray();
                Console.WriteLine($"{texPaths.Length} frames");
                if (texPaths.Length == 0) { Console.Error.WriteLine("No frames extracted from video."); return 1; }

                // Load all texture frames into memory.
                Console.Write("  Loading texture frames... ");
                var texFrames = new List<(byte[] Bytes, int W, int H, int RowBytes)>(texPaths.Length);
                foreach (var p in texPaths)
                    if (TryLoadSurfaceTextureImage(p, out var fb, out int fw, out int fh, out int frb, out _))
                        texFrames.Add((fb!, fw, fh, frb));
                Directory.Delete(texDir, recursive: true);
                Console.WriteLine($"{texFrames.Count} × {texFrames[0].W}×{texFrames[0].H}");
                if (texFrames.Count == 0) { Console.Error.WriteLine("Failed to load texture frames."); return 1; }

                Console.WriteLine($"metal-burning-video-texture — {duration:F1}s @ {fps}fps ({totalFrames} frames, {w}×{h})");
                Console.WriteLine($"  source: {Path.GetFileName(videoPath)}, {texFrames.Count} tex frames, orbit-trap mode, loops={totalFrames / texFrames.Count + 1}×");

                using var renderer = new MetalBurningShipRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1.5e-3f, MaxDistance: 25f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 0.9f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);
                var bg      = new Color(0.05f, 0.05f, 0.08f);
                var surface = new Color(0.70f, 0.45f, 0.25f);
                var light   = Vector3.Normalize(new Vector3(1.5f, 2.0f, 1.0f));
                var palette = PaletteParams.Default;
                var post    = new PostProcessParams { Brightness = 1.05f, Contrast = 1.1f, Saturation = 1.2f, Gamma = 2.2f };

                const float radiusStart = 6.0f, radiusEnd = 2.5f;
                const float elevStart   = 2.0f, elevEnd   = 0.5f;
                const float turns       = 1.5f;

                var frameDir = Path.Combine(Path.GetTempPath(), $"parsec-bvtex-{Guid.NewGuid():N}");
                Directory.CreateDirectory(frameDir);

                try
                {
                    // Prime the texture cache with the first frame; subsequent frames use UpdateImage.
                    var (f0b, f0w, f0h, f0r) = texFrames[0];
                    MetalSurfaceTextureManager.SetImage(f0b, f0w, f0h, f0r);
                    MetalSurfaceTextureManager.SetControls(enabled: true, blend: 0.85f, scale: 1.0f, mode: 1);

                    for (int fi = 0; fi < totalFrames; fi++)
                    {
                        float t      = totalFrames > 1 ? fi / (float)(totalFrames - 1) : 0f;
                        float smooth = t * t * (3f - 2f * t);
                        float r      = radiusStart + (radiusEnd - radiusStart) * smooth;
                        float elev   = elevStart   + (elevEnd   - elevStart)   * smooth;
                        float theta  = 2f * MathF.PI * turns * t;
                        var   pos    = new Vector3(MathF.Cos(theta) * r, elev, MathF.Sin(theta) * r);
                        var   cam    = new Camera3D(pos, Vector3.Zero, Vector3.UnitY, fov, aspect);
                        float power  = 2.3f + 0.5f * MathF.Sin(2f * MathF.PI * 1.5f * t);
                        var   fractal = new BurningShipParams { Power = power };

                        // Advance video texture — UpdateImage replaces pixels in the cached MTLTexture
                        // without reallocation; first frame already set above.
                        var (fb, fw, fh, frb) = texFrames[fi % texFrames.Count];
                        if (fi > 0) MetalSurfaceTextureManager.UpdateImage(fb, fw, fh, frb);

                        uint[] pixels = renderer.RenderBurningShip(fractal, cam, w, h, settings, bg, surface, light, palette, post);

                        var bmpInfo  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                        var bmp      = new SKBitmap(bmpInfo);
                        var bmpBytes = new byte[pixels.Length * 4];
                        Buffer.BlockCopy(pixels, 0, bmpBytes, 0, bmpBytes.Length);
                        Marshal.Copy(bmpBytes, 0, bmp.GetPixels(), bmpBytes.Length);
                        ImageOutput.SavePng(bmp, Path.Combine(frameDir, $"frame_{fi:D4}.png"));
                        bmp.Dispose();

                        if (fi % fps == 0 || fi == totalFrames - 1)
                            Console.WriteLine($"  frame {fi + 1,4}/{totalFrames}  r={r:F2}  power={power:F2}  tex={fi % texFrames.Count}");
                        else
                            Console.Write($"\r  frame {fi + 1}/{totalFrames}");
                    }

                    string ffArgs = $"-y -framerate {fps} -i \"{Path.Combine(frameDir, "frame_%04d.png")}\" " +
                                    $"-c:v libx264 -crf 18 -preset fast -pix_fmt yuv420p \"{outFile}\"";
                    Console.Write("\n  ffmpeg...");
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                        { RedirectStandardError = true, UseShellExecute = false })!;
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                    Console.WriteLine($" OK → {outFile}");
                }
                finally
                {
                    Directory.Delete(frameDir, recursive: true);
                    MetalSurfaceTextureManager.ClearImage();
                    MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                }

                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-burning-video-texture FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-closeup-hq [duration] [out.mp4]
        // High-quality close-up: Mandelbox with Mandelbrot zoom texture (triplanar), camera
        // approaches from radius 7→4.5 while arcing 90°. 960×540 4× SSAA (≈1920×1080 quality).
        // Mandelbrot zoom radius 1.5→1e-6 over the clip. HDR: contrast 1.4, saturation 1.7, tanh.
        if (args[0] == "metal-closeup-hq")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-closeup-hq requires macOS."); return 1; }
            try
            {
                double duration  = args.Length >= 2 && double.TryParse(args[1], out var dch) ? dch : 8.0;
                string outFile   = args.Length >= 3 ? args[2] : ResolveOutputPath("closeup_hq.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int fps    = 24;
                const int sceneW = 960, sceneH = 540;
                const int texW   = 384, texH   = 384;
                int totalFrames  = (int)Math.Ceiling(duration * fps);

                const string centerRe   = "-0.743643887037158704752191506114774";
                const string centerIm   =  "0.131825904205311970493132056385139";
                const double startRadius = 1.5;
                const double endRadius   = 1e-6;
                double logStart = Math.Log(startRadius);
                double logEnd   = Math.Log(endRadius);

                Console.WriteLine($"metal-closeup-hq — Mandelbox close fly-in, 4× SSAA, {sceneW}×{sceneH}, {duration:F1}s @ {fps}fps ({totalFrames} frames)");
                Console.WriteLine($"  Mandelbrot zoom texture {texW}×{texH}  radius {startRadius}→{endRadius:e1}  camera radius 7→4.5");

                using var deepRenderer  = new MetalDeepZoomRenderer();
                if (!deepRenderer.IsAvailable) { Console.Error.WriteLine("Metal deep-zoom unavailable."); return 1; }
                using var sceneRenderer = new MetalMandelboxRenderer();
                if (!sceneRenderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var texPalette = new PaletteParams
                {
                    Base      = new Vector3(0.5f, 0.5f, 0.5f),
                    Amp       = new Vector3(0.5f, 0.45f, 0.4f),
                    Frequency = 1.8f,
                    Phase     = new Vector3(0.0f, 0.25f, 0.58f),
                    TrapScale = 1.0f, ShellMix = 0f,
                };
                var texBg       = new Color(0.01f, 0.01f, 0.03f);
                var texSettings = new RaymarchSettings(
                    MaxSteps: 0, HitEpsilon: 0, MaxDistance: 0, NormalEpsilon: 0,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                    HeroSamples: 1,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0, F0: 0, LightIntensity: 0);

                var sceneFractal  = new MandelboxParams();
                var sceneSettings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 5e-4f, MaxDistance: 30f, NormalEpsilon: 8e-4f,
                    EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 14f,
                    EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.1f,
                    HeroSamples: 4,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);

                var post = new PostProcessParams
                {
                    Brightness = 1.0f, Contrast  = 1.4f,
                    Gamma      = 0.95f, Saturation = 1.7f,
                    HdrEnabled = true,
                };
                var sceneBg      = new Color(0.03f, 0.03f, 0.06f);
                var sceneSurface = new Color(0.65f, 0.60f, 0.55f);
                var sceneLight   = Vector3.Normalize(new Vector3(1.5f, 2.5f, 0.8f));
                var scenePalette = new PaletteParams
                {
                    Base      = new Vector3(0.55f, 0.50f, 0.45f),
                    Amp       = new Vector3(0.35f, 0.30f, 0.25f),
                    Frequency = 0.7f,
                    Phase     = new Vector3(0.0f, 0.15f, 0.30f),
                    TrapScale = 0.8f, ShellMix = 0.2f,
                    TrapMix   = new Vector3(0.5f, 0.4f, 0.3f),
                };

                string frameDir = Path.Combine(Path.GetDirectoryName(outFile)!, "closeup-hq-frames");
                Directory.CreateDirectory(frameDir);

                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);

                var texBytes = new byte[texW * texH * 4];

                for (int i = 0; i < totalFrames; i++)
                {
                    float  t    = (float)i / fps;
                    double tN   = (double)i / totalFrames;
                    double radius = Math.Exp(logStart + tN * (logEnd - logStart));

                    // Camera approaches and arcs: radius 7→4.5, sweeps 90° horizontally, rises slightly
                    float camR   = 7.0f - (float)tN * 2.5f;
                    float camAng = (float)tN * MathF.PI * 0.5f;
                    float camY   = 1.0f + (float)tN * 1.5f;
                    var camera   = new Camera3D(
                        new Vector3(camR * MathF.Sin(camAng), camY, camR * MathF.Cos(camAng)),
                        new Vector3(0f, 0.3f, 0f),
                        Vector3.UnitY, MathF.PI / 4f, (float)sceneW / sceneH);

                    float blend = Math.Min(t / 1.5f, 1.0f) * 0.72f;

                    // --- 2D Mandelbrot zoom → texture ---
                    var view = new Parsec.Rendering.DeepZoom.DeepZoomView
                    {
                        CenterRe = centerRe, CenterIm = centerIm,
                        Radius   = radius,   Formula  = 0,
                    };
                    uint[] texPixels = deepRenderer.Render(view, texW, texH, texPalette, texBg, texSettings);
                    Buffer.BlockCopy(texPixels, 0, texBytes, 0, texBytes.Length);

                    if (i == 0)
                        MetalSurfaceTextureManager.SetImage(texBytes, texW, texH, texW * 4);
                    else
                        MetalSurfaceTextureManager.UpdateImage(texBytes, texW, texH, texW * 4);
                    MetalSurfaceTextureManager.SetControls(enabled: true, blend: blend, scale: 0.7f, mode: 0);

                    // --- 3D Mandelbox 4× SSAA ---
                    uint[] scenePixels = sceneRenderer.RenderMandelbox(
                        sceneFractal, camera, sceneW, sceneH,
                        sceneSettings, sceneBg, sceneSurface, sceneLight, scenePalette);

                    string framePath = Path.Combine(frameDir, $"frame_{i:D5}.png");
                    var info = new SKImageInfo(sceneW, sceneH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var bmp = new SKBitmap(info);
                    var saveBytes = new byte[scenePixels.Length * 4];
                    Buffer.BlockCopy(scenePixels, 0, saveBytes, 0, saveBytes.Length);
                    Marshal.Copy(saveBytes, 0, bmp.GetPixels(), saveBytes.Length);
                    ImageOutput.SavePng(bmp, framePath);

                    if (i % 24 == 0 || i == totalFrames - 1)
                        Console.WriteLine($"  frame {i + 1}/{totalFrames}  camR={camR:F2}  radius={radius:e2}  blend={blend:F2}  t={t:F1}s");
                }

                Console.WriteLine($"  muxing → {outFile} ...");
                var ffArgs = $"-y -framerate {fps} -i \"{frameDir}/frame_%05d.png\" -c:v libx264 -preset slow -crf 15 -pix_fmt yuv420p \"{outFile}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed"); return 1; }
                Directory.Delete(frameDir, recursive: true);
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                Console.WriteLine($"  done → {outFile}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-closeup-hq FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-closeup-oracle [duration] [out.mp4]
        // Close fly-in (radius 7→4.5) + 4× SSAA + Mandelbox Scale morph (1.75→2.25, 1.5 cycles)
        // + feedback: texture = blend(Mandelbrot_zoom, prev_frame). Feedback weight builds 0→0.45
        // from t=2s. Same quality settings as metal-closeup-hq.
        if (args[0] == "metal-closeup-oracle")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-closeup-oracle requires macOS."); return 1; }
            try
            {
                double duration  = args.Length >= 2 && double.TryParse(args[1], out var dco) ? dco : 10.0;
                string outFile   = args.Length >= 3 ? args[2] : ResolveOutputPath("closeup_oracle.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int fps    = 24;
                const int sceneW = 960, sceneH = 540;
                const int texW   = 384, texH   = 384;
                int totalFrames  = (int)Math.Ceiling(duration * fps);

                const string centerRe   = "-0.743643887037158704752191506114774";
                const string centerIm   =  "0.131825904205311970493132056385139";
                const double startRadius = 1.5;
                const double endRadius   = 1e-6;
                double logStart = Math.Log(startRadius);
                double logEnd   = Math.Log(endRadius);

                Console.WriteLine($"metal-closeup-oracle — close fly-in + Scale morph + feedback, 4× SSAA, {sceneW}×{sceneH}, {duration:F1}s @ {fps}fps ({totalFrames} frames)");

                using var deepRenderer  = new MetalDeepZoomRenderer();
                if (!deepRenderer.IsAvailable) { Console.Error.WriteLine("Metal deep-zoom unavailable."); return 1; }
                using var sceneRenderer = new MetalMandelboxRenderer();
                if (!sceneRenderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var texPalette = new PaletteParams
                {
                    Base = new Vector3(0.5f, 0.5f, 0.5f), Amp = new Vector3(0.5f, 0.45f, 0.4f),
                    Frequency = 1.8f, Phase = new Vector3(0.0f, 0.25f, 0.58f),
                    TrapScale = 1.0f, ShellMix = 0f,
                };
                var texBg       = new Color(0.01f, 0.01f, 0.03f);
                var texSettings = new RaymarchSettings(
                    MaxSteps: 0, HitEpsilon: 0, MaxDistance: 0, NormalEpsilon: 0,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                    HeroSamples: 1,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0, F0: 0, LightIntensity: 0);

                var sceneSettings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 5e-4f, MaxDistance: 30f, NormalEpsilon: 8e-4f,
                    EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 14f,
                    EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.1f,
                    HeroSamples: 4,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.2f);

                var post = new PostProcessParams
                {
                    Brightness = 1.0f, Contrast = 1.4f, Gamma = 0.95f,
                    Saturation = 1.7f, HdrEnabled = true,
                };
                var sceneBg      = new Color(0.03f, 0.03f, 0.06f);
                var sceneSurface = new Color(0.65f, 0.60f, 0.55f);
                var sceneLight   = Vector3.Normalize(new Vector3(1.5f, 2.5f, 0.8f));
                var scenePalette = new PaletteParams
                {
                    Base = new Vector3(0.55f, 0.50f, 0.45f), Amp = new Vector3(0.35f, 0.30f, 0.25f),
                    Frequency = 0.7f, Phase = new Vector3(0.0f, 0.15f, 0.30f),
                    TrapScale = 0.8f, ShellMix = 0.2f, TrapMix = new Vector3(0.5f, 0.4f, 0.3f),
                };

                string frameDir = Path.Combine(Path.GetDirectoryName(outFile)!, "closeup-oracle-frames");
                Directory.CreateDirectory(frameDir);

                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);

                int texLen          = texW * texH * 4;
                var mandelbrotBytes = new byte[texLen];
                var feedbackBytes   = new byte[texLen];
                var blendedBytes    = new byte[texLen];
                bool bootstrapped   = false;

                for (int i = 0; i < totalFrames; i++)
                {
                    float  t    = (float)i / fps;
                    double tN   = (double)i / totalFrames;
                    double radius = Math.Exp(logStart + tN * (logEnd - logStart));

                    // Mandelbox Scale morphs 1.75→2.25, 1.5 cycles
                    float scale_fractal = 2.0f + 0.25f * MathF.Sin(2f * MathF.PI * 1.5f * (float)tN);

                    // Camera: radius 7→4.5, arc 90°, rise
                    float camR   = 7.0f - (float)tN * 2.5f;
                    float camAng = (float)tN * MathF.PI * 0.5f;
                    float camY   = 1.0f + (float)tN * 1.5f;
                    var camera   = new Camera3D(
                        new Vector3(camR * MathF.Sin(camAng), camY, camR * MathF.Cos(camAng)),
                        new Vector3(0f, 0.3f, 0f), Vector3.UnitY,
                        MathF.PI / 4f, (float)sceneW / sceneH);

                    // Feedback weight builds 0→0.45 from t=2s; texture blend ramps over 1.5s
                    float feedbackWeight   = Math.Min(Math.Max(t - 2f, 0f) / 3f, 1f) * 0.45f;
                    float mandelbrotWeight = 1f - feedbackWeight;
                    float textureBlend     = Math.Min(t / 1.5f, 1.0f) * 0.72f;

                    // --- 2D Mandelbrot zoom ---
                    var view = new Parsec.Rendering.DeepZoom.DeepZoomView
                    {
                        CenterRe = centerRe, CenterIm = centerIm,
                        Radius = radius, Formula = 0,
                    };
                    uint[] texPixels = deepRenderer.Render(view, texW, texH, texPalette, texBg, texSettings);
                    Buffer.BlockCopy(texPixels, 0, mandelbrotBytes, 0, texLen);

                    // --- CPU blend: Mandelbrot + prev-frame feedback ---
                    if (!bootstrapped)
                    {
                        Buffer.BlockCopy(mandelbrotBytes, 0, blendedBytes, 0, texLen);
                    }
                    else
                    {
                        for (int p = 0; p < texLen; p++)
                            blendedBytes[p] = (byte)(mandelbrotBytes[p] * mandelbrotWeight + feedbackBytes[p] * feedbackWeight);
                    }

                    if (!bootstrapped)
                    {
                        MetalSurfaceTextureManager.SetImage(blendedBytes, texW, texH, texW * 4);
                        bootstrapped = true;
                    }
                    else
                    {
                        MetalSurfaceTextureManager.UpdateImage(blendedBytes, texW, texH, texW * 4);
                    }
                    MetalSurfaceTextureManager.SetControls(enabled: true, blend: textureBlend, scale: 0.7f, mode: 0);

                    // --- 3D Mandelbox 4× SSAA ---
                    var fractal    = new MandelboxParams { Scale = scale_fractal };
                    uint[] scenePixels = sceneRenderer.RenderMandelbox(
                        fractal, camera, sceneW, sceneH,
                        sceneSettings, sceneBg, sceneSurface, sceneLight, scenePalette, post);

                    // Store scene output as feedback
                    int sceneLen = scenePixels.Length * 4;
                    if (sceneLen >= texLen)
                        Buffer.BlockCopy(scenePixels, 0, feedbackBytes, 0, texLen);
                    else
                    {
                        Buffer.BlockCopy(scenePixels, 0, feedbackBytes, 0, sceneLen);
                        Buffer.BlockCopy(scenePixels, 0, feedbackBytes, sceneLen, texLen - sceneLen);
                    }

                    string framePath = Path.Combine(frameDir, $"frame_{i:D5}.png");
                    var info = new SKImageInfo(sceneW, sceneH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var bmp = new SKBitmap(info);
                    var saveBytes = new byte[scenePixels.Length * 4];
                    Buffer.BlockCopy(scenePixels, 0, saveBytes, 0, saveBytes.Length);
                    Marshal.Copy(saveBytes, 0, bmp.GetPixels(), saveBytes.Length);
                    ImageOutput.SavePng(bmp, framePath);

                    if (i % 24 == 0 || i == totalFrames - 1)
                        Console.WriteLine($"  frame {i + 1}/{totalFrames}  scale={scale_fractal:F3}  camR={camR:F2}  fbWeight={feedbackWeight:F2}  blend={textureBlend:F2}  t={t:F1}s");
                }

                Console.WriteLine($"  muxing → {outFile} ...");
                var ffArgs = $"-y -framerate {fps} -i \"{frameDir}/frame_%05d.png\" -c:v libx264 -preset slow -crf 15 -pix_fmt yuv420p \"{outFile}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed"); return 1; }
                Directory.Delete(frameDir, recursive: true);
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                Console.WriteLine($"  done → {outFile}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-closeup-oracle FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-oracle [duration] [out.mp4]
        // Experimental: BurningShip orbit-trap with texture = blend(Mandelbrot_zoom, prev_frame).
        // Three recursion layers: Mandelbrot structure mapped through BurningShip iteration-space
        // UV, previous frames haunting the surface via feedback, Power morphing the UV space live.
        // Aggressive HDR post (contrast 1.5, tanh, saturation 1.8). Two Metal renders + CPU blend.
        if (args[0] == "metal-oracle")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-oracle requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var dor) ? dor : 20.0;
                string outFile  = args.Length >= 3 ? args[2] : ResolveOutputPath("oracle.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int fps    = 24;
                const int sceneW = 480, sceneH = 270;
                const int texW   = 320, texH   = 320;   // square — orbit trap UV is roughly square
                int totalFrames  = (int)Math.Ceiling(duration * fps);

                // Seahorse Valley deep zoom — saturated rainbow cosine palette
                const string centerRe   = "-0.743643887037158704752191506114774";
                const string centerIm   =  "0.131825904205311970493132056385139";
                const double startRadius = 1.5;
                const double endRadius   = 1e-10;
                double logStart = Math.Log(startRadius);
                double logEnd   = Math.Log(endRadius);

                Console.WriteLine($"metal-oracle — BurningShip orbit-trap × Mandelbrot zoom × feedback, {duration:F1}s @ {fps}fps ({totalFrames} frames)");
                Console.WriteLine($"  texture: {texW}×{texH}  scene: {sceneW}×{sceneH}  Power: 1.4→2.6  zoom: {startRadius}→{endRadius:e1}");

                using var deepRenderer  = new MetalDeepZoomRenderer();
                if (!deepRenderer.IsAvailable) { Console.Error.WriteLine("Metal deep-zoom unavailable."); return 1; }
                using var sceneRenderer = new MetalBurningShipRenderer();
                if (!sceneRenderer.IsAvailable) { Console.Error.WriteLine("Metal BurningShip unavailable."); return 1; }

                var texPalette = new PaletteParams
                {
                    Base      = new Vector3(0.5f, 0.5f, 0.5f),
                    Amp       = new Vector3(0.5f, 0.5f, 0.45f),
                    Frequency = 1.5f,
                    Phase     = new Vector3(0.0f, 0.20f, 0.55f),
                    TrapScale = 1.0f, ShellMix = 0f,
                };
                var texBg       = new Color(0.01f, 0.01f, 0.03f);
                var texSettings = new RaymarchSettings(
                    MaxSteps: 0, HitEpsilon: 0, MaxDistance: 0, NormalEpsilon: 0,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                    HeroSamples: 1,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0, F0: 0, LightIntensity: 0);

                var sceneSettings = new RaymarchSettings(
                    MaxSteps: 96, HitEpsilon: 1.2e-3f, MaxDistance: 25f, NormalEpsilon: 1.8e-3f,
                    EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 12f,
                    EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 1.0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.3f);

                var post = new PostProcessParams
                {
                    Brightness = 1.05f, Contrast   = 1.5f,
                    Gamma      = 1.0f,  Saturation  = 1.8f,
                    HdrEnabled = true,
                };

                var sceneBg      = new Color(0.02f, 0.02f, 0.05f);
                var sceneSurface = new Color(0.55f, 0.45f, 0.60f);   // purple-ish base tint
                var sceneLight   = Vector3.Normalize(new Vector3(0.8f, 1.8f, 1.2f));
                var scenePalette = new PaletteParams
                {
                    Base      = new Vector3(0.5f, 0.4f, 0.6f),
                    Amp       = new Vector3(0.4f, 0.5f, 0.4f),
                    Frequency = 0.8f,
                    Phase     = new Vector3(0.1f, 0.45f, 0.8f),
                    TrapScale = 1.2f, ShellMix = 0.3f,
                    TrapMix   = new Vector3(0.4f, 0.5f, 0.35f),
                };

                string frameDir = Path.Combine(Path.GetDirectoryName(outFile)!, "oracle-frames");
                Directory.CreateDirectory(frameDir);

                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1.5f, mode: 1); // orbit trap

                int texLen         = texW * texH * 4;
                var mandelbrotBytes = new byte[texLen];
                var feedbackBytes   = new byte[texLen];
                var blendedBytes    = new byte[texLen];
                bool bootstrapped   = false;

                for (int i = 0; i < totalFrames; i++)
                {
                    float  t     = (float)i / fps;
                    double tN    = (double)i / totalFrames;
                    double radius = Math.Exp(logStart + tN * (logEnd - logStart));

                    // Power: slow sinusoidal morph 1.4→2.6, 2 full cycles
                    float power  = 2.0f + 0.6f * MathF.Sin(2f * MathF.PI * 2f * (float)tN);

                    // Camera: very slow arc — mostly static so orbit trap UV holds still
                    float camAngle = (float)tN * 0.4f * MathF.PI;
                    float camR     = 11f - (float)tN * 3f;  // slowly approaching
                    var camera = new Camera3D(
                        new Vector3(camR * MathF.Sin(camAngle), 1.5f + (float)tN * 2f, camR * MathF.Cos(camAngle)),
                        Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)sceneW / sceneH);

                    // Blend: Mandelbrot weight fades in; feedback weight builds from 0→0.5 after 4 s
                    float feedbackWeight   = Math.Min(Math.Max(t - 4f, 0f) / 4f, 1f) * 0.5f;
                    float mandelbrotWeight = 1f - feedbackWeight;
                    float textureBlend     = Math.Min(t / 2.5f, 1.0f) * 0.88f;

                    // --- Step 1: render 2D Mandelbrot zoom frame ---
                    var view = new Parsec.Rendering.DeepZoom.DeepZoomView
                    {
                        CenterRe = centerRe, CenterIm = centerIm,
                        Radius   = radius,   Formula  = 0,
                    };
                    uint[] texPixels = deepRenderer.Render(view, texW, texH, texPalette, texBg, texSettings);
                    Buffer.BlockCopy(texPixels, 0, mandelbrotBytes, 0, texLen);

                    // --- Step 2: CPU blend Mandelbrot + prev-frame feedback ---
                    if (!bootstrapped)
                    {
                        Buffer.BlockCopy(mandelbrotBytes, 0, blendedBytes, 0, texLen);
                    }
                    else
                    {
                        for (int p = 0; p < texLen; p++)
                            blendedBytes[p] = (byte)(mandelbrotBytes[p] * mandelbrotWeight + feedbackBytes[p] * feedbackWeight);
                    }

                    if (!bootstrapped)
                    {
                        MetalSurfaceTextureManager.SetImage(blendedBytes, texW, texH, texW * 4);
                        bootstrapped = true;
                    }
                    else
                    {
                        MetalSurfaceTextureManager.UpdateImage(blendedBytes, texW, texH, texW * 4);
                    }
                    MetalSurfaceTextureManager.SetControls(enabled: true, blend: textureBlend, scale: 1.5f, mode: 1);

                    // --- Step 3: render 3D BurningShip scene ---
                    var bs = new BurningShipParams { Power = power };
                    uint[] scenePixels = sceneRenderer.RenderBurningShip(
                        bs, camera, sceneW, sceneH,
                        sceneSettings, sceneBg, sceneSurface, sceneLight, scenePalette, post);

                    // Store scene output as feedback for next frame (at texture resolution via resize)
                    // Simple: just use the mandelbrot texture dims — feed the scene pixel data
                    // downsampled by block-copying only as many bytes as fit (scene is smaller than tex)
                    int sceneLen = scenePixels.Length * 4;
                    if (sceneLen >= texLen)
                    {
                        Buffer.BlockCopy(scenePixels, 0, feedbackBytes, 0, texLen);
                    }
                    else
                    {
                        // scene is smaller: tile it
                        Buffer.BlockCopy(scenePixels, 0, feedbackBytes, 0, sceneLen);
                        Buffer.BlockCopy(scenePixels, 0, feedbackBytes, sceneLen, texLen - sceneLen);
                    }

                    // Save frame
                    string framePath = Path.Combine(frameDir, $"frame_{i:D5}.png");
                    var info = new SKImageInfo(sceneW, sceneH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var bmp = new SKBitmap(info);
                    var saveBytes = new byte[scenePixels.Length * 4];
                    Buffer.BlockCopy(scenePixels, 0, saveBytes, 0, saveBytes.Length);
                    Marshal.Copy(saveBytes, 0, bmp.GetPixels(), saveBytes.Length);
                    ImageOutput.SavePng(bmp, framePath);

                    if (i % 24 == 0 || i == totalFrames - 1)
                        Console.WriteLine($"  frame {i + 1}/{totalFrames}  power={power:F3}  radius={radius:e2}  fbWeight={feedbackWeight:F2}  blend={textureBlend:F2}  t={t:F1}s");
                }

                Console.WriteLine($"  muxing → {outFile} ...");
                var ffArgs = $"-y -framerate {fps} -i \"{frameDir}/frame_%05d.png\" -c:v libx264 -preset slow -crf 16 -pix_fmt yuv420p \"{outFile}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed"); return 1; }
                Directory.Delete(frameDir, recursive: true);
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                Console.WriteLine($"  done → {outFile}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-oracle FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-cross-fractal-texture [duration] [out.mp4]
        // Cross-fractal texture: render a Mandelbrot deep-zoom frame each tick, project it onto
        // the Mandelbox surface via triplanar mapping. Two renders per frame (2D tex + 3D scene).
        // Zoom path: Seahorse Valley, radius 1.5→1e-8. Texture res 256×256; scene 320×180.
        if (args[0] == "metal-cross-fractal-texture")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-cross-fractal-texture requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var dcf) ? dcf : 10.0;
                string outFile  = args.Length >= 3 ? args[2] : ResolveOutputPath("cross_fractal_texture.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int fps      = 24;
                const int sceneW   = 320, sceneH = 180;
                const int texW     = 256, texH   = 256;
                int totalFrames    = (int)Math.Ceiling(duration * fps);

                // Seahorse Valley — 8 orders of magnitude over the full clip
                const string centerRe  = "-0.743643887037158704752191506114774";
                const string centerIm  =  "0.131825904205311970493132056385139";
                const double startRadius = 1.5;
                const double endRadius   = 1e-8;
                double logStart = Math.Log(startRadius);
                double logEnd   = Math.Log(endRadius);

                Console.WriteLine($"metal-cross-fractal-texture — Mandelbrot→Mandelbox, {duration:F1}s @ {fps}fps ({totalFrames} frames)");
                Console.WriteLine($"  2D texture: {texW}×{texH}  3D scene: {sceneW}×{sceneH}  zoom radius {startRadius}→{endRadius:e1}");

                using var deepRenderer = new MetalDeepZoomRenderer();
                if (!deepRenderer.IsAvailable) { Console.Error.WriteLine("Metal deep-zoom backend not available."); return 1; }

                using var sceneRenderer = new MetalMandelboxRenderer();
                if (!sceneRenderer.IsAvailable) { Console.Error.WriteLine("Metal scene backend not available."); return 1; }

                // Vibrant rainbow cosine palette for the 2D Mandelbrot texture
                var texPalette = new PaletteParams
                {
                    Base      = new Vector3(0.5f, 0.5f, 0.5f),
                    Amp       = new Vector3(0.5f, 0.5f, 0.5f),
                    Frequency = 1.2f,
                    Phase     = new Vector3(0.0f, 0.33f, 0.67f),
                    TrapScale = 1.0f,
                    ShellMix  = 0f,
                };
                var texBg       = new Color(0.01f, 0.01f, 0.03f);
                var texSettings = new RaymarchSettings(
                    MaxSteps: 0, HitEpsilon: 0, MaxDistance: 0, NormalEpsilon: 0,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                    HeroSamples: 1,
                    EnableReflections: false, ReflectionBounces: 0, Gloss: 0, F0: 0, LightIntensity: 0);

                var sceneFractal  = new MandelboxParams();
                var sceneSettings = new RaymarchSettings(
                    MaxSteps: 80, HitEpsilon: 1.5e-3f, MaxDistance: 25f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: true, ShadowSteps: 24, ShadowSoftness: 8f,
                    EnableAmbientOcclusion: true, AOSamples: 3, AOStepDistance: 0.06f, AOIntensity: 0.8f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.1f);
                var sceneBg      = new Color(0.04f, 0.04f, 0.07f);
                var sceneSurface = new Color(0.6f, 0.55f, 0.5f);
                var sceneLight   = Vector3.Normalize(new Vector3(1.2f, 2f, 1f));
                var scenePalette = PaletteParams.Default;
                var sceneCamera  = new Camera3D(
                    new Vector3(0f, 2f, 10f), Vector3.Zero, Vector3.UnitY,
                    MathF.PI / 4f, (float)sceneW / sceneH);

                // Texture blend ramps to 0.75 over first 2 s then holds
                // Scale 1.0 — one full Mandelbrot tile across each triplanar face
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);

                string frameDir = Path.Combine(Path.GetDirectoryName(outFile)!, "cross-fractal-frames");
                Directory.CreateDirectory(frameDir);

                var texBytes = new byte[texW * texH * 4];

                for (int i = 0; i < totalFrames; i++)
                {
                    float t     = (float)i / fps;
                    double tN   = (double)i / totalFrames;
                    double radius = Math.Exp(logStart + tN * (logEnd - logStart));
                    float blend   = Math.Min(t / 2.0f, 1.0f) * 0.75f;

                    // --- Step 1: render 2D Mandelbrot zoom frame as texture ---
                    var view = new Parsec.Rendering.DeepZoom.DeepZoomView
                    {
                        CenterRe = centerRe, CenterIm = centerIm,
                        Radius   = radius,   Formula  = 0,
                    };
                    uint[] texPixels = deepRenderer.Render(view, texW, texH, texPalette, texBg, texSettings);
                    Buffer.BlockCopy(texPixels, 0, texBytes, 0, texBytes.Length);

                    if (i == 0)
                        MetalSurfaceTextureManager.SetImage(texBytes, texW, texH, texW * 4);
                    else
                        MetalSurfaceTextureManager.UpdateImage(texBytes, texW, texH, texW * 4);

                    MetalSurfaceTextureManager.SetControls(enabled: true, blend: blend, scale: 1.0f, mode: 0);

                    // --- Step 2: render 3D Mandelbox with Mandelbrot texture ---
                    uint[] scenePixels = sceneRenderer.RenderMandelbox(
                        sceneFractal, sceneCamera, sceneW, sceneH,
                        sceneSettings, sceneBg, sceneSurface, sceneLight, scenePalette);

                    // Save scene frame
                    string framePath = Path.Combine(frameDir, $"frame_{i:D5}.png");
                    var info = new SKImageInfo(sceneW, sceneH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var bmp = new SKBitmap(info);
                    var sceneBytes = new byte[scenePixels.Length * 4];
                    Buffer.BlockCopy(scenePixels, 0, sceneBytes, 0, sceneBytes.Length);
                    Marshal.Copy(sceneBytes, 0, bmp.GetPixels(), sceneBytes.Length);
                    ImageOutput.SavePng(bmp, framePath);

                    if (i % 24 == 0 || i == totalFrames - 1)
                        Console.WriteLine($"  frame {i + 1}/{totalFrames}  radius={radius:e2}  blend={blend:F2}  t={t:F1}s");
                }

                Console.WriteLine($"  muxing to {outFile} ...");
                var ffArgs = $"-y -framerate {fps} -i \"{frameDir}/frame_%05d.png\" -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p \"{outFile}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed"); return 1; }
                Directory.Delete(frameDir, recursive: true);
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                Console.WriteLine($"  done → {outFile}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-cross-fractal-texture FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-fractal-feedback [duration] [out.mp4]
        // Feedback loop: each rendered frame is fed back as the surface texture for the next frame.
        // Fixed camera; Mandelbox Scale morphs 1.7→2.3 so the geometry changes under the texture.
        // Blend ramps 0→0.50 over first 3 s. Texture scale holds at 1.0 (no zoom crawl).
        // Frame 0 bootstraps with no texture; frame 1+ uses the previous output as texture input.
        if (args[0] == "metal-fractal-feedback")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-fractal-feedback requires macOS."); return 1; }
            try
            {
                double duration   = args.Length >= 2 && double.TryParse(args[1], out var dfd) ? dfd : 12.0;
                string outFile    = args.Length >= 3 ? args[2] : ResolveOutputPath("fractal_feedback.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

                const int   fps    = 24;
                const int   w      = 320;
                const int   h      = 180;
                const float fov    = MathF.PI / 4f;
                const float aspect = 16f / 9f;
                int totalFrames    = (int)Math.Ceiling(duration * fps);

                Console.WriteLine($"metal-fractal-feedback — Mandelbox Scale morph 1.7→2.3, {duration:F1}s @ {fps}fps ({totalFrames} frames, {w}×{h})");

                using var renderer = new MetalMandelboxRenderer();
                if (!renderer.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }

                var settings = new RaymarchSettings(
                    MaxSteps: 80, HitEpsilon: 1.5e-3f, MaxDistance: 25f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: true, ShadowSteps: 24, ShadowSoftness: 8f,
                    EnableAmbientOcclusion: true, AOSamples: 3, AOStepDistance: 0.06f, AOIntensity: 0.8f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0, Gloss: 0f, F0: 0f, LightIntensity: 1.1f);
                var bg      = new Color(0.04f, 0.04f, 0.07f);
                var surface = new Color(0.6f, 0.55f, 0.5f);
                var light   = Vector3.Normalize(new Vector3(1.2f, 2f, 1f));
                var palette = PaletteParams.Default;

                // Fixed camera — geometry change drives all the motion
                var camera = new Camera3D(
                    new Vector3(0f, 2f, 10f),
                    Vector3.Zero,
                    Vector3.UnitY,
                    fov, aspect);

                string frameDir = Path.Combine(Path.GetDirectoryName(outFile)!, "feedback-frames");
                Directory.CreateDirectory(frameDir);

                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);

                byte[]? feedbackBytes = null;
                int rowBytes = w * 4;

                for (int i = 0; i < totalFrames; i++)
                {
                    float t      = (float)i / fps;
                    float tNorm  = (float)i / totalFrames;

                    // Scale morphs 1.7→2.3 over 1.5 cycles — geometry changes under the texture
                    float scale_fractal = 2.0f + 0.3f * MathF.Sin(2f * MathF.PI * 1.5f * tNorm);

                    // Blend ramps 0→0.50 in first 3 s then holds; texture scale fixed at 1.0
                    float blend = Math.Min(t / 3.0f, 1.0f) * 0.50f;

                    if (feedbackBytes is not null)
                        MetalSurfaceTextureManager.UpdateImage(feedbackBytes, w, h, rowBytes);

                    MetalSurfaceTextureManager.SetControls(enabled: feedbackBytes is not null, blend: blend, scale: 1.0f, mode: 0);

                    var fractal = new MandelboxParams { Scale = scale_fractal };
                    uint[] pixels = renderer.RenderMandelbox(fractal, camera, w, h, settings, bg, surface, light, palette);

                    // Convert RGBA8 uint[] → byte[] for next frame's texture and PNG save
                    feedbackBytes ??= new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, feedbackBytes, 0, feedbackBytes.Length);
                    if (i == 0)
                        MetalSurfaceTextureManager.SetImage(feedbackBytes, w, h, rowBytes);

                    // Write PNG frame
                    string framePath = Path.Combine(frameDir, $"frame_{i:D5}.png");
                    var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    using var bmp = new SKBitmap(info);
                    Marshal.Copy(feedbackBytes, 0, bmp.GetPixels(), feedbackBytes.Length);
                    ImageOutput.SavePng(bmp, framePath);

                    if (i % 24 == 0 || i == totalFrames - 1)
                        Console.WriteLine($"  frame {i + 1}/{totalFrames}  blend={blend:F3}  foldScale={scale_fractal:F3}  t={t:F1}s");
                }

                Console.WriteLine($"  muxing to {outFile} ...");
                var ffArgs = $"-y -framerate {fps} -i \"{frameDir}/frame_%05d.png\" -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p \"{outFile}\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", ffArgs)
                    { RedirectStandardError = true, UseShellExecute = false })!;
                proc.WaitForExit();
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed"); return 1; }
                Directory.Delete(frameDir, recursive: true);
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                Console.WriteLine($"  done → {outFile}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-fractal-feedback FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-m9d-check [duration] [outDir]
        // M9d smoke test: verifies SonificationMode enum and DirectOrbit export routing.
        // Renders Mandelbox frames, synthesises via DirectOrbitSynth (same code as
        // OnRenderToVideoClick in DirectOrbit mode), writes m9d_export_direct.wav.
        // Live streaming and UI mode-toggle must be verified in the app.
        if (args[0] == "metal-m9d-check")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-m9d-check requires macOS."); return 1; }
            try
            {
                double duration = args.Length >= 2 && double.TryParse(args[1], out var d9d) ? d9d : 8.0;
                string outDir   = args.Length >= 3 ? args[2] : Path.GetDirectoryName(ResolveOutputPath("x"))!;
                const double ctlHz = 30.0;
                int nFrames     = (int)Math.Ceiling(duration * ctlHz);
                Directory.CreateDirectory(outDir);

                Console.WriteLine($"metal-m9d-check — export routing smoke, {duration:F1}s @ {ctlHz:F0} Hz ({nFrames} frames)");

                // Verify enum is orthogonal to FractalVoice
                var hybridMode = SonificationMode.Hybrid;
                var directMode = SonificationMode.DirectOrbit;
                Console.WriteLine($"  SonificationMode: Hybrid={hybridMode}, DirectOrbit={directMode}  ✓");

                // Build Mandelbox frames (same fly-in as m9b)
                using var renderer9d = new MetalMandelboxRenderer();
                if (!renderer9d.IsAvailable) { Console.Error.WriteLine("Metal unavailable."); return 1; }
                var settings9d = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                    EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0f,
                    EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0f, AOIntensity: 0f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1f);
                var startPos9d = new Vector3(0f, 5f, 30f);
                var endPos9d   = new Vector3(0f, 1.5f, 7f);
                var frames9d   = new List<FractalSonicFrame>(nFrames);
                Vector3 prevPos9d = startPos9d;
                for (int fi = 0; fi < nFrames; fi++)
                {
                    float t9   = nFrames > 1 ? fi / (float)(nFrames - 1) : 0f;
                    var   pos9 = Vector3.Lerp(startPos9d, endPos9d, t9);
                    var   fwd9 = Vector3.Normalize(Vector3.Zero - pos9);
                    var   cam9 = new Camera3D(pos9, Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 16f / 9f);
                    var   fp9  = new MandelboxParams { Scale = 2.0f };
                    var   st9  = renderer9d.RunTelemetryPass(fp9, cam9, settings9d);
                    float spd9 = (pos9 - prevPos9d).Length() * (float)ctlHz;
                    float zv9  = Vector3.Dot(pos9 - prevPos9d, fwd9) * (float)ctlHz;
                    prevPos9d  = pos9;
                    FractalSonicCell[]? cells9 = null;
                    if (st9?.Cells is { Length: > 0 } mc9)
                    {
                        cells9 = new FractalSonicCell[mc9.Length];
                        for (int ci = 0; ci < mc9.Length; ci++)
                            cells9[ci] = new FractalSonicCell(
                                mc9[ci].WorldPosition, mc9[ci].HitRatio, mc9[ci].MeanDepth,
                                mc9[ci].StepComplexity, mc9[ci].NormalMean, mc9[ci].TrapMean,
                                mc9[ci].Energy, mc9[ci].RayWavetable, mc9[ci].OrbitWavetable,
                                mc9[ci].OrbitTrajectory);
                    }
                    frames9d.Add(new FractalSonicFrame(
                        Time: fi / ctlHz, HitRatio: st9?.HitRatio ?? 0f,
                        MeanDepth: st9?.MeanDepth ?? 0f, DepthVariance: st9?.DepthVariance ?? 0f,
                        StepMean: st9?.StepMean ?? 0f, StepP90: st9?.StepP90 ?? 0f,
                        NormalMean: st9?.NormalMean ?? Vector3.Zero, NormalVariance: st9?.NormalVariance ?? 0f,
                        TrapMean: st9?.TrapMean ?? Vector4.Zero, TrapVariance: st9?.TrapVariance ?? Vector4.Zero,
                        CameraSpeed: spd9, ParameterVelocity: 0f,
                        CameraPosition: pos9, CameraForward: fwd9, CameraUp: Vector3.UnitY,
                        Cells: cells9, ZoomVelocity: zv9,
                        WaveshaperCurve: st9?.WaveshaperCurve,
                        FieldScanWaveformTL: st9?.FieldScanWaveformTL, FieldScanWaveformTR: st9?.FieldScanWaveformTR,
                        FieldScanWaveformBL: st9?.FieldScanWaveformBL, FieldScanWaveformBR: st9?.FieldScanWaveformBR));
                }

                // DirectOrbit export path
                Console.Write("  DirectOrbit export... ");
                var pcm9d = DirectOrbitSynth.Synthesize(frames9d, controlRateHz: ctlHz);
                string wav9d = Path.Combine(outDir, "m9d_export_direct.wav");
                WavEncoder.Write(wav9d, pcm9d, DirectOrbitSynth.DefaultSampleRate, channels: 2);
                float pk9d = pcm9d.Max(s => MathF.Abs(s)) / 32767f;
                Console.WriteLine($"peak {20f * MathF.Log10(pk9d + 1e-9f):F1} dBFS  →  {wav9d}");

                // Hybrid export path (Apollonian fallback)
                Console.Write("  Hybrid export (Apollonian fallback)... ");
                var pcmH9d = HybridSynth.Synthesize(frames9d, controlRateHz: ctlHz, voice: FractalVoice.Mandelbox);
                float pkH9d = pcmH9d.Max(s => MathF.Abs(s)) / 32767f;
                Console.WriteLine($"peak {20f * MathF.Log10(pkH9d + 1e-9f):F1} dBFS  ✓");

                Console.WriteLine($"\nOK — M9d export routing verified.");
                Console.WriteLine("GUI accept criteria (verify in app):");
                Console.WriteLine("  1. 'Hybrid'/'DirectOrbit' button toggles next to Live Sonify.");
                Console.WriteLine("  2. Mode switch while active: no restart, takes effect at next buffer.");
                Console.WriteLine("  3. Render-to-Video with DirectOrbit → WAV uses direct-orbit algorithm.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-m9d-check FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-golden [--generate]
        // Golden-frame regression: renders 5 fixed-seed deterministic frames (64×64, 1 sample)
        // and compares SHA-256 hashes against committed baselines in tests/golden/hashes.txt.
        // Pass --generate on first run (or after intentional shader changes) to write baselines.
        if (args[0] == "metal-golden")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-golden requires macOS."); return 1; }
            try
            {
                bool generate = args.Contains("--generate");
                string goldenDir = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                    "..", "..", "..", "..", "..", "tests", "golden");
                goldenDir = Path.GetFullPath(goldenDir);
                Directory.CreateDirectory(goldenDir);
                string hashFile = Path.Combine(goldenDir, "hashes.txt");

                const int w = 64, h = 64;
                var settings = new RaymarchSettings(
                    MaxSteps: 128, HitEpsilon: 5e-4f, MaxDistance: 30f, NormalEpsilon: 6e-4f,
                    EnableSoftShadows: false, ShadowSteps: 32, ShadowSoftness: 8f,
                    EnableAmbientOcclusion: false, AOSamples: 3, AOStepDistance: 0.05f, AOIntensity: 0.35f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.8f);
                var palette  = PaletteParams.Default;
                var bg       = new Color(0.02f, 0.03f, 0.07f);
                var surf     = new Color(0.6f, 0.6f, 0.6f);
                var light    = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));
                var camera   = new Camera3D(new Vector3(0f, 1f, 5f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, 1f);
                var post     = new PostProcessParams { Brightness = 1f, Contrast = 1f, Gamma = 1f, Saturation = 1f, HdrEnabled = false };

                DomainWarpState.SetPhase(0f);
                MetalSurfaceTextureManager.ClearImage();
                MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);

                static string Sha256Hex(uint[] pixels)
                {
                    byte[] raw = new byte[pixels.Length * 4];
                    Buffer.BlockCopy(pixels, 0, raw, 0, raw.Length);
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    return string.Concat(sha.ComputeHash(raw).Select(b => b.ToString("x2")));
                }

                var rendered = new List<(string name, string hash)>();

                // 1 — Plain Mandelbox
                {
                    using var r = new MetalMandelboxRenderer();
                    var px = r.RenderMandelbox(new MandelboxParams(), camera, w, h, settings, bg, surf, light, palette, post);
                    rendered.Add(("plain_mandelbox", Sha256Hex(px)));
                }
                Console.WriteLine($"  plain_mandelbox         done ({rendered[^1].hash[..12]}…)");

                // 2 — Mandelbulb
                {
                    using var r = new MetalMandelbulbRenderer();
                    var px = r.RenderMandelbulb(new MandelbulbParams(), camera, w, h, settings, bg, surf, light, palette, postProcess: post);
                    rendered.Add(("mandelbulb", Sha256Hex(px)));
                }
                Console.WriteLine($"  mandelbulb              done ({rendered[^1].hash[..12]}…)");

                // 3 — BurningShip with orbit-trap projection
                {
                    // Minimal 4×4 checkerboard test texture (RGBA8)
                    int texW = 4, texH = 4;
                    var texBytes = new byte[texW * texH * 4];
                    for (int ty = 0; ty < texH; ty++)
                    for (int tx = 0; tx < texW; tx++)
                    {
                        byte v = (byte)(((tx + ty) & 1) == 0 ? 220 : 80);
                        int offs = (ty * texW + tx) * 4;
                        texBytes[offs] = v; texBytes[offs+1] = v; texBytes[offs+2] = (byte)(255 - v); texBytes[offs+3] = 255;
                    }
                    MetalSurfaceTextureManager.SetImage(texBytes, texW, texH, texW * 4);
                    MetalSurfaceTextureManager.SetControls(enabled: true, blend: 0.7f, scale: 1f, mode: 2); // orbit trap
                    using var r = new MetalBurningShipRenderer();
                    var bs   = new BurningShipParams();
                    var px   = r.RenderBurningShip(bs, camera, w, h, settings, bg, surf, light, palette, post);
                    rendered.Add(("burningship_orbittrap", Sha256Hex(px)));
                    MetalSurfaceTextureManager.SetControls(enabled: false, blend: 0f, scale: 1f, mode: 0);
                    MetalSurfaceTextureManager.ClearImage();
                }
                Console.WriteLine($"  burningship_orbittrap   done ({rendered[^1].hash[..12]}…)");

                // 4 — Mandelbox with domain warp enabled
                {
                    DomainWarpState.SetControls(enabled: true, strength: 0.15f, scale: 1.5f);
                    DomainWarpState.SetPhase(0f);
                    using var r = new MetalMandelboxRenderer();
                    var px = r.RenderMandelbox(new MandelboxParams(), camera, w, h, settings, bg, surf, light, palette, post);
                    rendered.Add(("mandelbox_domainwarp", Sha256Hex(px)));
                    DomainWarpState.SetControls(enabled: false, strength: 0f, scale: 1f);
                }
                Console.WriteLine($"  mandelbox_domainwarp    done ({rendered[^1].hash[..12]}…)");

                // 5 — Deep zoom (Mandelbrot, Seahorse Valley, fixed radius)
                {
                    var dzSettings = new RaymarchSettings(
                        MaxSteps: 0, HitEpsilon: 0, MaxDistance: 0, NormalEpsilon: 0,
                        EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 0,
                        EnableAmbientOcclusion: false, AOSamples: 0, AOStepDistance: 0, AOIntensity: 0,
                        HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                        Gloss: 0f, F0: 0f, LightIntensity: 0f);
                    var view = new Parsec.Rendering.DeepZoom.DeepZoomView
                    {
                        CenterRe = "-0.74364388703715870",
                        CenterIm = "0.13182590420531197",
                        Radius   = 1e-6,
                        Formula  = 0,
                    };
                    using var r = new MetalDeepZoomRenderer();
                    var px = r.Render(view, w, h, palette, bg, dzSettings);
                    rendered.Add(("deepzoom_mandelbrot", Sha256Hex(px)));
                }
                Console.WriteLine($"  deepzoom_mandelbrot     done ({rendered[^1].hash[..12]}…)");

                Console.WriteLine();

                if (generate)
                {
                    var lines = rendered.Select(e => $"{e.name}  {e.hash}");
                    File.WriteAllLines(hashFile, lines);
                    Console.WriteLine($"Wrote {rendered.Count} baselines → {hashFile}");
                    Console.WriteLine("metal-golden baselines generated.");
                    return 0;
                }

                // Compare against stored baselines
                if (!File.Exists(hashFile))
                {
                    Console.Error.WriteLine($"Baseline file not found: {hashFile}");
                    Console.Error.WriteLine("Run with --generate to create baselines.");
                    return 1;
                }

                var baselines = File.ReadAllLines(hashFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Split(new char[]{' ', '\t'}, StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length >= 2)
                    .ToDictionary(p => p[0], p => p[1]);

                int pass = 0, fail = 0;
                foreach (var (name, hash) in rendered)
                {
                    if (!baselines.TryGetValue(name, out var expected))
                    {
                        Console.WriteLine($"  {name,-28} MISSING BASELINE");
                        fail++;
                    }
                    else if (hash == expected)
                    {
                        Console.WriteLine($"  {name,-28} PASS");
                        pass++;
                    }
                    else
                    {
                        Console.WriteLine($"  {name,-28} FAIL (got {hash[..12]}… expected {expected[..12]}…)");
                        fail++;
                    }
                }
                Console.WriteLine($"\n  {pass} passed, {fail} failed");
                return fail == 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-golden FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-glow-smoke [strength] [falloff] [outDir]
        // A/B render of Mandelbox with step-glow off vs on. Verifies the glow path
        // compiles, raises luminance, and changes pixels. Writes both PNGs.
        if (args[0] == "metal-glow-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-glow-smoke requires macOS."); return 1; }
            try
            {
                float strength = args.Length > 1 && float.TryParse(args[1], out var gs) ? gs : 2.5f;
                float falloff  = args.Length > 2 && float.TryParse(args[2], out var gf) ? gf : 12f;
                string outDir  = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "parsec-glow");
                Directory.CreateDirectory(outDir);

                const int w = 320, h = 240;
                // Pull the camera back so the fractal is a compact object against dark
                // negative space — glow is a silhouette/gap halo, so it only reads against
                // sky, not on a frame-filling surface.
                var camera   = new Camera3D(new Vector3(0f, 2f, 11f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)w / h);
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 4e-4f, MaxDistance: 40f, NormalEpsilon: 5e-4f,
                    EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 0.4f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 1.6f);
                // Dark palette with headroom so the glow halo stands out against shadow.
                var palette = new PaletteParams
                {
                    Base = new Vector3(0.10f, 0.13f, 0.22f), Amp = new Vector3(0.45f, 0.40f, 0.55f),
                    Frequency = 1.4f, Phase = new Vector3(0.0f, 0.30f, 0.62f), TrapScale = 0.9f,
                    TrapMix = new Vector3(0.6f, 0.4f, 0.3f), ShellMix = 0.25f,
                };
                var bg    = new Color(0.02f, 0.03f, 0.07f);
                var surf  = Color.Rgb(200, 200, 210);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                void WritePng(uint[] px, string name)
                {
                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[px.Length * 4];
                    Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(outDir, name));
                }
                static double MeanLuma(uint[] px)
                {
                    double sum = 0;
                    foreach (var p in px)
                    {
                        double r = (p & 0xFF), g = ((p >> 8) & 0xFF), b = ((p >> 16) & 0xFF);
                        sum += 0.299 * r + 0.587 * g + 0.114 * b;
                    }
                    return sum / px.Length;
                }

                using var r = new MetalMandelboxRenderer();
                if (!r.IsAvailable) { Console.Error.WriteLine("Metal backend unavailable."); return 1; }

                GlowState.SetControls(enabled: false, strength: 0f, falloff: falloff);
                var off = r.RenderMandelbox(new MandelboxParams(), camera, w, h, settings, bg, surf, light, palette);
                WritePng(off, "glow_off.png");

                GlowState.SetControls(enabled: true, strength: strength, falloff: falloff);
                var on = r.RenderMandelbox(new MandelboxParams(), camera, w, h, settings, bg, surf, light, palette);
                WritePng(on, "glow_on.png");
                GlowState.SetControls(enabled: false, strength: 0f, falloff: falloff); // reset

                int changed = 0;
                for (int i = 0; i < off.Length; i++) if (off[i] != on[i]) changed++;
                double lumaOff = MeanLuma(off), lumaOn = MeanLuma(on);

                Console.WriteLine($"metal-glow-smoke — strength={strength} falloff={falloff}, {w}×{h}");
                Console.WriteLine($"  mean luma  off={lumaOff:F2}  on={lumaOn:F2}  (+{lumaOn - lumaOff:F2})");
                Console.WriteLine($"  changed pixels: {changed}/{off.Length} ({100.0 * changed / off.Length:F1}%)");
                Console.WriteLine($"  PNGs: {Path.Combine(outDir, "glow_off.png")} , glow_on.png");

                bool pass = lumaOn > lumaOff + 0.5 && changed > off.Length / 20;
                Console.WriteLine(pass ? "metal-glow-smoke PASS" : "metal-glow-smoke FAIL (glow had no/low effect)");
                return pass ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-glow-smoke FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // midi-smoke [sweepSeconds]
        // Publish a virtual CoreMIDI source "Parsec", run an in-process loopback self-test,
        // then sweep the 3 core CCs through MidiOutputController so an external MIDI monitor
        // (or DAW) can confirm reception. macOS only.
        if (args[0] == "midi-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("midi-smoke requires macOS."); return 1; }
            try
            {
                double sweepSeconds = args.Length > 1 && double.TryParse(args[1], out var ss) ? ss : 6.0;

                using var midi = new Parsec.Audio.Midi.MidiOutputSession("Parsec");
                Console.WriteLine($"midi-smoke — virtual source 'Parsec'");
                if (!midi.IsAvailable)
                {
                    Console.Error.WriteLine($"  MIDI unavailable: {midi.UnavailableReason}");
                    return 1;
                }
                Console.WriteLine("  virtual source created OK");

                int loop = midi.RunLoopbackSelfTest(sendCount: 4, waitMs: 1200);
                if (loop >= 4)
                    Console.WriteLine($"  loopback self-test: received {loop}/4 packets  PASS");
                else if (loop >= 0)
                    Console.WriteLine($"  loopback self-test: received {loop}/4 (MIDIServer delivery slow/absent — send path still OK)");
                else
                    Console.WriteLine($"  loopback self-test: setup code {loop} (skipped)");

                // Feed the controller a synthetic frame sweep so a monitor sees moving CCs.
                // The sweep also drives M2 derivatives: a fast size ramp (expand→contract),
                // a periodic ParameterVelocity spike (fold), and a complexity collapse (simplify).
                var controller = new Parsec.Audio.Midi.MidiOutputController(midi);
                Console.WriteLine($"  sweeping CC20–36 + gesture + spatial notes for {sweepSeconds:F1}s — open a MIDI monitor on 'Parsec' to watch");
                static (float r, float g, float b) HsvToRgb(float h, float s, float v)
                {
                    float i = MathF.Floor(h * 6f);
                    float f = h * 6f - i;
                    float p = v * (1f - s), q = v * (1f - f * s), t2 = v * (1f - (1f - f) * s);
                    return ((int)i % 6) switch
                    {
                        0 => (v, t2, p), 1 => (q, v, p), 2 => (p, v, t2),
                        3 => (p, q, v), 4 => (t2, p, v), _ => (v, p, q),
                    };
                }
                int events = 0;
                const int hz = 30;
                int frames = Math.Max(1, (int)(sweepSeconds * hz));
                for (int i = 0; i < frames; i++)
                {
                    double t = sweepSeconds * i / frames;       // real seconds, so dt is correct
                    float phase = (float)(t * Math.PI * 2 / Math.Max(0.5, sweepSeconds));
                    // Triangle size wave (fast slopes → expand/contract); complexity inverse;
                    // a sharp fold spike once per second.
                    float tri = 2f * MathF.Abs((phase / (2f * MathF.PI)) % 1f - 0.5f); // 0..1
                    float fold = ((i % hz) == hz / 2) ? 0.20f : 0.0f;                  // 1 Hz spike

                    // A bright energy blob orbiting the 4×4 grid → moving centroid (CC30/31) and
                    // region-note onsets (notes 36–51) as it crosses cells.
                    float ang = phase;
                    float bx = 1.5f + 1.5f * MathF.Cos(ang);   // 0..3 column
                    float by = 1.5f + 1.5f * MathF.Sin(ang);   // 0..3 row
                    var cells = new Parsec.Audio.Sonification.FractalSonicCell[16];
                    for (int c = 0; c < 16; c++)
                    {
                        float ccx = c % 4, ccy = c / 4;
                        float d2 = (ccx - bx) * (ccx - bx) + (ccy - by) * (ccy - by);
                        float e = MathF.Exp(-d2 * 1.2f);        // gaussian blob
                        cells[c] = new Parsec.Audio.Sonification.FractalSonicCell(
                            WorldPosition: System.Numerics.Vector3.Zero,
                            HitRatio: e, MeanDepth: 2f, StepComplexity: e,
                            NormalMean: System.Numerics.Vector3.Zero,
                            TrapMean: System.Numerics.Vector4.Zero, Energy: e);
                    }

                    // Finer 8×6 region grid (improvement 2b): the same blob, projected onto 8×6 → CH2 notes 36–83.
                    var fineGrid = new float[48];
                    float bxF = 3.5f + 3.5f * MathF.Cos(ang);
                    float byF = 2.5f + 2.5f * MathF.Sin(ang);
                    for (int cy2 = 0; cy2 < 6; cy2++)
                        for (int cx2 = 0; cx2 < 8; cx2++)
                        {
                            float d2f = (cx2 - bxF) * (cx2 - bxF) + (cy2 - byF) * (cy2 - byF);
                            fineGrid[cy2 * 8 + cx2] = MathF.Exp(-d2f * 0.7f);
                        }

                    var frame = new Parsec.Audio.Sonification.FractalSonicFrame(
                        Time: t,
                        HitRatio:  0.1f + 0.85f * tri,
                        MeanDepth: 2.0f + 2.0f * MathF.Sin(phase * 0.5f + 1f),
                        DepthVariance: 0.4f + 0.4f * MathF.Sin(phase * 0.7f),
                        StepMean: 20f + 50f * tri, StepP90: 0f,
                        NormalMean: new System.Numerics.Vector3(0f, MathF.Sin(phase), 0f),
                        NormalVariance: 0.02f + 0.12f * (1f - tri),
                        TrapMean: new System.Numerics.Vector4(0.5f + 0.5f * tri, 0f, 0f, 0f),
                        TrapVariance: new System.Numerics.Vector4(0.3f * (1f - tri), 0f, 0f, 0f),
                        CameraSpeed: 1.5f * tri, ParameterVelocity: fold,
                        Cells: cells, ZoomVelocity: 1.2f * MathF.Sin(phase),
                        // Improvement 2a: full-res centroid that tracks the orbiting blob → CC30/31/32.
                        CentroidX: MathF.Cos(ang), CentroidY: -MathF.Sin(ang), Dispersion: 0.2f,
                        // Improvement 2b: finer region grid → CH2 region notes.
                        MidiRegionEnergy: fineGrid);
                    // Real on-screen colour path: synthesize a small RGBA8 frame whose colour
                    // cycles (hue wheel + saturation/brightness wobble), then run it through the
                    // same reducer the app uses → CC24 (hue) + CC35 (sat) + CC36 (val).
                    float hsweep = (float)((t / Math.Max(0.5, sweepSeconds)) % 1.0);
                    float ssweep = 0.5f + 0.5f * MathF.Sin(phase);
                    float vsweep = 0.55f + 0.4f * MathF.Sin(phase * 0.5f);
                    var (cr, cg, cb) = HsvToRgb(hsweep, ssweep, vsweep);
                    uint packed = (uint)(cr * 255f) | ((uint)(cg * 255f) << 8) | ((uint)(cb * 255f) << 16) | 0xFF000000u;
                    var colorBuf = new uint[16 * 16];
                    for (int p = 0; p < colorBuf.Length; p++)
                        colorBuf[p] = (p % 4 == 0) ? 0xFF000000u : packed;  // 1/4 background → exercises the mask
                    var (mh, ms, mv) = Parsec.Audio.Midi.MidiOutputController.MeanScreenColorHsv(colorBuf, 16, 16, 1);
                    controller.Update(frame, mh, ms, mv);
                    if (controller.LastEventTime == t)   // a gesture event fired this exact frame
                    {
                        events++;
                        Console.WriteLine($"    t={t:F2}  EVENT note: {controller.LastEvent}");
                    }
                    if (i % hz == 0) Console.WriteLine($"    t={t:F2}  {controller.Monitor}");
                    System.Threading.Thread.Sleep(1000 / hz);
                }
                Console.WriteLine($"  event notes fired: {events}");
                Console.WriteLine(events > 0 ? "midi-smoke PASS" : "midi-smoke PASS (no events fired — check thresholds)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"midi-smoke FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // midi-map-check [seconds] — improvement M3b: remap the mapping table, then sweep so a
        // monitor sees the EDITED routing (Size→CC50 on ch3, Complexity disabled, Proximity range
        // 100–127). In-process it asserts the dedup/enable plumbing honours the config.
        if (args[0] == "midi-map-check")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("midi-map-check requires macOS."); return 1; }
            double secs = args.Length > 1 && double.TryParse(args[1], out var s2) ? s2 : 3.0;
            try
            {
                using var midi = new Parsec.Audio.Midi.MidiOutputSession("Parsec");
                if (!midi.IsAvailable) { Console.Error.WriteLine($"MIDI unavailable: {midi.UnavailableReason}"); return 1; }
                var cfg = new Parsec.Audio.Midi.MidiMappingConfig();
                cfg[Parsec.Audio.Midi.MidiSignal.Size].Cc = 50;
                cfg[Parsec.Audio.Midi.MidiSignal.Size].Channel = 2;       // MIDI channel 3
                cfg[Parsec.Audio.Midi.MidiSignal.Complexity].Enabled = false;
                cfg[Parsec.Audio.Midi.MidiSignal.Proximity].OutMin = 100; // narrowed range
                cfg[Parsec.Audio.Midi.MidiSignal.Proximity].OutMax = 127;
                var controller = new Parsec.Audio.Midi.MidiOutputController(midi, cfg);
                Console.WriteLine($"midi-map-check — Size→CC50(ch3), Complexity off, Proximity range 100–127; sweeping {secs:F1}s");

                int frames = Math.Max(1, (int)(secs * 30));
                for (int i = 0; i < frames; i++)
                {
                    double t = secs * i / frames;
                    float tri = 2f * MathF.Abs(((float)(t)) % 1f - 0.5f);
                    var frame = new Parsec.Audio.Sonification.FractalSonicFrame(
                        Time: t, HitRatio: 0.1f + 0.8f * tri, MeanDepth: 1.0f + 2.0f * (1f - tri),
                        DepthVariance: 0.3f, StepMean: 30f, StepP90: 0f,
                        NormalMean: System.Numerics.Vector3.Zero, NormalVariance: 0.05f + 0.1f * tri,
                        TrapMean: System.Numerics.Vector4.Zero, TrapVariance: System.Numerics.Vector4.Zero,
                        CameraSpeed: 0f, ParameterVelocity: 0f);
                    controller.Update(frame);
                    System.Threading.Thread.Sleep(1000 / 30);
                }

                bool sizeOnNew = cfg[Parsec.Audio.Midi.MidiSignal.Size].LastSent >= 0 && cfg[Parsec.Audio.Midi.MidiSignal.Size].Cc == 50;
                bool complexOff = cfg[Parsec.Audio.Midi.MidiSignal.Complexity].LastSent < 0;
                bool proxClamped = cfg[Parsec.Audio.Midi.MidiSignal.Proximity].LastSent >= 100;
                Console.WriteLine($"  Size sent on CC50: {sizeOnNew}");
                Console.WriteLine($"  Complexity suppressed (disabled): {complexOff}");
                Console.WriteLine($"  Proximity within 100–127: {proxClamped} (last={cfg[Parsec.Audio.Midi.MidiSignal.Proximity].LastSent})");
                bool ok = sizeOnNew && complexOff && proxClamped;
                Console.WriteLine(ok ? "midi-map-check PASS" : "midi-map-check FAIL");
                return ok ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"midi-map-check FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // midi-monitor [seconds]
        // Independent CoreMIDI receiver (separate client) that connects to every published
        // source and decodes the channel-voice messages it receives. Run this alongside
        // `midi-smoke` (or the app with MIDI OUT on) in another terminal to prove that MIDI
        // is delivered cross-process through the MIDIServer — the same path Ableton uses.
        if (args[0] == "midi-monitor")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("midi-monitor requires macOS."); return 1; }
            int secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 12;
            Console.WriteLine($"midi-monitor — listening on all MIDI sources for {secs}s …");
            Console.WriteLine("  (start 'parsec midi-smoke' in another terminal, or enable MIDI OUT in the app)");

            var r = Parsec.Audio.Midi.MidiMonitorProbe.Run(secs);
            if (r.Error != null) { Console.Error.WriteLine($"  monitor setup failed: {r.Error}"); return 1; }

            Console.WriteLine($"  sources connected : {r.SourcesConnected}");
            Console.WriteLine($"  messages received : {r.Messages}  (CC {r.CcMessages} · noteOn {r.NoteOnMessages} · noteOff {r.NoteOffMessages} · undecoded {r.Undecoded})");

            var ccNames = new Dictionary<int, string> {
                {20,"Size"},{21,"Proximity"},{22,"Complexity"},{23,"Expansion"},{24,"Colour"},
                {25,"Layering"},{26,"Haze"},{27,"Verticality"},{28,"Speed"},{29,"Dolly"},
                {30,"PositionX"},{31,"PositionY"},{32,"Dispersion"},{33,"Structure"},{34,"Heterogeneity"},
                {35,"Saturation"},{36,"Brightness"} };
            for (int cc = 20; cc <= 36; cc++)
                if (r.LastCc[cc] >= 0)
                    Console.WriteLine($"    CC{cc} {ccNames[cc],-13} last = {r.LastCc[cc]}");
            // Any remapped CC outside the default 20–36 block (improvement M3b).
            for (int cc = 0; cc < 128; cc++)
                if ((cc < 20 || cc > 36) && r.LastCc[cc] >= 0)
                    Console.WriteLine($"    CC{cc} (remapped)    last = {r.LastCc[cc]}");

            var noteNames = new Dictionary<int, string> {
                {60,"Expand"},{62,"Contract"},{64,"Fold"},{65,"Simplify"},
                {67,"Enclose"},{69,"Emerge"},{71,"Shimmer"} };
            foreach (var note in new[] { 60, 62, 64, 65, 67, 69, 71 })
                if (r.NoteOnCounts[note] > 0)
                    Console.WriteLine($"    note {note} {noteNames[note],-9} fired {r.NoteOnCounts[note]}x");
            int spatialNotes = 0, spatialFires = 0;
            for (int n = 36; n <= 51; n++) if (r.NoteOnCounts[n] > 0) { spatialNotes++; spatialFires += r.NoteOnCounts[n]; }
            if (spatialNotes > 0)
                Console.WriteLine($"    spatial notes 36–51 (4×4, ch1): {spatialNotes} distinct cells fired ({spatialFires} onsets)");
            // Improvement 2b: notes 52–83 are produced ONLY by the finer 8×6 grid (ch2) — the 4×4
            // never reaches them, so they unambiguously confirm fine-grid delivery (probe is
            // channel-agnostic, so 36–51 may mix both grids).
            int fineNotes = 0, fineFires = 0;
            for (int n = 52; n <= 83; n++) if (r.NoteOnCounts[n] > 0) { fineNotes++; fineFires += r.NoteOnCounts[n]; }
            if (fineNotes > 0)
                Console.WriteLine($"    fine region notes 52–83 (8×6, ch2): {fineNotes} distinct cells fired ({fineFires} onsets)");

            bool ok = r.Messages > 0 && r.CcMessages > 0;
            Console.WriteLine(ok ? "midi-monitor PASS — external delivery confirmed"
                                 : "midi-monitor: no messages received (was a sender running?)");
            return ok ? 0 : 1;
        }

        // metal-bloom-smoke [intensity] [threshold] [outDir]
        // A/B render of a frame-filling Mandelbox with screen-space bloom off vs on.
        // Bloom is the case march-glow fails: a busy close-up where bright detail should
        // bleed light into its surroundings. Writes both PNGs.
        if (args[0] == "metal-bloom-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-bloom-smoke requires macOS."); return 1; }
            try
            {
                float intensity = args.Length > 1 && float.TryParse(args[1], out var bi) ? bi : 1.0f;
                float threshold = args.Length > 2 && float.TryParse(args[2], out var bt) ? bt : 0.6f;
                string outDir   = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "parsec-bloom");
                Directory.CreateDirectory(outDir);

                const int w = 320, h = 240;
                // Frame-filling close-up — the common view, and where march-glow degraded
                // to a flat lift. Bloom should still read here.
                var camera   = new Camera3D(new Vector3(0f, 1f, 5f), Vector3.Zero, Vector3.UnitY, MathF.PI / 4f, (float)w / h);
                var settings = new RaymarchSettings(
                    MaxSteps: 160, HitEpsilon: 4e-4f, MaxDistance: 30f, NormalEpsilon: 5e-4f,
                    EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 10f,
                    EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 0.4f,
                    HeroSamples: 1, EnableReflections: false, ReflectionBounces: 0,
                    Gloss: 0f, F0: 0f, LightIntensity: 2.2f);
                // High-contrast dark palette: deep crevices, bright ridges — so bloom has
                // isolated highlights to bleed and shadow to bleed into. A flat-bright
                // rainbow palette has no contrast and neither glow nor bloom can read on it.
                var palette = new PaletteParams
                {
                    Base = new Vector3(0.04f, 0.05f, 0.10f), Amp = new Vector3(0.70f, 0.60f, 0.55f),
                    Frequency = 1.1f, Phase = new Vector3(0.0f, 0.18f, 0.42f), TrapScale = 0.8f,
                    TrapMix = new Vector3(0.6f, 0.4f, 0.3f), ShellMix = 0.30f,
                };
                var bg    = new Color(0.01f, 0.01f, 0.03f);
                var surf  = Color.Rgb(200, 200, 210);
                var light = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));

                void WritePng(uint[] px, string name)
                {
                    var info  = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp   = new SKBitmap(info);
                    var bytes = new byte[px.Length * 4];
                    Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
                    ImageOutput.SavePng(bmp, Path.Combine(outDir, name));
                }
                static double MeanLuma(uint[] px)
                {
                    double sum = 0;
                    foreach (var p in px)
                    {
                        double r = (p & 0xFF), g = ((p >> 8) & 0xFF), b = ((p >> 16) & 0xFF);
                        sum += 0.299 * r + 0.587 * g + 0.114 * b;
                    }
                    return sum / px.Length;
                }

                using var r = new MetalMandelboxRenderer();
                if (!r.IsAvailable) { Console.Error.WriteLine("Metal backend unavailable."); return 1; }

                var postOff = new PostProcessParams { Brightness = 1f, Contrast = 1f, Gamma = 1f, Saturation = 1f, BloomEnabled = false };
                var postOn  = new PostProcessParams { Brightness = 1f, Contrast = 1f, Gamma = 1f, Saturation = 1f,
                                                      BloomEnabled = true, BloomThreshold = threshold, BloomIntensity = intensity, BloomRadius = 1.0f };

                var off = r.RenderMandelbox(new MandelboxParams(), camera, w, h, settings, bg, surf, light, palette, postOff);
                WritePng(off, "bloom_off.png");
                var on = r.RenderMandelbox(new MandelboxParams(), camera, w, h, settings, bg, surf, light, palette, postOn);
                WritePng(on, "bloom_on.png");

                int changed = 0;
                for (int i = 0; i < off.Length; i++) if (off[i] != on[i]) changed++;
                double lumaOff = MeanLuma(off), lumaOn = MeanLuma(on);

                Console.WriteLine($"metal-bloom-smoke — intensity={intensity} threshold={threshold}, {w}×{h}");
                Console.WriteLine($"  mean luma  off={lumaOff:F2}  on={lumaOn:F2}  (+{lumaOn - lumaOff:F2})");
                Console.WriteLine($"  changed pixels: {changed}/{off.Length} ({100.0 * changed / off.Length:F1}%)");
                Console.WriteLine($"  PNGs: {Path.Combine(outDir, "bloom_off.png")} , bloom_on.png");

                bool pass = lumaOn > lumaOff + 0.5 && changed > off.Length / 20;
                Console.WriteLine(pass ? "metal-bloom-smoke PASS" : "metal-bloom-smoke FAIL (bloom had no/low effect)");
                return pass ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-bloom-smoke FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
        }

        // metal-attractor-smoke [w] [h]
        // Verify the MetalAttractorRenderer compiles and renders non-background pixels.
        if (args[0] == "metal-attractor-smoke")
        {
            if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("metal-attractor-smoke requires macOS."); return 1; }
            try
            {
                int w = args.Length > 1 && int.TryParse(args[1], out var aw) ? aw : 64;
                int h = args.Length > 2 && int.TryParse(args[2], out var ah) ? ah : 48;

                using var renderer = new MetalAttractorRenderer();
                if (!renderer.IsAvailable)
                {
                    Console.Error.WriteLine("metal-attractor-smoke: Metal backend unavailable.");
                    return 1;
                }

                Console.WriteLine($"metal-attractor-smoke — {w}×{h}, generating trajectory...");

                // Generate a small canonical Thomas attractor trajectory + hash.
                var ap = new Parsec.Core.Attractors.AttractorParams { NumSteps = 50_000 };
                var traj = Parsec.Core.Attractors.ThomasAttractor.Generate(ap);
                var hash = Parsec.Core.Attractors.AttractorHash.Build(traj, gridSize: 64);
                Console.WriteLine($"  trajectory: {traj.Count} pts, bounds [{hash.BoundsMin.X:F2},{hash.BoundsMax.X:F2}]×[{hash.BoundsMin.Y:F2},{hash.BoundsMax.Y:F2}]×[{hash.BoundsMin.Z:F2},{hash.BoundsMax.Z:F2}]");

                renderer.SetAttractor(hash);

                // Camera positioned to see the full attractor cloud.
                var center = (hash.BoundsMin + hash.BoundsMax) * 0.5f;
                float span  = (hash.BoundsMax - hash.BoundsMin).Length();
                var camPos  = center + new Vector3(0f, 0.3f, 1f) * span * 0.9f;
                var camera  = new Camera3D(camPos, center, Vector3.UnitY, MathF.PI / 4f, (float)w / h);

                var rp     = new Parsec.Rendering.Gpu.AttractorRenderParams { TubeRadius = 0.06f, Fudge = 0.45f };
                var bg     = new Color(0.02f, 0.03f, 0.07f);
                var surf   = new Color(0.9f, 0.47f, 0.27f);
                var light  = Vector3.Normalize(new Vector3(1f, 2f, 1.5f));
                var pal    = PaletteParams.Default;
                var settings = new RaymarchSettings();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pixels = renderer.Render(rp, camera, w, h, settings, bg, surf, light, pal, hash);
                sw.Stop();

                uint bgR = (uint)(bg.R * 255 + 0.5f), bgG = (uint)(bg.G * 255 + 0.5f), bgB = (uint)(bg.B * 255 + 0.5f);
                uint bgPacked = (255u << 24) | (bgB << 16) | (bgG << 8) | bgR;
                int nonBg = pixels.Count(p => p != bgPacked);
                Console.WriteLine($"  compute {renderer.LastComputeMs} ms, readback {renderer.LastReadbackMs} ms, total {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"  {w * h} pixels, {nonBg} non-background");

                bool pass = nonBg > 0;
                Console.WriteLine(pass ? "metal-attractor-smoke PASS" : "metal-attractor-smoke FAIL (all background)");
                return pass ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-attractor-smoke FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
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

    private static uint[] PixelsToRgba(uint[] pixels) => pixels;

    private static void SaveUintPixels(uint[] pixels, int w, int h, string path)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
        ImageOutput.SavePng(bmp, path);
    }

    private static (int changedPixels, float meanAbsDelta) ComparePixelBuffers(uint[] a, uint[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Pixel buffers must have the same length.");

        long totalAbsDelta = 0;
        int changedPixels = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
                continue;

            changedPixels++;
            totalAbsDelta += Math.Abs((int)(a[i] & 0xFF) - (int)(b[i] & 0xFF));
            totalAbsDelta += Math.Abs((int)((a[i] >> 8) & 0xFF) - (int)((b[i] >> 8) & 0xFF));
            totalAbsDelta += Math.Abs((int)((a[i] >> 16) & 0xFF) - (int)((b[i] >> 16) & 0xFF));
        }

        float meanAbsDelta = a.Length == 0 ? 0f : totalAbsDelta / (a.Length * 3f);
        return (changedPixels, meanAbsDelta);
    }

    private static bool TryLoadSurfaceTextureImage(
        string path,
        out byte[]? bytes,
        out int width,
        out int height,
        out int rowBytes,
        out string? error)
    {
        bytes = null;
        width = 0;
        height = 0;
        rowBytes = 0;
        error = null;

        using var codec = SKCodec.Create(path);
        if (codec is null)
        {
            error = "Failed to decode image file.";
            return false;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            error = $"Image decode failed ({result}).";
            return false;
        }

        int sourceByteCount = checked(bitmap.RowBytes * bitmap.Height);
        var sourceBytes = new byte[sourceByteCount];
        Marshal.Copy(bitmap.GetPixels(), sourceBytes, 0, sourceByteCount);

        rowBytes = checked(bitmap.Width * 4);
        bytes = new byte[checked(rowBytes * bitmap.Height)];
        if (bitmap.RowBytes == rowBytes)
        {
            Buffer.BlockCopy(sourceBytes, 0, bytes, 0, bytes.Length);
        }
        else
        {
            for (int y = 0; y < bitmap.Height; y++)
                Buffer.BlockCopy(sourceBytes, y * bitmap.RowBytes, bytes, y * rowBytes, rowBytes);
        }

        width = bitmap.Width;
        height = bitmap.Height;
        return true;
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
        Console.WriteLine("  parsec gpu-surface-texture-smoke <image> [w] [h] [outDir]  OpenGL texture projection A/B render");
        Console.WriteLine("  parsec metal-smoke [w] [h]            Metal Mandelbox spike test (macOS only)");
        Console.WriteLine("  parsec metal-surface-texture-smoke <image> [w] [h] [outDir]  Metal texture projection A/B render");
        Console.WriteLine("  parsec metal-burning-texture-mp4 [image] [duration] [out.mp4]  BurningShip surface texture fly-in (macOS)");
        Console.WriteLine("  parsec metal-burning-video-texture [video] [duration] [out.mp4]  BurningShip orbit-trap video texture fly-in (macOS)");
        Console.WriteLine("  parsec metal-closeup-hq [duration] [out.mp4]  Mandelbox close fly-in, 4× SSAA, Mandelbrot texture (macOS)");
        Console.WriteLine("  parsec metal-cross-fractal-texture [duration] [out.mp4]  Mandelbrot zoom projected onto Mandelbox surface (macOS)");;
        Console.WriteLine("  parsec metal-fractal-feedback [duration] [out.mp4]  Mandelbox recursive self-texture feedback loop (macOS)");;
        Console.WriteLine("  parsec metal-domain-warp-mp4 [duration] [out.mp4] [w] [h]  Mandelbox procedural domain-warp clip (macOS)");
        Console.WriteLine("  parsec metal-bulb-smoke [w] [h]      Metal Mandelbulb smoke test (macOS only)");
        Console.WriteLine("  parsec metal-rotbox-smoke [w] [h]    Metal RotBox smoke test (macOS only)");
        Console.WriteLine("  parsec metal-kifs-smoke [w] [h]      Metal KIFS smoke test (macOS only)");
        Console.WriteLine("  parsec metal-kleinian-smoke [w] [h]  Metal Kleinian smoke test (macOS only)");
        Console.WriteLine("  parsec metal-hybrid-smoke [w] [h]    Metal Hybrid smoke test (macOS only)");
        Console.WriteLine("  parsec metal-orbit-gif [frames] [w] [h] [out.gif]  Orbiting Mandelbulb GIF (macOS only)");
        Console.WriteLine("  parsec metal-attractor-smoke [w] [h]  Metal Attractor spatial-hash tube smoke test (macOS only)");
        Console.WriteLine("  parsec metal-golden [--generate]     Golden-frame regression (5 scenarios, SHA-256 hash compare)");
        Console.WriteLine("  parsec metal-glow-smoke [strength] [falloff] [outDir]  Mandelbox step-glow A/B render (macOS only)");
        Console.WriteLine("  parsec metal-bloom-smoke [intensity] [threshold] [outDir]  Mandelbox screen-space bloom A/B render (macOS only)");
        Console.WriteLine("  parsec midi-smoke [sweepSeconds]     Publish virtual MIDI source 'Parsec' + loopback test + CC sweep (macOS only)");
        Console.WriteLine("  parsec midi-monitor [seconds]        Independent receiver: decode MIDI from any source, cross-process (macOS only)");
        Console.WriteLine("  parsec m7a-check              JI/temperament quantizer self-check");
        Console.WriteLine("  parsec help           Show this help");
        Console.WriteLine();
        Console.WriteLine("Available examples:");
        foreach (var ex in examples)
            Console.WriteLine($"  {ex.Name,-24} {ex.Description}");
        Console.WriteLine();
        Console.WriteLine("Output goes to <exe-dir>/outputs/<example>.png");
    }
}
