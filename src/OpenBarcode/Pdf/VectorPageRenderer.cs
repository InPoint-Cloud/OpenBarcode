using System;
using System.Collections.Generic;
using InPoint.Cloud.OpenBarcode.Imaging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace InPoint.Cloud.OpenBarcode.Pdf;

/// <summary>
/// Rasterizes the filled and stroked paths of a PDF page in drawing order. Text and images are not drawn:
/// this exists to make barcodes that a PDF generator drew as vector shapes visible to the decoders.
/// </summary>
internal static class VectorPageRenderer
{
    /// <summary>Pages with fewer drawable subpaths than this have nothing that could be a barcode.</summary>
    private const int MinSubpaths = 4;

    public static GrayImage? Render(Page page, int dpi, long maxPixels)
    {
        var drawable = new List<PdfPath>();
        var subpaths = 0;
        foreach (var path in page.Paths)
        {
            if (!path.IsFilled && !path.IsStroked) continue;
            drawable.Add(path);
            subpaths += path.Count;
        }
        if (subpaths < MinSubpaths) return null;

        var crop = page.CropBox.Bounds;
        double left = Math.Min(crop.Left, crop.Right), top = Math.Max(crop.Top, crop.Bottom);
        double pageW = Math.Abs(crop.Width), pageH = Math.Abs(crop.Height);
        if (pageW < 1 || pageH < 1) return null;

        // Only rasterize the part of the page that has vector content, plus a margin for the quiet zone.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var path in drawable)
        {
            var r = path.GetBoundingRectangle();
            if (!r.HasValue) continue;
            var b = r.Value;
            minX = Math.Min(minX, Math.Min(b.Left, b.Right));
            maxX = Math.Max(maxX, Math.Max(b.Left, b.Right));
            minY = Math.Min(minY, Math.Min(b.Bottom, b.Top));
            maxY = Math.Max(maxY, Math.Max(b.Bottom, b.Top));
        }
        if (minX > maxX) return null;
        const double margin = 18; // a quarter inch, in points
        var regionLeft = Math.Max(left, minX - margin);
        var regionRight = Math.Min(left + pageW, maxX + margin);
        var regionBottom = Math.Max(top - pageH, minY - margin);
        var regionTop = Math.Min(top, maxY + margin);
        double regionW = regionRight - regionLeft, regionH = regionTop - regionBottom;
        if (regionW <= 0 || regionH <= 0) return null;

        var scale = dpi / 72.0;
        var pixels = regionW * scale * regionH * scale;
        if (pixels > maxPixels) scale *= Math.Sqrt(maxPixels / pixels);
        var width = Math.Max(1, (int)Math.Ceiling(regionW * scale));
        var height = Math.Max(1, (int)Math.Ceiling(regionH * scale));

        var image = new GrayImage(width, height);
        var rasterizer = new PolygonRasterizer(image);
        Point ToDevice(PdfPoint p) => new((p.X - regionLeft) * scale, (regionTop - p.Y) * scale);

        foreach (var path in drawable)
        {
            var contours = Flatten(path, ToDevice, out var closed);
            if (path.IsFilled)
            {
                var fill = ToGray(path.FillColor);
                rasterizer.Fill(contours, path.FillingRule == FillingRule.EvenOdd, fill);
            }
            if (path.IsStroked)
            {
                var stroke = ToGray(path.StrokeColor);
                var lineWidth = path.LineWidth * scale;
                for (var c = 0; c < contours.Count; c++)
                {
                    var contour = contours[c];
                    for (var i = 0; i + 1 < contour.Count; i++)
                        rasterizer.StrokeSegment(contour[i], contour[i + 1], lineWidth, stroke);
                    if (closed[c] && contour.Count > 2)
                        rasterizer.StrokeSegment(contour[contour.Count - 1], contour[0], lineWidth, stroke);
                }
            }
        }
        return image;
    }

    private static byte ToGray(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color == null) return 0;
        try
        {
            var (r, g, b) = color.ToRGBValues();
            return GrayImage.Luma(ToByte(r), ToByte(g), ToByte(b));
        }
        catch (Exception)
        {
            return 0; // pattern or unknown colour spaces: assume ink
        }
    }

    private static int ToByte(double v) => v <= 0 ? 0 : v >= 1 ? 255 : (int)Math.Round(v * 255);

    /// <summary>Converts a path to polylines in device space; one list per subpath, curves flattened.</summary>
    private static List<List<Point>> Flatten(PdfPath path, Func<PdfPoint, Point> toDevice, out List<bool> closed)
    {
        var result = new List<List<Point>>(path.Count);
        closed = new List<bool>(path.Count);
        foreach (var subpath in path)
        {
            var contour = new List<Point>();
            var isClosed = false;
            foreach (var command in subpath.Commands)
            {
                switch (command)
                {
                    case PdfSubpath.Move move:
                        if (contour.Count > 0) { result.Add(contour); closed.Add(false); contour = []; }
                        contour.Add(toDevice(move.Location));
                        break;
                    case PdfSubpath.Line line:
                        if (contour.Count == 0) contour.Add(toDevice(line.From));
                        contour.Add(toDevice(line.To));
                        break;
                    case PdfSubpath.CubicBezierCurve cubic:
                        AddCubic(contour, toDevice(cubic.StartPoint), toDevice(cubic.FirstControlPoint),
                            toDevice(cubic.SecondControlPoint), toDevice(cubic.EndPoint));
                        break;
                    case PdfSubpath.QuadraticBezierCurve quad:
                        {
                            // Elevate to cubic.
                            var p0 = toDevice(quad.StartPoint);
                            var q = toDevice(quad.ControlPoint);
                            var p3 = toDevice(quad.EndPoint);
                            var c1 = new Point(p0.X + 2.0 / 3 * (q.X - p0.X), p0.Y + 2.0 / 3 * (q.Y - p0.Y));
                            var c2 = new Point(p3.X + 2.0 / 3 * (q.X - p3.X), p3.Y + 2.0 / 3 * (q.Y - p3.Y));
                            AddCubic(contour, p0, c1, c2, p3);
                            break;
                        }
                    case PdfSubpath.Close:
                        isClosed = true;
                        break;
                }
            }
            result.Add(contour);
            closed.Add(isClosed);
        }
        return result;
    }

    private static void AddCubic(List<Point> contour, Point p0, Point p1, Point p2, Point p3)
    {
        if (contour.Count == 0) contour.Add(p0);
        var length = Distance(p0, p1) + Distance(p1, p2) + Distance(p2, p3);
        var steps = Math.Max(2, Math.Min(64, (int)(length / 3)));
        for (var i = 1; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            contour.Add(new Point(a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y));
        }
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
