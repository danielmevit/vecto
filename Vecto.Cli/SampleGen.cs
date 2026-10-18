using Vecto.Core;

namespace Vecto.Cli;

/// <summary>
/// Procedural test images (no external assets): a crisp aliased logo, the same shapes
/// anti-aliased, and an anti-aliased emblem on a transparent background. The rotated bar
/// crosses the other shapes so the boundary graph gets real junctions to chew on.
/// </summary>
public static class SampleGen
{
    public static RasterImage Crisp(int size = 320) => Shapes(size, aa: false, transparentBg: false);
    public static RasterImage Blended(int size = 320) => Shapes(size, aa: true, transparentBg: false);
    public static RasterImage Transparent(int size = 320) => Shapes(size, aa: true, transparentBg: true);

    /// <summary>
    /// A synthetic noisy "CAD scan" (deterministic seed): white paper, black strokes 1–3 px
    /// wide at 0/45/90 degrees and odd angles, circles, glyph-like blocks, a border frame
    /// and a title block — then blur, Gaussian noise and an illumination gradient are
    /// applied. Returns the scan plus the clean ink mask (ground truth for benchmarks).
    /// Coordinates are in a 480×360 design space and scaled to the requested size.
    /// </summary>
    public static (RasterImage Scan, RasterImage Mask) CadScan(int w = 480, int h = 360)
    {
        var ink = new bool[w * h];
        double sx = w / 480.0, sy = h / 360.0;

        void Plot(int x, int y)
        {
            if ((uint)x < (uint)w && (uint)y < (uint)h) ink[y * w + x] = true;
        }

        void Line(double ax, double ay, double bx, double by, double width)
        {
            ax *= sx; bx *= sx; ay *= sy; by *= sy;
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            int x0 = (int)Math.Floor(Math.Min(ax, bx) - width), x1 = (int)Math.Ceiling(Math.Max(ax, bx) + width);
            int y0 = (int)Math.Floor(Math.Min(ay, by) - width), y1 = (int)Math.Ceiling(Math.Max(ay, by) + width);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double t = lenSq == 0 ? 0 : Math.Clamp(((x + 0.5 - ax) * dx + (y + 0.5 - ay) * dy) / lenSq, 0, 1);
                    double px = ax + t * dx - (x + 0.5), py = ay + t * dy - (y + 0.5);
                    if (Math.Sqrt(px * px + py * py) < width / 2) Plot(x, y);
                }
        }

        void Rect(double x0, double y0, double x1, double y1, double width)
        {
            Line(x0, y0, x1, y0, width);
            Line(x1, y0, x1, y1, width);
            Line(x1, y1, x0, y1, width);
            Line(x0, y1, x0, y0, width);
        }

        void Cell(double x0, double y0, double x1, double y1)
        {
            int xa = (int)Math.Round(x0 * sx), xb = (int)Math.Round(x1 * sx);
            int ya = (int)Math.Round(y0 * sy), yb = (int)Math.Round(y1 * sy);
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++) Plot(x, y);
        }

        void Circle(double cx, double cy, double r, double width)
        {
            cx *= sx; cy *= sy; r *= (sx + sy) / 2;
            int x0 = (int)Math.Floor(cx - r - width), x1 = (int)Math.Ceiling(cx + r + width);
            int y0 = (int)Math.Floor(cy - r - width), y1 = (int)Math.Ceiling(cy + r + width);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                    if (Math.Abs(d - r) < width / 2) Plot(x, y);
                }
        }

        // the drawing: frame, title block, axis/diagonal/odd-angle strokes, circles, glyphs
        Rect(9, 9, 470, 350, 3);
        Rect(300, 290, 470, 350, 2);
        Line(300, 318, 470, 318, 1);
        Line(340, 290, 340, 318, 1);
        Cell(306, 296, 320, 310);
        Cell(346, 296, 386, 310);
        Cell(306, 324, 320, 338);
        Cell(346, 324, 386, 338);
        Line(30, 40, 210, 40, 1);
        Line(30, 70, 210, 70, 2);
        Line(30, 100, 210, 100, 3);
        Line(30, 130, 30, 250, 1);
        Line(60, 130, 60, 250, 2);
        Line(90, 130, 90, 250, 3);
        Line(260, 30, 410, 180, 1);
        Line(260, 60, 420, 220, 2);
        Line(260, 90, 430, 260, 3);
        Line(30, 300, 175, 346, 1);
        Line(190, 330, 235, 190, 2);
        Circle(350, 180, 40, 2);
        Circle(430, 100, 22, 1);
        foreach (var gx in new[] { 110.0, 118, 126, 134 })
        {
            Cell(gx, 200, gx + 2, 205);
            Cell(gx + 9, 215, gx + 12, 221);
        }

        // synthesize the scan: illumination gradient -> blur -> noise
        var lum = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double grad = 0.88 + 0.12 * (x + 0.35 * y) / (w + 0.35 * h);
                lum[y * w + x] = (ink[y * w + x] ? 25.0 : 250.0) * grad;
            }
        lum = Blur(lum, w, h);
        uint seed = 0x1234567;
        double NextGaussian()
        {
            uint Next()
            {
                seed = seed * 1664525u + 1013904223u;
                return seed;
            }
            double u1 = (Next() + 1.0) / 4294967296.0;
            double u2 = (Next() + 1.0) / 4294967296.0;
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
        var scan = new RasterImage(w, h);
        for (int i = 0; i < lum.Length; i++)
        {
            byte v = (byte)Math.Clamp(Math.Round(lum[i] + NextGaussian() * 3.0), 0, 255);
            scan.SetPixel(i % w, i / w, new Rgba32(v, v, v, 255));
        }
        return (scan, ScanPreprocess.MaskToImage(ink, w, h));
    }

    /// <summary>One separable [1,2,1]/4 blur pass (σ ≈ 0.45; the scan applies it twice).</summary>
    static double[] Blur(double[] src, int w, int h)
    {
        var tmp = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
                tmp[y * w + x] = (src[y * w + xm] + 2 * src[y * w + x] + src[y * w + xp]) / 4;
            }
        var dst = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1);
                dst[y * w + x] = (tmp[ym * w + x] + 2 * tmp[y * w + x] + tmp[yp * w + x]) / 4;
            }
        return dst;
    }

    static RasterImage Shapes(int size, bool aa, bool transparentBg)
    {
        var img = new RasterImage(size, size);
        double s = size / 320.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                var c = transparentBg ? (R: 0.0, G: 0.0, B: 0.0, A: 0.0) : (R: 0.97, G: 0.97, B: 0.96, A: 1.0);
                c = Blend(c, (0.08, 0.27, 0.78), Coverage(Disc(px, py, 105 * s, 105 * s, 62 * s), aa));
                c = Blend(c, (0.88, 0.23, 0.23), Coverage(RoundBox(px - 215 * s, py - 215 * s, 55 * s, 55 * s, 16 * s), aa));
                c = Blend(c, (0.12, 0.65, 0.35), Coverage(Bar(px, py, 160 * s, 160 * s, 110 * s, 14 * s, 45 * Math.PI / 180, 4 * s), aa));
                img.SetPixel(x, y, new Rgba32(
                    (byte)Math.Round(c.R * 255),
                    (byte)Math.Round(c.G * 255),
                    (byte)Math.Round(c.B * 255),
                    (byte)Math.Round(c.A * 255)));
            }
        }
        return img;
    }

    static double Coverage(double sdf, bool aa) =>
        aa ? Math.Clamp(sdf / 1.5 + 0.5, 0, 1) : (sdf > 0 ? 1 : 0);

    static (double R, double G, double B, double A) Blend(
        (double R, double G, double B, double A) under, (double R, double G, double B) over, double cov)
    {
        if (cov <= 0) return under;
        double a = cov + under.A * (1 - cov);
        if (a <= 0) return (0, 0, 0, 0);
        double w = under.A * (1 - cov);
        return (
            (over.R * cov + under.R * w) / a,
            (over.G * cov + under.G * w) / a,
            (over.B * cov + under.B * w) / a,
            a);
    }

    // signed distance fields, positive inside
    static double Disc(double x, double y, double cx, double cy, double r) =>
        r - Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

    static double RoundBox(double dx, double dy, double hx, double hy, double round)
    {
        double qx = Math.Abs(dx) - (hx - round), qy = Math.Abs(dy) - (hy - round);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        double outside = Math.Sqrt(ox * ox + oy * oy);
        double inside = Math.Min(Math.Max(qx, qy), 0);
        return round - (outside + inside);
    }

    static double Bar(double x, double y, double cx, double cy, double hx, double hy, double angle, double round)
    {
        double dx = x - cx, dy = y - cy;
        double ca = Math.Cos(angle), sa = Math.Sin(angle);
        return RoundBox(ca * dx + sa * dy, -sa * dx + ca * dy, hx, hy, round);
    }
}
