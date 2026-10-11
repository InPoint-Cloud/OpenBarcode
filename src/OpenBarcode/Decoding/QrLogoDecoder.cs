using System;
using System.Collections.Generic;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;
using QrVersion = ZXing.QrCode.Internal.Version;

namespace InPoint.Cloud.OpenBarcode.Decoding;

/// <summary>
/// Reads QR codes with a logo printed over the middle. ZXing treats the covered modules as ordinary errors,
/// each costing two check codewords, so a logo plus a little blur or noise exceeds what the code can
/// correct. Declaring the covered codewords unknown (erasures) halves their cost. Logos sit in the centre
/// almost always, so centred squares of increasing size are tried as the covered area.
/// </summary>
internal static class QrLogoDecoder
{
    /// <summary>Side of the assumed logo area as a share of the symbol's side.</summary>
    private static readonly double[] LogoSides = [0.16, 0.22, 0.28, 0.34, 0.40, 0.46];

    /// <summary>
    /// Check codewords that must be left over across all blocks after correction. Every spare one makes a
    /// mis-correction of an unreadable symbol about 256 times less likely.
    /// </summary>
    private const int MinTotalSpare = 8;

    private const int FormatMask = 0x5412;

    /// <summary>
    /// Wrong format bits tolerated. The format code only corrects three, but error correction of the data
    /// rejects a wrong guess anyway.
    /// </summary>
    private const int MaxFormatDistance = 5;

    /// <summary>Decodes a sampled module grid, as ZXing's QR detector produces it.</summary>
    /// <param name="grid">The modules, one bit each.</param>
    /// <param name="hints">ZXing decoding hints, such as the character set.</param>
    /// <returns>The decoded symbol, or null.</returns>
    public static DecoderResult? Decode(BitMatrix grid, IDictionary<DecodeHintType, object>? hints)
    {
        var dimension = grid.Height;
        if (grid.Width != dimension || dimension < 21 || dimension > 177 || (dimension - 17) % 4 != 0) return null;
        var version = QrVersion.getVersionForNumber((dimension - 17) / 4);
        var function = FunctionPattern(version, dimension);

        // Where each module's bit goes: raw codeword index and bit (7 = most significant), in reading order.
        var moduleCodeword = new int[dimension, dimension];
        var moduleBit = new int[dimension, dimension];
        var total = version.TotalCodewords;
        var bitsRead = 0;
        var up = true;
        for (var j = dimension - 1; j > 0; j -= 2)
        {
            if (j == 6) j--;
            for (var count = 0; count < dimension; count++)
            {
                var y = up ? dimension - 1 - count : count;
                for (var col = 0; col < 2; col++)
                {
                    var x = j - col;
                    moduleCodeword[x, y] = -1;
                    if (function[x, y]) continue;
                    if (bitsRead / 8 < total)
                    {
                        moduleCodeword[x, y] = bitsRead / 8;
                        moduleBit[x, y] = 7 - bitsRead % 8;
                    }
                    bitsRead++;
                }
            }
            up = !up;
        }

        // Blur near the finder patterns can garble the format information beyond what its own code corrects.
        // A wrong level or mask leaves the codewords inconsistent, which error correction rejects, so the
        // nearest few candidates are all worth a try.
        foreach (var format in FormatCandidates(grid))
        {
            var result = Decode(grid, version, moduleCodeword, moduleBit, ErrorCorrectionLevel.forBits(format >> 3), format & 7, hints);
            if (result != null) return result;
        }
        return null;
    }

    private static DecoderResult? Decode(BitMatrix grid, QrVersion version, int[,] moduleCodeword, int[,] moduleBit, ErrorCorrectionLevel level, int mask,
        IDictionary<DecodeHintType, object>? hints)
    {
        int dimension = grid.Height, total = version.TotalCodewords;
        var raw = new int[total];
        for (var y = 0; y < dimension; y++)
            for (var x = 0; x < dimension; x++)
                if (moduleCodeword[x, y] >= 0 && grid[x, y] ^ Masked(mask, y, x))
                    raw[moduleCodeword[x, y]] |= 1 << moduleBit[x, y];

        // De-interleave into blocks exactly as ZXing's DataBlock does: data codewords round-robin (longer blocks
        // get one extra at the end), then check codewords round-robin.
        var ecBlocks = version.getECBlocksForLevel(level);
        var checkCount = ecBlocks.ECCodewordsPerBlock;
        var blockSizes = new List<int>();
        foreach (var ecb in ecBlocks.getECBlocks())
            for (var i = 0; i < ecb.Count; i++) blockSizes.Add(ecb.DataCodewords + checkCount);
        var blocks = blockSizes.Count;
        var place = new (int Block, int Position)[total];
        int shortData = blockSizes[0] - checkCount, offset = 0;
        for (var i = 0; i < shortData; i++)
            for (var b = 0; b < blocks; b++) place[offset++] = (b, i);
        for (var b = 0; b < blocks; b++)
            if (blockSizes[b] - checkCount > shortData) place[offset++] = (b, shortData);
        for (var i = 0; i < checkCount; i++)
            for (var b = 0; b < blocks; b++) place[offset++] = (b, blockSizes[b] - checkCount + i);
        if (offset != total) return null;

        foreach (var side in LogoSides)
        {
            double lo = dimension * (1 - side) / 2, hi = dimension * (1 + side) / 2;
            var erased = new bool[total];
            for (var y = (int)lo; y < Math.Ceiling(hi); y++)
                for (var x = (int)lo; x < Math.Ceiling(hi); x++)
                    if (moduleCodeword[x, y] >= 0) erased[moduleCodeword[x, y]] = true;

            var blockWords = new int[blocks][];
            var blockErasures = new List<int>[blocks];
            for (var b = 0; b < blocks; b++)
            {
                blockWords[b] = new int[blockSizes[b]];
                blockErasures[b] = [];
            }
            for (var i = 0; i < total; i++)
            {
                var (b, p) = place[i];
                blockWords[b][p] = raw[i];
                if (erased[i]) blockErasures[b].Add(p);
            }

            var ok = true;
            var totalSpare = 0;
            for (var b = 0; b < blocks && ok; b++)
            {
                // Leave a couple of check codewords per block to catch a wrong correction.
                var spare = 0;
                ok = blockErasures[b].Count <= checkCount - 2
                    && ReedSolomonErasures.Decode(blockWords[b], checkCount, [.. blockErasures[b]], out spare)
                    && spare >= 1;
                totalSpare += spare;
            }
            if (!ok || totalSpare < MinTotalSpare) continue;

            // Write the corrected codewords back into the grid and let ZXing's decoder, which now finds no
            // errors, parse the contents.
            var repaired = (BitMatrix)grid.Clone();
            for (var y = 0; y < dimension; y++)
                for (var x = 0; x < dimension; x++)
                {
                    var index = moduleCodeword[x, y];
                    if (index < 0) continue;
                    var (b, p) = place[index];
                    var bit = ((blockWords[b][p] >> moduleBit[x, y]) & 1) != 0;
                    repaired[x, y] = bit ^ Masked(mask, y, x);
                }
            WriteFormat(repaired, (level.Bits << 3) | mask);
            try
            {
                var result = new Decoder().decode(repaired, hints);
                if (result?.Text != null) return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                // Not a valid bit stream after all.
            }
            // The codewords were consistent, so a larger area would only find the same ones.
            return null;
        }
        return null;
    }

    /// <summary>
    /// Five-bit format values (level bits, then mask) whose code lies within <see cref="MaxFormatDistance"/>
    /// bits of either copy of the format information, nearest first.
    /// </summary>
    private static List<int> FormatCandidates(BitMatrix grid)
    {
        var dimension = grid.Height;
        int first = 0, second = 0;
        for (var i = 0; i < 6; i++) first = Bit(grid, i, 8, first);
        first = Bit(grid, 7, 8, first);
        first = Bit(grid, 8, 8, first);
        first = Bit(grid, 8, 7, first);
        for (var j = 5; j >= 0; j--) first = Bit(grid, 8, j, first);
        for (var j = dimension - 1; j >= dimension - 7; j--) second = Bit(grid, 8, j, second);
        for (var i = dimension - 8; i < dimension; i++) second = Bit(grid, i, 8, second);

        var candidates = new List<(int Distance, int Data)>();
        for (var data = 0; data < 32; data++)
        {
            var code = Encode(data) ^ FormatMask;
            var distance = Math.Min(Popcount(first ^ code), Popcount(second ^ code));
            if (distance <= MaxFormatDistance) candidates.Add((distance, data));
        }
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return candidates.ConvertAll(c => c.Data);
    }

    /// <summary>Writes the format information (both copies) so ZXing's decoder reads the level and mask used here.</summary>
    private static void WriteFormat(BitMatrix grid, int data)
    {
        int code = Encode(data) ^ FormatMask, dimension = grid.Height, bit = 14;
        void Set(int x, int y) => grid[x, y] = ((code >> bit--) & 1) != 0;
        for (var i = 0; i < 6; i++) Set(i, 8);
        Set(7, 8);
        Set(8, 8);
        Set(8, 7);
        for (var j = 5; j >= 0; j--) Set(8, j);
        bit = 14;
        for (var j = dimension - 1; j >= dimension - 7; j--) Set(8, j);
        for (var i = dimension - 8; i < dimension; i++) Set(i, 8);
    }

    private static int Bit(BitMatrix grid, int x, int y, int bits) => grid[x, y] ? (bits << 1) | 1 : bits << 1;

    /// <summary>BCH(15, 5) code of the five format bits.</summary>
    private static int Encode(int data)
    {
        var value = data << 10;
        for (var bit = 14; bit >= 10; bit--)
            if ((value >> bit & 1) != 0) value ^= 0x537 << (bit - 10);
        return (data << 10) | value;
    }

    private static int Popcount(int v)
    {
        var count = 0;
        for (; v != 0; v &= v - 1) count++;
        return count;
    }

    /// <summary>The QR data masks, for module row <paramref name="i"/> and column <paramref name="j"/>.</summary>
    private static bool Masked(int mask, int i, int j)
    {
        return mask switch
        {
            0 => ((i + j) & 1) == 0,
            1 => (i & 1) == 0,
            2 => j % 3 == 0,
            3 => (i + j) % 3 == 0,
            4 => (((i / 2) + (j / 3)) & 1) == 0,
            5 => ((i * j) & 1) + (i * j) % 3 == 0,
            6 => ((((i * j) & 1) + (i * j) % 3) & 1) == 0,
            _ => ((((i + j) & 1) + (i * j) % 3) & 1) == 0,
        };
    }

    /// <summary>Finder, timing, alignment, format and version modules, which carry no data.</summary>
    private static bool[,] FunctionPattern(QrVersion version, int dimension)
    {
        var function = new bool[dimension, dimension];
        void Region(int left, int top, int width, int height)
        {
            for (var y = top; y < top + height; y++)
                for (var x = left; x < left + width; x++) function[x, y] = true;
        }
        Region(0, 0, 9, 9);
        Region(dimension - 8, 0, 8, 9);
        Region(0, dimension - 8, 9, 8);
        var centres = version.AlignmentPatternCenters;
        var max = centres.Length;
        for (var x = 0; x < max; x++)
            for (var y = 0; y < max; y++)
            {
                if ((x == 0 && y == 0) || (x == 0 && y == max - 1) || (x == max - 1 && y == 0)) continue;
                Region(centres[y] - 2, centres[x] - 2, 5, 5);
            }
        Region(6, 9, 1, dimension - 17);
        Region(9, 6, dimension - 17, 1);
        if (version.VersionNumber > 6)
        {
            Region(dimension - 11, 0, 3, 6);
            Region(0, dimension - 11, 6, 3);
        }
        return function;
    }
}
