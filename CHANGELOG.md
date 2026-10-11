# Changelog

All notable changes to the `InPoint.Cloud.OpenBarcode` library and the `InPoint.Cloud.OpenBarcode.Cli` tool.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `BarcodeReader.Enumerate` and `EnumerateFile` return each page's barcodes (or each TIFF page's) as soon as that
  page is read, so long documents show results early and can be stopped partway.
- Every `Read` method has an overload that takes a `CancellationToken`. It is checked between pages and before every
  decoding pass, so even a slow search over a single image stops promptly.
- `tests/OpenBarcode.Benchmark`, a detection benchmark of 462 simulated poor pictures and 24 without a barcode.
  CI fails when a change reads fewer pictures or makes more wrong reads.

## [0.2.0] - 2026-10-11

### Added

- Clean-up passes for poor photos and scans, run only when the normal passes find nothing: local lighting
  correction, adaptive thresholding, denoising, sharpening, tilt correction for Aztec, PDF417 and 1D codes, and
  averaging along the bars of grainy 1D codes. On the project's synthetic benchmark of degraded images the
  read rate went from 93 to 385 of 462, with no false reads. Turn them off with
  `BarcodeReaderOptions.EnhancePoorImages = false` or `--no-enhance` (#9, #17).
- QR codes with a logo over the centre: the covered modules are treated as unknown, so codes at error
  correction level M read with a logo up to about 45% of the width (#10).
- A Data Matrix sampler of its own for blurry, noisy and faint Data Matrix codes (#10).
- `BarcodeReaderOptions.MaxImagePixels` (default 100 million) and `--max-pixels`: images larger than this are
  refused from their header, before any memory is allocated. Before, a 430 KB PNG declaring 20000×20000 pixels
  made the reader use about 6 GB of memory. Embedded PDF images over the limit are skipped with a warning.
  `ImageDecoder.Decode(byte[], long)` takes the limit too.

### Changed

- 1D codes with a single check digit (EAN/UPC, EAN-8, UPC-E, ITF, Codabar, DataBar) found by the clean-up
  passes are only reported when two passes agree and the bars continue beside the read line, which stops
  QR-code and noise texture from passing as UPC-E or EAN-8 (#10, #17).
- `ImageDecoder.Decode(byte[])` now refuses images larger than 100 million pixels.
- The library ships for .NET Standard 2.0 only; .NET 8 and later use that build (#12).
- The CLI tool targets .NET 10 and needs the .NET 10 runtime or later (#15).

## [0.1.0] - 2026-10-10

First release: QR Code, Data Matrix, PDF417, Aztec, MaxiCode and the common 1D symbologies, read from PNG, JPEG,
BMP, GIF, TIFF (incl. CCITT G3/G4), TGA, PSD and PNM images and from PDFs (embedded images incl. JBIG2 and CCITT,
and barcodes drawn as vector graphics), with managed code only.

[Unreleased]: https://github.com/InPoint-Cloud/OpenBarcode/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/InPoint-Cloud/OpenBarcode/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/InPoint-Cloud/OpenBarcode/releases/tag/v0.1.0
