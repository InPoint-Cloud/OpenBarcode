using System;
using System.Collections.Generic;
using InPoint.Cloud.OpenBarcode.Imaging;
using ZXing.Common;
using DataMatrixDecoder = ZXing.Datamatrix.Internal.Decoder;

namespace InPoint.Cloud.OpenBarcode.Decoding;

/// <summary>
/// A Data Matrix reader for blurred and noisy symbols that sit roughly level in a crop. ZXing's detector
/// traces the symbol's edges pixel by pixel and loses them in noise or blur. This one finds the symbol's
/// bounding box, tries every standard symbol size and orientation against the finder and timing border,
/// and averages each module's whole centre instead of reading one pixel. ZXing's decoder and its
/// Reed-Solomon error correction then decide whether the read is real.
/// </summary>
internal static class DataMatrixSampler
{
    /// <summary>Standard ECC 200 sizes, rows x columns.</summary>
    private static readonly (int Rows, int Cols)[] Sizes =
    [
        (10, 10), (12, 12), (14, 14), (16, 16), (18, 18), (20, 20), (22, 22), (24, 24), (26, 26),
        (32, 32), (36, 36), (40, 40), (44, 44), (48, 48), (52, 52), (64, 64), (72, 72), (80, 80),
        (88, 88), (96, 96), (104, 104), (120, 120), (132, 132), (144, 144),
        (8, 18), (8, 32), (12, 26), (12, 36), (16, 36), (16, 48),
    ];

    /// <summary>Smallest share of border modules that must match the finder and timing pattern.</summary>
    private const double MinBorderMatch = 0.8;

    /// <summary>Candidates handed to the decoder, best border match first.</summary>
    private const int MaxAttempts = 4;

    /// <summary>A decoded symbol and its corners in the crop.</summary>
    public readonly struct Result(string text, byte[]? rawBytes, BarcodePoint[] corners)
    {
        public string Text { get; } = text;
        public byte[]? RawBytes { get; } = rawBytes;
        public BarcodePoint[] Corners { get; } = corners;
    }

    /// <summary>Tries to read one Data Matrix symbol that fills most of <paramref name="crop"/>.</summary>
    public static Result? Read(GrayImage crop)
    {
        if (crop.Width < 16 || crop.Height < 16) return null;
        var threshold = Otsu(crop.Pixels);
        if (!FindBounds(crop, threshold, out var x0, out var y0, out var x1, out var y1)) return null;
        double width = x1 - x0 + 1, height = y1 - y0 + 1;
        if (width < 8 || height < 8) return null;

        var candidates = new List<(double Score, float[,] Levels)>();
        var cache = new Dictionary<(int, int), float[,]>();
        foreach (var (rows, cols) in Sizes)
        {
            for (var turn = 0; turn < 4; turn++)
            {
                // Grid as it appears in the crop: a quarter turn swaps rows and columns.
                int gridRows = turn % 2 == 0 ? rows : cols, gridCols = turn % 2 == 0 ? cols : rows;
                var aspect = (width / gridCols) / (height / gridRows);
                if (aspect < 0.75 || aspect > 1.33) continue;
                // Modules need at least about 1.5 pixels to be sampled at all.
                if (width / gridCols < 1.5 || height / gridRows < 1.5) continue;
                if (!cache.TryGetValue((gridRows, gridCols), out var grid))
                    cache[(gridRows, gridCols)] = grid = Sample(crop, x0, y0, width, height, gridRows, gridCols);
                var levels = Turn(grid, turn);
                var score = BorderScore(levels, 0.5, out _);
                if (score >= MinBorderMatch) candidates.Add((score, levels));
            }
        }
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var decoder = new DataMatrixDecoder();
        for (var i = 0; i < candidates.Count && i < MaxAttempts; i++)
        {
            // Blur leaks ink between neighbouring modules, so a lone light module among dark ones reads grey.
            // Undo some of that by contrasting each module with its neighbours, and try a few thresholds.
            foreach (var contrast in Contrasts)
            {
                var levels = contrast == 0 ? candidates[i].Levels : Sharpen(candidates[i].Levels, contrast);
                foreach (var split in Splits)
                {
                    if (BorderScore(levels, split, out var modules) < MinBorderMatch) continue;
                    var decoded = Decode(decoder, modules);
                    if (decoded?.Text == null) continue;
                    var corners = new[]
                    {
                        new BarcodePoint(x0, y1 + 1), new BarcodePoint(x0, y0),
                        new BarcodePoint(x1 + 1, y0), new BarcodePoint(x1 + 1, y1 + 1),
                    };
                    return new Result(decoded.Text, decoded.RawBytes, corners);
                }
            }
        }
        return null;
    }

    private static readonly float[] Contrasts = [0, 0.7f, 1.5f];
    private static readonly double[] Splits = [0.5, 0.4, 0.6];

    private static DecoderResult? Decode(DataMatrixDecoder decoder, bool[,] modules)
    {
        int rows = modules.GetLength(0), cols = modules.GetLength(1);
        var bits = new BitMatrix(cols, rows);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                if (modules[r, c]) bits[c, r] = true;
        try
        {
            return decoder.decode(bits);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Pushes each module away from the mean of its four neighbours.</summary>
    private static float[,] Sharpen(float[,] levels, float amount)
    {
        int rows = levels.GetLength(0), cols = levels.GetLength(1);
        var result = new float[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                float sum = 0;
                var n = 0;
                if (r > 0) { sum += levels[r - 1, c]; n++; }
                if (r < rows - 1) { sum += levels[r + 1, c]; n++; }
                if (c > 0) { sum += levels[r, c - 1]; n++; }
                if (c < cols - 1) { sum += levels[r, c + 1]; n++; }
                result[r, c] = levels[r, c] + amount * (levels[r, c] - sum / n);
            }
        return result;
    }

    /// <summary>
    /// Outer edges of the symbol: the outermost rows and columns, counted from the middle, that are at least
    /// a quarter ink. Finder sides are solid and timing sides half ink, while the quiet zone is (almost) blank.
    /// </summary>
    private static bool FindBounds(GrayImage crop, int threshold, out int x0, out int y0, out int x1, out int y1)
    {
        int w = crop.Width, h = crop.Height;
        var px = crop.Pixels;
        var colInk = new int[w];
        var rowInk = new int[h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (px[y * w + x] < threshold) { colInk[x]++; rowInk[y]++; }

        // Find the ink's extent in each direction first, then require the edge line to be substantially inked
        // relative to that extent, so a few specks in the quiet zone do not count.
        x0 = y0 = 0;
        x1 = w - 1;
        y1 = h - 1;
        if (!Extent(rowInk, w, out var ry0, out var ry1) || !Extent(colInk, h, out var cx0, out var cx1)) return false;
        for (var pass = 0; pass < 2; pass++)
        {
            // Recount within the current box so ink outside it does not dilute the fractions.
            var cols = new int[w];
            var rows = new int[h];
            for (var y = ry0; y <= ry1; y++)
                for (var x = cx0; x <= cx1; x++)
                    if (px[y * w + x] < threshold) { cols[x]++; rows[y]++; }
            int boxW = cx1 - cx0 + 1, boxH = ry1 - ry0 + 1;
            if (!Edges(cols, boxH, cx0, cx1, out cx0, out cx1) || !Edges(rows, boxW, ry0, ry1, out ry0, out ry1)) return false;
        }
        x0 = cx0;
        x1 = cx1;
        y0 = ry0;
        y1 = ry1;
        return x1 > x0 && y1 > y0;
    }

    private static bool Extent(int[] ink, int length, out int first, out int last)
    {
        first = -1;
        last = -1;
        var min = Math.Max(1, length / 20);
        for (var i = 0; i < ink.Length; i++)
            if (ink[i] >= min) { if (first < 0) first = i; last = i; }
        return first >= 0 && last > first;
    }

    /// <summary>Moves each end inward while the line is less than a quarter ink.</summary>
    private static bool Edges(int[] ink, int length, int from, int to, out int first, out int last)
    {
        var min = Math.Max(1, length / 4);
        first = from;
        last = to;
        while (first < last && ink[first] < min) first++;
        while (last > first && ink[last] < min) last--;
        return last > first;
    }

    /// <summary>Mean grey value of the middle half of every module of a grid laid over the box.</summary>
    private static float[,] Sample(GrayImage crop, int x0, int y0, double width, double height, int rows, int cols)
    {
        var levels = new float[rows, cols];
        double mw = width / cols, mh = height / rows;
        var w = crop.Width;
        var px = crop.Pixels;
        for (var r = 0; r < rows; r++)
        {
            int ya = (int)Math.Round(y0 + (r + 0.25) * mh), yb = Math.Max(ya, (int)Math.Round(y0 + (r + 0.75) * mh) - 1);
            for (var c = 0; c < cols; c++)
            {
                int xa = (int)Math.Round(x0 + (c + 0.25) * mw), xb = Math.Max(xa, (int)Math.Round(x0 + (c + 0.75) * mw) - 1);
                long sum = 0;
                var n = 0;
                for (var y = ya; y <= yb && y < crop.Height; y++)
                    for (var x = xa; x <= xb && x < w; x++) { sum += px[y * w + x]; n++; }
                levels[r, c] = n == 0 ? 255 : (float)sum / n;
            }
        }
        return levels;
    }

    /// <summary>Rotates a grid clockwise by <paramref name="quarterTurns"/> quarter turns.</summary>
    private static float[,] Turn(float[,] grid, int quarterTurns)
    {
        var result = grid;
        for (var t = 0; t < quarterTurns; t++)
        {
            int rows = result.GetLength(0), cols = result.GetLength(1);
            var next = new float[cols, rows];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                    next[c, rows - 1 - r] = result[r, c];
            result = next;
        }
        return result;
    }

    /// <summary>
    /// How well the border matches an upright symbol: solid left column and bottom row, alternating top row
    /// and right column. The ink threshold is placed between the expected-dark and expected-light border
    /// modules, which also gives the module values for the decoder.
    /// </summary>
    /// <param name="levels">Module grey levels, upright.</param>
    /// <param name="split">Where the threshold sits between the dark and light border means (0.5 = midway).</param>
    /// <param name="modules">Receives the thresholded modules.</param>
    private static double BorderScore(float[,] levels, double split, out bool[,] modules)
    {
        int rows = levels.GetLength(0), cols = levels.GetLength(1);
        double darkSum = 0, lightSum = 0;
        int darkCount = 0, lightCount = 0;
        foreach (var (r, c, dark) in Border(rows, cols))
        {
            if (dark) { darkSum += levels[r, c]; darkCount++; }
            else { lightSum += levels[r, c]; lightCount++; }
        }
        modules = new bool[rows, cols];
        double darkMean = darkSum / darkCount, lightMean = lightSum / lightCount;
        // The finder must be clearly darker than the gaps in the timing pattern.
        if (lightMean - darkMean < 20) return 0;
        var threshold = (float)(darkMean + (lightMean - darkMean) * split);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                modules[r, c] = levels[r, c] < threshold;
        int match = 0, total = 0;
        foreach (var (r, c, dark) in Border(rows, cols))
        {
            total++;
            if (modules[r, c] == dark) match++;
        }
        return (double)match / total;
    }

    /// <summary>The border modules of an upright ECC 200 symbol and whether each is dark.</summary>
    private static IEnumerable<(int Row, int Col, bool Dark)> Border(int rows, int cols)
    {
        for (var r = 0; r < rows; r++) yield return (r, 0, true);                         // left finder
        for (var c = 1; c < cols; c++) yield return (rows - 1, c, true);                  // bottom finder
        for (var c = 1; c < cols; c++) yield return (0, c, c % 2 == 0);                   // top timing
        for (var r = 1; r < rows - 1; r++) yield return (r, cols - 1, (rows - 1 - r) % 2 == 0); // right timing
    }

    private static int Otsu(byte[] pixels)
    {
        var histogram = new long[256];
        foreach (var p in pixels) histogram[p]++;
        long total = pixels.Length;
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)histogram[i];
        double sumB = 0, best = -1;
        long wB = 0;
        var threshold = 128;
        for (var t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)histogram[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t + 1; }
        }
        return threshold;
    }
}
