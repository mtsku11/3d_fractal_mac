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
