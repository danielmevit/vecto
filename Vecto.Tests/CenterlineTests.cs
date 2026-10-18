using Vecto.Cli;
using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>Centerline extraction (VEC-50 stage 2): thin line-work traces as single stroked
/// paths with a stroke weight; text glyphs and wide shapes keep the filled treatment.</summary>
public class CenterlineTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Black = new(0, 0, 0, 255);

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
    public void EvenWidthLine_CentersBetweenTwinRows()
    {
        // 2-px vertical band, cols 30-31, y 8..55: the twin-row shift must put the
        // centreline at x = 31.0, between the rows — a centreline that wobbles onto a
        // row paints a 2-px stroke 3 px wide (what the CAD benchmark gate catches)
        var img = Make(64, 64, (x, y) => x is 30 or 31 && y is >= 8 and <= 55);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.False(stroke.Closed);
        Assert.True(Math.Abs(stroke.Width - 2.0) <= 0.5, $"width {stroke.Width:0.00} vs 2.0");
        foreach (var seg in stroke.Segments)
        {
            Assert.True(seg.IsLine(0.05), "a straight stroke must not come out curved");
            Assert.True(Math.Abs(seg.P0.X - 31.0) <= 0.1 && Math.Abs(seg.P3.X - 31.0) <= 0.1,
                $"segment drifted off the band centre: {seg.P0.X:0.00}..{seg.P3.X:0.00}");
        }
        Assert.Single(doc.Regions);
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
