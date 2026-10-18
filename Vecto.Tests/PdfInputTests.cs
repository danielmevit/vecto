using Vecto.Cli;
using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// PDF input (VEC-47): the generated cad.pdf (FlateDecode-embedded CAD scan) rasterizes
/// to a bitmap that matches the embedded scan, --pages/--pdf-dpi parsing is strict,
/// non-PDF inputs keep taking the raster path, and Auto on raster inputs stays
/// byte-identical (regression: the PDF path must not perturb the existing pipeline).
/// </summary>
public class PdfInputTests
{
    // cad.pdf/cad.png are written once into a temp dir; PDFium is not thread-safe, so
    // xUnit's parallel test collections must not race the rasterizer — one static lazy
    // keeps a single shared fixture
    static readonly Lazy<string> Fixture = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "vecto-pdf-tests");
        Directory.CreateDirectory(dir);
        var (scan, _) = SampleGen.CadScan();
        File.WriteAllBytes(Path.Combine(dir, "cad.pdf"), SampleGen.CadPdf());
        ImageIo.SavePng(scan, Path.Combine(dir, "cad.png"));
        return dir;
    });

    static string FixturePath(string name) => Path.Combine(Fixture.Value, name);

    [Fact]
    public void Rasterize_At600Dpi_MatchesEmbeddedScanDimensionsAndInk()
    {
        // 480x360 pt page at 600 DPI; PDFtoImage truncates 4000.0*0.9999 float error to 3999
        var page = PdfInput.Load(FixturePath("cad.pdf"), 600, "1").Single();
        Assert.Equal(1, page.Number);
        var img = page.Image;
        Assert.Equal(3999, img.Width);
        Assert.Equal(3000, img.Height);

        // ink presence: the dark-pixel share must track the scan's ground-truth ink mask
        double maskInk = InkFraction(SampleGen.CadScan().Mask);
        double rasterInk = InkFraction(img);
        Assert.True(rasterInk >= 0.85 * maskInk && rasterInk <= 1.15 * maskInk,
            $"raster ink share {rasterInk:0.####} drifted from the mask's {maskInk:0.####}");
    }

    [Fact]
    public void Rasterize_At72Dpi_IsPixelCloseToEmbeddedScan()
    {
        // at 72 DPI the page maps 1:1 onto the embedded 480x360 raster; with image
        // smoothing disabled the render must reproduce the scan, not a resampled copy
        var img = PdfInput.Load(FixturePath("cad.pdf"), 72, "1").Single().Image;
        var scan = SampleGen.CadScan().Scan;
        Assert.Equal(scan.Width, img.Width);
        Assert.Equal(scan.Height, img.Height);
        long diff = 0;
        for (int i = 0; i < scan.Pixels.Length; i += 4)
            diff += Math.Abs(img.Pixels[i] - scan.Pixels[i]);
        double mean = (double)diff / (scan.Width * scan.Height);
        Assert.True(mean <= 1.0, $"mean gray difference {mean:0.###} > 1 per pixel");
    }

    [Theory]
    [InlineData("1", 1, new[] { 1 })]
    [InlineData("2-4", 10, new[] { 2, 3, 4 })]
    [InlineData("1,3-4,2", 10, new[] { 1, 2, 3, 4 })]
    [InlineData(" 3 ", 10, new[] { 3 })]
    [InlineData("4-4", 10, new[] { 4 })]
    [InlineData("10", 10, new[] { 10 })]
    [InlineData("1-3,2-4", 10, new[] { 1, 2, 3, 4 })]
    public void ParsePageRange_Valid_SortsAndDeduplicates(string range, int pageCount, int[] expected)
    {
        Assert.Equal(expected, PdfInput.ParsePageRange(range, pageCount));
    }

    [Theory]
    [InlineData("", 1)]          // empty
    [InlineData("   ", 1)]      // whitespace only
    [InlineData("abc", 1)]      // not a number
    [InlineData("0", 1)]        // pages are 1-based
    [InlineData("1.5", 1)]
    [InlineData("2-1", 1)]      // reversed range
    [InlineData("1-", 1)]        // dangling range
    [InlineData("-2", 1)]
    [InlineData("1,,2", 1)]     // empty list element
    [InlineData("1;2", 1)]
    public void ParsePageRange_Invalid_Throws(string range, int pageCount)
    {
        Assert.Throws<ArgumentException>(() => PdfInput.ParsePageRange(range, pageCount));
    }

    [Theory]
    [InlineData("2", 1)]        // beyond the last page
    [InlineData("1-5", 2)]
    [InlineData("3", 2)]
    public void ParsePageRange_OutOfRange_Throws(string range, int pageCount)
    {
        Assert.Throws<ArgumentException>(() => PdfInput.ParsePageRange(range, pageCount));
    }

    [Theory]
    [InlineData("600", 600)]
    [InlineData("72", 72)]
    [InlineData("1", 1)]
    [InlineData("4800", 4800)]
    public void ParseDpi_Valid(string value, int expected)
    {
        Assert.Equal(expected, PdfInput.ParseDpi(value));
    }

    [Theory]
    [InlineData("")]             // empty
    [InlineData("abc")]          // not a number
    [InlineData("0")]            // out of range
    [InlineData("-5")]           // out of range
    [InlineData("600.5")]
    [InlineData("4801")]         // above the accepted maximum
    [InlineData("999999999999")] // overflow
    public void ParseDpi_Invalid_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => PdfInput.ParseDpi(value));
    }

    [Theory]
    [InlineData("drawing.pdf", true)]
    [InlineData("DRAWING.PDF", true)]
    [InlineData("dRaWing.PdF", true)]
    [InlineData("drawing.png", false)]
    [InlineData("drawing", false)]
    [InlineData("drawing.pdfx", false)]
    [InlineData("drawing.pdf.png", false)]
    public void IsPdf_ExtensionOnly_CaseInsensitive(string path, bool expected)
    {
        Assert.Equal(expected, PdfInput.IsPdf(path));
    }

    [Fact]
    public void NonPdfExtension_DoesNotTakePdfPath()
    {
        // the branch selector must send .png through ImageIo; the loaded pixels must be
        // exactly the generated scan (lossless PNG round trip)
        var path = FixturePath("cad.png");
        Assert.False(PdfInput.IsPdf(path));
        var img = ImageIo.Load(path);
        var scan = SampleGen.CadScan().Scan;
        Assert.Equal(scan.Width, img.Width);
        Assert.Equal(scan.Height, img.Height);
        Assert.Equal(scan.Pixels, img.Pixels);
    }

    [Fact]
    public void TraceCadPdf_Lineart_YieldsTwoColourResult()
    {
        // 150 DPI keeps the test fast; the Validate line exercises the full 600 DPI trace
        var img = PdfInput.Load(FixturePath("cad.pdf"), 150, "1").Single().Image;
        var res = Tracer.Trace(img, new TraceOptions { Style = ImageStyle.LineArt });
        Assert.Equal(2, res.Document.Palette.Count);
        Assert.True(res.Document.Regions.Count >= 2, "expected paper plus at least one ink region");
        Assert.Contains(res.Document.Regions,
            r => res.Document.Palette[r.PaletteIndex].Color.R < 128);
    }

    [Fact]
    public void RasterInputs_AutoIsByteIdentical_Regression()
    {
        // the PDF feature must not perturb the raster path: Auto on cad.png must produce
        // the same SVG as Auto on the in-memory scan, and re-tracing must be deterministic
        var scan = SampleGen.CadScan().Scan;
        var fromFile = SvgWriter.Write(Tracer.Trace(ImageIo.Load(FixturePath("cad.png")), new TraceOptions()).Document);
        var fromMemory = SvgWriter.Write(Tracer.Trace(scan, new TraceOptions()).Document);
        Assert.Equal(fromMemory, fromFile);
        var again = SvgWriter.Write(Tracer.Trace(ImageIo.Load(FixturePath("cad.png")), new TraceOptions()).Document);
        Assert.Equal(fromFile, again);
    }

    static double InkFraction(RasterImage img)
    {
        long ink = 0;
        var px = img.Pixels;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i + 3] >= 128 && px[i] < 128)
                ink++;
        return (double)ink / ((long)img.Width * img.Height);
    }
}
