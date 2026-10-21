using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vecto.Core;

namespace Vecto.App;

/// <summary>Actual WPF geometry and pixel checks, run on the startup STA without a window.</summary>
internal static class RenderingSmoke
{
    internal static void Run()
    {
        var output = Path.Combine(Path.GetTempPath(), "vecto-rendering-smoke");
        Directory.CreateDirectory(output);
        static CubicBezier Line(double x, double y, double u, double v) =>
            new(new(x, y), new(x, y), new(u, v), new(u, v));
        var doc = new VectorDocument
        {
            Width = 160, Height = 120,
            Palette = [new() { Color = new(255, 255, 255, 255) },
                new() { Color = new(20, 20, 20, 255) }, new() { Color = new(220, 40, 60, 255) }],
            Regions = [new() { PaletteIndex = 0, Loops = [[Line(0,0,160,0), Line(160,0,160,120),
                Line(160,120,0,120), Line(0,120,0,0)]] },
                new() { PaletteIndex = 1, Loops = [[Line(35,10,55,10), Line(55,10,55,35),
                    Line(55,35,35,35), Line(35,35,35,10)],
                    [Line(40,15,40,30), Line(40,30,50,30), Line(50,30,50,15), Line(50,15,40,15)]] }],
            Strokes = [new() { PaletteIndex = 2, Width = 8, Segments = [Line(20,25,80,25)] },
                new() { PaletteIndex = 2, Width = 4, Segments = [
                    new(new(20,80), new(20,40), new(50,40), new(50,80)),
                    new(new(50,80), new(50,100), new(80,100), new(80,80))] },
                new() { PaletteIndex = 2, Width = 4, Closed = true, Segments = [
                    new(new(110,60), new(90,60), new(90,90), new(110,90)),
                    new(new(110,90), new(130,90), new(130,60), new(110,60))] },
                new() { Segments = [] }],
        };
        var preview = (DrawingImage)VectorRendering.ToDrawingImage(doc);
        Require(preview.IsFrozen, "preview frozen");
        var pixels = Render(preview, 1, "preview");
        CheckColor(pixels, 45, 25, 220, 40, 60); // visibility and paint order, including baseline failure
        var group = (DrawingGroup)preview.Drawing;
        Require(group.Children.Count == 6, "fills then three nonempty strokes");
        var line = (GeometryDrawing)group.Children[3];
        Require(line.Brush == null && line.Pen.Thickness == 8 && line.Pen.StartLineCap == PenLineCap.Round
            && line.Pen.EndLineCap == PenLineCap.Round && line.Pen.LineJoin == PenLineJoin.Round, "weighted round unfilled stroke");
        Require(!PathGeometry.CreateFromGeometry(line.Geometry).Figures.Single().IsClosed, "open line");
        Require(PathGeometry.CreateFromGeometry(((GeometryDrawing)group.Children[5]).Geometry).Figures.Single().IsClosed, "closed ring");
        CheckColor(pixels, 45, 18, 255, 255, 255); // nonzero glyph hole preserved
        CheckColor(pixels, 45, 28, 220, 40, 60);
        CheckColor(pixels, 17, 25, 220, 40, 60); // round cap extends beyond endpoint
        CheckColor(pixels, 16, 21, 255, 255, 255); // corner outside round cap
        CheckColor(pixels, 45, 31, 20, 20, 20); // width bounded; original glyph preserved
        CheckColor(pixels, 35, 80, 255, 255, 255); // no chord across open arc
        CheckColor(pixels, 35, 50, 220, 40, 60); // cubic arc centreline
        CheckColor(pixels, 95, 75, 220, 40, 60); // ring centreline
        CheckColor(pixels, 110, 75, 255, 255, 255); // ring has no fill
        foreach (double zoom in new[] { 1.0, 2.0 })
        {
            var wire = (DrawingImage)VectorRendering.ToWireframeImage(doc, zoom);
            Require(wire.IsFrozen && wire.Width == 160 && wire.Height == 120, "wire frozen/canvas alignment");
            var wg = (DrawingGroup)wire.Drawing;
            Require(Math.Abs(((GeometryDrawing)wg.Children[1]).Pen.Thickness * zoom - 1) < .001, "screen outline width");
            var markers = (GeometryGroup)((GeometryDrawing)wg.Children[2]).Geometry;
            Require(markers.Children.Count == 19, "12 fill + 2 line + 3 curve + 2 ring anchors");
            foreach (var x in new[] { 20, 80 })
                Require(markers.Children.OfType<RectangleGeometry>().Count(r =>
                    Math.Abs(r.Rect.X + r.Rect.Width / 2 - x) < .001 &&
                    Math.Abs(r.Rect.Y + r.Rect.Height / 2 - 25) < .001) == 1, "unique line endpoints");
            var bmp = Render(wire, zoom, $"wire-{zoom}");
            CheckColor(bmp, (int)(20 * zoom), (int)(25 * zoom), 12, 140, 233);
            Require(Math.Abs(markers.Children[0].Bounds.Width * zoom - 3) < .001, "screen marker size");
        }
        // Exactly 20,000 anchors retain markers; adding two stroke endpoints suppresses them.
        doc.Strokes.Clear();
        for (int i = 0; i < 9994; i++) doc.Strokes.Add(new() { Width = 1, Segments = [Line(10,50,15,50)] });
        Require(((DrawingGroup)((DrawingImage)VectorRendering.ToWireframeImage(doc, 1)).Drawing).Children.Count == 3, "20k markers retained");
        doc.Strokes.Add(new() { Width = 1, Segments = [Line(10,50,15,50)] });
        Require(((DrawingGroup)((DrawingImage)VectorRendering.ToWireframeImage(doc, 1)).Drawing).Children.Count == 2, "stroke anchors enforce marker limit");
        File.WriteAllText(Path.Combine(output, "checks.txt"), "PASS: geometry, pixels, frozen images, two zooms, 20k threshold\n");

        BitmapSource Render(ImageSource source, double scale, string name)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, 160 * scale, 120 * scale));
            var bitmap = new RenderTargetBitmap((int)(160 * scale), (int)(120 * scale), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(file);
            return bitmap;
        }
    }

    static void CheckColor(BitmapSource bitmap, int x, int y, int r, int g, int b)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Require(Math.Abs(pixel[0] - b) <= 8 && Math.Abs(pixel[1] - g) <= 8
            && Math.Abs(pixel[2] - r) <= 8 && pixel[3] == 255, $"pixel ({x},{y}) expected {r},{g},{b}, actual {string.Join(',', pixel)}");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Rendering smoke: " + message);
    }
}
