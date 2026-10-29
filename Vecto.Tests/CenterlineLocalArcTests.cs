using System.Reflection;
using Vecto.Core;

namespace Vecto.Tests;

public class CenterlineLocalArcTests
{
    [Theory]
    [InlineData(false, 1, 0, 70, 10)]
    [InlineData(false, 1.5, 37, 55, 8)]
    [InlineData(true, 1, 0, 100, 10)]
    [InlineData(true, 1.5, 23, 80, 8)]
    public void PublicAttachedDrawingRecoversCoherentAnalyticArcs(bool corner, double scale,
        double rotation, double radius, double width)
    {
        var fixture = Make(corner, scale, rotation, radius, width);
        var doc = fixture.Doc;
        foreach (var target in fixture.Targets)
        {
            var stroke = Assert.Single(doc.Strokes, s => Covers(s, target.Center, target.Start,
                target.Sweep, radius * scale));
            var first = stroke.Segments[0].P0 - target.Center;
            var last = stroke.Segments[^1].P3 - target.Center;
            double startAngle = Math.Atan2(first.Y, first.X);
            double endAngle = Math.Atan2(last.Y, last.X);
            double Error(double a) => Math.Abs(Math.Atan2(Math.Sin(a), Math.Cos(a))) * 180 / Math.PI;
            Assert.True(Error(startAngle - target.Start) <= 1.5 ||
                Error(startAngle - target.Start - target.Sweep) <= 1.5,
                $"Start {startAngle * 180 / Math.PI} target {target.Start * 180 / Math.PI}, end {endAngle * 180 / Math.PI}");
            Assert.True(Error(endAngle - target.Start) <= 1.5 ||
                Error(endAngle - target.Start - target.Sweep) <= 1.5,
                $"End {endAngle * 180 / Math.PI} target {target.Start * 180 / Math.PI}");
            Assert.False(stroke.Closed);
            Assert.InRange(stroke.Width, (width - 2) * scale, (width + 2) * scale);
            Assert.All(stroke.Segments, s => Assert.False(s.IsLine(.05)));
            Assert.InRange(stroke.Segments.Count, 1, 4);
            for (int i = 1; i < stroke.Segments.Count; i++)
                Assert.Equal(stroke.Segments[i - 1].P3, stroke.Segments[i].P0);
            // The public DXF must contain a weighted open polyline whose every
            // segment reconstructs this same circle, not tessellated chord geometry.
            var dxf = DxfWriter.Write(doc);
            var records = Dxf<List<List<(int Code, string Value)>>>("RecordsIn", dxf, "ENTITIES", "POLYLINE");
            bool analytic = false;
            for (int i = 0; i < records.Count; i++)
            {
                if (Dxf<double>("Num", records[i], 70) != 0) continue;
                var vertices = Dxf<List<(double X, double Y, double B)>>("Vertices", dxf, i);
                if (vertices.Count != stroke.Segments.Count + 1) continue;
                bool matches = true;
                for (int j = 0; j < vertices.Count - 1; j++)
                {
                    var a = vertices[j]; var b = vertices[j + 1];
                    if (Math.Abs(a.B) < .001 || Math.Abs(a.B) > Math.Tan(Math.PI / 8) + .000001)
                    { matches = false; break; }
                    var arc = Dxf<(Vec2 Center, double Radius, double Sweep)>("ArcFromBulge",
                        new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), a.B);
                    if ((arc.Center - new Vec2(target.Center.X, doc.Height - target.Center.Y)).Length > 2 * scale ||
                        Math.Abs(arc.Radius - radius * scale) > 2 * scale) matches = false;
                }
                analytic |= matches;
            }
            Assert.True(analytic, "No analytic common-circle DXF polyline covers the public arc");
        }
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel((int)fixture.Solid.X, (int)fixture.Solid.Y).R < 128);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("ellipse")]
    [InlineData("crossing")]
    [InlineData("concentric")]
    public void UnsupportedOrAmbiguousGeometryDoesNotBecomeACompleteSlotEnd(string kind)
    {
        var f = Make(false, 1, 0, 70, 10, kind);
        if (kind != "concentric")
            foreach (var t in f.Targets)
                Assert.DoesNotContain(f.Doc.Strokes, s => Covers(s, t.Center, t.Start, t.Sweep, 70));
        var rendered = Rasterizer.Render(f.Doc);
        if (kind == "gap")
        {
            Assert.True(rendered.GetPixel(150, 320).R > 128);
            Assert.True(rendered.GetPixel(490, 320).R > 128);
        }
        // Concentric bands must stay separated by real paper, even where both
        // share the drawing component through their tangent attachments.
        if (kind == "concentric") Assert.True(rendered.GetPixel(138, 320).R > 128);
        Assert.True(rendered.GetPixel((int)f.Solid.X, (int)f.Solid.Y).R < 128);
    }

    [Fact]
    public void PrintedCircularGlyphRowDoesNotAddOpenArcs()
    {
        var image = new RasterImage(640, 320);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var p = new Vec2(x + .5, y + .5);
                bool ink = Enumerable.Range(0, 6).Any(i => Math.Abs((p - new Vec2(60 + i * 85, 180)).Length - 24) < 3) ||
                    (y >= 40 && y <= 46 && x >= 30 && x <= 600);
                image.SetPixel(x, y, ink ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
            }
        var doc = Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        Assert.DoesNotContain(doc.Strokes, s => !s.Closed && s.Segments.Any(g => g.P0.Y > 120));
        var rendered = Rasterizer.Render(doc);
        for (int i = 0; i < 6; i++) Assert.True(rendered.GetPixel(84 + i * 85, 180).R < 128);
    }

    static bool Covers(StrokePath stroke, Vec2 center, double start, double sweep, double radius)
    {
        if (stroke.Closed || stroke.Segments.Count == 0) return false;
        var samples = stroke.Segments.SelectMany(g => Enumerable.Range(0, 101).Select(i => g.Eval(i / 100.0))).ToList();
        if (samples.Any(p => Math.Abs((p - center).Length - radius) > 2)) return false;
        int hits = 0;
        for (int i = 0; i <= 72; i++)
        {
            double a = start + sweep * i / 72;
            var p = center + new Vec2(Math.Cos(a), Math.Sin(a)) * radius;
            if (samples.Min(q => (q - p).Length) < 3) hits++;
        }
        return hits >= 70;
    }

    static (VectorDocument Doc, List<(Vec2 Center, double Start, double Sweep)> Targets, Vec2 Solid)
        Make(bool corner, double scale, double degrees, double radius, double width, string kind = "")
    {
        int size = (int)(640 * scale);
        double rotation = degrees * Math.PI / 180;
        Vec2 Map(Vec2 p) => new Vec2(320, 320) * scale +
            new Vec2((p.X - 320) * Math.Cos(rotation) - (p.Y - 320) * Math.Sin(rotation),
                (p.X - 320) * Math.Sin(rotation) + (p.Y - 320) * Math.Cos(rotation)) * scale;
        var image = new RasterImage(size, size);
        var targets = corner
            ? new List<(Vec2, double, double)> { (Map(new Vec2(320, 320)), -Math.PI / 2 + rotation, Math.PI / 2) }
            : new List<(Vec2, double, double)> { (Map(new Vec2(220, 320)), Math.PI / 2 + rotation, Math.PI),
                (Map(new Vec2(420, 320)), -Math.PI / 2 + rotation, Math.PI) };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var v = new Vec2((x + .5) / scale - 320, (y + .5) / scale - 320);
                var p = new Vec2(v.X * Math.Cos(rotation) + v.Y * Math.Sin(rotation) + 320,
                    -v.X * Math.Sin(rotation) + v.Y * Math.Cos(rotation) + 320);
                bool solid = p.X >= 295 && p.X < 345 && p.Y >= 110 && p.Y < 180;
                bool stem = Math.Abs(p.X - 320) <= width / 2 && p.Y >= 170 && p.Y <= 320 - radius;
                bool ink;
                if (corner)
                {
                    var q = p - new Vec2(320, 320);
                    ink = (q.X >= 0 && q.Y <= 0 && Math.Abs(q.Length - radius) <= width / 2) ||
                        (p.X >= 150 && p.X <= 320 && Math.Abs(q.Y + radius) <= width / 2) ||
                        (p.Y >= 320 && p.Y <= 500 && Math.Abs(q.X - radius) <= width / 2);
                }
                else
                {
                    double cx = p.X < 320 ? 220 : 420;
                    var q = p - new Vec2(cx, 320);
                    bool side = p.X < 220 || p.X > 420;
                    double r = kind == "ellipse" ? Math.Sqrt(q.X * q.X * .64 + q.Y * q.Y) : q.Length;
                    ink = (side && Math.Abs(r - radius) <= width / 2) ||
                        (p.X >= 220 && p.X <= 420 && Math.Abs(Math.Abs(p.Y - 320) - radius) <= width / 2);
                    if (kind == "gap" && Math.Abs(p.Y - 320) < 8) ink = false;
                    if (kind == "crossing" && side && Math.Abs(p.Y - 320) < 5 && Math.Abs(q.X) < radius + 30) ink = true;
                    if (kind == "concentric") ink |= (side && Math.Abs(q.Length - radius - 24) <= width / 2) ||
                        (p.X >= 220 && p.X <= 420 && Math.Abs(Math.Abs(p.Y - 320) - radius - 24) <= width / 2);
                }
                ink |= solid || stem;
                image.SetPixel(x, y, ink ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
            }
        return (Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document, targets, Map(new Vec2(320, 140)));
    }

    static T Dxf<T>(string name, params object[] args) =>
        (T)typeof(DxfWriterTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
}
