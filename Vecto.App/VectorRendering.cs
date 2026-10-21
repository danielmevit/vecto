using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vecto.Core;

namespace Vecto.App;

internal static class VectorRendering
{
    /// <summary>Vector document → WPF DrawingImage; stays crisp at any zoom because it re-renders as geometry.</summary>
    public static ImageSource ToDrawingImage(VectorDocument doc)
    {
        var group = new DrawingGroup
        {
            ClipGeometry = new RectangleGeometry(new Rect(0, 0, doc.Width, doc.Height)),
        };
        // transparent canvas rect keeps bounds equal to the full image even when shapes don't cover it
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, doc.Width, doc.Height))));
        foreach (var region in doc.Regions)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var g = geometry.Open())
            {
                foreach (var loop in region.Loops)
                {
                    if (loop.Count == 0) continue;
                    g.BeginFigure(P(loop[0].P0), isFilled: true, isClosed: true);
                    foreach (var seg in loop)
                        g.BezierTo(P(seg.P1), P(seg.P2), P(seg.P3), isStroked: false, isSmoothJoin: false);
                }
            }
            geometry.Freeze();
            var c = doc.Palette[region.PaletteIndex].Color;
            var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, geometry));
        }
        foreach (var stroke in doc.Strokes)
        {
            if (stroke.Segments.Count == 0) continue;
            var c = doc.Palette[stroke.PaletteIndex].Color;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), stroke.Width)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen, StrokeGeometry(stroke)));
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;

        static Point P(Vec2 v) => new(v.X, v.Y);
    }

    /// <summary>Path outlines + node markers over a transparent canvas; stroke/marker sizes are in
    /// screen pixels (divided by zoom), so callers must rebuild when the zoom changes.</summary>
    public static ImageSource ToWireframeImage(VectorDocument doc, double zoom)
    {
        double t = 1.0 / Math.Max(zoom, 0.01);
        var group = new DrawingGroup
        {
            // strokes/markers overhang the canvas at the border; clipping keeps the image
            // bounds exactly doc-sized so both panes stay in scale
            ClipGeometry = new RectangleGeometry(new Rect(0, 0, doc.Width, doc.Height)),
        };
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, doc.Width, doc.Height))));

        var outlines = new GeometryGroup();
        int nodeTotal = 0;
        foreach (var region in doc.Regions)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                foreach (var loop in region.Loops)
                {
                    if (loop.Count == 0) continue;
                    nodeTotal += loop.Count;
                    g.BeginFigure(P(loop[0].P0), isFilled: false, isClosed: true);
                    foreach (var seg in loop)
                        g.BezierTo(P(seg.P1), P(seg.P2), P(seg.P3), isStroked: true, isSmoothJoin: false);
                }
            }
            geometry.Freeze();
            outlines.Children.Add(geometry);
        }
        foreach (var stroke in doc.Strokes)
        {
            if (stroke.Segments.Count == 0) continue;
            outlines.Children.Add(StrokeGeometry(stroke));
            nodeTotal += StrokeAnchors(stroke).Count();
        }
        outlines.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x8c, 0x8c, 0x8c)), t);
        pen.Freeze();
        group.Children.Add(new GeometryDrawing(null, pen, outlines));

        // marker rects are per-node WPF objects; past ~20k the view stops being interactive
        if (nodeTotal <= 20_000)
        {
            var markers = new GeometryGroup();
            double r = 1.5 * t;
            foreach (var region in doc.Regions)
                foreach (var loop in region.Loops)
                    foreach (var seg in loop)
                        markers.Children.Add(new RectangleGeometry(new Rect(seg.P0.X - r, seg.P0.Y - r, 2 * r, 2 * r)));
            foreach (var stroke in doc.Strokes)
                foreach (var anchor in StrokeAnchors(stroke))
                    markers.Children.Add(new RectangleGeometry(new Rect(anchor.X - r, anchor.Y - r, 2 * r, 2 * r)));
            markers.Freeze();
            var fill = new SolidColorBrush(Color.FromRgb(0x0c, 0x8c, 0xe9));
            fill.Freeze();
            group.Children.Add(new GeometryDrawing(fill, null, markers));
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;

        static Point P(Vec2 v) => new(v.X, v.Y);
    }

    static StreamGeometry StrokeGeometry(StrokePath stroke)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            if (stroke.Segments.Count > 0)
            {
                var start = stroke.Segments[0].P0;
                g.BeginFigure(new Point(start.X, start.Y), false, stroke.Closed);
                foreach (var seg in stroke.Segments)
                    g.BezierTo(new Point(seg.P1.X, seg.P1.Y), new Point(seg.P2.X, seg.P2.Y),
                        new Point(seg.P3.X, seg.P3.Y), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    static IEnumerable<Vec2> StrokeAnchors(StrokePath stroke)
    {
        if (stroke.Segments.Count == 0) yield break;
        yield return stroke.Segments[0].P0;
        for (int i = 0; i < stroke.Segments.Count; i++)
        {
            var end = stroke.Segments[i].P3;
            if (i == stroke.Segments.Count - 1 && stroke.Closed && end == stroke.Segments[0].P0)
                continue;
            yield return end;
        }
    }

    /// <summary>RasterImage (RGBA) → frozen BitmapSource, for PNG encoding.</summary>
    public static BitmapSource ToBitmapSource(RasterImage img)
    {
        int w = img.Width, h = img.Height;
        var src = img.Pixels;
        var buf = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            buf[i] = src[i + 2];
            buf[i + 1] = src[i + 1];
            buf[i + 2] = src[i];
            buf[i + 3] = src[i + 3];
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource ToSegmentationBitmap(TraceResult result)
    {
        int w = result.Width, h = result.Height;
        var buf = new byte[w * h * 4];
        for (int i = 0; i < result.LabelMap.Length; i++)
        {
            int l = result.LabelMap[i];
            if (l < 0) continue;
            var c = result.Document.Palette[l].Color;
            int pi = i * 4;
            buf[pi] = c.B;
            buf[pi + 1] = c.G;
            buf[pi + 2] = c.R;
            buf[pi + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
