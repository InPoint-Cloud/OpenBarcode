# Third-party components

OpenBarcode is Apache-2.0 licensed and depends only on managed (.NET) libraries with permissive licenses.
No native binaries are shipped or loaded.

| Package | Used for | License |
| --- | --- | --- |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | Barcode detection and decoding | Apache-2.0 |
| [PdfPig](https://github.com/UglyToad/PdfPig) | PDF parsing, embedded images, vector paths | Apache-2.0 |
| [PdfPig.Filters.Jbig2.PdfboxJbig2](https://github.com/BobLd/UglyToad.PdfPig.Filters.Jbig2.PdfboxJbig2) | JBIG2 image decoding (port of Apache PDFBox jbig2) | Apache-2.0 |
| [StbImageSharp](https://github.com/StbSharp/StbImageSharp) | PNG, JPEG, BMP, GIF, TGA, PSD decoding | Public domain (Unlicense) or MIT |
| [BitMiracle.LibTiff.NET](https://github.com/BitMiracle/libtiff.net) | TIFF decoding, including CCITT fax compression | BSD-3-Clause (New BSD) |

Test-only: [StbImageWriteSharp](https://github.com/StbSharp/StbImageWriteSharp) (Unlicense or MIT), xUnit (Apache-2.0).
