using System;
using System.Collections.Generic;
using System.IO;
using BitMiracle.LibTiff.Classic;
using StbImageSharp;

namespace InPoint.Cloud.OpenBarcode.Imaging;

/// <summary>Decodes raster image files to grayscale using managed code only.</summary>
public static class ImageDecoder
{
    /// <summary>The default for <see cref="BarcodeReaderOptions.MaxImagePixels"/>.</summary>
    internal const long DefaultMaxPixels = 100_000_000;

    /// <summary>
    /// Decodes every frame of an image file (TIFF files can hold several pages). Images larger than 100 million
    /// pixels are refused.
    /// </summary>
    /// <exception cref="NotSupportedException">The data is not a supported image format.</exception>
    /// <exception cref="InvalidDataException">The image is broken or larger than the limit.</exception>
    public static IReadOnlyList<GrayImage> Decode(byte[] data) => Decode(data, DefaultMaxPixels);

    /// <summary>Decodes every frame of an image file, refusing any frame larger than <paramref name="maxPixels"/>.</summary>
    /// <param name="data">The file content.</param>
    /// <param name="maxPixels">Largest frame to decode, in pixels. Checked against the header before decoding.</param>
    /// <exception cref="NotSupportedException">The data is not a supported image format.</exception>
    /// <exception cref="InvalidDataException">The image is broken or larger than <paramref name="maxPixels"/>.</exception>
    public static IReadOnlyList<GrayImage> Decode(byte[] data, long maxPixels)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (maxPixels <= 0) throw new ArgumentOutOfRangeException(nameof(maxPixels));
        var format = FormatSniffer.Detect(data);
        return format switch
        {
            ImageFileFormat.Tiff => DecodeTiff(data, maxPixels),
            ImageFileFormat.Pnm => new[] { PnmDecoder.Decode(data, maxPixels) },
            ImageFileFormat.Png or ImageFileFormat.Jpeg or ImageFileFormat.Bmp or ImageFileFormat.Gif or ImageFileFormat.Psd or ImageFileFormat.Tga => new[] { DecodeStb(data, maxPixels) },
            ImageFileFormat.Pdf => throw new NotSupportedException("The data is a PDF document; use BarcodeReader.ReadPdf."),
            _ => throw new NotSupportedException("Unrecognised image format."),
        };
    }

    /// <summary>Decodes a PNG, JPEG, BMP, GIF, TGA or PSD image.</summary>
    internal static GrayImage DecodeStb(byte[] data, long maxPixels)
    {
        ImageResult image;
        try
        {
            using (var header = new MemoryStream(data, writable: false))
            {
                var info = ImageInfo.FromStream(header);
                if (info != null) CheckSize(info.Value.Width, info.Value.Height, maxPixels);
            }
            image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException("The image could not be decoded: " + ex.Message, ex);
        }
        if (image == null || image.Data == null)
            throw new InvalidDataException("The image could not be decoded.");
        return GrayImage.FromPixels(image.Data, image.Width, image.Height, PixelLayout.Rgba32);
    }

    /// <summary>Throws when a <paramref name="width"/> × <paramref name="height"/> image exceeds <paramref name="maxPixels"/>.</summary>
    internal static void CheckSize(long width, long height, long maxPixels)
    {
        if (width * height > maxPixels)
            throw new InvalidDataException($"The image is {width}×{height} pixels, more than the limit of {maxPixels:N0} (MaxImagePixels).");
    }

    private static IReadOnlyList<GrayImage> DecodeTiff(byte[] data, long maxPixels)
    {
        var frames = new List<GrayImage>();
        Tiff.SetErrorHandler(SilentTiffErrors.Instance);
        using var stream = new MemoryStream(data, writable: false);
        using var tiff = Tiff.ClientOpen("image.tif", "r", stream, new TiffStream()) ?? throw new InvalidDataException("The TIFF image could not be opened.");
        do
        {
            var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            CheckSize(width, height, maxPixels);
            var raster = new int[checked(width * height)];
            if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
                continue;
            var gray = new byte[raster.Length];
            for (var i = 0; i < raster.Length; i++)
            {
                var p = raster[i];
                gray[i] = GrayImage.Composite(GrayImage.Luma(Tiff.GetR(p), Tiff.GetG(p), Tiff.GetB(p)), Tiff.GetA(p));
            }
            frames.Add(new GrayImage(width, height, gray));
        } while (tiff.ReadDirectory());

        if (frames.Count == 0) throw new InvalidDataException("The TIFF image has no readable pages.");
        return frames;
    }

    private sealed class SilentTiffErrors : TiffErrorHandler
    {
        public static readonly SilentTiffErrors Instance = new();
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }
}
