namespace Vecto.Core;

/// <summary>
/// Least-squares cubic Bezier fitting after Philip J. Schneider, "An Algorithm for
/// Automatically Fitting Digitized Curves" (Graphics Gems, 1990): fit with chord-length
/// parameterization, Newton-Raphson reparameterization, recursive split at max error.
/// Corners and junctions become segment boundaries with one-sided tangents so they stay sharp.
/// </summary>
internal static class BezierFitter
{
    public static List<CubicBezier> FitChain(List<Vec2> pts, bool closed, List<int> corners, double tolSq)
    {
        var output = FitChainCore(pts, closed, corners, tolSq);
        MergeCollinearLines(output, closed, tolSq);
        return output;
    }

    /// <summary>
    /// Consecutive straight segments whose shared vertex sits on the merged chord collapse
    /// into one line — heals sides split by the chain seam or by max-error splits. The
    /// centerline regularizer reuses this to collapse the pieces a gap-jump merge
    /// concatenates (VEC-52).
    /// </summary>
    internal static void MergeCollinearLines(List<CubicBezier> segs, bool closed, double tolSq)
    {
        bool merged = true;
        while (merged && segs.Count > 1)
        {
            merged = false;
            int limit = closed ? segs.Count : segs.Count - 1;
            for (int i = 0; i < limit && segs.Count > 1; i++)
            {
                int j = (i + 1) % segs.Count;
                var a = segs[i];
                var b = segs[j];
                if (!a.IsLine() || !b.IsLine()) continue;
                var start = a.P0;
                var mid = a.P3;
                var end = b.P3;
                var chord = end - start;
                double len2 = chord.LengthSq;
                if (len2 < 1e-12) continue;
                double t = Math.Clamp((mid - start).Dot(chord) / len2, 0, 1);
                if (mid.DistSq(start + chord * t) > tolSq) continue;
                segs[i] = CubicBezier.Line(start, end);
                segs.RemoveAt(j);
                merged = true;
                if (j < i) i--;
                limit = closed ? segs.Count : segs.Count - 1;
            }
        }
    }

    static List<CubicBezier> FitChainCore(List<Vec2> pts, bool closed, List<int> corners, double tolSq)
    {
        var output = new List<CubicBezier>();
        if (pts.Count < 2) return output;

        if (closed && corners.Count > 0)
        {
            // rotate the ring so a corner is the seam, then fit corner-to-corner like an open chain
            int n = pts.Count - 1, c0 = corners[0];
            var rotated = new List<Vec2>(pts.Count);
            for (int i = 0; i <= n; i++) rotated.Add(pts[(c0 + i) % n]);
            var splits = corners
                .Select(c => ((c - c0) % n + n) % n)
                .Where(c => c != 0)
                .OrderBy(c => c)
                .ToList();
            FitOpen(rotated, splits, tolSq, output);
        }
        else if (closed)
        {
            int n = pts.Count - 1;
            if (n < 2) return output;
            // wrap-around tangent keeps the seam C1-continuous
            var t1 = (pts[1] - pts[n - 1]).Normalized();
            if (t1.LengthSq < 0.5) t1 = (pts[1] - pts[0]).Normalized();
            FitCubic(pts, 0, pts.Count - 1, t1, -t1, tolSq, output, 0);
        }
        else
        {
            FitOpen(pts, corners, tolSq, output);
        }
        return output;
    }

    static void FitOpen(List<Vec2> pts, List<int> splits, double tolSq, List<CubicBezier> output)
    {
        int prev = 0;
        foreach (var s in splits.Append(pts.Count - 1))
        {
            if (s <= prev) continue;
            FitCubic(pts, prev, s, LeftTangent(pts, prev, s), RightTangent(pts, s, prev), tolSq, output, 0);
            prev = s;
        }
    }

    static Vec2 LeftTangent(List<Vec2> pts, int i, int limit)
    {
        for (int j = i + 1; j <= limit; j++)
        {
            var t = pts[j] - pts[i];
            if (t.LengthSq > 1e-12) return t.Normalized();
        }
        return new Vec2(1, 0);
    }

    static Vec2 RightTangent(List<Vec2> pts, int i, int limit)
    {
        for (int j = i - 1; j >= limit; j--)
        {
            var t = pts[j] - pts[i];
            if (t.LengthSq > 1e-12) return t.Normalized();
        }
        return new Vec2(-1, 0);
    }

    static void FitCubic(List<Vec2> d, int first, int last, Vec2 tHat1, Vec2 tHat2, double tolSq, List<CubicBezier> output, int depth)
    {
        if (last - first + 1 == 2)
        {
            output.Add(HeuristicSegment(d[first], d[last], tHat1, tHat2));
            return;
        }
        // simplest-model-first: a chord that explains every point within tolerance IS a
        // straight line — the residual staircase wobble is quantization noise, not signal.
        // (Chords can tilt up to the lattice error of their pinned endpoints; sub-pixel
        // junction relaxation is the roadmap fix that would tighten this further.)
        if (TryLine(d, first, last, tolSq, output)) return;
        if (depth > 28)
        {
            for (int i = first; i < last; i++) output.Add(CubicBezier.Line(d[i], d[i + 1]));
            return;
        }
        // circles get first claim on long spans — a cubic could squeak under tolerance on a
        // quarter-ring, but the arc is the true model (and whole rings land here too)
        if (last - first + 1 >= 16 && TryArc(d, first, last, tolSq, output)) return;
        // greedy straight-run extraction: icon outlines are [line][cap][line] with no corner
        // between — peel truly straight runs off either end instead of bowing one cubic over all
        double runTolSq = tolSq * 0.35;
        const int MinRun = 16;
        int pj = LongestLinePrefix(d, first, last, runTolSq, MinRun);
        if (pj - first >= MinRun && pj < last)
        {
            output.Add(CubicBezier.Line(d[first], d[pj]));
            FitCubic(d, pj, last, LeftTangent(d, pj, last), tHat2, tolSq, output, depth + 1);
            return;
        }
        int sj = LongestLineSuffix(d, first, last, runTolSq, MinRun);
        if (last - sj >= MinRun && sj > first)
        {
            FitCubic(d, first, sj, tHat1, RightTangent(d, sj, first), tolSq, output, depth + 1);
            output.Add(CubicBezier.Line(d[sj], d[last]));
            return;
        }
        var u = ChordLengthParameterize(d, first, last);
        var bez = GenerateBezier(d, first, last, u, tHat1, tHat2);
        var (err, split) = ComputeMaxError(d, first, last, bez, u);
        if (err < tolSq)
        {
            output.Add(bez);
            return;
        }
        if (err < tolSq * 16)
        {
            for (int i = 0; i < 4; i++)
            {
                u = Reparameterize(d, first, last, u, bez);
                bez = GenerateBezier(d, first, last, u, tHat1, tHat2);
                (err, split) = ComputeMaxError(d, first, last, bez, u);
                if (err < tolSq)
                {
                    output.Add(bez);
                    return;
                }
            }
        }
        // one cubic can't hold it — a circular arc often can (caps, dots, ring segments,
        // and whole circles: a closed no-corner ring lands here with a zero-length chord)
        if (TryArc(d, first, last, tolSq, output)) return;
        var tCenter = (d[split - 1] - d[split + 1]).Normalized();
        if (tCenter.LengthSq < 0.5) tCenter = (d[first] - d[last]).Normalized();
        if (tCenter.LengthSq < 0.5) tCenter = new Vec2(0, 1);
        FitCubic(d, first, split, tHat1, tCenter, tolSq, output, depth + 1);
        FitCubic(d, split, last, -tCenter, tHat2, tolSq, output, depth + 1);
    }

    static bool TryLine(List<Vec2> d, int first, int last, double tolSq, List<CubicBezier> output)
    {
        if (!LineFits(d, first, last, tolSq)) return false;
        output.Add(CubicBezier.Line(d[first], d[last]));
        return true;
    }

    static bool LineFits(List<Vec2> d, int a, int b, double tolSq)
    {
        var p0 = d[a];
        var ab = d[b] - p0;
        double len2 = ab.LengthSq;
        if (len2 < 1e-12) return false;
        for (int i = a + 1; i < b; i++)
        {
            double t = Math.Clamp((d[i] - p0).Dot(ab) / len2, 0, 1);
            if (d[i].DistSq(p0 + ab * t) > tolSq) return false;
        }
        return true;
    }

    static int LongestLinePrefix(List<Vec2> d, int first, int last, double tolSq, int minRun)
    {
        if (first + minRun > last || !LineFits(d, first, first + minRun, tolSq)) return first;
        int j = first + minRun;
        while (j + 8 <= last && LineFits(d, first, j + 8, tolSq)) j += 8;
        while (j + 1 <= last && LineFits(d, first, j + 1, tolSq)) j++;
        return j;
    }

    static int LongestLineSuffix(List<Vec2> d, int first, int last, double tolSq, int minRun)
    {
        if (last - minRun < first || !LineFits(d, last - minRun, last, tolSq)) return last;
        int j = last - minRun;
        while (j - 8 >= first && LineFits(d, j - 8, last, tolSq)) j -= 8;
        while (j - 1 >= first && LineFits(d, j - 1, last, tolSq)) j--;
        return j;
    }

    /// <summary>
    /// Circular-arc hypothesis: algebraic least-squares circle (Kåsa) through the range;
    /// accepted when every point sits within tolerance of the circle. Emitted as ≤90°
    /// cubic pieces whose endpoints stay EXACTLY at the range endpoints (they are shared
    /// with neighboring segments and, at junctions, with other chains).
    /// </summary>
    static bool TryArc(List<Vec2> d, int first, int last, double tolSq, List<CubicBezier> output)
    {
        int n = last - first + 1;
        if (n < 6) return false;

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        for (int i = first; i <= last; i++)
        {
            double x = d[i].X, y = d[i].Y, z = x * x + y * y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            sxz += x * z; syz += y * z; sz += z;
        }
        // x² + y² = A·x + B·y + C  (linear least squares, 3×3 Cramer)
        double m00 = sxx, m01 = sxy, m02 = sx;
        double m10 = sxy, m11 = syy, m12 = sy;
        double m20 = sx, m21 = sy, m22 = n;
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-9) return false;
        double detA = sxz * (m11 * m22 - m12 * m21) - m01 * (syz * m22 - m12 * sz) + m02 * (syz * m21 - m11 * sz);
        double detB = m00 * (syz * m22 - m12 * sz) - sxz * (m10 * m22 - m12 * m20) + m02 * (m10 * sz - syz * m20);
        double detC = m00 * (m11 * sz - syz * m21) - m01 * (m10 * sz - syz * m20) + sxz * (m10 * m21 - m11 * m20);
        double ca = detA / det, cb = detB / det, cc = detC / det;
        var center = new Vec2(ca / 2, cb / 2);
        double r2 = cc + center.LengthSq;
        if (r2 <= 0.25 || r2 > 1e10) return false;
        double radius = Math.Sqrt(r2);

        double tol = Math.Sqrt(tolSq);
        for (int i = first; i <= last; i++)
        {
            double dev = Math.Abs((d[i] - center).Length - radius);
            if (dev > tol) return false;
        }

        // unwrapped total sweep (handles full rings, where the chord is zero)
        double sweep = 0;
        double prevAngle = DetMath.Atan2(d[first].Y - center.Y, d[first].X - center.X);
        for (int i = first + 1; i <= last; i++)
        {
            double ang = DetMath.Atan2(d[i].Y - center.Y, d[i].X - center.X);
            double delta = ang - prevAngle;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta <= -Math.PI) delta += 2 * Math.PI;
            sweep += delta;
            prevAngle = ang;
        }
        if (Math.Abs(sweep) < 0.05 || Math.Abs(sweep) > 2.05 * Math.PI) return false;

        var pStart = d[first];
        var pEnd = d[last];
        double rStart = (pStart - center).Length;
        double rEnd = (pEnd - center).Length;
        double angStart = DetMath.Atan2(pStart.Y - center.Y, pStart.X - center.X);
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        double pieceSweep = sweep / pieces;
        double k = 4.0 / 3.0 * DetMath.Tan(pieceSweep / 4);
        var e0 = pStart;
        for (int i = 0; i < pieces; i++)
        {
            double t1 = (i + 1) / (double)pieces;
            double a0 = angStart + pieceSweep * i;
            double a1 = angStart + pieceSweep * (i + 1);
            double r0 = rStart + (rEnd - rStart) * (i / (double)pieces);
            double r1 = rStart + (rEnd - rStart) * t1;
            var e1 = i == pieces - 1 ? pEnd : center + new Vec2(DetMath.Cos(a1), DetMath.Sin(a1)) * r1;
            var t0v = new Vec2(-DetMath.Sin(a0), DetMath.Cos(a0));
            var t1v = new Vec2(-DetMath.Sin(a1), DetMath.Cos(a1));
            output.Add(new CubicBezier(e0, e0 + t0v * (k * r0), e1 - t1v * (k * r1), e1));
            e0 = e1;
        }
        return true;
    }

    static CubicBezier HeuristicSegment(Vec2 p0, Vec2 p3, Vec2 t1, Vec2 t2)
    {
        double dist = (p3 - p0).Length / 3.0;
        return new CubicBezier(p0, p0 + t1 * dist, p3 + t2 * dist, p3);
    }

    static double[] ChordLengthParameterize(List<Vec2> d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (int i = 1; i < u.Length; i++)
            u[i] = u[i - 1] + (d[first + i] - d[first + i - 1]).Length;
        double total = u[^1];
        if (total < 1e-12)
        {
            for (int i = 0; i < u.Length; i++) u[i] = i / (double)(u.Length - 1);
            return u;
        }
        for (int i = 1; i < u.Length; i++) u[i] /= total;
        return u;
    }

    static CubicBezier GenerateBezier(List<Vec2> d, int first, int last, double[] u, Vec2 tHat1, Vec2 tHat2)
    {
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int i = 0; i < u.Length; i++)
        {
            double t = u[i], mt = 1 - t;
            double b0 = mt * mt * mt, b1 = 3 * mt * mt * t, b2 = 3 * mt * t * t, b3 = t * t * t;
            var a0 = tHat1 * b1;
            var a1 = tHat2 * b2;
            c00 += a0.Dot(a0);
            c01 += a0.Dot(a1);
            c11 += a1.Dot(a1);
            var tmp = d[first + i] - (d[first] * (b0 + b1) + d[last] * (b2 + b3));
            x0 += a0.Dot(tmp);
            x1 += a1.Dot(tmp);
        }
        double det = c00 * c11 - c01 * c01;
        double alphaL = 0, alphaR = 0;
        if (Math.Abs(det) > 1e-12)
        {
            alphaL = (x0 * c11 - x1 * c01) / det;
            alphaR = (c00 * x1 - c01 * x0) / det;
        }
        double segLen = (d[last] - d[first]).Length;
        double eps = 1e-6 * segLen;
        if (alphaL < eps || alphaR < eps || alphaL > 3 * segLen || alphaR > 3 * segLen)
        {
            double dist = segLen / 3.0;
            alphaL = alphaR = dist;
        }
        return new CubicBezier(d[first], d[first] + tHat1 * alphaL, d[last] + tHat2 * alphaR, d[last]);
    }

    static (double Err, int Split) ComputeMaxError(List<Vec2> d, int first, int last, CubicBezier bez, double[] u)
    {
        double max = 0;
        int split = (first + last) / 2;
        for (int i = 1; i < last - first; i++)
        {
            double distSq = bez.Eval(u[i]).DistSq(d[first + i]);
            if (distSq > max)
            {
                max = distSq;
                split = first + i;
            }
        }
        split = Math.Clamp(split, first + 1, last - 1);
        return (max, split);
    }

    static double[] Reparameterize(List<Vec2> d, int first, int last, double[] u, CubicBezier bez)
    {
        var u2 = new double[u.Length];
        for (int i = 0; i < u.Length; i++)
            u2[i] = NewtonRaphson(bez, d[first + i], u[i]);
        return u2;
    }

    static double NewtonRaphson(CubicBezier q, Vec2 p, double u)
    {
        var d10 = (q.P1 - q.P0) * 3;
        var d11 = (q.P2 - q.P1) * 3;
        var d12 = (q.P3 - q.P2) * 3;
        var d20 = (d11 - d10) * 2;
        var d21 = (d12 - d11) * 2;
        double mt = 1 - u;
        var qu = q.Eval(u);
        var q1u = d10 * (mt * mt) + d11 * (2 * mt * u) + d12 * (u * u);
        var q2u = d20 * mt + d21 * u;
        var diff = qu - p;
        double num = diff.Dot(q1u);
        double den = q1u.Dot(q1u) + diff.Dot(q2u);
        if (Math.Abs(den) < 1e-12) return u;
        return Math.Clamp(u - num / den, 0, 1);
    }
}
