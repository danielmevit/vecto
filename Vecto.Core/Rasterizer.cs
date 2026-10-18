namespace Vecto.Core;

/// <summary>
/// Renders a <see cref="VectorDocument"/> to RGBA pixels: nonzero scanline fill in
/// document (painter's) order, supersampled and box-downsampled. Exists so the engine can
/// be evaluated round-trip — vector → raster → trace → raster — without external renderers.
/// </summary>
public static class Rasterizer
{
    public static RasterImage Render(VectorDocument doc, double scale = 1.0, int supersample = 4, Rgba32? background = null)
    {
        int outW = Math.Max(1, (int)Math.Round(doc.Width * scale));
        int outH = Math.Max(1, (int)Math.Round(doc.Height * scale));
        while (supersample > 1 && (long)outW * outH * supersample * supersample > 80_000_000)
            supersample--;
        int ssW = outW * supersample, ssH = outH * supersample;
        double sx = (double)ssW / doc.Width, sy = (double)ssH / doc.Height;

        var owner = new int[ssW * ssH];
        Array.Fill(owner, -1);
        for (int r = 0; r < doc.Regions.Count; r++)
            FillRegion(owner, ssW, ssH, doc.Regions[r], sx, sy, r);
        // strokes paint after the fills: thin line-work sits on paper, never under a region
        for (int s = 0; s < doc.Strokes.Count; s++)
            StrokeRegion(owner, ssW, ssH, doc.Strokes[s], sx, sy, doc.Regions.Count + s);

        var colors = new Rgba32[doc.Regions.Count + doc.Strokes.Count];
        for (int r = 0; r < doc.Regions.Count; r++)
            colors[r] = doc.Palette[doc.Regions[r].PaletteIndex].Color;
        for (int s = 0; s < doc.Strokes.Count; s++)
            colors[doc.Regions.Count + s] = doc.Palette[doc.Strokes[s].PaletteIndex].Color;

        var img = new RasterImage(outW, outH);
        var px = img.Pixels;
        int area = supersample * supersample;
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                long sr = 0, sg = 0, sb = 0;
                int hit = 0;
                for (int dy = 0; dy < supersample; dy++)
                {
                    int row = (y * supersample + dy) * ssW + x * supersample;
                    for (int dx = 0; dx < supersample; dx++)
                    {
                        int o = owner[row + dx];
                        if (o >= 0)
                        {
                            var c = colors[o];
                            sr += c.R;
                            sg += c.G;
                            sb += c.B;
                            hit++;
                        }
                        else if (background is { } bg)
                        {
                            sr += bg.R;
                            sg += bg.G;
                            sb += bg.B;
                            hit++;
                        }
                    }
                }
                int pi = (y * outW + x) * 4;
                if (hit == 0) continue;   // fully transparent
                px[pi] = (byte)(sr / hit);
                px[pi + 1] = (byte)(sg / hit);
                px[pi + 2] = (byte)(sb / hit);
                px[pi + 3] = (byte)(255 * hit / area);
            }
        }
        return img;
    }

    static void FillRegion(int[] owner, int ssW, int ssH, RegionPath region, double sx, double sy, int id)
    {
        var edges = new List<(double X0, double Y0, double X1, double Y1)>();
        double minY = double.MaxValue, maxY = double.MinValue;

        void AddEdge(Vec2 a, Vec2 b)
        {
            if (a.Y == b.Y) return;
            edges.Add((a.X, a.Y, b.X, b.Y));
            minY = Math.Min(minY, Math.Min(a.Y, b.Y));
            maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
        }

        foreach (var loop in region.Loops)
        {
            if (loop.Count == 0) continue;
            var first = Scale(loop[0].P0);
            var prev = first;
            foreach (var seg in loop)
            {
                if (seg.IsLine(0.001))
                {
                    var q = Scale(seg.P3);
                    AddEdge(prev, q);
                    prev = q;
                }
                else
                {
                    double len = (Scale(seg.P1) - prev).Length
                               + (Scale(seg.P2) - Scale(seg.P1)).Length
                               + (Scale(seg.P3) - Scale(seg.P2)).Length;
                    int steps = Math.Clamp((int)Math.Ceiling(len / 0.5), 2, 512);
                    for (int i = 1; i <= steps; i++)
                    {
                        var q = Scale(seg.Eval(i / (double)steps));
                        AddEdge(prev, q);
                        prev = q;
                    }
                }
            }
            AddEdge(prev, first);   // no-op when the loop is already closed
        }
        if (edges.Count == 0) return;

        int y0 = Math.Max(0, (int)Math.Floor(minY));
        int y1 = Math.Min(ssH - 1, (int)Math.Ceiling(maxY));
        var crossings = new List<(double X, int W)>(64);
        for (int y = y0; y <= y1; y++)
        {
            double yc = y + 0.5;
            crossings.Clear();
            foreach (var e in edges)
            {
                bool up = e.Y1 > e.Y0;
                double lo = up ? e.Y0 : e.Y1, hi = up ? e.Y1 : e.Y0;
                if (yc < lo || yc >= hi) continue;   // half-open span avoids double-counted vertices
                double t = (yc - e.Y0) / (e.Y1 - e.Y0);
                crossings.Add((e.X0 + t * (e.X1 - e.X0), up ? 1 : -1));
            }
            crossings.Sort((a, b) => a.X.CompareTo(b.X));
            int winding = 0;
            double spanStart = 0;
            foreach (var (cx, cw) in crossings)
            {
                int prevWinding = winding;
                winding += cw;
                if (prevWinding == 0 && winding != 0)
                {
                    spanStart = cx;
                }
                else if (prevWinding != 0 && winding == 0)
                {
                    int xa = Math.Max(0, (int)Math.Ceiling(spanStart - 0.5));
                    int xb = Math.Min(ssW - 1, (int)Math.Ceiling(cx - 0.5) - 1);
                    for (int x = xa; x <= xb; x++) owner[y * ssW + x] = id;
                }
            }
        }

        Vec2 Scale(Vec2 v) => new(v.X * sx, v.Y * sy);
    }

    /// <summary>
    /// Stroked centerline path: the curve is flattened to a supersample-resolution polyline,
    /// then every pixel within half the stroke width of any segment is claimed — the clamped
    /// point-to-segment distance gives round caps and round joins for free, matching the SVG
    /// (stroke-linecap/join="round") and PDF (1 J / 1 j) output.
    /// </summary>
    static void StrokeRegion(int[] owner, int ssW, int ssH, StrokePath stroke, double sx, double sy, int id)
    {
        if (stroke.Segments.Count == 0) return;
        Vec2 Scale(Vec2 v) => new(v.X * sx, v.Y * sy);
        var pts = new List<Vec2> { Scale(stroke.Segments[0].P0) };
        var prev = pts[0];
        foreach (var seg in stroke.Segments)
        {
            if (seg.IsLine(0.001))
            {
                Add(Scale(seg.P3));
            }
            else
            {
                double len = (Scale(seg.P1) - prev).Length
                           + (Scale(seg.P2) - Scale(seg.P1)).Length
                           + (Scale(seg.P3) - Scale(seg.P2)).Length;
                int steps = Math.Clamp((int)Math.Ceiling(len / 0.5), 2, 512);
                for (int i = 1; i <= steps; i++) Add(Scale(seg.Eval(i / (double)steps)));
            }
        }
        if (stroke.Closed && pts.Count > 2) Add(pts[0]);   // the ring's closing segment
        double half = stroke.Width * Math.Sqrt(sx * sy) / 2;
        if (half < 0.05) half = 0.05;
        double r2 = half * half;
        if (pts.Count == 1)
            FillSpan(pts[0], pts[0]);
        else
            for (int k = 0; k + 1 < pts.Count; k++) FillSpan(pts[k], pts[k + 1]);

        void Add(Vec2 q)
        {
            if (q.DistSq(pts[^1]) > 1e-12) pts.Add(q);
            prev = q;
        }

        void FillSpan(Vec2 a, Vec2 b)
        {
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - half));
            int x1 = Math.Min(ssW - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + half));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - half));
            int y1 = Math.Min(ssH - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + half));
            var ab = b - a;
            double len2 = ab.LengthSq;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var q = new Vec2(x + 0.5, y + 0.5);
                    double t = len2 < 1e-12 ? 0 : Math.Clamp((q - a).Dot(ab) / len2, 0, 1);
                    if (q.DistSq(a + ab * t) <= r2) owner[y * ssW + x] = id;
                }
        }
    }
}
