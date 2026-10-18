using System.Runtime.CompilerServices;

namespace Vecto.Core;

/// <summary>One stroked centerline path (VEC-50): an open or closed fitted curve plus a stroke weight.</summary>
public sealed class StrokePath
{
    public int PaletteIndex { get; init; }
    /// <summary>Stroke weight in document pixels: 2× the median distance-transform value over the chain.</summary>
    public double Width { get; init; }
    /// <summary>Closed ring (a circle stroke): the curve already loops back to its start.</summary>
    public bool Closed { get; init; }
    public required List<CubicBezier> Segments { get; init; }
}

/// <summary>
/// Centerline extraction for <see cref="ImageStyle.LineArt"/> (VEC-50, stage 2 of the CAD mode):
/// thin line-work is re-represented as single stroked paths instead of the dual-boundary filled
/// ribbons the planar pipeline produces. Pipeline: exact Euclidean distance transform
/// (Felzenszwalb–Huttenlocher, two 1-D passes) → Zhang–Suen thinning plus an 8-simple cleanup →
/// chain tracing that splits at junctions → spur pruning → per-chain width = 2×median DT (+1
/// when the stroke has no centre pixel, i.e. even widths) → the same corner/smooth/fit stage the
/// boundary chains get. A component goes centerline only when it is line-like: thin (≤ 2× the
/// image's median stroke width) AND long (bbox ≥ 8×) AND not part of a dense row of same-size
/// neighbours (text rows — glyphs stay filled, which is what keeps text legible). Everything
/// else — glyphs, wide bars, hatching-scale blobs — keeps the filled-region treatment, and the
/// pixels of centerline components are relabelled to paper so the planar partition still tiles
/// the canvas exactly (the strokes paint on top; see docs/ai/research/2026-10-12-cad-mode-research.md).
/// </summary>
public static class Centerline
{
    /// <summary>TEMPORARY debug hook — remove before commit.</summary>
    public static List<string>? DebugLog;

    /// <summary>A component is thin when its widest stroke is at most this × the image's median width.</summary>
    public const double ThinFactor = 2.0;
    /// <summary>Parity slack on the thin test: even-width strokes have no centre pixel, so their
    /// discrete max DT reads one half below the true half-width.</summary>
    public const double ThinSlack = 0.75;
    /// <summary>A component is long when its bbox side reaches this × the median width.</summary>
    public const double LongFactor = 8.0;
    /// <summary>Text candidates are at most this × the median width across (glyph-sized).</summary>
    public const double TextMaxFactor = 20.0;
    /// <summary>…and sit in a row of at least this many same-size peers (Fletcher–Kasturi text rows).</summary>
    public const int TextPeerMin = 4;

    /// <summary>TODO(VEC-50): master switch — off until the excess-ink quality fix lands
    /// (CAD benchmark precision 0.761 &lt; 0.95) and the writers emit strokes.</summary>
    public static bool Enabled = false;

    /// <summary>
    /// The LineArt centerline stage: classifies ink components, replaces the line-like ones with
    /// stroked paths and relabels their pixels to paper (in place) so the region pipeline never
    /// sees them. Returns the strokes in scan order; deterministic for a given input.
    /// </summary>
    internal static List<StrokePath> Extract(int[] labels, List<PaletteEntry> palette, EffectiveParams p, int w, int h, CancellationToken ct)
    {
        if (!Enabled) return [];
        int ink = 0, paper = 0;
        for (int c = 1; c < palette.Count; c++)
        {
            if (Lum(palette[c].Color) < Lum(palette[ink].Color)) ink = c;
            if (Lum(palette[c].Color) > Lum(palette[paper].Color)) paper = c;
        }
        var inkMask = new bool[w * h];
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] == ink) inkMask[i] = true;
        if (!inkMask.Any(b => b)) return [];

        // 4-connected components, like the region pipeline (diagonal contacts were bridged
        // upstream by ScanPreprocess, so 4-connectivity already follows thin diagonals)
        var comp = new int[w * h];
        Array.Fill(comp, -1);
        var comps = new List<Comp>();
        var stack = new int[w * h];
        for (int s = 0; s < inkMask.Length; s++)
        {
            if (!inkMask[s] || comp[s] >= 0) continue;
            int id = comps.Count, sp = 0;
            stack[sp++] = s;
            comp[s] = id;
            int minx = w, miny = h, maxx = 0, maxy = 0;
            while (sp > 0)
            {
                int q = stack[--sp], x = q % w, y = q / w;
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
                miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                if (x > 0 && comp[q - 1] < 0 && inkMask[q - 1]) { comp[q - 1] = id; stack[sp++] = q - 1; }
                if (x < w - 1 && comp[q + 1] < 0 && inkMask[q + 1]) { comp[q + 1] = id; stack[sp++] = q + 1; }
                if (y > 0 && comp[q - w] < 0 && inkMask[q - w]) { comp[q - w] = id; stack[sp++] = q - w; }
                if (y < h - 1 && comp[q + w] < 0 && inkMask[q + w]) { comp[q + w] = id; stack[sp++] = q + w; }
            }
            comps.Add(new Comp { Id = id, Pixels = new List<int>(), MinX = minx, MinY = miny, MaxX = maxx, MaxY = maxy });
        }
        for (int i = 0; i < inkMask.Length; i++)
            if (comp[i] >= 0) comps[comp[i]].Pixels.Add(i);

        var dtSq = EdtSq(inkMask, w, h);
        var skel = (bool[])inkMask.Clone();
        Thin(skel, w, h);

        // per-skeleton-pixel width: 2×DT−1 measures to the paper pixel CENTRES, which is exact
        // for odd stroke widths; a 4-neighbour of equal DT that fell out of the skeleton is the
        // twin row of an even-width stroke — its centre line runs between the two rows
        var widthAt = new double[w * h];
        var allWidths = new List<double>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!skel[i]) continue;
                double width = 2 * Math.Sqrt(dtSq[i]) - 1;
                if (Twin(skel, inkMask, dtSq, x, y, i, w, h)) width += 1;
                widthAt[i] = width;
                allWidths.Add(width);
            }
        if (allWidths.Count == 0) return [];
        double median = Median(allWidths);

        // classification: thin AND long AND not a text-row glyph
        foreach (var c in comps)
        {
            int maxDtSq = 0;
            foreach (int i in c.Pixels)
                if (dtSq[i] > maxDtSq) maxDtSq = dtSq[i];
            c.Width = 2 * Math.Sqrt(maxDtSq) - 1;
            c.MaxDim = Math.Max(c.MaxX - c.MinX + 1, c.MaxY - c.MinY + 1);
            c.Centerline = c.Width <= ThinFactor * median + ThinSlack && c.MaxDim >= LongFactor * median;
        }
        MarkTextRows(comps, median);

        DebugLog?.Add($"median={median:0.00} pruneLen={Math.Max(2, (int)Math.Ceiling(2 * median))}");
        foreach (var c in comps)
            DebugLog?.Add($"comp id={c.Id} px={c.Pixels.Count} bbox=({c.MinX},{c.MinY})-({c.MaxX},{c.MaxY}) width={c.Width:0.00} maxDim={c.MaxDim} cl={c.Centerline}");

        int pruneLen = Math.Max(2, (int)Math.Ceiling(2 * median));
        var strokes = new List<StrokePath>();
        foreach (var c in comps)
        {
            if (!c.Centerline) continue;
            ct.ThrowIfCancellationRequested();
            var tracer = new ChainTracer(skel, comp, c.Id, w, h);
            tracer.PruneSpurs(c.Pixels, pruneLen);
            foreach (var (pixels, closed) in tracer.Trace(c.Pixels))
            {
                DebugLog?.Add($"  comp {c.Id} chain len={pixels.Count} closed={closed} start=({pixels[0] % w},{pixels[0] / w})");
                if (pixels.Count < 2) continue;
                var widths = new List<double>();
                foreach (int q in pixels)
                    if (tracer.Degree(q) == 2) widths.Add(widthAt[q]);
                if (widths.Count < 2)
                {
                    widths.Clear();
                    foreach (int q in pixels) widths.Add(widthAt[q]);
                }
                if (widths.Count == 0) continue;
                var stroke = BuildStroke(pixels, closed, Median(widths), ink, p, w);
                if (stroke != null)
                {
                    strokes.Add(stroke);
                    c.Emitted = true;
                }
            }
        }

        // only components that actually produced strokes give up their fill; anything that
        // classified as line-like but traced to nothing keeps today's filled treatment
        foreach (var c in comps)
            if (c.Centerline && c.Emitted)
                foreach (int i in c.Pixels)
                    labels[i] = paper;
        return strokes;
    }

    /// <summary>
    /// Thin+long components of glyph size that sit in a dense row of same-size neighbours are
    /// text (Fletcher–Kasturi): they stay filled — centerlining glyphs destroys legibility.
    /// </summary>
    static void MarkTextRows(List<Comp> comps, double median)
    {
        var cand = comps.Where(c => c.Centerline && c.MaxDim <= TextMaxFactor * median).OrderBy(c => c.MinY).ToList();
        if (cand.Count < TextPeerMin + 1) return;
        int maxDim = cand.Max(c => c.MaxDim);
        var miny = new double[cand.Count];
        for (int i = 0; i < cand.Count; i++) miny[i] = cand[i].MinY;
        for (int ci = 0; ci < cand.Count; ci++)
        {
            var c = cand[ci];
            double low = c.MinY - maxDim;
            int lo = Array.BinarySearch(miny, low);
            if (lo < 0) lo = ~lo;
            while (lo > 0 && miny[lo - 1] >= low) lo--;
            int peers = 0;
            for (int oi = lo; oi < cand.Count; oi++)
            {
                var o = cand[oi];
                if (o.MinY > c.MaxY) break;          // sorted by MinY — nothing later can overlap
                if (oi == ci || !Peer(c, o)) continue;
                if (++peers >= TextPeerMin) break;
            }
            if (peers >= TextPeerMin) c.Centerline = false;
        }
    }

    /// <summary>Same text row: similar box size, vertically overlapping, horizontally close.</summary>
    static bool Peer(Comp a, Comp b)
    {
        int ha = a.MaxY - a.MinY + 1, hb = b.MaxY - b.MinY + 1;
        int wa = a.MaxX - a.MinX + 1, wb = b.MaxX - b.MinX + 1;
        if (ha > 2 * hb || hb > 2 * ha || wa > 2 * wb || wb > 2 * wa) return false;
        if (b.MaxY < a.MinY || b.MinY > a.MaxY) return false;
        int gap = b.MinX > a.MaxX ? b.MinX - a.MaxX : a.MinX > b.MaxX ? a.MinX - b.MaxX : -1;
        return gap >= 0 && gap <= 4 * Math.Max(ha, hb);
    }

    /// <summary>Pixel centres to fitted curve, through the same corner/smooth/fit stage boundary chains use.</summary>
    static StrokePath? BuildStroke(List<int> pixels, bool closed, double width, int ink, EffectiveParams p, int w)
    {
        var pts = new List<Vec2>(pixels.Count + 1);
        foreach (int i in pixels) pts.Add(new Vec2(i % w + 0.5, i / w + 0.5));
        if (closed) pts.Add(pts[0]);
        List<CubicBezier> segs;
        if (p.CurveFitting)
        {
            var poly = CornerPolygon.OptimalPolygon(pts, closed);
            var corners = CornerPolygon.Corners(pts, closed, poly, p.CornerThresholdDeg);
            ChainGeometry.SmoothInPlace(pts, closed, corners, 2, 0.5, 0.35);
            segs = BezierFitter.FitChain(pts, closed, corners, p.FitToleranceSq);
        }
        else
        {
            var simple = ChainGeometry.SimplifyDp(pts, p.PolygonEpsilon);
            segs = new List<CubicBezier>(Math.Max(0, simple.Count - 1));
            for (int i = 0; i + 1 < simple.Count; i++) segs.Add(CubicBezier.Line(simple[i], simple[i + 1]));
        }
        if (segs.Count == 0) return null;
        return new StrokePath { PaletteIndex = ink, Width = width, Closed = closed, Segments = segs };
    }

    static bool Twin(bool[] skel, bool[] ink, int[] dtSq, int x, int y, int i, int w, int h)
    {
        return At(x - 1, y, i - 1) || At(x + 1, y, i + 1) || At(x, y - 1, i - w) || At(x, y + 1, i + w);

        bool At(int xx, int yy, int q)
        {
            if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return false;
            return ink[q] && !skel[q] && dtSq[q] == dtSq[i];
        }
    }

    static double Lum(Rgba32 c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    // ---- exact Euclidean distance transform (Felzenszwalb–Huttenlocher) ----

    const double Inf = 1e12;

    /// <summary>
    /// Squared distance from every ink pixel to the nearest non-ink pixel (0 on non-ink):
    /// the 1-D lower-envelope transform run over rows, then over columns — exact, BCL only.
    /// </summary>
    public static int[] EdtSq(bool[] ink, int w, int h)
    {
        int n = Math.Max(w, h);
        var f = new double[n];
        var d = new double[n];
        var v = new int[n];
        var z = new double[n + 1];
        var dt = new double[w * h];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++) f[x] = ink[row + x] ? Inf : 0;
            Dt1d(f, d, v, z, w);
            for (int x = 0; x < w; x++) dt[row + x] = d[x];
        }
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) f[y] = dt[y * w + x];
            Dt1d(f, d, v, z, h);
            for (int y = 0; y < h; y++) dt[y * w + x] = d[y];
        }
        var result = new int[w * h];
        for (int i = 0; i < result.Length; i++) result[i] = (int)Math.Round(dt[i]);
        return result;
    }

    /// <summary>f[i] ← min over j of (i−j)² + f[j] (parabola lower envelope, Felzenszwalb–Huttenlocher 2012).</summary>
    static void Dt1d(double[] f, double[] d, int[] v, double[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = -Inf;
        z[1] = Inf;
        for (int q = 1; q < n; q++)
        {
            double s = Intersect(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersect(f, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = Inf;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    static double Intersect(double[] f, int q, int vk) =>
        ((f[q] + (double)q * q) - (f[vk] + (double)vk * vk)) / (2.0 * (q - vk));

    // ---- thinning ----

    /// <summary>
    /// Zhang–Suen thinning (8-connected ink) followed by an 8-simple cleanup. ZS is stable on
    /// 2-px diagonal strips — every pixel of one has two 0→1 transitions, so neither subpass
    /// ever deletes it (a bridged 1-px diagonal is exactly such a strip) — and it leaves small
    /// plateaus at junctions. The cleanup deletes, in raster order, every pixel with ≥3
    /// skeleton neighbours whose neighbours stay 8-connected without it; pixels with ≤2
    /// neighbours (chain middles, stroke ends) are never touched.
    /// </summary>
    public static void Thin(bool[] skel, int w, int h)
    {
        var del = new List<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int sub = 0; sub < 2; sub++)
            {
                del.Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!skel[i]) continue;
                        bool p2 = At(x, y - 1), p3 = At(x + 1, y - 1), p4 = At(x + 1, y),
                             p5 = At(x + 1, y + 1), p6 = At(x, y + 1), p7 = At(x - 1, y + 1),
                             p8 = At(x - 1, y), p9 = At(x - 1, y - 1);
                        int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0)
                              + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;
                        int a = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0)
                              + (!p5 && p6 ? 1 : 0) + (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0)
                              + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                        if (a != 1) continue;
                        if (sub == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8)
                                     : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                        del.Add(i);
                    }
                foreach (int i in del) skel[i] = false;
                changed |= del.Count > 0;
            }
        }

        bool removed = true;
        while (removed)
        {
            removed = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!skel[i] || Neighbors8(skel, x, y, w, h) < 3 || !Simple8(skel, x, y, w, h)) continue;
                    skel[i] = false;
                    removed = true;
                }
        }

        bool At(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && skel[y * w + x];
    }

    static int Neighbors8(bool[] skel, int x, int y, int w, int h)
    {
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int xx = x + dx, yy = y + dy;
                if ((uint)xx < (uint)w && (uint)yy < (uint)h && skel[yy * w + xx]) n++;
            }
        return n;
    }

    /// <summary>Would deleting (x,y) keep its skeleton neighbours 8-connected among themselves?</summary>
    static bool Simple8(bool[] skel, int x, int y, int w, int h)
    {
        Span<int> nb = stackalloc int[8];
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int xx = x + dx, yy = y + dy;
                if ((uint)xx < (uint)w && (uint)yy < (uint)h && skel[yy * w + xx]) nb[n++] = yy * w + xx;
            }
        if (n == 0) return false;
        Span<bool> seen = stackalloc bool[8];
        Span<int> st = stackalloc int[8];
        int sp = 0, reached = 1;
        st[sp++] = 0;
        seen[0] = true;
        while (sp > 0)
        {
            int k = st[--sp];
            int qx = nb[k] % w, qy = nb[k] / w;
            for (int j = 0; j < n; j++)
            {
                if (seen[j]) continue;
                int jx = nb[j] % w, jy = nb[j] / w;
                if (Math.Abs(qx - jx) <= 1 && Math.Abs(qy - jy) <= 1)
                {
                    seen[j] = true;
                    reached++;
                    st[sp++] = j;
                }
            }
        }
        return reached == n;
    }

    // ---- chain tracing ----

    sealed class Comp
    {
        public required List<int> Pixels { get; init; }   // ascending pixel indices
        public int MinX, MinY, MaxX, MaxY;
        public int Id;                                    // set after all components exist
        public double Width;                               // 2×max DT − 1
        public int MaxDim;
        public bool Centerline;
        public bool Emitted;
    }

    /// <summary>
    /// Walks one component's skeleton: chains split at junctions (degree ≥ 3), junction pixels
    /// ride along as chain ends so round caps cover the crossing, and junction-free cycles come
    /// out as closed rings. Deterministic: pixels and neighbours are visited in index order.
    /// </summary>
    sealed class ChainTracer
    {
        readonly bool[] skel;
        readonly int[] comp;
        readonly int id;
        readonly int w, h;
        readonly HashSet<int> visited = [];
        readonly HashSet<long> used = [];
        readonly List<int> nb = [];

        public ChainTracer(bool[] skel, int[] comp, int id, int w, int h)
        {
            this.skel = skel;
            this.comp = comp;
            this.id = id;
            this.w = w;
            this.h = h;
        }

        public int Degree(int p)
        {
            int x = p % w, y = p / w, n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx, yy = y + dy;
                    if ((uint)xx < (uint)w && (uint)yy < (uint)h && skel[yy * w + xx] && comp[yy * w + xx] == id) n++;
                }
            return n;
        }

        /// <summary>Removes dead-end branches shorter than <paramref name="pruneLen"/> that end at a
        /// junction; a branch that ends at another endpoint is a real (short) stroke and stays.</summary>
        public void PruneSpurs(List<int> pixels, int pruneLen)
        {
            bool removed = true;
            while (removed)
            {
                removed = false;
                foreach (int e in pixels)
                {
                    if (!skel[e] || Degree(e) != 1) continue;
                    var path = new List<int>();
                    int prev = -1, cur = e;
                    while (cur >= 0)
                    {
                        path.Add(cur);
                        if (path.Count > 1 && Degree(cur) != 2) break;
                        if (path.Count > pruneLen + 1) break;
                        int next = Neighbor(cur, prev);
                        prev = cur;
                        cur = next;
                    }
                    if (Degree(path[^1]) < 3 || path.Count > pruneLen) continue;
                    for (int k = 0; k < path.Count - 1; k++) skel[path[k]] = false;
                    removed = true;
                }
            }
        }

        public List<(List<int> Pixels, bool Closed)> Trace(List<int> pixels)
        {
            var chains = new List<(List<int>, bool)>();
            foreach (int e in pixels)
            {
                if (!skel[e] || visited.Contains(e) || Degree(e) != 1) continue;
                chains.Add((Walk(e, -1), false));
            }
            foreach (int j in pixels)
            {
                if (!skel[j] || Degree(j) < 3) continue;
                Neighbors(j, nb);
                foreach (int n in nb)
                {
                    if (used.Contains(Edge(j, n))) continue;
                    if (Degree(n) >= 3)
                    {
                        used.Add(Edge(j, n));
                        used.Add(Edge(n, j));
                        chains.Add((new List<int> { j, n }, false));
                        continue;
                    }
                    if (visited.Contains(n)) continue;
                    chains.Add((Walk(j, n), false));
                }
            }
            foreach (int r in pixels)
            {
                if (!skel[r] || visited.Contains(r) || Degree(r) != 2) continue;
                chains.Add(WalkRing(r));
            }
            return chains;
        }

        List<int> Walk(int start, int first)
        {
            var chain = new List<int> { start };
            if (first < 0) visited.Add(start);
            int prev = start, cur = first < 0 ? Neighbor(start, -1) : first;
            while (cur >= 0)
            {
                int d = Degree(cur);
                if (d == 2 && visited.Contains(cur)) break;
                chain.Add(cur);
                if (d != 2)
                {
                    if (d == 1) visited.Add(cur);
                    break;
                }
                visited.Add(cur);
                int next = Neighbor(cur, prev);
                prev = cur;
                cur = next;
            }
            return chain;
        }

        (List<int>, bool) WalkRing(int start)
        {
            var chain = new List<int> { start };
            visited.Add(start);
            int prev = start, cur = Neighbor(start, -1);
            while (cur >= 0 && cur != start && !visited.Contains(cur))
            {
                chain.Add(cur);
                visited.Add(cur);
                int next = Neighbor(cur, prev);
                prev = cur;
                cur = next;
            }
            return (chain, cur == start);
        }

        int Neighbor(int p, int excluding)
        {
            int x = p % w, y = p / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx, yy = y + dy;
                    if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                    int q = yy * w + xx;
                    if (q == excluding || !skel[q] || comp[q] != id) continue;
                    return q;
                }
            return -1;
        }

        void Neighbors(int p, List<int> into)
        {
            into.Clear();
            int x = p % w, y = p / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx, yy = y + dy;
                    if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                    int q = yy * w + xx;
                    if (skel[q] && comp[q] == id) into.Add(q);
                }
        }

        static long Edge(int a, int b) => (long)a << 32 | (uint)b;
    }
}
