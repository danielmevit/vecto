using Vecto.Core;

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
        var circle = Assert.Single(doc.Strokes.Where(s => s.Closed));
        Assert.Equal(4, circle.Segments.Count);
        var center = (circle.Segments[0].P0 + circle.Segments[2].P0) / 2;
        Assert.InRange((center - new Vec2(90, 100)).Length, 0, 1.5);
        Assert.InRange(Math.Abs((circle.Segments[0].P0 - center).Length - 40), 0, 1.5);
        Assert.InRange(circle.Width, 5, 7);
        Assert.Contains(doc.Regions, r => doc.Palette[r.PaletteIndex].Color.R == 0 && r.Area > 4000);
    }

    [Fact]
    public void WholeComponentRingIsNotDuplicated()
    {
        var doc = Trace(Ring);
        Assert.Single(doc.Strokes);
        Assert.True(doc.Strokes[0].Closed);
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
        var stroke = new StrokePath { Width = 3, Segments = [new CubicBezier(a, a + d / 3, a + d * (2.0 / 3), b)] };
        Assert.False(Centerline.OwnedByCircle(stroke, proposal, new int[240 * 200], 240, 200, 6));
        // Pair the containment regression with the public pipeline: the crossing source
        // destroys the empty cavity and must never be replaced by a completed circle.
        var doc = Trace(p => Ring(p) || Solid(p) || Distance(p, a, b) <= 1.5);
        Assert.NotEmpty(doc.Strokes);
        if (kind != "tangent") Assert.DoesNotContain(doc.Strokes, s => s.Closed);
    }

    static double Distance(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a;
        return (p - (a + d * Math.Clamp((p - a).Dot(d) / d.LengthSq, 0, 1))).Length;
    }
}
