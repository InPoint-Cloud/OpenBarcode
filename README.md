# OpenBarcode

An open-source barcode reader for .NET that reads **images and PDF files** with **managed code only**:
no native libraries, no System.Drawing, no Ghostscript or PDFium. It runs anywhere .NET runs (Windows, Linux,
macOS, containers, Azure Functions) and targets .NET Standard 2.0.

```csharp
using InPoint.Cloud.OpenBarcode;

var reader = new BarcodeReader();
foreach (var barcode in reader.ReadFile("invoice.pdf"))
    Console.WriteLine($"page {barcode.PageNumber}: {barcode.Format} {barcode.Text}");
```

## Install

```
dotnet add package InPoint.Cloud.OpenBarcode
dotnet tool install -g InPoint.Cloud.OpenBarcode.Cli   # command: openbarcode (needs .NET 10 or later)
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

Long documents can be read page by page, and any read can be cancelled:

```csharp
// Each page's barcodes come back as soon as that page is done; breaking out of the loop stops reading.
foreach (var barcode in reader.EnumerateFile("archive.pdf", warnings, cancellationToken))
    Console.WriteLine($"page {barcode.PageNumber}: {barcode.Text}");

// Every Read method has an overload that takes a CancellationToken; it throws OperationCanceledException.
results = reader.ReadFile("scan.tif", cancellationToken);
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
| `MaxImagePixels` | 100 M | Larger images (files, TIFF pages, images in PDFs) are refused from their header, so a small file can't claim gigabytes. |
| `PdfFirstPage` / `PdfLastPage` | all | Page range. |
| `PdfPassword` | | For encrypted PDFs. |
| `CharacterSet` | | Fallback encoding for byte-mode data, e.g. `"ISO-8859-1"`. |

## Command line

```
dotnet run --project src/OpenBarcode.Cli -- invoice.pdf scans/*.tif
dotnet run --project src/OpenBarcode.Cli -- --json --formats qr,datamatrix label.png
openbarcode -j 0 scans/*.pdf   # read a batch on every core; output keeps the order of the files
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

- lighting correction that stretches contrast locally (faint print, shadows, dark photos) without
  amplifying sensor noise,
- adaptive thresholding, which ZXing's per-row binarizer for 1D codes lacks,
- denoising (median and Gaussian), and averaging along the bars of 1D codes, then thresholding each row
  midway between its local darkest and lightest pixel, so grainy, blurred phone photos of 1D codes read
  (ZXing's own row binarizer sharpens the noise back in and its threshold drifts when ink spreads),
- sharpening for out-of-focus pictures,
- QR codes with a logo over the middle: when ZXing finds a QR code but cannot decode it, the modules under a
  central logo are treated as unknown rather than wrong, which halves what they cost the error correction,
- a Data Matrix reader of its own that samples each module's average brightness, for blurry, noisy and
  faint Data Matrix codes that ZXing's detector cannot outline,
- tilt detection from edge directions, smoothed first on grainy pictures, so Aztec, PDF417 and 1D codes
  are read at any angle (ZXing gives up beyond about 10–15°),
- large images are cleaned up at half size.

Codes with only a single check digit (EAN/UPC, EAN-8, UPC-E, ITF, Codabar, DataBar) are easy to "read" out of
noise, so on these passes they are only reported when two different passes agree, and a read must also
repeat on a line a little beside the one it came from: real bars carry on, while the texture of a 2D code or
of a tilted 1D code that passed for a short UPC-E or EAN-8 does not.

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

Needs the .NET 10 SDK. The library targets .NET Standard 2.0; the CLI and the tests target .NET 10.

```
dotnet build
dotnet test
```

The tests generate barcodes in every supported symbology and file format, build PDFs with hand-written content
streams and image dictionaries, and read real-world-style samples made by reportlab, poppler, ImageMagick and
jbig2enc (`tests/samples`).

### Detection benchmark

`tests/OpenBarcode.Benchmark` renders 462 poor pictures (11 conditions such as blur, noise, fading, tilt,
perspective and phone photos, each for 7 symbologies at 6 severities) plus 24 pictures without a barcode, and
prints how many were read per condition and how many reads were wrong. CI runs it on every push and fails when
fewer pictures are read or more are read wrongly than in `tests/OpenBarcode.Benchmark/baseline.json`.

```
dotnet run -c Release --project tests/OpenBarcode.Benchmark -- --baseline tests/OpenBarcode.Benchmark/baseline.json --verbose
```

After a change that reads more, save the new result with `--save tests/OpenBarcode.Benchmark/baseline.json` and
commit it with the change.

## License

Apache-2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the libraries it builds on.

## Releasing

Move the `Unreleased` notes in [CHANGELOG.md](CHANGELOG.md) under the new version, then push a tag such as
`v0.2.0` (or `v0.3.0-beta.1` for a prerelease). The `Release` workflow builds, tests, packs and
publishes both packages to nuget.org under the InPoint.Cloud account using Trusted Publishing, then creates a
GitHub release with the packages attached. The package version comes from the tag. After a release, set
`PackageValidationBaselineVersion` in `src/OpenBarcode/OpenBarcode.csproj` to it: packing fails when the public API
breaks compatibility with that version.
