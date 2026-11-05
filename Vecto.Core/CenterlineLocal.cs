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

    /// <summary>Admit wide components beyond the global text scale only after row protection.
    /// Size relative to a global hairline median alone cannot exclude larger glyphs.</summary>
    static bool LocalEligible(Comp c, double median) =>
        !c.TextRow && c.MaxDim > TextMaxFactor * median && c.Width > ThinFactor * median + ThinSlack;

    /// <summary>
    /// Protect only new local admissions using a common row direction through component
    /// centres. Axis-aligned boxes may overlap in a diagonal word; ink need not. Existing
    /// whole-component geometry retains its classification. Previously protected glyphs
    /// can still supply context. This is bounded row evidence, not closure-based OCR.
    /// </summary>
    static void MarkOrientedLocalTextRows(List<Comp> comps, double median, double[] widthAt, int w)
    {
        var candidates = new List<Comp>();
        var shapes = new Dictionary<Comp, (double Along, double Across, double X, double Y, double Width)>();
        foreach (var c in comps)
        {
            // The component's own width prevents a long drawing hairline from setting
            // the glyph scale. Intrinsic compactness also excludes DIAGONAL hatches,
            // whose axis-aligned box alone can look square.
            var widths = new List<double>();
            double cx = (c.MinX + c.MaxX) * 0.5, cy = (c.MinY + c.MaxY) * 0.5;
            double xx = 0, xy = 0, yy = 0;
            foreach (int i in c.Pixels)
            {
                if (widthAt[i] > 0) widths.Add(widthAt[i]);
                double x = i % w - cx, y = i / w - cy;
                xx += x * x; xy += x * y; yy += y * y;
            }
            // Diagonal ridges can underestimate the own median (stair-step widths).
            // The component EDT maximum is a conservative fallback, still gated by
            // intrinsic compactness and a consistent four-peer row.
            if (widths.Count == 0 || c.MaxDim > TextMaxFactor * Math.Max(Median(widths), c.Width)) continue;
            if (IsThinAnnulus(c, w)) continue;
            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
            double cs = Math.Cos(angle), sn = Math.Sin(angle);
            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            foreach (int i in c.Pixels)
            {
                double x = i % w - cx, y = i / w - cy;
                double u = x * cs + y * sn, v = -x * sn + y * cs;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            double along = maxU - minU + 1, across = maxV - minV + 1;
            if (Math.Max(along, across) <= 4 * Math.Min(along, across))
            {
                candidates.Add(c);
                shapes[c] = (along, across, cs, sn, Median(widths));
            }
        }
        MarkShortLocalText(candidates, shapes, median, w);
        if (candidates.Count < TextPeerMin + 1) return;
        candidates.Sort((a, b) => (a.MinX + a.MaxX).CompareTo(b.MinX + b.MaxX));
        var centresX = candidates.Select(c => (c.MinX + c.MaxX) * 0.5).ToArray();
        foreach (var c in candidates)
        {
            if (c.Centerline || !LocalEligible(c, median)) continue;
            double cx = (c.MinX + c.MaxX) * 0.5, cy = (c.MinY + c.MaxY) * 0.5;
            double radius = 4 * c.MaxDim;
            int lo = Array.BinarySearch(centresX, cx - radius);
            if (lo < 0) lo = ~lo;
            while (lo > 0 && centresX[lo - 1] >= cx - radius) lo--;
            var neighbours = new List<(double X, double Y, double Size)>();
            bool crowded = false;
            for (int j = lo; j < candidates.Count && centresX[j] <= cx + radius; j++)
            {
                var o = candidates[j];
                if (o == c || o.MaxDim > 2 * c.MaxDim || c.MaxDim > 2 * o.MaxDim) continue;
                double dx = centresX[j] - cx, dy = (o.MinY + o.MaxY) * 0.5 - cy;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 0.4 * Math.Max(c.MaxDim, o.MaxDim) || distance > radius) continue;
                // Bound directional hypotheses in dense symbol fields. Ambiguous local
                // candidates keep their original fill rather than losing source letters.
                if (neighbours.Count == 64) { crowded = true; break; }
                neighbours.Add((dx, dy, o.MaxDim));
            }
            if (crowded) { c.TextRow = true; continue; }
            foreach (var axis in neighbours)
            {
                double len = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y);
                double ux = axis.X / len, uy = axis.Y / len;
                int peers = 0;
                foreach (var o in neighbours)
                {
                    double cross = Math.Abs(o.X * uy - o.Y * ux);
                    if (cross > 0.25 * Math.Min(c.MaxDim, o.Size)) continue;
                    if (++peers < TextPeerMin) continue;
                    c.TextRow = true;
                    break;
                }
                if (c.TextRow) break;
            }
        }
    }

    // VEC107: ONE positive-geometry requirement replaces letter-specific peer rules.
    // A compact component whose intrinsic oriented extent is within the text scale of
    // its OWN typical ridge width is ambiguous between a short label (E, F, H, L, 1, 7,
    // B, 8 …) and a small CAD primitive. New local recovery is admitted only for a
    // coherent simple primitive: an isolated full rectangular frame around exactly one
    // counter. Thin annuli are excluded before this pass and keep their own path.
    // Everything else stays filled. Original whole-component decisions are unchanged;
    // long hatches and large outlines exceed the compact scale and are not affected.
    static void MarkShortLocalText(List<Comp> candidates,
        Dictionary<Comp, (double Along, double Across, double X, double Y, double Width)> shapes,
        double median, int w)
    {
        var compact = new List<Comp>();
        foreach (var c in candidates)
        {
            var s = shapes[c];
            if (Math.Max(s.Along, s.Across) <= TextMaxFactor * (s.Width + ThinSlack)) compact.Add(c);
        }
        foreach (var c in compact)
        {
            if (c.Centerline || !LocalEligible(c, median)) continue;
            var s = shapes[c];
            if (CounterCount(c, s.Width, w, 2) == 1 && IsRectangularFrame(c, s, w) && !HasCompactPeer(c, s, compact, shapes))
                continue;
            c.TextRow = true;
        }
    }

    // A rectangle beside similar compact shapes is ambiguous with a row of block
    // 0/O/D glyphs, and thick frames do not recover as one coherent closed loop.
    // Any similar-scale compact neighbour within four extents keeps it filled.
    static bool HasCompactPeer(Comp c, (double Along, double Across, double X, double Y, double Width) s,
        List<Comp> compact, Dictionary<Comp, (double Along, double Across, double X, double Y, double Width)> shapes)
    {
        double cx = (c.MinX + c.MaxX) * 0.5, cy = (c.MinY + c.MaxY) * 0.5;
        foreach (var o in compact)
        {
            if (o == c || o.MaxDim > 2 * c.MaxDim || c.MaxDim > 2 * o.MaxDim) continue;
            var t = shapes[o];
            if (t.Width > 2 * s.Width || s.Width > 2 * t.Width) continue;
            double dx = (o.MinX + o.MaxX) * 0.5 - cx, dy = (o.MinY + o.MaxY) * 0.5 - cy;
            if (dx * dx + dy * dy <= 16.0 * c.MaxDim * c.MaxDim) return true;
        }
        return false;
    }

    // Full rectangular outline in the component's own principal frame: nearly all ink
    // lies in a band of the own ridge width along the oriented box, and every side of
    // that band is substantially inked. A partial frame (E, F, L, C) fails one side.
    static bool IsRectangularFrame(Comp c, (double Along, double Across, double X, double Y, double Width) s, int w)
    {
        double band = s.Width + 1.5;
        if (s.Along < 4 * band || s.Across < 4 * band) return false;
        double cx = (c.MinX + c.MaxX) * 0.5, cy = (c.MinY + c.MaxY) * 0.5;
        double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
        foreach (int i in c.Pixels)
        {
            double x = i % w - cx, y = i / w - cy;
            double u = x * s.X + y * s.Y, v = -x * s.Y + y * s.X;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        // Coverage per side along its length, in unit bins.
        int nu = (int)Math.Ceiling(maxU - minU) + 1, nv = (int)Math.Ceiling(maxV - minV) + 1;
        var top = new bool[nu]; var bottom = new bool[nu]; var left = new bool[nv]; var right = new bool[nv];
        int inside = 0;
        foreach (int i in c.Pixels)
        {
            double x = i % w - cx, y = i / w - cy;
            double u = x * s.X + y * s.Y - minU, v = -x * s.Y + y * s.X - minV;
            double du = maxU - minU - u, dv = maxV - minV - v;
            bool any = false;
            if (v <= band) { top[(int)u] = true; any = true; }
            if (dv <= band) { bottom[(int)u] = true; any = true; }
            if (u <= band) { left[(int)v] = true; any = true; }
            if (du <= band) { right[(int)v] = true; any = true; }
            if (any) inside++;
        }
        static double Share(bool[] a) => a.Count(b => b) / (double)a.Length;
        return inside >= 0.95 * c.Pixels.Count &&
            Math.Min(Math.Min(Share(top), Share(bottom)), Math.Min(Share(left), Share(right))) >= 0.9;
    }

    // Four-connected ink uses eight-connected paper, so diagonal raster pinholes
    // are not invented counters. Ignore cavities smaller than a stroke-width square.
    static int CounterCount(Comp c, double width, int w, int limit)
    {
        int bw = c.MaxX - c.MinX + 3, bh = c.MaxY - c.MinY + 3;
        var seen = new bool[bw * bh];
        foreach (int q in c.Pixels) seen[(q / w - c.MinY + 1) * bw + q % w - c.MinX + 1] = true;
        var queue = new int[seen.Length];
        int counters = 0;
        for (int start = 0; start < seen.Length; start++)
        {
            if (seen[start]) continue;
            int head = 0, tail = 0;
            bool exterior = false;
            seen[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                int q = queue[head++], x = q % bw, y = q / bw;
                if (x == 0 || y == 0 || x == bw - 1 || y == bh - 1) exterior = true;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(bh - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(bw - 1, x + 1); xx++)
                    {
                        int n = yy * bw + xx;
                        if (seen[n]) continue;
                        seen[n] = true;
                        queue[tail++] = n;
                    }
            }
            if (!exterior && tail >= width * width && ++counters >= limit) return counters;
        }
        return counters;
    }

    // Uniform radial ink in a near-square box is positive circular CAD evidence,
    // unlike closure alone. O/C-like letters remain an ambiguous geometry family.
    static bool IsThinAnnulus(Comp c, int w)
    {
        int boxWidth = c.MaxX - c.MinX + 1, boxHeight = c.MaxY - c.MinY + 1;
        if (Math.Max(boxWidth, boxHeight) > 1.1 * Math.Min(boxWidth, boxHeight)) return false;
        double cx = (c.MinX + c.MaxX) * 0.5, cy = (c.MinY + c.MaxY) * 0.5;
        double minRadiusSq = double.PositiveInfinity, maxRadiusSq = 0;
        foreach (int i in c.Pixels)
        {
            double x = i % w - cx, y = i / w - cy, r2 = x * x + y * y;
            minRadiusSq = Math.Min(minRadiusSq, r2); maxRadiusSq = Math.Max(maxRadiusSq, r2);
        }
        double inner = Math.Sqrt(minRadiusSq), outer = Math.Sqrt(maxRadiusSq);
        return inner > 0 && outer - inner <= 0.25 * (outer + inner) * 0.5;
    }

    static void ExtractLocal(Comp c, int[] comp, bool[] skel, double[] widthAt, int[] dtSq, bool[] inkMask,
        int ink, double median, int pruneLen, int growLen,
        EffectiveParams p, int w, int h, List<StrokePath> strokes, CancellationToken ct)
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
        var chains = tracer.Trace(c.Pixels).ToList();
        // Isolated ridge maxima can be the only observation between two staircase
        // fragments. They are evidence for this local arc pass, never legacy strokes.
        foreach (int q in c.Pixels)
            if (skel[q] && tracer.Degree(q) == 0) chains.Add(([q], false));
        WeldJunctionSplitChains(chains, c.Id, comp, inkMask, widthAt, dtSq, thin, median, minLen, w, h);
        var arcOwned = RecoverLocalArcs(chains, c.Id, comp, inkMask, widthAt, dtSq, thin, minLen, ink, w, h, strokes, ct);
        // Read before the admission loop below grows the admitted chains' tips.
        var hatchMembers = ProposeHatchMembers(chains, arcOwned, tracer, c.Id, comp, inkMask, widthAt, thin, minLen, growLen, ink, w, h, ct);
        foreach (var (pixels, closed) in chains)
        {
            if (arcOwned.Contains(pixels)) continue;
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
        foreach (var member in hatchMembers)
            if (!DuplicatesLine(member, strokes)) strokes.Add(member);

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

    /// <summary>VEC143: family members share a direction within this angle.</summary>
    public const double HatchAngleTolDeg = 1.0;
    /// <summary>VEC143: adjacent family gaps stay within this share of the seed gap.</summary>
    public const double HatchPitchTol = 0.12;
    /// <summary>VEC143: a recovered member's source span, in its own widths.</summary>
    public const double HatchMinAspect = 12;
    /// <summary>VEC143: a recovered member's span stays within this share of each peer's span.</summary>
    public const double HatchSpanTol = 0.3;
    /// <summary>VEC143: more straight candidates than this keep their fill (bounded work).</summary>
    public const int HatchMaxMembers = 256;

    sealed record HatchLine(Vec2 Center, Vec2 Dir, double Lo, double Hi, double Width, bool Admitted, Vec2 A, Vec2 B)
    {
        public double Span => Hi - Lo;

        public static bool SameLine(HatchLine a, HatchLine b)
        {
            if (Math.Abs(a.Dir.X * b.Dir.Y - a.Dir.Y * b.Dir.X) > DetMath.Sin(HatchAngleTolDeg * Math.PI / 180)) return false;
            var delta = b.Center - a.Center;
            if (Math.Abs(delta.X * a.Dir.Y - delta.Y * a.Dir.X) > Math.Max(a.Width, b.Width) / 2) return false;
            return Overlap(a, b) >= .5 * Math.Min(a.Span, b.Span);
        }

        public static double Overlap(HatchLine a, HatchLine b)
        {
            double along = (b.Center - a.Center).Dot(a.Dir);
            // PCA directions can straddle +/-90 degrees. Express the peer's signed
            // walk interval in the seed's orientation before comparing or deduplicating.
            bool reversed = a.Dir.Dot(b.Dir) < 0;
            double lo = reversed ? -b.Hi : b.Lo;
            double hi = reversed ? -b.Lo : b.Hi;
            return Math.Min(a.Hi, along + hi) - Math.Max(a.Lo, along + lo);
        }
    }

    /// <summary>
    /// VEC143: a one-pixel width step in a raster line leaves no ridge local maximum across
    /// it, so one drawn line can trace as a crumb, a main chain and a crumb whose main chain
    /// misses the length gate. The gate is unchanged: the main chain's SOURCE span must
    /// pass it, measured by walking cross-sections of uniform ink, so a solid, a wall, a
    /// crossing or paper ends the span. That alone is not enough to redraw it. The line must
    /// also sit in a regular parallel family (similar direction, width, span and pitch) with
    /// independently admitted peers; compact loops and irregular marks keep their fill.
    /// </summary>
    static List<StrokePath> ProposeHatchMembers(List<(List<int> Pixels, bool Closed)> chains,
        HashSet<List<int>> arcOwned, ChainTracer tracer, int id, int[] comp, bool[] inkMask, double[] widthAt,
        double thin, int minLen, int growLen, int ink, int w, int h, CancellationToken ct)
    {
        var lines = new List<HatchLine>();
        foreach (var (pixels, closed) in chains)
        {
            ct.ThrowIfCancellationRequested();
            if (closed || arcOwned.Contains(pixels)) continue;
            if (Measure(pixels) is not { } line) continue;
            // Two fragments of one drawn line walk to the same span: keep one.
            if (lines.Any(o => HatchLine.SameLine(o, line))) continue;
            if (lines.Count == HatchMaxMembers) return [];
            lines.Add(line);
        }
        var result = new List<StrokePath>();
        if (lines.Count < TextPeerMin) return result;
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Admitted || line.Span < minLen || line.Span < HatchMinAspect * line.Width) continue;
            if (!InRegularFamily(line)) continue;
            result.Add(new StrokePath { PaletteIndex = ink, Width = line.Width, Closed = false,
                Segments = [CubicBezier.Line(line.A, line.B)] });
        }
        return result;

        HatchLine? Measure(List<int> pixels)
        {
            var samples = pixels.Where(q => widthAt[q] > 0 && tracer.Degree(q) == 2).Select(q => widthAt[q]).ToList();
            if (samples.Count < 2) return null;
            samples.Sort();
            double med = samples[samples.Count / 2];
            if (med <= 0 || med > thin || pixels.Count < LongFactor * med || samples.Count * 2 < pixels.Count) return null;
            if (samples[samples.Count / 10] < LocalUniformLow * med || samples[samples.Count * 9 / 10] > LocalUniformHigh * med) return null;
            double mx = 0, my = 0;
            foreach (int q in pixels) { mx += q % w + .5; my += q / w + .5; }
            mx /= pixels.Count; my /= pixels.Count;
            double xx = 0, xy = 0, yy = 0;
            foreach (int q in pixels)
            {
                double x = q % w + .5 - mx, y = q / w + .5 - my;
                xx += x * x; xy += x * y; yy += y * y;
            }
            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
            var dir = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle));
            if (dir.X < -1e-9 || (Math.Abs(dir.X) <= 1e-9 && dir.Y < 0)) dir = dir * -1;
            var normal = new Vec2(-dir.Y, dir.X);
            var origin = new Vec2(mx, my);
            foreach (int q in pixels)
                if (Math.Abs((new Vec2(q % w + .5, q / w + .5) - origin).Dot(normal)) > 1.5) return null;
            double shift = 0;
            int sections = 0;
            double hi = Walk(1), lo = -Walk(-1);
            if (sections == 0) return null;
            var center = origin + normal * (shift / sections);
            return new HatchLine(center, dir, lo, hi, med, pixels.Count >= minLen,
                Grow(center + dir * lo, dir * -1), Grow(center + dir * hi, dir));

            double Walk(int sign)
            {
                double t = 0;
                for (int step = 0; step <= 4 * minLen + pixels.Count; step++, t = step)
                {
                    var q = origin + dir * (sign * t);
                    int a = Ink(q, normal, -1, med), b = Ink(q, normal, 1, med);
                    if (a < 0 || b < 0) break;
                    double width = a + b + 1;
                    if (width < LocalUniformLow * med || width > LocalUniformHigh * med) break;
                    if (Math.Abs(b - a) * .5 > 1.5) break;
                    shift += (b - a) * .5;
                    sections++;
                }
                return Math.Max(0, t - 1);
            }
        }

        // Ink pixels from q along +-normal. -1: q itself is paper, or the section is wider
        // than any uniform band could accept.
        int Ink(Vec2 q, Vec2 normal, int sign, double med)
        {
            int limit = (int)Math.Ceiling(LocalUniformHigh * med) + 1;
            if (!IsInk(q)) return -1;
            for (int n = 1; n <= limit; n++)
                if (!IsInk(q + normal * (sign * n))) return n - 1;
            return -1;
        }

        bool IsInk(Vec2 q)
        {
            int x = (int)Math.Floor(q.X), y = (int)Math.Floor(q.Y);
            return (uint)x < (uint)w && (uint)y < (uint)h && inkMask[y * w + x] && comp[y * w + x] == id;
        }

        // Same growth as an admitted chain's tip: into this component's ink only, so a
        // joined end reaches its host's centre line and a free end stays on source ink.
        Vec2 Grow(Vec2 end, Vec2 dir)
        {
            for (int n = 1; n <= growLen; n++)
            {
                var q = end + dir * n;
                if (!IsInk(q)) return end + dir * (n - 1);
            }
            return end + dir * growLen;
        }

        bool InRegularFamily(HatchLine seed)
        {
            double tol = DetMath.Sin(HatchAngleTolDeg * Math.PI / 180);
            var normal = new Vec2(-seed.Dir.Y, seed.Dir.X);
            var peers = new List<(double Offset, HatchLine Line)>();
            foreach (var o in lines)
            {
                if (Math.Abs(o.Dir.X * seed.Dir.Y - o.Dir.Y * seed.Dir.X) > tol) continue;
                if (o.Width < 0.75 * seed.Width || o.Width > seed.Width / 0.75) continue;
                if (Math.Abs(o.Span - seed.Span) > HatchSpanTol * seed.Span) continue;
                if (HatchLine.Overlap(seed, o) < .5 * Math.Min(seed.Span, o.Span)) continue;
                peers.Add(((o.Center - seed.Center).Dot(normal), o));
            }
            peers.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            int at = peers.FindIndex(q => ReferenceEquals(q.Line, seed));
            int first = at, last = at;
            double pitch = 0;
            // The nearest gap on either side sets the pitch; ties resolve to the lower side.
            if (at > 0) pitch = peers[at].Offset - peers[at - 1].Offset;
            if (at + 1 < peers.Count && (pitch == 0 || peers[at + 1].Offset - peers[at].Offset < pitch))
                pitch = peers[at + 1].Offset - peers[at].Offset;
            if (pitch < 2.5 * seed.Width) return false;
            while (first > 0 && Math.Abs(peers[first].Offset - peers[first - 1].Offset - pitch) <= HatchPitchTol * pitch) first--;
            while (last + 1 < peers.Count && Math.Abs(peers[last + 1].Offset - peers[last].Offset - pitch) <= HatchPitchTol * pitch) last++;
            if (last - first + 1 < TextPeerMin) return false;
            int admitted = 0;
            for (int i = first; i <= last; i++)
                if (peers[i].Line.Admitted) admitted++;
            return admitted >= 2;
        }
    }

    // A proposed member never overdraws a line the admission loop already emitted.
    static bool DuplicatesLine(StrokePath member, List<StrokePath> strokes)
    {
        var m = member.Segments[0];
        var dir = (m.P3 - m.P0).Normalized();
        double length = (m.P3 - m.P0).Length;
        foreach (var s in strokes)
            foreach (var g in s.Segments)
            {
                if (!g.IsLine(.05)) continue;
                var d = g.P3 - g.P0;
                double len = d.Length;
                if (len < 1e-6) continue;
                var u = d * (1 / len);
                if (Math.Abs(u.X * dir.Y - u.Y * dir.X) > DetMath.Sin(2 * Math.PI / 180)) continue;
                var delta = g.P0 - m.P0;
                if (Math.Abs(delta.X * dir.Y - delta.Y * dir.X) > (member.Width + s.Width) / 2) continue;
                double a = delta.Dot(dir), b = (g.P3 - m.P0).Dot(dir);
                if (Math.Min(length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b)) >= .5 * length) return true;
            }
        return false;
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
            foreach (var seg in stroke.Segments)
            {
                // Retain the raster ownership margin along curve interiors. At an
                // open curved end, pixels beyond the endpoint's tangent plane must
                // lie inside the painted round cap rather than its release margin.
                double r = stroke.Width / 2 + 0.5, r2 = r * r;
                bool curved = !seg.IsLine(0.02);
                bool first = !stroke.Closed && seg.Equals(stroke.Segments[0]);
                bool last = !stroke.Closed && seg.Equals(stroke.Segments[^1]);
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
                            double cap2 = stroke.Width * stroke.Width / 4;
                            if (curved && first && (q - seg.P0).Dot(seg.P1 - seg.P0) < 0 &&
                                q.DistSq(seg.P0) > cap2) continue;
                            if (curved && last && (q - seg.P3).Dot(seg.P3 - seg.P2) > 0 &&
                                q.DistSq(seg.P3) > cap2) continue;
                            if (q.DistSq(a + d * t) <= r2) labels[i] = paper;
                        }
                    a = b;
                }
            }
        }
    }
}
