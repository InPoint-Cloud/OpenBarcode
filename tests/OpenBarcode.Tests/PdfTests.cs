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

        var hit = Reader.Read(pdf).Should().ContainSingle().Subject;

        hit.Text.Should().Be("VECTOR-128");
        hit.Format.Should().Be(BarcodeFormat.Code128);
        hit.Source.Should().Be(BarcodeSource.PdfVector);
        hit.PageNumber.Should().Be(1);
    }

    [Fact]
    public void Reads_vector_qr_drawn_as_one_path()
    {
        var matrix = Fixtures.Matrix(ZxFormat.QR_CODE, "vector qr, single path");
        var pdf = SinglePage((_, page) => page.FilledBarcode(matrix, 100, 400, module: 3, singlePath: true));

        Reader.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("vector qr, single path");
    }

    [Fact]
    public void Reads_vector_barcode_drawn_with_stroked_lines()
    {
        var matrix = Fixtures.Matrix(ZxFormat.EAN_13, "4006381333931");
        var pdf = SinglePage((_, page) => page.StrokedBarcode(matrix, 100, 500, module: 1.2));

        var hit = Reader.Read(pdf).Should().ContainSingle().Subject;
        hit.Text.Should().Be("4006381333931");
        hit.Format.Should().Be(BarcodeFormat.Ean13);
    }

    [Fact]
    public void Reads_vector_barcode_under_a_rotating_transform()
    {
        var matrix = Fixtures.Matrix(ZxFormat.CODE_39, "ROTATED39");
        // Rotate 90 degrees and scale by 1.5 around (300, 200).
        var pdf = SinglePage((_, page) => page.Op("q 0 1.5 -1.5 0 300 200 cm").FilledBarcode(matrix, 0, 0, module: 1).Op("Q"));

        Reader.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("ROTATED39");
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

        var hit = Reader.Read(pdf).Should().ContainSingle().Subject;
        hit.Text.Should().Be("flate image");
        hit.Source.Should().Be(BarcodeSource.PdfImage);
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

        Reader.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("jpeg in pdf");
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

        Reader.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("SCANNED-G4");
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

        Reader.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("stencil mask");
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
        results.Select(r => r.Text).Should().Equal(PdfPageTexts);
        results.Select(r => r.PageNumber).Should().Equal(new int?[] { 1, 2, 3 });

        var onlySecond = new BarcodeReader(new BarcodeReaderOptions { PdfFirstPage = 2, PdfLastPage = 2 });
        onlySecond.Read(data).Should().ContainSingle().Which.Text.Should().Be("page 2");
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

        Reader.Read(pdf).Should().ContainSingle();
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

        results.Should().BeEmpty();
        warnings.Should().Contain(w => w.Contains("JPEG 2000"));
    }

    [Fact]
    public void Ignores_pages_with_only_text()
    {
        var pdf = SinglePage((_, page) => page.Op("BT /F1 12 Tf 72 720 Td (Hello) Tj ET"));

        Reader.Read(pdf).Should().BeEmpty();
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
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("gray");
    }

    [Fact]
    public void Gray_image_with_inverting_decode_array()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "decode 1 0");
        var inverted = Fixtures.Invert(image);
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Filter /FlateDecode",
            Fixtures.Deflate(inverted.Pixels));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("decode 1 0");
    }

    [Fact]
    public void One_bit_gray_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "one bit");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode",
            Fixtures.Deflate(Fixtures.PackBits(image, blackIsOne: false)));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("one bit");
    }

    [Fact]
    public void Image_mask()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "mask");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ImageMask true /BitsPerComponent 1 /Filter /FlateDecode",
            Fixtures.Deflate(Fixtures.PackBits(image, blackIsOne: false)), prelude: "0 g");
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("mask");
    }

    [Fact]
    public void Image_mask_with_inverting_decode_array()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "mask 1 0");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ImageMask true /Decode [1 0] /BitsPerComponent 1 /Filter /FlateDecode",
            Fixtures.Deflate(Fixtures.PackBits(image, blackIsOne: true)), prelude: "0 g");
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("mask 1 0");
    }

    [Fact]
    public void Jpeg_image_with_inverting_decode_array()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "jpeg 1 0");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Filter /DCTDecode",
            Fixtures.Jpeg(Fixtures.Invert(image)));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("jpeg 1 0");
    }

    [Fact]
    public void Rgb_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "rgb");
        var rgb = image.Pixels.SelectMany(p => new[] { p, p, p }).ToArray();
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(rgb));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("rgb");
    }

    [Fact]
    public void Cmyk_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "cmyk");
        var cmyk = image.Pixels.SelectMany(p => new byte[] { 0, 0, 0, (byte)(255 - p) }).ToArray();
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(cmyk));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("cmyk");
    }

    [Fact]
    public void Indexed_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "indexed");
        var indices = image.Pixels.Select(p => p < 128 ? (byte)1 : (byte)0).ToArray();
        var pdf = PageWithImage(
            $"/Width {image.Width} /Height {image.Height} /ColorSpace [/Indexed /DeviceRGB 1 <FFFFFF000000>] /BitsPerComponent 8 /Filter /FlateDecode",
            Fixtures.Deflate(indices));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("indexed");
    }

    [Fact]
    public void Ccitt_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "ccitt");
        var pdf = PageWithImage(
            $"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 " +
            $"/Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns {image.Width} /Rows {image.Height} >>",
            Fixtures.CcittG4(image));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("ccitt");
    }

    [Fact]
    public void Jpeg_image()
    {
        var image = Fixtures.Image(ZxFormat.QR_CODE, "jpeg");
        var pdf = PageWithImage($"/Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode",
            Fixtures.Jpeg(image));
        Strict.Read(pdf).Should().ContainSingle().Which.Text.Should().Be("jpeg");
    }
}
