using Vecto.Core;
using Xunit;
using Xunit.Abstractions;

namespace Vecto.Tests;

/// <summary>
/// In-app tips (VEC-46): ImageHints flags small pictures and smooth colour fades, and stays
/// quiet on flat art, anti-aliased edges and JPEG-like noise.
/// </summary>
public class ImageHintsTests
{
    readonly ITestOutputHelper _out;
    public ImageHintsTests(ITestOutputHelper output) => _out = output;

    static RasterImage Fill(int w, int h, Func<int, int, Rgba32> f)
    {
        var img = new RasterImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, f(x, y));
        return img;
    }

    static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Flat logo: white ground, a red disc and a blue bar with 1-px anti-aliased edges.</summary>
    static RasterImage FlatLogo(int size) => Fill(size, size, (x, y) =>
    {
        double cx = size * 0.4, r = size * 0.25;
        double d = Math.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
        double a = Math.Clamp(r + 0.5 - d, 0, 1);
        var c = (R: 255 - a * 35, G: 255 - a * 215, B: 255 - a * 215);
        double barA = Math.Clamp(Math.Min(y - size * 0.75 + 0.5, size * 0.85 + 0.5 - y), 0, 1);
        c = (c.R * (1 - barA) + 30 * barA, c.G * (1 - barA) + 80 * barA, c.B * (1 - barA) + 200 * barA);
        return new Rgba32(B(c.R), B(c.G), B(c.B), 255);
    });

    [Fact]
    public void SmallPictureFires()
    {
        var r = ImageHints.Analyze(FlatLogo(200));
        Assert.Equal(ImageHint.SmallPicture, r.Hint);
        Assert.Equal(200, r.ContentWidth);
    }

    [Fact]
    public void LargeFlatLogoIsQuiet()
    {
        var r = ImageHints.Analyze(FlatLogo(512));
        _out.WriteLine($"share {r.RampShare:F3}");
        Assert.Equal(ImageHint.None, r.Hint);
    }

    [Fact]
    public void SmallSideUsesOpaqueContent()
    {
        // 600x600 canvas, only a 150x150 opaque block: small content.
        var img = Fill(600, 600, (x, y) => x < 150 && y < 150 ? new Rgba32(10, 10, 10, 255) : new Rgba32(0, 0, 0, 0));
        var r = ImageHints.Analyze(img);
        Assert.Equal(ImageHint.SmallPicture, r.Hint);
        Assert.Equal(150, r.ContentHeight);
    }

    [Fact]
    public void LinearGradientFires()
    {
        var img = Fill(512, 384, (x, y) => new Rgba32(B(20 + x * 200.0 / 511), B(60 + x * 120.0 / 511), B(220 - x * 150.0 / 511), 255));
        var r = ImageHints.Analyze(img);
        _out.WriteLine($"share {r.RampShare:F3}");
        Assert.Equal(ImageHint.SmoothGradients, r.Hint);
    }

    [Fact]
    public void JpegLikeNoiseOnFlatIsQuiet()
    {
        var rng = new Random(7);
        var img = Fill(512, 512, (x, y) =>
        {
            int n() => rng.Next(-3, 4);
            return x < 256 ? new Rgba32(B(200 + n()), B(40 + n()), B(40 + n()), 255)
                           : new Rgba32(B(250 + n()), B(250 + n()), B(250 + n()), 255);
        });
        var r = ImageHints.Analyze(img);
        _out.WriteLine($"share {r.RampShare:F3}");
        Assert.Equal(ImageHint.None, r.Hint);
    }

    [Fact]
    public void BigSmallBothPicksSmall()
    {
        var img = Fill(400, 120, (x, y) => new Rgba32(B(x * 255.0 / 399), 100, 100, 255));
        Assert.Equal(ImageHint.SmallPicture, ImageHints.Analyze(img).Hint);
    }
}
