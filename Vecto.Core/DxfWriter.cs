using System.Globalization;
using System.Text;

namespace Vecto.Core;

/// <summary>
/// ASCII DXF R12 (AC1009) export — VEC-54, stage 4 of the CAD mode: the traced document
/// as editable CAD linework, hand-assembled from group code/value pairs (BCL only, no
/// library — netDxf cannot even write R12). Region loops (outer boundaries AND holes)
/// each become one closed zero-width POLYLINE contour — R12 has no region fill, so a
/// hole is simply a second closed contour beside its outer loop, and glyph outlines
/// keep their exact fitted geometry. Stroked centerlines become open/closed POLYLINEs
/// carrying the stroke weight in the global width codes 40/41 (one path = one
/// selectable stroke). Circular pieces are recovered as VERTEX bulges (group 42 =
/// tan(sweep/4), signed by the turn direction); an exact full circle whose contour
/// carries no width becomes a CIRCLE entity — R12 CIRCLE has no stroke width, so a
/// width-bearing ring stays a closed bulged polyline. Never SPLINE, LWPOLYLINE or
/// HATCH: R12 predates them, and CAD users re-drawing scans want editable primitives.
/// Units: the document has no physical DPI, so 1 pixel = 1 drawing unit, unitless.
/// $INSUNITS is written as 0 as advisory metadata — the variable postdates R12, and
/// strict R12 readers simply ignore it; nothing claims pixels are mm. Coordinates
/// flip to the CAD convention, yCAD = Height − y, so the canvas origin is the
/// lower-left corner and $EXTMIN/$EXTMAX span the canvas. General (non-circular)
/// cubics use control-hull-bounded subdivision and cleanup checked against all
/// original subdivision vertices, within 0.1 px including serialization. Recognized
/// circular control constructions recover the intended analytic circle (rather than
/// the scale-dependent cubic approximation). Culture-invariant and deterministic.
/// </summary>
public static class DxfWriter
{
    /// <summary>Maximum general-cubic approximation error, including cleanup (px).
    /// Analytic recovery of circular control constructions is a separate operation.</summary>
    public const double ApproxTolerance = 0.1;
    const double PieceTolerance = 0.025;
    const double MergeTolSq = 0.025 * 0.025;
    const int MaxDepth = 20;
    const int MaxVertices = 1_000_000;
    const double Eps = 1e-6;

    public static void Write(VectorDocument doc, string path) =>
        File.WriteAllText(path, Write(doc), Encoding.ASCII);

    public static string Write(VectorDocument doc)
    {
        var sb = new StringBuilder(1 << 16);

        // HEADER — version, advisory units, canvas extents (lower-left origin)
        Section(sb, "HEADER");
        Pair(sb, 9, "$ACADVER"); Pair(sb, 1, "AC1009");
        Pair(sb, 9, "$INSUNITS"); Pair(sb, 70, 0);
        Pair(sb, 9, "$EXTMIN"); Pair(sb, 10, 0.0); Pair(sb, 20, 0.0); Pair(sb, 30, 0.0);
        Pair(sb, 9, "$EXTMAX"); Pair(sb, 10, doc.Width); Pair(sb, 20, doc.Height); Pair(sb, 30, 0.0);
        EndSection(sb);

        // TABLES — the CONTINUOUS linetype every layer references, plus one LAYER per
        // palette colour an entity actually uses (stable: named by palette index)
        var used = new SortedSet<int>();
        foreach (var region in doc.Regions)
            if (region.Loops.Any(l => l.Count > 0)) used.Add(region.PaletteIndex);
        foreach (var stroke in doc.Strokes)
            if (stroke.Segments.Count > 0) used.Add(stroke.PaletteIndex);
        var layers = used.Where(i => (uint)i < (uint)doc.Palette.Count).ToList();
        Section(sb, "TABLES");
        Pair(sb, 0, "TABLE"); Pair(sb, 2, "LTYPE"); Pair(sb, 70, 1);
        Pair(sb, 0, "LTYPE"); Pair(sb, 2, "CONTINUOUS"); Pair(sb, 70, 0);
        Pair(sb, 3, "Solid line"); Pair(sb, 72, 65); Pair(sb, 40, 0.0); Pair(sb, 73, 0);
        Pair(sb, 0, "ENDTAB");
        Pair(sb, 0, "TABLE"); Pair(sb, 2, "LAYER"); Pair(sb, 70, layers.Count);
        foreach (int i in layers)
        {
            Pair(sb, 0, "LAYER"); Pair(sb, 2, Layer(i));
            Pair(sb, 70, 0); Pair(sb, 62, NearestAci(doc.Palette[i].Color)); Pair(sb, 6, "CONTINUOUS");
        }
        Pair(sb, 0, "ENDTAB");
        EndSection(sb);

        // ENTITIES — region contours first (the SVG/PDF paint order), strokes on top
        Section(sb, "ENTITIES");
        foreach (var region in doc.Regions)
            foreach (var loop in region.Loops)
                WriteContour(sb, loop, closed: true, width: 0, doc.Height, Layer(region.PaletteIndex));
        foreach (var stroke in doc.Strokes)
            WriteContour(sb, stroke.Segments, stroke.Closed, stroke.Width, doc.Height, Layer(stroke.PaletteIndex));
        EndSection(sb);
        Pair(sb, 0, "EOF");
        return sb.ToString();

        static string Layer(int paletteIndex) => "VECTO_" + paletteIndex.ToString(CultureInfo.InvariantCulture);
    }

    // ---- contours -------------------------------------------------------

    /// <summary>One contour as CIRCLE (exact full circle, no width) or POLYLINE with
    /// VERTEX bulges and a SEQEND; degenerate contours are skipped.</summary>
    static void WriteContour(StringBuilder sb, List<CubicBezier> segs, bool closed, double width, int height, string layer)
    {
        if (segs.Count == 0 || segs.Any(s => !IsFinite(s.P0) || !IsFinite(s.P1) ||
            !IsFinite(s.P2) || !IsFinite(s.P3))) return;
        width = double.IsFinite(width) && width > 0 ? width : 0;
        if (width == 0 && closed && TryFullCircle(segs, height, out var center, out var radius))
        {
            Pair(sb, 0, "CIRCLE"); Pair(sb, 8, layer);
            Pair(sb, 10, center.X); Pair(sb, 20, center.Y); Pair(sb, 30, 0.0);
            Pair(sb, 40, radius);
            return;
        }
        var verts = BuildVertices(segs, closed, height);
        if (verts == null) return;
        Pair(sb, 0, "POLYLINE"); Pair(sb, 8, layer);
        Pair(sb, 66, 1);                                    // vertices follow
        Pair(sb, 10, 0.0); Pair(sb, 20, 0.0); Pair(sb, 30, 0.0);
        Pair(sb, 70, closed ? 1 : 0);
        Pair(sb, 40, width); Pair(sb, 41, width);            // global start/end width
        foreach (var (p, b) in verts)
        {
            Pair(sb, 0, "VERTEX"); Pair(sb, 8, layer);
            Pair(sb, 10, p.X); Pair(sb, 20, p.Y); Pair(sb, 30, 0.0);
            Pair(sb, 42, b);                                 // bulge of the segment starting here
        }
        Pair(sb, 0, "SEQEND"); Pair(sb, 8, layer);
    }

    /// <summary>Contour segments to (point, bulge) vertices. Vertex i carries the bulge
    /// of the segment from vertex i to vertex i+1; a closed contour never repeats its
    /// first vertex (the last vertex's bulge closes the ring). Null when degenerate.</summary>
    static List<(Vec2 P, double B)>? BuildVertices(List<CubicBezier> segs, bool closed, int height)
    {
        foreach (var s in segs)
            if (!IsFinite(s.P0) || !IsFinite(s.P1) || !IsFinite(s.P2) || !IsFinite(s.P3))
                return null;                                 // never NaN/Infinity in the output
        var verts = new List<(Vec2 P, double B)>(segs.Count + 1);
        foreach (var seg in segs)
            AppendSegment(verts, seg, height, 0);
        if (!closed)
        {
            var end = Flip(segs[^1].P3, height);
            if (verts.Count == 0 || end != verts[^1].P)
                verts.Add((end, 0));
        }
        Deduplicate(verts, closed);
        MergeCollinear(verts, closed);
        if (closed)
        {
            if (verts.Count == 2 && (Math.Abs(verts[0].B) > 1e-9 || Math.Abs(verts[1].B) > 1e-9))
                return verts;                                // two bulged halves: a valid ring
            if (verts.Count < 3) return null;
        }
        else if (verts.Count < 2) return null;
        return verts;
    }

    // A cubic lies in its control hull. Distance to the finite chord is convex,
    // so bounding both interior controls bounds the entire curve, including cusps.
    // Only original pieces may recover circular intent; subdivided general curves
    // must never acquire a new circular interpretation.
    static void AppendSegment(List<(Vec2 P, double B)> verts, CubicBezier seg, int height, int depth)
    {
        if (verts.Count >= MaxVertices)
            throw new InvalidOperationException("DXF geometry exceeds the bounded subdivision budget.");
        if (depth == 0 && TryConstruction(seg, out _, out _, out double sweep))
        {
            verts.Add((Flip(seg.P0, height), -Math.Tan(sweep / 4)));
            return;
        }
        if (seg.IsLine(PieceTolerance))
        {
            verts.Add((Flip(seg.P0, height), 0));
            return;
        }
        if (depth >= MaxDepth)
        {
            // Linear interpolation error <= max|B''|/(8*n*n). B'' is linear,
            // hence its maximum norm is bounded by its two endpoint norms.
            double second = 6 * Math.Max((seg.P2 - seg.P1 * 2 + seg.P0).Length,
                (seg.P3 - seg.P2 * 2 + seg.P1).Length);
            double count = Math.Ceiling(Math.Sqrt(second / (8 * PieceTolerance)));
            if (!double.IsFinite(count) || count > MaxVertices - verts.Count)
                throw new InvalidOperationException("DXF geometry cannot meet tolerance within the subdivision budget.");
            int n = Math.Max(1, (int)count);
            for (int i = 0; i < n; i++) verts.Add((Flip(seg.Eval(i / (double)n), height), 0));
            return;
        }
        var (left, right) = SplitAt(seg, 0.5);
        AppendSegment(verts, left, height, depth + 1);
        AppendSegment(verts, right, height, depth + 1);
    }

    // Recover only the fitter's endpoint/tangent/control construction:
    // controls = endpoints +/- unit tangent * r * 4/3*tan(sweep/4).
    // Work relative to P0 to avoid cancellation on translated drawing sheets.
    static bool TryConstruction(CubicBezier s, out Vec2 center, out double radius, out double sweep)
    {
        center = default; radius = 0; sweep = 0;
        if (!IsFinite(s.P0) || !IsFinite(s.P1) || !IsFinite(s.P2) || !IsFinite(s.P3)) return false;
        var d = s.P3 - s.P0;
        var t = s.P1 - s.P0;
        double length = t.Length;
        if (!(length > Eps) || !(d.Length > Eps)) return false;
        t *= 1 / length;
        var normal = new Vec2(-t.Y, t.X);
        double denom = 2 * normal.Dot(d);
        if (Math.Abs(denom) < 1e-12 * d.Length) return false;
        var relativeCenter = normal * (d.LengthSq / denom);
        radius = relativeCenter.Length;
        if (!(radius > Eps) || !double.IsFinite(radius)) return false;
        var r0 = -relativeCenter;
        var r1 = d - relativeCenter;
        sweep = Math.Atan2(r0.X * r1.Y - r0.Y * r1.X, r0.Dot(r1));
        if (Math.Abs(sweep) < 1e-5 || Math.Abs(sweep) > Math.PI / 2 + 1e-8) return false;
        double k = 4.0 / 3 * Math.Tan(sweep / 4);
        var expected1 = new Vec2(-r0.Y, r0.X) * k;
        var expected2 = d - new Vec2(-r1.Y, r1.X) * k;
        // Strict numerical construction tolerance, capped in absolute drawing units.
        double tol = Math.Min(1e-5, 1e-8 * Math.Max(1, radius));
        if ((s.P1 - s.P0 - expected1).Length > tol ||
            (s.P2 - s.P0 - expected2).Length > tol) return false;
        center = s.P0 + relativeCenter;
        return IsFinite(center);
    }

    static bool TryFullCircle(List<CubicBezier> loop, int height, out Vec2 center, out double radius)
    {
        center = default; radius = 0;
        if (loop.Count < 4 || !TryConstruction(loop[0], out var c, out double r, out double first)) return false;
        double sum = 0;
        double tol = Math.Min(1e-5, 1e-8 * Math.Max(1, r));
        for (int i = 0; i < loop.Count; i++)
        {
            if ((loop[i].P3 - loop[(i + 1) % loop.Count].P0).Length > tol ||
                !TryConstruction(loop[i], out var ci, out double ri, out double turn) ||
                (ci - c).Length > tol || Math.Abs(ri - r) > tol || turn * first <= 0) return false;
            sum += turn;
        }
        if (Math.Abs(Math.Abs(sum) - 2 * Math.PI) > 1e-8) return false;
        center = Flip(c, height); radius = r;
        return true;
    }

    // ---- vertex cleanup --------------------------------------------------

    // Remove only identical straight vertices; never overwrite a surviving arc bulge.
    static void Deduplicate(List<(Vec2 P, double B)> v, bool closed)
    {
        for (int i = v.Count - 2; i >= 0; i--)
            if (v[i].P == v[i + 1].P && v[i].B == 0) v.RemoveAt(i);
        if (closed && v.Count > 1 && v[0].P == v[^1].P && v[^1].B == 0)
            v.RemoveAt(v.Count - 1);
    }

    // Simplify each straight run against its ORIGINAL vertices, never the last
    // locally shortened path. The convex distance bound extends to whole chords.
    // Anchoring closed contours at vertex zero preserves their traversal seam.
    static void MergeCollinear(List<(Vec2 P, double B)> v, bool closed)
    {
        if (v.Count < 3) return;
        var source = v.ToList();
        if (closed) source.Add((source[0].P, 0));
        var keep = new bool[source.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        int start = 0;
        for (int i = 0; i + 1 < source.Count; i++)
            if (source[i].B != 0)
            {
                stack.Push((start, i));
                keep[i] = keep[i + 1] = true;
                start = i + 1;
            }
        stack.Push((start, source.Count - 1));
        while (stack.TryPop(out var run))
        {
            int split = -1; double worst = MergeTolSq;
            for (int i = run.A + 1; i < run.B; i++)
            {
                double error = DistSqToChord(source[i].P, source[run.A].P, source[run.B].P);
                if (error > worst) { worst = error; split = i; }
            }
            if (split < 0) continue;
            keep[split] = true;
            stack.Push((run.A, split)); stack.Push((split, run.B));
        }
        v.Clear();
        for (int i = 0; i < source.Count - (closed ? 1 : 0); i++)
            if (keep[i]) v.Add(source[i]);
    }

    static double DistSqToChord(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSq;
        if (len2 < 1e-12) return p.DistSq(a);
        double t = Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return p.DistSq(a + ab * t);
    }

    static (CubicBezier Left, CubicBezier Right) SplitAt(CubicBezier c, double t)
    {
        var p01 = Lerp(c.P0, c.P1, t);
        var p12 = Lerp(c.P1, c.P2, t);
        var p23 = Lerp(c.P2, c.P3, t);
        var p012 = Lerp(p01, p12, t);
        var p123 = Lerp(p12, p23, t);
        var m = Lerp(p012, p123, t);
        return (new CubicBezier(c.P0, p01, p012, m), new CubicBezier(m, p123, p23, c.P3));
    }

    static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

    /// <summary>Image point to CAD point: y flips, x stays — the canvas origin becomes
    /// the lower-left corner.</summary>
    static Vec2 Flip(Vec2 p, int height) => new(p.X, height - p.Y);

    static bool IsFinite(Vec2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    // ---- ACI colour mapping ----------------------------------------------

    /// <summary>Nearest conventional ACI colour by squared RGB distance over all
    /// indices 1..255; ties use the lower index. ACI 7 is background-dependent.</summary>
    public static int NearestAci(Rgba32 c)
    {
        int best = 7;
        long bestD = long.MaxValue;
        foreach (var (aci, r, g, b) in AciCandidates)
        {
            long dr = c.R - r, dg = c.G - g, db = c.B - b;
            long d = dr * dr + dg * dg + db * db;
            if (d >= bestD) continue;
            bestD = d;
            best = aci;
        }
        return best;
    }

    static readonly (int Aci, byte R, byte G, byte B)[] AciCandidates = BuildAciCandidates();

    static (int Aci, byte R, byte G, byte B)[] BuildAciCandidates()
    {
        var list = new List<(int Aci, byte R, byte G, byte B)>
        {
            // standard colours 1-9: red, yellow, green, cyan, blue, magenta, white, gray, light gray
            (1, 255, 0, 0), (2, 255, 255, 0), (3, 0, 255, 0), (4, 0, 255, 255), (5, 0, 0, 255),
            (6, 255, 0, 255), (7, 255, 255, 255), (8, 128, 128, 128), (9, 192, 192, 192),
            // Conventional six-step gray ramp (ACI 7 remains display-dependent).
            (250, 0, 0, 0), (251, 51, 51, 51), (252, 102, 102, 102),
            (253, 153, 153, 153), (254, 204, 204, 204), (255, 255, 255, 255),
        };
        // 24 hues spaced 15 degrees, five brightness levels, each at full and
        // half saturation. Truncate channels as in the conventional ACI palette.
        double[] values = [255, 255 * .65, 255 * .5, 255 * .3, 255 * .15];
        for (int hue = 0; hue < 24; hue++)
            for (int shade = 0; shade < 10; shade++)
            {
                double value = values[shade / 2];
                double low = shade % 2 == 0 ? 0 : value / 2;
                double rise = low + (value - low) * (hue % 4) / 4;
                double fall = value - (value - low) * (hue % 4) / 4;
                var rgb = (hue / 4) switch
                {
                    0 => (value, rise, low), 1 => (fall, value, low),
                    2 => (low, value, rise), 3 => (low, fall, value),
                    4 => (rise, low, value), _ => (value, low, fall),
                };
                list.Add((10 + 10 * hue + shade, (byte)rgb.Item1, (byte)rgb.Item2, (byte)rgb.Item3));
            }
        return list.OrderBy(t => t.Aci).ToArray();
    }

    // ---- group code plumbing ---------------------------------------------

    static void Section(StringBuilder sb, string name)
    {
        Pair(sb, 0, "SECTION");
        Pair(sb, 2, name);
    }

    static void EndSection(StringBuilder sb) => Pair(sb, 0, "ENDSEC");

    static void Pair(StringBuilder sb, int code, string value) =>
        sb.Append(code).Append('\n').Append(value).Append('\n');

    static void Pair(StringBuilder sb, int code, int value) =>
        Pair(sb, code, value.ToString(CultureInfo.InvariantCulture));

    static void Pair(StringBuilder sb, int code, double value) =>
        Pair(sb, code, value.ToString("0.0##############", CultureInfo.InvariantCulture));
}
