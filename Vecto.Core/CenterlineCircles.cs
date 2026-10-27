using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Vecto.Tests")]

namespace Vecto.Core;

/// <summary>
/// Source-only circle hypotheses for a later, protected centerline integration. This does
/// not classify drawing/text roles, change labels, claim ink ownership, or emit strokes.
/// Coordinates use pixel cells [x,x+1) × [y,y+1), like the boundary geometry pipeline.
/// </summary>
internal static class CenterlineCircles
{
    internal sealed record Evidence(
        int CavityPixels, int BoundarySamples, double InnerRadius,
        double BoundaryRmsError, double BoundaryMaxError,
        int CircumferenceSamples, int SupportedSamples, int ConsistentWidthSamples,
        double WidthTolerance, double CavityAreaRatio);

    internal sealed record Proposal(
        int SourceComponentId, int CavitySeedIndex, Vec2 Center,
        double Radius, double Width, Evidence Evidence);

    /// <summary>
    /// Ink and componentIds must describe the SAME unmodified source observation. IDs are
    /// caller-owned connected-component identities (negative = unknown); paper IDs are ignored.
    /// Only explicitly eligible IDs may produce proposals. Any protected INK pixel vetoes
    /// its entire component, including remote attachments. An empty eligible set allows none.
    /// Callers must preserve this role decision and independently account for residual ink
    /// before integrating a proposal: a circle does not own all ink in its component or bbox.
    ///
    /// Conservatively requires an empty, bounded, round paper cavity; interior crossings,
    /// occlusion, open rings, and uncertain geometry return no proposal. Outside hatches,
    /// leaders, and solid attachments may contaminate at most 25% of the width rays.
    ///
    /// O(N) working memory: one N-byte visited plane and one 4N-byte flood queue, plus a
    /// blocked-ID set and O(radius) radial samples per candidate. No per-candidate image copy.
    /// Flooding/fitting is O(N); radial work is O(sum radius²) = O(N), since verified round
    /// cavities are disjoint and width searches are capped at 0.4 × inner radius (min 4px).
    /// </summary>
    internal static IReadOnlyList<Proposal> Propose(
        bool[] ink, int[] componentIds, IReadOnlySet<int> eligibleComponents,
        int width, int height, bool[]? protectedInk = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ink);
        ArgumentNullException.ThrowIfNull(componentIds);
        ArgumentNullException.ThrowIfNull(eligibleComponents);
        if (width < 1 || height < 1 || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));
        int size = width * height;
        if (ink.Length != size || componentIds.Length != size ||
            (protectedInk != null && protectedInk.Length != size))
            throw new ArgumentException("Source planes must match width × height.");
        ct.ThrowIfCancellationRequested();
        var proposals = new List<Proposal>();
        if (eligibleComponents.Count == 0) return proposals;
        var blocked = new HashSet<int>();
        if (protectedInk != null)
            for (int i = 0; i < size; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (ink[i] && protectedInk[i]) blocked.Add(componentIds[i]);
            }

        var seen = new bool[size];
        var queue = new int[size];
        for (int seed = 0; seed < size; seed++)
        {
            if ((seed & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (ink[seed] || seen[seed]) continue;
            int count = 1, head = 0, sourceId = -1;
            int minX = width, minY = height, maxX = 0, maxY = 0;
            bool border = false, admissible = true;
            queue[0] = seed;
            seen[seed] = true;
            while (head < count)
            {
                if ((head & 4095) == 0) ct.ThrowIfCancellationRequested();
                int p = queue[head++], x = p % width, y = p / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                border |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                // Eight-connected paper leaks through diagonal openings too: never close
                // a source gap merely because four-connected flood fill cannot see it.
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == 0 && dy == 0) || x + dx < 0 || x + dx >= width ||
                            y + dy < 0 || y + dy >= height) continue;
                        int q = p + dy * width + dx;
                        if (!ink[q])
                        {
                            if (!seen[q]) { seen[q] = true; queue[count++] = q; }
                        }
                        else if (dx == 0 || dy == 0)
                        {
                            int id = componentIds[q];
                            if (id < 0 || !eligibleComponents.Contains(id) || blocked.Contains(id))
                                admissible = false;
                            if (sourceId < 0) sourceId = id;
                            else if (sourceId != id) admissible = false;
                        }
                    }
            }
            if (border || !admissible || sourceId < 0 || count < 40) continue;
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (Math.Abs(bw - bh) > Math.Max(2, 0.04 * Math.Max(bw, bh))) continue;
            double boxFill = count / ((double)bw * bh);
            if (boxFill < 0.70 || boxFill > 0.86) continue;

            var boundary = Boundary(queue, count, ink, width);
            if (!FitCavity(boundary, out var center, out double innerRadius,
                out int boundaryCount, out double rms, out double maxError)) continue;
            double areaRatio = count / (Math.PI * innerRadius * innerRadius);
            if (innerRadius < 4 || areaRatio < 0.96 || areaRatio > 1.04) continue;
            var proposal = MeasureRing(ink, componentIds, width, height, sourceId, seed,
                center, innerRadius, count, boundaryCount, rms, maxError, areaRatio, ct);
            if (proposal != null) proposals.Add(proposal);
        }
        return proposals;
    }

    // Cell-face midpoints are unbiased samples of the paper/ink interface. Using paper
    // pixel centres instead would underestimate the inner radius by about half a pixel.
    // The flood is complete before enumeration; queue is not modified until fitting ends.
    static IEnumerable<Vec2> Boundary(int[] queue, int count, bool[] ink, int width)
    {
        for (int i = 0; i < count; i++)
        {
            int p = queue[i], x = p % width, y = p / width;
            if (ink[p - 1]) yield return new Vec2(x, y + 0.5);
            if (ink[p + 1]) yield return new Vec2(x + 1, y + 0.5);
            if (ink[p - width]) yield return new Vec2(x + 0.5, y);
            if (ink[p + width]) yield return new Vec2(x + 0.5, y + 1);
        }
    }

    static bool FitCavity(IEnumerable<Vec2> points, out Vec2 center, out double radius,
        out int count, out double rms, out double maxError)
    {
        center = default; radius = rms = maxError = 0; count = 0;
        var mean = new Vec2();
        foreach (var p in points) { mean += p; count++; }
        if (count < 24) return false;
        mean *= 1.0 / count;
        // Kåsa, as in BezierFitter.TryArc, but centred first to avoid cancellation on
        // large page coordinates. Centring reduces the 3×3 normal equation to 2×2.
        double xx = 0, yy = 0, xy = 0, xz = 0, yz = 0, zsum = 0;
        foreach (var p in points)
        {
            var d = p - mean;
            double z = d.LengthSq;
            xx += d.X * d.X; yy += d.Y * d.Y; xy += d.X * d.Y;
            xz += d.X * z; yz += d.Y * z; zsum += z;
        }
        double det = xx * yy - xy * xy;
        if (det <= 1e-12 * (xx + yy) * (xx + yy)) return false;
        var offset = new Vec2((xz * yy - yz * xy) / (2 * det),
            (yz * xx - xz * xy) / (2 * det));
        center = mean + offset;
        radius = Math.Sqrt(zsum / count + offset.LengthSq);
        double sq = 0;
        foreach (var p in points)
        {
            double error = Math.Abs((p - center).Length - radius);
            maxError = Math.Max(maxError, error);
            sq += error * error;
        }
        rms = Math.Sqrt(sq / count);
        // Fixed pixel tolerances express raster uncertainty; scaling a wrong ellipse
        // up must not make it a circle. Interior islands participate in these residuals.
        return double.IsFinite(radius) && rms <= 0.5 && maxError <= 1.0;
    }

    static Proposal? MeasureRing(bool[] ink, int[] ids, int w, int h, int sourceId,
        int seed, Vec2 center, double innerRadius, int cavityPixels, int boundaryCount,
        double rms, double maxError, double areaRatio, CancellationToken ct)
    {
        const double step = 0.25;
        double maxWidth = Math.Max(4, innerRadius * 0.4);
        // At most 0.5px along even the outermost searched circumference. The width
        // limit keeps both sample storage and ray work proportional to cavity size.
        int samples = (int)Math.Ceiling(2 * Math.PI * (innerRadius + maxWidth + 2) / 0.5);
        var widths = new double[samples];
        int measured = 0;
        for (int i = 0; i < samples; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            double angle = 2 * Math.PI * i / samples;
            var direction = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle));
            double start = innerRadius - 2, enter = double.NaN;
            if (At(center + direction * start) != -1) return null;
            for (double r = start + step; r <= innerRadius + maxWidth + 2; r += step)
            {
                int id = At(center + direction * r);
                if (double.IsNaN(enter))
                {
                    if (id == -1) continue;
                    if (id != sourceId || Math.Abs(r - step / 2 - innerRadius) > 1.25) return null;
                    enter = r - step / 2;
                }
                else if (id != sourceId)
                {
                    if (id == -1) widths[measured++] = r - step / 2 - enter;
                    break;
                }
            }
        }
        if (measured < samples * 0.75) return null;
        Array.Sort(widths, 0, measured);
        double width = (widths[(measured - 1) / 2] + widths[measured / 2]) / 2;
        if (width < 1.5 || width > maxWidth) return null;
        double tolerance = Math.Max(1.5, width * 0.15);
        int consistent = 0;
        for (int i = 0; i < measured; i++)
            if (Math.Abs(widths[i] - width) <= tolerance) consistent++;
        if (consistent < samples * 0.75) return null;

        double radius = innerRadius + width / 2;
        for (int i = 0; i < samples; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            double angle = 2 * Math.PI * i / samples;
            var direction = new Vec2(DetMath.Cos(angle), DetMath.Sin(angle));
            // All angles must have source ink at the proposed centre and both quarter
            // widths. No majority vote, gap filling, or boundary-only ring is sufficient.
            // A one-pixel raster tolerance covers pixel-grid quantization of a thin ring:
            // the ideal sample can land on a paper corner beside supported source ink.
            for (int band = -1; band <= 1; band++)
                if (!SupportedNear(center + direction * (radius + band * width * 0.25)))
                    return null;
        }
        return new Proposal(sourceId, seed, center, radius, width,
            new Evidence(cavityPixels, boundaryCount, innerRadius, rms, maxError,
                samples, samples, consistent, tolerance, areaRatio));

        // -1 paper, -2 outside/unknown. Unknown ink is never treated as paper or support.
        int At(Vec2 p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h) return -2;
            int index = (int)p.Y * w + (int)p.X;
            return !ink[index] ? -1 : ids[index] < 0 ? -2 : ids[index];
        }

        // Same source component within the sample cell or one of its eight neighbours.
        // Other components and unknown ink never satisfy support.
        bool SupportedNear(Vec2 p)
        {
            int x = (int)p.X, y = (int)p.Y;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int qx = x + dx, qy = y + dy;
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                    int index = qy * w + qx;
                    if (ink[index] && ids[index] == sourceId) return true;
                }
            return false;
        }
    }
}
