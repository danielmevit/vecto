namespace Vecto.Core;

public static partial class Centerline
{
    // Arc recovery reads the ungrown ridge. Endpoint buckets bound continuation work;
    // source cross-sections, rather than a successful fit alone, authorize every gap.
    static HashSet<List<int>> RecoverLocalArcs(List<(List<int> Pixels, bool Closed)> chains,
        int id, int[] comp, bool[] inkMask, double[] widthAt, int[] dtSq,
        double thin, int minLen, int ink, int w, int h, List<StrokePath> strokes, CancellationToken ct)
    {
        const int cell = 8, maxCandidates = 512, maxSamples = 262144;
        var owned = new HashSet<List<int>>();
        var buckets = new Dictionary<(int, int), List<(int Chain, bool End)>>();
        Vec2 Point(int q) => new(q % w + .5, q / w + .5);
        double Width(int q) => widthAt[q] > 0 ? widthAt[q] : 2 * Math.Sqrt(dtSq[q]) - 1;
        for (int i = 0; i < chains.Count; i++)
        {
            var (pixels, closed) = chains[i];
            if (closed || pixels.Count == 0) continue;
            Add(pixels[0], false);
            if (pixels.Count > 1) Add(pixels[^1], true);
            void Add(int q, bool end)
            {
                var key = (q % w / cell, q / w / cell);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add((i, end));
            }
        }
        int candidates = 0, budget = maxSamples;
        // Angular intervals where a whole-primitive continuation crosses a merged
        // arc/line band. Inside them a thin radial section is not expected.
        var bridges = new List<(double Lo, double Hi)>();
        foreach (var (seed, closed) in chains.OrderByDescending(c => c.Pixels.Count))
        {
            if (closed || owned.Contains(seed) || seed.Count < 24 || seed.Count > 4096 || candidates++ >= maxCandidates) continue;
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
                                if (members.Contains(run) || owned.Contains(run)) continue;
                                if (run.Count + points.Count > 4096) continue;
                                var q = Point(entry.End ? run[^1] : run[0]);
                                var other = Point(entry.End ? run[0] : run[^1]);
                                // A tiny chain can put both endpoints in this bucket.
                                // It is one continuation, not two competing branches.
                                if ((other - tip).LengthSq < (q - tip).LengthSq ||
                                    (entry.End && (other - tip).LengthSq == (q - tip).LengthSq)) continue;
                                if ((q - tip).Length > Math.Min(8, width * .55)) continue;
                                if (!Uniform(run, width)) continue;
                                var ordered = run.Select(Point).ToList();
                                if (entry.End) ordered.Reverse();
                                // A short partial-circle fit has uncertain centre/radius.
                                // Refit the combined observation before accepting it;
                                // the final common fit retains the 1.5px radial gate.
                                double uncertainty = Math.Clamp(radius * .05, 2, 4);
                                if (ordered.Any(p => Math.Abs((p - center).Length - radius) > uncertainty)) continue;
                                if (!Fit(points.Concat(ordered).ToList(), out var joinedCenter, out double joinedRadius) ||
                                    (joinedCenter - center).Length > uncertainty || Math.Abs(joinedRadius - radius) > uncertainty) continue;
                                double advance = Delta(Angle(ordered[^1]) - Angle(tip)) * direction * (side == 0 ? -1 : 1);
                                if (advance <= 0 || advance > Math.PI / 2) continue;
                                int window = Math.Min(12, ordered.Count - 1);
                                var inner = side == 0 ? points[Math.Min(12, points.Count - 1)] : points[Math.Max(0, points.Count - 13)];
                                var tangent = ordered.Count >= 8 ? ordered[window] - ordered[0] : ordered[^1] - inner;
                                var radial = q - center;
                                if (Math.Abs(tangent.Dot(radial)) > .45 * tangent.Length * radius) continue;
                                if (!SupportedSweep(Angle(tip), Delta(Angle(q) - Angle(tip)), center, radius, width)) continue;
                                matches.Add((run, ordered));
                            }
                        }
                    if (matches.Count == 0) break;
                    // Several successive fragments can be within one search radius.
                    // They are not branches if their angular intervals are disjoint.
                    double Progress(Vec2 p) => Delta(Angle(p) - Angle(tip)) * direction * (side == 0 ? -1 : 1);
                    matches.Sort((a, b) => Progress(a.Points[0]).CompareTo(Progress(b.Points[0])));
                    for (int j = 1; j < matches.Count; j++)
                        if (matches[j].Chain.Count >= 8 && matches[j - 1].Chain.Count >= 8 &&
                            Progress(matches[j].Points[0]) < Progress(matches[j - 1].Points[^1]) - .001)
                            ambiguous = true;
                    if (ambiguous) break;
                    var match = matches[0];
                    members.Add(match.Chain);
                    if (side == 0) { match.Points.Reverse(); points.InsertRange(0, match.Points); }
                    else points.AddRange(match.Points);
                    Fit(points, out center, out radius);
                }
            }
            if (ambiguous || points.Count < minLen || !Fit(points, out center, out radius)) continue;
            start = Angle(points[0]); end = start;
            foreach (var point in points.Skip(1)) end += Delta(Angle(point) - end);
            if ((end - start) * direction <= 0) continue;
            // EDT maxima can sit one pixel to one side of a rotated ribbon. Refit
            // unbiased radial interface midpoints before extrapolating its ends.
            var middle = new List<Vec2>();
            var rayWidths = new List<double>();
            int rays = Math.Min(4096, (int)Math.Ceiling(Math.Abs(end - start) * radius));
            for (int k = 0; k <= rays; k++)
            {
                double a = start + (end - start) * k / Math.Max(1, rays);
                double observedWidth = MeasureArcSection(a, center, radius, width, out double shift);
                if (observedWidth <= 0) continue;
                middle.Add(center + new Vec2(DetMath.Cos(a), DetMath.Sin(a)) * (radius + shift));
                rayWidths.Add(observedWidth);
            }
            if (middle.Count >= 24 && Fit(middle, out var refinedCenter, out double refinedRadius) &&
                (refinedCenter - center).Length <= 2 && Math.Abs(refinedRadius - radius) <= 2 &&
                points.All(q => Math.Abs((q - refinedCenter).Length - refinedRadius) <= 1.5))
            {
                double oldStart = start, oldEnd = end;
                var first = center + new Vec2(DetMath.Cos(oldStart), DetMath.Sin(oldStart)) * radius;
                var last = center + new Vec2(DetMath.Cos(oldEnd), DetMath.Sin(oldEnd)) * radius;
                center = refinedCenter; radius = refinedRadius;
                start = Angle(first); end = start + (oldEnd - oldStart) + Delta(Angle(last) - Angle(first) - (oldEnd - oldStart));
            }
            if (rayWidths.Count >= 24)
            {
                double observedWidth = Median(rayWidths);
                if (observedWidth <= thin && Math.Abs(observedWidth - width) <= Math.Max(2, width * .15))
                    width = observedWidth;
            }
            // Follow only short source-supported continuations. The radial cross-section
            // must remain centred and thin, so solids, straight tangencies and gaps stop it.
            double increment = .5 / radius;
            // A true branch may contaminate a few endpoint cross-sections. Stop on
            // its thin side; the unrepresented junction ink remains source fill.
            start = Trim(start, direction); end = Trim(end, -direction);
            bool startTangent = false, endTangent = false;
            start = Grow(start, -direction, out startTangent); end = Grow(end, direction, out endTangent);
            bridges.Clear();
            if (startTangent) start = Continue(start, -direction, ref startTangent, ref end);
            if (endTangent) end = Continue(end, direction, ref endTangent, ref start);
            double sweep = end - start;
            if (Math.Abs(sweep) < Math.PI / 3 || Math.Abs(sweep) > Math.PI * 1.9 ||
                radius * Math.Abs(sweep) < minLen || !SupportedSweep(start, sweep, center, radius, width, startTangent, endTangent)) continue;
            var measuredWidths = new List<double>();
            int sections = (int)Math.Ceiling(radius * Math.Abs(sweep));
            for (int k = 0; k <= sections; k++)
            {
                double value = MeasureSection(start + sweep * k / sections, center, radius, width);
                if (value <= 0 && InBridge(start + sweep * k / sections)) continue;
                if (value <= 0 && ((startTangent && k * radius * Math.Abs(sweep) / sections <= width) ||
                    (endTangent && (sections - k) * radius * Math.Abs(sweep) / sections <= width))) continue;
                measuredWidths.Add(value);
            }
            if (measuredWidths.Any(value => value <= 0)) continue;
            double measuredWidth = Median(measuredWidths);
            if (measuredWidth > thin || Math.Abs(measuredWidth - width) > Math.Max(2, width * .15)) continue;
            width = measuredWidth;
            strokes.Add(ArcStroke(center, radius, start, sweep, width, ink));
            foreach (var member in members) owned.Add(member);
            // A continuation through a merged band explains far-side ridge chains that
            // never joined by endpoint. A chain is this arc only if all of it lies
            // inside the accepted sweep and band, and most of it on the centreline;
            // the straight side's own ridge leaves the band and keeps its stroke.
            if (bridges.Count > 0)
                foreach (var (run, runClosed) in chains)
                {
                    if (runClosed || run.Count < 2 || owned.Contains(run)) continue;
                    int onCircle = 0;
                    bool inside = true;
                    foreach (int q in run)
                    {
                        var p = Point(q);
                        double offset = Math.Abs((p - center).Length - radius);
                        double along = Delta(Angle(p) - start) * Math.Sign(sweep);
                        if (along < 0) along += 2 * Math.PI;
                        if (offset > width || along > Math.Abs(sweep)) { inside = false; break; }
                        if (offset <= width / 2) onCircle++;
                    }
                    if (inside && onCircle * 2 >= run.Count) owned.Add(run);
                }

            // The merged band that forced this continuation also hides the straight side
            // that made it. Explain that side from source as the other half of the union.
            if (bridges.Count > 0)
                RecoverAttachedSides(center, radius, start, sweep, width, bridges, chains, owned, At, thin, minLen, ink, w, strokes, ct);

            double Angle(Vec2 p) => DetMath.Atan2(p.Y - center.Y, p.X - center.X);
            // A near-tangent straight side paints over the circular band, so radial
            // sections there are thick and Grow stops at the line's foot. Explain that
            // band as a union: one longer arc plus the straight side. Continue only if
            // (1) the band is painted along the arc centreline and is no wider than the
            // two side-by-side primitives, (2) a thin, centred circular run of the same
            // centre, radius and width resumes beyond it, and (3) the longer arc newly
            // explains that far-side ink while inventing almost no paper. Gaps, walls,
            // transverse crossings and tangent stops fail one of these and stay fill.
            double Continue(double angle, double sign, ref bool tangent, ref double other)
            {
                // A bounded set of circle hypotheses: the accepted fit, and unbiased radial
                // midpoints of the observed near arc with and without its foot, where the
                // coincident straight side biases sections outward.
                var hypotheses = new List<(Vec2 Center, double Radius)> { (center, radius) };
                double length = Math.Abs(angle - other) * radius;
                foreach (double margin in new[] { 0, Math.Sqrt(2 * radius * width) })
                {
                    var near = new List<Vec2>();
                    for (double t = margin > 0 ? width : 0; t <= length - margin; t += 1)
                    {
                        double a = other + (angle - other) * t / Math.Max(1, length);
                        if (MeasureArcSection(a, center, radius, width, out double shift) > 0)
                            near.Add(center + new Vec2(DetMath.Cos(a), DetMath.Sin(a)) * (radius + shift));
                    }
                    if (near.Count >= 24 && Fit(near, out var nearCenter, out double nearRadius) &&
                        (nearCenter - center).Length <= 3 && Math.Abs(nearRadius - radius) <= 3)
                        hypotheses.Add((nearCenter, nearRadius));
                }
                foreach (var hypothesis in hypotheses)
                {
                    double result = Attempt(hypothesis.Center, hypothesis.Radius, angle, sign, ref tangent, ref other);
                    if (result != angle) return result;
                }
                return angle;
            }
            double Attempt(Vec2 c, double r, double angle, double sign, ref bool tangent, ref double other)
            {
                // Two short refits: a partial-circle fit drifts away from its observed
                // span, so confirm the far run against a fit that includes it.
                for (int pass = 0; pass < 3; pass++)
                {
                    var (thick, farStart, farEnd, inRun) = Walk(c, r, angle, sign, other);
                    if (thick < 2 || !inRun) return angle;
                    double farLength = Math.Abs(farEnd - farStart) * r;
                    var observed = new List<Vec2>();
                    var farObserved = new List<Vec2>();
                    double nearLength = Math.Abs(angle - other) * r;
                    for (double t = 0; t <= nearLength; t += 1)
                        AddMiddle(other + (angle - other) * t / Math.Max(1, nearLength), observed);
                    for (double t = 0; t <= farLength; t += 1)
                        AddMiddle(farStart + sign * t / r, farObserved);
                    void AddMiddle(double a, List<Vec2> into)
                    {
                        double value = MeasureArcSection(a, c, r, width, out double shift);
                        if (value > 0) into.Add(c + new Vec2(DetMath.Cos(a), DetMath.Sin(a)) * (r + shift));
                    }
                    if (farObserved.Count < 2 || observed.Count < 24) return angle;
                    if (!Fit(observed.Concat(farObserved).ToList(), out var refit, out double refitRadius) ||
                        (refit - center).Length > 3 || Math.Abs(refitRadius - radius) > 3) return angle;
                    bool settled = (refit - c).Length <= .25 && Math.Abs(refitRadius - r) <= .25;
                    if (!settled && pass < 2) { c = refit; r = refitRadius; continue; }
                    if (farLength < 3 * width || farObserved.Count < 2 * width) return angle;
                    // The far run must be this circle, not a parallel or offset band.
                    if (farObserved.Any(q => Math.Abs((q - c).Length - r) > 1.5) ||
                        observed.Any(q => Math.Abs((q - c).Length - r) > 1.5)) return angle;
                    // Union score against source ink over the whole added sweep: the
                    // continuing arc must newly explain far ink while inventing no paper.
                    int samples = 0, invented = 0, explained = 0;
                    double total = Math.Abs(farEnd - angle) * r, half = Math.Max(.5, width / 2 - 1);
                    double bridgeLength = Math.Abs(farStart - angle) * r;
                    for (double t = 0; t <= total; t += .5)
                    {
                        double a = angle + sign * t / r;
                        var n = new Vec2(DetMath.Cos(a), DetMath.Sin(a));
                        for (double o = -half; o <= half; o += 1)
                        {
                            if (--budget < 0) return angle;
                            samples++;
                            if (!At(c + n * (r + o))) invented++;
                            else if (t >= bridgeLength) explained++;
                        }
                    }
                    if (invented > samples * .01 || explained - 2 * invented < 10 * width) return angle;
                    // Walk angles are already in the confirmed circle's frame; re-express
                    // this tip, the opposite endpoint and earlier bridges in it too.
                    var otherPoint = center + new Vec2(DetMath.Cos(other), DetMath.Sin(other)) * radius;
                    var anglePoint = center + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * radius;
                    double newAngle = DetMath.Atan2(anglePoint.Y - c.Y, anglePoint.X - c.X);
                    newAngle = angle + Delta(newAngle - angle);
                    double newOther = DetMath.Atan2(otherPoint.Y - c.Y, otherPoint.X - c.X);
                    newOther = other + Delta(newOther - other);
                    for (int b = 0; b < bridges.Count; b++)
                        bridges[b] = (bridges[b].Lo + Delta(Shifted(bridges[b].Lo) - bridges[b].Lo),
                            bridges[b].Hi + Delta(Shifted(bridges[b].Hi) - bridges[b].Hi));
                    double Shifted(double a)
                    {
                        var q = center + new Vec2(DetMath.Cos(a), DetMath.Sin(a)) * radius;
                        return DetMath.Atan2(q.Y - c.Y, q.X - c.X);
                    }
                    center = c; radius = r; other = newOther;
                    bridges.Add((Math.Min(newAngle, farStart), Math.Max(newAngle, farStart)));
                    tangent = false;
                    // The walk is bounded; a far run still thin at that bound continues
                    // on the same confirmed circle until its own source end.
                    double limit = newOther + sign * Math.PI * 1.85, step = .5 / r;
                    while ((limit - farEnd) * sign > step && Section(farEnd + sign * step, c, r, width))
                        farEnd += sign * step;
                    return farEnd;
                }
                return angle;
            }
            // At the foot the two bands coincide and sections are still thin; the merged
            // band thickens as the straight side departs from the circle. Raster phase can
            // open a brief thin window inside that band, so the far run is the last thin
            // run before paper or the walk bound.
            (int Thick, double FarStart, double FarEnd, bool InRun) Walk(Vec2 c, double r, double angle, double sign, double other)
            {
                int maxBand = (int)Math.Ceiling(3 * Math.Sqrt(2 * r * width) / .5);
                double room = Math.PI * 1.85 - Math.Abs(angle - other);
                int maxSteps = (int)Math.Min(Math.Ceiling(Math.Min(Math.PI / 2, room) * r / .5), 4096);
                int thick = 0, after = 0;
                double farStart = angle, farEnd = angle, step = .5 / r;
                bool inRun = false;
                for (int k = 1; k <= maxSteps; k++)
                {
                    double next = angle + sign * step * k;
                    if (Section(next, c, r, width))
                    {
                        if (thick == 0) continue;
                        if (!inRun || after > 0) { farStart = next; inRun = true; after = 0; }
                        farEnd = next;
                        continue;
                    }
                    if (!Painted(next, c, r))
                        break;
                    if (++thick > maxBand) { inRun = false; break; }
                    if (inRun) after++;
                }
                // A short contaminated tail (an end cap or touching fill) still ends the
                // run; a long one means the run was only a window inside the band.
                if (after * .5 > width) inRun = false;
                return (thick, farStart, farEnd, inRun);
            }
            bool Painted(double a, Vec2 c, double r)
            {
                var n = new Vec2(DetMath.Cos(a), DetMath.Sin(a));
                var p = c + n * r;
                for (int band = -2; band <= 2; band++)
                    if (!At(p + n * (band * width * .2))) return false;
                double inside = 0, outside = 0, limit = 2 * width + 1;
                while (inside <= limit && At(p - n * inside)) inside += .5;
                while (outside <= limit && At(p + n * outside)) outside += .5;
                budget -= (int)((inside + outside) * 2);
                return inside <= limit && outside <= limit && inside + outside - .5 <= 2.2 * width + 1;
            }
            double Trim(double angle, double sign)
            {
                for (int k = 0; k <= 4 * width; k++)
                {
                    double candidate = angle + sign * increment * k;
                    if (Section(candidate, center, radius, width)) return candidate;
                }
                return angle;
            }
            double Grow(double angle, double sign, out bool tangent)
            {
                tangent = false;
                double grown = angle;
                int count = (int)Math.Ceiling(4 * width / .5);
                for (int k = 1; k <= count; k++)
                {
                    double next = angle + sign * increment * k;
                    if (!Section(next, center, radius, width)) break;
                    grown = next;
                }
                // A tangent side continues to fill radial sections after the circular
                // band has ended. Read its remote centreline directly from source:
                // ridge endpoint buckets can omit the side at this very transition.
                // The observation length scales with sqrt(radius * width), so a real
                // circular continuation cannot masquerade as a straight tangent.
                double reach = Math.Max(6 * width, 3 * Math.Sqrt(radius * width));
                var tangentAngles = new List<double>();
                for (double d = -4 * width; d <= 4 * width; d += 1)
                {
                    double a = grown + sign * d / radius;
                    var normal = new Vec2(DetMath.Cos(a), DetMath.Sin(a));
                    var axis = new Vec2(-normal.Y, normal.X) * sign;
                    var origin = center + normal * radius;
                    var observed = new List<Vec2>();
                    for (int k = 0; k <= 16; k++)
                    {
                        var p = origin + axis * (reach * (1.0 / 3 + k / 24.0));
                        if (--budget < 0 || !At(p)) break;
                        double inside = 0, outside = 0;
                        while (inside <= width && At(p - normal * inside)) inside += .5;
                        while (outside <= width && At(p + normal * outside)) outside += .5;
                        double measured = inside + outside - .5;
                        if (inside > width || outside > width ||
                            Math.Abs(measured - width) > Math.Max(2.5, width * .15)) continue;
                        observed.Add(p + normal * ((outside - inside) * .5));
                    }
                    if (observed.Count < 12 || (observed[^1] - observed[0]).Length < reach * .5) continue;
                    var mean = observed.Aggregate(new Vec2(), (sum, q) => sum + q) * (1.0 / observed.Count);
                    double xxSum = 0, yySum = 0, xySum = 0;
                    foreach (var q in observed)
                    {
                        var v = q - mean;
                        xxSum += v.X * v.X; yySum += v.Y * v.Y; xySum += v.X * v.Y;
                    }
                    double lineAngle = .5 * DetMath.Atan2(2 * xySum, xxSum - yySum);
                    var fitted = new Vec2(DetMath.Cos(lineAngle), DetMath.Sin(lineAngle));
                    if (observed.Any(q => Math.Abs(Cross(q - mean, fitted)) > .75)) continue;
                    var foot = mean + fitted * (center - mean).Dot(fitted);
                    if (Math.Abs((foot - center).Length - radius) > 1.5) continue;
                    double target = angle + Delta(Angle(foot) - angle);
                    if (Math.Abs(target - grown) * radius > 4 * width) continue;
                    if ((target - angle) * sign < 0)
                    {
                        // A ridge can already overrun into the straight attachment.
                        // A backward correction is safe only while that overrun is
                        // painted by this same source-supported straight side, and
                        // no distinct circular continuation exists beyond its band.
                        if (Section(target + sign * 4 * width / radius, center, radius, width)) continue;
                        bool redundant = true;
                        int overlapSteps = (int)Math.Ceiling(Math.Abs(angle - target) * radius * 2);
                        for (int k = 1; k <= overlapSteps; k++)
                        {
                            double a0 = target + (angle - target) * k / overlapSteps;
                            var point = center + new Vec2(DetMath.Cos(a0), DetMath.Sin(a0)) * radius;
                            if (Math.Abs(Cross(point - foot, fitted)) > width / 2 || !At(point))
                            { redundant = false; break; }
                            var radial = new Vec2(DetMath.Cos(a0), DetMath.Sin(a0));
                            for (int band = -1; band <= 1; band += 2)
                            {
                                var edge = point + radial * (band * width * .4);
                                if (Math.Abs(Cross(edge - foot, fitted)) > width / 2 + .5 && At(edge))
                                { redundant = false; break; }
                            }
                            if (!redundant) break;
                        }
                        if (!redundant) continue;
                    }
                    if (fitted.Dot(axis) < 0) fitted = fitted * -1;
                    // Require the actual attachment, including the interval before
                    // the remote fit. Nearby parallel sides and crossings do not join.
                    bool connected = true;
                    for (double t = 0; t <= reach; t += .5)
                        if (--budget < 0 || !At(foot + fitted * t)) { connected = false; break; }
                    if (!SupportedSweep(angle, target - angle, center, radius, width, false, true)) connected = false;
                    if (!connected) continue;
                    tangentAngles.Add(target);
                }
                if (tangentAngles.Count == 0) return grown;
                tangentAngles.Sort();
                // Raster phase changes individual cross-section fits. Require a
                // coherent central population; competing tangent branches stay ambiguous.
                if ((tangentAngles[tangentAngles.Count * 3 / 4] - tangentAngles[tangentAngles.Count / 4]) * radius > 2)
                    return angle;
                tangent = true;
                return Median(tangentAngles);
            }
        }
        return owned;

        bool InBridge(double a) => bridges.Any(b => a >= b.Lo - 1e-9 && a <= b.Hi + 1e-9);
        bool Uniform(List<int> run, double width)
        {
            if (run.Count == 0) return false;
            var widths = run.Select(Width).Order().ToList();
            return widths[^1] <= thin && widths[widths.Count / 10] >= .8 * width &&
                widths[widths.Count * 9 / 10] <= 1.2 * width;
        }
        bool Fit(List<Vec2> points, out Vec2 center, out double radius) =>
            CenterlineCircles.FitCavity(points, out center, out radius, out _, out _, out _, .65, 1.5);
        bool At(Vec2 p) => p.X >= 0 && p.Y >= 0 && p.X < w && p.Y < h &&
            inkMask[(int)p.Y * w + (int)p.X] && comp[(int)p.Y * w + (int)p.X] == id;
        bool Section(double angle, Vec2 center, double radius, double width) =>
            MeasureSection(angle, center, radius, width) > 0;
        double MeasureSection(double angle, Vec2 center, double radius, double width) =>
            MeasureArcSection(angle, center, radius, width, out _);
        double MeasureArcSection(double angle, Vec2 center, double radius, double width, out double shift)
        {
            shift = 0;
            if (--budget < 0) return 0;
            var n = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle));
            var p = center + n * radius;
            for (int band = -2; band <= 2; band++)
                if (!At(p + n * (band * width * .2))) return 0;
            double inside = 0, outside = 0;
            while (inside <= width && At(p - n * inside)) inside += .5;
            while (outside <= width && At(p + n * outside)) outside += .5;
            double measured = inside + outside - .5;
            shift = (outside - inside) * .5;
            return inside <= width && outside <= width && Math.Abs(inside - outside) <= 3 &&
                Math.Abs(measured - width) <= Math.Max(2.5, width * .15) ? measured : 0;
        }
        bool SupportedSweep(double start, double sweep, Vec2 center, double radius, double width, bool startTangent = false, bool endTangent = false)
        {
            int steps = (int)Math.Ceiling(Math.Abs(sweep) * radius * 2);
            if (steps > 4096 || steps > budget) return false;
            for (int k = 0; k <= steps; k++)
            {
                double a = start + sweep * k / Math.Max(1, steps);
                if (Section(a, center, radius, width)) continue;
                if (InBridge(a))
                {
                    if (--budget < 0 || !At(center + new Vec2(DetMath.Cos(a), DetMath.Sin(a)) * radius)) return false;
                    continue;
                }
                if (!((startTangent && k * .5 <= width) || (endTangent && (steps - k) * .5 <= width))) return false;
                var n = new Vec2(DetMath.Cos(a), DetMath.Sin(a));
                for (int band = -1; band <= 1; band++)
                    if (--budget < 0 || !At(center + n * (radius + band * width * .25))) return false;
            }
            return true;
        }
    }

    // Line+arc union at an accepted arc's merged band. Pixels there cannot say which
    // primitive painted them, so the side is measured where it is alone: ink beyond the
    // accepted arc's band, then thin centred cross-sections along its remote run. Only
    // then is it extended back through the overlap, on source ink alone. Paper, foreign
    // ink beside the union, a side that never reaches the arc band, a steep crossing or
    // an uneven or curved run rejects the hypothesis and leaves dev's output (chains and
    // fill) untouched. The near end cannot be observed exactly: it is the arc join, or
    // earlier if ink outside the arc's own band still shows the side there.
    static void RecoverAttachedSides(Vec2 center, double radius, double start, double sweep, double arcWidth,
        List<(double Lo, double Hi)> bridges, List<(List<int> Pixels, bool Closed)> chains, HashSet<List<int>> owned,
        Func<Vec2, bool> at, double thin, int minLen, int ink, int w, List<StrokePath> strokes, CancellationToken ct)
    {
        const int maxResidual = 1 << 14, maxClusters = 4;
        const double maxCrossing = .42; // about 25 degrees between side and arc tangent
        int budget = 1 << 21, clusters = 0;
        double band = arcWidth / 2 + 1.5, cross = Math.Sqrt(2 * radius * arcWidth);
        double reach = Math.Max(3 * cross, 16 * arcWidth), limit = Math.Max(thin, 3 * arcWidth);
        Vec2 Radial(double a) => new(DetMath.Cos(a), DetMath.Sin(a));
        Vec2 Center(int q) => new(q % w + .5, q / w + .5);
        bool Ink(Vec2 p) => --budget >= 0 && at(p);
        var seen = new HashSet<int>();
        var accepted = new List<(Vec2 A, Vec2 B, double Width)>();
        foreach (var (lo, hi) in bridges)
        {
            var origin = center + Radial((lo + hi) / 2) * radius;
            double margin = cross / radius;
            for (double a = lo - margin; a <= hi + margin; a += .5 / radius)
                for (int side = -1; side <= 1; side += 2)
                    for (double o = band + .5; o <= band + arcWidth; o += 1)
                    {
                        var p = center + Radial(a) * (radius + side * o);
                        int seed = (int)p.Y * w + (int)p.X;
                        if (!Ink(p) || !seen.Add(seed)) continue;
                        if (clusters++ >= maxClusters) return;
                        ct.ThrowIfCancellationRequested();
                        var far = Residual(seed, origin);
                        if (far != null && Measure(far, origin, out var near, out var remote, out double width))
                        {
                            strokes.Add(new StrokePath { PaletteIndex = ink, Width = width, Closed = false,
                                Segments = [CubicBezier.Line(remote, near)] });
                            accepted.Add((near, remote, width));
                        }
                    }
        }
        if (accepted.Count == 0) return;
        // Ridges the union explains must not also emit: no partial duplicate of the side.
        foreach (var (run, closed) in chains)
        {
            if (closed || owned.Contains(run)) continue;
            if (run.All(q => Explained(Center(q)))) owned.Add(run);
        }

        bool Explained(Vec2 p)
        {
            double along = Delta(DetMath.Atan2(p.Y - center.Y, p.X - center.X) - start) * Math.Sign(sweep);
            if (along < 0) along += 2 * Math.PI;
            if (along <= Math.Abs(sweep) && Math.Abs((p - center).Length - radius) <= band) return true;
            foreach (var (a, b, width) in accepted)
            {
                var d = (b - a).Normalized();
                double t = (p - a).Dot(d);
                if (t >= -width && t <= (b - a).Length + width && Math.Abs(Cross(p - a, d)) <= width / 2 + 1.5) return true;
            }
            return false;
        }

        // Bounded flood over ink beyond the arc's band; returns the pixels clear of it.
        List<Vec2>? Residual(int seed, Vec2 origin)
        {
            var queue = new Queue<int>();
            var far = new List<Vec2>();
            queue.Enqueue(seed);
            int visited = 0;
            while (queue.Count > 0)
            {
                int q = queue.Dequeue();
                if (++visited > maxResidual) return null; // a solid, not a side
                var p = Center(q);
                if (Math.Abs((p - center).Length - radius) >= 1.5 * arcWidth) far.Add(p);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var n = p + new Vec2(dx, dy);
                        int index = (int)n.Y * w + (int)n.X;
                        if (n.X < 0 || n.Y < 0 || seen.Contains(index) || !Ink(n) ||
                            Math.Abs((n - center).Length - radius) <= band || (n - origin).Length > reach) continue;
                        seen.Add(index);
                        queue.Enqueue(index);
                    }
            }
            return far;
        }

        bool Measure(List<Vec2> far, Vec2 origin, out Vec2 nearPoint, out Vec2 remotePoint, out double width)
        {
            nearPoint = remotePoint = default;
            width = 0;
            if (far.Count < 4 * arcWidth * arcWidth || !Principal(far, out var m, out var d, out double elongation) || elongation < 9) return false;
            if ((m - origin).Dot(d) < 0) d = d * -1;
            double lo = far.Min(p => (p - m).Dot(d)), hi = far.Max(p => (p - m).Dot(d));
            for (int pass = 0; pass < 3; pass++)
            {
                var (centers, widths) = Sections(m, d, lo, hi);
                if (centers.Count < .8 * (hi - lo) || centers.Count < 4 * arcWidth || !Principal(centers, out m, out var refined, out _)) return false;
                d = refined.Dot(d) < 0 ? refined * -1 : refined;
                width = Median(widths);
                if (width < 2 || width > thin) return false;
                widths.Sort();
                if (widths[widths.Count / 10] < width - Math.Max(2, .15 * width) ||
                    widths[widths.Count * 9 / 10] > width + Math.Max(2, .15 * width)) return false;
                lo = centers.Min(p => (p - m).Dot(d));
                hi = centers.Max(p => (p - m).Dot(d));
                // Follow the thin run to its real remote end: the long lever arm is the fit.
                double full = hi, tip = hi;
                for (double t = hi + .5; t <= hi + 8192; t += .5)
                {
                    double value = Across(m + d * t, d, out double shift);
                    if (value <= 0 || !at(m + d * t)) break;
                    if (Math.Abs(value - width) > Math.Max(2.5, .15 * width) || Math.Abs(shift) > 2)
                    {
                        // Past a narrowing cap, or a contaminated section: only a cap may follow.
                        if (t - tip > width) break;
                        continue;
                    }
                    tip = t;
                    if (value >= width - 1) full = t;
                }
                hi = tip;
                var all = Sections(m, d, lo, hi);
                if (all.Centers.Count < .9 * (hi - lo) || !Principal(all.Centers, out m, out refined, out _)) return false;
                d = refined.Dot(d) < 0 ? refined * -1 : refined;
                if (all.Centers.Any(p => Math.Abs(Cross(p - m, d)) > .85)) return false;
                lo = all.Centers.Min(p => (p - m).Dot(d));
                hi = all.Centers.Max(p => (p - m).Dot(d));
                if (pass < 2) continue;
                if (hi - lo < Math.Max(minLen, 8 * width)) return false;
                // Backward through the overlap. Overlap is allowed; paper and ink that
                // neither primitive explains stop the walk.
                double walk = lo, evidence = lo, halfWidth = width / 2, inset = Math.Max(.5, halfWidth - 1);
                double furthest = lo - 4 * reach;
                for (double t = lo - .5; t >= furthest; t -= .5)
                {
                    var q = m + d * t;
                    var normal = new Vec2(-d.Y, d.X);
                    if (!Ink(q) || !Ink(q + normal * inset) || !Ink(q - normal * inset) || Foreign(q, normal)) break;
                    walk = t;
                    foreach (double s in new[] { -1.0, 1 })
                    {
                        var edge = q + normal * (s * (halfWidth - .75));
                        if (Ink(edge) && Math.Abs((edge - center).Length - radius) > arcWidth / 2 + 1) evidence = t;
                    }
                }
                // Geometric join: the line's backward crossing of the circle, else its foot.
                var from = m - center;
                double b = from.Dot(d), disc = b * b - from.LengthSq + radius * radius;
                double back = disc >= 0 ? -b - Math.Sqrt(disc) : -b;
                var joint = m + d * back;
                if (Math.Abs((joint - center).Length - radius) > (arcWidth + width) / 2) return false;
                if (Math.Abs((joint - center).Normalized().Dot(d)) > maxCrossing) return false;
                var reached = m + d * walk;
                if (Math.Abs((reached - center).Length - radius) > (arcWidth + width) / 2) return false;
                if (!InsideSweep(joint) && !InsideSweep(reached)) return false;
                double near = Math.Max(walk, Math.Min(back, evidence));
                // A butt end has full sections up to its edge; a round one tapers past them.
                double painted = hi;
                while (painted < hi + width && Ink(m + d * (painted + .5))) painted += .5;
                double remote = painted - full >= width / 4 ? Math.Max(painted - halfWidth, hi - halfWidth) : full;
                remote = Math.Min(remote, hi);
                if (remote - near < Math.Max(minLen, 8 * width)) return false;
                int samples = 0, paper = 0;
                for (double t = near; t <= remote; t += .5)
                    for (double o = -inset; o <= inset; o += 1)
                    {
                        samples++;
                        if (!Ink(m + d * t + new Vec2(-d.Y, d.X) * o)) paper++;
                    }
                if (budget < 0 || paper > samples * .01) return false;
                nearPoint = m + d * near;
                remotePoint = m + d * remote;
                return true;
            }
            return false;

            bool InsideSweep(Vec2 p)
            {
                double along = Delta(DetMath.Atan2(p.Y - center.Y, p.X - center.X) - start) * Math.Sign(sweep);
                if (along < 0) along += 2 * Math.PI;
                return along <= Math.Abs(sweep);
            }
        }

        // The union's only explained thick section is the arc band beside the line.
        bool Foreign(Vec2 q, Vec2 normal)
        {
            foreach (double s in new[] { -1.0, 1 })
            {
                double o = 0;
                while (o <= limit && Ink(q + normal * (s * o))) o += .5;
                if (o > limit) return true;
                var last = q + normal * (s * (o - .5));
                if (o - .5 > limit / 2 && Math.Abs((last - center).Length - radius) > band) return true;
            }
            return false;
        }

        double Across(Vec2 q, Vec2 d, out double shift)
        {
            shift = 0;
            var normal = new Vec2(-d.Y, d.X);
            double inside = 0, outside = 0;
            while (inside <= limit && Ink(q - normal * inside)) inside += .5;
            while (outside <= limit && Ink(q + normal * outside)) outside += .5;
            if (inside == 0 || outside == 0 || inside > limit || outside > limit) return 0;
            shift = (outside - inside) / 2;
            return inside + outside - .5;
        }

        (List<Vec2> Centers, List<double> Widths) Sections(Vec2 m, Vec2 d, double lo, double hi)
        {
            var centers = new List<Vec2>();
            var widths = new List<double>();
            var normal = new Vec2(-d.Y, d.X);
            // A long run can hold 8192 sections at most, in keeping with the walk bound.
            for (double t = lo; t <= hi && centers.Count < 8192; t += 1)
            {
                var q = m + d * t;
                double value = Across(q, d, out double shift);
                if (value <= 0 || value > thin + 2) continue;
                centers.Add(q + normal * shift);
                widths.Add(value);
            }
            return (centers, widths);
        }
    }

    // Major axis of a point set; elongation is its variance ratio.
    static bool Principal(List<Vec2> points, out Vec2 mean, out Vec2 axis, out double elongation)
    {
        mean = axis = default;
        elongation = 0;
        if (points.Count < 2) return false;
        foreach (var p in points) mean = mean + p;
        mean = mean * (1.0 / points.Count);
        double xx = 0, xy = 0, yy = 0;
        foreach (var p in points)
        {
            var v = p - mean;
            xx += v.X * v.X; xy += v.X * v.Y; yy += v.Y * v.Y;
        }
        double angle = .5 * DetMath.Atan2(2 * xy, xx - yy), root = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        axis = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle));
        double major = (xx + yy + root) / 2, minor = (xx + yy - root) / 2;
        elongation = major / Math.Max(minor, 1e-9);
        return major > 0;
    }

    static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    static double Delta(double angle) => DetMath.Atan2(DetMath.Sin(angle), DetMath.Cos(angle));

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
