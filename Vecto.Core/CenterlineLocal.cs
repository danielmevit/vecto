namespace Vecto.Core;

/// <summary>
/// VEC95 local centerline recovery inside mixed-width components. The whole-component test in
/// <see cref="Centerline"/> rejects a component as soon as ANY part of it is wide, so a solid
/// boss vetoes every thin line attached to it. This pass keeps that rejection for the
/// component as a whole and instead admits individual skeleton runs: maximal stretches of a
/// traced chain whose local width passes the same thin test, that are long enough to be
/// supported linework and whose width is near-uniform. Only the ink the final emitted
/// geometry paints is released; the solid, the junction cores and anything unexplained stay
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
        int ink, double median, int pruneLen, int growLen,
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
                Emit(run, closed && allThin, true);
            }
        }

        void Emit(List<int> run, bool closed, bool partition)
        {
            if (run.Count < minLen) return;
            var samples = run.Where(q => widthAt[q] > 0 && tracer.Degree(q) == 2).Select(q => widthAt[q]).ToList();
            if (samples.Count < minLen / 2) return;
            samples.Sort();
            double med = samples[samples.Count / 2];
            if (med <= 0) return;
            if (samples[samples.Count / 10] < LocalUniformLow * med || samples[samples.Count * 9 / 10] > LocalUniformHigh * med)
            {
                if (!partition) return;
                // A real weight change must not veto the whole attached outline. Only
                // rejected nonuniform runs are partitioned; median filtering suppresses
                // junction-width spikes. Short transition pieces keep their source fill.
                int radius = Math.Max(2, (int)Math.Ceiling(median));
                int start = 0, previous = 0;
                var window = new List<double>();
                for (int i = 0; i < run.Count; i++)
                {
                    window.Clear();
                    for (int j = Math.Max(0, i - radius); j <= Math.Min(run.Count - 1, i + radius); j++)
                        if (widthAt[run[j]] > 0 && tracer.Degree(run[j]) == 2) window.Add(widthAt[run[j]]);
                    window.Sort();
                    double value = window.Count == 0 ? med : window[window.Count / 2];
                    int band = value < LocalUniformLow * med ? -1 : value > LocalUniformHigh * med ? 1 : 0;
                    if (i > 0 && band != previous)
                    {
                        Emit(run.GetRange(start, i - start), false, false);
                        start = i;
                    }
                    previous = band;
                }
                Emit(run.GetRange(start, run.Count - start), false, false);
                return;
            }
            var stroke = BuildStroke(run, closed, med, ink, p, w, h, skel, inkMask, dtSq);
            if (stroke != null) strokes.Add(stroke);
        }
    }

    // Only already fitted, long, exact straight primitives become independent objects.
    // Short cap fragments stay with their curve; closed rings keep their cyclic fit.
    static void SeparateLocalPrimitives(List<StrokePath> strokes, HashSet<int> local, int[] comp, double[] widthAt, int w, int h)
    {
        var result = new List<StrokePath>();
        foreach (var stroke in strokes)
        {
            if (stroke.Closed || stroke.Segments.Count < 2)
            {
                result.Add(stroke);
                continue;
            }
            bool IsPrimitive(CubicBezier seg)
            {
                var mid = seg.Eval(0.5);
                int x = (int)mid.X, y = (int)mid.Y;
                return seg.IsLine(0.02) && (seg.P3 - seg.P0).Length >= LongFactor * stroke.Width
                    && (uint)x < (uint)w && (uint)y < (uint)h && local.Contains(comp[y * w + x]);
            }
            if (!stroke.Segments.Any(IsPrimitive))
            {
                result.Add(stroke);
                continue;
            }
            var pending = new List<CubicBezier>();
            foreach (var seg in stroke.Segments)
            {
                bool separate = IsPrimitive(seg);
                if (!separate) { pending.Add(seg); continue; }
                Flush();
                result.Add(new StrokePath { PaletteIndex = stroke.PaletteIndex, Width = PrimitiveWidth(seg),
                    Closed = false, Segments = [seg] });
            }
            Flush();
            double PrimitiveWidth(CubicBezier seg)
            {
                // A connected outline can change weight at a corner. Use the nearest
                // measured ridge along this primitive's interior, not the chain median.
                var samples = new List<double>();
                double length = (seg.P3 - seg.P0).Length;
                int reach = (int)Math.Ceiling(LineTol(stroke.Width) + 1);
                for (double d = 2 * stroke.Width; d < length - 2 * stroke.Width; d += 1)
                {
                    var pt = seg.P0 + (seg.P3 - seg.P0) * (d / length);
                    double nearest = double.PositiveInfinity, value = 0;
                    int cx = (int)pt.X, cy = (int)pt.Y;
                    for (int y = Math.Max(0, cy - reach); y <= Math.Min(h - 1, cy + reach); y++)
                        for (int x = Math.Max(0, cx - reach); x <= Math.Min(w - 1, cx + reach); x++)
                        {
                            int q = y * w + x;
                            if (widthAt[q] <= 0 || !local.Contains(comp[q])) continue;
                            double dist = pt.DistSq(new Vec2(x + 0.5, y + 0.5));
                            if (dist < nearest) { nearest = dist; value = widthAt[q]; }
                        }
                    if (value > 0) samples.Add(value);
                }
                if (samples.Count < LongFactor * stroke.Width) return stroke.Width;
                samples.Sort();
                double med = samples[samples.Count / 2];
                return samples[samples.Count / 10] >= LocalUniformLow * med &&
                    samples[samples.Count * 9 / 10] <= LocalUniformHigh * med ? med : stroke.Width;
            }
            void Flush()
            {
                if (pending.Count == 0) return;
                // Tiny straight corner/cap remnants are not independent CAD objects.
                // Leave their source ink filled rather than promoting them to fake lines.
                if (pending.All(g => g.IsLine(0.02)) &&
                    pending.Sum(g => (g.P3 - g.P0).Length) < 2 * stroke.Width)
                {
                    pending = [];
                    return;
                }
                result.Add(new StrokePath { PaletteIndex = stroke.PaletteIndex, Width = stroke.Width,
                    Closed = false, Segments = pending });
                pending = [];
            }
        }
        strokes.Clear();
        strokes.AddRange(result);
    }

    // Ownership is evaluated AFTER fitting, collinear welding, angle rationalization and
    // junction movement. A conservative subpixel polyline follows each final cubic;
    // only source ink in local components inside its actual round stroke band is released.
    static void ReleaseLocalInk(List<StrokePath> strokes, HashSet<int> local, int[] comp,
        int[] labels, int paper, int w, int h)
    {
        if (local.Count == 0) return;
        foreach (var stroke in strokes)
        {
            double r = stroke.Width / 2 + 0.5, r2 = r * r;
            foreach (var seg in stroke.Segments)
            {
                double lengthBound = (seg.P1 - seg.P0).Length + (seg.P2 - seg.P1).Length + (seg.P3 - seg.P2).Length;
                int steps = seg.IsLine(0.02) ? 1 : Math.Max(1, (int)Math.Ceiling(lengthBound * 2));
                var a = seg.P0;
                for (int k = 1; k <= steps; k++)
                {
                    var b = seg.Eval((double)k / steps);
                    int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - r));
                    int maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + r));
                    int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - r));
                    int maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + r));
                    var d = b - a;
                    for (int y = minY; y <= maxY; y++)
                        for (int x = minX; x <= maxX; x++)
                        {
                            int i = y * w + x;
                            if (labels[i] == paper || !local.Contains(comp[i])) continue;
                            var q = new Vec2(x + 0.5, y + 0.5);
                            double t = d.LengthSq == 0 ? 0 : Math.Clamp((q - a).Dot(d) / d.LengthSq, 0, 1);
                            if (q.DistSq(a + d * t) <= r2) labels[i] = paper;
                        }
                    a = b;
                }
            }
        }
    }
}
