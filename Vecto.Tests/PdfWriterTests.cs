using System.Globalization;
using System.IO.Compression;
using System.Text;
using Vecto.Cli;
using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// PDF export (VEC-49): the hand-written PDF is structurally valid (header, xref offsets
/// that actually point at their objects, MediaBox, white underlay only for transparent
/// documents), byte-identical across writes, and a cad line-art trace exported to PDF
/// and rasterized back through PDFium matches the Core rasterizer's own render of the
/// same document. SVG output is guarded to be unaffected.
/// </summary>
public class PdfWriterTests
{
    static readonly Lazy<string> Dir = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "vecto-pdfwriter-tests");
        Directory.CreateDirectory(dir);
        return dir;
    });

    // ---- structure -------------------------------------------------------

    [Fact]
    public void Structure_HeaderXrefTrailerMediaBox()
    {
        var pdf = PdfWriter.Write(SquareDoc(opaque: true));
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/MediaBox [0 0 100 80]", text);

        // startxref must point at the xref table
        int sx = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        int eol = text.IndexOf('\n', sx);
        int startxref = int.Parse(text[sx..eol], CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[startxref..]);

        // every xref entry must point at its "N 0 obj" line
        int p = startxref + "xref\n".Length;
        Assert.StartsWith("0 5\n", text[p..]);
        p += "0 5\n".Length;
        for (int i = 0; i < 5; i++)
        {
            var line = text.Substring(p + i * 20, 20);
            if (i == 0)
                Assert.StartsWith("0000000000 65535 f", line);
            else
            {
                int off = int.Parse(line[..10], CultureInfo.InvariantCulture);
                Assert.StartsWith($"{i} 0 obj", text[off..]);
            }
        }
    }

    [Fact]
    public void Structure_ContentStreamOperators()
    {
        // opaque document: no white underlay, y is flipped (image (20,20) -> PDF (20,60))
        var content = Content(PdfWriter.Write(SquareDoc(opaque: true)));
        Assert.DoesNotContain(" re ", content);
        Assert.Contains("1 1 1 rg\n", content);
        Assert.Contains("0 0 0 rg\n", content);
        Assert.Contains("0.00 80.00 m\n", content);   // page rect starts at image (0,0)
        Assert.Contains("20.00 60.00 m\n", content);  // ink square starts at image (20,20)
        Assert.Contains("h\nf\n", content);
    }

    [Fact]
    public void Structure_TransparentDocumentGetsWhiteUnderlay()
    {
        var content = Content(PdfWriter.Write(SquareDoc(opaque: false)));
        Assert.Contains("1 1 1 rg\n0 0 100 80 re f\n", content);
        Assert.Contains("0 0 0 rg\n", content);
    }

    // ---- determinism -----------------------------------------------------

    [Fact]
    public void TwoWrites_AreByteIdentical()
    {
        var doc = Tracer.Trace(SampleGen.Blended(), new TraceOptions()).Document;
        Assert.Equal(PdfWriter.Write(doc), PdfWriter.Write(doc));

        var path = Path.Combine(Dir.Value, "det.pdf");
        PdfWriter.Write(doc, path);
        Assert.Equal(PdfWriter.Write(doc), File.ReadAllBytes(path));
    }

    // ---- round trip through a real PDF renderer --------------------------

    [Fact]
    public void RoundTrip_CadLineArt_PdfRenderMatchesCoreRasterizer()
    {
        // 144 DPI = exact 2x (150 hits a PDFtoImage float truncation, cf. the 600 DPI 3999
        // case in PdfInputTests); the Validate line exercises the full 600 DPI trace
        const int dpi = 144;
        var (scan, _) = SampleGen.CadScan();
        var doc = Tracer.Trace(scan, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        var path = Path.Combine(Dir.Value, "roundtrip.pdf");
        PdfWriter.Write(doc, path);

        var raster = PdfInput.Load(path, dpi, "1").Single().Image;
        // the page is w×h pt, so the bitmap is the document at DPI/72 scale; PDFium's own
        // rounding decides the exact size, so render the document at that size
        var direct = Rasterizer.Render(doc, (double)raster.Width / doc.Width);
        Assert.Equal(raster.Width, direct.Width);
        Assert.True(Math.Abs(raster.Height - direct.Height) <= 1,
            $"heights differ by more than rounding: PDFium {raster.Height} vs render {direct.Height}");
        int h = Math.Min(raster.Height, direct.Height);

        long total = 0, within = 0;
        long px = (long)raster.Width * h;
        var a = raster.Pixels;
        var b = direct.Pixels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < raster.Width; x++)
            {
                int i = (y * raster.Width + x) * 4;
                int d1 = Math.Abs(a[i] - b[i]);
                int d2 = Math.Abs(a[i + 1] - b[i + 1]);
                int d3 = Math.Abs(a[i + 2] - b[i + 2]);
                total += d1 + d2 + d3;
                if (Math.Max(d1, Math.Max(d2, d3)) <= 8) within++;
            }
        double mean = (double)total / (px * 3);
        Assert.True(mean <= 3.0, $"mean channel difference {mean:0.###} > 3/255");
        double share = (double)within / px;
        Assert.True(share >= 0.95, $"only {share:P1} of pixels within 8/255");
    }

    // ---- SVG regression guard --------------------------------------------

    public static TheoryData<string> Samples => new() { "crisp", "blended", "transparent", "lineart" };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SvgOutput_IsUnchangedByPdfExport(string sample)
    {
        // the PDF writer shares the document model but must not touch the SVG emit path:
        // tracing stays deterministic and a PDF export between two traces changes nothing
        var (img, opt) = Sample(sample);
        var svg1 = SvgWriter.Write(Tracer.Trace(img, opt).Document);
        PdfWriter.Write(Tracer.Trace(img, opt).Document, Path.Combine(Dir.Value, "guard.pdf"));
        var svg2 = SvgWriter.Write(Tracer.Trace(img, opt).Document);
        Assert.Equal(svg1, svg2);
    }

    // ---- helpers ----------------------------------------------------------

    static (RasterImage Image, TraceOptions Options) Sample(string name) => name switch
    {
        "crisp" => (SampleGen.Crisp(), new TraceOptions()),
        "blended" => (SampleGen.Blended(), new TraceOptions()),
        "transparent" => (SampleGen.Transparent(), new TraceOptions()),
        "lineart" => (SampleGen.CadScan().Scan, new TraceOptions { Style = ImageStyle.LineArt }),
        _ => throw new ArgumentException(name),
    };

    /// <summary>100×80 page; opaque tiles it (paper with a hole + ink in the hole),
    /// transparent has only the ink square over nothing.</summary>
    static VectorDocument SquareDoc(bool opaque)
    {
        var regions = new List<RegionPath>();
        if (opaque)
            regions.Add(new RegionPath
            {
                PaletteIndex = 0,
                Area = 6800,
                Loops = [RectLoop(0, 0, 100, 80, cw: false), RectLoop(20, 20, 60, 50, cw: true)],
            });
        regions.Add(new RegionPath { PaletteIndex = 1, Area = 1200, Loops = [RectLoop(20, 20, 60, 50, cw: false)] });
        return new VectorDocument
        {
            Width = 100,
            Height = 80,
            Palette =
            [
                new PaletteEntry { Color = new Rgba32(255, 255, 255, 255), PixelCount = 6800 },
                new PaletteEntry { Color = new Rgba32(0, 0, 0, 255), PixelCount = 1200 },
            ],
            Regions = regions,
        };
    }

    static List<CubicBezier> RectLoop(double x0, double y0, double x1, double y1, bool cw)
    {
        Vec2 a = new(x0, y0), b = new(x1, y0), c = new(x1, y1), d = new(x0, y1);
        var pts = cw ? new[] { a, d, c, b } : new[] { a, b, c, d };
        var segs = new List<CubicBezier>(4);
        for (int i = 0; i < 4; i++)
            segs.Add(CubicBezier.Line(pts[i], pts[(i + 1) % 4]));
        return segs;
    }

    /// <summary>Decompresses the single FlateDecode content stream of a written PDF.</summary>
    static string Content(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        int lenAt = text.IndexOf("/Length ", StringComparison.Ordinal) + "/Length ".Length;
        int length = int.Parse(text[lenAt..text.IndexOf(' ', lenAt)], CultureInfo.InvariantCulture);
        int at = text.IndexOf(">> stream\n", StringComparison.Ordinal) + ">> stream\n".Length;
        using var zlib = new ZLibStream(new MemoryStream(pdf, at, length), CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        zlib.CopyTo(outMs);
        return Encoding.Latin1.GetString(outMs.ToArray());
    }
}
