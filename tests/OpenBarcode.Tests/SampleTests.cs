using Xunit;

namespace OpenBarcode.Tests;

/// <summary>
/// Files made by third-party tools (reportlab, poppler, ImageMagick, jbig2enc), see tests/samples/README.md.
/// They catch what hand-built fixtures miss: real content streams, real scanner compression, real noise.
/// </summary>
public class SampleTests
{
    private static readonly BarcodeReader Reader = new();

    private static readonly string[] Page1 = { "QrCode https://example.com/pay?inv=2026-0815&amount=123.45", "Code128 INV-2026-0815" };
    private static readonly string[] Page2 =
        { "Code39 LABEL39", "Ean13 4006381333931", "Code128 ROTATED-LABEL", "DataMatrix DM-SERIAL-000123" };

    public static IEnumerable<object[]> TwoPageDocuments() => new[]
    {
        new object[] { "reportlab-invoice.pdf" },   // vector graphics
        new object[] { "scanned-jpeg.pdf" },        // DCT images
        new object[] { "scanned-g4.pdf" },          // CCITT Group 4 images
        new object[] { "scanned-jbig2-generic.pdf" },
        new object[] { "scanned-jbig2-symbol.pdf" }, // lossy JBIG2 symbol coding with shared globals
        new object[] { "multipage-g4.tif" },
    };

    [Theory]
    [MemberData(nameof(TwoPageDocuments))]
    public void Reads_every_code_on_both_pages(string file)
    {
        var results = Reader.ReadFile(Path.Combine("samples", file));

        string[] Page(int index) => results
            .Where(r => (r.PageNumber ?? r.FrameIndex + 1) == index)
            .Select(r => $"{r.Format} {r.Text}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Page1.OrderBy(s => s, StringComparer.Ordinal), Page(1));
        Assert.Equal(Page2.OrderBy(s => s, StringComparer.Ordinal), Page(2));
    }

    [Fact]
    public void Reads_a_jpeg_scan_without_false_positives()
    {
        var results = Reader.ReadFile(Path.Combine("samples", "scan-jpeg-p1.jpg")).Select(r => $"{r.Format} {r.Text}");

        Assert.Equal(Page1.OrderBy(s => s, StringComparer.Ordinal), results.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Reads_a_tilted_noisy_photo()
    {
        // Rotated 7 degrees, blurred and noisy. The sideways Code 128 is beyond what ZXing's 1D readers tolerate
        // on top of the tilt, so only the other three are expected.
        var results = Reader.ReadFile(Path.Combine("samples", "photo-like.jpg")).Select(r => $"{r.Format} {r.Text}");

        Assert.Equal(new[] { "Code39 LABEL39", "DataMatrix DM-SERIAL-000123", "Ean13 4006381333931" },
            results.OrderBy(s => s, StringComparer.Ordinal));
    }
}
