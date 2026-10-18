using System.Globalization;
using PDFtoImage;
using SkiaSharp;
using Vecto.Core;

namespace Vecto.Cli;

/// <summary>
/// PDF input for the CLI: rasterizes the selected pages in memory with PDFium (via the
/// PDFtoImage wrapper) and hands RGBA bitmaps to the tracing pipeline — no temp files.
/// Image smoothing is disabled (PDFium's RENDER_NO_SMOOTHIMAGE): scanned drawings must
/// not be blurred by resampling before Sauvola binarization. PDFium is not thread-safe;
/// every call here is single-threaded.
/// </summary>
public static class PdfInput
{
    public const int DefaultDpi = 600;

    /// <summary>Upper bound accepted for --pdf-dpi; the per-page pixel cap is the real memory guard.</summary>
    public const int MaxDpi = 4800;

    /// <summary>Pages that would rasterize to more than this on a side are rejected (pathological MediaBox guard).</summary>
    public const int MaxSidePx = 16000;

    /// <summary>One rasterized PDF page; Number is 1-based as the user wrote it in --pages.</summary>
    public sealed record PdfPage(int Number, RasterImage Image);

    public static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a --pdf-dpi value: an integer in [1, MaxDpi], invariant culture.</summary>
    public static int ParseDpi(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dpi) ||
            dpi < 1 || dpi > MaxDpi)
            throw new ArgumentException(
                FormattableString.Invariant($"--pdf-dpi must be an integer between 1 and {MaxDpi}, got \"{value}\""));
        return dpi;
    }

    /// <summary>
    /// Parses a page range — "1", "2-4", "1,3-5" — into sorted, deduplicated 1-based page
    /// numbers validated against the document's page count.
    /// </summary>
    public static List<int> ParsePageRange(string range, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(range))
            throw new ArgumentException("--pages: empty page range");
        var pages = new SortedSet<int>();
        foreach (var token in range.Split(',', StringSplitOptions.TrimEntries))
        {
            int dash = token.IndexOf('-');
            int first, last;
            if (dash < 0)
            {
                if (!TryPage(token, out first))
                    throw new ArgumentException($"--pages: \"{token}\" is not a page number");
                last = first;
            }
            else
            {
                if (!TryPage(token[..dash], out first) || !TryPage(token[(dash + 1)..], out last) || first > last)
                    throw new ArgumentException($"--pages: \"{token}\" is not a page range (expected e.g. 2-4)");
            }
            if (first < 1 || last > pageCount)
                throw new ArgumentException(
                    FormattableString.Invariant($"--pages: page {(first < 1 ? first : last)} is out of range (document has {pageCount} page(s))"));
            for (int p = first; p <= last; p++)
                pages.Add(p);
        }
        return [.. pages];

        static bool TryPage(string s, out int page) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out page);
    }

    /// <summary>
    /// Rasterizes the selected pages of a PDF at the requested DPI. Page sizes come from
    /// PDFium in points (1/72"); a page that would exceed <see cref="MaxSidePx"/> on a side
    /// is rejected with the highest DPI that would have fit.
    /// </summary>
    public static List<PdfPage> Load(string path, int dpi, string pageRange)
    {
        if (dpi < 1 || dpi > MaxDpi)
            throw new ArgumentException(FormattableString.Invariant($"DPI must be between 1 and {MaxDpi}"));
        var bytes = File.ReadAllBytes(path);
        var sizes = Conversion.GetPageSizes(bytes);
        if (sizes.Count == 0)
            throw new ArgumentException($"{Path.GetFileName(path)}: PDF has no pages");
        var numbers = ParsePageRange(pageRange, sizes.Count);
        foreach (var n in numbers)
        {
            var size = sizes[n - 1];
            int w = (int)Math.Ceiling(size.Width * dpi / 72.0);
            int h = (int)Math.Ceiling(size.Height * dpi / 72.0);
            if (w > MaxSidePx || h > MaxSidePx)
            {
                int fit = Math.Min(MaxDpi, (int)(MaxSidePx * 72.0 / Math.Max(size.Width, size.Height)));
                throw new ArgumentException(FormattableString.Invariant(
                    $"{Path.GetFileName(path)}: page {n} would rasterize to {w}x{h} px at {dpi} DPI (cap is {MaxSidePx} px per side); use --pdf-dpi {fit} or lower"));
            }
        }
        var options = new RenderOptions(dpi)
        {
            // no Images flag => PDFium renders with RENDER_NO_SMOOTHIMAGE; text and vector
            // paths keep their anti-aliasing so vector PDFs still rasterize cleanly
            AntiAliasing = PdfAntiAliasing.Text | PdfAntiAliasing.Paths,
        };
        var result = new List<PdfPage>(numbers.Count);
        foreach (var bitmap in Conversion.ToImages(bytes, numbers.Select(n => n - 1), options: options))
            using (bitmap)
                result.Add(new PdfPage(numbers[result.Count], ToRasterImage(bitmap)));
        return result;
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
