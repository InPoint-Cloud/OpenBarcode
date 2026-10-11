using System;
using System.Collections.Generic;
using InPoint.Cloud.OpenBarcode.Imaging;

namespace InPoint.Cloud.OpenBarcode.Pdf;

/// <summary>
/// Scanline polygon filler. Samples each pixel at its centre (no anti-aliasing), which keeps bar
/// edges crisp for the barcode detectors. Coordinates are device pixels, y pointing down.
/// </summary>
internal sealed class PolygonRasterizer(GrayImage target)
{
    private const double Epsilon = 1e-4;

    private readonly GrayImage _target = target;

    private struct Edge
    {
        public double X0, Y0, X1, Y1, Slope;
        public int Direction;
    }

    /// <summary>Fills the area enclosed by <paramref name="contours"/> (each implicitly closed) with a gray level.</summary>
    public void Fill(IReadOnlyList<List<Point>> contours, bool evenOdd, byte value)
    {
        var edges = new List<Edge>();
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var contour in contours)
        {
            var n = contour.Count;
            if (n < 2) continue;
            for (var i = 0; i < n; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % n];
                if (a.Y == b.Y) continue;
                var e = a.Y < b.Y
                    ? new Edge { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y, Direction = 1 }
                    : new Edge { X0 = b.X, Y0 = b.Y, X1 = a.X, Y1 = a.Y, Direction = -1 };
                e.Slope = (e.X1 - e.X0) / (e.Y1 - e.Y0);
                edges.Add(e);
                if (e.Y0 < minY) minY = e.Y0;
                if (e.Y1 > maxY) maxY = e.Y1;
            }
        }
        if (edges.Count == 0) return;

        edges.Sort((p, q) => p.Y0.CompareTo(q.Y0));
        int width = _target.Width, height = _target.Height;
        var pixels = _target.Pixels;

        var rowStart = Math.Max(0, (int)Math.Floor(minY));
        var rowEnd = Math.Min(height - 1, (int)Math.Ceiling(maxY));
        var active = new List<int>();
        var crossings = new List<(double X, int Dir)>();
        var next = 0;

        for (var row = rowStart; row <= rowEnd; row++)
        {
            // Edges are half-open in y, covering rows whose centre lies in [Y0, Y1). Sampling a hair below
            // the centre makes shapes that abut on a pixel centre agree on which of them owns that row.
            var sy = row + 0.5 + Epsilon;
            while (next < edges.Count && edges[next].Y0 <= sy) active.Add(next++);
            active.RemoveAll(i => edges[i].Y1 <= sy);
            if (active.Count == 0) continue;

            crossings.Clear();
            foreach (var i in active)
            {
                var e = edges[i];
                if (sy < e.Y0) continue;
                crossings.Add((e.X0 + (sy - e.Y0) * e.Slope, e.Direction));
            }
            crossings.Sort((p, q) => p.X.CompareTo(q.X));

            var winding = 0;
            var offset = row * width;
            for (var c = 0; c < crossings.Count - 1; c++)
            {
                winding += evenOdd ? 1 : crossings[c].Dir;
                var inside = evenOdd ? (winding & 1) != 0 : winding != 0;
                if (!inside) continue;
                // Pixels whose centres lie in [xa, xb), widened by a hair so that shapes which
                // touch exactly on a pixel centre (adjacent bars) leave no gap between them.
                var x0 = (int)Math.Ceiling(crossings[c].X - 0.5 - Epsilon);
                var x1 = (int)Math.Ceiling(crossings[c + 1].X - 0.5 + Epsilon) - 1;
                if (x0 < 0) x0 = 0;
                if (x1 >= width) x1 = width - 1;
                for (var x = x0; x <= x1; x++) pixels[offset + x] = value;
            }
        }
    }

    /// <summary>Draws a straight line of the given width as a filled quadrilateral.</summary>
    public void StrokeSegment(Point a, Point b, double lineWidth, byte value)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        var hw = Math.Max(lineWidth, 1.0) / 2;
        double nx = -dy / len * hw, ny = dx / len * hw;
        var quad = new List<Point>(4)
        {
            new(a.X + nx, a.Y + ny),
            new(b.X + nx, b.Y + ny),
            new(b.X - nx, b.Y - ny),
            new(a.X - nx, a.Y - ny),
        };
        Fill(new[] { quad }, evenOdd: false, value);
    }
}

internal readonly struct Point(double x, double y)
{
    public double X { get; } = x;
    public double Y { get; } = y;
}
