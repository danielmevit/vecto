namespace Vecto.Core;

/// <summary>
/// VEC95 local centerline recovery inside mixed-width components. The whole-component test in
/// <see cref="Centerline"/> rejects a component as soon as ANY part of it is wide, so a solid
/// boss vetoes every thin line attached to it. This pass keeps that rejection for the
/// component as a whole and instead admits individual skeleton runs: maximal stretches of a
/// traced chain whose local width passes the same thin test, that are long enough to be
/// supported linework and whose width is near-uniform. Only the ink an emitted run's own
/// stroke paints is released; the solid, the junction cores and anything unexplained stay
/// filled for the region pipeline.
/// </summary>
public static partial class Centerline
{
    /// <summary>A local run is near-uniform when its 10th–90th percentile widths sit within
    /// this factor below / above the run's median width.</summary>
    public const double LocalUniformLow = 0.6, LocalUniformHigh = 1.4;

    /// <summary>Only components larger than any glyph go local: glyph-sized components never
    /// reach this pass, so text keeps the existing (filled) treatment.</summary>
    static bool LocalEligible(Comp c, double median) =>
        c.MaxDim > TextMaxFactor * median && c.Width > ThinFactor * median + ThinSlack;

    static void ExtractLocal(Comp c, int[] comp, bool[] skel, double[] widthAt, int[] dtSq, bool[] inkMask,
        int[] labels, int paper, int ink, double median, int pruneLen, int growLen,
        EffectiveParams p, int w, int h, List<StrokePath> strokes)
    {
        double thin = ThinFactor * median + ThinSlack;
        int minLen = (int)Math.Ceiling(LongFactor * median);
        var tracer = new ChainTracer(skel, comp, c.Id, w, h);
        tracer.PruneSpurs(c.Pixels, pruneLen);
        foreach (int q in tracer.BridgeGaps(c.Pixels, BridgeDist))
        {
            double width = 2 * Math.Sqrt(dtSq[q]) - 1;
            if (Twin(skel, inkMask, dtSq, q % w, q / w, q, w, h) >= 0) width += 1;
            widthAt[q] = width;
        }
        var release = new List<(List<int> Run, double Width)>();
        foreach (var (pixels, closed) in tracer.Trace(c.Pixels))
        {
            if (pixels.Count < minLen) continue;
            if (!closed) tracer.Extend(pixels, growLen);
            // a pixel is locally thin by its own DT: grown tips carry no ridge width sample
            bool Thin(int q) => (widthAt[q] > 0 ? widthAt[q] : 2 * Math.Sqrt(dtSq[q]) - 1) <= thin;
            bool allThin = pixels.All(Thin);
            int s = 0;
            while (s < pixels.Count)
            {
                if (!Thin(pixels[s])) { s++; continue; }
                int e = s;
                while (e < pixels.Count && Thin(pixels[e])) e++;
                var run = pixels.GetRange(s, e - s);
                s = e;
                if (run.Count < minLen) continue;
                var samples = run.Where(q => widthAt[q] > 0 && tracer.Degree(q) == 2).Select(q => widthAt[q]).ToList();
                if (samples.Count < minLen / 2) continue;
                samples.Sort();
                double med = samples[samples.Count / 2];
                if (med <= 0) continue;
                if (samples[samples.Count / 10] < LocalUniformLow * med || samples[samples.Count * 9 / 10] > LocalUniformHigh * med) continue;
                var stroke = BuildStroke(run, closed && allThin, med, ink, p, w, h, skel, inkMask, dtSq);
                if (stroke == null) continue;
                strokes.Add(stroke);
                release.Add((run, med));
            }
        }
        // ownership: an ink pixel is released only when its centre lies inside the round-capped
        // band an emitted run paints (half its width plus the half-pixel the fitted centre may
        // sit off a pixel centre) — everything else in the component keeps its fill
        foreach (var (run, width) in release)
        {
            double r = width / 2 + 0.5;
            int ri = (int)Math.Ceiling(r);
            double r2 = r * r;
            foreach (int q in run)
            {
                int qx = q % w, qy = q / w;
                for (int dy = -ri; dy <= ri; dy++)
                    for (int dx = -ri; dx <= ri; dx++)
                    {
                        if (dx * dx + dy * dy > r2) continue;
                        int x = qx + dx, y = qy + dy;
                        if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                        int i = y * w + x;
                        if (comp[i] == c.Id) labels[i] = paper;
                    }
            }
        }
    }
}
