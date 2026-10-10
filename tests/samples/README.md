# Sample files

Real-world-style inputs produced by third-party tools, used by `SampleTests`.

| File | Made with | What it exercises |
| --- | --- | --- |
| `reportlab-invoice.pdf` | Python reportlab | Barcodes drawn as vector paths (Code 128, QR, EAN-13, Code 39, Data Matrix, a Code 128 rotated 90°) next to text |
| `scanned-jpeg.pdf` | poppler `pdftoppm` → ImageMagick, JPEG quality 60 | Scanned pages stored as DCT images |
| `scanned-g4.pdf` | poppler → ImageMagick, CCITT Group 4 | Bilevel fax-style scans |
| `scanned-jbig2-generic.pdf` | poppler → `jbig2` (jbig2enc), generic region | Lossless JBIG2 scans |
| `scanned-jbig2-symbol.pdf` | poppler → `jbig2 -s` (jbig2enc), symbol mode with globals | Lossy JBIG2 text coding, as many office scanners produce |
| `multipage-g4.tif` | ImageMagick | Two-page Group 4 TIFF |
| `scan-jpeg-p1.jpg` | poppler → ImageMagick | Page 1 as a plain JPEG, guards against false positives in text |
| `photo-like.jpg` | ImageMagick (rotate 7°, blur, Gaussian noise) | A camera-like capture of page 2 |

Page 1 holds a QR code and a Code 128; page 2 holds a Code 39, an EAN-13, a sideways Code 128 and a Data Matrix.
