namespace Vecto.Core;

/// <summary>A plain-language tip about an input the tracer handles poorly. Never affects the trace.</summary>
public enum ImageHint { None, SmallPicture, SmoothGradients }

/// <summary>
/// Cheap, deterministic look at the input to spot known weak cases (VEC-46): a small picture
/// (shortest side of the opaque content under 256 px) or smooth colour fades. Read-only; the
/// trace never calls it, so trace output cannot change.
/// </summary>
public static class ImageHints
{
    public const int SmallSide = 256;
    /// <summary>Shortest ramp window (adjacent pixels, one direction); longer on big pictures (longest side / 16).</summary>
    public const int RampLength = 16;
    /// <summary>Largest per-channel step inside a ramp: bigger is an edge, not a fade.</summary>
    public const int RampMaxStep = 6;
    /// <summary>Net change (largest channel) over the window for it to count as a fade.</summary>
    public const int RampMinNet = 8;
    /// <summary>Share of sampled opaque windows that must be ramps.</summary>
    public const double GradientShare = 0.15;

    public readonly record struct Result(ImageHint Hint, int ContentWidth, int ContentHeight, double RampShare);

    public static Result Analyze(RasterImage img)
    {
        int w = img.Width, h = img.Height;
        var px = img.Pixels;
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] >= 128)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 < 0) return new Result(ImageHint.None, 0, 0, 0);
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        double share = RampShare(img);
        var hint = Math.Min(cw, ch) < SmallSide ? ImageHint.SmallPicture
                 : share >= GradientShare ? ImageHint.SmoothGradients
                 : ImageHint.None;
        return new Result(hint, cw, ch, share);
    }

    /// <summary>
    /// Share of sampled opaque points where the window to the right or the one below
    /// (at least <see cref="RampLength"/> adjacent pixels) is a gentle steady ramp: every step small, no channel turning back by more
    /// than 1 against its overall direction, and a real net change. Flat fills have no net change,
    /// anti-aliased edges have a big step, JPEG noise and texture turn back and forth.
    /// </summary>
    public static double RampShare(RasterImage img)
    {
        int w = img.Width, h = img.Height;
        int len = Math.Max(RampLength, Math.Max(w, h) / 16);
        int stride = Math.Max(3, Math.Max(w, h) / 160);
        long windows = 0, ramps = 0;
        for (int y = 0; y < h; y += stride)
            for (int x = 0; x < w; x += stride)
            {
                int best = -1;
                if (x + len <= w) best = Math.Max(best, Window(img.Pixels, w, x, y, 1, 0, len));
                if (y + len <= h) best = Math.Max(best, Window(img.Pixels, w, x, y, 0, 1, len));
                if (best < 0) continue;
                windows++;
                if (best == 1) ramps++;
            }
        return windows == 0 ? 0 : (double)ramps / windows;
    }

    /// <summary>-1 = not fully opaque, 0 = not a ramp, 1 = ramp.</summary>
    static int Window(byte[] px, int w, int x, int y, int dx, int dy, int len)
    {
        if (px[(y * w + x) * 4 + 3] < 255) return -1;
        int bestNet = 0;
        for (int c = 0; c < 3; c++)
        {
            int first = px[(y * w + x) * 4 + c];
            int last = px[((y + dy * (len - 1)) * w + x + dx * (len - 1)) * 4 + c];
            int net = last - first, sign = Math.Sign(net);
            int prev = first;
            for (int i = 1; i < len; i++)
            {
                int k = ((y + dy * i) * w + x + dx * i) * 4;
                if (px[k + 3] < 255) return -1;
                int v = px[k + c], step = v - prev;
                if (Math.Abs(step) > RampMaxStep) return 0;
                if (step * sign < -1 || (sign == 0 && step != 0 && Math.Abs(step) > 1)) return 0;
                prev = v;
            }
            bestNet = Math.Max(bestNet, Math.Abs(net));
        }
        return bestNet >= RampMinNet ? 1 : 0;
    }
}
