using Vecto.Cli;
using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>LineArt mode (VEC-07): Sauvola binarization, diagonal bridging, CAD benchmark,
/// and the invariant that every pre-existing style is untouched.</summary>
public class LineArtTests
{
    static readonly Rgba32 White = new(255, 255, 255, 255);
    static readonly Rgba32 Black = new(0, 0, 0, 255);

    static RasterImage Make(int w, int h, Func<int, int, Rgba32> f)
    {
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, f(x, y));
        return img;
    }

    static int Components4(bool[] mask, int w, int h)
    {
        var seen = new bool[w * h];
        int count = 0;
        var stack = new int[w * h];
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            count++;
            int sp = 0;
            stack[sp++] = i;
            seen[i] = true;
            while (sp > 0)
            {
                int p = stack[--sp];
                int x = p % w;
                if (x > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack[sp++] = p - 1; }
                if (x < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack[sp++] = p + 1; }
                if (p >= w && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack[sp++] = p - w; }
                if (p < w * (h - 1) && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack[sp++] = p + w; }
            }
        }
        return count;
    }

    [Fact]
    public void Sauvola_GradientLitPage_SeparatesInkFromPaper()
    {
        // Faint ink (90) under a strong 1.0 -> 0.45 illumination gradient: Sauvola classifies
        // ink vs paper correctly across the page with a locally adapted threshold.
        const int w = 160, h = 120;
        var img = Make(w, h, (x, y) =>
        {
            double grad = 1.0 - 0.55 * x / (w - 1.0);
            bool stroke = y is >= 30 and <= 32 && x is >= 20 and < 140;
            byte v = (byte)Math.Round((stroke ? 90.0 : 250.0) * grad);
            return new Rgba32(v, v, v, 255);
        });
        var ink = ScanPreprocess.InkMask(img);
        Assert.True(ink[31 * w + 70], "stroke centre must be ink");
        Assert.True(ink[30 * w + 70] && ink[32 * w + 70], "the whole 3-px stroke must be ink");
        Assert.False(ink[100 * w + 10], "dark-corner paper must stay paper");
        Assert.False(ink[100 * w + 150], "bright-corner paper must stay paper");
        // no ink anywhere outside the stroke
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (y is < 30 or > 32 || x is < 20 or >= 140)
                    Assert.False(ink[y * w + x], $"stray ink at {x},{y}");
    }

    [Fact]
    public void BridgeDiagonalContacts_45DegreeLine_BecomesOneComponent()
    {
        const int w = 64, h = 64;
        var ink = new bool[w * h];
        for (int i = 0; i <= 47; i++) ink[(8 + i) * w + (8 + i)] = true;
        Assert.Equal(48, Components4(ink, w, h));   // 4-connectivity sees 48 isolated pixels
        ScanPreprocess.BridgeDiagonalContacts(ink, w, h);
        Assert.Equal(1, Components4(ink, w, h));
    }

    [Fact]
    public void LineArt_OnePixelDiagonal_TracesAsSingleInkRegion()
    {
        // the VEC-07 repro: a 1-px 45-degree line used to vanish (4-connected labelling split
        // it into 1-2 px runs that small-region absorption deleted)
        var img = Make(64, 64, (x, y) => x >= 8 && x <= 56 && y == x - 8 ? Black : White);
        var res = Tracer.Trace(img, new TraceOptions { Style = ImageStyle.LineArt });
        Assert.Equal(2, res.Document.Regions.Count);   // paper + the stroke
        Assert.Equal(2, res.Document.Palette.Count);   // pure ink/paper palette
        var dark = res.Document.Regions.Where(r => res.Document.Palette[r.PaletteIndex].Color.R < 128).ToList();
        Assert.Single(dark);
        Assert.True(dark[0].Area >= 49, $"stroke region area {dark[0].Area} lost pixels");
    }

    [Fact]
    public void CadScanBenchmark_RecallAndPrecisionAtLeast95()
    {
        var (scan, mask) = SampleGen.CadScan();
        var res = Tracer.Trace(scan, new TraceOptions { Style = ImageStyle.LineArt });
        var rendered = Rasterizer.Render(res.Document);

        long truth = 0, traced = 0, both = 0;
        var mp = mask.Pixels;
        var rp = rendered.Pixels;
        for (int i = 0; i < mp.Length; i += 4)
        {
            bool trueInk = mp[i] < 128;
            bool tracedInk = rp[i + 3] >= 128 && rp[i] < 128;
            if (trueInk) truth++;
            if (tracedInk) traced++;
            if (trueInk && tracedInk) both++;
        }
        double recall = (double)both / truth;
        double precision = (double)both / traced;
        Assert.True(recall >= 0.95, $"recall {recall:0.###} < 0.95");
        Assert.True(precision >= 0.95, $"precision {precision:0.###} < 0.95");
    }

    [Fact]
    public void ExistingStyles_AndAuto_AreUnchanged()
    {
        // region/node counts captured from the current dev build for the three procedural
        // samples; LineArt must not perturb a single code path the other styles use
        var crisp = SampleGen.Crisp();
        var blended = SampleGen.Blended();
        var transparent = SampleGen.Transparent();
        AssertCounts(ImageStyle.Auto, crisp, 4, 50);
        AssertCounts(ImageStyle.Auto, blended, 4, 58);
        AssertCounts(ImageStyle.Auto, transparent, 3, 41);
        AssertCounts(ImageStyle.Crisp, crisp, 4, 50);
        AssertCounts(ImageStyle.Crisp, blended, 8, 82);
        AssertCounts(ImageStyle.Crisp, transparent, 3, 41);
        AssertCounts(ImageStyle.Blended, crisp, 4, 50);
        AssertCounts(ImageStyle.Blended, blended, 4, 58);
        AssertCounts(ImageStyle.Blended, transparent, 3, 33);
        AssertCounts(ImageStyle.Photo, crisp, 4, 66);
        AssertCounts(ImageStyle.Photo, blended, 4, 58);
        AssertCounts(ImageStyle.Photo, transparent, 3, 33);

        // Auto must keep resolving to Crisp/Blended, never to LineArt
        Assert.Equal(ImageStyle.Crisp, Tracer.Trace(crisp, new TraceOptions()).Diagnostics.Params.Style);
        Assert.Equal(ImageStyle.Blended, Tracer.Trace(blended, new TraceOptions()).Diagnostics.Params.Style);

        static void AssertCounts(ImageStyle style, RasterImage img, int regions, int nodes)
        {
            var res = Tracer.Trace(img, new TraceOptions { Style = style });
            Assert.True(res.Diagnostics.RegionCount == regions && res.Diagnostics.NodeCount == nodes,
                $"{style} on {img.Width}x{img.Height}: {res.Diagnostics.RegionCount} regions / {res.Diagnostics.NodeCount} nodes, expected {regions}/{nodes}");
        }
    }
}
