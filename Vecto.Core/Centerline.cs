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
/// (Felzenszwalb–Huttenlocher, two 1-D passes) → medial-axis ridge plus an 8-simple cleanup →
/// chain tracing that splits at junctions → spur pruning → per-chain width = 2×median DT (+1
/// when the stroke has no centre pixel, i.e. even widths) → the same corner/smooth/fit stage the
/// boundary chains get. A component goes centerline only when it is line-like: thin (≤ 2× the
/// image's median stroke width) AND long (bbox ≥ 8×) AND not part of a dense row of same-size
/// neighbours (text rows — glyphs stay filled, which is what keeps text legible). Everything
/// else — glyphs, wide bars, hatching-scale blobs — keeps the filled-region treatment, and the
/// pixels of centerline components are relabelled to paper so the planar partition still tiles
/// the canvas exactly (the strokes paint on top; see docs/ai/research/2026-10-12-cad-mode-research.md).
/// VEC-52 (stage 3) regularizes the chains into CAD primitives: corner-split runs become exact
/// 2-anchor lines with 0/90/45° axis snapping, and circular strokes become exact arcs. A
/// junction pass then welds the collinear pieces a crossing or a scan gap split off one
/// straight stroke, and snaps line endpoints to the exact line-line intersections.
/// VEC-52d finally rationalizes the straight lines' angles to drafting intent: parallel
/// families unify on one angle, near-grid angles snap onto the 15° grid, and everything
/// else rounds to a whole degree.
/// </summary>
public static class Centerline
{
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
    /// <summary>Maximum gap the ridge bridge reconnects: at staircase transitions the DT
    /// local-max ridge drops 1–2 pixels, so rings shatter into arcs at their octants, and a
    /// wobbly scan line's ridge breaks every few pixels (VEC-52). The bridge's direction
    /// guard (<see cref="BridgeMaxAngleDeg"/>) is what keeps the larger reach safe.</summary>
    public const double BridgeDist = 4.5;
    /// <summary>Largest angle (deg) between a ridge bridge and the dead end's outward chain
    /// direction: bridges continue strokes, they do not weld caps shut or jump between
    /// strokes that merely pass close by.</summary>
    public const double BridgeMaxAngleDeg = 45.0;

    /// <summary>Master switch for the centerline stage: off → every LineArt component keeps
    /// today's filled treatment (the VEC-50 escape hatch).</summary>
    public static bool Enabled = true;

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
        var skel = Ridge(inkMask, dtSq, w, h);

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
                if (Twin(skel, inkMask, dtSq, x, y, i, w, h) >= 0) width += 1;
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

        int pruneLen = Math.Max(2, (int)Math.Ceiling(2 * median));
        int growLen = Math.Max(4, (int)Math.Ceiling(median));
        var strokes = new List<StrokePath>();
        foreach (var c in comps)
        {
            if (!c.Centerline) continue;
            ct.ThrowIfCancellationRequested();
            var tracer = new ChainTracer(skel, comp, c.Id, w, h);
            tracer.PruneSpurs(c.Pixels, pruneLen);
            // bridging closes the staircase gaps before widths are read: a bridged pixel
            // becomes a degree-2 chain middle, so its width must be measured like any other
            foreach (int q in tracer.BridgeGaps(c.Pixels, BridgeDist))
            {
                double width = 2 * Math.Sqrt(dtSq[q]) - 1;
                if (Twin(skel, inkMask, dtSq, q % w, q / w, q, w, h) >= 0) width += 1;
                widthAt[q] = width;
            }
            // trace every chain first and grow the dead ends into the ink the ridge
            // dropped — the cap-crumb pass needs the grown tips to measure coverage
            var chains = new List<(List<int> Pixels, bool Closed)>();
            foreach (var (pixels, closed) in tracer.Trace(c.Pixels))
            {
                if (pixels.Count < 2) continue;
                if (!closed) tracer.Extend(pixels, growLen);
                chains.Add((pixels, closed));
            }
            foreach (var (pixels, closed, width) in DropCapCrumbs(chains, tracer, widthAt, median, w))
            {
                if (width <= 0) continue;
                var stroke = BuildStroke(pixels, closed, width, ink, p, w, h, skel, inkMask, dtSq);
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
        // stage 3 junction pass: weld collinear pieces split by junctions or scan gaps,
        // then land every line endpoint that ends against another line on the exact
        // intersection — the strokes a CAD user sees cross and meet cleanly
        MergeCollinearAcrossJunctions(strokes);
        RationalizeLineAngles(strokes);
        SnapEndsToIntersections(strokes);
        return strokes;
    }

    /// <summary>
    /// Measures every traced chain's stroke width and drops the CAP CRUMBS: short ridge
    /// fragments a stroke's flat cap leaves beside the true tip (the local-max test keeps
    /// a little arc across the cap even after bridging and growth). A crumb is an open
    /// chain, at most <see cref="CrumbMaxLen"/> pixels long, every pixel of which sits
    /// inside the round cap of a strictly longer chain's tip — the stroke's own cap
    /// already paints that ink, so emitting the crumb would double-paint the tip and add
    /// a spurious micro-stroke. That is why the tips are grown before this pass: the
    /// coverage radius is measured from the grown tip, which reaches the true stroke end.
    /// Width is the median over degree-2 chain pixels (junction ends and grown tips are
    /// excluded — their width sample is not the stroke's), falling back to all sampled
    /// pixels; a chain with no sample at all gets width 0 and is dropped by the caller.
    /// </summary>
    static List<(List<int> Pixels, bool Closed, double Width)> DropCapCrumbs(
        List<(List<int> Pixels, bool Closed)> chains, ChainTracer tracer, double[] widthAt, double median, int w)
    {
        var measured = new List<(List<int> Pixels, bool Closed, double Width)>(chains.Count);
        var samples = new List<double>();
        foreach (var (pixels, closed) in chains)
        {
            samples.Clear();
            foreach (int q in pixels)
                if (widthAt[q] > 0 && tracer.Degree(q) == 2) samples.Add(widthAt[q]);
            if (samples.Count < 2)
            {
                samples.Clear();
                foreach (int q in pixels)
                    if (widthAt[q] > 0) samples.Add(widthAt[q]);
            }
            measured.Add((pixels, closed, samples.Count == 0 ? 0 : Median(samples)));
        }
        for (int i = 0; i < measured.Count; i++)
        {
            var (pixels, closed, _) = measured[i];
            if (closed || pixels.Count > CrumbMaxLen(median)) continue;
            for (int j = 0; j < measured.Count; j++)
            {
                if (i == j) continue;
                var (cover, _, coverWidth) = measured[j];
                if (cover.Count <= pixels.Count) continue;
                double r = coverWidth / 2 + CrumbCoverSlack;
                if (Covered(pixels, cover[0], r, w) || Covered(pixels, cover[^1], r, w))
                {
                    measured.RemoveAt(i);
                    i--;
                    break;
                }
            }
        }
        return measured;
    }

    /// <summary>Longest chain (pixels) that can still be a cap crumb: a cap fragment spans
    /// about one stroke width; anything longer is a real (short) stroke and stays.</summary>
    static int CrumbMaxLen(double median) => Math.Max(3, (int)Math.Ceiling(median));

    /// <summary>Slack on the round-cap coverage radius: the fitted tip sits within half a
    /// pixel of the grown tip pixel, and the crumb's own pixels carry the same slack.</summary>
    const double CrumbCoverSlack = 1.0;

    /// <summary>True when every crumb pixel's centre lies within <paramref name="r"/> of the
    /// cover chain's tip pixel — the round cap painted at that tip covers the crumb whole.</summary>
    static bool Covered(List<int> crumbs, int cap, double r, int w)
    {
        double cx = cap % w + 0.5, cy = cap / w + 0.5, r2 = r * r;
        foreach (int q in crumbs)
        {
            double dx = q % w + 0.5 - cx, dy = q / w + 0.5 - cy;
            if (dx * dx + dy * dy > r2) return false;
        }
        return true;
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

    /// <summary>Pixel centres to fitted curve, through the same corner/smooth/fit stage boundary chains use.
    /// A pixel with a twin row shifts half a pixel toward it, so the centre line of an
    /// even-width stroke runs between its two rows instead of along one of them.</summary>
    static StrokePath? BuildStroke(List<int> pixels, bool closed, double width, int ink, EffectiveParams p, int w, int h, bool[] skel, bool[] inkMask, int[] dtSq)
    {
        var pts = new List<Vec2>(pixels.Count + 1);
        foreach (int i in pixels)
        {
            double px = i % w + 0.5, py = i / w + 0.5;
            int twin = Twin(skel, inkMask, dtSq, i % w, i / w, i, w, h);
            if (twin >= 0)
            {
                px += 0.5 * Math.Sign(twin % w - i % w);
                py += 0.5 * Math.Sign(twin / w - i / w);
            }
            pts.Add(new Vec2(px, py));
        }
        if (closed) pts.Add(pts[0]);
        List<CubicBezier> segs;
        if (p.CurveFitting)
        {
            // Boundary chains are lattice staircases, so the optimal-polygon DP always finds a
            // straight run into each chain end. Centerline chains are not lattice geometry:
            // twin-row half-pixel shifts and ridge-gap bridges can wobble a chain end enough
            // that no straight run reaches it and the DP comes back empty. Fall back to the
            // simple fitter there instead of crashing — the stroke still emits.
            try
            {
                var poly = CornerPolygon.OptimalPolygon(pts, closed);
                var corners = CornerPolygon.Corners(pts, closed, poly, p.CornerThresholdDeg);
                ChainGeometry.SmoothInPlace(pts, closed, corners, 2, 0.5, 0.35);
                segs = FitCadPrimitives(pts, closed, corners, width, p);
            }
            catch (InvalidOperationException)
            {
                segs = FitSimply();
            }
        }
        else
        {
            segs = FitSimply();
        }
        if (segs.Count == 0) return null;
        return new StrokePath { PaletteIndex = ink, Width = width, Closed = closed, Segments = segs };

        List<CubicBezier> FitSimply()
        {
            var simple = ChainGeometry.SimplifyDp(pts, Math.Max(0.05, p.PolygonEpsilon * StrokeFitFactor));
            var list = new List<CubicBezier>(Math.Max(0, simple.Count - 1));
            for (int i = 0; i + 1 < simple.Count; i++) list.Add(CubicBezier.Line(simple[i], simple[i + 1]));
            return list;
        }
    }

    /// <summary>Centerline chains fit far tighter than boundary chains: a boundary wiggle
    /// costs nothing (the fill still covers the same pixels), but a stroke is PAINTED with
    /// its width — a fit allowed to wander like a boundary makes a 2-px stroke paint 3 px
    /// wide where it drifts (chain ends wobble ±0.5 px at junctions; the fit must not smear
    /// that across a long straight run).</summary>
    const double StrokeFitFactor = 0.16;

    static double StrokeFitTolSq(EffectiveParams p) => Math.Max(0.01, p.FitToleranceSq * StrokeFitFactor);

    // ---- VEC-52 stage 3: CAD regularization ----

    /// <summary>Primitive hypothesis tolerance: this × the chain's stroke width. A
    /// centerline that drifts less than half its width still paints the same ink (the
    /// stroke is painted <see cref="StrokePath.Width"/> wide), so the simplest primitive
    /// that explains the chain within it wins — a straight ink line becomes ONE 2-anchor
    /// path instead of a Bezier chain following the scan's pixel wobble.</summary>
    public const double FitWidthFactor = 0.5;
    /// <summary>Clamps on the width-derived tolerance (px): a floor so 1-px strokes keep a
    /// workable hypothesis, a cap so wide ones cannot drift a full half-width (a scan's
    /// wobble rides ±1 px on the ridge — the cap must admit it or every real stroke
    /// falls through to the Bezier fitter).</summary>
    public const double FitTolMin = 0.25, FitTolMax = 1.5;
    /// <summary>Directions this close to a 45° multiple snap exactly (Scan2CAD-style axis
    /// snap): near-horizontal/vertical/diagonal lines come out at exactly 0/90/45°.</summary>
    public const double CadAngleSnapDeg = 1.5;
    /// <summary>Two straight strokes whose directions agree within this (deg, wrap-aware)
    /// are the same INTENDED angle: one cluster, one final angle — a drawing's parallel
    /// lines come out exactly parallel (VEC-52d; the model is prototyped and validated in
    /// coord/artifacts/prototype-angles/angle_proto.py).</summary>
    public const double AngleClusterTolDeg = 1.0;
    /// <summary>The drafting grid (deg): a cluster angle within <see cref="AngleGridSnapDeg"/>
    /// of a multiple of this snaps there exactly (0/15/30/45/60/75/90…).</summary>
    public const double AngleGridStepDeg = 15.0;
    /// <summary>Cluster angle this close (deg, wrap-aware) to a grid multiple snaps to it;
    /// everything else keeps its measured WHOLE degree — nothing off the grid is forced onto it.</summary>
    public const double AngleGridSnapDeg = 1.5;
    /// <summary>Largest bow (quadratic residual trend, px) a straight run may have; above
    /// it the run is an arc, not a noisy line (same convention as
    /// <see cref="ChainGeometry.StraightRunMaxSag"/>).</summary>
    public const double StraightRunMaxSag = 0.3;
    /// <summary>Fewest interior points a run between pins needs before it can be claimed
    /// straight (cf. <see cref="ChainGeometry.StraightRunMinPoints"/>, which boundary
    /// chains need because their noise is JPEG ringing, not scan wobble).</summary>
    public const int StraightRunMinPoints = 4;

    static double LineTol(double width) => Math.Clamp(FitWidthFactor * width, FitTolMin, FitTolMax);

    /// <summary>Largest gap (px) between the facing endpoints of two collinear pieces the
    /// junction weld still bridges: a crossing's opposite arms share the junction pixel
    /// (gap ≈ 0), and a scan's broken line leaves a few paper pixels between the pieces —
    /// the same reach the ridge bridge grants inside one component (<see cref="BridgeDist"/>).</summary>
    public const double JunctionMergeGap = 4.5;
    /// <summary>Largest direction mismatch (deg) between the end pieces of a weldable pair:
    /// two pieces of one straight stroke fit slightly different TLS directions across a
    /// scan gap, but a stroke that merely ENDS beside another one points somewhere else.</summary>
    public const double JunctionMergeAngleDeg = 15.0;

    /// <summary>
    /// Junction pass, first half: weld collinear pieces of one straight stroke back
    /// together across the junctions and scan gaps that split them. A crossing cuts every
    /// stroke it crosses into chains; when two facing pieces are collinear — each facing
    /// endpoint sits on the other piece's line within the width tolerance, and the pieces
    /// point the same way — they are ONE stroke, and the concatenated segments collapse
    /// through <see cref="BezierFitter.MergeCollinearLines"/> into a single 2-anchor line
    /// spanning both (the seam vertex rides the merged chord, gap included). Deterministic:
    /// the same welds happen in the same order as the original restart-after-every-weld
    /// scan — repeatedly the first weldable pair in stroke order — but without its
    /// quadratic rescans: a weld only changes the merged stroke, so the next first pair is
    /// a pair of the merged stroke (tested at once, earlier strokes first) or the next
    /// unscanned pair, and candidates come from a spatial hash on the stroke endpoints
    /// (two strokes can weld only when two endpoints face within
    /// <see cref="JunctionMergeGap"/>).
    /// </summary>
    static void MergeCollinearAcrossJunctions(List<StrokePath> strokes)
    {
        double gapSq = JunctionMergeGap * JunctionMergeGap;
        double minDot = DetMath.Cos(JunctionMergeAngleDeg * Math.PI / 180.0);
        int n = strokes.Count;
        if (n < 2) return;
        var dead = new bool[n];      // welded away; the survivors keep their scan order
        var scanned = new bool[n];   // every pair of this unchanged stroke has been tested
        var grid = new Dictionary<long, List<int>>();
        for (int s = 0; s < n; s++)
        {
            if (strokes[s].Closed) continue;
            AddEndpoint(grid, strokes[s].Segments[0].P0, s);
            AddEndpoint(grid, strokes[s].Segments[^1].P3, s);
        }
        var cand = new List<int>();
        var stamp = new int[n];
        int round = 0;
        for (int i = 0; i < n; i++)
        {
            if (dead[i] || scanned[i] || strokes[i].Closed) continue;
            int j = Partner(i, forwardOnly: true);
            if (j < 0)
            {
                scanned[i] = true;
                continue;
            }
            int m = i;
            while (j >= 0)
            {
                WeldAt(Math.Min(m, j), Math.Max(m, j));
                m = Math.Min(m, j);
                j = Partner(m, forwardOnly: false);
            }
            scanned[m] = true;
            i = m - 1;
        }
        int w = 0;
        for (int r = 0; r < n; r++)
            if (!dead[r]) strokes[w++] = strokes[r];
        strokes.RemoveRange(w, strokes.Count - w);

        // the first weldable pair of stroke s, in scan order (forwardOnly: only pairs
        // (s, t) t > s; otherwise the earlier strokes' pairs (t, s) come first) — or -1
        int Partner(int s, bool forwardOnly)
        {
            round++;
            cand.Clear();
            CollectAround(strokes[s].Segments[0].P0);
            CollectAround(strokes[s].Segments[^1].P3);
            cand.Sort();
            foreach (int t in cand)
            {
                if (forwardOnly && t < s) continue;
                var a = strokes[Math.Min(s, t)];
                var b = strokes[Math.Max(s, t)];
                if (Math.Abs(a.Width - b.Width) > Math.Max(1.0, 0.3 * Math.Min(a.Width, b.Width))) continue;
                if (Weld(a, b) == null) continue;
                return t;
            }
            return -1;

            void CollectAround(Vec2 p)
            {
                int cx = (int)Math.Floor(p.X / JunctionMergeGap), cy = (int)Math.Floor(p.Y / JunctionMergeGap);
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                    for (int gx = cx - 1; gx <= cx + 1; gx++)
                        if (grid.TryGetValue(CellKey(gx, gy), out var list))
                            foreach (int t in list)
                            {
                                if (t == s || dead[t] || stamp[t] == round) continue;
                                stamp[t] = round;
                                cand.Add(t);
                            }
            }
        }

        void WeldAt(int lo, int hi)
        {
            var a = strokes[lo];
            var b = strokes[hi];
            var merged = Weld(a, b)!;
            RemoveEndpoint(grid, a.Segments[0].P0, lo);
            RemoveEndpoint(grid, a.Segments[^1].P3, lo);
            RemoveEndpoint(grid, b.Segments[0].P0, hi);
            RemoveEndpoint(grid, b.Segments[^1].P3, hi);
            strokes[lo] = new StrokePath
            {
                PaletteIndex = a.PaletteIndex,
                Width = Math.Max(a.Width, b.Width),
                Closed = false,
                Segments = merged,
            };
            dead[hi] = true;
            AddEndpoint(grid, merged[0].P0, lo);
            AddEndpoint(grid, merged[^1].P3, lo);
        }

        List<CubicBezier>? Weld(StrokePath a, StrokePath b)
        {
            double tol = LineTol((a.Width + b.Width) / 2);
            double tolSq = tol * tol;
            for (int ra = 0; ra < 2; ra++)
                for (int rb = 0; rb < 2; rb++)
                {
                    // the facing end piece of each stroke, reversed in place — the same
                    // values the reversed-list form tested, without building the lists
                    var xe = ra == 0 ? a.Segments[^1] : a.Segments[0].Reversed();
                    var ye = rb == 0 ? b.Segments[0] : b.Segments[^1].Reversed();
                    if (!xe.IsLine(0.05) || !ye.IsLine(0.05)) continue;
                    if (xe.P3.DistSq(ye.P0) > gapSq) continue;
                    var dx = (xe.P3 - xe.P0).Normalized();
                    var dy = (ye.P3 - ye.P0).Normalized();
                    if (dx.Dot(dy) < minDot) continue;
                    if (PerpDistSqToLine(ye.P0, xe.P0, xe.P3) > tolSq) continue;
                    if (PerpDistSqToLine(xe.P3, ye.P0, ye.P3) > tolSq) continue;
                    var concat = new List<CubicBezier>(a.Segments.Count + b.Segments.Count);
                    if (ra == 0) concat.AddRange(a.Segments); else concat.AddRange(ReversedSegments(a.Segments));
                    if (rb == 0) concat.AddRange(b.Segments); else concat.AddRange(ReversedSegments(b.Segments));
                    int before = concat.Count;
                    BezierFitter.MergeCollinearLines(concat, false, tolSq);
                    if (concat.Count >= before) continue;
                    return concat;
                }
            return null;
        }
    }

    /// <summary>Cell key of the stage-3 spatial hashes.</summary>
    static long CellKey(int x, int y) => ((long)x << 32) | (uint)y;

    static void AddEndpoint(Dictionary<long, List<int>> grid, Vec2 p, int s)
    {
        long key = CellKey((int)Math.Floor(p.X / JunctionMergeGap), (int)Math.Floor(p.Y / JunctionMergeGap));
        if (!grid.TryGetValue(key, out var list)) { list = []; grid[key] = list; }
        list.Add(s);
    }

    static void RemoveEndpoint(Dictionary<long, List<int>> grid, Vec2 p, int s)
    {
        long key = CellKey((int)Math.Floor(p.X / JunctionMergeGap), (int)Math.Floor(p.Y / JunctionMergeGap));
        if (grid.TryGetValue(key, out var list)) list.Remove(s);
    }

    static List<CubicBezier> ReversedSegments(List<CubicBezier> segs)
    {
        var r = new List<CubicBezier>(segs.Count);
        for (int i = segs.Count - 1; i >= 0; i--) r.Add(segs[i].Reversed());
        return r;
    }

    /// <summary>Squared perpendicular distance from <paramref name="p"/> to the infinite
    /// line through <paramref name="a"/> and <paramref name="b"/>.</summary>
    static double PerpDistSqToLine(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSq;
        if (len2 < 1e-12) return p.DistSq(a);
        double t = (p - a).Dot(ab) / len2;
        return p.DistSq(a + ab * t);
    }

    /// <summary>
    /// Junction pass, second half: a straight stroke that ends against another straight
    /// stroke moves its endpoint to the exact intersection of the two fitted lines — a T's
    /// stem lands precisely on the bar it ends against, and the pieces of a crossing that
    /// did not weld (curved or too-short arms) converge on one shared point instead of each
    /// stopping on its own junction pixel. Only straight end pieces move (a curve's
    /// endpoint IS the junction pixel), the intersection must lie on the target stroke, and
    /// the jump is capped at the width tolerance: anything farther would leave the ink.
    /// Candidates come from a spatial hash: only a line passing within the tolerance
    /// (≤ <see cref="FitTolMax"/> px) of the endpoint can win, so each line is indexed in
    /// the cells its segment crosses and each endpoint queries its neighbourhood — the
    /// full pairwise scan is quadratic in the strokes, which huge sheets cannot afford.
    /// </summary>
    static void SnapEndsToIntersections(List<StrokePath> strokes)
    {
        var lines = new List<(Vec2 A, Vec2 Dir, double Len, int Owner)>();
        for (int s = 0; s < strokes.Count; s++)
            foreach (var g in strokes[s].Segments)
            {
                if (!g.IsLine(0.05)) continue;
                var d = g.P3 - g.P0;
                double len = d.Length;
                if (len < 2) continue;
                lines.Add((g.P0, d * (1.0 / len), len, s));
            }
        const double cell = 8.0;
        var grid = new Dictionary<long, List<int>>();
        for (int li = 0; li < lines.Count; li++)
        {
            var (a, dir, len, _) = lines[li];
            int cx = (int)Math.Floor(a.X / cell), cy = (int)Math.Floor(a.Y / cell);
            int ex = (int)Math.Floor((a.X + dir.X * len) / cell), ey = (int)Math.Floor((a.Y + dir.Y * len) / cell);
            int sx = Math.Sign(ex - cx), sy = Math.Sign(ey - cy);
            double tMaxX = sx > 0 ? ((cx + 1) * cell - a.X) / dir.X : sx < 0 ? (cx * cell - a.X) / dir.X : double.PositiveInfinity;
            double tMaxY = sy > 0 ? ((cy + 1) * cell - a.Y) / dir.Y : sy < 0 ? (cy * cell - a.Y) / dir.Y : double.PositiveInfinity;
            double tDX = sx != 0 ? cell / Math.Abs(dir.X) : double.PositiveInfinity;
            double tDY = sy != 0 ? cell / Math.Abs(dir.Y) : double.PositiveInfinity;
            AddCell(cx, cy);
            while (cx != ex || cy != ey)
            {
                if (tMaxX < tMaxY) { cx += sx; tMaxX += tDX; }
                else { cy += sy; tMaxY += tDY; }
                AddCell(cx, cy);
            }

            void AddCell(int x, int y)
            {
                if (!grid.TryGetValue(CellKey(x, y), out var list)) { list = []; grid[CellKey(x, y)] = list; }
                list.Add(li);
            }
        }
        var stamp = new int[lines.Count];
        var cand = new List<int>();
        int round = 0;
        for (int s = 0; s < strokes.Count; s++)
        {
            var stroke = strokes[s];
            if (stroke.Closed) continue;
            double tol = LineTol(stroke.Width);
            SnapEnd(head: true);
            SnapEnd(head: false);

            void SnapEnd(bool head)
            {
                var seg = stroke.Segments[head ? 0 : stroke.Segments.Count - 1];
                if (!seg.IsLine(0.05)) return;
                var p = head ? seg.P0 : seg.P3;
                var inner = head ? seg.P3 : seg.P0;
                var dir = (inner - p).Normalized();
                if (dir.LengthSq < 0.5) return;
                round++;
                cand.Clear();
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell);
                for (int gy = cy - 2; gy <= cy + 2; gy++)
                    for (int gx = cx - 2; gx <= cx + 2; gx++)
                        if (grid.TryGetValue(CellKey(gx, gy), out var list))
                            foreach (int li in list)
                                if (stamp[li] != round)
                                {
                                    stamp[li] = round;
                                    cand.Add(li);
                                }
                cand.Sort();
                bool found = false;
                var best = p;
                double bestD2 = tol * tol;
                foreach (int li in cand)
                {
                    var (a, tdir, len, owner) = lines[li];
                    if (owner == s) continue;
                    double cross = dir.X * tdir.Y - dir.Y * tdir.X;
                    if (Math.Abs(cross) < 1e-9) continue;
                    var dpa = a - p;
                    double u = (dpa.X * tdir.Y - tdir.X * dpa.Y) / cross;
                    double v = (dpa.X * dir.Y - dir.X * dpa.Y) / cross;
                    if (v < -tol || v > len + tol || u < -tol) continue;
                    var q = p + dir * u;
                    double d2 = q.DistSq(p);
                    if (d2 > bestD2) continue;
                    bestD2 = d2;
                    best = q;
                    found = true;
                }
                if (found)
                    stroke.Segments[head ? 0 : stroke.Segments.Count - 1] =
                        head ? CubicBezier.Line(best, inner) : CubicBezier.Line(inner, best);
            }
        }
    }

    /// <summary>
    /// Stage 3 angle rationalization (VEC-52d), the owner's angle-intent model: a measured
    /// angle that lands near a round value IS that round value with scan error. The
    /// straight 2-anchor strokes first CLUSTER — directions that agree within
    /// <see cref="AngleClusterTolDeg"/> (wrap-aware: a 179.999° line IS a 0° line) are one
    /// intended angle, and every member takes the cluster's circular mean, so a drawing's
    /// parallel lines come out exactly parallel. Each cluster angle then snaps to the
    /// <see cref="AngleGridStepDeg"/> grid within <see cref="AngleGridSnapDeg"/>
    /// (0/15/30/45…), and everything else rounds to the nearest WHOLE degree — drawings
    /// dimension in whole degrees, and a genuinely off-grid line keeps its measured whole
    /// degree (17.2° → 17, never 15). Each line is rebuilt at its final angle through the
    /// chord midpoint at the fitted length; the intersection pass afterwards re-lands the
    /// endpoints that end against other lines. Straight single-line strokes only — arcs,
    /// rings and corner-split polylines keep their fitted geometry. Deterministic:
    /// clusters seed at the lowest unused stroke in scan order.
    /// </summary>
    static void RationalizeLineAngles(List<StrokePath> strokes)
    {
        var lines = new List<int>();
        for (int s = 0; s < strokes.Count; s++)
        {
            var st = strokes[s];
            if (st.Closed || st.Segments.Count != 1 || !st.Segments[0].IsLine(0.05)) continue;
            if ((st.Segments[0].P3 - st.Segments[0].P0).LengthSq < 4) continue;
            lines.Add(s);
        }
        if (lines.Count == 0) return;
        var ang = new double[lines.Count];
        for (int k = 0; k < lines.Count; k++)
        {
            var g = strokes[lines[k]].Segments[0];
            ang[k] = LineDirDeg(g.P0, g.P3);
        }
        var used = new bool[lines.Count];
        var final = new double[lines.Count];
        // direction buckets: a cluster candidate is within AngleClusterTolDeg of the
        // seed, so it sits in the seed's 1-degree bucket or a neighbour — the pairwise
        // cluster scan is quadratic in the lines, which huge sheets cannot afford
        int span = Math.Max(1, (int)Math.Ceiling(AngleClusterTolDeg));
        var bucket = new List<int>[180];
        for (int k = 0; k < lines.Count; k++)
        {
            int b = (int)ang[k];
            (bucket[b] ??= []).Add(k);
        }
        var cluster = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            cluster.Clear();
            cluster.Add(i);
            int bi = (int)ang[i];
            for (int db = -span; db <= span; db++)
            {
                var bb = bucket[((bi + db) % 180 + 180) % 180];
                if (bb == null) continue;
                foreach (int j in bb)
                    if (j > i && !used[j] && DirDistDeg(ang[i], ang[j]) <= AngleClusterTolDeg)
                        cluster.Add(j);
            }
            // the original scan collected the members in stroke order; restore it so the
            // circular mean sums the same values in the same order
            cluster.Sort(1, cluster.Count - 1, null);
            foreach (var k in cluster) used[k] = true;
            // circular mean of line directions: double the angles, average, halve
            double x = 0, y = 0;
            foreach (var k in cluster)
            {
                x += Math.Cos(2 * ang[k] * Math.PI / 180.0);
                y += Math.Sin(2 * ang[k] * Math.PI / 180.0);
            }
            double mean = Math.Atan2(y, x) * (180.0 / Math.PI) / 2 % 180.0;
            if (mean < 0) mean += 180.0;
            double grid = Math.Round(mean / AngleGridStepDeg) * AngleGridStepDeg % 180.0;
            double f = DirDistDeg(mean, grid) <= AngleGridSnapDeg ? grid : Math.Round(mean) % 180.0;
            foreach (var k in cluster) final[k] = f;
        }
        for (int k = 0; k < lines.Count; k++)
        {
            var g = strokes[lines[k]].Segments[0];
            double cur = Math.Atan2(g.P3.Y - g.P0.Y, g.P3.X - g.P0.X) * (180.0 / Math.PI);
            // the final angle is a line direction (mod 180): rotate toward the directed
            // target nearest the stroke's current heading, so nothing flips end for end
            double target = Math.Abs(TurnDeg(cur, final[k])) <= Math.Abs(TurnDeg(cur, final[k] + 180.0))
                ? final[k] : final[k] + 180.0;
            double rad = target * Math.PI / 180.0;
            var dir = new Vec2(Math.Cos(rad), Math.Sin(rad));
            var mid = new Vec2((g.P0.X + g.P3.X) / 2, (g.P0.Y + g.P3.Y) / 2);
            double half = (g.P3 - g.P0).Length / 2;
            strokes[lines[k]].Segments[0] = CubicBezier.Line(mid - dir * half, mid + dir * half);
        }
    }

    /// <summary>Direction of the line through a→b as a line angle in [0, 180): the heading
    /// mod 180, so a stroke and its reverse share one direction.</summary>
    static double LineDirDeg(Vec2 a, Vec2 b)
    {
        double deg = Math.Atan2(b.Y - a.Y, b.X - a.X) * (180.0 / Math.PI) % 180.0;
        return deg < 0 ? deg + 180.0 : deg;
    }

    /// <summary>Angular distance between two line directions (mod 180, wrap-aware).</summary>
    static double DirDistDeg(double a, double b)
    {
        double d = Math.Abs(a - b) % 180.0;
        return Math.Min(d, 180.0 - d);
    }

    /// <summary>Signed turn from one heading to another, in (-180, 180].</summary>
    static double TurnDeg(double from, double to)
    {
        double d = (to - from) % 360.0;
        return d < -180.0 ? d + 360.0 : d > 180.0 ? d - 360.0 : d;
    }

    /// <summary>
    /// Stage 3 of the CAD mode: the corner-split chain becomes CLEAN primitives. Chain
    /// splitting reuses the corner machinery — <see cref="CornerPolygon.Corners"/> at the
    /// style's <see cref="EffectiveParams.CornerThresholdDeg"/> (45° turn between
    /// optimal-polygon edges, the same convention the boundary pipeline resolves for
    /// LineArt), so high-curvature points are segment boundaries with sharp corners.
    /// Corners are then relocated to the intersection of their flank lines with
    /// <see cref="CadAngleSnapDeg"/> snapping — the same machinery region edges use — so
    /// axis corners land exactly on both lines. Each run between pins is tested against a
    /// total-least-squares line at the width-derived tolerance: accepted runs emit ONE
    /// 2-anchor path (exactly axis-aligned when snapped); anything else goes to the Bezier
    /// fitter at the same tolerance, whose simplest-model-first order gives circular
    /// strokes the Kåsa arc hypothesis — exact arcs and whole circles.
    /// </summary>
    static List<CubicBezier> FitCadPrimitives(List<Vec2> pts, bool closed, List<int> corners, double width, EffectiveParams p)
    {
        // a corner may leave its pin by at most the line tolerance: centerline strokes are
        // thin, and a corner pushed off the ink paints outside the scan
        ChainGeometry.RelocateCorners(pts, closed, corners, Math.Min(p.CornerRelocateMaxShift, LineTol(width)), CadAngleSnapDeg);
        double tol = LineTol(width);
        double tolSq = Math.Max(StrokeFitTolSq(p), tol * tol);
        int n = closed ? pts.Count - 1 : pts.Count;
        var pins = corners.Distinct().OrderBy(i => i).ToList();
        if (!closed)
        {
            pins.Insert(0, 0);
            pins.Add(n - 1);
            pins = pins.Distinct().OrderBy(i => i).ToList();
        }
        // no corner splits the chain: the whole thing is one hypothesis — a straight stroke
        // becomes one line, anything else goes to the fitter (rings land on its arc path)
        if (pins.Count < 2)
            return BezierFitter.FitChain(pts, closed, [], tolSq);
        // a closed ring with a single corner is still one cyclic run — keep the wrap-around
        // tangent the closed fitter fits at the seam
        if (closed && pins.Count == 1)
            return BezierFitter.FitChain(pts, closed, [], tolSq);

        var segs = new List<CubicBezier>();
        int runs = closed ? pins.Count : pins.Count - 1;
        for (int k = 0; k < runs; k++)
        {
            int a = pins[k];
            int b = k + 1 < pins.Count ? pins[k + 1] : pins[0] + n;
            if (b - a < 2)
            {
                segs.Add(CubicBezier.Line(pts[Wrap(a)], pts[Wrap(b)]));
                continue;
            }
            if (StraightRunLine(pts, closed, a, b, n, tol) is { } line)
            {
                segs.Add(CubicBezier.Line(line.A, line.B));
                continue;
            }
            var sub = new List<Vec2>(b - a + 1);
            for (int i = a; i <= b; i++) sub.Add(pts[Wrap(i)]);
            segs.AddRange(BezierFitter.FitChain(sub, false, [], tolSq));
        }
        return segs;

        int Wrap(int i) => closed ? ((i % n) + n) % n : i;
    }

    /// <summary>
    /// The 2-anchor line for one run between pins, or null when the run is not straight.
    /// Total-least-squares fit of the run's interior (the two vertices beside each pin are
    /// excluded — smoothing damps them, and at a flat tip they are cap geometry); the
    /// direction snaps to a 45° multiple within <see cref="CadAngleSnapDeg"/>, and the run
    /// counts as straight only when every fitted interior point stays within tolerance of
    /// the snapped line AND the residuals show no bow — a curve's signature (the bow gate
    /// sees the full interior, caps included). The endpoints are the pins projected onto
    /// the line, so the emitted chord IS the snapped line: exactly 0/90/45° when it
    /// snapped, and a cap hook at a pin cannot drag it off the stroke.
    /// </summary>
    static (Vec2 A, Vec2 B)? StraightRunLine(List<Vec2> pts, bool closed, int a, int b, int n, double tol)
    {
        var idx = new List<int>();
        for (int i = a + 1; i < b; i++) idx.Add(closed ? i % n : i);
        if (idx.Count < StraightRunMinPoints) return null;
        var fit = idx.Count >= 6 ? idx.GetRange(1, idx.Count - 2) : idx;
        double mx = 0, my = 0;
        foreach (var i in fit) { mx += pts[i].X; my += pts[i].Y; }
        mx /= fit.Count;
        my /= fit.Count;
        double cxx = 0, cxy = 0, cyy = 0;
        foreach (var i in fit)
        {
            double dx = pts[i].X - mx, dy = pts[i].Y - my;
            cxx += dx * dx;
            cxy += dx * dy;
            cyy += dy * dy;
        }
        if (cxx + cyy < 1e-12) return null;
        double theta = 0.5 * DetMath.Atan2(2 * cxy, cxx - cyy);
        double deg = theta * (180.0 / Math.PI);
        double snappedDeg = Math.Round(deg / 45.0) * 45.0;
        if (Math.Abs(deg - snappedDeg) <= CadAngleSnapDeg) theta = snappedDeg * (Math.PI / 180.0);
        var dir = new Vec2(DetMath.Cos(theta), DetMath.Sin(theta));
        var nrm = new Vec2(-dir.Y, dir.X);
        var c = new Vec2(mx, my);

        double s0 = double.MaxValue, s1 = double.MinValue, worst = 0;
        foreach (var i in idx)
        {
            var q = pts[i] - c;
            double s = q.Dot(dir), r = q.Dot(nrm);
            s0 = Math.Min(s0, s);
            s1 = Math.Max(s1, s);
        }
        // the residual gate runs on the same trimmed interior the fit used: the two
        // vertices beside each pin are cap geometry (an edge-hugging hook the ridge leaves
        // at a flat tip), not the stroke's course — the projected endpoints and the round
        // cap cover them. The bow gate below still sees every interior vertex, so a
        // genuinely curved run never passes as a line.
        foreach (var i in fit)
        {
            var q = pts[i] - c;
            worst = Math.Max(worst, Math.Abs(q.Dot(nrm)));
        }
        if (worst > tol) return null;
        double half = (s1 - s0) / 2, mid = (s0 + s1) / 2;
        if (half < 1e-9) return null;
        // bow: fit r = α·u² + β·u + γ with u in [-1, 1]; |α| is the sagitta
        double su0 = 0, su1 = 0, su2 = 0, su3 = 0, su4 = 0, sr0 = 0, sr1 = 0, sr2 = 0;
        foreach (var i in idx)
        {
            var q = pts[i] - c;
            double u = (q.Dot(dir) - mid) / half, r = q.Dot(nrm), u2 = u * u;
            su0 += 1; su1 += u; su2 += u2; su3 += u2 * u; su4 += u2 * u2;
            sr0 += r; sr1 += r * u; sr2 += r * u2;
        }
        double det = su4 * (su2 * su0 - su1 * su1) - su3 * (su3 * su0 - su1 * su2) + su2 * (su3 * su1 - su2 * su2);
        if (Math.Abs(det) < 1e-12) return null;
        double alpha = (sr2 * (su2 * su0 - su1 * su1) - su3 * (sr1 * su0 - su1 * sr0) + su2 * (sr1 * su1 - su2 * sr0)) / det;
        if (Math.Abs(alpha) > StraightRunMaxSag) return null;

        return (Project(pts[Wrap(a)]), Project(pts[Wrap(b)]));

        Vec2 Project(Vec2 p)
        {
            var q = p - c;
            return c + dir * q.Dot(dir);
        }

        int Wrap(int i) => closed ? ((i % n) + n) % n : i;
    }

    /// <summary>
    /// The twin row pixel of an even-width stroke at (x,y): a 4-neighbour that is ink, not
    /// skeleton, with equal DT — and that twin is itself part of a parallel row: it has an
    /// equal-DT ink partner besides (x,y), or equal-DT ink on both sides along the row. The
    /// zigzag a 2-px band's cleanup leaves has skeleton pixels as the twin row's partners,
    /// and the isolated equal-DT ink pixel a diagonal bridge leaves beside a 1-px staircase
    /// has paper on one side — so thin diagonals and curves keep their true width instead of
    /// doubling. Returns -1 when there is no twin.
    /// </summary>
    static int Twin(bool[] skel, bool[] ink, int[] dtSq, int x, int y, int i, int w, int h)
    {
        if (At(x - 1, y, i - 1, out int t)) return t;
        if (At(x + 1, y, i + 1, out t)) return t;
        if (At(x, y - 1, i - w, out t)) return t;
        if (At(x, y + 1, i + w, out t)) return t;
        return -1;

        bool At(int xx, int yy, int q, out int twin)
        {
            twin = q;
            if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return false;
            if (!ink[q] || skel[q] || dtSq[q] != dtSq[i]) return false;
            // the pixel must sit at the NEAR edge of its DT plateau across the band: when the
            // opposite 4-neighbour holds equal DT as well, the pixel is inside a wider plateau
            // (the centre row of a 3-px stroke tip, a corner bridge pixel) and the true centre
            // is the pixel itself — shifting it wobbles the stroke off the ink
            int ox = 2 * x - xx, oy = 2 * y - yy;
            if ((uint)ox < (uint)w && (uint)oy < (uint)h)
            {
                int o = oy * w + ox;
                if (ink[o] && dtSq[o] == dtSq[i]) return false;
            }
            if (RowTwin(xx - 1, yy, q - 1, q) || RowTwin(xx + 1, yy, q + 1, q) || RowTwin(xx, yy - 1, q - w, q) || RowTwin(xx, yy + 1, q + w, q))
                return true;
            // the zigzag twin: the row's own pixels are skeleton here, so demand the band
            // continues on both sides along the row instead
            return yy == y
                ? BandAt(xx, yy - 1, q - w) && BandAt(xx, yy + 1, q + w)
                : BandAt(xx - 1, yy, q - 1) && BandAt(xx + 1, yy, q + 1);
        }

        bool RowTwin(int xx, int yy, int q, int except)
        {
            if ((uint)xx >= (uint)w || (uint)yy >= (uint)h || q == except) return false;
            return ink[q] && !skel[q] && dtSq[q] == dtSq[except];
        }

        bool BandAt(int xx, int yy, int q) =>
            (uint)xx < (uint)w && (uint)yy < (uint)h && ink[q] && dtSq[q] == dtSq[i];
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

    // ---- skeleton ----

    /// <summary>
    /// Medial-axis skeleton: a pixel survives when its squared distance transform is a local
    /// maximum in the 4-neighbourhood (paper neighbours have DT 0, so they never disqualify).
    /// This is the ridge the research doc prescribes, and unlike iterative thinning it cannot
    /// erode a structure away: a 2-px diagonal strip — what diagonal bridging turns a 1-px
    /// diagonal into — keeps its full extent instead of collapsing to a single pixel. The
    /// 8-simple cleanup then flattens the equal-DT plateaus (twin rows of even-width strokes,
    /// staircase strips) down to 8-connected 1-px chains.
    /// </summary>
    public static bool[] Ridge(bool[] ink, int[] dtSq, int w, int h)
    {
        var skel = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!ink[i]) continue;
                int d = dtSq[i];
                if (Ok(i - 1, x > 0, d) && Ok(i + 1, x < w - 1, d) && Ok(i - w, y > 0, d) && Ok(i + w, y < h - 1, d))
                    skel[i] = true;
            }
        Cleanup(skel, w, h);
        return skel;

        bool Ok(int q, bool inside, int d) => !inside || !ink[q] || dtSq[q] <= d;
    }

    /// <summary>
    /// Reduces the raw ridge to 1-px chains. Phase 1 deletes, in raster order, every pixel with
    /// ≥3 skeleton neighbours whose neighbours stay 8-connected without it, repeating until
    /// stable, so plateaus (twin rows of even-width strokes, staircase strips) collapse.
    /// Phase 2 then deletes the third corner of every triangle — a pixel whose exactly two
    /// neighbours are 8-adjacent to each other. Triangle cycles are ridge artifacts at octant
    /// transitions; left in place they read as junctions, and spur pruning then eats the real
    /// stroke arms attached to them. Phase 2 must run after phase 1: inside a plateau every
    /// pixel has two adjacent neighbours, so the triangle rule would cascade down the whole
    /// band. The phases repeat until neither removes anything.
    /// </summary>
    public static void Cleanup(bool[] skel, int w, int h)
    {
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
            bool triangles = true;
            while (triangles)
            {
                triangles = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!skel[i] || Neighbors8(skel, x, y, w, h) != 2 || !Triangle(skel, x, y, w, h)) continue;
                        skel[i] = false;
                        triangles = true;
                    }
            }
        }
    }

    /// <summary>
    /// True when (x,y) has exactly two skeleton neighbours that are 8-adjacent to each other:
    /// the pixel is the third corner of a triangle, so deleting it keeps the graph connected
    /// (the neighbours join directly). Triangle cycles are ridge artifacts at octant
    /// transitions — left in place they read as junctions, and spur pruning then eats the
    /// real stroke arms attached to them.
    /// </summary>
    static bool Triangle(bool[] skel, int x, int y, int w, int h)
    {
        int ax = -1, ay = -1, bx = -1, by = -1;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int xx = x + dx, yy = y + dy;
                if ((uint)xx >= (uint)w || (uint)yy >= (uint)h || !skel[yy * w + xx]) continue;
                if (ax < 0) { ax = xx; ay = yy; }
                else { bx = xx; by = yy; }
            }
        return Math.Abs(ax - bx) <= 1 && Math.Abs(ay - by) <= 1;
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
        int _pruneLen;

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
        /// junction; a branch that ends at another endpoint is a real (short) stroke and stays.
        /// Each round collects every spur against the round-start skeleton and deletes them
        /// together: pruning one-by-one lets the twin fork branch of a stroke end survive
        /// (its junction degrades to degree 2 once the first twin is gone) and weld itself
        /// onto the main chain, drifting the fitted curve off the stroke centre.</summary>
        public void PruneSpurs(List<int> pixels, int pruneLen)
        {
            _pruneLen = pruneLen;
            bool removed = true;
            while (removed)
            {
                removed = false;
                var spurs = new List<List<int>>();
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
                    spurs.Add(path);
                }
                foreach (var path in spurs)
                    for (int k = 0; k < path.Count - 1; k++) skel[path[k]] = false;
                removed = spurs.Count > 0;
            }
        }

        /// <summary>
        /// Reconnects ridge gaps: at a staircase transition the DT local-max test drops the
        /// pixels that link two centre-line runs (a neighbour one step along the true centre
        /// holds strictly greater DT), which shatters rings at their octants and cuts wobbly
        /// scan lines into crumbs. Two dead ends within <paramref name="maxDist"/> whose
        /// connecting pixels are all ink of this component get the missing pixels added.
        /// VEC-52: a wobbly stroke's ridge breaks every few pixels (the DT profile along the
        /// true centre is non-monotonic at each wobble step, so the local-max test leaves
        /// isolated pixels and short runs behind), so the bridge now also treats isolated
        /// ridge pixels (degree 0) as dead ends, iterates until no pair bridges (each pass
        /// re-reads the ends — a bridged degree-0 pixel becomes a pass-through end that
        /// bridges onward), and only bridges along the chains: the bridge bearing must stay
        /// within <see cref="BridgeMaxAngleDeg"/> of the dead end's outward tangent, so a
        /// stroke's flat tip (whose cap chain ends point back INTO the stroke) never welds
        /// shut, and parallel strokes that merely pass close stay apart. A degree-0 end is a
        /// junction remnant (the crossing centre is the ridge's local max while the arms die
        /// around it), so it is never spent: every arm may weld to it in one pass, rebuilding
        /// the junction the tracer then splits at — otherwise the two arms left over weld to
        /// each other diagonally around the crossing corner.
        /// Returns the added pixels so their widths can be measured.
        /// </summary>
        public List<int> BridgeGaps(List<int> pixels, double maxDist)
        {
            var added = new List<int>();
            bool bridged = true;
            while (bridged)
            {
                bridged = false;
                var ends = new List<(int P, int Deg)>();
                foreach (int e in pixels)
                    if (skel[e] && Degree(e) <= 1) ends.Add((e, Degree(e)));
                var endDeg = new Dictionary<int, int>();
                foreach (var (e, d) in ends) endDeg[e] = d;
                var pairs = new List<(double D2, int A, int B, List<int> Line)>();
                for (int i = 0; i < ends.Count; i++)
                    for (int j = i + 1; j < ends.Count; j++)
                    {
                        int a = ends[i].P, b = ends[j].P;
                        int dx = b % w - a % w, dy = b / w - a / w;
                        double d2 = (double)dx * dx + dy * dy;
                        if (d2 > maxDist * maxDist || d2 < 4) continue;   // 8-connected ends need no bridge
                        if (!Aligned(a, b) || !Aligned(b, a)) continue;
                        var line = Connect(a, b);
                        if (line.Count > 0) pairs.Add((d2, a, b, line));
                    }
                pairs.Sort((x, y) => x.D2 != y.D2 ? x.D2.CompareTo(y.D2) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
                var used = new HashSet<int>();
                foreach (var (_, a, b, line) in pairs)
                {
                    if (used.Contains(a) || used.Contains(b)) continue;
                    foreach (int q in line)
                    {
                        skel[q] = true;
                        added.Add(q);
                    }
                    // only a real chain end is spent by a bridge; the degree-0 remnant stays
                    // available for the remaining arms of its junction
                    if (endDeg[a] > 0) used.Add(a);
                    if (endDeg[b] > 0) used.Add(b);
                    bridged = true;
                }
            }
            return added;

            /// <summary>True when the bridge from <paramref name="a"/> to <paramref name="b"/>
            /// continues a's chain: within <see cref="BridgeMaxAngleDeg"/> of the direction
            /// pointing away from a's only skeleton neighbour. Isolated pixels have no
            /// direction and accept any bridge.</summary>
            bool Aligned(int a, int b)
            {
                int nb = Neighbor(a, -1);
                if (nb < 0) return true;
                double ax = a % w - nb % w, ay = a / w - nb / w;
                double bx = b % w - a % w, by = b / w - a / w;
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                if (la < 1e-9 || lb < 1e-9) return true;
                return (ax * bx + ay * by) / (la * lb) >= DetMath.Cos(BridgeMaxAngleDeg * Math.PI / 180.0);
            }

            List<int> Connect(int a, int b)
            {
                int ax = a % w, ay = a / w, bx = b % w, by = b / w;
                var line = new List<int>();
                int dx = Math.Abs(bx - ax), dy = Math.Abs(by - ay);
                int sx = bx >= ax ? 1 : -1, sy = by >= ay ? 1 : -1;
                int x = ax, y = ay, err = dx - dy;
                while (x != bx || y != by)
                {
                    int e2 = 2 * err;
                    if (e2 > -dy) { err -= dy; x += sx; }
                    if (e2 < dx) { err += dx; y += sy; }
                    if (x == bx && y == by) break;
                    int q = y * w + x;
                    if (comp[q] != id) return [];
                    line.Add(q);
                }
                // an all-skeleton line bridges nothing (two ends of one run) and would only
                // burn the ends so no real gap could close afterwards
                foreach (int q in line)
                    if (!skel[q]) return line;
                return [];
            }
        }

        /// <summary>
        /// Grows an open chain into the ink the ridge missed at its ends: towards a stroke
        /// tip the DT shrinks while a neighbour along the line still holds the high ground,
        /// so the local-max test drops the last pixel or two and the fitted curve would stop
        /// short of the ink. On a wobbly stroke the drop is wider (a whole wobble step can
        /// fall out), so the growth limit scales with the image's median stroke width.
        /// Only true dead ends (skeleton degree 1) grow — junction ends keep their
        /// crossing. Growth stops at the first paper pixel, the image edge, or a junction
        /// (skeleton degree ≥ 3); a plain skeleton pixel (degree ≤ 2) is ABSORBED — the U a
        /// flat cap leaves in the ridge is the stroke's own tip geometry, and absorbing it
        /// lets the chain grow through to the true stroke end (the cap-crumb pass then drops
        /// the leftover fragment, covered by the grown tip's round cap).
        /// </summary>
        public void Extend(List<int> chain, int maxGrow)
        {
            if (chain.Count < 2) return;
            Grow(atEnd: false);
            Grow(atEnd: true);

            void Grow(bool atEnd)
            {
                int p = atEnd ? chain[^1] : chain[0];
                int q = atEnd ? chain[^2] : chain[1];
                if (Degree(p) != 1) return;
                int sx = Math.Sign(p % w - q % w), sy = Math.Sign(p / w - q / w);
                int x = p % w, y = p / w;
                var grown = new List<int>();
                while (grown.Count < maxGrow)
                {
                    x += sx; y += sy;
                    if ((uint)x >= (uint)w || (uint)y >= (uint)h) break;
                    int c = y * w + x;
                    if (comp[c] != id) break;
                    if (skel[c] && Degree(c) >= 3) break;
                    grown.Add(c);
                }
                if (atEnd) chain.AddRange(grown);
                else
                {
                    grown.Reverse();
                    chain.InsertRange(0, grown);
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
                    var chain = Walk(j, n);
                    // a walk that comes straight back to its own junction is a micro-cycle
                    // artifact (the end-of-stroke bridge triangle), not a real ring: drop it
                    // when it is spur-sized — a genuine lollipop ring is far longer
                    if (chain[^1] == j && chain.Count - 1 < _pruneLen) continue;
                    chains.Add((chain, false));
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
