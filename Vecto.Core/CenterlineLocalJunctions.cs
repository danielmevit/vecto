namespace Vecto.Core;

public static partial class Centerline
{
    // Only local source-owned painted junctions participate. Legacy snapping and its
    // global tolerances are unchanged; this pass can only shorten a finite line.
    static void RepairLocalJunctions(List<StrokePath> strokes,
        List<CenterlineCircles.Proposal> circles, HashSet<int> local, int[] comp, int w, int h)
    {
        const double cell = 16;
        var targets = new List<(Vec2 A, Vec2 B, double Width, int Owner, CenterlineCircles.Proposal? Circle)>();
        var grid = new Dictionary<long, HashSet<int>>();
        for (int s = 0; s < strokes.Count; s++)
            if (!strokes[s].Closed)
                foreach (var g in strokes[s].Segments)
                    if (g.IsLine(.05)) Add(g.P0, g.P3, strokes[s].Width, s, null);
        foreach (var c in circles)
        {
            int n = Math.Max(16, (int)Math.Ceiling(2 * Math.PI * c.Radius / 4));
            for (int k = 0; k < n; k++)
            {
                double a = 2 * Math.PI * k / n, b = 2 * Math.PI * (k + 1) / n;
                Add(c.Center + new Vec2(Math.Cos(a), Math.Sin(a)) * c.Radius,
                    c.Center + new Vec2(Math.Cos(b), Math.Sin(b)) * c.Radius, c.Width, -1, c);
            }
        }
        for (int s = 0; s < strokes.Count; s++)
        {
            var stroke = strokes[s];
            if (stroke.Closed || stroke.Segments.Count == 0) continue;
            Fix(true); Fix(false);
            void Fix(bool head)
            {
                int index = head ? 0 : stroke.Segments.Count - 1;
                var g = stroke.Segments[index];
                if (!g.IsLine(.05)) return;
                var p = head ? g.P0 : g.P3;
                var inner = head ? g.P3 : g.P0;
                double length = (inner - p).Length;
                if (length < 4 * stroke.Width) return;
                var dir = (inner - p) * (1 / length);
                int id = Id(p);
                if (!local.Contains(id) || Id(p + dir * stroke.Width) != id) return;
                if (!grid.TryGetValue(CellKey((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell)), out var candidates)) return;
                double best = double.PositiveInfinity;
                Vec2 point = p;
                bool ambiguous = false;
                foreach (int ti in candidates.Order())
                {
                    var t = targets[ti];
                    if (t.Owner == s) continue;
                    var tangent = (t.B - t.A).Normalized();
                    double u;
                    Vec2 normal;
                    if (t.Circle is { } c)
                    {
                        if (c.SourceComponentId != id || Math.Abs((p - c.Center).Length - c.Radius) > t.Width / 2) continue;
                        var delta = p - c.Center;
                        double dot = delta.Dot(dir), disc = dot * dot - delta.LengthSq + c.Radius * c.Radius;
                        if (disc < 0) continue;
                        double root = Math.Sqrt(disc);
                        u = -dot - root;
                        if (u <= .05) u = -dot + root;
                        normal = (p + dir * u - c.Center).Normalized();
                    }
                    else
                    {
                        double cross = dir.X * tangent.Y - dir.Y * tangent.X;
                        if (Math.Abs(cross) < .25) continue;
                        var delta = t.A - p;
                        u = (delta.X * tangent.Y - delta.Y * tangent.X) / cross;
                        var q0 = p + dir * u;
                        double along = (q0 - t.A).Dot(tangent);
                        if (along < t.Width || along > (t.B - t.A).Length - t.Width) continue;
                        normal = new Vec2(-tangent.Y, tangent.X);
                        if (Math.Abs((p - t.A).Dot(normal)) > t.Width / 2) continue;
                    }
                    if (u <= .05 || u > 2 * t.Width || u >= length - 2 * stroke.Width) continue;
                    var q = p + dir * u;
                    if (Id(q) != id || Id(q + normal * (t.Width * .35)) != id ||
                        Id(q - normal * (t.Width * .35)) != id) continue;
                    bool supported = true;
                    for (double d = 0; d <= u; d += .5)
                        if (Id(p + dir * d) != id) { supported = false; break; }
                    if (!supported) continue;
                    if (u < best - .5) { best = u; point = q; ambiguous = false; }
                    else if (Math.Abs(u - best) <= .5 && point.DistSq(q) > .25) ambiguous = true;
                }
                if (!double.IsFinite(best) || ambiguous) return;
                var a = head ? point : g.P0;
                var b = head ? g.P3 : point;
                stroke.Segments[index] = new CubicBezier(a, a + (b - a) * (1.0 / 3), a + (b - a) * (2.0 / 3), b);
            }
        }
        int Id(Vec2 p)
        {
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
            return (uint)x < (uint)w && (uint)y < (uint)h ? comp[y * w + x] : -1;
        }
        void Add(Vec2 a, Vec2 b, double width, int owner, CenterlineCircles.Proposal? circle)
        {
            int index = targets.Count;
            targets.Add((a, b, width, owner, circle));
            int steps = Math.Max(1, (int)Math.Ceiling((b - a).Length / 4));
            int reach = (int)Math.Ceiling(width / 2 / cell) + 1;
            for (int k = 0; k <= steps; k++)
            {
                var p = a + (b - a) * ((double)k / steps);
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell);
                for (int y = cy - reach; y <= cy + reach; y++)
                    for (int x = cx - reach; x <= cx + reach; x++)
                    {
                        long key = CellKey(x, y);
                        if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
                        bucket.Add(index);
                    }
            }
        }
    }
}
