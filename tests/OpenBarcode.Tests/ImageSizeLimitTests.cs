using System.Buffers.Binary;
using System.Text;
using InPoint.Cloud.OpenBarcode.Imaging;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

public class ImageSizeLimitTests
{
    private static readonly GrayImage Qr = Fixtures.Image(ZxFormat.QR_CODE, "size limit");

    public static TheoryData<string> Encodings => ["png", "jpeg", "bmp", "tga", "tiff", "pgm"];

    private static byte[] Encode(string encoding, GrayImage image) => encoding switch
    {
        "png" => Fixtures.Png(image),
        "jpeg" => Fixtures.Jpeg(image),
        "bmp" => Fixtures.Bmp(image),
        "tga" => Fixtures.Tga(image),
        "tiff" => Fixtures.Tiff(false, image),
        "pgm" => Fixtures.Pgm(image),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Refuses_image_files_over_the_limit(string encoding)
    {
        var data = Encode(encoding, Qr);
        var pixels = (long)Qr.Width * Qr.Height;

        var atLimit = new BarcodeReader(new BarcodeReaderOptions { MaxImagePixels = pixels });
        var belowLimit = new BarcodeReader(new BarcodeReaderOptions { MaxImagePixels = pixels - 1 });

        atLimit.Read(data).Should().ContainSingle().Which.Text.Should().Be("size limit");
        FluentActions.Invoking(() => belowLimit.Read(data))
            .Should().Throw<InvalidDataException>().WithMessage("*MaxImagePixels*");
    }

    [Fact]
    public void Refuses_a_small_png_that_declares_a_huge_picture_before_decoding_it()
    {
        // 20000 × 20000 pixels would need gigabytes; the header alone is enough to refuse it.
        var png = PngHeaderOnly(20_000, 20_000);

        FluentActions.Invoking(() => new BarcodeReader().Read(png))
            .Should().Throw<InvalidDataException>().WithMessage("*20000×20000*MaxImagePixels*");
    }

    [Fact]
    public void Skips_pdf_images_over_the_limit_with_a_warning()
    {
        var pdf = new MiniPdf();
        var page = new MiniPdf.PageBuilder();
        page.Images["Im1"] = pdf.AddImage(
            $"/Width {Qr.Width} /Height {Qr.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(Qr.Pixels));
        page.DrawImage("Im1", 100, 400, 250, 250);
        pdf.AddPage(page);
        var data = pdf.Build();
        var warnings = new List<string>();

        var reader = new BarcodeReader(new BarcodeReaderOptions { MaxImagePixels = 1000, PdfReadVectorGraphics = false });

        reader.Read(data, warnings).Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().Contain("MaxImagePixels");
        new BarcodeReader().Read(data).Should().ContainSingle().Which.Text.Should().Be("size limit");
    }

    [Fact]
    public void Rejects_a_limit_that_is_not_positive()
    {
        FluentActions.Invoking(() => new BarcodeReader(new BarcodeReaderOptions { MaxImagePixels = 0 }))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>A PNG with an IHDR and an IEND chunk but no image data. CRCs are left zero; header readers do not check them.</summary>
    private static byte[] PngHeaderOnly(int width, int height)
    {
        var png = new byte[8 + 25 + 12];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8; // bit depth; colour type, compression, filter and interlace stay 0 (8-bit gray)
        Encoding.ASCII.GetBytes("IEND").CopyTo(png, 37);
        return png;
    }
}
