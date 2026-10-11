using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using InPoint.Cloud.OpenBarcode.Decoding;
using InPoint.Cloud.OpenBarcode.Imaging;
using InPoint.Cloud.OpenBarcode.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InPoint.Cloud.OpenBarcode;

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
        if (_options.MaxImagePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxImagePixels must be positive.");
    }

    /// <summary>
    /// Reads a PDF or image file. The type is detected from the content, not the extension.
    /// </summary>
    public IReadOnlyList<BarcodeResult> ReadFile(string path) => ReadFile(path, CancellationToken.None);

    /// <summary>Reads a PDF or image file, stopping with <see cref="OperationCanceledException"/> when cancelled.</summary>
    public IReadOnlyList<BarcodeResult> ReadFile(string path, CancellationToken cancellationToken)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return Read(File.ReadAllBytes(path), null, cancellationToken);
    }

    /// <summary>Reads a PDF or image from a stream. The stream is read to the end and not disposed.</summary>
    public IReadOnlyList<BarcodeResult> Read(Stream stream) => Read(stream, CancellationToken.None);

    /// <summary>Reads a PDF or image from a stream, stopping with <see cref="OperationCanceledException"/> when cancelled.</summary>
    public IReadOnlyList<BarcodeResult> Read(Stream stream, CancellationToken cancellationToken)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray(), null, cancellationToken);
    }

    /// <summary>Reads a PDF or image from memory.</summary>
    /// <exception cref="NotSupportedException">The content is neither a PDF nor a supported image format.</exception>
    public IReadOnlyList<BarcodeResult> Read(byte[] data) => Read(data, null, CancellationToken.None);

    /// <summary>Reads a PDF or image from memory, reporting PDF images that had to be skipped.</summary>
    /// <param name="data">The file content.</param>
    /// <param name="warnings">Receives a message for each part of a PDF that could not be read, if not null.</param>
    /// <exception cref="NotSupportedException">The content is neither a PDF nor a supported image format.</exception>
    public IReadOnlyList<BarcodeResult> Read(byte[] data, ICollection<string>? warnings) => Read(data, warnings, CancellationToken.None);

    /// <summary>Reads a PDF or image from memory, stopping with <see cref="OperationCanceledException"/> when cancelled.</summary>
    /// <param name="data">The file content.</param>
    /// <param name="warnings">Receives a message for each part of a PDF that could not be read, if not null.</param>
    /// <param name="cancellationToken">Checked between pages and between the passes over each image.</param>
    /// <exception cref="NotSupportedException">The content is neither a PDF nor a supported image format.</exception>
    public IReadOnlyList<BarcodeResult> Read(byte[] data, ICollection<string>? warnings, CancellationToken cancellationToken) =>
        [.. Enumerate(data, warnings, cancellationToken)];

    /// <summary>
    /// Reads a PDF or image file and returns each page's barcodes as soon as that page is done, instead of
    /// waiting for the whole document. Stopping the enumeration early stops reading.
    /// </summary>
    /// <param name="path">The file to read. It is loaded when the enumeration starts.</param>
    /// <param name="warnings">Receives a message for each part of a PDF that could not be read, if not null.</param>
    /// <param name="cancellationToken">Checked between pages and between the passes over each image.</param>
    public IEnumerable<BarcodeResult> EnumerateFile(string path, ICollection<string>? warnings = null, CancellationToken cancellationToken = default)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return Iterate();

        IEnumerable<BarcodeResult> Iterate()
        {
            foreach (var result in EnumerateCore(File.ReadAllBytes(path), warnings, cancellationToken))
                yield return result;
        }
    }

    /// <summary>
    /// Reads a PDF or image from memory and returns each page's barcodes (or each TIFF page's) as soon as that
    /// page is done. Stopping the enumeration early stops reading.
    /// </summary>
    /// <param name="data">The file content.</param>
    /// <param name="warnings">Receives a message for each part of a PDF that could not be read, if not null.</param>
    /// <param name="cancellationToken">Checked between pages and between the passes over each image.</param>
    /// <exception cref="NotSupportedException">The content is neither a PDF nor a supported image format.</exception>
    public IEnumerable<BarcodeResult> Enumerate(byte[] data, ICollection<string>? warnings = null, CancellationToken cancellationToken = default)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return EnumerateCore(data, warnings, cancellationToken);
    }

    private IEnumerable<BarcodeResult> EnumerateCore(byte[] data, ICollection<string>? warnings, CancellationToken cancellationToken) =>
        FormatSniffer.Detect(data) == ImageFileFormat.Pdf
            ? EnumeratePdf(data, warnings, cancellationToken)
            : EnumerateImage(data, cancellationToken);

    /// <summary>Reads an encoded image (PNG, JPEG, BMP, GIF, TIFF, TGA, PSD, PBM/PGM/PPM). Every TIFF page is read.</summary>
    public IReadOnlyList<BarcodeResult> ReadImage(byte[] data) => ReadImage(data, CancellationToken.None);

    /// <summary>Reads an encoded image, stopping with <see cref="OperationCanceledException"/> when cancelled.</summary>
    public IReadOnlyList<BarcodeResult> ReadImage(byte[] data, CancellationToken cancellationToken)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return [.. EnumerateImage(data, cancellationToken)];
    }

    private IEnumerable<BarcodeResult> EnumerateImage(byte[] data, CancellationToken cancellationToken)
    {
        var frames = ImageDecoder.Decode(data, _options.MaxImagePixels);
        var decoder = new ZxingDecoder(_options, cancellationToken);
        var results = new ResultSet();
        for (var i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hits = decoder.Decode(frames[i], _options.EnhancePoorImages);
            foreach (var result in results.AddAll(hits, BarcodeSource.Image, null, frames.Count > 1 ? i : null))
                yield return result;
        }
    }

    /// <summary>Reads a bitmap you have already decoded, for example from System.Drawing, SkiaSharp or a camera.</summary>
    public IReadOnlyList<BarcodeResult> Read(GrayImage image) => Read(image, CancellationToken.None);

    /// <summary>Reads a bitmap you have already decoded, stopping with <see cref="OperationCanceledException"/> when cancelled.</summary>
    public IReadOnlyList<BarcodeResult> Read(GrayImage image, CancellationToken cancellationToken)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        var hits = new ZxingDecoder(_options, cancellationToken).Decode(image, _options.EnhancePoorImages);
        return new ResultSet().AddAll(hits, BarcodeSource.Image, null, null);
    }

    /// <summary>Reads a PDF from memory.</summary>
    public IReadOnlyList<BarcodeResult> ReadPdf(byte[] data) => ReadPdf(data, null, CancellationToken.None);

    /// <summary>Reads a PDF from memory, reporting images that could not be decoded.</summary>
    /// <param name="data">The PDF file content.</param>
    /// <param name="warnings">Receives a message for each embedded image that was skipped (e.g. JBIG2), if not null.</param>
    public IReadOnlyList<BarcodeResult> ReadPdf(byte[] data, ICollection<string>? warnings) => ReadPdf(data, warnings, CancellationToken.None);

    /// <summary>Reads a PDF from memory, stopping with <see cref="OperationCanceledException"/> when cancelled.</summary>
    /// <param name="data">The PDF file content.</param>
    /// <param name="warnings">Receives a message for each embedded image that was skipped (e.g. JBIG2), if not null.</param>
    /// <param name="cancellationToken">Checked between pages and between the passes over each image.</param>
    public IReadOnlyList<BarcodeResult> ReadPdf(byte[] data, ICollection<string>? warnings, CancellationToken cancellationToken)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return [.. EnumeratePdf(data, warnings, cancellationToken)];
    }

    private IEnumerable<BarcodeResult> EnumeratePdf(byte[] data, ICollection<string>? warnings, CancellationToken cancellationToken)
    {
        var parsing = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            FilterProvider = ManagedFilterProvider.Instance,
        };
        if (_options.PdfPassword != null) parsing.Password = _options.PdfPassword;

        var decoder = new ZxingDecoder(_options, cancellationToken);
        var results = new ResultSet();
        using var document = PdfDocument.Open(data, parsing);
        var first = Math.Max(1, _options.PdfFirstPage);
        var last = Math.Min(document.NumberOfPages, _options.PdfLastPage ?? document.NumberOfPages);
        for (var number = first; number <= last; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            foreach (var result in ReadPage(page, decoder, results, warnings))
                yield return result;
        }
    }

    /// <summary>Reads one page and returns the barcodes not already found on it.</summary>
    private List<BarcodeResult> ReadPage(Page page, ZxingDecoder decoder, ResultSet results, ICollection<string>? warnings)
    {
        var found = new List<BarcodeResult>();
        if (_options.PdfReadImages)
            ReadPageImages(page, decoder, results, warnings, found);
        if (_options.PdfReadVectorGraphics)
            ReadPageVectors(page, decoder, results, warnings, found);
        return found;
    }

    private void ReadPageImages(Page page, ZxingDecoder decoder, ResultSet results, ICollection<string>? warnings, List<BarcodeResult> found)
    {
        var index = 0;
        foreach (var image in page.GetImages())
        {
            var imageIndex = index++;
            var bitmap = DecodeImage(image, _options.MaxImagePixels, out var reason);
            if (bitmap == null)
            {
                warnings?.Add($"Page {page.Number}, image {imageIndex}: skipped ({reason}).");
                continue;
            }
            if (bitmap.Width < 8 || bitmap.Height < 8) continue;
            found.AddRange(results.AddAll(decoder.Decode(bitmap, _options.EnhancePoorImages), BarcodeSource.PdfImage, page.Number, imageIndex));
        }
    }

    /// <summary>Decodes an embedded image; null with a reason when it is unsupported or broken.</summary>
    private static GrayImage? DecodeImage(IPdfImage image, long maxPixels, out string? reason)
    {
        try
        {
            return PdfImageDecoder.Decode(image, maxPixels, out reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = ex.Message;
            return null;
        }
    }

    private void ReadPageVectors(Page page, ZxingDecoder decoder, ResultSet results, ICollection<string>? warnings, List<BarcodeResult> found)
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
            // Rendered vector graphics are clean, so the clean-up passes would only cost time.
            found.AddRange(results.AddAll(decoder.Decode(raster, enhance: false), BarcodeSource.PdfVector, page.Number, null));
    }

    private static BarcodeReaderOptions Copy(BarcodeReaderOptions o) => new()
    {
        Formats = o.Formats,
        TryHarder = o.TryHarder,
        TryInverted = o.TryInverted,
        AutoRotate = o.AutoRotate,
        EnhancePoorImages = o.EnhancePoorImages,
        ReadMultiple = o.ReadMultiple,
        PdfReadImages = o.PdfReadImages,
        PdfReadVectorGraphics = o.PdfReadVectorGraphics,
        PdfRenderDpi = o.PdfRenderDpi,
        PdfMaxRenderPixels = o.PdfMaxRenderPixels,
        MaxImagePixels = o.MaxImagePixels,
        PdfFirstPage = o.PdfFirstPage,
        PdfLastPage = o.PdfLastPage,
        PdfPassword = o.PdfPassword,
        CharacterSet = o.CharacterSet,
    };

    /// <summary>Drops repeats of the same code on the same page.</summary>
    private sealed class ResultSet
    {
        private readonly HashSet<(int?, BarcodeFormat, string)> _seen = [];

        /// <summary>Returns the hits not seen before on this page, in discovery order.</summary>
        public List<BarcodeResult> AddAll(List<RawHit> hits, BarcodeSource source, int? page, int? frame)
        {
            var added = new List<BarcodeResult>();
            foreach (var hit in hits)
            {
                // TIFF pages are separate images, so the same code on two pages is two results.
                var scope = page ?? frame;
                if (!_seen.Add((scope, hit.Format, hit.Text))) continue;
                added.Add(new BarcodeResult(hit.Text, hit.Format, hit.RawBytes, hit.Points, source, page, frame));
            }
            return added;
        }
    }
}
