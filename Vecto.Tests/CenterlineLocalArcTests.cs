using System.Reflection;
using Vecto.Core;

namespace Vecto.Tests;

public class CenterlineLocalArcTests
{
    [Theory]
    [InlineData(false, 1, 0, 70, 10, 0)]
    [InlineData(false, 1.5, 37, 55, 8, 0)]
    [InlineData(false, 1.5, 37, 55, 8, .35)]
    [InlineData(true, 1, 0, 100, 10, 0)]
    [InlineData(true, 1.5, 23, 80, 8, 0)]
    [InlineData(true, 1.5, 23, 80, 8, .35)]
    public void PublicAttachedDrawingRecoversCoherentAnalyticArcs(bool corner, double scale,
        double rotation, double radius, double width, double phase)
    {
        var fixture = Make(corner, scale, rotation, radius, width, phase: phase);
        var doc = fixture.Doc;
        foreach (var target in fixture.Targets)
        {
            var stroke = Assert.Single(doc.Strokes, s => Covers(s, target.Center, target.Start,
                target.Sweep, radius * scale));
            double signedSweep = AssertEndpoints(stroke, target.Center, target.Start, target.Sweep, radius * scale, scale);
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
            int analytic = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (Dxf<double>("Num", records[i], 70) != 0) continue;
                var vertices = Dxf<List<(double X, double Y, double B)>>("Vertices", dxf, i);
                if (vertices.Count != stroke.Segments.Count + 1) continue;
                bool matches = true;
                double exportedSweep = 0;
                for (int j = 0; j < vertices.Count; j++)
                {
                    var expected = j < stroke.Segments.Count ? stroke.Segments[j].P0 : stroke.Segments[^1].P3;
                    if ((new Vec2(vertices[j].X, doc.Height - vertices[j].Y) - expected).Length > .002)
                        matches = false;
                }
                if (!matches) continue;
                Assert.InRange(Math.Abs(Dxf<double>("Num", records[i], 40) - stroke.Width), 0, .002);
                for (int j = 0; j < vertices.Count - 1; j++)
                {
                    var a = vertices[j]; var b = vertices[j + 1];
                    if (Math.Abs(a.B) < .001 || Math.Abs(a.B) > Math.Tan(Math.PI / 8) + .000001)
                    { matches = false; break; }
                    var arc = Dxf<(Vec2 Center, double Radius, double Sweep)>("ArcFromBulge",
                        new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), a.B);
                    exportedSweep += arc.Sweep;
                    if ((arc.Center - new Vec2(target.Center.X, doc.Height - target.Center.Y)).Length > 2 * scale ||
                        Math.Abs(arc.Radius - radius * scale) > 2 * scale) matches = false;
                }
                Assert.InRange(Math.Abs(exportedSweep + signedSweep), 0, .0001);
                if (matches) analytic++;
            }
            Assert.Equal(1, analytic);
        }
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel((int)fixture.Solid.X, (int)fixture.Solid.Y).R < 128);
    }

    [Theory]
    [InlineData(1, 0, "open")]
    [InlineData(1.5, 31, "open")]
    [InlineData(1, 0, "open-adjacent")]
    [InlineData(1.5, 31, "open-adjacent")]
    public void GenuineOpenArcKeepsItsSourceEndpoints(double scale, double rotation, string kind)
    {
        var fixture = Make(true, scale, rotation, 100, 10, kind);
        var target = fixture.Targets[0];
        var stroke = Assert.Single(fixture.Doc.Strokes, s => Covers(s, target.Center, target.Start, target.Sweep, 100 * scale));
        AssertEndpoints(stroke, target.Center, target.Start, target.Sweep, 100 * scale, scale);
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
    public void CurveInteriorOwnershipAvoidsSliversAndPreservesSourceInk()
    {
        var f = Make(true, 1, 0, 100, 10);
        var rendered = Rasterizer.Render(f.Doc);
        int lost = 0, source = 0;
        for (int y = 0; y < f.Source.Height; y++)
            for (int x = 0; x < f.Source.Width; x++)
                if (f.Source.GetPixel(x, y).R < 128)
                {
                    source++;
                    if (rendered.GetPixel(x, y).R >= 128) lost++;
                }
        Assert.InRange(lost / (double)source, 0, .025);
        // A clean ribbon must not leave one filled region per raster rim pixel.
        Assert.InRange(f.Doc.Regions.Count, 1, 20);
        Assert.DoesNotMatch(@"M ([\d.]+) ([\d.]+) L \1 \2 Z", SvgWriter.Write(f.Doc));
    }

    [Theory]
    [InlineData("backward-foot", 0, 0)]
    [InlineData("backward-foot", 23, .35)]
    [InlineData("backward-foot", 78, .35)]
    [InlineData("near-crossing", 0, 0)]
    [InlineData("near-crossing", 23, .35)]
    [InlineData("near-crossing", 45, 0)]
    [InlineData("near-crossing", 78, .35)]
    public void NearTangentSideDoesNotTruncateAContinuingSourceArc(string kind, double rotation, double phase)
    {
        // The source draws one r100 arc from -90 to +45 deg; a straight side grazes
        // it near 0 deg and paints over the circular band. The arc must continue
        // through that merged band to its real source end, as one analytic arc.
        var fixture = Make(true, 1, rotation, 100, 10, kind, phase);
        var center = fixture.Targets[0].Center;
        double start = -Math.PI / 2 + rotation * Math.PI / 180, sweep = 135 * Math.PI / 180;
        var stroke = Assert.Single(fixture.Doc.Strokes, s => Covers(s, center, start, sweep, 100));
        Assert.All(stroke.Segments, g => Assert.False(g.IsLine(.05)));
        double signed = AssertEndpoints(stroke, center, start, sweep, 100, 1);
        AssertAnalyticExport(fixture.Doc, stroke, center, 100, signed);
        // At least 30 deg beyond the straight side's tangent foot.
        double foot = (kind == "near-crossing" ? -Math.Atan(.1) : 0) + rotation * Math.PI / 180;
        double reach = Math.Max(Angle(stroke.Segments[0].P0), Angle(stroke.Segments[^1].P3));
        Assert.InRange(reach * 180 / Math.PI, 30, 180);
        double Angle(Vec2 p) => Math.Atan2(Math.Sin(Math.Atan2(p.Y - center.Y, p.X - center.X) - foot),
            Math.Cos(Math.Atan2(p.Y - center.Y, p.X - center.X) - foot));
        var rendered = Rasterizer.Render(fixture.Doc);
        int source = 0, lost = 0;
        for (int y = 0; y < fixture.Source.Height; y++)
            for (int x = 0; x < fixture.Source.Width; x++)
                if (fixture.Source.GetPixel(x, y).R < 128)
                {
                    source++;
                    if (rendered.GetPixel(x, y).R >= 128) lost++;
                }
        // Rotated straight sides still lose some ink they did on dev (measured
        // 3.5%/4.2% before this change at 23/45 deg); the arc itself must not add loss.
        Assert.InRange(lost / (double)source, 0, rotation % 78 == 0 ? .025 : .045);
        Assert.True(rendered.GetPixel((int)fixture.Solid.X, (int)fixture.Solid.Y).R < 128);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(23, .35)]
    [InlineData(78, .35)]
    public void ArcContinuingFarPastANearTangentSideIsOneUnduplicatedPrimitive(double rotation, double phase)
    {
        // One continuous source circle from -90 to +90 deg, rasterized in one pass,
        // with a straight side grazing it near 0 deg. The far ridge beyond the merged
        // band belongs to the same arc: one analytic arc, no overlapping second curve.
        var fixture = Make(true, 1, rotation, 100, 10, "extended", phase);
        var center = fixture.Targets[0].Center;
        double start = -Math.PI / 2 + rotation * Math.PI / 180, sweep = Math.PI;
        var stroke = Assert.Single(fixture.Doc.Strokes, s => Covers(s, center, start, sweep, 100));
        Assert.All(stroke.Segments, g => Assert.False(g.IsLine(.05)));
        double signed = AssertEndpoints(stroke, center, start, sweep, 100, 1);
        AssertAnalyticExport(fixture.Doc, stroke, center, 100, signed);
        // No other curved stroke may retrace any 10 deg stretch of the same circle.
        // (At 0 deg a short straight stub at the -90 deg tangent junction is not a curve.)
        foreach (var other in fixture.Doc.Strokes.Where(s => s != stroke && !s.Segments.All(g => g.IsLine(.5))))
        {
            var samples = other.Segments.SelectMany(g => Enumerable.Range(0, 21).Select(i => g.Eval(i / 20.0)))
                .Where(p => Math.Abs((p - center).Length - 100) <= 3).ToList();
            var angles = samples.Select(p => Math.Atan2(p.Y - center.Y, p.X - center.X)).Order().ToList();
            Assert.True(angles.Count < 2 || (angles[^1] - angles[0]) * 180 / Math.PI < 10,
                $"duplicate curve on the arc circle: {string.Join(",", angles.Select(a => Math.Round(a * 180 / Math.PI)))}");
        }
        var rendered = Rasterizer.Render(fixture.Doc);
        int source = 0, lost = 0;
        for (int y = 0; y < fixture.Source.Height; y++)
            for (int x = 0; x < fixture.Source.Width; x++)
                if (fixture.Source.GetPixel(x, y).R < 128)
                {
                    source++;
                    if (rendered.GetPixel(x, y).R >= 128) lost++;
                }
        Assert.InRange(lost / (double)source, 0, rotation % 78 == 0 ? .025 : .045);
        Assert.True(rendered.GetPixel((int)fixture.Solid.X, (int)fixture.Solid.Y).R < 128);
    }

    [Theory]
    [InlineData("extended", 0, 0)]
    [InlineData("extended", 23, .35)]
    [InlineData("extended", 45, 0)]
    [InlineData("extended", 78, .35)]
    [InlineData("extended", 200, .1)]
    [InlineData("near-crossing", 0, 0)]
    [InlineData("near-crossing", 23, .35)]
    [InlineData("near-crossing", 78, .35)]
    [InlineData("ext-outer", 0, 0)]
    [InlineData("ext-outer", 78, .35)]
    public void StraightSideOfAContinuedArcIsOneTwoAnchorStroke(string kind, double rotation, double phase)
    {
        // The same one-pass source: the arc must stay whole, and the long side that
        // overlaps it must be one weighted line, not a lower piece or remaining fill.
        var f = Make(true, 1, rotation, 100, 10, kind, phase);
        var center = f.Targets[0].Center;
        double start = -Math.PI / 2 + rotation * Math.PI / 180, sweep = (kind == "near-crossing" ? 135 : 180) * Math.PI / 180;
        var arc = Assert.Single(f.Doc.Strokes, s => Covers(s, center, start, sweep, 100));
        AssertAnalyticExport(f.Doc, arc, center, 100, AssertEndpoints(arc, center, start, sweep, 100, 1));
        var near = SidePoint(rotation, phase, 300, 0);
        var remote = SidePoint(rotation, phase, 500, 0);
        var line = Assert.Single(SideLines(f.Doc, near, remote));
        Assert.False(line.Closed);
        var segment = Assert.Single(line.Segments);
        Assert.True(segment.IsLine(.02));
        Assert.InRange(line.Width, 8.5, 11.5);
        var ends = (segment.P3 - remote).Length < (segment.P0 - remote).Length
            ? (Near: segment.P0, Remote: segment.P3) : (Near: segment.P3, Remote: segment.P0);
        Assert.InRange((ends.Remote - remote).Length, 0, 3);
        double turn = Math.Atan2(Cross(ends.Remote - ends.Near, remote - near), (ends.Remote - ends.Near).Dot(remote - near));
        Assert.InRange(Math.Abs(turn) * 180 / Math.PI, 0, 1);
        // No gap: the end sits on the accepted arc's band at the geometric join.
        Assert.InRange(Math.Abs((ends.Near - center).Length - 100), 0, (arc.Width + line.Width) / 2);
        Assert.InRange((ends.Near - near).Length, 0, 8);
        // Nothing else retraces the side, and its source ink is painted. The side's own
        // near-tangent junction costs dev up to 4%; the union must not add to that.
        var axis = (remote - near).Normalized();
        Assert.DoesNotContain(f.Doc.Strokes, o => o != line && o != arc && o.Segments.All(g => g.IsLine(.5)) &&
            o.Segments.Any(g => (g.Eval(.5) - near).Dot(axis) >= 0 && (g.Eval(.5) - remote).Dot(axis) <= 0 &&
                Math.Abs(Cross(g.Eval(.5) - near, axis)) <= 5));
        var rendered = Rasterizer.Render(f.Doc);
        int source = 0, lost = 0, invented = 0;
        for (int y = 0; y < f.Source.Height; y++)
            for (int x = 0; x < f.Source.Width; x++)
            {
                bool painted = rendered.GetPixel(x, y).R < 128;
                if (f.Source.GetPixel(x, y).R < 128)
                {
                    source++;
                    if (!painted) lost++;
                }
                else if (painted && !Nearby(f.Source, x, y, 3)) invented++;
            }
        Assert.InRange(lost / (double)source, 0, .03);
        // Only the round cap over the source's butt end may pass the ink by >3px.
        Assert.InRange(invented, 0, 40);
        Assert.True(rendered.GetPixel((int)f.Solid.X, (int)f.Solid.Y).R < 128);
    }

    [Theory]
    [InlineData("ext-gap", 0, 0)]
    [InlineData("ext-gap", 78, .35)]
    [InlineData("ext-parallel", 0, 0)]
    [InlineData("ext-parallel", 23, .35)]
    [InlineData("ext-cross", 0, 0)]
    [InlineData("ext-cross", 78, .35)]
    [InlineData("ext-wall", 0, 0)]
    [InlineData("ext-wall", 23, .35)]
    [InlineData("ext-radius", 0, 0)]
    [InlineData("ext-radius", 78, .35)]
    [InlineData("ext-glyphs", 0, 0)]
    [InlineData("ext-glyphs", 78, .35)]
    [InlineData("ext-far-gap", 0, 0)]
    [InlineData("ext-far-gap", 78, .35)]
    [InlineData("ext-near-gap", 0, 0)]
    [InlineData("ext-near-gap", 78, .35)]
    [InlineData("ext-far-cross", 0, 0)]
    [InlineData("ext-far-cross", 78, .35)]
    [InlineData("ext-far-wall", 0, 0)]
    [InlineData("ext-far-wall", 78, .35)]
    [InlineData("ext-step", 0, 0)]
    [InlineData("ext-step", 78, .35)]
    public void UnsupportedSideEvidenceInventsNoLineAndKeepsItsInk(string kind, double rotation, double phase)
    {
        var f = Make(true, 1, rotation, 100, 10, kind, phase);
        var rendered = Rasterizer.Render(f.Doc);
        bool Painted(Vec2 p) => rendered.GetPixel((int)p.X, (int)p.Y).R < 128;
        Assert.True(Painted(f.Solid));
        var near = SidePoint(rotation, phase, 300, 0);
        var remote = SidePoint(rotation, phase, 500, 0);
        var lines = SideLines(f.Doc, near, remote);
        double Along(Vec2 p) => (p - near).Dot((remote - near).Normalized());
        double Y(double along) => 300 + along / Math.Sqrt(1.01);
        bool Spans(StrokePath l, double y) => l.Segments.Any(g =>
            Math.Min(Y(Along(g.P0)), Y(Along(g.P3))) < y && Math.Max(Y(Along(g.P0)), Y(Along(g.P3))) > y);
        void AssertWholeArc()
        {
            double a0 = -Math.PI / 2 + rotation * Math.PI / 180;
            Assert.Single(f.Doc.Strokes, s => Covers(s, f.Targets[0].Center, a0, Math.PI, 100));
        }
        switch (kind)
        {
            case "ext-gap":
                // Nothing crosses the paper before the join; the merged stub stays painted.
                Assert.False(Painted(SidePoint(rotation, phase, 364, 0)));
                Assert.True(Painted(SidePoint(rotation, phase, 340, 3)));
                Assert.All(lines, l => Assert.All(l.Segments, g => Assert.InRange(Y(Math.Min(Along(g.P0), Along(g.P3))), 364, 520)));
                break;
            case "ext-parallel":
                // The paper between the two sides survives, and each stays in its own band.
                Assert.False(Painted(SidePoint(rotation, phase, 450, -7)));
                Assert.True(Painted(SidePoint(rotation, phase, 450, -14)));
                Assert.True(Painted(SidePoint(rotation, phase, 450, 0)));
                break;
            case "ext-cross":
            {
                // A transverse crossing in the merged band keeps its arm as ink, and the
                // side does not claim the union through it.
                var q = (near + remote) * .5;
                var c = f.Targets[0].Center;
                double a = 15 * Math.PI / 180 + rotation * Math.PI / 180;
                Assert.True(Painted(c + new Vec2(Math.Cos(a), Math.Sin(a)) * 122));
                Assert.All(lines, l => Assert.All(l.Segments, g =>
                    Assert.InRange(Y(Math.Min(Along(g.P0), Along(g.P3))), 330, 520)));
                Assert.True(q.X > 0);
                break;
            }
            case "ext-wall":
                Assert.True(Painted(SidePoint(rotation, phase, 361, 26)));
                Assert.True(Painted(SidePoint(rotation, phase, 361, 0)));
                Assert.All(lines, l => Assert.All(l.Segments, g =>
                    Assert.InRange(Y(Math.Min(Along(g.P0), Along(g.P3))), 365, 520)));
                break;
            case "ext-radius":
                // A larger-radius continuation is no straight side, however shallow it leaves.
                Assert.DoesNotContain(f.Doc.Strokes, s => s.Segments.Count == 1 && s.Segments[0].IsLine(.02) &&
                    OnRadius300(s.Segments[0].P0, rotation, phase) && OnRadius300(s.Segments[0].P3, rotation, phase) &&
                    (s.Segments[0].P3 - s.Segments[0].P0).Length >= 100);
                Assert.True(Painted(RadiusPoint(rotation, phase, 120)));
                break;
            case "ext-glyphs":
                // A compact row touching the side stays filled source; the side stops at it.
                for (int i = 0; i < 4; i++)
                    Assert.True(Painted(SidePoint(rotation, phase, 434 + 12 * i, 11)));
                Assert.DoesNotContain(f.Doc.Strokes, s => s.Segments.Any(g => Enumerable.Range(0, 41).Any(i =>
                    (g.Eval(i / 40.0) - SidePoint(rotation, phase, 446, 11)).Length < 2)));
                break;
            // Below, the arc itself survives whole, so the side hypothesis really runs.
            case "ext-far-gap":
                AssertWholeArc();
                Assert.False(Painted(SidePoint(rotation, phase, 414, 0)));
                Assert.All(lines, l => Assert.False(Spans(l, 414)));
                Assert.True(Painted(SidePoint(rotation, phase, 380, 0)));
                break;
            case "ext-near-gap":
                Assert.False(Painted(SidePoint(rotation, phase, 346, 4)));
                Assert.All(lines, l => Assert.False(Spans(l, 346)));
                break;
            case "ext-far-cross":
                AssertWholeArc();
                Assert.True(Painted(SidePoint(rotation, phase, 420, 34)));
                Assert.True(Painted(SidePoint(rotation, phase, 420, -34)));
                Assert.True(Painted(SidePoint(rotation, phase, 380, 0)));
                break;
            case "ext-step":
            {
                // A weight change is no licence to retrace: straight pieces never overlap.
                AssertWholeArc();
                Assert.True(Painted(SidePoint(rotation, phase, 470, 7)));
                var spans = lines.SelectMany(l => l.Segments).Select(g =>
                    (Lo: Math.Min(Along(g.P0), Along(g.P3)), Hi: Math.Max(Along(g.P0), Along(g.P3)))).OrderBy(x => x.Lo).ToList();
                for (int i = 1; i < spans.Count; i++)
                    Assert.True(spans[i].Lo >= spans[i - 1].Hi - 12, $"retraced {spans[i - 1]} {spans[i]}");
                break;
            }
            case "ext-far-wall":
                AssertWholeArc();
                Assert.True(Painted(SidePoint(rotation, phase, 426, 26)));
                Assert.True(Painted(SidePoint(rotation, phase, 380, 0)));
                break;
        }
    }

    static Vec2 RotateFrame(double degrees, double phase, Vec2 p)
    {
        double r = degrees * Math.PI / 180;
        return new Vec2(phase, -phase * .7) + new Vec2(320, 320) +
            new Vec2((p.X - 320) * Math.Cos(r) - (p.Y - 320) * Math.Sin(r),
                (p.X - 320) * Math.Sin(r) + (p.Y - 320) * Math.Cos(r));
    }

    // A point on the source side x = 420 + .1(y - 320), `offset` along its normal.
    static Vec2 SidePoint(double degrees, double phase, double y, double offset)
    {
        double d = Math.Sqrt(1.01);
        return RotateFrame(degrees, phase, new Vec2(420 + .1 * (y - 320) + offset / d, y - .1 * offset / d));
    }

    // The r300 circle tangent to the r100 arc at (420, 320).
    static Vec2 RadiusPoint(double degrees, double phase, double y) =>
        RotateFrame(degrees, phase, new Vec2(120 + Math.Sqrt(90000 - (y - 320) * (y - 320)), y));

    static bool OnRadius300(Vec2 p, double degrees, double phase) =>
        Math.Abs((p - RotateFrame(degrees, phase, new Vec2(120, 320))).Length - 300) <= 6;

    static List<StrokePath> SideLines(VectorDocument doc, Vec2 near, Vec2 remote)
    {
        var d = (remote - near).Normalized();
        bool On(Vec2 p) => Math.Abs(Cross(p - near, d)) <= 6 && (p - near).Dot(d) >= -12 && (p - remote).Dot(d) <= 12;
        return doc.Strokes.Where(s => s.Segments.All(g => g.IsLine(.05)) && s.Segments.All(g => On(g.P0) && On(g.P3)) &&
            (s.Segments[0].P0 - s.Segments[^1].P3).Length >= 40).ToList();
    }

    static bool Nearby(RasterImage image, int x, int y, int reach)
    {
        for (int yy = Math.Max(0, y - reach); yy <= Math.Min(image.Height - 1, y + reach); yy++)
            for (int xx = Math.Max(0, x - reach); xx <= Math.Min(image.Width - 1, x + reach); xx++)
                if (image.GetPixel(xx, yy).R < 128) return true;
        return false;
    }

    [Theory]
    [InlineData("nc-gap", 0, 0)]
    [InlineData("nc-gap", 23, .35)]
    [InlineData("nc-cross", 0, 0)]
    [InlineData("nc-cross", 78, .35)]
    [InlineData("nc-parallel", 0, 0)]
    [InlineData("nc-parallel", 23, .35)]
    [InlineData("nc-wall", 0, 0)]
    [InlineData("nc-wall", 78, .35)]
    [InlineData("nc-radius", 0, 0)]
    [InlineData("nc-radius", 23, .35)]
    public void UnsupportedFarSideDoesNotExtendTheArcPastItsFoot(string kind, double rotation, double phase)
    {
        // Same near-tangent junction, but the far side is a gap, a transverse
        // crossing, a parallel side, a filled wall or a different radius. No
        // continuation may be invented; the far source ink stays painted.
        var fixture = Make(true, 1, rotation, 100, 10, kind, phase);
        var center = fixture.Targets[0].Center;
        double r = rotation * Math.PI / 180, foot = -Math.Atan(.1) + r;
        Assert.DoesNotContain(fixture.Doc.Strokes, s => Covers(s, center, foot, 40 * Math.PI / 180, 100));
        var rendered = Rasterizer.Render(fixture.Doc);
        Vec2 At(double degrees, double radius) => center +
            new Vec2(Math.Cos(degrees * Math.PI / 180 + r), Math.Sin(degrees * Math.PI / 180 + r)) * radius;
        var kept = kind switch
        {
            "nc-gap" => At(40, 100),
            "nc-cross" => At(15, 118),
            "nc-parallel" => At(0, 86),
            "nc-wall" => At(27, 100),
            _ => At(35, 107),
        };
        Assert.True(rendered.GetPixel((int)kept.X, (int)kept.Y).R < 128);
        Assert.True(rendered.GetPixel((int)fixture.Solid.X, (int)fixture.Solid.Y).R < 128);
    }

    [Theory]
    [InlineData(78, 0)]
    public void StraightAttachmentCorrectsRidgeOverrunToItsSourceFoot(double rotation, double phase)
    {
        const double radius = 100;
        var f = Make(true, 1, rotation, radius, 10, "tangent-foot", phase);
        var center = f.Targets[0].Center;
        // Before rotation, the source side is x - .1*y = radius*sqrt(1.01).
        // Its perpendicular foot has angle atan(-.1); the circular source band
        // ends at that same angle, rather than at the raster ridge's split.
        double start = -Math.PI / 2 + rotation * Math.PI / 180, sweep = Math.PI / 2 - Math.Atan(.1);
        var stroke = Assert.Single(f.Doc.Strokes, s => Covers(s, center, start, sweep, radius));
        double signed = AssertEndpoints(stroke, center, start, sweep, radius, 1);
        AssertAnalyticExport(f.Doc, stroke, center, radius, signed);
        var rendered = Rasterizer.Render(f.Doc);
        int source = 0, lost = 0;
        for (int y = 0; y < f.Source.Height; y++)
            for (int x = 0; x < f.Source.Width; x++)
                if (f.Source.GetPixel(x, y).R < 128)
                {
                    source++;
                    if (rendered.GetPixel(x, y).R >= 128) lost++;
                }
        Assert.InRange(lost / (double)source, 0, .025);
    }


    static void AssertAnalyticExport(VectorDocument doc, StrokePath stroke, Vec2 center, double radius, double signed)
    {
        string dxf = DxfWriter.Write(doc);
        var records = Dxf<List<List<(int Code, string Value)>>>("RecordsIn", dxf, "ENTITIES", "POLYLINE");
        int matches = 0;
        for (int i = 0; i < records.Count; i++)
        {
            if (Dxf<double>("Num", records[i], 70) != 0) continue;
            var vertices = Dxf<List<(double X, double Y, double B)>>("Vertices", dxf, i);
            if (vertices.Count != stroke.Segments.Count + 1) continue;
            if (vertices.Where((v, j) => (new Vec2(v.X, doc.Height - v.Y) -
                (j < stroke.Segments.Count ? stroke.Segments[j].P0 : stroke.Segments[^1].P3)).Length > .002).Any()) continue;
            double sweep = 0;
            Assert.InRange(Math.Abs(Dxf<double>("Num", records[i], 40) - stroke.Width), 0, .002);
            for (int j = 0; j < vertices.Count - 1; j++)
            {
                var a = vertices[j]; var b = vertices[j + 1];
                Assert.True(Math.Abs(a.B) > .001);
                var arc = Dxf<(Vec2 Center, double Radius, double Sweep)>("ArcFromBulge",
                    new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), a.B);
                Assert.InRange((arc.Center - new Vec2(center.X, doc.Height - center.Y)).Length, 0, 2);
                Assert.InRange(Math.Abs(arc.Radius - radius), 0, 2);
                sweep += arc.Sweep;
            }
            Assert.InRange(Math.Abs(sweep + signed), 0, .0001);
            matches++;
        }
        Assert.Equal(1, matches);
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

    static double AssertEndpoints(StrokePath stroke, Vec2 center, double start, double sweep, double radius, double scale)
    {
        double Error(double a) => Math.Abs(Math.Atan2(Math.Sin(a), Math.Cos(a))) * 180 / Math.PI;
        double Angle(Vec2 p) => Math.Atan2(p.Y - center.Y, p.X - center.X);
        var first = stroke.Segments[0].P0;
        var last = stroke.Segments[^1].P3;
        double signed = stroke.Segments.Sum(g =>
        {
            var a = g.P1 - g.P0;
            var b = g.P3 - g.P2;
            return Math.Atan2(a.X * b.Y - a.Y * b.X, a.Dot(b));
        });
        double expectedStart = signed * sweep > 0 ? start : start + sweep;
        double expectedSweep = signed * sweep > 0 ? sweep : -sweep;
        Assert.InRange(Error(Angle(first) - expectedStart), 0, 1.5);
        Assert.InRange(Error(Angle(last) - expectedStart - expectedSweep), 0, 1.5);
        Assert.InRange(Math.Abs(signed - expectedSweep) * 180 / Math.PI, 0, 1.5);
        Vec2 Position(double a) => center + new Vec2(Math.Cos(a), Math.Sin(a)) * radius;
        Assert.InRange((first - Position(expectedStart)).Length, 0, 3 * scale);
        Assert.InRange((last - Position(expectedStart + expectedSweep)).Length, 0, 3 * scale);
        return signed;
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

    static (VectorDocument Doc, List<(Vec2 Center, double Start, double Sweep)> Targets, Vec2 Solid, RasterImage Source)
        Make(bool corner, double scale, double degrees, double radius, double width, string kind = "", double phase = 0)
    {
        int size = (int)(640 * scale);
        double rotation = degrees * Math.PI / 180;
        Vec2 Map(Vec2 p) => new Vec2(phase, -phase * .7) + new Vec2(320, 320) * scale +
            new Vec2((p.X - 320) * Math.Cos(rotation) - (p.Y - 320) * Math.Sin(rotation),
                (p.X - 320) * Math.Sin(rotation) + (p.Y - 320) * Math.Cos(rotation)) * scale;
        var image = new RasterImage(size, size);
        var targets = corner
            ? new List<(Vec2, double, double)> { (Map(new Vec2(320, 320)), -Math.PI / 2 + rotation, Math.PI / 2) }
            : new List<(Vec2, double, double)> { (Map(new Vec2(220, 320)), Math.PI / 2 + rotation, Math.PI),
                (Map(new Vec2(420, 320)), -Math.PI / 2 + rotation, Math.PI) };
        if (kind.StartsWith("open", StringComparison.Ordinal)) targets = [(Map(new Vec2(320, 320)), -Math.PI / 2 + rotation, 90 * Math.PI / 180)];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var v = new Vec2((x + .5 - phase) / scale - 320, (y + .5 + phase * .7) / scale - 320);
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
                    if (kind.StartsWith("nc-", StringComparison.Ordinal))
                    {
                        double a = Math.Atan2(q.Y, q.X), d = q.Length - radius;
                        bool annulus = Math.Abs(d) <= width / 2;
                        bool side = p.Y >= 300 && p.Y <= 500 && Math.Abs(q.X - radius - q.Y * .1) <= width / 2;
                        double lineDistance = Math.Abs(q.X - radius - q.Y * .1) / Math.Sqrt(1.01);
                        double end = kind == "nc-cross" || kind == "nc-parallel" ? -Math.Atan(.1) : 45 * Math.PI / 180;
                        if (kind == "nc-radius") end = 20 * Math.PI / 180;
                        bool arc = a >= -Math.PI / 2 && a <= end && annulus;
                        if (kind == "nc-cross") arc = a >= -Math.PI / 2 && a <= 20 * Math.PI / 180 && annulus;
                        if (kind == "nc-gap" && a >= 28 * Math.PI / 180 && a <= 34 * Math.PI / 180 && lineDistance > 7) arc = false;
                        ink = arc || side ||
                            (p.X >= 150 && p.X <= 320 && Math.Abs(q.Y + radius) <= width / 2);
                        if (kind == "nc-cross")
                        {
                            var n = new Vec2(Math.Cos(15 * Math.PI / 180), Math.Sin(15 * Math.PI / 180));
                            ink |= Math.Abs(Cross(q, n)) <= width / 2 && q.Dot(n) >= radius - 25 && q.Dot(n) <= radius + 25;
                        }
                        if (kind == "nc-parallel")
                            ink |= p.Y >= 300 && p.Y <= 500 && Math.Abs(q.X - radius + 14 - q.Y * .1) <= width / 2;
                        if (kind == "nc-wall")
                            ink |= a >= 18 * Math.PI / 180 && a <= 34 * Math.PI / 180 && q.Length >= radius - 18 && q.Length <= radius + 16;
                        if (kind == "nc-radius")
                            ink |= a >= 20 * Math.PI / 180 && a <= 45 * Math.PI / 180 && Math.Abs(q.Length - radius - 7) <= width / 2;
                    }
                    if (kind is "backward-foot" or "near-crossing" or "tangent-foot" or "extended")
                    {
                        double a = Math.Atan2(q.Y, q.X);
                        double tilt = kind is "near-crossing" or "tangent-foot" or "extended" ? .10 : 0;
                        double arcEnd = kind switch { "tangent-foot" => -Math.Atan(tilt), "extended" => Math.PI / 2, _ => 45 * Math.PI / 180 };
                        ink = (a >= -Math.PI / 2 && a <= arcEnd &&
                            Math.Abs(q.Length - radius) <= width / 2) ||
                            (p.X >= 150 && p.X <= 320 && Math.Abs(q.Y + radius) <= width / 2) ||
                            (p.Y >= 300 && p.Y <= 500 && Math.Abs(q.X - radius * (kind == "tangent-foot" ? Math.Sqrt(1 + tilt * tilt) : 1) - q.Y * tilt) <= width / 2);
                    }
                    if (kind.StartsWith("ext-", StringComparison.Ordinal))
                    {
                        double a = Math.Atan2(q.Y, q.X), d = Math.Sqrt(1.01);
                        double along = q.X - radius - q.Y * .1;
                        bool sideBand = p.Y >= 300 && p.Y <= 500 && Math.Abs(along) <= width / 2;
                        ink = (a >= -Math.PI / 2 && a <= Math.PI / 2 && Math.Abs(q.Length - radius) <= width / 2) ||
                            (p.X >= 150 && p.X <= 320 && Math.Abs(q.Y + radius) <= width / 2);
                        switch (kind)
                        {
                            case "ext-gap":
                                ink |= sideBand && !(p.Y >= 360 && p.Y <= 368);
                                break;
                            case "ext-far-gap":
                                ink |= sideBand && !(p.Y >= 410 && p.Y <= 418);
                                break;
                            case "ext-near-gap":
                                ink |= sideBand && !(p.Y >= 343 && p.Y <= 349);
                                break;
                            case "ext-far-cross":
                                ink |= sideBand || (p.Y >= 415 && p.Y <= 425 && along >= -40 && along <= 40);
                                break;
                            case "ext-far-wall":
                                ink |= sideBand || (p.Y >= 415 && p.Y <= 437 && along >= -width/2 && along <= 34);
                                break;
                            case "ext-step":
                                ink |= (sideBand && p.Y <= 440) || (p.Y > 440 && p.Y <= 500 && Math.Abs(along) <= 9);
                                break;
                            case "ext-outer":
                                ink |= sideBand || (p.Y >= 360 && p.Y <= 500 && Math.Abs(along - 14 * d) <= width / 2);
                                break;
                            case "ext-parallel":
                                ink |= sideBand || (p.Y >= 330 && p.Y <= 500 && Math.Abs(along + 14 * d) <= width / 2);
                                break;
                            case "ext-cross":
                            {
                                var n = new Vec2(Math.Cos(15 * Math.PI / 180), Math.Sin(15 * Math.PI / 180));
                                ink |= sideBand || (Math.Abs(Cross(q, n)) <= width / 2 && q.Dot(n) >= radius - 25 && q.Dot(n) <= radius + 25);
                                break;
                            }
                            case "ext-wall":
                                ink |= sideBand || (p.Y >= 350 && p.Y <= 372 && along >= -width / 2 && along <= 34);
                                break;
                            case "ext-radius":
                                ink |= p.Y >= 300 && p.Y <= 500 && Math.Abs((p - new Vec2(120, 320)).Length - 300) <= width / 2;
                                break;
                            case "ext-glyphs":
                                ink |= sideBand;
                                for (int i = 0; i < 4; i++)
                                    ink |= Math.Abs(p.Y - (434 + 12 * i)) <= 4 && along >= width / 2 && along <= width / 2 + 14;
                                break;
                        }
                    }
                    if (kind.StartsWith("open", StringComparison.Ordinal))
                        ink = (q.X >= 0 && q.Y <= 0 &&
                            Math.Abs(q.Length - radius) <= width / 2) ||
                            (p.X >= 150 && p.X <= 320 && Math.Abs(q.Y + radius) <= width / 2) ||
                            (kind == "open-adjacent" && p.Y >= 340 && p.Y <= 500 && Math.Abs(q.X - radius) <= width / 2);
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
        return (Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document, targets, Map(new Vec2(320, 140)), image);
    }

    static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    static T Dxf<T>(string name, params object[] args) =>
        (T)typeof(DxfWriterTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
}
