using System;
using System.Collections.Generic;
using System.IO;
using OpenBarcode.Decoding;
using OpenBarcode.Imaging;
using OpenBarcode.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OpenBarcode
{
    /// <summary>
    /// Reads barcodes from image files, PDF documents and raw bitmaps. Instances are immutable and thread-safe.
    /// </summary>
    /// <example>
    /// <code>
    /// var reader = new BarcodeReader();
    /// foreach (var barcode in reader.ReadFile("invoice.pdf"))
    ///     Console.WriteLine($"{barcode.PageNumber}: {barcode.Format} {barcode.Text}");
    /// </code>
    /// </example>
    public sealed class BarcodeReader
    {
        private readonly BarcodeReaderOptions _options;

        /// <summary>Creates a reader with default options (all formats, thorough search).</summary>
        public BarcodeReader() : this(new BarcodeReaderOptions()) { }

        /// <summary>Creates a reader with the given options. The options are copied.</summary>
        public BarcodeReader(BarcodeReaderOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _options = Copy(options);
            if (_options.Formats == BarcodeFormat.None) throw new ArgumentException("No barcode formats selected.", nameof(options));
            if (_options.PdfRenderDpi < 36 || _options.PdfRenderDpi > 1200)
                throw new ArgumentOutOfRangeException(nameof(options), "PdfRenderDpi must be between 36 and 1200.");
        }

        /// <summary>
        /// Reads a PDF or image file. The type is detected from the content, not the extension.
        /// </summary>
        public IReadOnlyList<BarcodeResult> ReadFile(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return Read(File.ReadAllBytes(path));
        }

        /// <summary>Reads a PDF or image from a stream. The stream is read to the end and not disposed.</summary>
        public IReadOnlyList<BarcodeResult> Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Read(buffer.ToArray());
        }

        /// <summary>Reads a PDF or image from memory.</summary>
        /// <exception cref="NotSupportedException">The content is neither a PDF nor a supported image format.</exception>
        public IReadOnlyList<BarcodeResult> Read(byte[] data) => Read(data, null);

        /// <summary>Reads a PDF or image from memory, reporting PDF images that had to be skipped.</summary>
        /// <param name="data">The file content.</param>
        /// <param name="warnings">Receives a message for each part of a PDF that could not be read, if not null.</param>
        /// <exception cref="NotSupportedException">The content is neither a PDF nor a supported image format.</exception>
        public IReadOnlyList<BarcodeResult> Read(byte[] data, ICollection<string>? warnings)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return FormatSniffer.Detect(data) == ImageFileFormat.Pdf ? ReadPdf(data, warnings) : ReadImage(data);
        }

        /// <summary>Reads an encoded image (PNG, JPEG, BMP, GIF, TIFF, TGA, PSD, PBM/PGM/PPM). Every TIFF page is read.</summary>
        public IReadOnlyList<BarcodeResult> ReadImage(byte[] data)
        {
            var frames = ImageDecoder.Decode(data);
            var decoder = new ZxingDecoder(_options);
            var results = new ResultSet();
            for (int i = 0; i < frames.Count; i++)
                results.AddAll(decoder.Decode(frames[i]), BarcodeSource.Image, null, frames.Count > 1 ? i : (int?)null);
            return results.ToList();
        }

        /// <summary>Reads a bitmap you have already decoded, for example from System.Drawing, SkiaSharp or a camera.</summary>
        public IReadOnlyList<BarcodeResult> Read(GrayImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var results = new ResultSet();
            results.AddAll(new ZxingDecoder(_options).Decode(image), BarcodeSource.Image, null, null);
            return results.ToList();
        }

        /// <summary>Reads a PDF from memory.</summary>
        public IReadOnlyList<BarcodeResult> ReadPdf(byte[] data) => ReadPdf(data, null);

        /// <summary>Reads a PDF from memory, reporting images that could not be decoded.</summary>
        /// <param name="data">The PDF file content.</param>
        /// <param name="warnings">Receives a message for each embedded image that was skipped (e.g. JBIG2), if not null.</param>
        public IReadOnlyList<BarcodeResult> ReadPdf(byte[] data, ICollection<string>? warnings)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var parsing = new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = ManagedFilterProvider.Instance,
            };
            if (_options.PdfPassword != null) parsing.Password = _options.PdfPassword;

            var decoder = new ZxingDecoder(_options);
            var results = new ResultSet();
            using var document = PdfDocument.Open(data, parsing);
            int first = Math.Max(1, _options.PdfFirstPage);
            int last = Math.Min(document.NumberOfPages, _options.PdfLastPage ?? document.NumberOfPages);
            for (int number = first; number <= last; number++)
            {
                Page page;
                try
                {
                    page = document.GetPage(number);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings?.Add($"Page {number}: could not be parsed ({ex.Message}).");
                    continue;
                }
                ReadPage(page, decoder, results, warnings);
            }
            return results.ToList();
        }

        private void ReadPage(Page page, ZxingDecoder decoder, ResultSet results, ICollection<string>? warnings)
        {
            if (_options.PdfReadImages)
            {
                int index = 0;
                foreach (var image in page.GetImages())
                {
                    int imageIndex = index++;
                    GrayImage? bitmap;
                    string? reason;
                    try
                    {
                        bitmap = PdfImageDecoder.Decode(image, out reason);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        bitmap = null;
                        reason = ex.Message;
                    }
                    if (bitmap == null)
                    {
                        warnings?.Add($"Page {page.Number}, image {imageIndex}: skipped ({reason}).");
                        continue;
                    }
                    if (bitmap.Width < 8 || bitmap.Height < 8) continue;
                    results.AddAll(decoder.Decode(bitmap), BarcodeSource.PdfImage, page.Number, imageIndex);
                }
            }

            if (_options.PdfReadVectorGraphics)
            {
                GrayImage? raster;
                try
                {
                    raster = VectorPageRenderer.Render(page, _options.PdfRenderDpi, _options.PdfMaxRenderPixels);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings?.Add($"Page {page.Number}: vector graphics could not be rendered ({ex.Message}).");
                    raster = null;
                }
                if (raster != null)
                    results.AddAll(decoder.Decode(raster), BarcodeSource.PdfVector, page.Number, null);
            }
        }

        private static BarcodeReaderOptions Copy(BarcodeReaderOptions o) => new BarcodeReaderOptions
        {
            Formats = o.Formats,
            TryHarder = o.TryHarder,
            TryInverted = o.TryInverted,
            AutoRotate = o.AutoRotate,
            ReadMultiple = o.ReadMultiple,
            PdfReadImages = o.PdfReadImages,
            PdfReadVectorGraphics = o.PdfReadVectorGraphics,
            PdfRenderDpi = o.PdfRenderDpi,
            PdfMaxRenderPixels = o.PdfMaxRenderPixels,
            PdfFirstPage = o.PdfFirstPage,
            PdfLastPage = o.PdfLastPage,
            PdfPassword = o.PdfPassword,
            CharacterSet = o.CharacterSet,
        };

        /// <summary>Collects results in discovery order, dropping repeats of the same code on the same page.</summary>
        private sealed class ResultSet
        {
            private readonly List<BarcodeResult> _results = new();
            private readonly HashSet<(int?, BarcodeFormat, string)> _seen = new();

            public void AddAll(List<RawHit> hits, BarcodeSource source, int? page, int? frame)
            {
                foreach (var hit in hits)
                {
                    // TIFF pages are separate images, so the same code on two pages is two results.
                    int? scope = page ?? frame;
                    if (!_seen.Add((scope, hit.Format, hit.Text))) continue;
                    _results.Add(new BarcodeResult(hit.Text, hit.Format, hit.RawBytes, hit.Points, source, page, frame));
                }
            }

            public IReadOnlyList<BarcodeResult> ToList() => _results;
        }
    }
}
