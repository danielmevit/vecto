using System.IO;
using PDFtoImage;
using SkiaSharp;
using Vecto.Core;

namespace Vecto.App;

/// <summary>
/// PDF input for the app: rasterizes the first page in memory with PDFium (via the same
/// PDFtoImage wrapper and render flags as the CLI's PdfInput) and hands the result to the
/// tracing pipeline as a RasterImage — no temp files. Image smoothing is disabled
/// (PDFium's RENDER_NO_SMOOTHIMAGE): scanned drawings must not be blurred by resampling
/// before Sauvola binarization. PDFium is not thread-safe; every call here is single-threaded.
/// </summary>
internal static class PdfImport
{
    /// <summary>Fixed render DPI — the CLI's default; scanned drawings need the resolution.</summary>
    public const int Dpi = 600;

    /// <summary>Pages that would rasterize to more than this on a side are rejected (pathological MediaBox guard).</summary>
    public const int MaxSidePx = 16000;

    /// <summary>One rasterized PDF page plus the document's total page count (for the status line).</summary>
    public sealed record FirstPage(RasterImage Image, int PageCount);

    public static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rasterizes page 1 at <see cref="Dpi"/>. Page size comes from PDFium in points (1/72");
    /// a page that would exceed <see cref="MaxSidePx"/> on a side is rejected. Throws on any
    /// failure — the caller shows the message in the status line and keeps the current image.
    /// </summary>
    public static FirstPage LoadFirstPage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var sizes = Conversion.GetPageSizes(bytes);
        if (sizes.Count == 0)
            throw new ArgumentException($"{Path.GetFileName(path)}: PDF has no pages");
        var size = sizes[0];
        int w = (int)Math.Ceiling(size.Width * Dpi / 72.0);
        int h = (int)Math.Ceiling(size.Height * Dpi / 72.0);
        if (w > MaxSidePx || h > MaxSidePx)
            throw new ArgumentException(FormattableString.Invariant(
                $"{Path.GetFileName(path)}: page 1 would rasterize to {w}x{h} px at {Dpi} DPI (cap is {MaxSidePx} px per side)"));
        var options = new RenderOptions(Dpi)
        {
            // no Images flag => PDFium renders with RENDER_NO_SMOOTHIMAGE; text and vector
            // paths keep their anti-aliasing so vector PDFs still rasterize cleanly
            AntiAliasing = PdfAntiAliasing.Text | PdfAntiAliasing.Paths,
        };
        SKBitmap? bitmap = null;
        foreach (var page in Conversion.ToImages(bytes, [0], options: options))
        {
            bitmap = page;
            break;
        }
        if (bitmap == null)
            throw new ArgumentException($"{Path.GetFileName(path)}: PDF has no pages");
        using (bitmap)
            return new FirstPage(ToRasterImage(bitmap), sizes.Count);
    }

    /// <summary>SKBitmap (Bgra8888 premultiplied) → RasterImage (straight RGBA8888, row-major).</summary>
    static RasterImage ToRasterImage(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        bool bgra = bitmap.ColorType == SKColorType.Bgra8888;
        if (!bgra && bitmap.ColorType != SKColorType.Rgba8888)
            throw new NotSupportedException($"PDF rendered as {bitmap.ColorType}; expected Bgra8888 or Rgba8888");
        int rI = bgra ? 2 : 0, bI = bgra ? 0 : 2;
        var src = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int s = y * rowBytes, d = y * w * 4;
            for (int x = 0; x < w; x++, s += 4, d += 4)
            {
                byte a = src[s + 3];
                if (a == 255)
                {
                    buf[d] = src[s + rI];
                    buf[d + 1] = src[s + 1];
                    buf[d + 2] = src[s + bI];
                    buf[d + 3] = a;
                }
                else if (a > 0)
                {
                    // un-premultiply (the page background is opaque, so this is rare)
                    buf[d] = Unpremultiply(src[s + rI], a);
                    buf[d + 1] = Unpremultiply(src[s + 1], a);
                    buf[d + 2] = Unpremultiply(src[s + bI], a);
                    buf[d + 3] = a;
                }
            }
        }
        return new RasterImage(w, h, buf);

        static byte Unpremultiply(byte c, byte a) => (byte)Math.Min(255, (c * 255 + a / 2) / a);
    }
}
