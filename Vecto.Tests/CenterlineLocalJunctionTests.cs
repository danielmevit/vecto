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

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 37)]
    public void PublicTracePreservesScaledOrientedTInk(double scale, double degrees)
    {
        int size = (int)(300 * scale);
        var image = new RasterImage(size, size);
        double angle = degrees * Math.PI / 180;
        var center = new Vec2(size / 2.0, size / 2.0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var q = (new Vec2(x + .5, y + .5) - center) * (1 / scale);
                double a = q.X * Math.Cos(angle) + q.Y * Math.Sin(angle);
                double b = -q.X * Math.Sin(angle) + q.Y * Math.Cos(angle);
                bool ink = (Math.Abs(a) < 95 && Math.Abs(b) < 6) ||
                    (Math.Abs(a) < 3 && b >= 0 && b < 90) ||
                    (a >= 85 && a < 120 && Math.Abs(b) < 24);
                image.SetPixel(x, y, ink ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
            }
        var doc = Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        Assert.Equal(size, doc.Width);
        var rendered = Rasterizer.Render(doc);
        // The crossing and attached solid remain painted in the actual public output.
        Assert.True(rendered.GetPixel((int)center.X, (int)center.Y).R < 128);
        var solid = center + new Vec2(Math.Cos(angle), Math.Sin(angle)) * (100 * scale);
        Assert.True(rendered.GetPixel((int)solid.X, (int)solid.Y).R < 128);
    }

    [Theory]
    [InlineData(143, 140)]
    [InlineData(155, 155)]
    public void AcceptedCircleShortensOnlyAnEndpointInsideItsPaintedBand(double endpoint, double expected)
    {
        const int w = 400;
        var center = new Vec2(200, 200);
        var a = new Vec2(endpoint, 200);
        var b = new Vec2(50, 200);
        var strokes = new List<StrokePath> { new() { Width = 4,
            Segments = [new CubicBezier(a, a + (b - a) * (1.0 / 3), a + (b - a) * (2.0 / 3), b)] } };
        var evidence = new CenterlineCircles.Evidence(1, 1, 54, 0, 0, 1, 1, 1, 0, 1);
        var circle = new CenterlineCircles.Proposal(1, 0, center, 60, 12, evidence);
        var comp = Enumerable.Repeat(-1, w * w).ToArray();
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                var p = new Vec2(x + .5, y + .5);
                if (Math.Abs((p - center).Length - 60) <= 6 ||
                    (p.X >= 50 && p.X <= endpoint && Math.Abs(p.Y - 200) <= 2))
                    comp[y * w + x] = 1;
            }
        typeof(Centerline).GetMethod("RepairLocalJunctions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [strokes, new List<CenterlineCircles.Proposal> { circle }, new HashSet<int> { 1 }, comp, w, w]);
        Assert.InRange(Math.Abs(strokes[0].Segments[0].P0.X - expected), 0, .000001);
        Assert.Equal(center, circle.Center);
        Assert.Equal(60, circle.Radius);
    }

    [Fact]
    public void NearParallelForeignComponentDoesNotSnap() =>
        Assert.Equal(new Vec2(150, 156), Run(1, 0, false, false, true, true));

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 37, true)]
    [InlineData(1, 90, true)]
    [InlineData(1, 0, false)]
    [InlineData(2, 37, false)]
    public void FilledWallFitsRoundCapButOpenLineKeepsEndpoint(double scale, double degrees, bool wall)
    {
        const int size = 500;
        var center = new Vec2(250, 250);
        double angle = degrees * Math.PI / 180;
        var dir = new Vec2(Math.Cos(angle), Math.Sin(angle));
        var normal = new Vec2(-dir.Y, dir.X);
        var a = center;
        var b = center + dir * (90 * scale);
        double width = 8 * scale;
        var strokes = new List<StrokePath> { new() { Width = width,
            Segments = [new CubicBezier(a, a + (b - a) * (1.0 / 3), a + (b - a) * (2.0 / 3), b)] } };
        var comp = Enumerable.Repeat(-1, size * size).ToArray();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var q = new Vec2(x + .5, y + .5) - center;
                double u = q.Dot(dir) / scale, v = q.Dot(normal) / scale;
                bool stem = u >= 0 && u <= 90 && Math.Abs(v) <= 4;
                bool bar = wall && u >= -1 && u <= 12 && Math.Abs(v) <= 40;
                if (stem || bar) comp[y * size + x] = 1;
            }
        var method = typeof(Centerline).GetMethod("RepairLocalJunctions", BindingFlags.Static | BindingFlags.NonPublic)!;
        var circles = Activator.CreateInstance(method.GetParameters()[1].ParameterType)!;
        method.Invoke(null, [strokes, circles, new HashSet<int> { 1 }, comp, size, size]);
        var result = strokes[0].Segments[0];
        if (wall)
        {
            double move = (result.P0 - a).Dot(dir);
            Assert.InRange(move, 0, width / 2);
            // Independent raster geometry oracle: the source envelope is a union
            // of pixel squares, and the outward painted disk must fit inside it.
            var envelope = new HashSet<int>();
            for (int y = 2; y < size - 2; y++)
                for (int x = 2; x < size - 2; x++)
                    if (comp[y * size + x] == 1)
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                                envelope.Add((y + dy) * size + x + dx);
            bool Fits(Vec2 endpoint)
            {
                double reach = width / 2 + .5;
                for (int y = (int)(endpoint.Y - reach) - 1; y <= endpoint.Y + reach + 1; y++)
                    for (int x = (int)(endpoint.X - reach) - 1; x <= endpoint.X + reach + 1; x++)
                    {
                        var delta = new Vec2(x + .5, y + .5) - endpoint;
                        if (delta.Dot(dir) <= 0 && delta.LengthSq < reach * reach &&
                            !envelope.Contains(y * size + x)) return false;
                    }
                return true;
            }
            Assert.True(Fits(result.P0), "The final outward cap needs source support.");
            if (move > .001)
                Assert.False(Fits(result.P0 - dir * .25), "One step less movement must still leave unsupported paint.");
        }
        else Assert.Equal(a, result.P0);
        Assert.Equal(b, result.P3);
        Assert.Equal(width, strokes[0].Width);
        Assert.True(result.IsLine(.000001));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, 45, true)]
    [InlineData(2, 45, true)]
    [InlineData(2, 90, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 45, false)]
    [InlineData(2, 45, false)]
    public void PublicWallAndOpenLinesKeepFiniteAnchors(double scale, double degrees, bool wall)
    {
        int size = (int)(300 * scale);
        var center = new Vec2(size / 2.0, size / 2.0);
        double angle = degrees * Math.PI / 180;
        var dir = new Vec2(Math.Cos(angle), Math.Sin(angle));
        var normal = new Vec2(-dir.Y, dir.X);
        var image = new RasterImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var q = (new Vec2(x + .5, y + .5) - center) * (1 / scale);
                double u = q.Dot(dir), v = q.Dot(normal);
                double axial = Math.Clamp(u, 0, 90);
                bool stem = (u - axial) * (u - axial) + v * v <= 2.25;
                bool bar = wall && u >= -1 && u <= 6 && Math.Abs(v) <= 40;
                image.SetPixel(x, y, stem || bar ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
            }
        var doc = Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        // Assert the actual editable finite object and both anchors, rather than
        // accepting any stroke whose paint happens to cover the source.
        var candidates = doc.Strokes.Where(s => !s.Closed && s.Segments.Count == 1 &&
            s.Segments[0].IsLine(.05) &&
            Math.Abs((s.Segments[0].P3 - s.Segments[0].P0).Normalized().Dot(dir)) > .99);
        var line = candidates.MaxBy(s => (s.Segments[0].P3 - s.Segments[0].P0).Length);
        Assert.NotNull(line);
        var segment = line.Segments[0];
        var a = segment.P0; var b = segment.P3;
        if ((b - a).Dot(dir) < 0) (a, b) = (b, a);
        Assert.InRange(Math.Abs((a - center).Dot(normal)), 0, scale);
        Assert.InRange(Math.Abs((b - center).Dot(normal)), 0, scale);
        Assert.InRange((a - center).Dot(dir), -2 * scale, 6 * scale);
        Assert.InRange((b - center).Dot(dir), 88 * scale, 92 * scale);
        Assert.InRange((b - a).Length, 82 * scale, 94 * scale);
        var rendered = Rasterizer.Render(doc);
        var solid = center + dir * (5 * scale) + normal * (20 * scale);
        if (wall) Assert.True(rendered.GetPixel((int)solid.X, (int)solid.Y).R < 128);
    }

    // VEC143 source scene: two thick rails make the global line weight 16 px, so the local
    // length gate is 128 px, and an attached solid boss sends the component down the local
    // path. Thin stems join the rails and read as a parallel hatch family.
    // A "stepped" stem is 8 px wide at both ends and 9 px between; the width step leaves no
    // ridge maximum, so the stem traces as crumb + main chain + crumb with a short main chain.
    const int HatchTop = 216, HatchRailWidth = 16;

    static RasterImage HatchScene(int length, (int X, bool Stepped)[] stems, Func<int, int, bool>? extraInk = null,
        Func<int, int, bool>? paper = null)
    {
        int bottom = HatchTop + length;
        var image = new RasterImage(1600, bottom + HatchRailWidth + 200);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                bool rail = x >= 100 && x < 1500 &&
                    ((y >= HatchTop - HatchRailWidth && y < HatchTop) || (y >= bottom && y < bottom + HatchRailWidth));
                bool boss = x >= 1400 && x < 1480 && y >= HatchTop - HatchRailWidth - 60 && y < HatchTop - HatchRailWidth;
                bool stem = false;
                foreach (var (sx, stepped) in stems)
                {
                    int width = stepped && (y < HatchTop + 12 || y >= bottom - 12) ? 8 : 9;
                    stem |= x >= sx && x < sx + width && y >= HatchTop && y < bottom;
                }
                bool ink = rail || boss || stem || (extraInk?.Invoke(x, y) ?? false);
                if (paper?.Invoke(x, y) ?? false) ink = false;
                image.SetPixel(x, y, ink ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255));
            }
        return image;
    }

    static VectorDocument TraceLineArt(RasterImage image) =>
        Tracer.Trace(image, new TraceOptions { Style = ImageStyle.LineArt }).Document;

    // Every straight stroke running along a stem's column, wherever it lies vertically.
    static List<StrokePath> StemStrokes(VectorDocument doc, int x) =>
        doc.Strokes.Where(s => s.Segments.Any(g => g.IsLine(.05) &&
            Math.Abs(g.P0.X - g.P3.X) < 2 && Math.Abs(g.P0.X - (x + 4.5)) < 6)).ToList();

    [Fact]
    public void FragmentedHatchMemberBecomesOneEditableLineInItsFamily()
    {
        const int length = 148;
        int[] xs = [300, 371, 442, 513];
        var image = HatchScene(length, [(xs[0], false), (xs[1], false), (xs[2], false), (xs[3], true)]);
        var doc = TraceLineArt(image);
        double topCentre = HatchTop - HatchRailWidth / 2.0, bottomCentre = HatchTop + length + HatchRailWidth / 2.0;
        var directions = new List<double>();
        foreach (int x in xs)
        {
            var stroke = Assert.Single(StemStrokes(doc, x));
            Assert.False(stroke.Closed);
            var line = Assert.Single(stroke.Segments);
            Assert.True(line.IsLine(.02));
            Assert.InRange(stroke.Width, 7.5, 9.5);
            Assert.InRange(line.P0.X, x + 3, x + 6);
            // Each end stays inside its rail's ink, within half a rail of the rail's centre.
            Assert.InRange(Math.Min(line.P0.Y, line.P3.Y), topCentre - HatchRailWidth / 2.0, topCentre + HatchRailWidth / 2.0);
            Assert.InRange(Math.Max(line.P0.Y, line.P3.Y), bottomCentre - HatchRailWidth / 2.0, bottomCentre + HatchRailWidth / 2.0);
            directions.Add(Math.Abs(line.P3.X - line.P0.X));
        }
        Assert.All(directions, d => Assert.InRange(d, 0, 1e-6));

        var dxf = DxfWriter.Write(doc);
        double dxfX = doc.Strokes.Single(s => StemStrokes(doc, xs[3]).Contains(s)).Segments[0].P0.X;
        var vertices = DxfPolylines(dxf).Single(v => v.All(p => Math.Abs(p.X - dxfX) < 1e-2));
        Assert.Equal(2, vertices.Count);
        Assert.All(vertices, v => Assert.Equal(0, v.Bulge));

        // The source ink around the recovered line, its rails and its peers stays painted.
        var rendered = Rasterizer.Render(doc);
        for (int y = HatchTop + 2; y < HatchTop + length - 2; y += 7)
            Assert.True(rendered.GetPixel(xs[3] + 4, y).R < 128);
        Assert.True(rendered.GetPixel(800, HatchTop - 8).R < 128);
        Assert.True(rendered.GetPixel(xs[3] + 35, HatchTop + 70).R > 128);
    }

    [Fact]
    public void CompactClosedGlyphRowAtHatchScaleStaysFilled()
    {
        // Same width and pitch as the hatch, attached to the rail so it is local ink, but
        // the marks are compact closed loops: nothing about them is a long open member.
        int[] xs = [300, 371, 442, 513];
        bool Loop(int x, int y)
        {
            foreach (int gx in xs)
            {
                bool outer = x >= gx && x < gx + 40 && y >= HatchTop - 70 && y < HatchTop - 16;
                bool inner = x >= gx + 9 && x < gx + 31 && y >= HatchTop - 61 && y < HatchTop - 25;
                if (outer && !inner) return true;
            }
            return false;
        }
        var doc = TraceLineArt(HatchScene(148, [(700, false), (771, false), (842, false), (913, true)], Loop));
        var rendered = Rasterizer.Render(doc);
        foreach (int gx in xs)
        {
            Assert.DoesNotContain(doc.Strokes, s => s.Segments.Any(g =>
                Math.Max(g.P0.Y, g.P3.Y) < HatchTop - 18 && g.P0.X >= gx - 2 && g.P0.X <= gx + 42));
            // Stem, top bar and counter of every loop keep their source appearance.
            Assert.True(rendered.GetPixel(gx + 4, HatchTop - 45).R < 128);
            Assert.True(rendered.GetPixel(gx + 20, HatchTop - 66).R < 128);
            Assert.True(rendered.GetPixel(gx + 20, HatchTop - 45).R > 128);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HatchMemberNeverExtendsAcrossCrossingOrSolid(bool solid)
    {
        // Every stem is long enough on its own, except that the last one is cut mid-span:
        // no single uniform span of it reaches the length gate, and it must stay in pieces.
        const int length = 200;
        int mid = HatchTop + length / 2;
        Func<int, int, bool> block = solid
            ? (x, y) => x >= 490 && x < 546 && y >= mid - 28 && y < mid + 28
            : (x, y) => x >= 480 && x < 556 && y >= mid - 4 && y < mid + 5;
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], block));
        foreach (var stroke in StemStrokes(doc, 513))
            foreach (var g in stroke.Segments)
            {
                Assert.True(Math.Max(g.P0.Y, g.P3.Y) <= mid + 2 || Math.Min(g.P0.Y, g.P3.Y) >= mid - 2,
                    "a member must not run through the interruption");
            }
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(517, mid).R < 128);
        if (solid) Assert.True(rendered.GetPixel(495, mid - 20).R < 128);
    }

    [Fact]
    public void HatchMemberDoesNotBridgeSourcePaper()
    {
        const int length = 200;
        int mid = HatchTop + length / 2;
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], null,
            (x, y) => x >= 505 && x < 530 && y >= mid - 4 && y < mid + 4));
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(517, mid).R > 128);
    }

    public static TheoryData<int[]> UnsupportedHatchFamilies => new()
    {
        new[] { 442, 513 },                 // one peer: no family at all
        new[] { 300, 442, 513 },            // three members: below the family size
        new[] { 300, 340, 442, 513 },       // irregular pitch
        new[] { 300, 371, 460, 513 },       // irregular pitch beside the stepped stem
    };

    [Theory]
    [MemberData(nameof(UnsupportedHatchFamilies))]
    public void SteppedStemWithoutRegularFamilyKeepsItsFill(int[] xs)
    {
        // The last stem is stepped and short-chained. Without spacing evidence it stays as it
        // was on accepted dev: filled, not stroked, and still fully painted.
        var stems = xs.Select((x, i) => (x, i == xs.Length - 1)).ToArray();
        var doc = TraceLineArt(HatchScene(148, stems));
        Assert.Empty(StemStrokes(doc, xs[^1]));
        var rendered = Rasterizer.Render(doc);
        for (int y = HatchTop + 2; y < HatchTop + 146; y += 7)
            Assert.True(rendered.GetPixel(xs[^1] + 4, y).R < 128);
    }

    [Fact]
    public void AllSteppedFamilyHasNoIndependentPeerAndStaysFilled()
    {
        // Spacing alone is not enough: at least two members must pass admission themselves.
        int[] xs = [300, 371, 442, 513];
        var doc = TraceLineArt(HatchScene(148, xs.Select(x => (x, true)).ToArray()));
        foreach (int x in xs) Assert.Empty(StemStrokes(doc, x));
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(30, true, true)]
    [InlineData(-70, false, false)]
    [InlineData(70, false, false)]
    [InlineData(200, false, false)]
    public void AsymmetricHatchOverlapIsInvariantToPeerDirection(double along, bool overlaps, bool duplicate)
    {
        // Opposite sides of the PCA +/-90 degree seam, with asymmetric source walks.
        const double epsilon = 1e-8;
        var up = new Vec2(epsilon, Math.Sqrt(1 - epsilon * epsilon));
        var down = new Vec2(epsilon, -up.Y);
        var type = typeof(Centerline).GetNestedType("HatchLine", BindingFlags.NonPublic)!;
        object Line(Vec2 center, Vec2 dir, double lo, double hi) =>
            Activator.CreateInstance(type, [center, dir, lo, hi, 9.0, true,
                center + dir * lo, center + dir * hi])!;
        var seed = Line(new Vec2(0, 0), up, -10, 100);
        var forward = Line(up * along, up, -10, 100);
        var reversed = Line(up * along, down, -100, 10);
        double Overlap(object peer) => (double)type.GetMethod("Overlap")!.Invoke(null, [seed, peer])!;
        bool Same(object peer) => (bool)type.GetMethod("SameLine")!.Invoke(null, [seed, peer])!;
        Assert.Equal(Overlap(forward), Overlap(reversed), 6);
        var reversedSeed = Line(new Vec2(0, 0), down, -100, 10);
        double reversedSeedOverlap = (double)type.GetMethod("Overlap")!.Invoke(null, [reversedSeed, forward])!;
        Assert.Equal(Overlap(forward), reversedSeedOverlap, 6);
        Assert.Equal(overlaps, Overlap(reversed) >= 55);
        Assert.Equal(duplicate, Same(reversed));
        // Lateral separation and nonparallel lines must not become duplicates.
        Assert.False(Same(Line(new Vec2(30, 0), down, -100, 10)));
        Assert.False(Same(Line(new Vec2(0, 0), new Vec2(1, 0), -10, 100)));
    }

    // ---- VEC147: junction-split rail weld ----
    //
    // The hatch scene's bottom rail carries the same junction geometry as the CAD
    // reference's housing bottom: the stems terminate on it from above, the elevated
    // distance transform drops its axis pixels for each stem's width, and the rail
    // traces as facing fragments whose short middles fail the admission gate.

    static List<StrokePath> RailStrokes(VectorDocument doc, double axisY) =>
        doc.Strokes.Where(s => s.Segments.Any(g => g.IsLine(.05) &&
            Math.Abs(g.P0.Y - g.P3.Y) < 2 && Math.Abs(g.P0.Y - axisY) < 3)).ToList();

    // Tip growth legitimately extends a rail stroke a few pixels into an obstacle's ink,
    // so "no stroke covers BOTH sides of the obstacle" is the weld discriminator.
    static void AssertRailNotWeldedAcross(VectorDocument doc, double axisY, double lo, double hi, string because)
    {
        foreach (var stroke in RailStrokes(doc, axisY))
            foreach (var g in stroke.Segments)
                Assert.False(Math.Min(g.P0.X, g.P3.X) < lo && Math.Max(g.P0.X, g.P3.X) > hi, because);
    }

    [Fact]
    public void JunctionSplitRailWeldsIntoOneTwoAnchorLine()
    {
        const int length = 148;
        int[] xs = [300, 371, 442, 513];
        var doc = TraceLineArt(HatchScene(length, [(xs[0], false), (xs[1], false), (xs[2], false), (xs[3], true)]));
        double axisY = HatchTop + length + HatchRailWidth / 2.0;
        var rail = Assert.Single(RailStrokes(doc, axisY));
        var line = Assert.Single(rail.Segments);
        Assert.True(line.IsLine(.02));
        Assert.InRange(Math.Min(line.P0.X, line.P3.X), 90, 115);
        Assert.InRange(Math.Max(line.P0.X, line.P3.X), 1485, 1510);
        Assert.InRange(line.P0.Y, axisY - 2, axisY + 2);
        Assert.InRange(line.P3.Y, axisY - 2, axisY + 2);
        // the same junctions split the top rail, where the stems hang below the axis
        var top = Assert.Single(RailStrokes(doc, HatchTop - HatchRailWidth / 2.0));
        var topLine = Assert.Single(top.Segments);
        Assert.InRange(Math.Min(topLine.P0.X, topLine.P3.X), 90, 115);
        Assert.InRange(Math.Max(topLine.P0.X, topLine.P3.X), 1400, 1430);
        // the stems stay one editable line each, and the solid boss keeps its fill
        foreach (int x in xs) Assert.Single(StemStrokes(doc, x));
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(1440, HatchTop - 40).R < 128);
        // DXF: the welded rail is one straight two-vertex polyline (y flips to CAD)
        double dxfY = doc.Height - axisY;
        var vertices = DxfPolylines(DxfWriter.Write(doc)).Single(v => v.Count == 2 &&
            Math.Abs(v[0].Y - dxfY) < 1 && Math.Abs(v[1].Y - dxfY) < 1);
        Assert.All(vertices, v => Assert.Equal(0, v.Bulge));
    }

    [Fact]
    public void JunctionSplitWeldRequiresALongTerminatingStem()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        // A 40 px stub and a compact glyph loop both end on the bottom rail from above.
        // Neither is a stroke of its own, so neither authorizes a weld across the rail,
        // and the loop is too wide for the gap bound: the rail keeps its source fill.
        bool Stub(int x, int y) => x >= 700 && x < 709 && y >= bottom - 40 && y < bottom;
        bool Loop(int x, int y)
        {
            bool outer = x >= 800 && x < 840 && y >= bottom - 40 && y < bottom;
            bool inner = x >= 808 && x < 832 && y >= bottom - 32 && y < bottom - 8;
            return outer && !inner;
        }
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)],
            (x, y) => Stub(x, y) || Loop(x, y)));
        double axisY = bottom + HatchRailWidth / 2.0;
        AssertRailNotWeldedAcross(doc, axisY, 675, 734, "no rail stroke may span the short stub");
        AssertRailNotWeldedAcross(doc, axisY, 775, 866, "no rail stroke may span the glyph loop");
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(704, bottom - 20).R < 128);
        Assert.True(rendered.GetPixel(804, bottom - 20).R < 128);
        Assert.True(rendered.GetPixel(820, bottom - 20).R > 128);
    }

    [Fact]
    public void JunctionSplitWeldDoesNotCrossAStraddlingSolid()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        bool Solid(int x, int y) => x >= 700 && x < 724 && y >= bottom - 22 && y < bottom + 38;
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], Solid));
        double axisY = bottom + HatchRailWidth / 2.0;
        AssertRailNotWeldedAcross(doc, axisY, 675, 749, "no rail stroke may run through the solid");
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(712, bottom + 20).R < 128);
    }

    [Fact]
    public void JunctionSplitWeldDoesNotBridgeSourcePaper()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], null,
            (x, y) => x >= 700 && x < 726 && y >= bottom && y < bottom + HatchRailWidth));
        double axisY = bottom + HatchRailWidth / 2.0;
        AssertRailNotWeldedAcross(doc, axisY, 675, 751, "no rail stroke may bridge the source paper");
        Assert.True(Rasterizer.Render(doc).GetPixel(712, bottom + 8).R > 128);
    }

    [Fact]
    public void JunctionSplitWeldDoesNotCrossACrossingLine()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        // A thin line crossing the rail continues on BOTH sides of the axis: its own
        // chain is transverse stem evidence, but the chord's cross section is polluted
        // on both halves and the source-ink walk must refuse the weld.
        bool Cross(int x, int y) => x >= 700 && x < 709 && y >= bottom - 100 && y < bottom + HatchRailWidth + 100;
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], Cross));
        double axisY = bottom + HatchRailWidth / 2.0;
        AssertRailNotWeldedAcross(doc, axisY, 675, 734, "no rail stroke may weld across the crossing");
        Assert.False(doc.Strokes.Any(s => s.Segments.Any(g => g.IsLine(.05) &&
            Math.Abs(g.P0.X - g.P3.X) < 2 && Math.Abs(g.P0.X - 704.5) < 3 &&
            Math.Min(g.P0.Y, g.P3.Y) < bottom + HatchRailWidth && Math.Max(g.P0.Y, g.P3.Y) > bottom)),
            "the crossing must keep its filled appearance");
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(704, bottom + 8).R < 128);
        Assert.True(rendered.GetPixel(704, bottom - 50).R < 128);
    }

    [Fact]
    public void JunctionSplitWeldNeedsOneCleanSideOfTheBand()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        // A real stem ends on the rail from above while a stub hangs from it below at
        // the same column: the stem authorizes the junction, but the walk's cross
        // section is polluted on both sides and the weld must still not fire.
        bool StemAndStub(int x, int y) =>
            (x >= 700 && x < 709 && y >= bottom - 100 && y < bottom) ||
            (x >= 700 && x < 709 && y >= bottom + HatchRailWidth && y < bottom + HatchRailWidth + 40);
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], StemAndStub));
        double axisY = bottom + HatchRailWidth / 2.0;
        AssertRailNotWeldedAcross(doc, axisY, 675, 734, "no rail stroke may weld across the polluted band");
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(704, bottom + HatchRailWidth + 20).R < 128);
    }

    [Fact]
    public void JunctionSplitWeldLeavesTangentialArcCurved()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        double axisY = bottom + HatchRailWidth / 2.0;
        var center = new Vec2(700, axisY + 90);
        // The rail ends at a quarter arc's tangency. The tangent staircase leaves facing
        // fragments, but no stem ends there and the arc's own end piece bows: the rail
        // must stay a straight stroke that stops at the tangency.
        bool Arc(int x, int y)
        {
            var p = new Vec2(x + .5, y + .5);
            double d = (p - center).Length;
            return d >= 82 && d <= 98 && p.X <= 700.5 && p.Y >= axisY - 8 && p.Y <= axisY + 90;
        }
        var doc = TraceLineArt(HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], Arc,
            (x, y) => x >= 700 && y >= bottom && y < bottom + HatchRailWidth));
        Assert.False(doc.Strokes.Any(s => s.Segments.Any(g => g.IsLine(.05) &&
            Math.Abs(g.P0.Y - g.P3.Y) < 2 && Math.Abs(g.P0.Y - axisY) < 3 &&
            Math.Min(g.P0.X, g.P3.X) < 706 && Math.Max(g.P0.X, g.P3.X) > 706)),
            "the rail must stay straight and stop at the tangency");
        var rendered = Rasterizer.Render(doc);
        Assert.True(rendered.GetPixel(636, (int)(axisY + 34)).R < 128);
    }

    [Fact]
    public void Vec147TemporaryInventory()
    {
        const int length = 148;
        int bottom = HatchTop + length;
        double axisY = bottom + HatchRailWidth / 2.0;
        var scenes = new List<(string Name, RasterImage Image)>()
        {
            ("plain", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)])),
            ("stub+loop", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)],
                (x, y) => (x >= 700 && x < 709 && y >= bottom - 40 && y < bottom) ||
                    ((x >= 800 && x < 840 && y >= bottom - 40 && y < bottom) && !(x >= 808 && x < 832 && y >= bottom - 32 && y < bottom - 8)))),
            ("solid", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)],
                (x, y) => x >= 700 && x < 724 && y >= bottom - 22 && y < bottom + 38)),
            ("paper", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)], null,
                (x, y) => x >= 700 && x < 726 && y >= bottom && y < bottom + HatchRailWidth)),
            ("crossing", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)],
                (x, y) => x >= 700 && x < 709 && y >= bottom - 100 && y < bottom + HatchRailWidth + 100)),
            ("stem+below", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)],
                (x, y) => (x >= 700 && x < 709 && y >= bottom - 100 && y < bottom) ||
                    (x >= 700 && x < 709 && y >= bottom + HatchRailWidth && y < bottom + HatchRailWidth + 40))),
            ("arc", HatchScene(length, [(300, false), (371, false), (442, false), (513, true)],
                (x, y) => { var p = new Vec2(x + .5, y + .5); double d = (p - new Vec2(700, axisY + 90)).Length;
                    return d >= 82 && d <= 98 && p.X <= 700.5 && p.Y >= axisY - 8 && p.Y <= axisY + 90; },
                (x, y) => x >= 700 && y >= bottom && y < bottom + HatchRailWidth)),
        };
        var sb = new System.Text.StringBuilder();
        foreach (var (name, image) in scenes)
        {
            var doc = TraceLineArt(image);
            sb.AppendLine($"== {name}: {doc.Strokes.Count} strokes");
            foreach (var s in doc.Strokes)
                sb.AppendLine("  " + string.Join("|", s.Segments.Select(g =>
                    $"({g.P0.X:0.#},{g.P0.Y:0.#})-({g.P3.X:0.#},{g.P3.Y:0.#}){(g.IsLine(.05) ? "L" : "C")}")));
        }
        Assert.Fail(sb.ToString());
    }

    static List<List<(double X, double Y, double Bulge)>> DxfPolylines(string dxf)    {
        var lines = dxf.Split('\n').Select(l => l.Trim()).ToArray();
        var result = new List<List<(double X, double Y, double Bulge)>>();
        List<(double X, double Y, double Bulge)>? current = null;
        for (int i = 1; i < lines.Length; i += 2)
        {
            if (lines[i - 1] != "0") continue;
            if (lines[i] == "POLYLINE") result.Add(current = []);
            else if (lines[i] == "VERTEX" && current != null)
            {
                double x = 0, y = 0, bulge = 0;
                for (int j = i + 1; j + 1 < lines.Length && lines[j] != "0"; j += 2)
                {
                    double value = lines[j] is "10" or "20" or "42" ? double.Parse(lines[j + 1], System.Globalization.CultureInfo.InvariantCulture) : 0;
                    if (lines[j] == "10") x = value;
                    if (lines[j] == "20") y = value;
                    if (lines[j] == "42") bulge = value;
                }
                current.Add((x, y, bulge));
            }
        }
        return result;
    }

    static Vec2 Run(double scale, double degrees, bool crossing, bool gap, bool foreign, bool parallel = false)
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
            parallel ? Line(Map(7, -65), Map(7, 65), 4 * scale) :
                Line(Map(-65, 0), Map(65, 0), 16 * scale),
        };
        var comp = Enumerable.Repeat(-1, w * w).ToArray();
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                var q = new Vec2(x + .5, y + .5) - origin;
                double a = (q.X * Math.Cos(angle) + q.Y * Math.Sin(angle)) / scale;
                double b = (-q.X * Math.Sin(angle) + q.Y * Math.Cos(angle)) / scale;
                bool bar = parallel ? Math.Abs(a - 7) <= 2 && Math.Abs(b) <= 65 :
                    Math.Abs(a) <= 65 && Math.Abs(b) <= 8;
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
