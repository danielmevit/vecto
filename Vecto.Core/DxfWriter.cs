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
/// cubics go through deterministic adaptive subdivision: each piece becomes a chord or
/// a recovered circular arc within <see cref="ApproxTolerance"/> of the original curve
/// (piece fit and collinear-vertex merge each get half the budget), recursion is
/// depth-capped and the cap's fallback is the chord. Culture-invariant by
/// construction; the same document always produces byte-identical output.
/// </summary>
public static class DxfWriter
{
    /// <summary>Documented approximation bound (px): every emitted chord or recovered arc
    /// of a general cubic stays within this distance of the original curve.</summary>
    public const double ApproxTolerance = 0.1;

    /// <summary>Chord/arc acceptance tolerance: half the approximation budget (the
    /// collinear-vertex merge spends the other half).</summary>
    const double PieceTolerance = ApproxTolerance / 2;

    /// <summary>Largest sweep (rad) one recovered arc may span; a wider sweep subdivides
    /// (the Bezier fitter emits ≤90° pieces, so real input never hits the cap).</summary>
    const double MaxArcSweep = Math.PI;

    /// <summary>Adaptive subdivision depth cap; at the cap the piece becomes a chord
    /// (safe fallback — the piece is then ≤ segment length / 2^cap).</summary>
    const int MaxDepth = 12;

    /// <summary>Pieces and vertex gaps shorter than this (px) are degenerate noise.</summary>
    const double Eps = 1e-6;
    static readonly double EpsSq = Eps * Eps;

    /// <summary>A vertex this close (px) to its neighbours' chord is dropped, so a
    /// straight run stays one two-vertex path.</summary>
    const double MergeTolerance = ApproxTolerance / 2;
    static readonly double MergeTolSq = MergeTolerance * MergeTolerance;

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
        if (segs.Count == 0) return;
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
            if (verts.Count == 0 || end.DistSq(verts[^1].P) > EpsSq)
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

    /// <summary>One cubic piece as a straight vertex (bulge 0), a recovered arc vertex
    /// (bulge = signed tan(sweep/4)), or two halves — adaptively, deterministically,
    /// with the chord as the depth-cap fallback.</summary>
    static void AppendSegment(List<(Vec2 P, double B)> verts, CubicBezier seg, int height, int depth)
    {
        if (depth >= MaxDepth)
        {
            verts.Add((Flip(seg.P0, height), 0));
            return;
        }
        if (seg.IsLine(PieceTolerance))
        {
            verts.Add((Flip(seg.P0, height), 0));
            return;
        }
        if (TryArcBulge(seg, height, out double bulge))
        {
            verts.Add((Flip(seg.P0, height), bulge));
            return;
        }
        var (left, right) = SplitAt(seg, 0.5);
        AppendSegment(verts, left, height, depth + 1);
        AppendSegment(verts, right, height, depth + 1);
    }

    /// <summary>Circular-arc hypothesis on one cubic: algebraic least-squares circle
    /// (Kåsa) through 17 samples, accepted when every sample sits within
    /// <see cref="PieceTolerance"/> of the circle and the samples walk the circle
    /// monotonically over at most <see cref="MaxArcSweep"/>. The bulge is signed by the
    /// CAD-space sweep direction (positive = counterclockwise), computed on Y-flipped
    /// samples so the image→CAD mirror is already accounted for.</summary>
    static bool TryArcBulge(CubicBezier seg, int height, out double bulge)
    {
        bulge = 0;
        var pts = new Vec2[17];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = Flip(seg.Eval(i / (double)(pts.Length - 1)), height);
        if (!TryCircleFit(pts, out var center, out double radius)) return false;
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs((pts[i] - center).Length - radius) > PieceTolerance) return false;
        if (!MonotoneSweep(pts, center, out double sweep)) return false;
        if (Math.Abs(sweep) > MaxArcSweep) return false;
        if (Math.Abs(sweep) < 1e-4) return true;             // effectively straight: chord
        bulge = Math.Tan(sweep / 4);
        return true;
    }

    /// <summary>Exact-full-circle hypothesis for a closed contour: one Kåsa circle
    /// through all segments' samples, accepted when every sample sits within
    /// <see cref="PieceTolerance"/> of it and the samples walk it monotonically through
    /// one full turn. An arbitrary closed cubic — an ellipse, a blob, a rounded
    /// polygon — fails the residual or the monotone-turn test; a circle is never
    /// guessed from a few sample points alone.</summary>
    static bool TryFullCircle(List<CubicBezier> loop, int height, out Vec2 center, out double radius)
    {
        center = default; radius = 0;
        if (loop.Count == 0) return false;
        var pts = new Vec2[loop.Count * 9];
        int k = 0;
        foreach (var seg in loop)
            for (int i = 0; i <= 8; i++)                     // includes both endpoints, so
                pts[k++] = Flip(seg.Eval(i / 8.0), height);  // the turn closes on itself
        if (!TryCircleFit(pts, out center, out radius)) return false;
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs((pts[i] - center).Length - radius) > PieceTolerance) return false;
        if (!MonotoneSweep(pts, center, out double sweep)) return false;
        return Math.Abs(Math.Abs(sweep) - 2 * Math.PI) <= 0.05;
    }

    /// <summary>Unwrapped angular sweep of consecutive samples around <paramref name="center"/>;
    /// false when the walk is not monotone (not one arc) or a single step exceeds 90°.</summary>
    static bool MonotoneSweep(Vec2[] pts, Vec2 center, out double sweep)
    {
        sweep = 0;
        double prev = Math.Atan2(pts[0].Y - center.Y, pts[0].X - center.X);
        for (int i = 1; i < pts.Length; i++)
        {
            double ang = Math.Atan2(pts[i].Y - center.Y, pts[i].X - center.X);
            double d = ang - prev;
            if (d > Math.PI) d -= 2 * Math.PI;
            else if (d <= -Math.PI) d += 2 * Math.PI;
            if (Math.Abs(d) > Math.PI / 2) return false;
            if (d > 1e-9) { if (sweep < -1e-9) return false; }
            else if (d < -1e-9) { if (sweep > 1e-9) return false; }
            sweep += d;
            prev = ang;
        }
        return true;
    }

    /// <summary>Algebraic least-squares circle (Kåsa): x² + y² = A·x + B·y + C solved by
    /// 3×3 Cramer. False for degenerate inputs; the radius is kept in [0.1, 10⁶] px so a
    /// zero-radius arc can never reach the output.</summary>
    static bool TryCircleFit(Vec2[] pts, out Vec2 center, out double radius)
    {
        center = default; radius = 0;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var p in pts)
        {
            double x = p.X, y = p.Y, z = x * x + y * y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            sxz += x * z; syz += y * z; sz += z;
        }
        int n = pts.Length;
        double m00 = sxx, m01 = sxy, m02 = sx;
        double m10 = sxy, m11 = syy, m12 = sy;
        double m20 = sx, m21 = sy, m22 = n;
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-9) return false;
        double detA = sxz * (m11 * m22 - m12 * m21) - m01 * (syz * m22 - m12 * sz) + m02 * (syz * m21 - m11 * sz);
        double detB = m00 * (syz * m22 - m12 * sz) - sxz * (m10 * m22 - m12 * m20) + m02 * (m10 * sz - syz * m20);
        double detC = m00 * (m11 * sz - syz * m21) - m01 * (m10 * sz - syz * m20) + sxz * (m10 * m21 - m11 * m20);
        double ca = detA / det, cb = detB / det, cc = detC / det;
        var c = new Vec2(ca / 2, cb / 2);
        double r2 = cc + c.LengthSq;
        if (!IsFinite(c) || !(r2 >= 0.01) || !(r2 <= 1e12)) return false;
        center = c;
        radius = Math.Sqrt(r2);
        return true;
    }

    // ---- vertex cleanup --------------------------------------------------

    /// <summary>Removes back-to-back duplicate vertices; a zero-chord bulge is degenerate
    /// noise (our arcs never sweep a full turn), so it dies with its vertex.</summary>
    static void Deduplicate(List<(Vec2 P, double B)> v, bool closed)
    {
        for (int i = 0; i + 1 < v.Count; i++)
            if (v[i].P.DistSq(v[i + 1].P) <= EpsSq)
            {
                v[i] = (v[i].P, 0);
                v.RemoveAt(i + 1);
                i--;
            }
        if (closed)
            while (v.Count > 1 && v[0].P.DistSq(v[^1].P) <= EpsSq)
                v.RemoveAt(v.Count - 1);                     // never a duplicate closure vertex
    }

    /// <summary>Drops a vertex whose two straight neighbours leave it within
    /// <see cref="MergeTolerance"/> of their chord — a straight run split at a seam or a
    /// scan gap stays one two-vertex path. Arc vertices never merge.</summary>
    static void MergeCollinear(List<(Vec2 P, double B)> v, bool closed)
    {
        bool removed = true;
        while (removed && v.Count > (closed ? 3 : 2))
        {
            removed = false;
            int n = v.Count;
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) continue;
                int a = (i - 1 + n) % n, b = (i + 1) % n;
                if (Math.Abs(v[a].B) > 1e-12 || Math.Abs(v[i].B) > 1e-12) continue;
                if (DistSqToChord(v[i].P, v[a].P, v[b].P) > MergeTolSq) continue;
                v.RemoveAt(i);
                removed = true;
                break;
            }
        }
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

    /// <summary>
    /// Nearest standard ACI pen index for a palette colour (R12 layers carry an ACI pen
    /// number, not RGB). Candidates: the nine standard colours 1-9 (primary, secondary,
    /// gray), the grayscale ramp 250-255, and the 24 hue families of the 10-249 colour
    /// wheel at their full-brightness step (ACI 10+10h) and half-brightness step
    /// (ACI 19+10h). Nearest by squared RGB distance, ties to the lower index — so
    /// white maps to 7 (not 255) and mid-gray to 8 (not 252).
    /// </summary>
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
            // grayscale ramp 250-255
            (250, 51, 51, 51), (251, 80, 80, 80), (252, 128, 128, 128),
            (253, 160, 160, 160), (254, 192, 192, 192), (255, 255, 255, 255),
        };
        for (int h = 0; h < 24; h++)
        {
            var (r, g, b) = WheelHue(h);
            list.Add((10 + 10 * h, r, g, b));
            list.Add((19 + 10 * h, (byte)((r + 1) / 2), (byte)((g + 1) / 2), (byte)((b + 1) / 2)));
        }
        return list.OrderBy(t => t.Aci).ToArray();
    }

    /// <summary>The ACI wheel's 24 hue families: 12 saturated hues (families 0-11) and
    /// their pastel counterparts (families 12-23, every channel lifted by 128).</summary>
    static (byte R, byte G, byte B) WheelHue(int h) => h switch
    {
        0 => (255, 0, 0), 1 => (255, 128, 0), 2 => (255, 255, 0), 3 => (128, 255, 0),
        4 => (0, 255, 0), 5 => (0, 255, 128), 6 => (0, 255, 255), 7 => (0, 128, 255),
        8 => (0, 0, 255), 9 => (128, 0, 255), 10 => (255, 0, 255), 11 => (255, 0, 128),
        12 => (255, 128, 128), 13 => (255, 192, 128), 14 => (255, 255, 128), 15 => (192, 255, 128),
        16 => (128, 255, 128), 17 => (128, 255, 192), 18 => (128, 255, 255), 19 => (128, 192, 255),
        20 => (128, 128, 255), 21 => (192, 128, 255), 22 => (255, 128, 255), 23 => (255, 128, 192),
        _ => (0, 0, 0),
    };

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
        Pair(sb, code, value.ToString("0.0#####", CultureInfo.InvariantCulture));
}
