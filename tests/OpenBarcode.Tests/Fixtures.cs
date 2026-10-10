using System.IO.Compression;
using System.Text;
using BitMiracle.LibTiff.Classic;
using InPoint.Cloud.OpenBarcode.Imaging;
using StbImageWriteSharp;
using ZXing;
using ZXing.Common;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

/// <summary>Generates barcode bitmaps and encodes them into the file formats the reader accepts.</summary>
internal static class Fixtures
{
    /// <summary>Renders a barcode with ZXing's writer; true = black module.</summary>
    public static BitMatrix Matrix(ZxFormat format, string text, int width = 0, int height = 0, int margin = 0)
    {
        var writer = new BarcodeWriterGeneric
        {
            Format = format,
            Options = new EncodingOptions { Width = width, Height = height, Margin = margin, PureBarcode = true },
        };
        return writer.Encode(text);
    }

    /// <summary>Renders a barcode to a grayscale bitmap, <paramref name="moduleSize"/> pixels per module, with a white margin.</summary>
    public static GrayImage Image(ZxFormat format, string text, int moduleSize = 4, int quietModules = 10, int barHeight = 80)
    {
        var m = Matrix(format, text);
        bool linear = m.Height == 1;
        int rows = linear ? barHeight / moduleSize : m.Height;
        int w = (m.Width + 2 * quietModules) * moduleSize;
        int h = (rows + 2 * quietModules) * moduleSize;
        var image = new GrayImage(w, h);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < m.Width; x++)
                if (m[x, linear ? 0 : y])
                    FillRect(image, (x + quietModules) * moduleSize, (y + quietModules) * moduleSize, moduleSize, moduleSize, 0);
        return image;
    }

    public static void FillRect(GrayImage image, int x0, int y0, int w, int h, byte value)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                image.Pixels[y * image.Width + x] = value;
    }

    /// <summary>Copies <paramref name="src"/> into <paramref name="dst"/> at an offset.</summary>
    public static void Blit(GrayImage dst, GrayImage src, int ox, int oy)
    {
        for (int y = 0; y < src.Height; y++)
            Buffer.BlockCopy(src.Pixels, y * src.Width, dst.Pixels, (oy + y) * dst.Width + ox, src.Width);
    }

    public static GrayImage Rotate90(GrayImage src)
    {
        var dst = new GrayImage(src.Height, src.Width);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                dst.Pixels[x * dst.Width + (src.Height - 1 - y)] = src.Pixels[y * src.Width + x];
        return dst;
    }

    public static GrayImage Invert(GrayImage src)
    {
        var dst = new GrayImage(src.Width, src.Height);
        for (int i = 0; i < src.Pixels.Length; i++) dst.Pixels[i] = (byte)(255 - src.Pixels[i]);
        return dst;
    }

    public static byte[] Png(GrayImage image) => Encode(image, (w, d, s) => w.WritePng(d, image.Width, image.Height, ColorComponents.Grey, s));
    public static byte[] Bmp(GrayImage image) => Encode(image, (w, d, s) => w.WriteBmp(d, image.Width, image.Height, ColorComponents.Grey, s));
    public static byte[] Tga(GrayImage image) => Encode(image, (w, d, s) => w.WriteTga(d, image.Width, image.Height, ColorComponents.Grey, s));
    public static byte[] Jpeg(GrayImage image, int quality = 90) =>
        Encode(image, (w, d, s) => w.WriteJpg(d, image.Width, image.Height, ColorComponents.Grey, s, quality));

    /// <summary>Black ink on a transparent background: RGBA where white pixels have alpha 0.</summary>
    public static byte[] TransparentPng(GrayImage image)
    {
        var rgba = new byte[image.Pixels.Length * 4];
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            bool ink = image.Pixels[i] < 128;
            rgba[i * 4 + 3] = ink ? (byte)255 : (byte)0;
            // Transparent pixels are black too, so ignoring alpha would lose the code.
        }
        using var s = new MemoryStream();
        new ImageWriter().WritePng(rgba, image.Width, image.Height, ColorComponents.RedGreenBlueAlpha, s);
        return s.ToArray();
    }

    private static byte[] Encode(GrayImage image, Action<ImageWriter, byte[], Stream> write)
    {
        using var s = new MemoryStream();
        write(new ImageWriter(), image.Pixels, s);
        return s.ToArray();
    }

    public static byte[] Pgm(GrayImage image)
    {
        var header = Encoding.ASCII.GetBytes($"P5\n# test\n{image.Width} {image.Height}\n255\n");
        return header.Concat(image.Pixels).ToArray();
    }

    public static byte[] Pbm(GrayImage image)
    {
        var header = Encoding.ASCII.GetBytes($"P4\n{image.Width} {image.Height}\n");
        return header.Concat(PackBits(image, blackIsOne: true)).ToArray();
    }

    /// <summary>1 bit per pixel, MSB first, rows padded to a byte.</summary>
    public static byte[] PackBits(GrayImage image, bool blackIsOne)
    {
        int rowBytes = (image.Width + 7) / 8;
        var bits = new byte[rowBytes * image.Height];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                bool black = image.Pixels[y * image.Width + x] < 128;
                if (black == blackIsOne) bits[y * rowBytes + x / 8] |= (byte)(0x80 >> (x & 7));
            }
        return bits;
    }

    /// <summary>Writes a TIFF with one page per image: 8-bit LZW, or 1-bit CCITT Group 4 when <paramref name="bilevel"/>.</summary>
    public static byte[] Tiff(bool bilevel, params GrayImage[] pages)
    {
        var ms = new MemoryStream();
        using (var tiff = BitMiracle.LibTiff.Classic.Tiff.ClientOpen("t.tif", "w", ms, new NonClosingTiffStream()))
        {
            foreach (var page in pages)
            {
                tiff.SetField(TiffTag.IMAGEWIDTH, page.Width);
                tiff.SetField(TiffTag.IMAGELENGTH, page.Height);
                tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                tiff.SetField(TiffTag.ROWSPERSTRIP, page.Height);
                if (bilevel)
                {
                    tiff.SetField(TiffTag.BITSPERSAMPLE, 1);
                    tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
                    tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
                    var bits = PackBits(page, blackIsOne: true);
                    int rowBytes = (page.Width + 7) / 8;
                    for (int y = 0; y < page.Height; y++)
                        tiff.WriteScanline(bits.AsSpan(y * rowBytes, rowBytes).ToArray(), y);
                }
                else
                {
                    tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
                    tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
                    tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
                    for (int y = 0; y < page.Height; y++)
                        tiff.WriteScanline(page.Pixels.AsSpan(y * page.Width, page.Width).ToArray(), y);
                }
                tiff.WriteDirectory();
            }
        }
        return ms.ToArray();
    }

    /// <summary>Raw CCITT Group 4 data for a bilevel image (black = 1 in the coded runs), as a PDF would embed it.</summary>
    public static byte[] CcittG4(GrayImage image)
    {
        var tiffBytes = Tiff(true, image);
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.ClientOpen("t.tif", "r", new MemoryStream(tiffBytes), new TiffStream());
        int size = (int)tiff.RawStripSize(0);
        var raw = new byte[size];
        tiff.ReadRawStrip(0, raw, 0, size);
        return raw;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private sealed class NonClosingTiffStream : TiffStream
    {
        public override void Close(object clientData) { }
    }
}
