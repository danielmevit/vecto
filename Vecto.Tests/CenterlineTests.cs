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
        // flattened length (16 sub-samples per segment): exact for lines, converges for
        // arcs — a chord sum would under-read the exact quarter-arc pieces of a circle
        double len = 0;
        foreach (var seg in stroke.Segments)
        {
            var prev = seg.P0;
            for (int i = 1; i <= 16; i++)
            {
                var q = seg.Eval(i / 16.0);
                len += Math.Sqrt(q.DistSq(prev));
                prev = q;
            }
        }
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
    public void Junction_Cross_CollinearArmsWeld_StillMeet()
    {
        // two crossing 3-px lines: the junction splits the skeleton into four chains, but
        // the opposite arms are collinear pieces of ONE straight stroke — stage 3 welds
        // each pair into a single 2-anchor line, and the two lines still cross exactly
        var img = Make(64, 64, (x, y) =>
            (y is >= 30 and <= 32 && x is >= 8 and <= 55) ||
            (x is >= 30 and <= 32 && y is >= 8 and <= 55));
        var doc = TraceLineArt(img);
        Assert.Equal(2, doc.Strokes.Count);
        StrokePath? h = null, v = null;
        foreach (var stroke in doc.Strokes)
        {
            Assert.False(stroke.Closed);
            Assert.True(Math.Abs(stroke.Width - 3.0) <= 0.5, $"width {stroke.Width:0.00} vs 3.0");
            var seg = Assert.Single(stroke.Segments);
            Assert.True(seg.IsLine(0.05), "a welded arm pair must be one line, not a curve");
            if (Math.Abs(seg.P3.Y - seg.P0.Y) < 1e-9) h = stroke;
            if (Math.Abs(seg.P3.X - seg.P0.X) < 1e-9) v = stroke;
        }
        Assert.NotNull(h);
        Assert.NotNull(v);
        // each welded line spans its full drawn extent, tip to tip
        Assert.True(Math.Min(h.Segments[0].P0.X, h.Segments[0].P3.X) <= 9.5 &&
                    Math.Max(h.Segments[0].P0.X, h.Segments[0].P3.X) >= 54.5,
            $"horizontal span {Math.Min(h.Segments[0].P0.X, h.Segments[0].P3.X):0.0}..{Math.Max(h.Segments[0].P0.X, h.Segments[0].P3.X):0.0}");
        Assert.True(Math.Min(v.Segments[0].P0.Y, v.Segments[0].P3.Y) <= 9.5 &&
                    Math.Max(v.Segments[0].P0.Y, v.Segments[0].P3.Y) >= 54.5,
            $"vertical span {Math.Min(v.Segments[0].P0.Y, v.Segments[0].P3.Y):0.0}..{Math.Max(v.Segments[0].P0.Y, v.Segments[0].P3.Y):0.0}");
        // and they still meet: the crossing point lies inside both spans, on both lines
        double vx = v.Segments[0].P0.X, hy = h.Segments[0].P0.Y;
        Assert.True(vx >= Math.Min(h.Segments[0].P0.X, h.Segments[0].P3.X) &&
                    vx <= Math.Max(h.Segments[0].P0.X, h.Segments[0].P3.X),
            "the vertical line must cross the horizontal one, not stop short");
        Assert.True(hy >= Math.Min(v.Segments[0].P0.Y, v.Segments[0].P3.Y) &&
                    hy <= Math.Max(v.Segments[0].P0.Y, v.Segments[0].P3.Y),
            "the horizontal line must cross the vertical one, not stop short");
    }

    [Fact]
    public void TJunction_BarWeldsAcross_StemSnapsExactlyOntoIt()
    {
        // a 3-px bar with a 3-px stem meeting it mid-length: the T junction splits the bar
        // into two collinear pieces — stage 3 welds them back into ONE 2-anchor line, and
        // the stem's head lands exactly on that line (endpoint snapped to the intersection)
        var img = Make(64, 64, (x, y) =>
            (y is >= 30 and <= 32 && x is >= 8 and <= 55) ||
            (x is >= 30 and <= 32 && y is >= 30 and <= 55));
        var doc = TraceLineArt(img);
        Assert.Equal(2, doc.Strokes.Count);
        StrokePath? bar = null, stem = null;
        foreach (var stroke in doc.Strokes)
        {
            Assert.False(stroke.Closed);
            var seg = Assert.Single(stroke.Segments);
            Assert.True(seg.IsLine(0.05), "both the welded bar and the stem must be single lines");
            if (Math.Abs(seg.P3.Y - seg.P0.Y) < 1e-9) bar = stroke;
            if (Math.Abs(seg.P3.X - seg.P0.X) < 1e-9) stem = stroke;
        }
        Assert.NotNull(bar);
        Assert.NotNull(stem);
        // the bar spans both sides of the junction: one stroke, tip to tip
        Assert.True(Math.Min(bar.Segments[0].P0.X, bar.Segments[0].P3.X) <= 9.5 &&
                    Math.Max(bar.Segments[0].P0.X, bar.Segments[0].P3.X) >= 54.5,
            $"bar span {Math.Min(bar.Segments[0].P0.X, bar.Segments[0].P3.X):0.0}..{Math.Max(bar.Segments[0].P0.X, bar.Segments[0].P3.X):0.0}");
        // the stem's head sits exactly on the bar's line — the intersection, not its own
        // junction pixel half a pixel off it
        double barY = bar.Segments[0].P0.Y;
        double headY = Math.Min(stem.Segments[0].P0.Y, stem.Segments[0].P3.Y);
        Assert.True(Math.Abs(headY - barY) < 1e-6,
            $"stem head at y={headY:0.00} must meet the bar's line at y={barY:0.00} exactly");
    }

    [Fact]
    public void BrokenLine_SmallGapJump_WeldsIntoOneStroke()
    {
        // a 3-px horizontal line broken by a 2-px paper gap: two components, two collinear
        // strokes — the junction pass jumps the gap and welds them into ONE 2-anchor line
        var img = Make(96, 64, (x, y) =>
            y is >= 30 and <= 32 && (x is >= 8 and <= 39 || x is >= 42 and <= 73));
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.False(stroke.Closed);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "the welded pieces must be one line, not a curve");
        Assert.True(Math.Abs(seg.P0.Y - 31.5) <= 0.1 && Math.Abs(seg.P3.Y - 31.5) <= 0.1,
            $"welded line drifted off the stroke: {seg.P0.Y:0.00}..{seg.P3.Y:0.00}");
        double x0 = Math.Min(seg.P0.X, seg.P3.X), x1 = Math.Max(seg.P0.X, seg.P3.X);
        Assert.True(x0 <= 9.5 && x1 >= 72.5, $"welded span {x0:0.0}..{x1:0.0} must cover both pieces");
        Assert.Single(doc.Regions);
    }

    [Fact]
    public void ManyBrokenLines600Pieces_WeldInto60Strokes()
    {
        // 60 parallel lines, each cut into 10 pieces by 2-px gaps: 600 strokes, 540 welds.
        // The junction pass must stay near-linear in the strokes — a pairwise scan that
        // restarts after every weld is O(welds × strokes²) and takes minutes here — and
        // every line must still come out as ONE straight stroke spanning all its pieces
        var img = Make(680, 500, (x, y) => false);
        for (int line = 0; line < 60; line++)
        {
            int y0 = 4 + line * 8;
            for (int piece = 0; piece < 10; piece++)
            {
                int x0 = 8 + piece * 66;
                for (int y = y0; y <= y0 + 2; y++)
                    for (int x = x0; x < x0 + 64; x++)
                        img.SetPixel(x, y, Black);
            }
        }
        var doc = TraceLineArt(img);
        Assert.Equal(60, doc.Strokes.Count);
        foreach (var stroke in doc.Strokes)
        {
            Assert.False(stroke.Closed);
            var seg = Assert.Single(stroke.Segments);
            Assert.True(seg.IsLine(0.05), "a welded line must be one line, not a curve");
            Assert.True(Math.Abs(seg.P0.Y - seg.P3.Y) <= 0.1, "welded line drifted off its row");
            double x0 = Math.Min(seg.P0.X, seg.P3.X), x1 = Math.Max(seg.P0.X, seg.P3.X);
            Assert.True(x0 <= 9.5 && x1 >= 664.5, $"welded span {x0:0.0}..{x1:0.0} must cover all pieces");
        }
        Assert.Single(doc.Regions);
    }

    [Fact]
    public void KnownWidthDiagonal45_SingleStrokeWithAccurateWidth()
    {
        // 45-degree band, three diagonal rows (x 8..56, |y-(x-8)| <= 1): the last
        // synthetic from the VEC-50 spec. Perpendicular truth: width 3/sqrt(2) = 2.12,
        // length 48*sqrt(2) = 67.9 — the staircase must not read wider than the band
        var img = Make(64, 64, (x, y) => x is >= 8 and <= 56 && Math.Abs(y - (x - 8)) <= 1);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.False(stroke.Closed);
        Assert.True(Math.Abs(stroke.Width - 3.0 / Math.Sqrt(2)) <= 0.5,
            $"width {stroke.Width:0.00} vs {3.0 / Math.Sqrt(2):0.00}");
        Assert.True(Math.Abs(PathLength(stroke) - 48.0 * Math.Sqrt(2)) <= 3.0,
            $"length {PathLength(stroke):0.0} vs {48.0 * Math.Sqrt(2):0.0}");
        // the centreline follows the diagonal: every BODY segment runs at ~45 degrees.
        // The staircase tips may wobble over a pixel (a short near-vertical jog where the
        // band ends) — round caps cover that; only a drifting body would smear the stroke
        foreach (var seg in stroke.Segments)
        {
            if (seg.P3.DistSq(seg.P0) < 4.0) continue;
            double dx = Math.Abs(seg.P3.X - seg.P0.X), dy = Math.Abs(seg.P3.Y - seg.P0.Y);
            Assert.True(Math.Abs(dx - dy) <= 0.5, $"segment not diagonal: dx={dx:0.00} dy={dy:0.00}");
        }
        // and the stroke actually spans the band, tip to tip
        var head = stroke.Segments[0].P0;
        var tail = stroke.Segments[^1].P3;
        Assert.True(head.DistSq(new Vec2(8.5, 0.5)) <= 6.5, $"head at {head.X:0.0},{head.Y:0.0}");
        Assert.True(tail.DistSq(new Vec2(56.5, 48.5)) <= 6.5, $"tail at {tail.X:0.0},{tail.Y:0.0}");
        Assert.Single(doc.Regions);
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

    // Mixed-width source ownership and editable primitives (VEC97). These use the public
    // tracer and renderer: a plausible skeleton alone is not a successful recovery.
    static void AssertInkRetained(RasterImage source, VectorDocument doc, int tolerance)
    {
        var render = Rasterizer.Render(doc);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).R > 128) continue;
                bool covered = false;
                for (int dy = -tolerance; dy <= tolerance && !covered; dy++)
                    for (int dx = -tolerance; dx <= tolerance; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((uint)xx >= (uint)source.Width || (uint)yy >= (uint)source.Height) continue;
                        var c = render.GetPixel(xx, yy);
                        if (c.A > 128 && c.R < 128) { covered = true; break; }
                    }
                Assert.True(covered, $"uncovered source ink at {x},{y}");
            }
    }

    static StrokePath AssertPrimitive(VectorDocument doc, Vec2 a, Vec2 b, double width, double endpointTolerance)
    {
        var matches = doc.Strokes.Where(s => !s.Closed && s.Segments.Count == 1 &&
            s.Segments[0].IsLine(0.02) && Math.Abs(s.Width - width) <= Math.Max(0.75, width * 0.15) &&
            Math.Min(Math.Max(s.Segments[0].P0.DistSq(a), s.Segments[0].P3.DistSq(b)),
                Math.Max(s.Segments[0].P0.DistSq(b), s.Segments[0].P3.DistSq(a))) <= endpointTolerance * endpointTolerance).ToList();
        Assert.True(matches.Count == 1, $"expected one editable line {a} to {b}, width {width}; found {matches.Count}");
        return matches[0];
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(7, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 2)]
    public void MixedSolidLongLine_EditableRunAndSolidInkSurvive(int width, int scale)
    {
        var img = Make(380 * scale, 180 * scale, (x, y) =>
            (x >= 20 * scale && x < 80 * scale && y >= 50 * scale && y < 110 * scale) ||
            (x >= 70 * scale && x < 340 * scale && y >= 78 * scale && y < (78 + width) * scale));
        var doc = TraceLineArt(img);
        AssertPrimitive(doc, new Vec2(80 * scale, (78 + width / 2.0) * scale),
            new Vec2(339.5 * scale, (78 + width / 2.0) * scale), width * scale, 2 * width * scale);
        AssertInkRetained(img, doc, 2 * scale);
        var fillOnly = new VectorDocument { Width = doc.Width, Height = doc.Height, Palette = doc.Palette,
            Regions = doc.Regions, Strokes = [] };
        var fill = Rasterizer.Render(fillOnly);
        for (int y = 60 * scale; y < 100 * scale; y++)
            for (int x = 30 * scale; x < 70 * scale; x++)
                Assert.True(fill.GetPixel(x, y).R < 128, "solid must remain filled, independently of strokes");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MixedTwoWidthOutline_LongCornerRunsAreSeparateEditableLines(int scale)
    {
        var img = Make(380 * scale, 250 * scale, (x, y) =>
        {
            double xx = (x + 0.5) / scale, yy = (y + 0.5) / scale;
            return (xx >= 20 && xx <= 80 && yy >= 100 && yy <= 150) ||
                (xx >= 50 && xx <= 330 && Math.Abs(yy - 40.5) < 2.5) ||
                (xx >= 50 && xx <= 330 && Math.Abs(yy - 200.5) < 1.5) ||
                (Math.Abs(xx - 50.5) < 2.5 && yy >= 40 && yy <= 200) ||
                (Math.Abs(xx - 330.5) < 2.5 && yy >= 40 && yy <= 200);
        });
        var doc = TraceLineArt(img);
        AssertPrimitive(doc, new Vec2(50 * scale, 40 * scale), new Vec2(330 * scale, 40 * scale), 5 * scale, 8 * scale);
        AssertPrimitive(doc, new Vec2(330 * scale, 40 * scale), new Vec2(330 * scale, 200 * scale), 5 * scale, 8 * scale);
        AssertPrimitive(doc, new Vec2(50 * scale, 200 * scale), new Vec2(330 * scale, 200 * scale), 3 * scale, 8 * scale);
        AssertInkRetained(img, doc, 2 * scale);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void MixedSolidJunction_MainRunsRemainContinuous(bool cross, int scale)
    {
        var img = Make(380 * scale, 240 * scale, (x, y) =>
        {
            double xx = (x + 0.5) / scale, yy = (y + 0.5) / scale;
            return (xx >= 20 && xx <= 70 && yy >= 95 && yy <= 145) ||
                (xx >= 60 && xx <= 340 && Math.Abs(yy - 120.5) < 1.5) ||
                (Math.Abs(xx - 200.5) < 1.5 && yy >= 40 && yy <= (cross ? 200 : 120));
        });
        var doc = TraceLineArt(img);
        var horizontal = AssertPrimitive(doc, new Vec2(70 * scale, 120 * scale), new Vec2(340 * scale, 120 * scale),
            3 * scale, 6 * scale);
        var vertical = AssertPrimitive(doc, new Vec2(200 * scale, 40 * scale), new Vec2(200 * scale, (cross ? 200 : 120) * scale),
            3 * scale, 6 * scale);
        var hs = horizontal.Segments[0];
        var vs = vertical.Segments[0];
        Assert.True(Math.Abs(hs.P0.Y - 120 * scale) < scale && Math.Abs(vs.P0.X - 200 * scale) < scale);
        AssertInkRetained(img, doc, 2 * scale);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LocalRecovery_SeparateRingStaysClosed_TextAndSolidStayFilled(int scale)
    {
        var img = Make(480 * scale, 260 * scale, (x, y) =>
        {
            double xx = (x + 0.5) / scale, yy = (y + 0.5) / scale;
            double radius = Math.Sqrt((xx - 410) * (xx - 410) + (yy - 90) * (yy - 90));
            bool glyph = Enumerable.Range(0, 5).Any(i => xx >= 110 + 20 * i && xx < 113 + 20 * i && yy >= 210 && yy < 230);
            return glyph || radius is >= 22.5 and <= 25.5 ||
                (xx >= 20 && xx < 80 && yy >= 50 && yy < 110) ||
                (xx >= 70 && xx < 340 && yy >= 78 && yy < 81);
        });
        var doc = TraceLineArt(img);
        AssertPrimitive(doc, new Vec2(80 * scale, 79.5 * scale), new Vec2(339.5 * scale, 79.5 * scale), 3 * scale, 6 * scale);
        var ring = Assert.Single(doc.Strokes, s => s.Closed);
        Assert.InRange(PathLength(ring), 2 * Math.PI * 24 * scale - 6 * scale, 2 * Math.PI * 24 * scale + 6 * scale);
        Assert.DoesNotContain(doc.Strokes, s => s.Segments.Any(g => g.P0.Y > 200 * scale));
        var fillOnly = new VectorDocument { Width = doc.Width, Height = doc.Height, Palette = doc.Palette,
            Regions = doc.Regions, Strokes = [] };
        var fill = Rasterizer.Render(fillOnly);
        for (int i = 0; i < 5; i++) Assert.True(fill.GetPixel((111 + 20 * i) * scale, 220 * scale).R < 128);
        Assert.True(fill.GetPixel(50 * scale, 80 * scale).R < 128);
        AssertInkRetained(img, doc, 2 * scale);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public void MixedScaleClosedGlyphRow_StaysFilled_WithHairline(int hairlineWidth, int scale)
    {
        // Same two-counter block glyph geometry for both hairline controls. A very
        // long hairline controls the global median without changing glyph stroke size.
        var img = Make(1000 * scale, 180 * scale, (x, y) =>
        {
            int xx = x / scale, yy = y / scale;
            if (xx >= 10 && xx < 990 && yy >= 15 && yy < 15 + hairlineWidth) return true;
            int gx = (xx - 60) % 30;
            return xx >= 60 && xx < 194 && gx < 14 && yy >= 60 && yy < 100 &&
                (gx < 2 || gx >= 12 || yy < 62 || yy >= 98 || yy is >= 79 and < 82);
        });
        var doc = TraceLineArt(img);
        Assert.Single(doc.Strokes);
        Assert.DoesNotContain(doc.Strokes, s => s.Segments.Any(g => g.P0.Y >= 50 * scale || g.P3.Y >= 50 * scale));
        // Five black glyph regions, ten white counters and the surrounding paper.
        Assert.Equal(16, doc.Regions.Count);
        var fill = Rasterizer.Render(new VectorDocument { Width = doc.Width, Height = doc.Height,
            Palette = doc.Palette, Regions = doc.Regions, Strokes = [] });
        for (int i = 0; i < 5; i++)
        {
            int left = 60 + 30 * i;
            foreach (int yy in new[] { 60, 80, 99 })
                Assert.True(fill.GetPixel((left + 7) * scale, yy * scale).R < 128);
            foreach (int yy in new[] { 70, 90 })
            {
                Assert.True(fill.GetPixel(left * scale, yy * scale).R < 128);
                Assert.True(fill.GetPixel((left + 13) * scale, yy * scale).R < 128);
                Assert.True(fill.GetPixel((left + 7) * scale, yy * scale).R > 128);
            }
        }
    }

    [Fact]
    public void MixedScaleLongOpenParallelLines_StillReachLocalExtraction()
    {
        var img = Make(1100, 240, (x, y) =>
            (x >= 10 && x < 1090 && y == 10) ||
            (Enumerable.Range(0, 5).Any(i =>
                (x >= 100 && x < 300 && y >= 50 + i * 35 && y < 52 + i * 35) ||
                (x >= 100 && x < 110 && y >= 46 + i * 35 && y < 56 + i * 35))));
        var doc = TraceLineArt(img);
        Assert.Equal(6, doc.Strokes.Count);
        for (int i = 0; i < 5; i++)
            Assert.Contains(doc.Strokes, s => s.Segments.Any(g => Math.Abs(g.P0.Y - (51 + i * 35)) < 2 &&
                (g.P3 - g.P0).Length > 180));
        AssertInkRetained(img, doc, 1);
    }

    // ---- VEC-52 stage 3: CAD regularization ----

    [Fact]
    public void NoisyStraightLine_ExactlyTwoAnchors_EndpointsWithin1Px()
    {
        // 5-px stroke along y≈40 with a ±1 px scan wobble (a gentle sine, like real scan
        // wobble): ONE 2-anchor path whose endpoints sit within 1 px of the true tips,
        // not a chain of cubics following the wobble point-by-point
        var img = Make(160, 80, (x, y) =>
        {
            int c = 40 + (int)Math.Round(Math.Sin(x * 0.3));
            return x is >= 10 and <= 149 && Math.Abs(y - c) <= 2;
        });
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.False(stroke.Closed);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line, not a curve");
        Assert.True(Math.Abs(seg.P0.X - 10.5) <= 1.0, $"head x {seg.P0.X:0.00} vs 10.5");
        Assert.True(Math.Abs(seg.P3.X - 149.5) <= 1.0, $"tail x {seg.P3.X:0.00} vs 149.5");
        Assert.True(Math.Abs(seg.P0.Y - 40.5) <= 1.0 && Math.Abs(seg.P3.Y - 40.5) <= 1.0,
            $"endpoints off the line: {seg.P0.Y:0.00}..{seg.P3.Y:0.00}");
    }

    [Fact]
    public void NoisyAxisLine_SnapsExactlyHorizontal()
    {
        // a stroke drawn 0.4° off horizontal: within the 1.5° snap tolerance, so it must
        // come out EXACTLY horizontal — the difference between a vector that looks like
        // the scan and one a CAD user can edit
        var img = Make(160, 80, (x, y) =>
        {
            int c = (int)Math.Round(40 + (x - 10) * Math.Tan(0.4 * Math.PI / 180));
            return x is >= 10 and <= 149 && Math.Abs(y - c) <= 1;
        });
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "an axis stroke must be one line");
        Assert.True(Math.Abs(seg.P3.Y - seg.P0.Y) < 1e-9,
            $"axis snap failed: dy = {seg.P3.Y - seg.P0.Y:0.###}");
        Assert.True(Math.Abs(seg.P3.X - seg.P0.X) > 100, "the stroke must span its length");
    }

    [Fact]
    public void CircleStroke_ExactArc_CenterRadiusWithinTolerance()
    {
        // 3-px annulus r=14: one closed stroke whose pieces all sit on ONE exact circle
        // (the Kåsa arc hypothesis), not a chain of cubics approximating it
        var img = Make(64, 64, (x, y) =>
        {
            double d = Math.Sqrt((x - 31.5) * (x - 31.5) + (y - 31.5) * (y - 31.5));
            return d is >= 12.5 and <= 15.5;
        });
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.True(stroke.Closed, "a ring skeleton must trace as a closed stroke");
        Assert.True(stroke.Segments.Count <= 8,
            $"a full circle is 4 quarter-arc pieces, not {stroke.Segments.Count}");
        // Kåsa fit of the anchor points: center within 1 px, radius within 0.75 px of truth
        var anchors = new List<Vec2>();
        foreach (var seg in stroke.Segments)
        {
            anchors.Add(seg.P0);
            anchors.Add(seg.P3);
        }
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var p in anchors)
        {
            double z = p.X * p.X + p.Y * p.Y;
            sx += p.X; sy += p.Y; sxx += p.X * p.X; syy += p.Y * p.Y; sxy += p.X * p.Y;
            sxz += p.X * z; syz += p.Y * z; sz += z;
        }
        int n = anchors.Count;
        double m00 = sxx, m01 = sxy, m02 = sx;
        double m10 = sxy, m11 = syy, m12 = sy;
        double m20 = sx, m21 = sy, m22 = n;
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        Assert.True(Math.Abs(det) > 1e-9, "anchors must not be collinear");
        double detA = sxz * (m11 * m22 - m12 * m21) - m01 * (syz * m22 - m12 * sz) + m02 * (syz * m21 - m11 * sz);
        double detB = m00 * (syz * m22 - m12 * sz) - sxz * (m10 * m22 - m12 * m20) + m02 * (m10 * sz - syz * m20);
        double detC = m00 * (m11 * sz - syz * m21) - m01 * (m10 * sz - syz * m20) + sxz * (m10 * m21 - m11 * m20);
        var center = new Vec2(detA / det / 2, detB / det / 2);
        double radius = Math.Sqrt(detC / det + center.LengthSq);
        Assert.True(center.DistSq(new Vec2(31.5, 31.5)) <= 1.0,
            $"fitted center {center.X:0.00},{center.Y:0.00} vs 31.5,31.5");
        Assert.True(Math.Abs(radius - 14.0) <= 0.75, $"fitted radius {radius:0.00} vs 14");
        // and every anchor really sits on that circle (chain points carry the skeleton's
        // ±0.3 px lattice residual — the Kasa fit averages, it does not interpolate)
        foreach (var p in anchors)
            Assert.True(Math.Abs((p - center).Length - radius) <= 0.35,
                $"anchor off the fitted circle by {Math.Abs((p - center).Length - radius):0.00}");
    }

    [Fact]
    public void AutoStyle_ByteIdentical_RegardlessOfCenterlineStage()
    {
        // VEC-52 may only touch the LineArt centerline path: Auto output must be
        // byte-identical with the stage on or off, and contain no stroked path
        var img = Make(64, 64, (x, y) => y is >= 30 and <= 32 && x is >= 8 and <= 55);
        string withStage;
        try
        {
            Centerline.Enabled = false;
            string withoutStage = SvgWriter.Write(Tracer.Trace(img, new TraceOptions { Style = ImageStyle.Auto }).Document);
            Centerline.Enabled = true;
            withStage = SvgWriter.Write(Tracer.Trace(img, new TraceOptions { Style = ImageStyle.Auto }).Document);
            Assert.Equal(withoutStage, withStage);
        }
        finally
        {
            Centerline.Enabled = true;
        }
        Assert.DoesNotContain("fill=\"none\"", withStage);
    }

    // ---- VEC-52d: angle rationalization (round-angle grid + parallel relationships) ----

    /// <summary>Paints a ~3-px band along a line at <paramref name="angleDeg"/> with a
    /// gentle sine wobble along the normal (deterministic scan noise), like the prototype's
    /// make_chain: discs of radius 1.5 stepped along the line.</summary>
    static void PaintLine(RasterImage img, double x0, double y0, double angleDeg, double len,
        double amp, double waveLen)
    {
        double ang = angleDeg * Math.PI / 180.0;
        double dx = Math.Cos(ang), dy = Math.Sin(ang), nx = -dy, ny = dx;
        for (double t = 0; t <= len; t += 0.2)
        {
            double off = amp * Math.Sin(2 * Math.PI * t / waveLen);
            double cx = x0 + t * dx + off * nx, cy = y0 + t * dy + off * ny;
            for (int py = (int)Math.Floor(cy - 2); py <= (int)Math.Ceiling(cy + 2); py++)
                for (int px = (int)Math.Floor(cx - 2); px <= (int)Math.Ceiling(cx + 2); px++)
                {
                    if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) continue;
                    double ex = px + 0.5 - cx, ey = py + 0.5 - cy;
                    if (ex * ex + ey * ey <= 2.25) img.SetPixel(px, py, Black);
                }
        }
    }

    /// <summary>The stroke's line direction as an angle in [0, 180): a stroke and its
    /// reverse share one direction, so 179.999° reads as 179.999, not −0.001.</summary>
    static double DirDeg(StrokePath stroke)
    {
        var seg = stroke.Segments[0];
        double deg = Math.Atan2(seg.P3.Y - seg.P0.Y, seg.P3.X - seg.P0.X) * 180.0 / Math.PI % 180.0;
        return deg < 0 ? deg + 180.0 : deg;
    }

    [Fact]
    public void Noisy30degLine_SnapsExactlyToGrid()
    {
        // 30° is on the 15° grid: a wobbly scan of a 30° line must come out at EXACTLY
        // 30.00° — one 2-anchor line, not the measured 29.x with its scan error
        var img = Make(200, 120, (x, y) => false);
        PaintLine(img, 25, 30, 30.0, 140, 0.6, 21);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        Assert.False(stroke.Closed);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line");
        Assert.True(Math.Abs(DirDeg(stroke) - 30.0) <= 1e-6, $"angle {DirDeg(stroke):0.000} vs 30");
    }

    [Fact]
    public void Noisy15degLine_SnapsExactlyToGrid()
    {
        // the grid extends past the old 45° family: 15° is a snap angle now
        var img = Make(200, 120, (x, y) => false);
        PaintLine(img, 25, 40, 15.0, 140, 0.6, 21);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line");
        Assert.True(Math.Abs(DirDeg(stroke) - 15.0) <= 1e-6, $"angle {DirDeg(stroke):0.000} vs 15");
    }

    [Fact]
    public void Noisy60_8degLine_SnapsExactlyToGrid60()
    {
        // drawn at 60.8°: within the 1.5° grid tolerance of 60, so exactly 60.00°
        var img = Make(200, 160, (x, y) => false);
        PaintLine(img, 30, 20, 60.8, 120, 0.6, 21);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line");
        Assert.True(Math.Abs(DirDeg(stroke) - 60.0) <= 1e-6, $"angle {DirDeg(stroke):0.000} vs 60");
    }

    [Fact]
    public void OffGridLine_62_7_RoundsToWhole63()
    {
        // the owner's case: a line measuring 62.7° is a 63° line with scan error — 62.7
        // is 2.7° off the grid so nothing snaps; the whole-degree round IS the intent
        var img = Make(200, 160, (x, y) => false);
        PaintLine(img, 30, 25, 62.7, 120, 0.6, 21);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line");
        Assert.True(Math.Abs(DirDeg(stroke) - 63.0) <= 1e-6, $"angle {DirDeg(stroke):0.000} vs 63");
    }

    [Fact]
    public void OffGridLine_17_2_KeepsWhole17_NotForcedTo15()
    {
        // 17.2° is 2.2° off the 15° grid: it keeps its measured WHOLE degree (17) and
        // must NOT be dragged onto the grid — the tolerance is the only guard
        var img = Make(200, 120, (x, y) => false);
        PaintLine(img, 25, 40, 17.2, 140, 0.6, 21);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line");
        double ang = DirDeg(stroke);
        Assert.True(Math.Abs(ang - 17.0) <= 1e-6, $"angle {ang:0.000} vs 17");
        Assert.True(Math.Abs(ang - 15.0) > 0.5, "an off-grid line must not be forced onto the grid");
    }

    [Fact]
    public void NearSeamLine_179_9_ComesOutExactly0()
    {
        // a 179.9° line IS a 0° line: the 0/180 seam is handled everywhere, so a stroke
        // just under horizontal comes out exactly horizontal
        var img = Make(200, 120, (x, y) => false);
        PaintLine(img, 150, 60, 179.9, 120, 0.6, 21);
        var doc = TraceLineArt(img);
        var stroke = Assert.Single(doc.Strokes);
        var seg = Assert.Single(stroke.Segments);
        Assert.True(seg.IsLine(0.05), "a straight stroke must be one line");
        Assert.True(Math.Abs(seg.P3.Y - seg.P0.Y) < 1e-9,
            $"seam wrap failed: dy = {seg.P3.Y - seg.P0.Y:0.###}");
    }

    [Fact]
    public void ParallelFamily_23_5_UnifiesToSingleAngle()
    {
        // three lines drawn at 23.3/23.5/23.6° — ONE intended angle: the cluster's
        // circular mean unifies them, so all three come out at one whole-degree angle,
        // mutually parallel (the drawing stays self-consistent)
        var img = Make(220, 200, (x, y) => false);
        PaintLine(img, 20, 20, 23.3, 135, 0.6, 21);
        PaintLine(img, 20, 75, 23.5, 135, 0.6, 21);
        PaintLine(img, 20, 130, 23.6, 135, 0.6, 21);
        var doc = TraceLineArt(img);
        Assert.Equal(3, doc.Strokes.Count);
        double a0 = DirDeg(doc.Strokes[0]);
        foreach (var stroke in doc.Strokes)
        {
            Assert.False(stroke.Closed);
            var seg = Assert.Single(stroke.Segments);
            Assert.True(seg.IsLine(0.05), "each family member must be one line");
            Assert.True(Math.Abs(DirDeg(stroke) - a0) <= 1e-9,
                $"family not unified: {a0:0.000} vs {DirDeg(stroke):0.000}");
            Assert.True(Math.Abs(DirDeg(stroke) - Math.Round(DirDeg(stroke))) <= 1e-9,
                $"the unified angle must be a whole degree, got {DirDeg(stroke):0.000}");
        }
        Assert.True(Math.Abs(a0 - 23.5) <= 1.0,
            $"unified angle {a0:0.000} drifted away from the drawn family");
    }
}
