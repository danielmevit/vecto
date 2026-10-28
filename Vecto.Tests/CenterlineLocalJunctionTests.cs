using System.Reflection;
using Vecto.Core;

namespace Vecto.Tests;

public class CenterlineLocalJunctionTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 37)]
    [InlineData(2, 90)]
    public void SupportedFiniteTShortensAlongItsLine(double scale, double degrees)
    {
        var p = Run(scale, degrees, false, false, false);
        Assert.InRange((p - new Vec2(150, 150)).Length, 0, .000001);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CrossingGapAndForeignTargetRemainUnchanged(bool crossing, bool gap, bool foreign)
    {
        var p = Run(1, 0, crossing, gap, foreign);
        Assert.Equal(new Vec2(150, crossing ? 170 : 156), p);
    }

    [Fact]
    public void CircleHatchesPreserveCircleSolidAndDisjointGlyphInk()
    {
        var image = new RasterImage(320, 220);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var p = new Vec2(x + .5, y + .5);
                bool ring = Math.Abs((p - new Vec2(90, 100)).Length - 40) <= 3;
                bool solid = p.X >= 128 && p.X < 220 && p.Y >= 60 && p.Y < 140;
                bool hatch = p.X >= 15 && p.X <= 53 && Math.Abs(p.Y - 100) <= 1.5;
                bool glyph = p.X >= 270 && p.X < 280 && p.Y >= 170 && p.Y < 185;
                image.SetPixel(x, y, ring || solid || hatch || glyph
                    ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
            }
        var doc = Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        var circle = Assert.Single(doc.Strokes, s => s.Closed);
        Assert.Equal(4, circle.Segments.Count);
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(180, 100).R < 128);
        Assert.True(rendered.GetPixel(275, 177).R < 128);
        Assert.True(rendered.GetPixel(30, 100).R < 128);
    }

    static Vec2 Run(double scale, double degrees, bool crossing, bool gap, bool foreign)
    {
        const int w = 400;
        double angle = degrees * Math.PI / 180;
        var origin = new Vec2(150, 150);
        Vec2 Map(double x, double y) => origin + new Vec2(x * Math.Cos(angle) - y * Math.Sin(angle),
            x * Math.Sin(angle) + y * Math.Cos(angle)) * scale;
        StrokePath Line(Vec2 a, Vec2 b, double width) => new()
        { Width = width, Segments = [new CubicBezier(a, a + (b - a) * (1.0 / 3), a + (b - a) * (2.0 / 3), b)] };
        var strokes = new List<StrokePath>
        {
            Line(Map(0, crossing ? 20 : 6), Map(0, -65), 8 * scale),
            Line(Map(-65, 0), Map(65, 0), 16 * scale),
        };
        var comp = Enumerable.Repeat(-1, w * w).ToArray();
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                var q = new Vec2(x + .5, y + .5) - origin;
                double a = (q.X * Math.Cos(angle) + q.Y * Math.Sin(angle)) / scale;
                double b = (-q.X * Math.Sin(angle) + q.Y * Math.Cos(angle)) / scale;
                bool bar = Math.Abs(a) <= 65 && Math.Abs(b) <= 8;
                bool stem = Math.Abs(a) <= 4 && b >= -65 && b <= (crossing ? 20 : 6);
                if (bar || stem) comp[y * w + x] = foreign && bar && !stem ? 2 : 1;
                if (gap && Math.Abs(b) < 2) comp[y * w + x] = -1;
            }
        var method = typeof(Centerline).GetMethod("RepairLocalJunctions", BindingFlags.Static | BindingFlags.NonPublic)!;
        var circles = Activator.CreateInstance(method.GetParameters()[1].ParameterType)!;
        method.Invoke(null, [strokes, circles, new HashSet<int> { 1 }, comp, w, w]);
        Assert.Equal(8 * scale, strokes[0].Width);
        Assert.Single(strokes[0].Segments);
        return strokes[0].Segments[0].P0;
    }
}
