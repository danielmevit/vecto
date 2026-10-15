using System.Diagnostics;

namespace Vecto.Core;

public sealed class PaletteEntry
{
    public Rgba32 Color { get; init; }
    public int PixelCount { get; init; }
}

public sealed class RegionPath
{
    public int PaletteIndex { get; init; }
    /// <summary>Exact pixel count of the region.</summary>
    public int Area { get; init; }
    /// <summary>Outer boundary + holes; holes wind opposite, so nonzero fill renders correctly.</summary>
    public required List<List<CubicBezier>> Loops { get; init; }
}

public sealed class VectorDocument
{
    public int Width { get; init; }
    public int Height { get; init; }
    public required List<PaletteEntry> Palette { get; init; }
    /// <summary>Largest area first, so backgrounds paint below details regardless of renderer.</summary>
    public required List<RegionPath> Regions { get; init; }
}

public sealed class TraceDiagnostics
{
    public required EffectiveParams Params { get; init; }
    public int RegionCount { get; init; }
    public int ChainCount { get; init; }
    public int NodeCount { get; init; }
    public long PaletteMs { get; init; }
    public long SegmentMs { get; init; }
    public long BoundaryMs { get; init; }
    public long FitMs { get; init; }
    public long TotalMs { get; set; }
}

public sealed class TraceResult
{
    public required VectorDocument Document { get; init; }
    /// <summary>Palette index per pixel (-1 transparent) — feeds the segmentation view.</summary>
    public required int[] LabelMap { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public required TraceDiagnostics Diagnostics { get; init; }
}

/// <summary>
/// The pipeline: palette → label map → small-region absorption → planar boundary graph →
/// per-chain corner detection, smoothing, Bezier fitting → document assembly.
/// Every stage is deterministic for a given input + options.
/// </summary>
public static class Tracer
{
    public static TraceResult Trace(RasterImage image, TraceOptions options, CancellationToken ct = default, Action<string, double>? progress = null)
    {
        var total = Stopwatch.StartNew();
        var p = Resolve(options, image);
        var img = p.MedianPrefilter ? Preprocess.Median3x3(image, ct) : image;
        int w = img.Width, h = img.Height;

        progress?.Invoke("palette", 0.0);
        var sw = Stopwatch.StartNew();
        var centers = PaletteBuilder.Build(img, p, ct, out var tints);
        long paletteMs = sw.ElapsedMilliseconds;

        progress?.Invoke("segmentation", 0.25);
        sw.Restart();
        var labels = Segmenter.Label(img, centers, ct);
        // Texture guard: on continuous-tone inputs (photos, textured art, old scans) the thin
        // in-between colour bands are real shading, not anti-aliasing fringes, and a high
        // interior noise share is texture, not JPEG ringing — skip both treatments there.
        bool rampLines = false;
        double preShare = NoiseEstimate.InteriorNoiseShare(img, labels, w, h, ct);
        p.Textured = preShare >= NoiseEstimate.TexturedShare;
        if (!p.ExactPalette && !p.Textured)
        {
            var pre = preShare < NoiseEstimate.NoisyShare ? (int[])labels.Clone() : null;
            Segmenter.RelabelFringe(img, labels, centers, w, h, ct);
            if (pre != null) rampLines = KeepRampLines(labels, pre, centers);
        }
        int opaque = 0;
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] >= 0) opaque++;
        int minArea = Math.Min(p.MinRegionArea, Math.Max(1, opaque / 4));
        Segmenter.AbsorbSmallRegions(labels, w, h, minArea, ct);
        var palette = CompactPalette(img, labels, centers.Length);
        if (!p.ExactPalette)
            palette = MergeSimilarDisplays(img, labels, palette, tints, w, h, minArea, ct);
        p.NoiseShare = NoiseEstimate.InteriorNoiseShare(img, labels, w, h, ct);
        // The share alone also fires on downscaled text, blurred shadows and dithered gradients;
        // only JPEG ringing fills the 8x8 blocks around edges, so that confirms it
        if (!p.Textured && p.NoiseShare >= NoiseEstimate.NoisyShare
            && NoiseEstimate.EdgeBlockRoughness(img, labels, w, h, ct) >= NoiseEstimate.JpegEdgeBlockShare)
        {
            p.Noisy = true;
            p.SmoothIterations = 32;
            p.SmoothClamp = 1.5;

            // JPEG ringing leaves 6-13 px blobs along edges: clean them up like Detail=Low does
            int noisyMin = Math.Min(Math.Max(p.MinRegionArea, NoiseEstimate.NoisyMinRegionArea), Math.Max(1, opaque / 4));
            if (noisyMin > minArea)
            {
                Segmenter.AbsorbSmallRegions(labels, w, h, noisyMin, ct);
                palette = CompactPalette(img, labels, palette.Count);
                p.MinRegionArea = noisyMin;
            }
        }
        var seg = Segmenter.Components(labels, w, h);
        long segmentMs = sw.ElapsedMilliseconds;

        progress?.Invoke("boundaries", 0.45);
        sw.Restart();
        var graph = new BoundaryTracer(seg.RegionId, w, h, seg.RegionCount).Run(ct);
        long boundaryMs = sw.ElapsedMilliseconds;

        progress?.Invoke("curves", 0.6);
        sw.Restart();
        Rgba32? ColorOf(int region) => region < 0 ? null : palette[seg.RegionPalette[region]].Color;
        // kept ramp lines are 1-2 px wide and low-contrast: sub-pixel refinement can't hold
        // their staircase, so let the smoothing move them further
        double smoothClamp = rampLines ? Math.Max(p.SmoothClamp, RampSmoothClamp) : p.SmoothClamp;
        Parallel.ForEach(graph.Chains, new ParallelOptions { CancellationToken = ct }, chain =>
        {
            if (options.CurveFitting)
            {
                // structural corners: optimal polygon of the lattice chain, then a per-vertex
                // turn test on its own two edges (CornerPolygon — Potrace-style)
                var poly = CornerPolygon.OptimalPolygon(chain.Points, chain.Closed);
                var corners = CornerPolygon.Corners(chain.Points, chain.Closed, poly, p.CornerThresholdDeg);
                var raw = corners.Count > 0 ? new List<Vec2>(chain.Points) : null;
                Process(corners);
                if (corners.Count > 0)
                {
                    // corner validation: re-measure on the refined sub-pixel geometry, where
                    // staircase quantization spikes are gone. False corners (small circles!)
                    // would pin lattice dents and block the whole-ring arc fit.
                    var kept = corners
                        .Where(c => CornerPolygon.Validate(chain.Points, chain.Closed, corners, c, p.CornerKeepDeg,
                            img, ColorOf(chain.Left), ColorOf(chain.Right)))
                        .ToList();
                    if (kept.Count != corners.Count)
                    {
                        chain.Points.Clear();
                        chain.Points.AddRange(raw!);
                        corners = kept;
                        Process(corners);
                    }
                    if (p.Noisy)
                        ChainGeometry.StraightenRuns(chain.Points, chain.Closed, corners);
                    // pinned lattice corners tilt their adjacent lines by up to ~0.5 px;
                    // move each to the intersection of its refined flank lines instead
                    ChainGeometry.RelocateCorners(chain.Points, chain.Closed, corners,
                        p.CornerRelocateMaxShift, p.AngleSnapDeg);
                }
                chain.Curve = BezierFitter.FitChain(chain.Points, chain.Closed, corners, p.FitToleranceSq);

                void Process(List<int> pins)
                {
                    ChainGeometry.SmoothInPlace(chain.Points, chain.Closed, pins, p.SmoothIterations, p.SmoothLambda, smoothClamp);
                    ChainGeometry.SubpixelRefine(chain.Points, chain.Closed, pins, img,
                        ColorOf(chain.Left), ColorOf(chain.Right), p.SubpixelMaxShift);
                    // light second pass: per-point refinement estimates jitter; smoothing them
                    // (clamped to the refined positions) costs <0.25px but many fewer nodes
                    ChainGeometry.SmoothInPlace(chain.Points, chain.Closed, pins, 3, 0.5, 0.25);
                }
            }
            else
            {
                var pts = ChainGeometry.SimplifyDp(chain.Points, p.PolygonEpsilon);
                var segs = new List<CubicBezier>(pts.Count - 1);
                for (int i = 0; i + 1 < pts.Count; i++) segs.Add(CubicBezier.Line(pts[i], pts[i + 1]));
                chain.Curve = segs;
            }
        });
        long fitMs = sw.ElapsedMilliseconds;

        progress?.Invoke("emit", 0.9);
        var regionOrder = Enumerable.Range(0, seg.RegionCount)
            .OrderByDescending(r => seg.RegionArea[r])
            .ToList();
        var regions = new List<RegionPath>(seg.RegionCount);
        int nodes = 0;
        foreach (var r in regionOrder)
        {
            var loops = graph.RegionLoops[r].Select(loop => BuildLoop(loop, graph.Chains)).ToList();
            nodes += loops.Sum(l => l.Count);
            regions.Add(new RegionPath
            {
                PaletteIndex = seg.RegionPalette[r],
                Area = seg.RegionArea[r],
                Loops = loops,
            });
        }
        var doc = new VectorDocument { Width = w, Height = h, Palette = palette, Regions = regions };
        var diag = new TraceDiagnostics
        {
            Params = p,
            RegionCount = seg.RegionCount,
            ChainCount = graph.Chains.Count,
            NodeCount = nodes,
            PaletteMs = paletteMs,
            SegmentMs = segmentMs,
            BoundaryMs = boundaryMs,
            FitMs = fitMs,
        };
        diag.TotalMs = total.ElapsedMilliseconds;
        return new TraceResult { Document = doc, LabelMap = labels, Width = w, Height = h, Diagnostics = diag };
    }

    static List<CubicBezier> BuildLoop(List<ChainUse> loop, List<Chain> chains)
    {
        var segs = new List<CubicBezier>();
        foreach (var use in loop)
        {
            var curve = chains[use.Chain].Curve!;
            if (use.Forward)
            {
                segs.AddRange(curve);
            }
            else
            {
                for (int i = curve.Count - 1; i >= 0; i--) segs.Add(curve[i].Reversed());
            }
        }
        return segs;
    }

    /// <summary>
    /// JPEG ringing can split one source colour into two palette entries whose DISPLAY
    /// colours are near-identical (e.g. #020202 and #040404): the ringing cluster sits
    /// farther than MergeThreshold from black, so k-means keeps it, and CompactPalette
    /// then averages both entries back to the source colour. Nothing merges them after
    /// that, so every flat area becomes a mosaic of near-identical regions. Here entries
    /// whose display colours are within <see cref="DisplayMergeThreshold"/> Oklab are
    /// united (smaller into larger), leftover specks are absorbed, and the compact
    /// palette is recomputed. Exact palettes are verbatim by design and skip this, and
    /// entries anchored by two distinct large flat light tints stay apart (PaletteBuilder).
    /// </summary>
    const double DisplayMergeThreshold = 0.05;

    const double RampStep = 0.1; // Oklab; palette neighbours this close form a low-contrast ramp
    const double RampSmoothClamp = 1.0; // px

    /// <summary>
    /// Undoes fringe moves inside a low-contrast ramp: on small greyscale line art a colour
    /// with near neighbours on both sides (white / light line / mid grey) is a thin line of
    /// its own, not anti-aliasing — relabelling it to a neighbour erases the line. Clean
    /// inputs only (the caller skips this when JPEG ringing builds such ramps).
    /// Returns whether any line was kept.
    /// </summary>
    static bool KeepRampLines(int[] labels, int[] pre, Lab[] centers)
    {
        bool kept = false;
        int n = centers.Length;
        double t2 = RampStep * RampStep;
        var near = new int[n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                if (a != b && Oklab.DistSq(centers[a], centers[b]) <= t2) near[a]++;
        for (int i = 0; i < labels.Length; i++)
        {
            int from = pre[i], to = labels[i];
            if (to != from && near[from] >= 2 && Oklab.DistSq(centers[from], centers[to]) <= t2)
            {
                labels[i] = from;
                kept = true;
            }
        }
        return kept;
    }

    static List<PaletteEntry> MergeSimilarDisplays(
        RasterImage img, int[] labels, List<PaletteEntry> palette, Lab[] tints, int w, int h, int minArea, CancellationToken ct)
    {
        int n = palette.Count;
        if (n < 2) return palette;
        var lab = new Lab[n];
        for (int c = 0; c < n; c++)
            lab[c] = Oklab.FromRgb(palette[c].Color.R, palette[c].Color.G, palette[c].Color.B);
        var anchor = PaletteBuilder.AnchorTints(lab, tints);
        double t2 = DisplayMergeThreshold * DisplayMergeThreshold;
        var parent = new int[n];
        var size = new long[n];
        for (int c = 0; c < n; c++) { parent[c] = c; size[c] = palette[c].PixelCount; }
        int Root(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        bool merged = false;
        var apart = new bool[n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Oklab.DistSq(lab[i], lab[j]) <= t2)
                {
                    int ri = Root(i), rj = Root(j);
                    if (ri == rj) continue;
                    if (PaletteBuilder.TintsApart(anchor[ri], anchor[rj], tints)) { apart[ri] = apart[rj] = true; continue; }
                    // a set keeps its heaviest (lowest-index) tint, so no chain of near
                    // neighbours can unite two distinct tints
                    int keep = anchor[ri] < 0 ? anchor[rj] : anchor[rj] < 0 ? anchor[ri] : Math.Min(anchor[ri], anchor[rj]);
                    if (size[ri] >= size[rj]) { parent[rj] = ri; size[ri] += size[rj]; anchor[ri] = keep; }
                    else { parent[ri] = rj; size[rj] += size[ri]; anchor[rj] = keep; }
                    merged = true;
                }
        // sets an anchor kept apart from a near colour they would otherwise have merged with
        var kept = new bool[n];
        bool anyKept = false;
        for (int c = 0; c < n; c++)
            if (apart[c]) { kept[Root(c)] = true; anyKept = true; }
        if (!merged && !anyKept) return palette;
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] >= 0) labels[i] = Root(labels[i]);
        if (anyKept) AbsorbTintSpecks(labels, kept, w, h, ct);
        Segmenter.AbsorbSmallRegions(labels, w, h, minArea, ct);
        return CompactPalette(img, labels, n);
    }

    /// <summary>
    /// The anchor keeps a large flat tint apart, but anti-aliasing pixels along other edges
    /// that land on the tint colour (pale blue beside an ink outline on white) would keep it
    /// too, as specks. Components of a kept set under
    /// <see cref="NoiseEstimate.NoisyMinRegionArea"/> pixels go to the neighbour they share the
    /// longest border with, as their colour merged away before tints were anchored.
    /// </summary>
    static void AbsorbTintSpecks(int[] labels, bool[] kept, int w, int h, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var seg = Segmenter.Components(labels, w, h);
        int rc = seg.RegionCount;
        var speck = new bool[rc];
        for (int r = 0; r < rc; r++)
            speck[r] = seg.RegionArea[r] < NoiseEstimate.NoisyMinRegionArea && kept[seg.RegionPalette[r]];
        var border = new Dictionary<(int Speck, int Label), int>();
        for (int i = 0; i < labels.Length; i++)
        {
            int r = seg.RegionId[i];
            if (r < 0 || !speck[r]) continue;
            int x = i % w, y = i / w;
            if (x > 0) Tally(i - 1);
            if (x < w - 1) Tally(i + 1);
            if (y > 0) Tally(i - w);
            if (y < h - 1) Tally(i + w);
            void Tally(int j)
            {
                if (seg.RegionId[j] == r || labels[j] < 0) return;
                var key = (r, labels[j]);
                border[key] = border.GetValueOrDefault(key) + 1;
            }
        }
        // longest border wins, ties to the lower label, so the result is order-free
        var target = new int[rc];
        Array.Fill(target, -1);
        var best = new int[rc];
        foreach (var ((r, l), len) in border)
            if (len > best[r] || (len == best[r] && l < target[r])) { best[r] = len; target[r] = l; }
        for (int i = 0; i < labels.Length; i++)
        {
            int r = seg.RegionId[i];
            if (r >= 0 && speck[r] && target[r] >= 0) labels[i] = target[r];
        }
    }

    /// <summary>
    /// Drops empty palette entries and computes display colors. Display colors average only
    /// flat (non-edge) pixels when a cluster has enough of them, so anti-aliasing fringes
    /// can't tint the fill — a pure white area stays #ffffff, like Vector Magic's output.
    /// </summary>
    static List<PaletteEntry> CompactPalette(RasterImage img, int[] labels, int centerCount)
    {
        if (centerCount == 0) return new List<PaletteEntry>();
        int w = img.Width;
        var sum = new long[centerCount, 3];
        var flatSum = new long[centerCount, 3];
        var count = new int[centerCount];
        var flatCount = new int[centerCount];
        var px = img.Pixels;
        for (int i = 0; i < labels.Length; i++)
        {
            int l = labels[i];
            if (l < 0) continue;
            int pi = i * 4;
            sum[l, 0] += px[pi];
            sum[l, 1] += px[pi + 1];
            sum[l, 2] += px[pi + 2];
            count[l]++;
            if (!PaletteBuilder.IsEdge(img, i % w, i / w))
            {
                flatSum[l, 0] += px[pi];
                flatSum[l, 1] += px[pi + 1];
                flatSum[l, 2] += px[pi + 2];
                flatCount[l]++;
            }
        }
        var map = new int[centerCount];
        var entries = new List<PaletteEntry>();
        for (int c = 0; c < centerCount; c++)
        {
            if (count[c] == 0)
            {
                map[c] = -1;
                continue;
            }
            bool useFlat = flatCount[c] >= Math.Max(1, count[c] / 50);
            long n = useFlat ? flatCount[c] : count[c];
            var src = useFlat ? flatSum : sum;
            map[c] = entries.Count;
            entries.Add(new PaletteEntry
            {
                Color = new Rgba32(
                    (byte)(src[c, 0] / n),
                    (byte)(src[c, 1] / n),
                    (byte)(src[c, 2] / n),
                    255),
                PixelCount = count[c],
            });
        }
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] >= 0) labels[i] = map[labels[i]];
        return entries;
    }

    static EffectiveParams Resolve(TraceOptions o, RasterImage img)
    {
        int unique = CountUniqueOpaque(img, 1025);
        var style = o.Style != ImageStyle.Auto ? o.Style
            : unique <= 64 ? ImageStyle.Crisp
            : ImageStyle.Blended;
        int cap = Math.Clamp(o.PaletteMode == PaletteMode.FixedCount ? o.ColorCount : o.MaxColors, 1, 64);
        bool exact = style == ImageStyle.Crisp && unique <= cap;
        var (minLow, minMed, minHigh) = style switch
        {
            ImageStyle.Photo => (24, 12, 6),
            ImageStyle.Crisp => (10, 5, 2),
            _ => (14, 6, 3),
        };
        return new EffectiveParams
        {
            Style = style,
            ExactPalette = exact,
            ExcludeEdgeSamples = style is ImageStyle.Blended or ImageStyle.Photo,
            KMeansK = cap,
            MergeClusters = o.PaletteMode == PaletteMode.Auto && !exact,
            MergeThreshold = style == ImageStyle.Photo ? 0.03 : 0.055,
            MinRegionArea = o.Detail switch { DetailLevel.Low => minLow, DetailLevel.High => minHigh, _ => minMed },
            MedianPrefilter = style == ImageStyle.Photo,
            SmoothIterations = 8,
            SmoothLambda = 0.55,
            SmoothClamp = 0.6,
            CornerThresholdDeg = 45,
            CornerKeepDeg = 55,
            SubpixelMaxShift = style == ImageStyle.Crisp ? 0.35 : 0.75,
            CornerRelocateMaxShift = 1.5,
            AngleSnapDeg = 1.0,
            FitToleranceSq = o.Detail switch { DetailLevel.Low => 1.0, DetailLevel.High => 0.09, _ => 0.25 },
            PolygonEpsilon = o.PolygonEpsilon >= 0 ? o.PolygonEpsilon
                : o.Detail switch { DetailLevel.Low => 1.6, DetailLevel.High => 0.4, _ => 0.8 },
            Seed = o.Seed,
        };
    }

    static int CountUniqueOpaque(RasterImage img, int cap)
    {
        var set = new HashSet<uint>();
        var px = img.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < Segmenter.OpaqueThreshold) continue;
            set.Add((uint)(px[i] << 16 | px[i + 1] << 8 | px[i + 2]));
            if (set.Count >= cap) break;
        }
        return set.Count;
    }
}
