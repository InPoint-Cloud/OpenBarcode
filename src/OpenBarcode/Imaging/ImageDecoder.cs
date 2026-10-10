using System;
using System.Collections.Generic;
using System.IO;
using BitMiracle.LibTiff.Classic;
using StbImageSharp;

namespace InPoint.Cloud.OpenBarcode.Imaging
{
    /// <summary>Decodes raster image files to grayscale using managed code only.</summary>
    public static class ImageDecoder
    {
        /// <summary>Decodes every frame of an image file (TIFF files can hold several pages).</summary>
        /// <exception cref="NotSupportedException">The data is not a supported image format.</exception>
        public static IReadOnlyList<GrayImage> Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var format = FormatSniffer.Detect(data);
            switch (format)
            {
                case ImageFileFormat.Tiff:
                    return DecodeTiff(data);
                case ImageFileFormat.Pnm:
                    return new[] { PnmDecoder.Decode(data) };
                case ImageFileFormat.Png:
                case ImageFileFormat.Jpeg:
                case ImageFileFormat.Bmp:
                case ImageFileFormat.Gif:
                case ImageFileFormat.Psd:
                case ImageFileFormat.Tga:
                    return new[] { DecodeStb(data) };
                case ImageFileFormat.Pdf:
                    throw new NotSupportedException("The data is a PDF document; use BarcodeReader.ReadPdf.");
                default:
                    throw new NotSupportedException("Unrecognised image format.");
            }
        }

        /// <summary>Decodes a PNG, JPEG, BMP, GIF, TGA or PSD image.</summary>
        internal static GrayImage DecodeStb(byte[] data)
        {
            ImageResult image;
            try
            {
                image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException("The image could not be decoded: " + ex.Message, ex);
            }
            if (image == null || image.Data == null)
                throw new InvalidDataException("The image could not be decoded.");
            return GrayImage.FromPixels(image.Data, image.Width, image.Height, PixelLayout.Rgba32);
        }

        private static IReadOnlyList<GrayImage> DecodeTiff(byte[] data)
        {
            var frames = new List<GrayImage>();
            Tiff.SetErrorHandler(SilentTiffErrors.Instance);
            using var stream = new MemoryStream(data, writable: false);
            using var tiff = Tiff.ClientOpen("image.tif", "r", stream, new TiffStream());
            if (tiff == null) throw new InvalidDataException("The TIFF image could not be opened.");
            do
            {
                int width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
                var raster = new int[checked(width * height)];
                if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
                    continue;
                var gray = new byte[raster.Length];
                for (int i = 0; i < raster.Length; i++)
                {
                    int p = raster[i];
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
}
