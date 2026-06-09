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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
                var fi = new FileInfo(outMp4);
                Console.WriteLine($"  -> {outMp4}  ({fi.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-morph-mp4 FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);

                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);
                if (proc.ExitCode != 0) { Console.Error.WriteLine("ffmpeg failed."); return 1; }
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
                proc.WaitForExit();
                Directory.Delete(frameDir, recursive: true);

                if (proc.ExitCode != 0) { Console.Error.WriteLine(" ffmpeg failed."); return 1; }
                var fi2   = new FileInfo(outMp4);
                Console.WriteLine($" done.\nOK → {outMp4}  ({fi2.Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"metal-sonify-clip FAILED: {ex.Message}\n{ex.StackTrace}"); return 1; }
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
        Console.WriteLine("  parsec metal-smoke [w] [h]            Metal Mandelbox spike test (macOS only)");
        Console.WriteLine("  parsec metal-bulb-smoke [w] [h]      Metal Mandelbulb smoke test (macOS only)");
        Console.WriteLine("  parsec metal-rotbox-smoke [w] [h]    Metal RotBox smoke test (macOS only)");
        Console.WriteLine("  parsec metal-kifs-smoke [w] [h]      Metal KIFS smoke test (macOS only)");
        Console.WriteLine("  parsec metal-kleinian-smoke [w] [h]  Metal Kleinian smoke test (macOS only)");
        Console.WriteLine("  parsec metal-hybrid-smoke [w] [h]    Metal Hybrid smoke test (macOS only)");
        Console.WriteLine("  parsec metal-orbit-gif [frames] [w] [h] [out.gif]  Orbiting Mandelbulb GIF (macOS only)");
        Console.WriteLine("  parsec help           Show this help");
        Console.WriteLine();
        Console.WriteLine("Available examples:");
        foreach (var ex in examples)
            Console.WriteLine($"  {ex.Name,-24} {ex.Description}");
        Console.WriteLine();
        Console.WriteLine("Output goes to <exe-dir>/outputs/<example>.png");
    }
}
