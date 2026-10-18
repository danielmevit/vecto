using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vecto.Core;

namespace Vecto.App;

internal static class ImageInterop
{
    public static RasterImage ToRaster(BitmapSource source)
    {
        BitmapSource conv = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var bgra = new byte[w * h * 4];
        conv.CopyPixels(bgra, w * 4, 0);
        for (int i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return new RasterImage(w, h, bgra);
    }

    /// <summary>RasterImage (straight RGBA8888) → frozen Bgra32 BitmapSource for display.</summary>
    public static BitmapSource ToBitmapSource(RasterImage raster)
    {
        int w = raster.Width, h = raster.Height;
        var px = raster.Pixels;
        var bgra = new byte[px.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = px[i + 2];
            bgra[i + 1] = px[i + 1];
            bgra[i + 2] = px[i];
            bgra[i + 3] = px[i + 3];
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
