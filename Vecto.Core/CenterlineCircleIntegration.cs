namespace Vecto.Core;

/// <summary>
/// VEC106 circle activation: source-supported circle proposals become actual traced
/// strokes. This partial owns the caller side of <see cref="CenterlineCircles"/>:
/// eligibility (explicit non-text local-eligible mixed components, selected by the real
/// classifier), glyph-row protection, emission of one closed weighted circle at the
/// measured geometry, and removal of duplicate ring fragments that the accepted circle
/// fully owns. Whole-component rings keep their existing centerline path and never
/// duplicate: only mixed components enter the eligible set, and proposals are made from
/// the ORIGINAL ink/component observation before any source erasure. The emitted circle
/// uses the same standard circular cubic construction the arc fitter emits
/// (<see cref="BezierFitter"/> quarter pieces), so the DXF writer recovers it
/// analytically as one closed bulged polyline carrying the stroke weight.
/// </summary>
public static partial class Centerline
{
    /// <summary>Half-width slack when testing whether an emitted stroke is a duplicate
    /// fragment of an accepted circle: the fitted fragment may ride the ring within the
    /// width-derived line tolerance (<see cref="LineTol"/>), and a grown tip adds its
    /// cap. Anything that leaves this band — a crossing hatch, a leader, a solid — is
    /// not owned by the circle and stays.</summary>
    static double CircleBandSlack(double width) => LineTol(width) + 0.75;

    /// <summary>
    /// Proposes circles from the original source observation: the ink mask and component
    /// IDs are the unmodified planes built at stage entry, and the call sits before any
    /// labels erasure. Eligibility is the explicit non-text local-eligible mixed component
    /// set (the same components local recovery extracts); text-row glyph components are
    /// never eligible and additionally protect their ink, so a protected pixel vetoes
    /// its whole component. No cavity of an unknown, glyph or whole-component ring is
    /// ever admitted.
    /// </summary>
    static List<CenterlineCircles.Proposal> ProposeLocalCircles(
        bool[] inkMask, int[] comp, HashSet<int> local, List<Comp> comps, int w, int h, CancellationToken ct)
    {
        if (local.Count == 0) return [];
        bool[]? protectedInk = null;
        foreach (var c in comps)
        {
            if (!c.TextRow) continue;
            protectedInk ??= new bool[w * h];
            foreach (int i in c.Pixels) protectedInk[i] = true;
        }
        return CenterlineCircles.Propose(inkMask, comp, local, w, h, protectedInk, ct).ToList();
    }

    /// <summary>
    /// Replaces the duplicate ring fragments of every accepted circle with the circle
    /// itself: a fragment is removed only when its whole fitted geometry lies inside the
    /// circle's painted band and rides only the circle's source component — the circle
    /// paints that ink whole. Touching or crossing hatches, leaders and attached solids
    /// leave the band and keep their strokes; the solid stays filled. The circle is
    /// appended after the local strokes, in the deterministic proposal order.
    /// </summary>
    static void IntegrateCircles(List<StrokePath> strokes, List<CenterlineCircles.Proposal> proposals,
        int ink, int[] comp, int w, int h)
    {
        foreach (var proposal in proposals)
        {
            RemoveCircleFragments(strokes, proposal, comp, w, h);
            strokes.Add(CircleStroke(proposal, ink));
        }
    }

    /// <summary>
    /// One closed weighted circle at the measured geometry: four exact quarter cubics
    /// whose controls sit at tangent · r · 4/3·tan(π/8) — the same construction
    /// <see cref="BezierFitter"/> emits for arcs, so the existing writers treat it
    /// exactly like a fitted ring (SVG keeps its standard cubic circle representation;
    /// DXF recovers the analytic bulges). Cardinal endpoints make the ring close
    /// exactly; the stroke weight is the radially measured source width.
    /// </summary>
    static StrokePath CircleStroke(CenterlineCircles.Proposal proposal, int ink)
    {
        double kk = (4.0 / 3.0 * DetMath.Tan(Math.PI / 8)) * proposal.Radius;
        var c = proposal.Center;
        var east = c + new Vec2(proposal.Radius, 0);
        var north = c + new Vec2(0, proposal.Radius);
        var west = c - new Vec2(proposal.Radius, 0);
        var south = c - new Vec2(0, proposal.Radius);
        return new StrokePath
        {
            PaletteIndex = ink,
            Width = proposal.Width,
            Closed = true,
            Segments =
            [
                new CubicBezier(east, east + new Vec2(0, kk), north + new Vec2(kk, 0), north),
                new CubicBezier(north, north - new Vec2(kk, 0), west + new Vec2(0, kk), west),
                new CubicBezier(west, west - new Vec2(0, kk), south - new Vec2(kk, 0), south),
                new CubicBezier(south, south + new Vec2(kk, 0), east - new Vec2(0, kk), east),
            ],
        };
    }

    static void RemoveCircleFragments(List<StrokePath> strokes, CenterlineCircles.Proposal proposal,
        int[] comp, int w, int h)
    {
        double band = proposal.Width / 2 + CircleBandSlack(proposal.Width);
        for (int s = strokes.Count - 1; s >= 0; s--)
            if (OwnedByCircle(strokes[s], proposal, comp, w, h, band))
                strokes.RemoveAt(s);
    }

    /// <summary>True when every point of the stroke's fitted geometry lies inside the
    /// circle's band and its samples ride only the circle's source component. A foreign
    /// component's ink inside the band is never claimed; paper cells (fit wobble) do
    /// not veto ownership.</summary>
    static bool OwnedByCircle(StrokePath stroke, CenterlineCircles.Proposal proposal,
        int[] comp, int w, int h, double band)
    {
        foreach (var seg in stroke.Segments)
        {
            double lengthBound = (seg.P1 - seg.P0).Length + (seg.P2 - seg.P1).Length + (seg.P3 - seg.P2).Length;
            int steps = seg.IsLine(0.02) ? 1 : Math.Max(1, (int)Math.Ceiling(lengthBound * 2));
            for (int k = 0; k <= steps; k++)
            {
                var q = seg.Eval((double)k / steps);
                if (Math.Abs((q - proposal.Center).Length - proposal.Radius) > band) return false;
                int x = (int)q.X, y = (int)q.Y;
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                int id = comp[y * w + x];
                if (id >= 0 && id != proposal.SourceComponentId) return false;
            }
        }
        return true;
    }
}
