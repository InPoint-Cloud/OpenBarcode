using System;
using System.Collections.Generic;
using System.Linq;
using InPoint.Cloud.OpenBarcode.Imaging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace InPoint.Cloud.OpenBarcode.Pdf;

/// <summary>Turns an image embedded in a PDF into a grayscale bitmap.</summary>
internal static class PdfImageDecoder
{
    /// <summary>Decodes the image, or returns null with a reason when its encoding is not supported or it is too large.</summary>
    public static GrayImage? Decode(IPdfImage image, long maxPixels, out string? skipReason)
    {
        skipReason = null;
        if ((long)image.WidthInSamples * image.HeightInSamples > maxPixels)
        {
            skipReason = $"{image.WidthInSamples}×{image.HeightInSamples} pixels, more than the limit of {maxPixels:N0} (MaxImagePixels)";
            return null;
        }
        var filters = GetFilterNames(image.ImageDictionary);

        if (filters.Contains("JPXDecode"))
        {
            skipReason = "JPEG 2000-compressed image";
            return null;
        }

        // PdfPig's PNG conversion applies the /Decode array itself; the other paths read raw samples.
        GrayImage? result;
        var applyDecode = true;
        if (filters.Count > 0 && filters[filters.Count - 1] == "DCTDecode")
        {
            if (filters.Count > 1)
            {
                skipReason = "JPEG image with additional filters";
                return null;
            }
            result = ImageDecoder.DecodeStb(image.RawMemory.ToArray(), maxPixels);
        }
        else if (image.TryGetPng(out var png) && png != null)
        {
            result = ImageDecoder.DecodeStb(png, maxPixels);
            applyDecode = false;
        }
        else if (image.TryGetBytesAsMemory(out var samples))
        {
            result = FromSamples(image, samples.ToArray());
            if (result == null) skipReason = "unsupported colour space or bit depth";
        }
        else
        {
            skipReason = "unsupported image encoding (" + string.Join(", ", filters) + ")";
            return null;
        }

        if (result != null && applyDecode && IsDecodeInverted(image)) Invert(result);
        return result;
    }

    /// <summary>Fallback for raw samples PdfPig cannot convert to PNG: 1-, 8- and 16-bit gray, RGB and CMYK.</summary>
    private static GrayImage? FromSamples(IPdfImage image, byte[] data)
    {
        int width = image.WidthInSamples, height = image.HeightInSamples;
        var bpc = image.IsImageMask ? 1 : image.BitsPerComponent;
        var components = image.IsImageMask ? 1 : image.ColorSpaceDetails?.NumberOfColorComponents ?? 1;
        if (width <= 0 || height <= 0) return null;
        if (bpc != 1 && bpc != 8 && bpc != 16) return null;
        if (components != 1 && components != 3 && components != 4) return null;

        var rowBytes = (width * components * bpc + 7) / 8;
        if (data.Length < (long)rowBytes * height) return null;
        var gray = new byte[width * height];
        var bytesPerSample = bpc / 8;

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
                gray[y * width + x] = bpc == 1 ? BitSample(data, row, x) : Luma(data, row + x * components * bytesPerSample, components, bytesPerSample);
        }
        return new GrayImage(width, height, gray);
    }

    /// <summary>Image masks paint where the sample is 0; regular 1-bit gray uses 1 = white.</summary>
    private static byte BitSample(byte[] data, int row, int x) =>
        (data[row + x / 8] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;

    /// <summary>The gray level of the gray, RGB or CMYK pixel at <paramref name="p"/>, using each sample's high byte.</summary>
    private static byte Luma(byte[] data, int p, int components, int bytesPerSample)
    {
        int c0 = data[p];
        if (components == 1) return (byte)c0;
        if (components == 3) return GrayImage.Luma(c0, data[p + bytesPerSample], data[p + 2 * bytesPerSample]);
        int c = c0, m = data[p + bytesPerSample], yy = data[p + 2 * bytesPerSample], k = data[p + 3 * bytesPerSample];
        return GrayImage.Luma(255 - Math.Min(255, c + k), 255 - Math.Min(255, m + k), 255 - Math.Min(255, yy + k));
    }

    /// <summary>True when the /Decode array maps the first component high-to-low, e.g. [1 0].</summary>
    private static bool IsDecodeInverted(IPdfImage image)
    {
        var decode = image.Decode;
        return decode != null && decode.Count >= 2 && decode[0] > decode[1];
    }

    private static void Invert(GrayImage image)
    {
        var p = image.Pixels;
        for (var i = 0; i < p.Length; i++) p[i] = (byte)(255 - p[i]);
    }

    private static List<string> GetFilterNames(DictionaryToken dictionary)
    {
        var names = new List<string>();
        if (!dictionary.TryGet(NameToken.Filter, out var token) && !dictionary.TryGet(NameToken.F, out token))
            return names;
        switch (token)
        {
            case NameToken name:
                names.Add(Normalize(name.Data));
                break;
            case ArrayToken array:
                names.AddRange(array.Data.OfType<NameToken>().Select(n => Normalize(n.Data)));
                break;
        }
        return names;
    }

    /// <summary>Maps the abbreviated filter names allowed in inline images to their full names.</summary>
    private static string Normalize(string name) => name switch
    {
        "DCT" => "DCTDecode",
        "Fl" => "FlateDecode",
        "CCF" => "CCITTFaxDecode",
        "LZW" => "LZWDecode",
        "RL" => "RunLengthDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        _ => name,
    };
}
