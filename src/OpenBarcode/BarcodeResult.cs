using System.Collections.Generic;

namespace OpenBarcode
{
    /// <summary>Where a barcode was found.</summary>
    public enum BarcodeSource
    {
        /// <summary>A raster image file or bitmap.</summary>
        Image,
        /// <summary>An image embedded in a PDF page (for example a scan).</summary>
        PdfImage,
        /// <summary>Vector graphics (filled and stroked paths) on a PDF page, rasterized by the reader.</summary>
        PdfVector,
    }

    /// <summary>A point in pixel coordinates of the image the barcode was decoded from.</summary>
    public readonly struct BarcodePoint
    {
        /// <summary>Creates a point.</summary>
        public BarcodePoint(float x, float y) { X = x; Y = y; }
        /// <summary>Horizontal coordinate in pixels.</summary>
        public float X { get; }
        /// <summary>Vertical coordinate in pixels, top-down.</summary>
        public float Y { get; }
        /// <inheritdoc />
        public override string ToString() => $"({X:0.#}, {Y:0.#})";
    }

    /// <summary>A decoded barcode.</summary>
    public sealed class BarcodeResult
    {
        internal BarcodeResult(string text, BarcodeFormat format, byte[]? rawBytes, IReadOnlyList<BarcodePoint> points,
            BarcodeSource source, int? pageNumber, int? frameIndex)
        {
            Text = text;
            Format = format;
            RawBytes = rawBytes;
            Points = points;
            Source = source;
            PageNumber = pageNumber;
            FrameIndex = frameIndex;
        }

        /// <summary>The decoded content.</summary>
        public string Text { get; }

        /// <summary>The symbology.</summary>
        public BarcodeFormat Format { get; }

        /// <summary>Raw codeword bytes, when the symbology provides them.</summary>
        public byte[]? RawBytes { get; }

        /// <summary>Key points (finder patterns or bar ends) in the decoded image's pixel coordinates.</summary>
        public IReadOnlyList<BarcodePoint> Points { get; }

        /// <summary>What kind of content the barcode was read from.</summary>
        public BarcodeSource Source { get; }

        /// <summary>1-based PDF page number, or null for raster images.</summary>
        public int? PageNumber { get; }

        /// <summary>0-based frame (TIFF page, PDF image on the page) the barcode came from, when there are several.</summary>
        public int? FrameIndex { get; }

        /// <inheritdoc />
        public override string ToString() =>
            PageNumber.HasValue ? $"[page {PageNumber}] {Format}: {Text}" : $"{Format}: {Text}";
    }
}
