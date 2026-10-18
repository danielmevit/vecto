using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Vecto.Core;

/// <summary>
/// PDF 1.4 output: one page sized w×h pt (1 px = 1 pt), one filled path per region with
/// all loops as subpaths and nonzero winding (<c>f</c>) — holes wind opposite, so they
/// render as holes, exactly like the SVG output. Hand-assembled from text operators,
/// BCL only, culture-invariant by construction; the same document always produces
/// byte-identical bytes (no timestamps, explicit compression level).
/// </summary>
public static class PdfWriter
{
    public static void Write(VectorDocument doc, string path) => File.WriteAllBytes(path, Write(doc));

    public static byte[] Write(VectorDocument doc)
    {
        var content = new StringBuilder(1 << 16);
        // PDF has no per-page alpha in this model: documents whose regions don't tile the
        // page (transparent backgrounds) get a white page first, like a viewer would show
        if (!CoversPage(doc))
            content.Append("1 1 1 rg\n0 0 ").Append(doc.Width).Append(' ').Append(doc.Height).Append(" re f\n");
        foreach (var region in doc.Regions)
        {
            if (!region.Loops.Any(l => l.Count > 0)) continue;
            var c = doc.Palette[region.PaletteIndex].Color;
            content.Append(C(c.R)).Append(' ').Append(C(c.G)).Append(' ').Append(C(c.B)).Append(" rg\n");
            foreach (var loop in region.Loops)
                AppendLoop(content, loop, doc.Height);
            content.Append("f\n");
        }
        foreach (var stroke in doc.Strokes)
        {
            var c = doc.Palette[stroke.PaletteIndex].Color;
            content.Append(C(c.R)).Append(' ').Append(C(c.G)).Append(' ').Append(C(c.B)).Append(" RG\n");
            content.Append(stroke.Width.ToString("0.00", CultureInfo.InvariantCulture)).Append(" w\n");
            content.Append("1 J\n1 j\n");
            AppendStroke(content, stroke, doc.Height);
            content.Append("S\n");
        }

        var stream = Compress(Encoding.Latin1.GetBytes(content.ToString()));
        using var pdf = new MemoryStream();
        var offsets = new long[4];
        void Put(string s) => pdf.Write(Encoding.Latin1.GetBytes(s));
        Put("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");
        offsets[0] = pdf.Position; Put("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        offsets[1] = pdf.Position; Put("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        offsets[2] = pdf.Position; Put(FormattableString.Invariant(
            $"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 {doc.Width} {doc.Height}] /Resources << >> /Contents 4 0 R >> endobj\n"));
        offsets[3] = pdf.Position; Put(FormattableString.Invariant(
            $"4 0 obj << /Filter /FlateDecode /Length {stream.Length} >> stream\n"));
        pdf.Write(stream);
        Put("\nendstream endobj\n");
        long xref = pdf.Position;
        Put("xref\n0 5\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Put(FormattableString.Invariant($"{o:D10} 00000 n \n"));
        Put(FormattableString.Invariant($"trailer << /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return pdf.ToArray();
    }

    /// <summary>One loop as one subpath: move, line/curve operators, close. y is flipped
    /// (PDF origin is bottom-left); mirroring preserves the outer/hole winding relation.</summary>
    static void AppendLoop(StringBuilder sb, List<CubicBezier> loop, int height)
    {
        if (loop.Count == 0) return;
        P(sb, loop[0].P0.X, height - loop[0].P0.Y);
        sb.Append(" m\n");
        foreach (var seg in loop)
        {
            if (seg.IsLine())
            {
                P(sb, seg.P3.X, height - seg.P3.Y);
                sb.Append(" l\n");
            }
            else
            {
                P(sb, seg.P1.X, height - seg.P1.Y);
                sb.Append(' ');
                P(sb, seg.P2.X, height - seg.P2.Y);
                sb.Append(' ');
                P(sb, seg.P3.X, height - seg.P3.Y);
                sb.Append(" c\n");
            }
        }
        sb.Append("h\n");
    }

    /// <summary>Stroked centerline path: same operators, but h only closes true rings — an open
    /// stroke must not gain a closing chord. Stroked, not filled, by the caller's S.</summary>
    static void AppendStroke(StringBuilder sb, StrokePath stroke, int height)
    {
        P(sb, stroke.Segments[0].P0.X, height - stroke.Segments[0].P0.Y);
        sb.Append(" m\n");
        foreach (var seg in stroke.Segments)
        {
            if (seg.IsLine())
            {
                P(sb, seg.P3.X, height - seg.P3.Y);
                sb.Append(" l\n");
            }
            else
            {
                P(sb, seg.P1.X, height - seg.P1.Y);
                sb.Append(' ');
                P(sb, seg.P2.X, height - seg.P2.Y);
                sb.Append(' ');
                P(sb, seg.P3.X, height - seg.P3.Y);
                sb.Append(" c\n");
            }
        }
        if (stroke.Closed) sb.Append("h\n");
    }

    static void P(StringBuilder sb, double x, double y)
    {
        sb.Append(x.ToString("0.00", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(y.ToString("0.00", CultureInfo.InvariantCulture));
    }

    /// <summary>Fill colour component in 0–1, 3 decimals (error well under 1/255).</summary>
    static string C(byte v) => (v / 255.0).ToString("0.###", CultureInfo.InvariantCulture);

    static byte[] Compress(byte[] data)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return raw.ToArray();
    }

    /// <summary>
    /// Whether the regions tile the whole page (opaque document). Same 16-sample
    /// quadrature the CLI planarity check uses; holes subtract because they wind opposite.
    /// </summary>
    static bool CoversPage(VectorDocument doc)
    {
        double sum = 0;
        foreach (var region in doc.Regions)
            foreach (var loop in region.Loops)
            {
                if (loop.Count == 0) continue;
                double signed = 0;
                var prev = loop[0].P0;
                foreach (var seg in loop)
                    for (int s = 1; s <= 16; s++)
                    {
                        var cur = seg.Eval(s / 16.0);
                        signed += prev.X * cur.Y - cur.X * prev.Y;
                        prev = cur;
                    }
                sum += signed * 0.5;
            }
        return Math.Abs(sum) >= 0.999 * (double)doc.Width * doc.Height;
    }
}
