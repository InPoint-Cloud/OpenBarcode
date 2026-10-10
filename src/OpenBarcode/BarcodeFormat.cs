using System;

namespace InPoint.Cloud.OpenBarcode
{
    /// <summary>Barcode symbologies the reader can detect. Flags can be combined to restrict a search.</summary>
    [Flags]
    public enum BarcodeFormat
    {
        /// <summary>No format.</summary>
        None = 0,
        /// <summary>QR Code (2D).</summary>
        QrCode = 1 << 0,
        /// <summary>Data Matrix (2D).</summary>
        DataMatrix = 1 << 1,
        /// <summary>PDF417 (stacked 2D).</summary>
        Pdf417 = 1 << 2,
        /// <summary>Aztec (2D).</summary>
        Aztec = 1 << 3,
        /// <summary>Code 128 (1D), including GS1-128.</summary>
        Code128 = 1 << 4,
        /// <summary>Code 39 (1D).</summary>
        Code39 = 1 << 5,
        /// <summary>Code 93 (1D).</summary>
        Code93 = 1 << 6,
        /// <summary>Codabar (1D).</summary>
        Codabar = 1 << 7,
        /// <summary>EAN-13 (1D).</summary>
        Ean13 = 1 << 8,
        /// <summary>EAN-8 (1D).</summary>
        Ean8 = 1 << 9,
        /// <summary>UPC-A (1D).</summary>
        UpcA = 1 << 10,
        /// <summary>UPC-E (1D).</summary>
        UpcE = 1 << 11,
        /// <summary>Interleaved 2 of 5 (1D).</summary>
        Itf = 1 << 12,
        /// <summary>MSI Plessey (1D).</summary>
        Msi = 1 << 13,
        /// <summary>GS1 DataBar / RSS-14 (1D).</summary>
        DataBar = 1 << 14,
        /// <summary>GS1 DataBar Expanded / RSS Expanded (1D).</summary>
        DataBarExpanded = 1 << 15,
        /// <summary>MaxiCode (2D, pure symbols only).</summary>
        MaxiCode = 1 << 16,

        /// <summary>All 1D (linear) formats.</summary>
        AllLinear = Code128 | Code39 | Code93 | Codabar | Ean13 | Ean8 | UpcA | UpcE | Itf | Msi | DataBar | DataBarExpanded,
        /// <summary>All 2D formats.</summary>
        All2D = QrCode | DataMatrix | Pdf417 | Aztec | MaxiCode,
        /// <summary>Every supported format.</summary>
        All = AllLinear | All2D,

        /// <summary>
        /// The default search: every format except MSI, which has no reliable start/stop pattern or checksum
        /// and is often "found" in plain text. Add <see cref="Msi"/> explicitly when you need it.
        /// </summary>
        Default = All & ~Msi,
    }
}
