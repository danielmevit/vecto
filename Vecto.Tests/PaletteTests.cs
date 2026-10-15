using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// Light tints: cream #fff6e8, white and pale blue #f1f6ff sit 0.031-0.034 apart in Oklab,
/// inside both palette merge thresholds — on the Store cat they melted into one near-white.
/// Large flat light fills must keep their colours, while JPEG-like noise around white
/// still collapses to a single white entry.
/// </summary>
public class PaletteTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Cream = new(0xff, 0xf6, 0xe8, 255);
    static readonly Rgba32 PaleBlue = new(0xf1, 0xf6, 0xff, 255);
    static readonly Rgba32 Ink = new(0x2a, 0x22, 0x30, 255);

    static Rgba32 Lerp(Rgba32 a, Rgba32 b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t),
        255);

    /// <summary>Disk coverage of a pixel at <paramref name="dist"/> from the centre (1 px anti-aliasing ramp).</summary>
    static double Cover(double radius, double dist) => Math.Clamp(radius - dist + 0.5, 0.0, 1.0);

    /// <summary>
    /// White background with two large anti-aliased disks — cream and pale blue — each with
    /// a 3 px dark outline, like flat cartoon art.
    /// </summary>
    static RasterImage TintImage(int w = 200, int h = 160)
    {
        var shapes = new[] { (X: 60.0, Y: 70.0, R: 48.0, Fill: Cream), (X: 145.0, Y: 90.0, R: 48.0, Fill: PaleBlue) };
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = White;
                foreach (var s in shapes)
                {
                    double d = Math.Sqrt((x - s.X) * (x - s.X) + (y - s.Y) * (y - s.Y));
                    c = Lerp(c, Ink, Cover(s.R, d));
                    c = Lerp(c, s.Fill, Cover(s.R - 3, d));
                }
                img.SetPixel(x, y, c);
            }
        return img;
    }

    static bool Near(Rgba32 a, Rgba32 b, int tol = 3) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    static bool IsLight(Rgba32 c) => Oklab.FromRgb(c.R, c.G, c.B).L >= 0.85f;

    [Fact]
    public void LargeFlatLightTintsKeepTheirOwnColours()
    {
        var res = Tracer.Trace(TintImage(), new TraceOptions());
        Assert.Equal(ImageStyle.Blended, res.Diagnostics.Params.Style);
        var palette = res.Document.Palette.Select(e => e.Color).ToList();

        int white = palette.FindIndex(c => Near(c, White));
        int cream = palette.FindIndex(c => Near(c, Cream));
        int blue = palette.FindIndex(c => Near(c, PaleBlue));
        Assert.True(white >= 0, "white lost: " + string.Join(",", palette));
        Assert.True(cream >= 0, "cream #fff6e8 merged away: " + string.Join(",", palette));
        Assert.True(blue >= 0, "pale blue #f1f6ff merged away: " + string.Join(",", palette));
        Assert.Contains(palette, c => Near(c, Ink, 12));
        // and no extra near-white duplicates beside the three tints
        Assert.Equal(3, palette.Count(IsLight));

        // each tint fills its disk as one region
        Assert.Single(res.Document.Regions, r => r.PaletteIndex == cream);
        Assert.Single(res.Document.Regions, r => r.PaletteIndex == blue);
    }

    /// <summary>
    /// JPEG-like noise on the white background: every 4×4 block gets its own −7…0 offset per
    /// channel (clipped at 255, as decoded JPEG near white is), so dozens of exact near-white
    /// colours each cover a sizeable area. None of them is a deliberate tint.
    /// </summary>
    [Fact]
    public void JpegNoiseAroundWhiteStaysOneColour()
    {
        int w = 200, h = 160;
        var img = TintImage(w, h);
        var rnd = new Random(7);
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                int dr = rnd.Next(-7, 4), dg = rnd.Next(-7, 4), db = rnd.Next(-7, 4);
                for (int y = by; y < by + 4; y++)
                    for (int x = bx; x < bx + 4; x++)
                    {
                        var p = img.GetPixel(x, y);
                        if (p != White) continue;
                        img.SetPixel(x, y, new Rgba32(
                            (byte)Math.Clamp(p.R + dr, 0, 255),
                            (byte)Math.Clamp(p.G + dg, 0, 255),
                            (byte)Math.Clamp(p.B + db, 0, 255),
                            255));
                    }
            }
        // the tint disks stay clean: the background noise alone must not add entries
        var res = Tracer.Trace(img, new TraceOptions());
        var light = res.Document.Palette.Select(e => e.Color).Where(IsLight).ToList();
        Assert.True(light.Count == 3, "light entries: " + string.Join(",", light));
        Assert.Contains(light, c => Near(c, Cream));
        Assert.Contains(light, c => Near(c, PaleBlue));
    }

    [Fact]
    public void JpegNoiseAroundWhiteAloneMakesNoTints()
    {
        int w = 160, h = 160;
        var img = new RasterImage(w, h);
        var rnd = new Random(11);
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                int dr = rnd.Next(-7, 4), dg = rnd.Next(-7, 4), db = rnd.Next(-7, 4);
                for (int y = by; y < by + 4; y++)
                    for (int x = bx; x < bx + 4; x++)
                    {
                        double d = Math.Sqrt((x - 80.0) * (x - 80.0) + (y - 80.0) * (y - 80.0));
                        var c = Lerp(White, Ink, Cover(40, d));
                        img.SetPixel(x, y, c == White
                            ? new Rgba32((byte)Math.Min(255, 255 + dr), (byte)Math.Min(255, 255 + dg), (byte)Math.Min(255, 255 + db), 255)
                            : c);
                    }
            }
        var res = Tracer.Trace(img, new TraceOptions());
        var light = res.Document.Palette.Select(e => e.Color).Where(IsLight).ToList();
        Assert.True(light.Count == 1, "light entries: " + string.Join(",", light));
    }
}
