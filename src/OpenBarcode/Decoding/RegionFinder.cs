using System;
using System.Collections.Generic;
using InPoint.Cloud.OpenBarcode.Imaging;

namespace InPoint.Cloud.OpenBarcode.Decoding;

internal readonly struct Region(int x, int y, int width, int height)
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public int Width { get; } = width;
    public int Height { get; } = height;

    public bool Contains(float px, float py) => px >= X && py >= Y && px < X + Width && py < Y + Height;
}

/// <summary>
/// Proposes compact blobs of ink that could be a 2D symbol. The Data Matrix and Aztec detectors search
/// outward from the centre of the image, so on a page with several codes they only see one; cropping to
/// each blob gives every symbol a turn at the centre.
/// </summary>
internal static class RegionFinder
{
    public static List<Region> Find(GrayImage image, int maxRegions)
    {
        var cell = Math.Max(4, Math.Max(image.Width, image.Height) / 256);
        var grid = new Grid(image.Width, image.Height, cell);
        var grown = Dilate(grid, MarkDarkCells(image, grid));

        var regions = new List<(Region Region, int Area)>();
        var visited = new bool[grown.Length];
        var queue = new Queue<int>();
        for (var start = 0; start < grown.Length; start++)
        {
            if (!grown[start] || visited[start]) continue;
            var (minX, minY, maxX, maxY) = FloodFill(grid, grown, visited, queue, start);
            if (ToRegion(grid, minX, minY, maxX, maxY) is Region region)
                regions.Add((region, region.Width * region.Height));
        }

        regions.Sort((a, b) => b.Area.CompareTo(a.Area));
        var result = new List<Region>(Math.Min(maxRegions, regions.Count));
        for (var i = 0; i < regions.Count && i < maxRegions; i++) result.Add(regions[i].Region);
        return result;
    }

    /// <summary>The image divided into square cells of <see cref="Cell"/> pixels.</summary>
    private readonly struct Grid(int width, int height, int cell)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Cell { get; } = cell;
        public int Columns { get; } = (width + cell - 1) / cell;
        public int Rows { get; } = (height + cell - 1) / cell;

        public bool Contains(int gx, int gy) => gx >= 0 && gy >= 0 && gx < Columns && gy < Rows;
    }

    /// <summary>Marks cells with a meaningful share of dark pixels.</summary>
    private static bool[] MarkDarkCells(GrayImage image, Grid grid)
    {
        int w = grid.Width, h = grid.Height, cell = grid.Cell, gw = grid.Columns, gh = grid.Rows;
        var threshold = ImageFilters.OtsuThreshold(image.Pixels);
        var dark = new int[gw * gh];
        var pixels = image.Pixels;
        for (var y = 0; y < h; y++)
        {
            int row = y * w, grow = (y / cell) * gw;
            for (var x = 0; x < w; x++)
                if (pixels[row + x] < threshold) dark[grow + x / cell]++;
        }
        var marked = new bool[gw * gh];
        for (var gy = 0; gy < gh; gy++)
            for (var gx = 0; gx < gw; gx++)
            {
                int cw = Math.Min(cell, w - gx * cell), ch = Math.Min(cell, h - gy * cell);
                marked[gy * gw + gx] = dark[gy * gw + gx] * 10 >= cw * ch;
            }
        return marked;
    }

    /// <summary>Dilates by one cell so the light modules inside a symbol do not split it apart.</summary>
    private static bool[] Dilate(Grid grid, bool[] marked)
    {
        var grown = new bool[marked.Length];
        for (var i = 0; i < marked.Length; i++)
        {
            if (!marked[i]) continue;
            int gx = i % grid.Columns, gy = i / grid.Columns;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (grid.Contains(gx + dx, gy + dy)) grown[(gy + dy) * grid.Columns + gx + dx] = true;
        }
        return grown;
    }

    /// <summary>Visits the blob of grown cells around <paramref name="start"/> and returns its bounds in cells.</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) FloodFill(Grid grid, bool[] grown, bool[] visited, Queue<int> queue, int start)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        visited[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int gx = i % grid.Columns, gy = i / grid.Columns;
            minX = Math.Min(minX, gx);
            maxX = Math.Max(maxX, gx);
            minY = Math.Min(minY, gy);
            maxY = Math.Max(maxY, gy);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    Visit(grid, grown, visited, queue, gx + dx, gy + dy);
        }
        return (minX, minY, maxX, maxY);
    }

    private static void Visit(Grid grid, bool[] grown, bool[] visited, Queue<int> queue, int gx, int gy)
    {
        if (!grid.Contains(gx, gy)) return;
        var n = gy * grid.Columns + gx;
        if (!grown[n] || visited[n]) return;
        visited[n] = true;
        queue.Enqueue(n);
    }

    /// <summary>Undoes the dilation and converts a blob's cell bounds to pixels; null when the blob cannot be a symbol.</summary>
    private static Region? ToRegion(Grid grid, int minX, int minY, int maxX, int maxY)
    {
        int w = grid.Width, h = grid.Height, cell = grid.Cell;
        int cx0 = Math.Min(minX + 1, maxX), cy0 = Math.Min(minY + 1, maxY);
        int cx1 = Math.Max(maxX - 1, cx0), cy1 = Math.Max(maxY - 1, cy0);
        int px = cx0 * cell, py = cy0 * cell;
        int pw = Math.Min(w, (cx1 + 1) * cell) - px, ph = Math.Min(h, (cy1 + 1) * cell) - py;
        if (pw < 16 || ph < 16) return null;
        var aspect = (double)Math.Max(pw, ph) / Math.Min(pw, ph);
        if (aspect > 6) return null;
        if ((long)pw * ph > (long)w * h * 9 / 10) return null;
        return new Region(px, py, pw, ph);
    }

    /// <summary>Returns the pixels of <paramref name="region"/> grown by <paramref name="margin"/>, clamped to the image.</summary>
    public static (GrayImage Image, int OffsetX, int OffsetY) Crop(GrayImage image, Region region, int margin)
    {
        int x0 = Math.Max(0, region.X - margin), y0 = Math.Max(0, region.Y - margin);
        var x1 = Math.Min(image.Width, region.X + region.Width + margin);
        var y1 = Math.Min(image.Height, region.Y + region.Height + margin);
        int cw = x1 - x0, ch = y1 - y0;
        var dst = new byte[cw * ch];
        for (var y = 0; y < ch; y++)
            Buffer.BlockCopy(image.Pixels, (y0 + y) * image.Width + x0, dst, y * cw, cw);
        return (new GrayImage(cw, ch, dst), x0, y0);
    }
}
