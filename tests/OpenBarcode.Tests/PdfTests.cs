using System.Text;
using Xunit;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

public class PdfTests
{
    private static readonly BarcodeReader Reader = new();
    private static readonly string[] PdfPageTexts = ["page 1", "page 2", "page 3"];

    private static byte[] SinglePage(Action<MiniPdf, MiniPdf.PageBuilder> draw)
    {
        var pdf = new MiniPdf();
        var page = new MiniPdf.PageBuilder();
        draw(pdf, page);
        pdf.AddPage(page);
        return pdf.Build();
    }

    [Fact]
    public void Reads_vector_1d_barcode_drawn_as_filled_rectangles()
    {
        var matrix = Fixtures.Matrix(ZxFormat.CODE_128, "VECTOR-128");
        var pdf = SinglePage((_, page) => page.FilledBarcode(matrix, 72, 600, module: 1.0));

        var hit = Assert.Single(Reader.Read(pdf));

        Assert.Equal("VECTOR-128", hit.Text);
        Assert.Equal(BarcodeFormat.Code128, hit.Format);
        Assert.Equal(BarcodeSource.PdfVector, hit.Source);
        Assert.Equal(1, hit.PageNumber);
    }

    [Fact]
    public void Reads_vector_qr_drawn_as_one_path()
    {
        var matrix = Fixtures.Matrix(ZxFormat.QR_CODE, "vector qr, single path");
        var pdf = SinglePage((_, page) => page.FilledBarcode(matrix, 100, 400, module: 3, singlePath: true));

        Assert.Equal("vector qr, single path", Assert.Single(Reader.Read(pdf)).Text);
    }

    [Fact]
    public void Reads_vector_barcode_drawn_with_stroked_lines()
    {
        var matrix = Fixtures.Matrix(ZxFormat.EAN_13, "4006381333931");
        var pdf = SinglePage((_, page) => page.StrokedBarcode(matrix, 100, 500, module: 1.2));

        var hit = Assert.Single(Reader.Read(pdf));
        Assert.Equal("4006381333931", hit.Text);
        Assert.Equal(BarcodeFormat.Ean13, hit.Format);
    }

    [Fact]
    public void Reads_vector_barcode_under_a_rotating_transform()
    {
        var matrix = Fixtures.Matrix(ZxFormat.CODE_39, "ROTATED39");
        // Rotate 90 degrees and scale by 1.5 around (300, 200).
        var pdf = SinglePage((_, page) => page.Op("q 0 1.5 -1.5 0 300 200 cm").FilledBarcode(matrix, 0, 0, module: 1).Op("Q"));

        Assert.Equal("ROTATED39", Assert.Single(Reader.Read(pdf)).Text);
    }

    [Fact]
    public void Reads_embedded_flate_gray_image()
    {
        var image = Fixtures.Image(ZxFormat.DATA_MATRIX, "flate image", moduleSize: 2);
        var pdf = SinglePage((doc, page) =>
        {
            page.Images["Im1"] = doc.AddImage(
                $"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
                Fixtures.Deflate(image.Pixels));
            page.DrawImage("Im1", 100, 500, 150, 150);
        });

        var hit = Assert.Single(Reader.Read(pdf));
        Assert.Equal("flate image", hit.Text);
        Assert.Equal(BarcodeSource.PdfImage, hit.Source);
    }

    [Fact]
    public void Reads_embedded_jpeg_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "jpeg in pdf");
        var jpeg = Fixtures.Jpeg(image, quality: 80);
        var pdf = SinglePage((doc, page) =>
        {
            page.Images["Im1"] = doc.AddImage(
                $"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
            page.DrawImage("Im1", 100, 500, 200, 200);
        });

        Assert.Equal("jpeg in pdf", Assert.Single(Reader.Read(pdf)).Text);
    }

    [Fact]
    public void Reads_embedded_ccitt_group4_scan()
    {
        // A bilevel "scan" of a page with a barcode, compressed the way scanners write PDFs.
        var scan = new Imaging.GrayImage(1240, 1754);
        Fixtures.Blit(scan, Fixtures.Image(ZxFormat.CODE_128, "SCANNED-G4", moduleSize: 3), 100, 200);
        var g4 = Fixtures.CcittG4(scan);
        var pdf = SinglePage((doc, page) =>
        {
            page.Images["Im1"] = doc.AddImage(
                $"/Width {scan.Width} /Height {scan.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 " +
                $"/Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns {scan.Width} /Rows {scan.Height} >>", g4);
            page.DrawImage("Im1", 0, 0, 595, 842);
        });

        Assert.Equal("SCANNED-G4", Assert.Single(Reader.Read(pdf)).Text);
    }

    [Fact]
    public void Reads_embedded_image_mask()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "stencil mask");
        // Image masks paint where the sample is 0.
        var bits = Fixtures.PackBits(image, blackIsOne: false);
        var pdf = SinglePage((doc, page) =>
        {
            page.Images["Im1"] = doc.AddImage(
                $"/Width {image.Width} /Height {image.Height} /ImageMask true /BitsPerComponent 1 /Filter /FlateDecode",
                Fixtures.Deflate(bits));
            page.Op("0 g").DrawImage("Im1", 100, 500, 200, 200);
        });

        Assert.Equal("stencil mask", Assert.Single(Reader.Read(pdf)).Text);
    }

    [Fact]
    public void Reports_page_numbers_across_a_multipage_document()
    {
        var pdf = new MiniPdf();
        for (var i = 1; i <= 3; i++)
        {
            var page = new MiniPdf.PageBuilder();
            page.FilledBarcode(Fixtures.Matrix(ZxFormat.QR_CODE, $"page {i}"), 100, 500, module: 3);
            pdf.AddPage(page);
        }
        var data = pdf.Build();

        var results = Reader.Read(data);
        Assert.Equal(PdfPageTexts, results.Select(r => r.Text));
        Assert.Equal(new int?[] { 1, 2, 3 }, results.Select(r => r.PageNumber));

        var onlySecond = new BarcodeReader(new BarcodeReaderOptions { PdfFirstPage = 2, PdfLastPage = 2 });
        Assert.Equal("page 2", Assert.Single(onlySecond.Read(data)).Text);
    }

    [Fact]
    public void Same_code_as_image_and_vector_on_one_page_is_reported_once()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "duplicate");
        var pdf = SinglePage((doc, page) =>
        {
            page.Images["Im1"] = doc.AddImage(
                $"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
                Fixtures.Deflate(image.Pixels));
            page.DrawImage("Im1", 50, 600, 150, 150);
            page.FilledBarcode(Fixtures.Matrix(ZxFormat.QR_CODE, "duplicate"), 300, 300, module: 3);
        });

        Assert.Single(Reader.Read(pdf));
    }

    [Fact]
    public void Warns_about_images_it_cannot_decode()
    {
        var pdf = SinglePage((doc, page) =>
        {
            page.Images["Im1"] = doc.AddImage("/Width 10 /Height 10 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode",
                [0, 1, 2, 3]);
            page.DrawImage("Im1", 0, 0, 100, 100);
        });
        var warnings = new List<string>();

        var results = Reader.ReadPdf(pdf, warnings);

        Assert.Empty(results);
        Assert.Contains(warnings, w => w.Contains("JPEG 2000"));
    }

    [Fact]
    public void Ignores_pages_with_only_text()
    {
        var pdf = SinglePage((_, page) => page.Op("BT /F1 12 Tf 72 720 Td (Hello) Tj ET"));

        Assert.Empty(Reader.Read(pdf));
    }
}

public class PdfImagePolarityTests
{
    // With inversion off, a code only decodes if the image came out with the right polarity.
    private static readonly BarcodeReader Strict = new(new BarcodeReaderOptions { TryInverted = false, PdfReadVectorGraphics = false });

    private static byte[] PageWithImage(string dictionary, byte[] data, string prelude = "")
    {
        var pdf = new MiniPdf();
        var page = new MiniPdf.PageBuilder();
        page.Images["Im1"] = pdf.AddImage(dictionary, data);
        if (prelude.Length > 0) page.Op(prelude);
        page.DrawImage("Im1", 100, 400, 250, 250);
        pdf.AddPage(page);
        return pdf.Build();
    }

    [Fact]
    public void Gray_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "gray");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(image.Pixels));
        Assert.Equal("gray", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Gray_image_with_inverting_decode_array()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "decode 1 0");
        var inverted = Fixtures.Invert(image);
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Filter /FlateDecode",
            Fixtures.Deflate(inverted.Pixels));
        Assert.Equal("decode 1 0", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void One_bit_gray_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "one bit");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode",
            Fixtures.Deflate(Fixtures.PackBits(image, blackIsOne: false)));
        Assert.Equal("one bit", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Image_mask()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "mask");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ImageMask true /BitsPerComponent 1 /Filter /FlateDecode",
            Fixtures.Deflate(Fixtures.PackBits(image, blackIsOne: false)), prelude: "0 g");
        Assert.Equal("mask", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Image_mask_with_inverting_decode_array()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "mask 1 0");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ImageMask true /Decode [1 0] /BitsPerComponent 1 /Filter /FlateDecode",
            Fixtures.Deflate(Fixtures.PackBits(image, blackIsOne: true)), prelude: "0 g");
        Assert.Equal("mask 1 0", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Jpeg_image_with_inverting_decode_array()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "jpeg 1 0");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Filter /DCTDecode",
            Fixtures.Jpeg(Fixtures.Invert(image)));
        Assert.Equal("jpeg 1 0", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Rgb_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "rgb");
        var rgb = image.Pixels.SelectMany(p => new[] { p, p, p }).ToArray();
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(rgb));
        Assert.Equal("rgb", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Cmyk_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "cmyk");
        var cmyk = image.Pixels.SelectMany(p => new byte[] { 0, 0, 0, (byte)(255 - p) }).ToArray();
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(cmyk));
        Assert.Equal("cmyk", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Indexed_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "indexed");
        var indices = image.Pixels.Select(p => p < 128 ? (byte)1 : (byte)0).ToArray();
        var pdf = PageWithImage(
            $"/Width {image.Width} /Height {image.Height} /ColorSpace [/Indexed /DeviceRGB 1 <FFFFFF000000>] /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(indices));
        Assert.Equal("indexed", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Ccitt_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "ccitt");
        var pdf = PageWithImage(
            $"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 " +
            $"/Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns {image.Width} /Rows {image.Height} >>",
            Fixtures.CcittG4(image));
        Assert.Equal("ccitt", Assert.Single(Strict.Read(pdf)).Text);
    }

    [Fact]
    public void Jpeg_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "jpeg");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode",
            Fixtures.Jpeg(image));
        Assert.Equal("jpeg", Assert.Single(Strict.Read(pdf)).Text);
    }
}
