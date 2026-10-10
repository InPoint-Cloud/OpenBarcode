using System;
using System.IO;

namespace InPoint.Cloud.OpenBarcode.Imaging
{
    /// <summary>Netpbm decoder: P1/P4 (bitmap), P2/P5 (graymap), P3/P6 (pixmap).</summary>
    internal static class PnmDecoder
    {
        public static GrayImage Decode(byte[] data)
        {
            int pos = 2;
            char kind = (char)data[1];
            int width = ReadInt(data, ref pos);
            int height = ReadInt(data, ref pos);
            bool isBitmap = kind == '1' || kind == '4';
            int max = isBitmap ? 1 : ReadInt(data, ref pos);
            if (width <= 0 || height <= 0 || max <= 0 || max > 65535) throw new InvalidDataException("Invalid PNM header.");
            pos++; // single whitespace after the header

            var gray = new byte[checked(width * height)];
            bool wide = max > 255;
            int Sample(ref int p)
            {
                if (kind <= '3') return ReadInt(data, ref p);
                if (wide) { int v = (data[p] << 8) | data[p + 1]; p += 2; return v; }
                return data[p++];
            }
            byte Scale(int v) => (byte)Math.Min(255, v * 255 / max);

            try
            {
                switch (kind)
                {
                    case '1':
                        for (int i = 0; i < gray.Length; i++) gray[i] = ReadBit(data, ref pos) ? (byte)0 : (byte)255;
                        break;
                    case '4':
                        int rowBytes = (width + 7) / 8;
                        for (int y = 0; y < height; y++)
                            for (int x = 0; x < width; x++)
                                gray[y * width + x] = (data[pos + y * rowBytes + x / 8] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
                        break;
                    case '2':
                    case '5':
                        for (int i = 0; i < gray.Length; i++) gray[i] = Scale(Sample(ref pos));
                        break;
                    default:
                        for (int i = 0; i < gray.Length; i++)
                        {
                            int r = Scale(Sample(ref pos)), g = Scale(Sample(ref pos)), b = Scale(Sample(ref pos));
                            gray[i] = GrayImage.Luma(r, g, b);
                        }
                        break;
                }
            }
            catch (IndexOutOfRangeException)
            {
                throw new InvalidDataException("Truncated PNM image.");
            }
            return new GrayImage(width, height, gray);
        }

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
}
