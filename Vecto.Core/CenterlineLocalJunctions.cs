namespace Vecto.Core;

public static partial class Centerline
{
    // Only local source-owned painted junctions participate. Legacy snapping and its
    // global tolerances are unchanged; this pass can only shorten a finite line.
    static void RepairLocalJunctions(List<StrokePath> strokes,
        List<CenterlineCircles.Proposal> circles, HashSet<int> local, int[] comp, int w, int h)
    {
        const double cell = 16;
        var targets = new List<(Vec2 A, Vec2 B, double Width, int Owner, CenterlineCircles.Proposal? Circle)>();
        var grid = new Dictionary<long, HashSet<int>>();
        for (int s = 0; s < strokes.Count; s++)
            if (!strokes[s].Closed)
                foreach (var g in strokes[s].Segments)
                    if (g.IsLine(.05)) Add(g.P0, g.P3, strokes[s].Width, s, null);
        foreach (var c in circles)
        {
            int n = Math.Max(16, (int)Math.Ceiling(2 * Math.PI * c.Radius / 4));
            for (int k = 0; k < n; k++)
            {
                double a = 2 * Math.PI * k / n, b = 2 * Math.PI * (k + 1) / n;
                Add(c.Center + new Vec2(Math.Cos(a), Math.Sin(a)) * c.Radius,
                    c.Center + new Vec2(Math.Cos(b), Math.Sin(b)) * c.Radius, c.Width, -1, c);
            }
        }
        for (int s = 0; s < strokes.Count; s++)
        {
            var stroke = strokes[s];
            if (stroke.Closed || stroke.Segments.Count == 0) continue;
            Fix(true); Fix(false);
            void Fix(bool head)
            {
                int index = head ? 0 : stroke.Segments.Count - 1;
                var g = stroke.Segments[index];
                if (!g.IsLine(.05)) return;
                var p = head ? g.P0 : g.P3;
                var inner = head ? g.P3 : g.P0;
                double length = (inner - p).Length;
                if (length < 4 * stroke.Width) return;
                var dir = (inner - p) * (1 / length);
                int id = Id(p);
                if (!local.Contains(id) || Id(p + dir * stroke.Width) != id) return;
                if (!grid.TryGetValue(CellKey((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell)), out var candidates)) return;
                double best = double.PositiveInfinity;
                Vec2 point = p;
                bool ambiguous = false;
                foreach (int ti in candidates.Order())
                {
                    var t = targets[ti];
                    if (t.Owner == s) continue;
                    var tangent = (t.B - t.A).Normalized();
                    double u;
                    Vec2 normal;
                    if (t.Circle is { } c)
                    {
                        if (c.SourceComponentId != id || Math.Abs((p - c.Center).Length - c.Radius) > t.Width / 2) continue;
                        var delta = p - c.Center;
                        double dot = delta.Dot(dir), disc = dot * dot - delta.LengthSq + c.Radius * c.Radius;
                        if (disc < 0) continue;
                        double root = Math.Sqrt(disc);
                        u = -dot - root;
                        if (u <= .05) u = -dot + root;
                        normal = (p + dir * u - c.Center).Normalized();
                    }
                    else
                    {
                        double cross = dir.X * tangent.Y - dir.Y * tangent.X;
                        if (Math.Abs(cross) < .25) continue;
                        var delta = t.A - p;
                        u = (delta.X * tangent.Y - delta.Y * tangent.X) / cross;
                        var q0 = p + dir * u;
                        double along = (q0 - t.A).Dot(tangent);
                        if (along < 0 || along > (t.B - t.A).Length) continue;
                        normal = new Vec2(-tangent.Y, tangent.X);
                        if (Math.Abs((p - t.A).Dot(normal)) > t.Width / 2) continue;
                    }
                    if (u <= .05 || u > 2 * t.Width || u >= length - 2 * stroke.Width) continue;
                    var q = p + dir * u;
                    if (Id(q) != id || Id(q + normal * (t.Width * .35)) != id ||
                        Id(q - normal * (t.Width * .35)) != id) continue;
                    bool supported = true;
                    for (double d = 0; d <= u; d += .5)
                        if (Id(p + dir * d) != id) { supported = false; break; }
                    if (!supported) continue;
                    if (u < best - .5) { best = u; point = q; ambiguous = false; }
                    else if (Math.Abs(u - best) <= .5 && point.DistSq(q) > .25) ambiguous = true;
                }
                if (!double.IsFinite(best) || ambiguous) return;
                var a = head ? point : g.P0;
                var b = head ? g.P3 : point;
                stroke.Segments[index] = new CubicBezier(a, a + (b - a) * (1.0 / 3), a + (b - a) * (2.0 / 3), b);
            }
        }
        // A filled wall can own a junction even when no finite target primitive
        // was recovered. Require long transverse source support, then fit only the
        // outward half of the round cap to that same component. Released ink is
        // computed afterwards, so source uncovered by the move remains filled.
        foreach (var stroke in strokes)
        {
            if (stroke.Closed || stroke.Segments.Count != 1) continue;
            var segment = stroke.Segments[0];
            if (!segment.IsLine(.05)) continue;
            FitCap(true); FitCap(false);
            void FitCap(bool head)
            {
                var g = stroke.Segments[0];
                var p = head ? g.P0 : g.P3;
                var inner = head ? g.P3 : g.P0;
                double length = (inner - p).Length, radius = stroke.Width / 2;
                if (!double.IsFinite(length) || !double.IsFinite(radius) || radius <= 0 ||
                    length < 8 * stroke.Width) return;
                var dir = (inner - p) * (1 / length);
                var normal = new Vec2(-dir.Y, dir.X);
                int id = Id(p);
                if (!local.Contains(id)) return;
                bool Wall(double sign)
                {
                    for (double t = radius + 1; t <= 2 * stroke.Width; t += .5)
                        if (Id(p + normal * (sign * t)) != id) return false;
                    return true;
                }
                if (!Wall(1) && !Wall(-1)) return;
                for (double t = 0; t <= 2 * stroke.Width; t += .5)
                    if (Id(p + dir * t) != id) return;
                bool Supported(Vec2 q)
                {
                    int reach = (int)Math.Ceiling(radius);
                    for (int y = (int)Math.Floor(q.Y) - reach; y <= (int)Math.Floor(q.Y) + reach; y++)
                        for (int x = (int)Math.Floor(q.X) - reach; x <= (int)Math.Floor(q.X) + reach; x++)
                        {
                            var pixel = new Vec2(x + .5, y + .5);
                            var delta = pixel - q;
                            if (delta.Dot(dir) > 0 || delta.LengthSq >= (radius + .5) * (radius + .5)) continue;
                            // Raster boundary placement and the fitted centerline each carry
                            // pixel uncertainty. Only paint outside their combined two-pixel
                            // envelope is evidence for shortening an editable line.
                            if (Id(pixel) == id) continue;
                            bool nearby = false;
                            for (int dy = -2; dy <= 2 && !nearby; dy++)
                                for (int dx = -2; dx <= 2; dx++)
                                    if (Id(pixel + new Vec2(dx, dy)) == id) { nearby = true; break; }
                            if (!nearby) return false;
                        }
                    return true;
                }
                if (Supported(p)) return;
                for (double move = .25; move <= radius; move += .25)
                {
                    var q = p + dir * move;
                    if (Id(q) != id || !Supported(q)) continue;
                    var a = head ? q : g.P0;
                    var b = head ? g.P3 : q;
                    stroke.Segments[0] = new CubicBezier(a, a + (b - a) * (1.0 / 3), a + (b - a) * (2.0 / 3), b);
                    break;
                }
            }
        }
        int Id(Vec2 p)
        {
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
            return (uint)x < (uint)w && (uint)y < (uint)h ? comp[y * w + x] : -1;
        }
        void Add(Vec2 a, Vec2 b, double width, int owner, CenterlineCircles.Proposal? circle)
        {
            int index = targets.Count;
            targets.Add((a, b, width, owner, circle));
            int steps = Math.Max(1, (int)Math.Ceiling((b - a).Length / 4));
            int reach = (int)Math.Ceiling(width / 2 / cell) + 1;
            for (int k = 0; k <= steps; k++)
            {
                var p = a + (b - a) * ((double)k / steps);
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell);
                for (int y = cy - reach; y <= cy + reach; y++)
                    for (int x = cx - reach; x <= cx + reach; x++)
                    {
                        long key = CellKey(x, y);
                        if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
                        bucket.Add(index);
                    }
            }
        }
    }

    /// <summary>
    /// VEC147: weld the chain fragments of one straight source line back together across
    /// the junction where another stroke terminates on it. A stem that ends on a line
    /// elevates the distance transform along the line's axis, so the ridge local-max test
    /// drops the axis pixels for the stem's width and the line traces as facing fragments
    /// with an isolated maximum between them; the short fragments fail the admission gate
    /// and their ink stays filled. A weld needs far more than collinearity: both facing
    /// end pieces must be straight and thin (an arc's end piece never passes the bow gate),
    /// collinear within <see cref="JunctionMergeAngleDeg"/> and laterally aligned within
    /// <see cref="LineTol"/>; the junction must be REAL — a third chain long enough to be
    /// a stroke of its own must end transversely beside the gap, the terminating stem —
    /// and every half-step of the chord must be this component's ink whose cross section
    /// stays uniform except where the stem itself widens it on ONE side (a solid, a
    /// perpendicular crossing or paper fails the walk; a short walk beside an oblique
    /// through-line of similar width can still pass and keeps that line intact). Deterministic: after every weld the end grid is
    /// rebuilt and the scan restarts, so the first weldable ordered pair of chain ends
    /// wins, the same order <see cref="MergeCollinearAcrossJunctions"/> keeps for strokes.
    /// A weld joins facing ends only and never moves the merged chain's outer ends: at a
    /// butt-ended corner the fitted endpoint stays inside the theoretical corner axis,
    /// because a round cap placed on that axis would paint source paper.
    /// </summary>
    static void WeldJunctionSplitChains(List<(List<int> Pixels, bool Closed)> chains,
        int id, int[] comp, bool[] inkMask, double[] widthAt, int[] dtSq,
        double thin, double median, int minLen, int w, int h)
    {
        double gapMax = 2 * median;
        double cell = Math.Max(4, gapMax);
        var grid = new Dictionary<long, List<(int Chain, int End)>>();
        bool welded = true;
        while (welded)
        {
            welded = false;
            grid.Clear();
            for (int i = 0; i < chains.Count; i++)
            {
                var (pixels, closed) = chains[i];
                if (closed || pixels.Count < 2) continue;
                Add(pixels[0], i, 0);
                Add(pixels[^1], i, 1);
            }
            for (int i = 0; i < chains.Count && !welded; i++)
            {
                if (chains[i].Closed || chains[i].Pixels.Count < 2) continue;
                for (int e = 0; e < 2 && !welded; e++)
                {
                    var found = new List<(int Chain, int End)>();
                    Collect(chains[i].Pixels[e == 0 ? 0 : ^1], found);
                    found.Sort();
                    foreach (var (j, f) in found)
                    {
                        if (j == i) continue;
                        if (Weld(i, e, j, f)) { welded = true; break; }
                    }
                }
            }
        }

        bool Weld(int i, int e, int j, int f)
        {
            var a = chains[i].Pixels;
            var b = chains[j].Pixels;
            int ea = a[e == 0 ? 0 : ^1], eb = b[f == 0 ? 0 : ^1];
            var pa = Point(ea);
            var pb = Point(eb);
            double gap = (pb - pa).Length;
            if (gap < .5 || gap > gapMax) return false;
            // a weld that would close a ring leaves the merged chain's far ends adjacent
            int fa = a[e == 0 ? ^1 : 0], fb = b[f == 0 ? ^1 : 0];
            if ((Point(fa) - Point(fb)).LengthSq <= 4) return false;
            var pieceA = EndPiece(a, e);
            var pieceB = EndPiece(b, f);
            if (Fit(pieceA) is not { } A || Fit(pieceB) is not { } B) return false;
            // Both facing pieces must carry their own straight evidence. A remnant too
            // short to test cannot be told from the ridge pixel of a CROSSING line at
            // the intersection: on the frozen cad-real scan such a remnant rode the
            // rail's line across the crossing, and the merged dead-end tip then
            // blocked the tip extension that had recovered the whole line as one
            // stroke. Pieces that fail the residual or bow gate are already null.
            if (!A.Straight || !B.Straight) return false;
            var dirA = A.Dir;
            if (dirA.Dot(pa - Point(e == 0 ? pieceA[^1] : pieceA[0])) < 0) dirA = dirA * -1;
            var dirB = B.Dir;
            if (dirB.Dot(pb - Point(f == 0 ? pieceB[^1] : pieceB[0])) < 0) dirB = dirB * -1;
            var chord = (pb - pa) * (1 / gap);
            if (chord.Dot(dirA) <= 0 || chord.Dot(dirB) >= 0) return false;
            if (dirA.Dot(dirB) > -DetMath.Cos(JunctionMergeAngleDeg * Math.PI / 180.0)) return false;
            if (Math.Abs((pb - A.Origin).Dot(A.Normal)) > A.Tol) return false;
            if (Math.Abs((pa - B.Origin).Dot(B.Normal)) > B.Tol) return false;
            if (!HasStem((pa + pb) * .5, dirA, i, j)) return false;
            if (!InkSupported(pa, pb, chord, Math.Max(A.Med, B.Med))) return false;
            var bridge = new List<int>();
            for (double t = .5; t < gap; t += .5)
            {
                int q = Pixel(pa + chord * t);
                if (q == ea || q == eb || bridge.Count > 0 && bridge[^1] == q) continue;
                bridge.Add(q);
            }
            var merged = new List<int>(a.Count + b.Count + bridge.Count);
            if (e == 1) merged.AddRange(a);
            else for (int k = a.Count - 1; k >= 0; k--) merged.Add(a[k]);
            merged.AddRange(bridge);
            if (f == 0) merged.AddRange(b);
            else for (int k = b.Count - 1; k >= 0; k--) merged.Add(b[k]);
            chains[i] = (merged, false);
            chains.RemoveAt(j);
            return true;
        }

        // The terminating stem: a third chain at least half the local admission length
        // with an end beside the gap whose end piece points across the weld line. Corner
        // staircase remnants and short stubs are not strokes of their own and never
        // authorize a weld; tangential arc joins have no transverse end at all.
        bool HasStem(Vec2 mid, Vec2 weldDir, int i, int j)
        {
            double radius = 2 * median;
            double transverse = DetMath.Cos(Math.PI / 4);
            for (int k = 0; k < chains.Count; k++)
            {
                if (k == i || k == j) continue;
                var (pixels, closed) = chains[k];
                if (closed || pixels.Count * 2 < minLen) continue;
                for (int end = 0; end < 2; end++)
                {
                    int eq = pixels[end == 0 ? 0 : ^1];
                    if ((Point(eq) - mid).LengthSq > radius * radius) continue;
                    if (StemDir(EndPiece(pixels, end)) is not { } stem) continue;
                    if (Math.Abs(stem.Dot(weldDir)) <= transverse) return true;
                }
            }
            return false;
        }

        // The stem only vouches that the junction is real, so its end piece needs a
        // transverse direction and the thin width — not the weld pieces' residual or
        // bow gates. A straight stem's one-pixel raster jog where it lands on the
        // line is a step the bow gate reads as strong curvature; the TLS direction
        // stays transverse, and the stem's length, thinness and the weld's own piece
        // gates and source-ink walk carry every remaining protection.
        Vec2? StemDir(List<int> piece)
        {
            if (piece.Count < 2) return null;
            var widths = piece.Select(Width).OrderBy(v => v).ToList();
            double med = widths[widths.Count / 2];
            if (med <= 0 || med > thin) return null;
            var pts = piece.Select(Point).ToList();
            double mx = 0, my = 0;
            foreach (var p in pts) { mx += p.X; my += p.Y; }
            mx /= pts.Count; my /= pts.Count;
            double xx = 0, xy = 0, yy = 0;
            foreach (var p in pts)
            {
                double dx = p.X - mx, dy = p.Y - my;
                xx += dx * dx; xy += dx * dy; yy += dy * dy;
            }
            if (xx + yy < 1e-12) return null;
            double theta = 0.5 * DetMath.Atan2(2 * xy, xx - yy);
            return new Vec2(DetMath.Cos(theta), DetMath.Sin(theta));
        }

        // Source-ink proof of the gap: every half-step is this component's ink, and the
        // cross section stays a uniform band of the line's own width. Where the stem
        // widens the section, exactly one side keeps the line's half band, and the
        // polluted zone may never exceed the stem-scale reach; a solid, a crossing or
        // paper ends the walk.
        bool InkSupported(Vec2 pa, Vec2 pb, Vec2 chord, double med)
        {
            var normal = new Vec2(-chord.Y, chord.X);
            double lo = .3 * med, hi = .7 * med;
            int limit = (int)Math.Ceiling(hi) + 1;
            double zone = 0, zoneMax = 2 * med;
            int steps = (int)Math.Ceiling((pb - pa).Length * 2);
            for (int s = 0; s <= steps; s++)
            {
                var q = pa + (pb - pa) * (s / (double)steps);
                if (!IsInk(q)) return false;
                int runA = Run(q, normal, limit), runB = Run(q, normal * -1, limit);
                double width = runA + runB + 1;
                if (width >= LocalUniformLow * med && width <= LocalUniformHigh * med && Math.Abs(runA - runB) <= 3)
                    zone = 0;
                else
                {
                    bool bandA = runA >= lo && runA <= hi, bandB = runB >= lo && runB <= hi;
                    if (!bandA && !bandB) return false;
                    zone += .5;
                    if (zone > zoneMax) return false;
                }
            }
            return true;

            int Run(Vec2 q, Vec2 step, int reach)
            {
                int n = 0;
                while (n < reach && IsInk(q + step * (n + 1))) n++;
                return n;
            }
        }

        // TLS fit of one end piece. Straight is false when the piece is too short to test
        // (the caller refuses the weld); null means unmeasurable, too thick for a thin
        // stroke, or failing the residual or bow gate.
        (Vec2 Dir, Vec2 Origin, Vec2 Normal, double Med, double Tol, bool Straight)? Fit(List<int> piece)
        {
            if (piece.Count < 2) return null;
            var widths = piece.Select(Width).OrderBy(v => v).ToList();
            double med = widths[widths.Count / 2];
            if (med <= 0 || med > thin) return null;
            var pts = piece.Select(Point).ToList();
            double mx = 0, my = 0;
            foreach (var p in pts) { mx += p.X; my += p.Y; }
            mx /= pts.Count; my /= pts.Count;
            double xx = 0, xy = 0, yy = 0;
            foreach (var p in pts)
            {
                double dx = p.X - mx, dy = p.Y - my;
                xx += dx * dx; xy += dx * dy; yy += dy * dy;
            }
            if (xx + yy < 1e-12) return null;
            double theta = 0.5 * DetMath.Atan2(2 * xy, xx - yy);
            var dir = new Vec2(DetMath.Cos(theta), DetMath.Sin(theta));
            var normal = new Vec2(-dir.Y, dir.X);
            var origin = new Vec2(mx, my);
            if (piece.Count < StraightRunMinPoints) return (dir, origin, normal, med, LineTol(med), false);
            double worst = 0;
            foreach (var p in pts) worst = Math.Max(worst, Math.Abs((p - origin).Dot(normal)));
            if (worst > LineTol(med)) return null;
            // bow: r = α·u² + β·u + γ with u in [-1, 1]; |α| is the sagitta (a curve's
            // signature — the same convention as StraightRunLine)
            double s0 = double.MaxValue, s1 = double.MinValue;
            foreach (var p in pts)
            {
                double s = (p - origin).Dot(dir);
                s0 = Math.Min(s0, s);
                s1 = Math.Max(s1, s);
            }
            double half = (s1 - s0) / 2, midS = (s0 + s1) / 2;
            if (half < 1e-9) return null;
            double su0 = 0, su1 = 0, su2 = 0, su3 = 0, su4 = 0, sr0 = 0, sr1 = 0, sr2 = 0;
            foreach (var p in pts)
            {
                double u = ((p - origin).Dot(dir) - midS) / half, r = (p - origin).Dot(normal), u2 = u * u;
                su0 += 1; su1 += u; su2 += u2; su3 += u2 * u; su4 += u2 * u2;
                sr0 += r; sr1 += r * u; sr2 += r * u2;
            }
            double det = su4 * (su2 * su0 - su1 * su1) - su3 * (su3 * su0 - su1 * su2) + su2 * (su3 * su1 - su2 * su2);
            if (Math.Abs(det) < 1e-12) return null;
            double alpha = (sr2 * (su2 * su0 - su1 * su1) - su3 * (sr1 * su0 - su1 * sr0) + su2 * (sr1 * su1 - su2 * sr0)) / det;
            if (Math.Abs(alpha) > StraightRunMaxSag) return null;
            return (dir, origin, normal, med, LineTol(med), true);
        }

        List<int> EndPiece(List<int> pixels, int end)
        {
            int count = Math.Min((int)Math.Ceiling(2 * median), pixels.Count);
            return end == 0 ? pixels.GetRange(0, count) : pixels.GetRange(pixels.Count - count, count);
        }

        Vec2 Point(int q) => new(q % w + .5, q / w + .5);
        int Pixel(Vec2 p) => (int)Math.Floor(p.Y) * w + (int)Math.Floor(p.X);
        double Width(int q) => widthAt[q] > 0 ? widthAt[q] : 2 * Math.Sqrt(dtSq[q]) - 1;
        bool IsInk(Vec2 q)
        {
            int x = (int)Math.Floor(q.X), y = (int)Math.Floor(q.Y);
            return (uint)x < (uint)w && (uint)y < (uint)h && inkMask[y * w + x] && comp[y * w + x] == id;
        }
        void Add(int q, int chain, int end)
        {
            int cx = (int)(q % w / cell), cy = (int)(q / w / cell);
            long key = CellKey(cx, cy);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add((chain, end));
        }
        void Collect(int q, List<(int Chain, int End)> into)
        {
            int cx = (int)(q % w / cell), cy = (int)(q / w / cell);
            for (int yy = cy - 1; yy <= cy + 1; yy++)
                for (int xx = cx - 1; xx <= cx + 1; xx++)
                    if (grid.TryGetValue(CellKey(xx, yy), out var list))
                        into.AddRange(list);
        }
    }
}
