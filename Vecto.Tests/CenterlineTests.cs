using Vecto.Core;
using Xunit;
using Xunit.Abstractions;

namespace Vecto.Tests;

/// <summary>Centerline extraction (VEC-50 stage 2): thin line-work traces as single stroked
/// paths with a stroke weight; text glyphs and wide shapes keep the filled treatment.</summary>
public class CenterlineTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Black = new(0, 0, 0, 255);

    readonly ITestOutputHelper _output;
    public CenterlineTests(ITestOutputHelper output) => _output = output;

    static RasterImage Make(int w, int h, Func<int, int, bool> ink) =>
        Make(w, h, (x, y) => ink(x, y) ? Black : White);

    static RasterImage Make(int w, int h, Func<int, int, Rgba32> f)
    {
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, f(x, y));
        return img;
    }

    static VectorDocument TraceLineArt(RasterImage img) =>
        Tracer.Trace(img, new TraceOptions { Style = ImageStyle.LineArt }).Document;

    static double PathLength(StrokePath stroke)
    {
        double len = 0;
        foreach (var seg in stroke.Segments) len += Math.Sqrt(seg.P3.DistSq(seg.P0));
        return len;
    }

    [Fact]
    public void KnownWidthLine_SingleStrokeWithAccurateWidth()
    {
        // 3-px horizontal stroke, 48 px long: one stroked path, width within 0.5 px, straight
        var img = Make(64, 64, (x, y) => y is >= 30 and <= 32 && x is >= 8 and <= 55);
        var doc = TraceLineArt(img);
        Assert.Single(doc.Strokes);
        var stroke = doc.Strokes[0];
        Assert.False(stroke.Closed);
        Assert.True(Math.Abs(stroke.Width - 3.0) <= 0.5, $"width {stroke.Width:0.00} vs 3.0");
        Assert.True(Math.Abs(PathLength(stroke) - 48.0) <= 2.0, $"length {PathLength(stroke):0.0} vs 48");
        foreach (var seg in stroke.Segments)
        {
            Assert.True(seg.IsLine(0.05), "a straight stroke must not come out curved");
            Assert.True(Math.Abs(seg.P0.Y - 31.5) <= 0.5 && Math.Abs(seg.P3.Y - 31.5) <= 0.5,
                $"segment drifted off the stroke centre: {seg.P0.Y:0.00}..{seg.P3.Y:0.00}");
        }
        // the ink pixels went to the stroke: paper is the only region left
        Assert.Single(doc.Regions);
    }

    [Fact]
    public void SvgOutput_StrokedPathHasFillNoneRoundCapsAndWidth()
    {
        var img = Make(64, 64, (x, y) => y is >= 30 and <= 32 && x is >= 8 and <= 55);
        var svg = SvgWriter.Write(TraceLineArt(img));
        Assert.Contains("fill=\"none\"", svg);
        Assert.Contains("stroke-linecap=\"round\"", svg);
        Assert.Contains("stroke-linejoin=\"round\"", svg);
        Assert.Contains("stroke-width=\"3.00\"", svg);
        Assert.Contains("stroke=\"#000000\"", svg);
        // the stroked path itself must not gain a closing chord (regions legitimately close with Z)
        var strokePath = svg.Split("fill=\"none\"")[1][..^8];
        Assert.DoesNotContain("Z", strokePath);
    }

    [Fact]
    public void ClosedRing_TracesAsClosedStroke()
    {
        // 3-px annulus, radius 14: the skeleton is a cycle, so the stroke must close
        var img = Make(64, 64, (x, y) =>
        {
            double d = Math.Sqrt((x - 31.5) * (x - 31.5) + (y - 31.5) * (y - 31.5));
            return d is >= 12.5 and <= 15.5;
        });
        var doc = TraceLineArt(img);
        Assert.Single(doc.Strokes);
        var stroke = doc.Strokes[0];
        Assert.True(stroke.Closed, "a ring skeleton must trace as a closed stroke");
        Assert.True(Math.Abs(stroke.Width - 3.0) <= 0.5, $"width {stroke.Width:0.00} vs 3.0");
        Assert.True(Math.Abs(PathLength(stroke) - 2 * Math.PI * 14) <= 5.0,
            $"ring length {PathLength(stroke):0.0} vs {2 * Math.PI * 14:0.0}");
        var svg = SvgWriter.Write(doc);
        Assert.Contains(" Z\"", svg);
    }

    [Fact]
    public void Junction_CrossSplitsIntoFourStrokes()
    {
        // two crossing 3-px lines: the junction splits the skeleton into four chains
        var img = Make(64, 64, (x, y) =>
            (y is >= 30 and <= 32 && x is >= 8 and <= 55) ||
            (x is >= 30 and <= 32 && y is >= 8 and <= 55));
        var doc = TraceLineArt(img);
        Assert.Equal(4, doc.Strokes.Count);
        foreach (var stroke in doc.Strokes)
        {
            Assert.False(stroke.Closed);
            Assert.True(Math.Abs(stroke.Width - 3.0) <= 0.5, $"width {stroke.Width:0.00} vs 3.0");
        }
    }

    [Fact]
    public void DEBUG_Dump()
    {
        Centerline.DebugLog = new List<string>();
        var line = Make(64, 64, (x, y) => y is >= 30 and <= 32 && x is >= 8 and <= 55);
        var inkm = ScanPreprocess.InkMask(line);
        var skel2 = Centerline.Ridge(inkm, Centerline.EdtSq(inkm, 64, 64), 64, 64);
        for (int y = 28; y < 35; y++)
        {
            var row = new char[64];
            for (int x = 5; x < 60; x++)
                row[x] = skel2[y * 64 + x] ? '#' : inkm[y * 64 + x] ? '.' : ' ';
            ((Action<string>)_output.WriteLine)(new string(row));
        }
        var linDoc = TraceLineArt(line);
        ((Action<string>)_output.WriteLine)($"LINE strokes={linDoc.Strokes.Count}");
        foreach (var s in Centerline.DebugLog) ((Action<string>)_output.WriteLine)(s);
        Centerline.DebugLog.Clear();
        var (scan, mask) = SampleGen.CadScan();
        var cadDoc = Tracer.Trace(scan, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        ((Action<string>)_output.WriteLine)($"CAD strokes={cadDoc.Strokes.Count} regions={cadDoc.Regions.Count}");
        foreach (var s in Centerline.DebugLog.Take(60)) ((Action<string>)_output.WriteLine)(s);
        Centerline.DebugLog = null;
        var ring = Make(48, 48, (x, y) =>
        {
            double d = Math.Sqrt((x - 23.5) * (x - 23.5) + (y - 23.5) * (y - 23.5));
            return d is >= 8.5 and <= 11.5;
        });
        var doc = TraceLineArt(ring);
        ((Action<string>)_output.WriteLine)($"RING strokes={doc.Strokes.Count} regions={doc.Regions.Count}");
        foreach (var s in Centerline.DebugLog) ((Action<string>)_output.WriteLine)(s);
        Centerline.DebugLog.Clear();
        var diag = Make(64, 64, (x, y) => x >= 8 && x <= 56 && y == x - 8);
        // reconstruct the exact mask the pipeline hands to Thin
        var ink = ScanPreprocess.InkMask(diag);
        ScanPreprocess.BridgeDiagonalContacts(ink, 64, 64);
        var skel = Centerline.Ridge(ink, Centerline.EdtSq(ink, 64, 64), 64, 64);
        ((Action<string>)_output.WriteLine)($"ink={ink.Count(b => b)} skel={skel.Count(b => b)}");
        for (int y = 0; y < 64; y++)
        {
            var row = new char[64];
            for (int x = 0; x < 64; x++)
                row[x] = skel[y * 64 + x] ? '#' : ink[y * 64 + x] ? '.' : ' ';
            ((Action<string>)_output.WriteLine)(new string(row));
        }
        doc = TraceLineArt(diag);
        ((Action<string>)_output.WriteLine)($"DIAG strokes={doc.Strokes.Count} regions={doc.Regions.Count}");
        foreach (var s in Centerline.DebugLog) ((Action<string>)_output.WriteLine)(s);
        Centerline.DebugLog.Clear();
        var cross = Make(64, 64, (x, y) =>
            (y is >= 30 and <= 32 && x is >= 8 and <= 55) ||
            (x is >= 30 and <= 32 && y is >= 8 and <= 55));
        doc = TraceLineArt(cross);
        ((Action<string>)_output.WriteLine)($"CROSS strokes={doc.Strokes.Count} regions={doc.Regions.Count}");
        foreach (var s in Centerline.DebugLog) ((Action<string>)_output.WriteLine)(s);
        Centerline.DebugLog = null;
    }

    [Fact]
    public void TextRowAndWideBar_StayFilled_LongLineBecomesStroke()
    {
        // a row of five glyph-sized bars (text), a wide bar, and one long 1-px line:
        // only the line may centerline — glyphs in a peer row and wide shapes stay filled
        var img = Make(96, 64, (x, y) =>
            (y is >= 20 and <= 29 && x is 10 or 20 or 30 or 40 or 50) ||   // glyph row
            (y is >= 40 and <= 49 && x is >= 60 and <= 79) ||              // wide bar
            (y == 57 && x is >= 8 and <= 67));                             // long line
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.True(Math.Abs(stroke.Width - 1.0) <= 0.5, $"line width {stroke.Width:0.00} vs 1.0");
        // the five glyphs and the bar are still filled ink regions (plus paper)
        Assert.Equal(7, doc.Regions.Count);
        Assert.Equal(2, doc.Palette.Count);
    }
}
