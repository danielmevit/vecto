namespace Vecto.Core;

public static partial class Centerline
{
    // Arc recovery reads the ungrown ridge. Endpoint buckets bound continuation work;
    // source cross-sections, rather than a successful fit alone, authorize every gap.
    static HashSet<List<int>> RecoverLocalArcs(List<(List<int> Pixels, bool Closed)> chains,
        int id, int[] comp, bool[] inkMask, double[] widthAt, int[] dtSq,
        double thin, int minLen, int ink, int w, int h, List<StrokePath> strokes)
    {
        const int cell = 8, maxCandidates = 512, maxSamples = 262144;
        var owned = new HashSet<List<int>>();
        var buckets = new Dictionary<(int, int), List<(int Chain, bool End)>>();
        Vec2 Point(int q) => new(q % w + .5, q / w + .5);
        double Width(int q) => widthAt[q] > 0 ? widthAt[q] : 2 * Math.Sqrt(dtSq[q]) - 1;
        for (int i = 0; i < chains.Count; i++)
        {
            var (pixels, closed) = chains[i];
            if (closed || pixels.Count < 2) continue;
            Add(pixels[0], false); Add(pixels[^1], true);
            void Add(int q, bool end)
            {
                var key = (q % w / cell, q / w / cell);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add((i, end));
            }
        }
        int candidates = 0, budget = maxSamples;
        foreach (var (seed, closed) in chains.OrderByDescending(c => c.Pixels.Count))
        {
            if (closed || owned.Contains(seed) || seed.Count < 24 || candidates++ >= maxCandidates) continue;
            var points = seed.Select(Point).ToList();
            if (!Fit(points, out var center, out double radius)) continue;
            double width = Median(seed.Select(Width).ToList());
            if (width < 2 || width > thin || radius < 3 * width) continue;
            if (!Uniform(seed, width)) continue;
            double start = Angle(points[0]), end = start;
            bool monotone = true;
            double direction = Math.Sign(Cross(points[points.Count / 2] - points[0], points[^1] - points[0]));
            if (direction == 0) continue;
            foreach (var point in points.Skip(1))
            {
                double next = end + Delta(Angle(point) - end);
                if ((next - end) * direction < -.02) monotone = false;
                end = next;
            }
            if (!monotone || Math.Abs(end - start) < .35 || Math.Abs(end - start) > Math.PI * 1.9) continue;
            var members = new HashSet<List<int>> { seed };
            bool ambiguous = false;
            for (int side = 0; side < 2 && !ambiguous; side++)
            {
                for (int step = 0; step < 64; step++)
                {
                    var tip = side == 0 ? points[0] : points[^1];
                    var matches = new List<(List<int> Chain, List<Vec2> Points)>();
                    int bx = (int)tip.X / cell, by = (int)tip.Y / cell;
                    for (int yy = by - 1; yy <= by + 1; yy++)
                        for (int xx = bx - 1; xx <= bx + 1; xx++)
                        {
                            if (!buckets.TryGetValue((xx, yy), out var entries)) continue;
                            // Dense junctions are ambiguous; never scan a whole component.
                            if (entries.Count > 16) { ambiguous = true; continue; }
                            foreach (var entry in entries)
                            {
                                var run = chains[entry.Chain].Pixels;
                                if (members.Contains(run) || owned.Contains(run) || !Uniform(run, width)) continue;
                                var q = Point(entry.End ? run[^1] : run[0]);
                                if ((q - tip).Length > Math.Min(8, width * .55)) continue;
                                var ordered = run.Select(Point).ToList();
                                if (entry.End) ordered.Reverse();
                                if (ordered.Any(p => Math.Abs((p - center).Length - radius) > 1.2)) continue;
                                double advance = Delta(Angle(ordered[^1]) - Angle(tip)) * direction * (side == 0 ? -1 : 1);
                                if (advance <= 0 || advance > Math.PI / 2) continue;
                                int window = Math.Min(12, ordered.Count - 1);
                                var tangent = ordered[window] - ordered[0];
                                var radial = q - center;
                                if (Math.Abs(tangent.Dot(radial)) > .45 * tangent.Length * radius) continue;
                                if (!SupportedSweep(Angle(tip), Delta(Angle(q) - Angle(tip)), center, radius, width)) continue;
                                matches.Add((run, ordered));
                            }
                        }
                    if (matches.Count > 1) { ambiguous = true; break; }
                    if (matches.Count == 0) break;
                    var match = matches[0];
                    members.Add(match.Chain);
                    if (side == 0) { match.Points.Reverse(); points.InsertRange(0, match.Points); }
                    else points.AddRange(match.Points);
                }
            }
            if (ambiguous || points.Count < minLen || !Fit(points, out center, out radius)) continue;
            start = Angle(points[0]); end = start;
            foreach (var point in points.Skip(1)) end += Delta(Angle(point) - end);
            if ((end - start) * direction <= 0) continue;
            // Follow only short source-supported continuations. The radial cross-section
            // must remain centred and thin, so solids, straight tangencies and gaps stop it.
            double increment = .5 / radius;
            start = Grow(start, -direction); end = Grow(end, direction);
            double sweep = end - start;
            if (Math.Abs(sweep) < .7 || Math.Abs(sweep) > Math.PI * 1.9 ||
                radius * Math.Abs(sweep) < minLen || !SupportedSweep(start, sweep, center, radius, width)) continue;
            strokes.Add(ArcStroke(center, radius, start, sweep, width, ink));
            foreach (var member in members) owned.Add(member);

            double Angle(Vec2 p) => Math.Atan2(p.Y - center.Y, p.X - center.X);
            double Grow(double angle, double sign)
            {
                double grown = angle;
                int count = (int)Math.Ceiling(2 * width / .5);
                for (int k = 1; k <= count; k++)
                {
                    double next = angle + sign * increment * k;
                    if (!Section(next, center, radius, width)) break;
                    grown = next;
                }
                return grown;
            }
        }
        return owned;

        bool Uniform(List<int> run, double width)
        {
            if (run.Count < 2) return false;
            return run.All(q => Width(q) >= .8 * width && Width(q) <= 1.2 * width && Width(q) <= thin);
        }
        bool Fit(List<Vec2> points, out Vec2 center, out double radius) =>
            CenterlineCircles.FitCavity(points, out center, out radius, out _, out _, out _);
        bool At(Vec2 p) => p.X >= 0 && p.Y >= 0 && p.X < w && p.Y < h &&
            inkMask[(int)p.Y * w + (int)p.X] && comp[(int)p.Y * w + (int)p.X] == id;
        bool Section(double angle, Vec2 center, double radius, double width)
        {
            if (--budget < 0) return false;
            var n = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle));
            var p = center + n * radius;
            for (int band = -2; band <= 2; band++)
                if (!At(p + n * (band * width * .2))) return false;
            double inside = 0, outside = 0;
            while (inside <= width && At(p - n * inside)) inside += .5;
            while (outside <= width && At(p + n * outside)) outside += .5;
            return inside <= width && outside <= width && Math.Abs(inside - outside) <= 2 &&
                Math.Abs(inside + outside - .5 - width) <= Math.Max(1.5, width * .15);
        }
        bool SupportedSweep(double start, double sweep, Vec2 center, double radius, double width)
        {
            int steps = (int)Math.Ceiling(Math.Abs(sweep) * radius * 2);
            if (steps > 4096 || steps > budget) return false;
            for (int k = 0; k <= steps; k++)
                if (!Section(start + sweep * k / Math.Max(1, steps), center, radius, width)) return false;
            return true;
        }
    }

    static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    static double Delta(double angle) => Math.Atan2(DetMath.Sin(angle), DetMath.Cos(angle));

    static StrokePath ArcStroke(Vec2 center, double radius, double start, double sweep, double width, int ink)
    {
        var segments = new List<CubicBezier>();
        int count = (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2));
        Vec2 Radial(double angle) => new(DetMath.Cos(angle), DetMath.Sin(angle));
        var a = Radial(start);
        var p0 = center + a * radius;
        for (int i = 1; i <= count; i++)
        {
            var b = Radial(start + sweep * i / count);
            var p3 = center + b * radius;
            double k = radius * 4 / 3 * DetMath.Tan(sweep / count / 4);
            segments.Add(new CubicBezier(p0, p0 + new Vec2(-a.Y, a.X) * k,
                p3 - new Vec2(-b.Y, b.X) * k, p3));
            a = b; p0 = p3;
        }
        return new StrokePath { PaletteIndex = ink, Width = width, Closed = false, Segments = segments };
    }
}
