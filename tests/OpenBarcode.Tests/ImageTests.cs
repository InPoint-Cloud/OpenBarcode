using InPoint.Cloud.OpenBarcode.Imaging;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

public class ImageTests
{
    private static readonly BarcodeReader Reader = new();
    private static readonly string[] TiffPageTexts = ["page one", "page two", "page one"];
    private static readonly string[] MixedCanvasTexts = ["4006381333931", "dm code", "first qr", "second qr"];

    [Theory]
    [InlineData(ZxFormat.QR_CODE, "https://example.com/?id=42", BarcodeFormat.QrCode)]
    [InlineData(ZxFormat.DATA_MATRIX, "DATAMATRIX-12345", BarcodeFormat.DataMatrix)]
    [InlineData(ZxFormat.AZTEC, "Aztec payload", BarcodeFormat.Aztec)]
    [InlineData(ZxFormat.PDF_417, "PDF417 stacked code", BarcodeFormat.Pdf417)]
    [InlineData(ZxFormat.CODE_128, "CODE128-abc-0815", BarcodeFormat.Code128)]
    [InlineData(ZxFormat.CODE_39, "CODE39TEST", BarcodeFormat.Code39)]
    [InlineData(ZxFormat.CODE_93, "CODE93TEST", BarcodeFormat.Code93)]
    [InlineData(ZxFormat.CODABAR, "A123456B", BarcodeFormat.Codabar)]
    [InlineData(ZxFormat.EAN_13, "4006381333931", BarcodeFormat.Ean13)]
    [InlineData(ZxFormat.EAN_8, "96385074", BarcodeFormat.Ean8)]
    [InlineData(ZxFormat.UPC_A, "036000291452", BarcodeFormat.UpcA)]
    [InlineData(ZxFormat.UPC_E, "01234565", BarcodeFormat.UpcE)]
    [InlineData(ZxFormat.ITF, "12345678901231", BarcodeFormat.Itf)]
    public void Reads_every_format_from_png(ZxFormat zx, string text, BarcodeFormat expected)
    {
        var png = Fixtures.Png(Fixtures.Image(zx, text, moduleSize: 3));

        var results = Reader.Read(png);

        var hit = results.Should().ContainSingle().Subject;
        using var scope = new AssertionScope();
        hit.Format.Should().Be(expected);
        hit.Text.Should().Be(expected == BarcodeFormat.Codabar ? text.Trim('A', 'B') : text);
        hit.Source.Should().Be(BarcodeSource.Image);
        hit.PageNumber.Should().BeNull();
    }

    public static IEnumerable<object[]> Encodings()
    {
        yield return new object[] { "png" };
        yield return new object[] { "jpeg" };
        yield return new object[] { "bmp" };
        yield return new object[] { "tga" };
        yield return new object[] { "tiff-lzw" };
        yield return new object[] { "tiff-g4" };
        yield return new object[] { "pgm" };
        yield return new object[] { "pbm" };
        yield return new object[] { "png-transparent" };
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Reads_qr_and_code128_from_each_file_format(string encoding)
    {
        var qr = Fixtures.Image(ZxFormat.QR_CODE, "format:" + encoding, moduleSize: 4);
        var bars = Fixtures.Image(ZxFormat.CODE_128, "BARS-" + encoding.ToUpperInvariant(), moduleSize: 2);
        var canvas = new GrayImage(Math.Max(qr.Width, bars.Width), qr.Height + bars.Height);
        Fixtures.Blit(canvas, qr, 0, 0);
        Fixtures.Blit(canvas, bars, 0, qr.Height);

        var file = encoding switch
        {
            "png" => Fixtures.Png(canvas),
            "jpeg" => Fixtures.Jpeg(canvas, quality: 75),
            "bmp" => Fixtures.Bmp(canvas),
            "tga" => Fixtures.Tga(canvas),
            "tiff-lzw" => Fixtures.Tiff(false, canvas),
            "tiff-g4" => Fixtures.Tiff(true, canvas),
            "pgm" => Fixtures.Pgm(canvas),
            "pbm" => Fixtures.Pbm(canvas),
            "png-transparent" => Fixtures.TransparentPng(canvas),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

        var texts = Reader.Read(file).Select(r => r.Text).OrderBy(t => t).ToArray();

        texts.Should().Equal(new[] { "BARS-" + encoding.ToUpperInvariant(), "format:" + encoding });
    }

    [Fact]
    public void Reads_every_page_of_a_multipage_tiff()
    {
        var tiff = Fixtures.Tiff(true,
            Fixtures.Image(ZxFormat.QR_CODE, "page one"),
            Fixtures.Image(ZxFormat.QR_CODE, "page two"),
            Fixtures.Image(ZxFormat.QR_CODE, "page one"));

        var results = Reader.Read(tiff);

        using var scope = new AssertionScope();
        results.Select(r => r.Text).Should().Equal(TiffPageTexts);
        results.Select(r => r.FrameIndex).Should().Equal(new int?[] { 0, 1, 2 });
    }

    [Fact]
    public void Reads_rotated_and_inverted_codes()
    {
        var rotated = Fixtures.Rotate90(Fixtures.Image(ZxFormat.CODE_128, "ROTATED-128", moduleSize: 3));
        var inverted = Fixtures.Invert(Fixtures.Image(ZxFormat.QR_CODE, "inverted qr"));

        Reader.Read(Fixtures.Png(rotated)).Should().ContainSingle().Which.Text.Should().Be("ROTATED-128");
        Reader.Read(Fixtures.Png(inverted)).Should().ContainSingle().Which.Text.Should().Be("inverted qr");
    }

    [Fact]
    public void Reads_a_tiny_code_with_no_quiet_zone()
    {
        // One pixel per module and the image cropped right to the symbol, as barcodes pasted into documents often are.
        var tight = Fixtures.Image(ZxFormat.QR_CODE, "tight crop", moduleSize: 1, quietModules: 0);

        Reader.Read(Fixtures.Png(tight)).Should().ContainSingle().Which.Text.Should().Be("tight crop");
    }

    [Fact]
    public void Finds_several_codes_in_one_image()
    {
        var canvas = new GrayImage(1400, 900);
        Fixtures.Blit(canvas, Fixtures.Image(ZxFormat.QR_CODE, "first qr"), 20, 20);
        Fixtures.Blit(canvas, Fixtures.Image(ZxFormat.QR_CODE, "second qr"), 800, 20);
        Fixtures.Blit(canvas, Fixtures.Image(ZxFormat.EAN_13, "4006381333931", moduleSize: 3), 20, 550);
        Fixtures.Blit(canvas, Fixtures.Image(ZxFormat.DATA_MATRIX, "dm code", moduleSize: 5), 900, 550);

        var texts = Reader.Read(Fixtures.Png(canvas)).Select(r => r.Text).OrderBy(t => t, StringComparer.Ordinal);

        texts.Should().Equal(MixedCanvasTexts);
    }

    [Fact]
    public void Format_filter_ignores_other_symbologies()
    {
        var canvas = new GrayImage(900, 900);
        Fixtures.Blit(canvas, Fixtures.Image(ZxFormat.QR_CODE, "qr only"), 20, 20);
        Fixtures.Blit(canvas, Fixtures.Image(ZxFormat.CODE_128, "IGNORED", moduleSize: 2), 20, 600);
        var reader = new BarcodeReader(new BarcodeReaderOptions { Formats = BarcodeFormat.QrCode });

        var hit = reader.Read(Fixtures.Png(canvas)).Should().ContainSingle().Subject;

        hit.Text.Should().Be("qr only");
    }

    [Fact]
    public void Reads_a_bitmap_from_raw_bgra_pixels()
    {
        var gray = Fixtures.Image(ZxFormat.QR_CODE, "from pixels");
        var bgra = new byte[gray.Pixels.Length * 4];
        for (var i = 0; i < gray.Pixels.Length; i++)
        {
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = gray.Pixels[i];
            bgra[i * 4 + 3] = 255;
        }

        var results = Reader.Read(GrayImage.FromPixels(bgra, gray.Width, gray.Height, PixelLayout.Bgra32));

        results.Should().ContainSingle().Which.Text.Should().Be("from pixels");
    }

    [Fact]
    public void Returns_nothing_for_an_image_without_barcodes()
    {
        Reader.Read(Fixtures.Png(new GrayImage(300, 200))).Should().BeEmpty();
    }

    [Fact]
    public void Rejects_unknown_content()
    {
        FluentActions.Invoking(() => Reader.Read([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]))
            .Should().Throw<NotSupportedException>();
    }
}
