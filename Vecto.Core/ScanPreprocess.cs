namespace Vecto.Core;

/// <summary>
/// Preprocessing for scanned line drawings / CAD art (<see cref="ImageStyle.LineArt"/>):
/// luminance → Sauvola adaptive threshold → two-colour ink/paper raster → diagonal-contact
/// bridging. Pure functions on <see cref="RasterImage"/>/arrays so every stage is unit-testable.
/// </summary>
public static class ScanPreprocess
{
    /// <summary>
    /// Sauvola window side (odd). Must comfortably exceed the widest expected stroke (~2×) so a
    /// stroke never dominates its own local statistics and erases itself; 31 px ≈ 2.6 mm of a
    /// 300 DPI scan, safe for strokes up to ~10 px. Windows are clamped at the image border.
    /// </summary>
    public const int SauvolaWindow = 31;

    /// <summary>
    /// Sauvola sensitivity k. Lower → more ink. Tuned on the CAD-scan benchmark: the blurred
    /// halo around a stroke (≈ 194 on 250-paper) must stay paper while a 1-px stroke centre
    /// (≈ 138) stays ink. Measured precision/recall on the benchmark: 0.97 / 0.97 at k = 0.42;
    /// below ~0.34 halos dilate every stroke by a pixel (precision 0.91), above ~0.45 faint
    /// 1-px strokes start dissolving.
    /// </summary>
    public const double SauvolaK = 0.42;

    /// <summary>Sauvola's R: normalizes the local standard deviation against the 8-bit range.</summary>
    public const double SauvolaR = 128.0;

    static readonly Rgba32 Ink = new(0, 0, 0, 255);
    static readonly Rgba32 Paper = new(255, 255, 255, 255);

    /// <summary>The LineArt front-end: Sauvola binarization plus diagonal bridging, as a
    /// two-colour opaque raster that the standard pipeline then traces.</summary>
    public static RasterImage ToInkPaper(RasterImage src, CancellationToken ct = default)
    {
        var ink = InkMask(src, ct);
        BridgeDiagonalContacts(ink, src.Width, src.Height);
        return MaskToImage(ink, src.Width, src.Height);
    }

    /// <summary>
    /// Rec.601 luminance per pixel; transparent pixels count as paper (255) so LineArt never
    /// traces the void of a transparent input as ink.
    /// </summary>
    public static byte[] Luminance(RasterImage src)
    {
        int w = src.Width, h = src.Height;
        var lum = new byte[w * h];
        var px = src.Pixels;
        for (int i = 0; i < lum.Length; i++)
        {
            int pi = i * 4;
            lum[i] = px[pi + 3] < Segmenter.OpaqueThreshold
                ? (byte)255
                : (byte)Math.Round(0.299 * px[pi] + 0.587 * px[pi + 1] + 0.114 * px[pi + 2]);
        }
        return lum;
    }

    /// <summary>
    /// Sauvola adaptive thresholding via integral images: a pixel is ink when its luminance is
    /// below T = m·(1 + k·(δ/R − 1)) with m, δ the mean and deviation over the local window.
    /// Local statistics make it immune to illumination gradients that defeat any global
    /// threshold (the whole point for scans).
    /// </summary>
    public static bool[] InkMask(RasterImage src, CancellationToken ct = default)
    {
        int w = src.Width, h = src.Height;
        var lum = Luminance(src);

        // integral images with a zero row/column in front: window sums become four lookups
        var sum = new long[(w + 1) * (h + 1)];
        var sumSq = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0, rowSumSq = 0;
            for (int x = 0; x < w; x++)
            {
                int v = lum[y * w + x];
                rowSum += v;
                rowSumSq += (long)v * v;
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + rowSum;
                sumSq[(y + 1) * (w + 1) + x + 1] = sumSq[y * (w + 1) + x + 1] + rowSumSq;
            }
        }

        int r = SauvolaWindow / 2;
        var ink = new bool[w * h];
        int stride = w + 1;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                int n = (x1 - x0 + 1) * (y1 - y0 + 1);
                long s = sum[(y1 + 1) * stride + x1 + 1]
                       - sum[y0 * stride + x1 + 1]
                       - sum[(y1 + 1) * stride + x0]
                       + sum[y0 * stride + x0];
                long sq = sumSq[(y1 + 1) * stride + x1 + 1]
                        - sumSq[y0 * stride + x1 + 1]
                        - sumSq[(y1 + 1) * stride + x0]
                        + sumSq[y0 * stride + x0];
                double mean = (double)s / n;
                double variance = Math.Max(0, (double)sq / n - mean * mean);
                double dev = Math.Sqrt(variance);
                double t = mean * (1 + SauvolaK * (dev / SauvolaR - 1));
                ink[y * w + x] = lum[y * w + x] < t;
            }
        });
        return ink;
    }

    /// <summary>
    /// Bridges diagonal-only ink contacts: in every 2×2 block whose ink pixels touch only
    /// corner-to-corner, one of the two paper pixels becomes ink (the one with more ink
    /// neighbours, first on ties — deterministic). Without this, 4-connected labelling splits
    /// a 1-px diagonal stroke into 1–2 px runs that small-region absorption then deletes.
    /// Bridges are collected from a snapshot of the mask, so the pass is order-independent.
    /// </summary>
    public static void BridgeDiagonalContacts(bool[] ink, int w, int h)
    {
        var bridges = new List<int>();
        for (int y = 0; y + 1 < h; y++)
        {
            for (int x = 0; x + 1 < w; x++)
            {
                int i = y * w + x;
                bool tl = ink[i], br = ink[i + w + 1];
                bool tr = ink[i + 1], bl = ink[i + w];
                if (tl && br && !tr && !bl)
                    bridges.Add(InkNeighbours(i + 1) >= InkNeighbours(i + w) ? i + 1 : i + w);
                else if (tr && bl && !tl && !br)
                    bridges.Add(InkNeighbours(i) >= InkNeighbours(i + w + 1) ? i : i + w + 1);
            }
        }
        foreach (int b in bridges) ink[b] = true;

        int InkNeighbours(int p)
        {
            int x = p % w, y = p / w, n = 0;
            if (x > 0 && ink[p - 1]) n++;
            if (x < w - 1 && ink[p + 1]) n++;
            if (y > 0 && ink[p - w]) n++;
            if (y < h - 1 && ink[p + w]) n++;
            return n;
        }
    }

    /// <summary>Two-colour raster: ink black, paper white, fully opaque.</summary>
    public static RasterImage MaskToImage(bool[] ink, int w, int h)
    {
        var img = new RasterImage(w, h);
        for (int i = 0; i < ink.Length; i++)
            img.SetPixel(i % w, i / w, ink[i] ? Ink : Paper);
        return img;
    }
}
