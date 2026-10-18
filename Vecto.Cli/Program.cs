using System.Globalization;
using System.Runtime.Versioning;
using Vecto.Core;

// the CLI ships for exactly these desktop platforms (win-x64, linux-x64, osx publishes);
// this also satisfies CA1416 for PDFtoImage, whose API carries the same platform set
[assembly: SupportedOSPlatform("Windows")]
[assembly: SupportedOSPlatform("Linux")]
[assembly: SupportedOSPlatform("macOS")]

namespace Vecto.Cli;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }
        if (args[0] == "--version")
        {
            var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                typeof(Program).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var ver = info?.InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";
            int plus = ver.IndexOf('+');   // strip build metadata (+commit)
            Console.WriteLine("vecto " + (plus >= 0 ? ver[..plus] : ver));
            return 0;
        }
        return args[0] switch
        {
            "trace" => Trace(args[1..]),
            "samples" => Samples(args[1..]),
            "render" => Render(args[1..]),
            "bench" => Bench(args[1..]),
            _ when File.Exists(args[0]) || args[0].Contains('*') || args[0].Contains('?') => Trace(args),
            _ => UnknownCommand(args[0]),
        };
    }

    static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"unknown command or missing file: {cmd}");
        return 1;
    }

    static int Trace(string[] args)
    {
        var inputs = new List<string>();
        string? output = null, outDir = null, segPng = null;
        var opt = new TraceOptions();
        int pdfDpi = PdfInput.DefaultDpi;
        string pdfPages = "1";
        bool check = false, stats = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-o" or "--out":
                    output = Next(args, ref i);
                    break;
                case "--out-dir":
                    outDir = Next(args, ref i);
                    break;
                case "--colors":
                    var v = Next(args, ref i);
                    if (v.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        opt.PaletteMode = PaletteMode.Auto;
                    }
                    else
                    {
                        opt.PaletteMode = PaletteMode.FixedCount;
                        opt.ColorCount = int.Parse(v, CultureInfo.InvariantCulture);
                    }
                    break;
                case "--max-colors":
                    opt.MaxColors = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--detail":
                    opt.Detail = Enum.Parse<DetailLevel>(Next(args, ref i), true);
                    break;
                case "--style":
                    opt.Style = Enum.Parse<ImageStyle>(Next(args, ref i), true);
                    break;
                case "--polygons":
                    opt.CurveFitting = false;
                    break;
                case "--epsilon":
                    opt.PolygonEpsilon = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    opt.Seed = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--pdf-dpi":
                    pdfDpi = PdfInput.ParseDpi(Next(args, ref i));
                    break;
                case "--pages":
                    pdfPages = Next(args, ref i);
                    break;
                case "--seg-png":
                    segPng = Next(args, ref i);
                    break;
                case "--check":
                    check = true;
                    break;
                case "--stats":
                    stats = true;
                    break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException("unknown option " + a);
                    inputs.AddRange(Expand(a));
                    break;
            }
        }
        if (inputs.Count == 0) throw new ArgumentException("no input image given (see: vecto --help)");
        if (inputs.Count > 1 && output != null) throw new ArgumentException("-o is for a single input; use --out-dir for batches");
        if (inputs.Count > 1 && segPng != null) throw new ArgumentException("--seg-png is for a single input");
        if (outDir != null) Directory.CreateDirectory(outDir);

        bool allPlanar = true;
        foreach (var input in inputs)
        {
            var basePath = outDir == null ? input : Path.Combine(outDir, Path.GetFileName(input));
            var pages = PdfInput.IsPdf(input)
                ? PdfInput.Load(input, pdfDpi, pdfPages)
                : [new PdfInput.PdfPage(1, ImageIo.Load(input))];
            if (pages.Count > 1 && output != null)
                throw new ArgumentException("-o is for a single page; use --out-dir for multi-page PDFs");
            if (pages.Count > 1 && segPng != null)
                throw new ArgumentException("--seg-png is for a single page");
            foreach (var page in pages)
            {
                string baseNoExt = Path.Combine(
                    Path.GetDirectoryName(basePath) ?? "",
                    Path.GetFileNameWithoutExtension(basePath));
                var outPath = output ?? (pages.Count > 1
                    ? baseNoExt + FormattableString.Invariant($"-{page.Number}.svg")
                    : baseNoExt + ".svg");
                var img = page.Image;
                var res = Tracer.Trace(img, opt);
                if (Path.GetExtension(outPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    PdfWriter.Write(res.Document, outPath);
                else
                    File.WriteAllText(outPath, SvgWriter.Write(res.Document));
                Console.WriteLine(FormattableString.Invariant(
                    $"{Path.GetFileName(input)}{(pages.Count > 1 ? $"[{page.Number}]" : "")} -> {outPath}  ({res.Diagnostics.TotalMs} ms, {res.Document.Palette.Count} colors, {res.Diagnostics.RegionCount} regions, {res.Diagnostics.NodeCount} nodes)"));
                if (segPng != null)
                {
                    ImageIo.SavePng(SegmentationImage(res), segPng);
                    Console.WriteLine("segmentation -> " + segPng);
                }
                if (stats) PrintStats(res);
                if (check && !PlanarityCheck(res, img)) allPlanar = false;
            }
        }
        return allPlanar ? 0 : 2;
    }

    /// <summary>Wildcards are expanded here, not by the shell — cmd/PowerShell pass them through verbatim.</summary>
    static IEnumerable<string> Expand(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return [pattern];
        var dir = Path.GetDirectoryName(pattern);
        var matches = Directory.GetFiles(string.IsNullOrEmpty(dir) ? "." : dir, Path.GetFileName(pattern))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (matches.Length == 0) throw new ArgumentException("no files match " + pattern);
        return matches;
    }

    static int Render(string[] args)
    {
        string? input = null, output = null;
        double scale = 1;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out":
                    output = Next(args, ref i);
                    break;
                case "--scale":
                    scale = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException("unknown option " + args[i]);
                    input = args[i];
                    break;
            }
        }
        if (input == null) throw new ArgumentException("no input SVG given");
        output ??= Path.ChangeExtension(input, ".png");
        var doc = SvgReader.ReadFile(input);
        var img = Rasterizer.Render(doc, scale);
        ImageIo.SavePng(img, output);
        Console.WriteLine(FormattableString.Invariant($"{Path.GetFileName(input)} -> {output}  ({img.Width}x{img.Height})"));
        return 0;
    }

    /// <summary>
    /// Ground-truth round trip: original vector → raster (the input a user would have) →
    /// trace → re-render → perceptual diff against that raster. The diff heatmap and the
    /// ΔE/node numbers say exactly where the engine falls short of the original vector.
    /// </summary>
    static int Bench(string[] args)
    {
        var files = new List<string>();
        string? outDir = null;
        double scale = 1;
        var opt = new TraceOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scale":
                    scale = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--out-dir":
                    outDir = Next(args, ref i);
                    break;
                case "--colors":
                    var v = Next(args, ref i);
                    if (!v.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        opt.PaletteMode = PaletteMode.FixedCount;
                        opt.ColorCount = int.Parse(v, CultureInfo.InvariantCulture);
                    }
                    break;
                case "--detail":
                    opt.Detail = Enum.Parse<DetailLevel>(Next(args, ref i), true);
                    break;
                case "--style":
                    opt.Style = Enum.Parse<ImageStyle>(Next(args, ref i), true);
                    break;
                case "--seed":
                    opt.Seed = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException("unknown option " + args[i]);
                    files.Add(args[i]);
                    break;
            }
        }
        if (files.Count == 0) throw new ArgumentException("no ground-truth SVG files given");
        if (outDir != null) Directory.CreateDirectory(outDir);
        Console.WriteLine("name                              size      meanDE  rmsDE   >JND    >0.1    nodes(gt)  regions  ms");
        foreach (var f in files)
        {
            var ground = SvgReader.ReadFile(f);
            var raster = Rasterizer.Render(ground, scale);
            var dir = outDir ?? (Path.GetDirectoryName(Path.GetFullPath(f)) ?? ".");
            var tag = FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(f)}-x{scale:0.##}");
            var basePath = Path.Combine(dir, tag);
            ImageIo.SavePng(raster, basePath + ".in.png");

            var res = Tracer.Trace(raster, opt);
            File.WriteAllText(basePath + ".out.svg", SvgWriter.Write(res.Document));
            var rendered = Rasterizer.Render(res.Document);
            ImageIo.SavePng(rendered, basePath + ".out.png");

            var m = Metrics.Compare(raster, rendered);
            ImageIo.SavePng(m.DiffImage, basePath + ".diff.png");
            int groundNodes = ground.Regions.Sum(r => r.Loops.Sum(l => l.Count));
            Console.WriteLine(FormattableString.Invariant(
                $"{tag,-32}  {raster.Width}x{raster.Height,-5} {m.MeanDe,7:0.0000} {m.RmsDe,7:0.0000} {m.PctOverJnd,6:P1} {m.PctOverBig,6:P2}  {res.Diagnostics.NodeCount}({groundNodes})   {res.Diagnostics.RegionCount,-7}  {res.Diagnostics.TotalMs}"));
        }
        return 0;
    }

    static int Samples(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : "samples";
        Directory.CreateDirectory(dir);
        Save(SampleGen.Crisp(), "crisp.png");
        Save(SampleGen.Blended(), "blended.png");
        Save(SampleGen.Transparent(), "transparent.png");
        var (cad, cadMask) = SampleGen.CadScan();
        Save(cad, "cad.png");
        Save(cadMask, "cad-mask.png");
        var pdfPath = Path.Combine(dir, "cad.pdf");
        File.WriteAllBytes(pdfPath, SampleGen.CadPdf());
        Console.WriteLine("wrote " + pdfPath);
        return 0;

        void Save(RasterImage img, string name)
        {
            var path = Path.Combine(dir, name);
            ImageIo.SavePng(img, path);
            Console.WriteLine("wrote " + path);
        }
    }

    /// <summary>
    /// Planarity self-check: with shared boundaries the region areas must tile the opaque
    /// canvas. Expected coverage is measured on the INPUT image (alpha >= 128, matching
    /// Segmenter.OpaqueThreshold), so pixels silently deleted by absorption are caught
    /// instead of being baked into the baseline.
    /// </summary>
    static bool PlanarityCheck(TraceResult res, RasterImage input)
    {
        double vector = res.Document.Regions.Sum(RegionArea);
        var px = input.Pixels;
        double raster = 0;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] >= 128) raster++;
        double deviation = raster == 0 ? 0 : Math.Abs(vector - raster) / raster;
        bool ok = deviation <= 0.02;
        Console.WriteLine(FormattableString.Invariant(
            $"planarity: vector {vector:0.0} px² vs raster {raster:0} px -> deviation {deviation:P2} [{(ok ? "PASS" : "FAIL")}]"));
        return ok;
    }

    static double RegionArea(RegionPath region)
    {
        double sum = 0;
        foreach (var loop in region.Loops)
        {
            if (loop.Count == 0) continue;
            double signed = 0;
            var prev = loop[0].P0;
            foreach (var seg in loop)
            {
                for (int s = 1; s <= 16; s++)
                {
                    var cur = seg.Eval(s / 16.0);
                    signed += prev.X * cur.Y - cur.X * prev.Y;
                    prev = cur;
                }
            }
            sum += signed * 0.5;
        }
        return Math.Abs(sum);
    }

    static RasterImage SegmentationImage(TraceResult res)
    {
        var img = new RasterImage(res.Width, res.Height);
        var px = img.Pixels;
        for (int i = 0; i < res.LabelMap.Length; i++)
        {
            int l = res.LabelMap[i];
            if (l < 0) continue;
            var c = res.Document.Palette[l].Color;
            int pi = i * 4;
            px[pi] = c.R;
            px[pi + 1] = c.G;
            px[pi + 2] = c.B;
            px[pi + 3] = 255;
        }
        return img;
    }

    static void PrintStats(TraceResult res)
    {
        var d = res.Diagnostics;
        var p = d.Params;
        Console.WriteLine(FormattableString.Invariant(
            $"  style={p.Style} exactPalette={p.ExactPalette} k={p.KMeansK} merge={(p.MergeClusters ? p.MergeThreshold : 0):0.###} minArea={p.MinRegionArea}"));
        Console.WriteLine(FormattableString.Invariant(
            $"  fitTol={Math.Sqrt(p.FitToleranceSq):0.##}px corner={p.CornerThresholdDeg}deg clamp={p.SmoothClamp}px noise={p.NoiseShare:F4} noisy={(p.Noisy ? "true" : "false")} textured={(p.Textured ? "true" : "false")}"));
        Console.WriteLine(FormattableString.Invariant(
            $"  chains={d.ChainCount} regions={d.RegionCount} nodes={d.NodeCount} palette={res.Document.Palette.Count}"));
        Console.WriteLine(FormattableString.Invariant(
            $"  timings: palette {d.PaletteMs} ms, segment {d.SegmentMs} ms, boundaries {d.BoundaryMs} ms, curves {d.FitMs} ms, total {d.TotalMs} ms"));
    }

    static string Next(string[] args, ref int i)
    {
        if (++i >= args.Length) throw new ArgumentException("missing value for " + args[i - 1]);
        return args[i];
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            vecto — bitmap-to-vector tracer (Vector Magic-style planar output)

            usage:
              vecto trace <images...> [options]  vectorize images (also: vecto <image>);
                                                wildcards ok: vecto trace *.png --out-dir out;
                                                PDF input is rasterized in memory (PDFium)
                                                and traced page by page
              vecto samples [dir]               write procedural test images
              vecto render <svg> [-o png] [--scale s]
                                                rasterize an SVG (M/L/H/V/C/Z paths)
              vecto bench <svg...> [--scale s] [--out-dir d] [trace options]
                                                ground-truth round trip: rasterize the
                                                original vector, trace it, diff the result

            options:
              -o, --out <file>      output path; .pdf writes a vector PDF, anything else
                                    SVG (default: input with .svg), single input only
              --out-dir <dir>       output directory for batches (default: next to each input)
              --colors <n|auto>     fixed palette size, or automatic (default auto)
              --max-colors <n>      cap for automatic palette (default 16)
              --detail <level>      low | medium | high (default medium)
              --style <style>       auto | crisp | blended | photo | lineart (default auto)
              --polygons            skip curve fitting, emit simplified polygons
              --epsilon <px>        polygon simplification tolerance (0 = exact lattice)
              --seed <n>            random seed for palette clustering (default 1)
              --pdf-dpi <n>         rasterization DPI for PDF pages (default 600)
              --pages <range>       PDF pages to trace: 1, 2-4, comma lists (default 1)
              --seg-png <file>      also write the segmentation as a PNG
              --check               verify planar partition (areas must tile the canvas)
              --stats               print resolved parameters and stage timings
            """);
    }
}
