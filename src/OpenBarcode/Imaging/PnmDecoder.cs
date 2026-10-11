using System;
using System.IO;

namespace InPoint.Cloud.OpenBarcode.Imaging;

/// <summary>Netpbm decoder: P1/P4 (bitmap), P2/P5 (graymap), P3/P6 (pixmap).</summary>
internal static class PnmDecoder
{
    public static GrayImage Decode(byte[] data)
    {
        var pos = 2;
        var kind = (char)data[1];
        var width = ReadInt(data, ref pos);
        var height = ReadInt(data, ref pos);
        var isBitmap = kind == '1' || kind == '4';
        var max = isBitmap ? 1 : ReadInt(data, ref pos);
        if (width <= 0 || height <= 0 || max <= 0 || max > 65535) throw new InvalidDataException("Invalid PNM header.");
        pos++; // single whitespace after the header

        var gray = new byte[checked(width * height)];
        try
        {
            ReadPixels(data, pos, kind, width, height, max, gray);
        }
        catch (IndexOutOfRangeException)
        {
            throw new InvalidDataException("Truncated PNM image.");
        }
        return new GrayImage(width, height, gray);
    }

    private static void ReadPixels(byte[] data, int pos, char kind, int width, int height, int max, byte[] gray)
    {
        switch (kind)
        {
            case '1':
                for (var i = 0; i < gray.Length; i++) gray[i] = ReadBit(data, ref pos) ? (byte)0 : (byte)255;
                break;
            case '4':
                ReadPackedBits(data, pos, width, height, gray);
                break;
            case '2':
            case '5':
                for (var i = 0; i < gray.Length; i++) gray[i] = Scale(Sample(data, ref pos, kind, max), max);
                break;
            default:
                for (var i = 0; i < gray.Length; i++)
                {
                    int r = Scale(Sample(data, ref pos, kind, max), max);
                    int g = Scale(Sample(data, ref pos, kind, max), max);
                    int b = Scale(Sample(data, ref pos, kind, max), max);
                    gray[i] = GrayImage.Luma(r, g, b);
                }
                break;
        }
    }

    /// <summary>P4: one bit per pixel, rows padded to whole bytes, 1 = black.</summary>
    private static void ReadPackedBits(byte[] data, int pos, int width, int height, byte[] gray)
    {
        var rowBytes = (width + 7) / 8;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                gray[y * width + x] = (data[pos + y * rowBytes + x / 8] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
    }

    /// <summary>One sample: a decimal number in the plain formats, one or (above 255) two bytes in the raw ones.</summary>
    private static int Sample(byte[] data, ref int p, char kind, int max)
    {
        if (kind <= '3') return ReadInt(data, ref p);
        if (max > 255) { var v = (data[p] << 8) | data[p + 1]; p += 2; return v; }
        return data[p++];
    }

    private static byte Scale(int v, int max) => (byte)Math.Min(255, v * 255 / max);

    private static void SkipSpaceAndComments(byte[] d, ref int p)
    {
        while (p < d.Length)
        {
            if (d[p] == '#') { while (p < d.Length && d[p] != '\n') p++; }
            else if (d[p] == ' ' || d[p] == '\t' || d[p] == '\r' || d[p] == '\n') p++;
            else break;
        }
    }

    private static int ReadInt(byte[] d, ref int p)
    {
        SkipSpaceAndComments(d, ref p);
        int v = 0, start = p;
        while (p < d.Length && d[p] >= '0' && d[p] <= '9') v = checked(v * 10 + (d[p++] - '0'));
        if (p == start) throw new InvalidDataException("Invalid PNM number.");
        return v;
    }

    private static bool ReadBit(byte[] d, ref int p)
    {
        SkipSpaceAndComments(d, ref p);
        if (p >= d.Length) throw new InvalidDataException("Truncated PNM image.");
        return d[p++] == '1';
    }
}
