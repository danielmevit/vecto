using System.Globalization;
using System.Reflection;
using Vecto.Cli;
using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// DXF export (VEC-54): the hand-written ASCII R12 file is structurally valid (HEADER
/// AC1009 with advisory $INSUNITS, TABLES with a defined CONTINUOUS linetype and one
/// LAYER per used palette colour, balanced sections, EOF), geometry is checked by
/// reconstructing entities from parsed group pairs — two-vertex straight strokes with
/// the Y-flip and global width, signed bulges that rebuild the original circles
/// including angle-seam traversal, CIRCLE for zero-width full circles vs bulged
/// closed polylines for width-bearing rings, region holes as separate closed
/// contours, the documented 0.1 px approximation bound on a genuinely non-circular
/// cubic — plus degenerate-input safety, determinism/de-DE culture, the no-mutation
/// guard on SVG/PDF output, and the CLI's case-insensitive .dxf selection.
/// </summary>
public class DxfWriterTests
{
    static readonly Lazy<string> Dir = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "vecto-dxfwriter-tests");
        Directory.CreateDirectory(dir);
        return dir;
    });

    // ---- parsing helpers -------------------------------------------------

    static List<(int Code, string Value)> ParsePairs(string dxf)
    {
        var lines = dxf.Split('\n');
        if (lines[^1] == "") lines = lines[..^1];
        Assert.Equal(0, lines.Length % 2);   // every group code has its value line
        var pairs = new List<(int, string)>(lines.Length / 2);
        for (int i = 0; i < lines.Length; i += 2)
            pairs.Add((int.Parse(lines[i], CultureInfo.InvariantCulture), lines[i + 1]));
        return pairs;
    }

    /// <summary>All records of one entity/table type inside one section, each with its
    /// group pairs up to the next group-0 record.</summary>
    static List<List<(int Code, string Value)>> RecordsIn(string dxf, string section, string type)
    {
        var pairs = ParsePairs(dxf);
        int start = -1;
        for (int i = 0; i + 1 < pairs.Count; i++)
            if (pairs[i] is (0, "SECTION") && pairs[i + 1] is (2, var s) && s == section)
            {
                start = i;
                break;
            }
        Assert.True(start >= 0, $"no {section} section");
        var result = new List<List<(int, string)>>();
        for (int i = start + 2; i < pairs.Count; i++)
        {
            if (pairs[i] is (0, "ENDSEC")) break;
            if (pairs[i] is not (0, var t) || t != type) continue;
            var record = new List<(int, string)> { pairs[i] };
            int j = i + 1;
            while (j < pairs.Count && pairs[j].Code != 0)
            {
                record.Add(pairs[j]);
                j++;
            }
            result.Add(record);
            i = j - 1;
        }
        return result;
    }

    static double Num(List<(int Code, string Value)> r, int code) =>
        double.Parse(r.First(p => p.Code == code).Value, CultureInfo.InvariantCulture);

    static string Str(List<(int Code, string Value)> r, int code) => r.First(p => p.Code == code).Value;

    static bool Has(List<(int Code, string Value)> r, int code) => r.Any(p => p.Code == code);

    /// <summary>The i-th POLYLINE's vertices as (x, y, bulge), in file order.</summary>
    static List<(double X, double Y, double B)> Vertices(string dxf, int index)
    {
        var pairs = ParsePairs(dxf);
        var polylines = new List<List<(double, double, double)>>();
        List<(double, double, double)>? cur = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i] is (0, "POLYLINE"))
            {
                cur = new List<(double, double, double)>();
                polylines.Add(cur);
            }
            else if (pairs[i] is (0, "SEQEND")) cur = null;
            else if (pairs[i] is (0, "VERTEX") && cur != null)
            {
                double x = 0, y = 0, b = 0;
                for (int j = i + 1; j < pairs.Count && pairs[j].Code != 0; j++)
                {
                    if (pairs[j].Code == 10) x = double.Parse(pairs[j].Value, CultureInfo.InvariantCulture);
                    else if (pairs[j].Code == 20) y = double.Parse(pairs[j].Value, CultureInfo.InvariantCulture);
                    else if (pairs[j].Code == 42) b = double.Parse(pairs[j].Value, CultureInfo.InvariantCulture);
                    i = j;
                }
                cur.Add((x, y, b));
            }
        }
        Assert.True(index < polylines.Count, $"only {polylines.Count} polylines");
        return polylines[index];
    }

    /// <summary>Center, radius and signed sweep of the DXF arc from a to b with the
    /// given bulge (positive bulge = counterclockwise in CAD space).</summary>
    static (Vec2 Center, double Radius, double Sweep) ArcFromBulge(Vec2 a, Vec2 b, double bulge)
    {
        Assert.True(Math.Abs(bulge) > 1e-9, "expected an arc vertex, got a straight one");
        double chord = (b - a).Length;
        double r = chord * (bulge * bulge + 1) / (4 * Math.Abs(bulge));
        var mid = (a + b) * 0.5;
        var dir = (b - a) * (1.0 / chord);
        var left = new Vec2(-dir.Y, dir.X);
        double h = Math.Sqrt(Math.Max(0, r * r - chord * chord / 4));
        var center = bulge > 0 ? mid + left * h : mid - left * h;
        return (center, r, 4 * Math.Atan(bulge));
    }

    static double DistToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSq;
        if (len2 < 1e-12) return (p - a).Length;
        double t = Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Distance from p to the arc/chord segment a→b with the given bulge.</summary>
    static double DistToArc(Vec2 p, Vec2 a, Vec2 b, double bulge)
    {
        if (Math.Abs(bulge) <= 1e-9) return DistToSegment(p, a, b);
        var (c, r, sweep) = ArcFromBulge(a, b, bulge);
        double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
        double ang = Math.Atan2(p.Y - c.Y, p.X - c.X);
        double d = ang - a0;
        if (d > Math.PI) d -= 2 * Math.PI;
        else if (d <= -Math.PI) d += 2 * Math.PI;
        bool onArc = sweep > 0 ? d >= -1e-9 && d <= sweep + 1e-9 : d <= 1e-9 && d >= sweep - 1e-9;
        return onArc ? Math.Abs((p - c).Length - r) : Math.Min((p - a).Length, (p - b).Length);
    }

    // ---- geometry builders ------------------------------------------------

    /// <summary>Exact circular arc as one cubic, the same construction the Bezier fitter
    /// emits (k = 4/3·tan(sweep/4)); angles in image coordinates.</summary>
    static CubicBezier ArcCubic(Vec2 center, double radius, double a0, double a1)
    {
        double sweep = a1 - a0;
        double k = 4.0 / 3.0 * Math.Tan(sweep / 4);
        var p0 = center + new Vec2(Math.Cos(a0), Math.Sin(a0)) * radius;
        var p3 = center + new Vec2(Math.Cos(a1), Math.Sin(a1)) * radius;
        var t0 = new Vec2(-Math.Sin(a0), Math.Cos(a0));
        var t1 = new Vec2(-Math.Sin(a1), Math.Cos(a1));
        return new CubicBezier(p0, p0 + t0 * (k * radius), p3 - t1 * (k * radius), p3);
    }

    static List<CubicBezier> CircleLoop(Vec2 center, double radius, bool ccw)
    {
        var segs = new List<CubicBezier>(4);
        double s = ccw ? 1 : -1;
        for (int i = 0; i < 4; i++)
            segs.Add(ArcCubic(center, radius, s * i * Math.PI / 2, s * (i + 1) * Math.PI / 2));
        return segs;
    }

    /// <summary>Four-cubic ellipse (κ construction) — closed, smooth, deliberately not
    /// a circle.</summary>
    static List<CubicBezier> EllipseLoop(Vec2 c, double a, double b)
    {
        const double k = 0.5522847498307936;
        return
        [
            new CubicBezier(c + new Vec2(a, 0), c + new Vec2(a, k * b), c + new Vec2(k * a, b), c + new Vec2(0, b)),
            new CubicBezier(c + new Vec2(0, b), c + new Vec2(-k * a, b), c + new Vec2(-a, k * b), c + new Vec2(-a, 0)),
            new CubicBezier(c + new Vec2(-a, 0), c + new Vec2(-a, -k * b), c + new Vec2(-k * a, -b), c + new Vec2(0, -b)),
            new CubicBezier(c + new Vec2(0, -b), c + new Vec2(k * a, -b), c + new Vec2(a, -k * b), c + new Vec2(a, 0)),
        ];
    }

    static List<CubicBezier> RectLoop(double x0, double y0, double x1, double y1, bool cw)
    {
        Vec2 a = new(x0, y0), b = new(x1, y0), c = new(x1, y1), d = new(x0, y1);
        var pts = cw ? new[] { a, d, c, b } : new[] { a, b, c, d };
        var segs = new List<CubicBezier>(4);
        for (int i = 0; i < 4; i++)
            segs.Add(CubicBezier.Line(pts[i], pts[(i + 1) % 4]));
        return segs;
    }

    static VectorDocument StrokeDoc(params StrokePath[] strokes) => new()
    {
        Width = 100,
        Height = 80,
        Palette = [new PaletteEntry { Color = new Rgba32(0, 0, 0, 255) }],
        Regions = [],
        Strokes = [.. strokes],
    };

    static StrokePath Stroke(double width, bool closed, params CubicBezier[] segs) => new()
    {
        PaletteIndex = 0,
        Width = width,
        Closed = closed,
        Segments = [.. segs],
    };

    // ---- structure ---------------------------------------------------------

    [Fact]
    public void Structure_HeaderTablesEntitiesEof()
    {
        var dxf = DxfWriter.Write(MixedDoc());
        var pairs = ParsePairs(dxf);
        Assert.Equal(3, pairs.Count(p => p is (0, "SECTION")));
        Assert.Equal(3, pairs.Count(p => p is (0, "ENDSEC")));
        Assert.Equal(("0", "EOF"), (pairs[^1].Code.ToString(), pairs[^1].Value));

        int i = pairs.FindIndex(p => p is (9, "$ACADVER"));
        Assert.True(i >= 0);
        Assert.Equal((1, "AC1009"), (pairs[i + 1].Code, pairs[i + 1].Value));
        i = pairs.FindIndex(p => p is (9, "$INSUNITS"));
        Assert.True(i >= 0);
        Assert.Equal((70, "0"), (pairs[i + 1].Code, pairs[i + 1].Value));   // unitless advisory
        i = pairs.FindIndex(p => p is (9, "$EXTMIN"));
        Assert.Equal((10, "0.0"), (pairs[i + 1].Code, pairs[i + 1].Value));
        i = pairs.FindIndex(p => p is (9, "$EXTMAX"));
        Assert.Equal((10, "100"), (pairs[i + 1].Code, pairs[i + 1].Value));
        Assert.Equal((20, "80"), (pairs[i + 2].Code, pairs[i + 2].Value));

        var ltype = RecordsIn(dxf, "TABLES", "LTYPE");
        var lt = Assert.Single(ltype);
        Assert.Equal("CONTINUOUS", Str(lt, 2));
        var layers = RecordsIn(dxf, "TABLES", "LAYER");
        Assert.Equal(2, layers.Count);                                     // both palette colours used
        Assert.All(layers, l => Assert.Equal("CONTINUOUS", Str(l, 6)));

        Assert.DoesNotContain("SPLINE", dxf, StringComparison.Ordinal);
        Assert.DoesNotContain("LWPOLYLINE", dxf, StringComparison.Ordinal);
        Assert.DoesNotContain("HATCH", dxf, StringComparison.Ordinal);
    }

    // ---- straight strokes ---------------------------------------------------

    [Fact]
    public void StraightStroke_ExactlyTwoVerticesWithWidthOpenFlag()
    {
        var dxf = DxfWriter.Write(StrokeDoc(Stroke(3, closed: false,
            CubicBezier.Line(new Vec2(10, 10), new Vec2(90, 70)))));
        var polys = RecordsIn(dxf, "ENTITIES", "POLYLINE");
        var poly = Assert.Single(polys);
        Assert.Equal("VECTO_0", Str(poly, 8));
        Assert.Equal(1, (int)Num(poly, 66));       // vertices follow
        Assert.Equal(0, (int)Num(poly, 70));       // open: no spurious closing chord
        Assert.Equal(3, Num(poly, 40));            // global width
        Assert.Equal(3, Num(poly, 41));
        var verts = Vertices(dxf, 0);
        Assert.Equal(2, verts.Count);             // a straight run is exactly two anchors
        Assert.Equal(10, verts[0].X, 5);
        Assert.Equal(70, verts[0].Y, 5);          // yCAD = 80 - 10
        Assert.Equal(90, verts[1].X, 5);
        Assert.Equal(10, verts[1].Y, 5);          // 80 - 70
        Assert.All(verts, v => Assert.Equal(0, v.B, 9));
        Assert.Single(RecordsIn(dxf, "ENTITIES", "SEQEND"));
        Assert.Empty(RecordsIn(dxf, "ENTITIES", "CIRCLE"));
    }

    // ---- arcs and bulges ----------------------------------------------------

    [Fact]
    public void ArcBulge_SignsMatchYFlipAndReconstruct()
    {
        var center = new Vec2(50, 40);
        const double radius = 20;

        // CCW in image space (y-down) mirrors to CW in CAD space: negative bulge
        var v = Vertices(DxfWriter.Write(StrokeDoc(Stroke(2, false,
            ArcCubic(center, radius, 0.3, 0.3 + Math.PI / 2)))), 0);
        Assert.Equal(2, v.Count);
        Assert.True(v[0].B < 0, "image-CCW arc must flip to a CAD-CW (negative) bulge");
        var (c, r, sweep) = ArcFromBulge(new Vec2(v[0].X, v[0].Y), new Vec2(v[1].X, v[1].Y), v[0].B);
        Assert.Equal(50, c.X, 2);                  // the cubic arc construction itself sits
        Assert.Equal(40, c.Y, 2);                  // ~3e-3 px off the true circle
        Assert.Equal(radius, r, 2);
        Assert.Equal(-Math.PI / 2, sweep, 3);

        // CW in image space mirrors to CCW in CAD space: positive bulge
        v = Vertices(DxfWriter.Write(StrokeDoc(Stroke(2, false,
            ArcCubic(center, radius, 0.3, 0.3 - Math.PI / 2)))), 0);
        Assert.True(v[0].B > 0, "image-CW arc must flip to a CAD-CCW (positive) bulge");
        (_, r, sweep) = ArcFromBulge(new Vec2(v[0].X, v[0].Y), new Vec2(v[1].X, v[1].Y), v[0].B);
        Assert.Equal(radius, r, 2);
        Assert.Equal(Math.PI / 2, sweep, 3);

        // angle-seam traversal: an arc crossing the 180° CAD ray unwraps, not jumps
        v = Vertices(DxfWriter.Write(StrokeDoc(Stroke(2, false,
            ArcCubic(center, radius, -(Math.PI - 0.3), -(Math.PI + 0.3))))), 0);
        (c, r, sweep) = ArcFromBulge(new Vec2(v[0].X, v[0].Y), new Vec2(v[1].X, v[1].Y), v[0].B);
        Assert.Equal(radius, r, 2);
        Assert.Equal(0.6, sweep, 5);
        double a0 = Math.Atan2(v[0].Y - c.Y, v[0].X - c.X);
        var mid = c + new Vec2(Math.Cos(a0 + sweep / 2), Math.Sin(a0 + sweep / 2)) * r;
        Assert.Equal(30, mid.X, 3);                // CAD angle 180°: x = 50 - 20
        Assert.Equal(40, mid.Y, 3);
    }

    // ---- circles ------------------------------------------------------------

    [Fact]
    public void FullCircle_RegionLoopBecomesCircle_RingKeepsBulgedPolyline()
    {
        var doc = new VectorDocument
        {
            Width = 100,
            Height = 80,
            Palette = [new PaletteEntry { Color = new Rgba32(0, 0, 0, 255) }],
            Regions = [new RegionPath { PaletteIndex = 0, Area = 1257, Loops = [CircleLoop(new Vec2(50, 40), 20, ccw: true)] }],
            Strokes = [Stroke(2.5, closed: true, [.. CircleLoop(new Vec2(50, 40), 20, ccw: true)])],
        };
        var dxf = DxfWriter.Write(doc);

        // the zero-width region contour is an exact CIRCLE
        var circles = RecordsIn(dxf, "ENTITIES", "CIRCLE");
        var circle = Assert.Single(circles);
        Assert.Equal(50, Num(circle, 10), 2);
        Assert.Equal(40, Num(circle, 20), 2);      // 80 - 40
        Assert.Equal(20, Num(circle, 40), 2);
        Assert.False(Has(circle, 41));             // R12 CIRCLE carries no width

        // the width-bearing ring stays a closed bulged polyline
        var polys = RecordsIn(dxf, "ENTITIES", "POLYLINE");
        var ring = Assert.Single(polys);
        Assert.Equal(1, (int)Num(ring, 70));
        Assert.Equal(2.5, Num(ring, 40), 5);
        Assert.Equal(2.5, Num(ring, 41), 5);
        var verts = Vertices(dxf, 0);
        Assert.True(verts.Count >= 3, "a ring must keep at least three vertices");
        Assert.All(verts, v => Assert.Equal(0.414214, Math.Abs(v.B), 3));   // tan(π/8) quarters
    }

    [Fact]
    public void EllipseLoop_IsNotACircle()
    {
        var doc = new VectorDocument
        {
            Width = 100,
            Height = 80,
            Palette = [new PaletteEntry { Color = new Rgba32(0, 0, 0, 255) }],
            Regions = [new RegionPath { PaletteIndex = 0, Area = 1885, Loops = [EllipseLoop(new Vec2(50, 40), 40, 15)] }],
            Strokes = [],
        };
        var dxf = DxfWriter.Write(doc);
        Assert.Empty(RecordsIn(dxf, "ENTITIES", "CIRCLE"));
        Assert.Single(RecordsIn(dxf, "ENTITIES", "POLYLINE"));
    }

    // ---- regions and holes ---------------------------------------------------

    [Fact]
    public void RegionHoles_SeparateClosedContours()
    {
        var doc = new VectorDocument
        {
            Width = 100,
            Height = 80,
            Palette = [new PaletteEntry { Color = new Rgba32(255, 255, 255, 255) }, new PaletteEntry { Color = new Rgba32(0, 0, 0, 255) }],
            Regions =
            [
                new RegionPath
                {
                    PaletteIndex = 1,
                    Area = 3000,
                    Loops = [RectLoop(10, 10, 90, 70, cw: false), RectLoop(30, 30, 50, 50, cw: true)],
                },
            ],
        };
        var dxf = DxfWriter.Write(doc);
        var polys = RecordsIn(dxf, "ENTITIES", "POLYLINE");
        Assert.Equal(2, polys.Count);              // outer loop AND hole, as separate contours
        Assert.All(polys, p => Assert.Equal(1, (int)Num(p, 70)));
        Assert.All(polys, p => Assert.Equal(0, Num(p, 40), 5));
        var outer = Vertices(dxf, 0);
        var hole = Vertices(dxf, 1);
        Assert.Equal(4, outer.Count);              // no duplicate closure vertex
        Assert.Equal(4, hole.Count);
        Assert.Contains(outer, p => Math.Abs(p.X - 10) < 1e-6 && Math.Abs(p.Y - 70) < 1e-6);
        Assert.Contains(outer, p => Math.Abs(p.X - 90) < 1e-6 && Math.Abs(p.Y - 10) < 1e-6);
        Assert.Contains(hole, p => Math.Abs(p.X - 30) < 1e-6 && Math.Abs(p.Y - 50) < 1e-6);
    }

    // ---- layers and ACI ------------------------------------------------------

    [Fact]
    public void Layers_OnePerUsedColor_NearestAci()
    {
        // primary, secondary, gray, grayscale and non-primary wheel coverage
        Assert.Equal(1, DxfWriter.NearestAci(new Rgba32(255, 0, 0, 255)));
        Assert.Equal(2, DxfWriter.NearestAci(new Rgba32(255, 255, 0, 255)));
        Assert.Equal(3, DxfWriter.NearestAci(new Rgba32(0, 255, 0, 255)));
        Assert.Equal(4, DxfWriter.NearestAci(new Rgba32(0, 255, 255, 255)));
        Assert.Equal(5, DxfWriter.NearestAci(new Rgba32(0, 0, 255, 255)));
        Assert.Equal(6, DxfWriter.NearestAci(new Rgba32(255, 0, 255, 255)));
        Assert.Equal(7, DxfWriter.NearestAci(new Rgba32(255, 255, 255, 255)));   // white → 7, not 255
        Assert.Equal(8, DxfWriter.NearestAci(new Rgba32(128, 128, 128, 255)));   // gray → 8, not 252
        Assert.Equal(250, DxfWriter.NearestAci(new Rgba32(0, 0, 0, 255)));       // black → nearest gray
        Assert.Equal(30, DxfWriter.NearestAci(new Rgba32(255, 128, 0, 255)));    // conventional 30° orange
        Assert.Equal(51, DxfWriter.NearestAci(new Rgba32(255, 255, 128, 255))); // half-saturated yellow
        Assert.Equal(14, DxfWriter.NearestAci(new Rgba32(128, 0, 0, 255)));      // red at brightness 127

        var colors = new[]
        {
            new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 255, 255), new Rgba32(255, 255, 255, 255),
            new Rgba32(0, 0, 0, 255), new Rgba32(128, 128, 128, 255), new Rgba32(255, 128, 0, 255),
        };
        var expected = new[] { 1, 4, 7, 250, 8, 30 };
        var doc = new VectorDocument
        {
            Width = 100,
            Height = 80,
            Palette = [.. colors.Select((c, i) => new PaletteEntry { Color = c, PixelCount = 100 })],
            Regions = [.. colors.Select((c, i) => new RegionPath
            {
                PaletteIndex = i,
                Area = 100,
                Loops = [RectLoop(10 + 10 * i, 10, 18 + 10 * i, 18, cw: false)],
            })],
            Strokes = [],
        };
        var dxf = DxfWriter.Write(doc);
        var layers = RecordsIn(dxf, "TABLES", "LAYER");
        Assert.Equal(colors.Length, layers.Count);
        for (int i = 0; i < colors.Length; i++)
        {
            Assert.Equal("VECTO_" + i, Str(layers[i], 2));
            Assert.Equal(expected[i], (int)Num(layers[i], 62));
            Assert.Equal("CONTINUOUS", Str(layers[i], 6));
        }
        // entities are BYLAYER: no explicit colour group anywhere
        foreach (var type in new[] { "POLYLINE", "VERTEX", "SEQEND", "CIRCLE" })
            Assert.All(RecordsIn(dxf, "ENTITIES", type), e => Assert.False(Has(e, 62)));
    }

    // ---- general cubics --------------------------------------------------------

    [Fact]
    public void NoncircularCubic_SubdividesWithinTolerance()
    {
        var seg = new CubicBezier(new Vec2(10, 40), new Vec2(90, -30), new Vec2(20, 110), new Vec2(90, 40));
        var dxf = DxfWriter.Write(StrokeDoc(Stroke(2, closed: false, seg)));
        var verts = Vertices(dxf, 0);
        Assert.True(verts.Count > 2, "an S-curve must subdivide, not collapse to one arc");

        // every point of the true curve stays within the documented 0.1 px bound
        for (int i = 0; i <= 200; i++)
        {
            var q = seg.Eval(i / 200.0);
            var p = new Vec2(q.X, 80 - q.Y);      // CAD space, like the writer
            double best = double.MaxValue;
            for (int k = 0; k + 1 < verts.Count; k++)
                best = Math.Min(best, DistToArc(p,
                    new Vec2(verts[k].X, verts[k].Y),
                    new Vec2(verts[k + 1].X, verts[k + 1].Y), verts[k].B));
            Assert.True(best <= DxfWriter.ApproxTolerance + 1e-6,
                $"curve point {i} deviates {best:0.0000} px from the emitted segments");
        }
    }

    [Fact]
    public void CumulativeCleanup_IsBoundedAgainstEveryOriginalLine()
    {
        var points = Enumerable.Range(0, 201).Select(i => new Vec2(i, .001 * i * i)).ToArray();
        var lines = points.Zip(points.Skip(1), CubicBezier.Line).ToArray();
        AssertEmittedError(lines, false);
        var straight = Enumerable.Range(0, 200).Select(i =>
            CubicBezier.Line(new Vec2(10000 + i, 9000 + 2 * i), new Vec2(10001 + i, 9002 + 2 * i))).ToArray();
        Assert.Equal(2, Vertices(DxfWriter.Write(StrokeDoc(Stroke(1, false, straight))), 0).Count);
    }

    [Fact]
    public void CollinearTraversal_RetainsReversalAndArcBulges()
    {
        var points = new[] { new Vec2(0, 0), new Vec2(80, 0), new Vec2(20, 0), new Vec2(100, 0) };
        var lines = points.Zip(points.Skip(1), CubicBezier.Line).ToArray();
        var v = Vertices(DxfWriter.Write(StrokeDoc(Stroke(1, false, lines))), 0);
        Assert.Equal(points.Length, v.Count);
        for (int i = 0; i < points.Length; i++) Assert.Equal(points[i].X, v[i].X);
        var arc = ArcCubic(new(9000, 8000), 2000, 0, Math.PI / 2);
        var duplicate = CubicBezier.Line(arc.P0, arc.P0);
        v = Vertices(DxfWriter.Write(StrokeDoc(Stroke(1, false, duplicate, arc))), 0);
        Assert.Equal(2, v.Count);
        Assert.Equal(-Math.Tan(Math.PI / 8), v[0].B, 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GeneralGeometry_LargeTranslatedInflectedCuspAndClosed(int kind)
    {
        var origin = new Vec2(9000, 8000);
        var curve = kind switch
        {
            0 => new CubicBezier(new(0, 0), new(4500, .19), new(9000, -.19), new(14000, 0)),
            1 => new CubicBezier(new(0, 0), new(1000, 1800), new(-1000, 1800), new(0, 0)),
            2 => new CubicBezier(new(0, 0), new(1400, 2100), new(-1400, -2100), new(0, 0)),
            3 => new CubicBezier(new(0, 0), new(4000, -3000), new(-3000, 4000), new(1000, 1000)),
            _ => new CubicBezier(new(0, 0), new(2000, 0), new(-1000, 0), new(1000, 0)),
        };
        curve = new(curve.P0 + origin, curve.P1 + origin, curve.P2 + origin, curve.P3 + origin);
        AssertEmittedError([curve], kind == 1 || kind == 2);
    }

    // Checks the serialized output, including cleanup and rounding, in both directions.
    // The reference is a densely sampled ORIGINAL curve, independent of writer predicates.
    static void AssertEmittedError(CubicBezier[] curves, bool closed)
    {
        var dxf = DxfWriter.Write(StrokeDoc(Stroke(1, closed, curves)));
        var vertices = Vertices(dxf, 0);
        var reference = new List<Vec2>();
        foreach (var c in curves)
            for (int i = 0; i <= 8192; i++)
            {
                var q = c.Eval(i / 8192.0);
                reference.Add(new(q.X, 80 - q.Y));
            }
        int edges = vertices.Count - (closed ? 0 : 1);
        var emitted = vertices.Select(v => new Vec2(v.X, v.Y)).ToArray();
        double forward = 0, reverse = 0;
        // Multi-line regression needs only 33 samples per original line.
        int step = curves.Length > 1 ? 256 : 1;
        for (int i = 0; i < reference.Count; i += step)
        {
            double best = double.MaxValue;
            for (int k = 0; k < edges; k++)
                best = Math.Min(best, DistToArc(reference[i], emitted[k], emitted[(k + 1) % emitted.Length], vertices[k].B));
            forward = Math.Max(forward, best);
        }
        for (int k = 0; k < edges; k++)
        {
            Assert.Equal(0, vertices[k].B); // general curves cannot gain circular intent
            for (int i = 0; i <= 8; i++)
            {
                var q = emitted[k] + (emitted[(k + 1) % emitted.Length] - emitted[k]) * (i / 8.0);
                double best = double.MaxValue;
                for (int j = 0; j + 1 < reference.Count; j += step)
                    best = Math.Min(best, DistToSegment(q, reference[j], reference[Math.Min(j + step, reference.Count - 1)]));
                reverse = Math.Max(reverse, best);
            }
        }
        Assert.True(forward <= .1, $"Original to DXF: {forward:R}");
        Assert.True(reverse <= .1, $"DXF to original: {reverse:R}");
        Assert.Equal(curves[0].P0.X, vertices[0].X, 8);
        Assert.Equal(80 - curves[0].P0.Y, vertices[0].Y, 8);
        if (!closed)
        {
            Assert.Equal(curves[^1].P3.X, vertices[^1].X, 8);
            Assert.Equal(80 - curves[^1].P3.Y, vertices[^1].Y, 8);
        }
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(500, false)]
    [InlineData(2000, true)]
    [InlineData(5000, false)]
    public void CircularConstruction_RecoversAtSheetScale(double radius, bool ccw)
    {
        var center = new Vec2(9000, 8000);
        var loop = CircleLoop(center, radius, ccw);
        // Move the seam, preserving traversal.
        loop = [.. loop.Skip(1), loop[0]];
        var dxf = DxfWriter.Write(StrokeDoc(Stroke(0, true, [.. loop]), Stroke(3, true, [.. loop])));
        var circle = Assert.Single(RecordsIn(dxf, "ENTITIES", "CIRCLE"));
        Assert.Equal(center.X, Num(circle, 10), 7);
        Assert.Equal(80 - center.Y, Num(circle, 20), 7);
        Assert.Equal(radius, Num(circle, 40), 7);
        var v = Vertices(dxf, 0);
        Assert.Equal(4, v.Count);
        Assert.Equal(1, Num(Assert.Single(RecordsIn(dxf, "ENTITIES", "POLYLINE")), 70));
        for (int i = 0; i < v.Count; i++)
        {
            var a = new Vec2(v[i].X, v[i].Y);
            var b = new Vec2(v[(i + 1) % v.Count].X, v[(i + 1) % v.Count].Y);
            var (c, r, sweep) = ArcFromBulge(a, b, v[i].B);
            Assert.True((c - new Vec2(center.X, 80 - center.Y)).Length < 1e-7);
            Assert.Equal(radius, r, 7);
            Assert.Equal((ccw ? -1 : 1) * Math.PI / 2, sweep, 10);
            Assert.Equal(loop[i].P0.X, a.X, 7);
            Assert.Equal(80 - loop[i].P0.Y, a.Y, 7);
        }
        var arc = ArcCubic(center, radius, 3.0, ccw ? 3.7 : 2.3);
        var av = Vertices(DxfWriter.Write(StrokeDoc(Stroke(1, false, arc))), 0);
        Assert.Equal(2, av.Count);
        var (_, ar, turn) = ArcFromBulge(new(av[0].X, av[0].Y), new(av[1].X, av[1].Y), av[0].B);
        Assert.Equal(radius, ar, 7);
        Assert.Equal(ccw ? -.7 : .7, turn, 10);
    }

    [Fact]
    public void CircularRecovery_RejectsNearEllipseRoundedRectangleAndPerturbedControls()
    {
        Vec2[] cornerCenters = [new(9900, 7100), new(9900, 8900), new(8100, 8900), new(8100, 7100)];
        var rounded = new List<CubicBezier>();
        var corners = cornerCenters.Select((c, i) => ArcCubic(c, 100, (i - 1) * Math.PI / 2, i * Math.PI / 2)).ToArray();
        for (int i = 0; i < corners.Length; i++)
        {
            rounded.Add(corners[i]);
            rounded.Add(CubicBezier.Line(corners[i].P3, corners[(i + 1) % corners.Length].P0));
        }
        var loops = new List<List<CubicBezier>>
        {
            EllipseLoop(new(9000, 8000), 2000, 1999),
            rounded,
            CircleLoop(new(9000, 8000), 2000, true),
        };
        var c = loops[2][0];
        loops[2][0] = new(c.P0, c.P1 + new Vec2(1, 0), c.P2, c.P3);
        foreach (var loop in loops)
        {
            var dxf = DxfWriter.Write(StrokeDoc(Stroke(0, true, [.. loop])));
            Assert.Empty(RecordsIn(dxf, "ENTITIES", "CIRCLE"));
            Assert.Single(RecordsIn(dxf, "ENTITIES", "POLYLINE"));
        }
    }

    [Theory]
    [InlineData(255, 63, 0, 20)]
    [InlineData(165, 0, 0, 12)]
    [InlineData(127, 63, 63, 15)]
    [InlineData(76, 0, 0, 16)]
    [InlineData(38, 19, 19, 19)]
    [InlineData(255, 191, 127, 31)]
    [InlineData(0, 127, 255, 150)]
    [InlineData(165, 124, 82, 33)]
    [InlineData(82, 145, 165, 143)]
    [InlineData(38, 38, 76, 177)]
    [InlineData(51, 51, 51, 251)]
    [InlineData(102, 102, 102, 252)]
    [InlineData(153, 153, 153, 253)]
    [InlineData(204, 204, 204, 254)]
    public void ConventionalAci_KnownShadesAndHueFamilies(byte r, byte g, byte b, int index) =>
        Assert.Equal(index, DxfWriter.NearestAci(new(r, g, b, 255)));

    // ---- degenerate input --------------------------------------------------------

    [Fact]
    public void DegenerateInputs_SkippedSafely()
    {
        var doc = new VectorDocument
        {
            Width = 100,
            Height = 80,
            Palette = [new PaletteEntry { Color = new Rgba32(0, 0, 0, 255) }],
            Regions =
            [
                new RegionPath { PaletteIndex = 0, Area = 0, Loops = [] },
                new RegionPath { PaletteIndex = 0, Area = 0, Loops = [[]] },
                new RegionPath { PaletteIndex = 0, Area = 400, Loops = [RectLoop(10, 10, 30, 30, cw: false)] },
            ],
            Strokes =
            [
                Stroke(2, false),
                Stroke(2, false, CubicBezier.Line(new Vec2(5, 5), new Vec2(5, 5))),
                Stroke(2, false, CubicBezier.Line(new Vec2(double.NaN, 5), new Vec2(9, 9))),
            ],
        };
        var dxf = DxfWriter.Write(doc);
        Assert.DoesNotContain("NaN", dxf, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", dxf, StringComparison.Ordinal);
        Assert.EndsWith("0\nEOF\n", dxf);
        Assert.Single(RecordsIn(dxf, "ENTITIES", "POLYLINE"));   // only the valid region survives
        Assert.Equal(4, Vertices(dxf, 0).Count);
        Assert.Empty(RecordsIn(dxf, "ENTITIES", "CIRCLE"));

        var empty = DxfWriter.Write(new VectorDocument { Width = 10, Height = 10, Palette = [], Regions = [], Strokes = [] });
        Assert.EndsWith("0\nEOF\n", empty);
        Assert.Empty(RecordsIn(empty, "ENTITIES", "POLYLINE"));
    }

    // ---- determinism, culture, no mutation ---------------------------------------

    [Fact]
    public void Deterministic_AndCultureInvariant()
    {
        var doc = Tracer.Trace(SampleGen.CadScan().Scan, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        var invariant = DxfWriter.Write(doc);
        Assert.Equal(invariant, DxfWriter.Write(doc));

        var path = Path.Combine(Dir.Value, "det.dxf");
        DxfWriter.Write(doc, path);
        Assert.Equal(invariant, File.ReadAllText(path));

        var prevDefault = CultureInfo.DefaultThreadCurrentCulture;
        var prevCurrent = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(invariant, DxfWriter.Write(doc));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = prevDefault;
            CultureInfo.CurrentCulture = prevCurrent;
        }
    }

    [Fact]
    public void Export_DoesNotMutateDocument_OrSvgPdfOutput()
    {
        var doc = Tracer.Trace(SampleGen.CadScan().Scan, new TraceOptions { Style = ImageStyle.LineArt }).Document;
        var svg1 = SvgWriter.Write(doc);
        var pdf1 = PdfWriter.Write(doc);
        var dxf1 = DxfWriter.Write(doc);
        Assert.Equal(svg1, SvgWriter.Write(doc));
        Assert.Equal(pdf1, PdfWriter.Write(doc));
        Assert.Equal(dxf1, DxfWriter.Write(doc));
    }

    // ---- CLI integration ------------------------------------------------------------

    [Fact]
    public void Cli_TraceWritesDxfFile()
    {
        var png = Path.Combine(Dir.Value, "cli.png");
        ImageIo.SavePng(SampleGen.CadScan().Scan, png);
        var outPath = Path.Combine(Dir.Value, "cli-out.DxF");   // mixed case on purpose
        var run = typeof(SampleGen).Assembly.GetType("Vecto.Cli.Program")!
            .GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static)!;
        int rc = (int)run.Invoke(null, new object[] { new[] { "trace", png, "-o", outPath, "--style", "lineart" } })!;
        Assert.Equal(0, rc);
        Assert.True(File.Exists(outPath), "the CLI must select DXF by extension, case-insensitively");
        var dxf = File.ReadAllText(outPath);
        Assert.StartsWith("0\nSECTION\n2\nHEADER\n", dxf);
        Assert.EndsWith("0\nEOF\n", dxf);
        Assert.NotEmpty(RecordsIn(dxf, "ENTITIES", "POLYLINE"));
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>A document with a holed region and a straight stroke — two palette colours.</summary>
    static VectorDocument MixedDoc() => new()
    {
        Width = 100,
        Height = 80,
        Palette = [new PaletteEntry { Color = new Rgba32(255, 255, 255, 255) }, new PaletteEntry { Color = new Rgba32(0, 0, 0, 255) }],
        Regions =
        [
            new RegionPath
            {
                PaletteIndex = 0,
                Area = 3000,
                Loops = [RectLoop(10, 10, 90, 70, cw: false), RectLoop(30, 30, 50, 50, cw: true)],
            },
        ],
        Strokes = [new StrokePath
        {
            PaletteIndex = 1,
            Width = 2,
            Closed = false,
            Segments = [CubicBezier.Line(new Vec2(20, 60), new Vec2(80, 20))],
        }],
    };
}
