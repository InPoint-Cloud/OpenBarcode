# OpenBarcode

An open-source barcode reader for .NET that reads **images and PDF files** with **managed code only**:
no native libraries, no System.Drawing, no Ghostscript or PDFium. It runs anywhere .NET runs (Windows, Linux,
macOS, containers, Azure Functions) and targets .NET Standard 2.0 and .NET 8.

```csharp
using InPoint.Cloud.OpenBarcode;

var reader = new BarcodeReader();
foreach (var barcode in reader.ReadFile("invoice.pdf"))
    Console.WriteLine($"page {barcode.PageNumber}: {barcode.Format} {barcode.Text}");
```

## Install

```
dotnet add package InPoint.Cloud.OpenBarcode
dotnet tool install -g InPoint.Cloud.OpenBarcode.Cli   # command: openbarcode
```

The namespace is `InPoint.Cloud.OpenBarcode`.

## What it reads

**Symbologies:** QR Code, Data Matrix, PDF417, Aztec, Code 128 (incl. GS1-128), Code 39, Code 93, Codabar,
EAN-13, EAN-8, UPC-A, UPC-E, ITF, GS1 DataBar (+ Expanded), MaxiCode (clean crops only) and MSI (opt-in).

**Images:** PNG, JPEG, BMP, GIF, TIFF (all pages, incl. CCITT G3/G4 fax compression), TGA, PSD, PBM/PGM/PPM.
Transparent images are composited on white, so black-on-transparent barcode PNGs work.

**PDFs:** two kinds of content are searched on every page:

- *Embedded images* such as scans: JPEG (DCT), Flate, LZW, CCITT G3/G4 and JBIG2 (generic and symbol-coded),
  in gray, RGB, CMYK and indexed colour, image masks and `/Decode` inversions.
- *Vector graphics*: barcodes that PDF generators draw as filled rectangles, paths or stroked lines
  (reportlab, iText, Crystal Reports, label printers' "print to PDF", ...). The reader rasterizes the page's
  paths at 300 dpi with its own scanline renderer and decodes the result.

The same code found twice on one page (for example as both an image and vector art) is reported once.

## Usage

```csharp
var reader = new BarcodeReader(new BarcodeReaderOptions
{
    Formats = BarcodeFormat.QrCode | BarcodeFormat.Code128,  // fewer formats = faster, fewer false positives
    PdfFirstPage = 1,
    PdfLastPage = 3,
});

// From a path, a Stream or a byte[]; the type is detected from the content, not the extension.
IReadOnlyList<BarcodeResult> results = reader.ReadFile("scan.tif");

// PDFs: find out which images had to be skipped.
var warnings = new List<string>();
results = reader.Read(File.ReadAllBytes("doc.pdf"), warnings);

// Pixels you already have (System.Drawing, SkiaSharp, ImageSharp, a camera frame, ...).
var image = GrayImage.FromPixels(bgraBytes, width, height, PixelLayout.Bgra32, stride);
results = reader.Read(image);
```

Each `BarcodeResult` has `Text`, `Format`, `RawBytes`, `Points` (corner or end points, in pixels of the image it
was decoded from), `Source` (`Image`, `PdfImage`, `PdfVector`), `PageNumber` (PDFs, 1-based) and `FrameIndex`
(TIFF page, or image index on a PDF page).

`BarcodeReader` is immutable and thread-safe; share one instance.

### Options

| Option | Default | |
| --- | --- | --- |
| `Formats` | `BarcodeFormat.Default` | Everything except MSI. |
| `TryHarder` | `true` | Denser scanning. |
| `TryInverted` | `true` | Also try light-on-dark. |
| `AutoRotate` | `true` | Also look for 1D codes turned 90°. 2D codes are found at any angle regardless. |
| `ReadMultiple` | `true` | Find every code, not just the first. |
| `EnhancePoorImages` | `true` | When nothing is found in a photo or scan, retry with clean-up (see below). Off for rendered PDF vectors. |
| `PdfReadImages` / `PdfReadVectorGraphics` | `true` | Turn either PDF source off. |
| `PdfRenderDpi` | `300` | Resolution for vector graphics. |
| `PdfMaxRenderPixels` | 40 M | Caps memory for huge pages; DPI is lowered to fit. |
| `PdfFirstPage` / `PdfLastPage` | all | Page range. |
| `PdfPassword` | | For encrypted PDFs. |
| `CharacterSet` | | Fallback encoding for byte-mode data, e.g. `"ISO-8859-1"`. |

## Command line

```
dotnet run --project src/OpenBarcode.Cli -- invoice.pdf scans/*.tif
dotnet run --project src/OpenBarcode.Cli -- --json --formats qr,datamatrix label.png
```

Run with `--help` for all options. Output is `file[:page]<TAB>format<TAB>text`; the exit code is 0 when
something was found, 1 when nothing was, 2 on errors.

## Performance

A full A4 page scanned at 300 dpi takes roughly 0.5–2 s with the thorough defaults on one core.
Restricting `Formats`, setting `ReadMultiple = false` (stop at the first code) or `TryHarder = false`
makes it several times faster. Pages are independent, so batches parallelise well.

When an image holds no barcode, the clean-up passes for poor pictures (`EnhancePoorImages`) roughly triple
the time spent on it (an A4 page at 300 dpi: about 1 s becomes about 3 s). Images where a code is found
straight away cost nothing extra. Turn the option off, or use `--no-enhance`, for clean input in bulk.

## Poor pictures

Faded print, dark or unevenly lit photos, noise, JPEG artefacts, blur and tilted codes are handled by
clean-up passes that run only when the normal passes find nothing:

- lighting correction that stretches contrast locally (faint print, shadows, dark photos),
- adaptive thresholding, which ZXing's per-row binarizer for 1D codes lacks,
- denoising (median and Gaussian), and averaging along the bars of 1D codes,
- sharpening for out-of-focus pictures,
- tilt detection from edge directions, so Aztec, PDF417 and 1D codes are read at any angle
  (ZXing gives up beyond about 10–15°),
- large images are cleaned up at half size.

Codes with only a single check digit (EAN/UPC, EAN-8, UPC-E, ITF, Codabar, DataBar) are easy to "read" out of
noise, so on these passes they are only reported when two different passes agree.

## Limitations

- Barcodes set as **text in a barcode font** inside a PDF are not detected (glyphs are not rendered).
  Most generators draw bars as paths or images, which work.
- **JPEG 2000** (`JPXDecode`) images in PDFs are skipped with a warning; no managed decoder is used.
- The vector renderer ignores clipping paths, soft masks and shading patterns.
- Damaged, very blurry or strongly perspective-distorted photos may still not decode, and neither do codes
  with modules much smaller than 1.5 pixels; this is a document reader first, not a camera scanner.

## How it works

```
file ──► sniff type ─┬─► image decoders (StbImageSharp, LibTiff.NET, built-in PNM) ──────────────┐
                     └─► PdfPig ─┬─► embedded images ─► filters (+ managed JBIG2) ─► grayscale ─┤
                                 └─► paths ─► scanline rasterizer (this project) ───────────────┤
                                                                                                 ▼
                         ZXing.Net detectors ◄── quiet-zone padding, upscaling of tiny images,
                                                 90° pass for 1D, blob crops for Data Matrix/Aztec,
                                                 fragment and duplicate filtering, then, if nothing
                                                 was found, clean-up passes for poor pictures
```

ZXing's Data Matrix and Aztec detectors search outward from the image centre, so on a page with several codes
they only see one. The reader proposes compact ink blobs and gives each its own crop. Partial reads that ZXing
can report inside a longer 1D barcode (a "UPC-E" made of half an EAN-13) are filtered out.

## Building and testing

Needs the .NET 10 SDK (for the `.slnx` solution file); the library itself targets .NET Standard 2.0 and .NET 8.

```
dotnet build
dotnet test
```

The tests generate barcodes in every supported symbology and file format, build PDFs with hand-written content
streams and image dictionaries, and read real-world-style samples made by reportlab, poppler, ImageMagick and
jbig2enc (`tests/samples`).

## License

Apache-2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the libraries it builds on.

## Releasing

Push a tag such as `v0.1.0` (or `v0.2.0-beta.1` for a prerelease). The `Release` workflow builds, tests, packs and
publishes both packages to nuget.org under the InPoint.Cloud account using Trusted Publishing, then creates a
GitHub release with the packages attached. The package version comes from the tag.
