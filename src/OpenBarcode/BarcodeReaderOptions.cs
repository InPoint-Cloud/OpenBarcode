namespace InPoint.Cloud.OpenBarcode
{
    /// <summary>Settings for <see cref="BarcodeReader"/>.</summary>
    public sealed class BarcodeReaderOptions
    {
        /// <summary>
        /// Formats to look for. Defaults to <see cref="BarcodeFormat.Default"/> (everything except MSI).
        /// Restricting this makes reading faster and avoids false positives.
        /// </summary>
        public BarcodeFormat Formats { get; set; } = BarcodeFormat.Default;

        /// <summary>Spend more time looking for barcodes (denser scanning, more rotations). Default true.</summary>
        public bool TryHarder { get; set; } = true;

        /// <summary>Also try the inverted image (light bars on a dark background). Default true.</summary>
        public bool TryInverted { get; set; } = true;

        /// <summary>Also try the image rotated by 90, 180 and 270 degrees. Default true.</summary>
        public bool AutoRotate { get; set; } = true;

        /// <summary>
        /// When the normal passes find nothing in a photo, scan or embedded PDF image, retry with image clean-up:
        /// lighting and contrast correction, adaptive thresholding, denoising, sharpening, rescaling and
        /// diagonal orientations for 1D codes. Helps with blurry, noisy, dark, low-contrast or skewed pictures.
        /// It only costs time on images where nothing is found. Default true.
        /// </summary>
        public bool EnhancePoorImages { get; set; } = true;

        /// <summary>Find every barcode in an image instead of stopping at the first. Default true.</summary>
        public bool ReadMultiple { get; set; } = true;

        /// <summary>Decode images embedded in PDF pages (scans, pasted barcode pictures). Default true.</summary>
        public bool PdfReadImages { get; set; } = true;

        /// <summary>Rasterize PDF vector graphics so barcodes drawn as paths can be read. Default true.</summary>
        public bool PdfReadVectorGraphics { get; set; } = true;

        /// <summary>Resolution used when rasterizing PDF vector graphics. Default 300.</summary>
        public int PdfRenderDpi { get; set; } = 300;

        /// <summary>Upper bound on the size of a rasterized PDF page, in pixels. The DPI is lowered to fit. Default 40 million.</summary>
        public long PdfMaxRenderPixels { get; set; } = 40_000_000;

        /// <summary>1-based first PDF page to read. Default 1.</summary>
        public int PdfFirstPage { get; set; } = 1;

        /// <summary>1-based last PDF page to read, or null for the last page of the document.</summary>
        public int? PdfLastPage { get; set; }

        /// <summary>Password for encrypted PDFs.</summary>
        public string? PdfPassword { get; set; }

        /// <summary>Character set to assume for byte-mode content that does not declare one (e.g. "UTF-8", "ISO-8859-1").</summary>
        public string? CharacterSet { get; set; }
    }
}
