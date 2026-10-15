namespace Vecto.Core;

/// <summary>
/// Palette inference: exact unique colors for small crisp palettes, otherwise
/// k-means++ in Oklab over (optionally edge-excluded) samples, followed by an
/// agglomerative merge of perceptually close clusters in Auto mode.
/// </summary>
internal static class PaletteBuilder
{
    const int TargetSamples = 120_000;
    const int EdgeChannelDelta = 24;

    /// <summary>
    /// Light tints: Oklab packs the near-white colours tight (cream #fff6e8, white and pale
    /// blue #f1f6ff are 0.031-0.034 apart), so the merge thresholds (0.055 cluster merge,
    /// 0.05 display merge) melt distinct pastel fills into one near-white. A large flat
    /// light fill — one exact colour holding at least <see cref="TintShare"/> of the sampled
    /// pixels, lightness at least <see cref="TintMinL"/> — is a deliberate colour: two such
    /// tints at least <see cref="TintApart"/> apart keep their own palette entries. JPEG
    /// ringing and anti-aliasing never pile that many pixels on one exact colour (and edge
    /// pixels are not sampled), and dark noise is excluded by the lightness gate: near
    /// black a few RGB levels already span more than <see cref="TintApart"/>.
    /// </summary>
    const double TintShare = 0.005;
    const int TintMinSamples = 64;
    const float TintMinL = 0.85f;
    const double TintApart = 0.025;

    public static Lab[] Build(RasterImage img, EffectiveParams p, CancellationToken ct, out Lab[] tints)
    {
        tints = Array.Empty<Lab>();
        if (p.ExactPalette)
        {
            var exact = TryExact(img, p.KMeansK);
            if (exact != null) return exact;
        }
        var samples = CollectSamples(img, p.ExcludeEdgeSamples, ct);
        if (samples.Count == 0) return Array.Empty<Lab>();
        tints = LightTints(samples);
        var centers = KMeans(samples, Math.Min(p.KMeansK, samples.Count), p.Seed, ct, out var weights);
        if (p.MergeClusters) centers = MergeClose(centers, weights, p.MergeThreshold, tints);
        return centers;
    }

    /// <summary>Exact light colours that fill a large flat area, heaviest first.</summary>
    static Lab[] LightTints(List<(Lab Color, long Weight)> samples)
    {
        long total = 0;
        foreach (var s in samples) total += s.Weight;
        long min = Math.Max(TintMinSamples, (long)(total * TintShare));
        return samples
            .Where(s => s.Weight >= min && s.Color.L >= TintMinL)
            .OrderByDescending(s => s.Weight)
            .Select(s => s.Color)
            .ToArray();
    }

    /// <summary>
    /// Per colour, the index of the heaviest light tint (<paramref name="tints"/> is heaviest
    /// first) that lies within <see cref="TintApart"/> and nearest to it, or -1.
    /// </summary>
    internal static int[] AnchorTints(IReadOnlyList<Lab> colors, Lab[] tints)
    {
        var anchor = new int[colors.Count];
        Array.Fill(anchor, -1);
        double t2 = TintApart * TintApart;
        for (int t = tints.Length - 1; t >= 0; t--)
        {
            int best = -1;
            double bd = t2;
            for (int c = 0; c < colors.Count; c++)
            {
                double d = Oklab.DistSq(colors[c], tints[t]);
                if (d <= bd) { bd = d; best = c; }
            }
            if (best >= 0) anchor[best] = t; // heavier tints come later and win
        }
        return anchor;
    }

    /// <summary>Two colours anchored by distinct light tints must not merge.</summary>
    internal static bool TintsApart(int ta, int tb, Lab[] tints) =>
        ta >= 0 && tb >= 0 && Oklab.DistSq(tints[ta], tints[tb]) >= TintApart * TintApart;

    static Lab[]? TryExact(RasterImage img, int cap)
    {
        var seen = new HashSet<uint>();
        var order = new List<uint>();
        var px = img.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < Segmenter.OpaqueThreshold) continue;
            uint key = (uint)(px[i] << 16 | px[i + 1] << 8 | px[i + 2]);
            if (seen.Add(key))
            {
                order.Add(key);
                if (order.Count > cap) return null;
            }
        }
        return order
            .Select(k => Oklab.FromRgb((byte)(k >> 16), (byte)(k >> 8), (byte)k))
            .ToArray();
    }

    /// <summary>Unique colors with occurrence weights — collapses flat artwork to a handful of points.</summary>
    static List<(Lab Color, long Weight)> CollectSamples(RasterImage img, bool excludeEdges, CancellationToken ct)
    {
        int w = img.Width, h = img.Height;
        var px = img.Pixels;
        int opaque = 0, flat = 0;
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] < Segmenter.OpaqueThreshold) continue;
                opaque++;
                if (excludeEdges && !IsEdge(img, x, y)) flat++;
            }
        }
        // if almost everything is gradient (photos), edge exclusion would starve the sampler
        bool useFlatOnly = excludeEdges && flat >= opaque / 4;
        int eligible = useFlatOnly ? flat : opaque;
        if (eligible == 0) return new List<(Lab, long)>();
        int stride = Math.Max(1, eligible / TargetSamples);
        var histogram = new Dictionary<uint, long>();
        int n = 0;
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] < Segmenter.OpaqueThreshold) continue;
                if (useFlatOnly && IsEdge(img, x, y)) continue;
                if (n++ % stride != 0) continue;
                uint key = (uint)(px[i] << 16 | px[i + 1] << 8 | px[i + 2]);
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }
        var samples = new List<(Lab, long)>(histogram.Count);
        foreach (var (key, count) in histogram)
            samples.Add((Oklab.FromRgb((byte)(key >> 16), (byte)(key >> 8), (byte)key), count));
        return samples;
    }

    internal static bool IsEdge(RasterImage img, int x, int y)
    {
        int w = img.Width, h = img.Height;
        var px = img.Pixels;
        int i = (y * w + x) * 4;
        int r = px[i], g = px[i + 1], b = px[i + 2];
        Span<int> nx = stackalloc int[4] { x - 1, x + 1, x, x };
        Span<int> ny = stackalloc int[4] { y, y, y - 1, y + 1 };
        for (int k = 0; k < 4; k++)
        {
            if ((uint)nx[k] >= (uint)w || (uint)ny[k] >= (uint)h) continue;
            int j = (ny[k] * w + nx[k]) * 4;
            if (Math.Abs(px[j] - r) > EdgeChannelDelta ||
                Math.Abs(px[j + 1] - g) > EdgeChannelDelta ||
                Math.Abs(px[j + 2] - b) > EdgeChannelDelta)
                return true;
        }
        return false;
    }

    static Lab[] KMeans(List<(Lab Color, long Weight)> pts, int k, int seed, CancellationToken ct, out long[] weights)
    {
        var rng = new XorShift(seed);
        var centers = new Lab[k];
        var minDist = new float[pts.Count];

        // k-means++ seeding (weight-aware roulette)
        centers[0] = pts[(int)(rng.NextDouble() * pts.Count)].Color;
        for (int i = 0; i < pts.Count; i++) minDist[i] = Oklab.DistSq(pts[i].Color, centers[0]);
        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int i = 0; i < pts.Count; i++) total += minDist[i] * pts[i].Weight;
            double r = rng.NextDouble() * total;
            int pick = pts.Count - 1;
            double acc = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                acc += minDist[i] * pts[i].Weight;
                if (acc >= r) { pick = i; break; }
            }
            centers[c] = pts[pick].Color;
            for (int i = 0; i < pts.Count; i++)
                minDist[i] = Math.Min(minDist[i], Oklab.DistSq(pts[i].Color, centers[c]));
        }

        // Lloyd iterations
        var assign = new int[pts.Count];
        Array.Fill(assign, -1);
        weights = new long[k];
        var sumL = new double[k];
        var sumA = new double[k];
        var sumB = new double[k];
        for (int iter = 0; iter < 48; iter++)
        {
            ct.ThrowIfCancellationRequested();
            bool changed = false;
            Array.Clear(weights);
            Array.Clear(sumL);
            Array.Clear(sumA);
            Array.Clear(sumB);
            int farIdx = 0;
            float farDist = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                var (lab, wgt) = pts[i];
                int best = 0;
                float bd = float.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    float d = Oklab.DistSq(lab, centers[c]);
                    if (d < bd) { bd = d; best = c; }
                }
                if (assign[i] != best) { assign[i] = best; changed = true; }
                weights[best] += wgt;
                sumL[best] += lab.L * wgt;
                sumA[best] += lab.A * wgt;
                sumB[best] += lab.B * wgt;
                if (bd > farDist) { farDist = bd; farIdx = i; }
            }
            for (int c = 0; c < k; c++)
            {
                if (weights[c] == 0)
                {
                    centers[c] = pts[farIdx].Color;
                    changed = true;
                    continue;
                }
                centers[c] = new Lab(
                    (float)(sumL[c] / weights[c]),
                    (float)(sumA[c] / weights[c]),
                    (float)(sumB[c] / weights[c]));
            }
            if (!changed) break;
        }
        return centers;
    }

    static Lab[] MergeClose(Lab[] centers, long[] counts, double threshold, Lab[] tints)
    {
        var labs = centers.ToList();
        var weights = counts.Select(c => (double)Math.Max(c, 1)).ToList();
        var anchors = AnchorTints(labs, tints).ToList();
        double t2 = threshold * threshold;
        while (labs.Count > 1)
        {
            double best = double.MaxValue;
            int bi = -1, bj = -1;
            for (int i = 0; i < labs.Count; i++)
                for (int j = i + 1; j < labs.Count; j++)
                {
                    if (TintsApart(anchors[i], anchors[j], tints)) continue;
                    double d = Oklab.DistSq(labs[i], labs[j]);
                    if (d < best) { best = d; bi = i; bj = j; }
                }
            if (best > t2) break;
            // the merged cluster keeps the heavier (lower-index) tint anchor
            if (anchors[bi] < 0 || (anchors[bj] >= 0 && anchors[bj] < anchors[bi])) anchors[bi] = anchors[bj];
            anchors.RemoveAt(bj);
            double wi = weights[bi], wj = weights[bj], tw = wi + wj;
            labs[bi] = new Lab(
                (float)((labs[bi].L * wi + labs[bj].L * wj) / tw),
                (float)((labs[bi].A * wi + labs[bj].A * wj) / tw),
                (float)((labs[bi].B * wi + labs[bj].B * wj) / tw));
            weights[bi] = tw;
            labs.RemoveAt(bj);
            weights.RemoveAt(bj);
        }
        return labs.ToArray();
    }

    struct XorShift
    {
        ulong _s;
        public XorShift(int seed) => _s = (ulong)(uint)seed * 2654435761UL + 1;
        public uint Next()
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return (uint)_s;
        }
        public double NextDouble() => Next() / 4294967296.0;
    }
}
