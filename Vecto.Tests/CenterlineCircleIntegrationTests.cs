using Vecto.Core;
using System.Reflection;

namespace Vecto.Tests;

public class CenterlineCircleIntegrationTests
{
    static VectorDocument Trace(Func<Vec2, bool> shape, int w = 240, int h = 200)
    {
        var image = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                image.SetPixel(x, y, shape(new Vec2(x + .5, y + .5))
                    ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
        return Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document;
    }

    static bool Ring(Vec2 p) => Math.Abs((p - new Vec2(90, 100)).Length - 40) <= 3;
    static bool Solid(Vec2 p) => p.X >= 128 && p.X < 220 && p.Y >= 60 && p.Y < 140;

    [Fact]
    public void AttachedSolidEmitsOneMeasuredCircleAndKeepsSolidFilled()
    {
        var doc = Trace(p => Ring(p) || Solid(p));
        var circle = Assert.Single(doc.Strokes, s => s.Closed);
        Assert.Equal(4, circle.Segments.Count);
        var center = (circle.Segments[0].P0 + circle.Segments[2].P0) * .5;
        Assert.InRange((center - new Vec2(90, 100)).Length, 0, 1.5);
        Assert.InRange(Math.Abs((circle.Segments[0].P0 - center).Length - 40), 0, 1.5);
        Assert.InRange(circle.Width, 5, 7);
        // Reuse the established DXF parser and arc reconstruction, on actual public output.
        var dxf = DxfWriter.Write(doc);
        var polylines = Dxf<List<List<(int Code, string Value)>>>("RecordsIn", dxf, "ENTITIES", "POLYLINE");
        int index = Assert.Single(Enumerable.Range(0, polylines.Count), i =>
            polylines[i].Any(p => p.Code == 40 && double.Parse(p.Value,
                System.Globalization.CultureInfo.InvariantCulture) > 0) &&
            Dxf<double>("Num", polylines[i], 70) == 1);
        Assert.InRange(Math.Abs(Dxf<double>("Num", polylines[index], 40) - circle.Width), 0, .00001);
        Assert.InRange(Math.Abs(Dxf<double>("Num", polylines[index], 41) - circle.Width), 0, .00001);
        var vertices = Dxf<List<(double X, double Y, double B)>>("Vertices", dxf, index);
        Assert.Equal(4, vertices.Count);
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            Assert.InRange(Math.Abs(a.B + Math.Tan(Math.PI / 8)), 0, .000001);
            var arc = Dxf<(Vec2 Center, double Radius, double Sweep)>("ArcFromBulge",
                new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), a.B);
            Assert.InRange((arc.Center - new Vec2(center.X, 200 - center.Y)).Length, 0, .0001);
            Assert.InRange(Math.Abs(arc.Radius - (circle.Segments[0].P0 - center).Length), 0, .0001);
        }
        Assert.Contains(doc.Regions, r => doc.Palette[r.PaletteIndex].Color.R == 0 && r.Area > 4000);
    }

    [Fact]
    public void WholeComponentRingIsNotDuplicated()
    {
        var doc = Trace(Ring);
        // Identical source on accepted edcaa6a produces eight open pieces.
        // This fixture guards unchanged legacy behavior, not canonical admission.
        Assert.Equal(8, doc.Strokes.Count);
        Assert.All(doc.Strokes, stroke => Assert.False(stroke.Closed));
        // Supported whole-component control: five r65/width9 rings plus one long line.
        new CenterlineTests().CadCircleRowWithDrawingLine_KeepsFiveClosedStrokes(false);
    }

    [Fact]
    public void AttachedRingKeepsHatchesAndTextInk()
    {
        var doc = Trace(p => Ring(p) || Solid(p) ||
            Enumerable.Range(0, 4).Any(i => p.X >= 210 && p.X < 380 &&
                p.Y >= 70 + 18 * i && p.Y < 73 + 18 * i) ||
            Enumerable.Range(0, 5).Any(i => p.X >= 420 + 20 * i && p.X < 423 + 20 * i &&
                p.Y >= 160 && p.Y < 180), 560, 220);
        Assert.Single(doc.Strokes, s => s.Closed);
        for (int i = 0; i < 4; i++)
        {
            double y = 71.5 + 18 * i;
            Assert.Contains(doc.Strokes, s => s.Segments.Any(g =>
                Math.Abs(g.P0.Y - y) < 2 && Math.Abs(g.P3.Y - y) < 2 &&
                Math.Min(g.P0.X, g.P3.X) < 250 && Math.Max(g.P0.X, g.P3.X) > 370));
        }
        var filled = Rasterizer.Render(new VectorDocument { Width = doc.Width, Height = doc.Height,
            Palette = doc.Palette, Regions = doc.Regions, Strokes = [] });
        for (int i = 0; i < 5; i++) Assert.True(filled.GetPixel(421 + 20 * i, 170).R < 128);
        Assert.True(filled.GetPixel(180, 100).R < 128);
        Assert.DoesNotContain(doc.Strokes, s => s.Segments.Any(g => g.P0.X > 410));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrOccludedSourceDoesNotBecomeCircle(bool occluded)
    {
        var doc = Trace(p => (Ring(p) && (occluded || !(p.X < 90 && Math.Abs(p.Y - 100) < 5))) ||
            Solid(p) || (occluded && p.X > 60 && p.X < 120 && p.Y > 100 && p.Y < 140));
        Assert.DoesNotContain(doc.Strokes, s => s.Closed && s.Segments.Count == 4 &&
            s.Segments.All(seg => !seg.IsLine(.05)));
    }

    [Theory]
    [InlineData("diameter")]
    [InlineData("chord")]
    [InlineData("tangent")]
    public void ContainmentDoesNotDeleteCrossingLine(string kind)
    {
        var center = new Vec2(90, 100);
        var proposal = new CenterlineCircles.Proposal(0, 0, center, 40, 6,
            new CenterlineCircles.Evidence(1, 1, 37, 0, 0, 1, 1, 1, 1, 1));
        var a = kind switch { "diameter" => new Vec2(50, 100), "chord" => new Vec2(58, 76), _ => new Vec2(50, 60) };
        var b = kind switch { "diameter" => new Vec2(130, 100), "chord" => new Vec2(122, 76), _ => new Vec2(130, 60) };
        var d = b - a;
        var stroke = new StrokePath { Width = 3, Segments = [new CubicBezier(a, a + d * (1.0 / 3), a + d * (2.0 / 3), b)] };
        Assert.False(Centerline.OwnedByCircle(stroke, proposal, new int[240 * 200], 240, 200, 6));
        // Pair the containment regression with the public pipeline: the crossing source
        // destroys the empty cavity and must never be replaced by a completed circle.
        var doc = Trace(p => Ring(p) || Solid(p) || Distance(p, a, b) <= 1.5);
        Assert.NotEmpty(doc.Strokes);
        var rendered = Rasterizer.Render(doc);
        for (int i = 1; i < 20; i++)
        {
            var p = a + (b - a) * (i / 20.0);
            Assert.True(rendered.GetPixel((int)p.X, (int)p.Y).R < 128,
                $"{kind} source ink lost at {p}");
        }
        if (kind != "tangent") Assert.DoesNotContain(doc.Strokes, s => s.Closed);
    }

    static T Dxf<T>(string name, params object[] args) =>
        (T)typeof(DxfWriterTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;

    static double Distance(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a;
        return (p - (a + d * Math.Clamp((p - a).Dot(d) / d.LengthSq, 0, 1))).Length;
    }
}
