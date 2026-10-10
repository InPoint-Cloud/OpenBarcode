using System;
using System.Collections.Generic;
using OpenBarcode.Imaging;

namespace OpenBarcode.Decoding
{
    internal readonly struct Region
    {
        public Region(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
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
            int w = image.Width, h = image.Height;
            int cell = Math.Max(4, Math.Max(w, h) / 256);
            int gw = (w + cell - 1) / cell, gh = (h + cell - 1) / cell;
            byte threshold = OtsuThreshold(image.Pixels);

            // Mark cells with a meaningful share of dark pixels.
            var dark = new int[gw * gh];
            var pixels = image.Pixels;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, grow = (y / cell) * gw;
                for (int x = 0; x < w; x++)
                    if (pixels[row + x] < threshold) dark[grow + x / cell]++;
            }
            var marked = new bool[gw * gh];
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    int cw = Math.Min(cell, w - gx * cell), ch = Math.Min(cell, h - gy * cell);
                    marked[gy * gw + gx] = dark[gy * gw + gx] * 10 >= cw * ch;
                }

            // Dilate by one cell so the light modules inside a symbol do not split it apart.
            var grown = new bool[gw * gh];
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    if (!marked[gy * gw + gx]) continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = gx + dx, ny = gy + dy;
                            if (nx >= 0 && ny >= 0 && nx < gw && ny < gh) grown[ny * gw + nx] = true;
                        }
                }

            var regions = new List<(Region Region, int Area)>();
            var visited = new bool[gw * gh];
            var queue = new Queue<int>();
            for (int start = 0; start < grown.Length; start++)
            {
                if (!grown[start] || visited[start]) continue;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    int gx = i % gw, gy = i / gw;
                    if (gx < minX) minX = gx;
                    if (gx > maxX) maxX = gx;
                    if (gy < minY) minY = gy;
                    if (gy > maxY) maxY = gy;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = gx + dx, ny = gy + dy;
                            if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                            int n = ny * gw + nx;
                            if (grown[n] && !visited[n]) { visited[n] = true; queue.Enqueue(n); }
                        }
                }

                // Undo the dilation and convert to pixels.
                int cx0 = Math.Min(minX + 1, maxX), cy0 = Math.Min(minY + 1, maxY);
                int cx1 = Math.Max(maxX - 1, cx0), cy1 = Math.Max(maxY - 1, cy0);
                int px = cx0 * cell, py = cy0 * cell;
                int pw = Math.Min(w, (cx1 + 1) * cell) - px, ph = Math.Min(h, (cy1 + 1) * cell) - py;
                if (pw < 16 || ph < 16) continue;
                double aspect = (double)Math.Max(pw, ph) / Math.Min(pw, ph);
                if (aspect > 6) continue;
                if ((long)pw * ph > (long)w * h * 9 / 10) continue;
                regions.Add((new Region(px, py, pw, ph), pw * ph));
            }

            regions.Sort((a, b) => b.Area.CompareTo(a.Area));
            var result = new List<Region>(Math.Min(maxRegions, regions.Count));
            for (int i = 0; i < regions.Count && i < maxRegions; i++) result.Add(regions[i].Region);
            return result;
        }

        private static byte OtsuThreshold(byte[] pixels)
        {
            var histogram = new long[256];
            foreach (var p in pixels) histogram[p]++;
            long total = pixels.Length;
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += i * (double)histogram[i];
            double sumB = 0, best = -1;
            long wB = 0;
            int threshold = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += histogram[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += t * (double)histogram[t];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; threshold = t + 1; }
            }
            return (byte)Math.Max(1, Math.Min(255, threshold));
        }

        /// <summary>Returns the pixels of <paramref name="region"/> grown by <paramref name="margin"/>, clamped to the image.</summary>
        public static (GrayImage Image, int OffsetX, int OffsetY) Crop(GrayImage image, Region region, int margin)
        {
            int x0 = Math.Max(0, region.X - margin), y0 = Math.Max(0, region.Y - margin);
            int x1 = Math.Min(image.Width, region.X + region.Width + margin);
            int y1 = Math.Min(image.Height, region.Y + region.Height + margin);
            int cw = x1 - x0, ch = y1 - y0;
            var dst = new byte[cw * ch];
            for (int y = 0; y < ch; y++)
                Buffer.BlockCopy(image.Pixels, (y0 + y) * image.Width + x0, dst, y * cw, cw);
            return (new GrayImage(cw, ch, dst), x0, y0);
        }
    }
}
