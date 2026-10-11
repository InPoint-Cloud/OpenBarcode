using InPoint.Cloud.OpenBarcode.Imaging;
using ZXing.QrCode.Internal;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

/// <summary>Photos and scans of poor quality; each case failed before the clean-up passes existed.</summary>
public class PoorImageTests
{
    private static readonly BarcodeReader Reader = new();

    private static Degraded Condition(string name) => name switch
    {
        // Grey edges from a camera-like resample at a fractional scale; nearest-neighbour upscaling broke these.
        "anti-aliased" => new Degraded { PixelsPerModule = 3.21 },
        "blurry" => new Degraded { Blur = 1.8 },
        "very blurry" => new Degraded { PixelsPerModule = 4, Blur = 2.4 },
        "faded" => new Degraded { Ink = 0.52f, Paper = 0.59f, Blur = 0.5, Noise = 2 },
        "dark" => new Degraded { Ink = 0.02f, Paper = 0.18f, Blur = 0.5, Noise = 3 },
        "shadowed" => new Degraded { LightFalloff = 0.85, Blur = 0.5, Noise = 3 },
        "noisy" => new Degraded { Ink = 0.1f, Paper = 0.9f, Blur = 0.5, Noise = 40, Seed = 3 },
        "specks" => new Degraded { Specks = 0.05, Seed = 5 },
        "tilted" => new Degraded { Angle = 30, Blur = 0.5 },
        "keystone" => new Degraded { Angle = 5, Keystone = 0.3, Blur = 0.5 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("anti-aliased", ZxFormat.EAN_13, "4006381333931")]
    [InlineData("anti-aliased", ZxFormat.CODE_39, "PART42X")]
    [InlineData("blurry", ZxFormat.DATA_MATRIX, "LOT 2026-10 SN 000123")]
    [InlineData("very blurry", ZxFormat.EAN_13, "4006381333931")]
    [InlineData("faded", ZxFormat.DATA_MATRIX, "LOT 2026-10 SN 000123")]
    [InlineData("noisy", ZxFormat.DATA_MATRIX, "LOT 2026-10 SN 000123")]
    [InlineData("faded", ZxFormat.QR_CODE, "https://example.com/order?id=4711")]
    [InlineData("faded", ZxFormat.CODE_128, "SHIP-00012345")]
    [InlineData("faded", ZxFormat.AZTEC, "Aztec ticket 0815-4711")]
    [InlineData("dark", ZxFormat.CODE_128, "SHIP-00012345")]
    [InlineData("shadowed", ZxFormat.EAN_13, "4006381333931")]
    [InlineData("noisy", ZxFormat.CODE_128, "SHIP-00012345")]
    [InlineData("noisy", ZxFormat.QR_CODE, "https://example.com/order?id=4711")]
    [InlineData("specks", ZxFormat.QR_CODE, "https://example.com/order?id=4711")]
    [InlineData("tilted", ZxFormat.AZTEC, "Aztec ticket 0815-4711")]
    [InlineData("tilted", ZxFormat.PDF_417, "PDF417 payload 1234567890")]
    [InlineData("tilted", ZxFormat.CODE_128, "SHIP-00012345")]
    [InlineData("keystone", ZxFormat.PDF_417, "PDF417 payload 1234567890")]
    public void Reads_poor_pictures(string condition, ZxFormat format, string text)
    {
        var image = Condition(condition).Render(format, text);

        var results = Reader.Read(Fixtures.Png(image));

        var hit = results.Should().ContainSingle().Subject;
        hit.Text.Should().Be(text);
    }

    /// <summary>The payload of the Swiss QR-bill sample, which prints a cross over the middle at level M.</summary>
    private const string QrBill = "SPC\n0200\n1\nCH2231989000007611146\nS\nKrankenkasse fit&munter\nAm Wasser\n1\n3000\nBern\nCH\n" +
        "\n\n\n\n\n\n\n121.00\nCHF\nS\nSarah Beispiel\nMustergasse\n1\n3600\nThun\nCH\nQRR\n000037015881325831368099972\n" +
        "Monatspramie Juli 2020\nEPD\n//S1/10/10201409/11/200630/20/140.000-53/30/102673831/31/200630/32/7.7/33/7.7:9.30/40/0:30";

    [Theory]
    [InlineData(0.40, 3.0, 0, 0.4, 0)]   // large logo, clean print
    [InlineData(0.35, 2.5, 4, 1.0, 8)]   // logo in a blurred, tilted, grainy photo
    public void Reads_qr_codes_with_a_logo(double logo, double pixelsPerModule, double angle, double blur, double noise)
    {
        var image = new Degraded
        {
            QrLevel = ErrorCorrectionLevel.M, Logo = logo, PixelsPerModule = pixelsPerModule, Angle = angle, Blur = blur, Noise = noise, Seed = 1,
        }.Render(ZxFormat.QR_CODE, QrBill);

        var hit = Reader.Read(image).Should().ContainSingle().Subject;

        hit.Text.Should().Be(QrBill);
    }

    [Fact]
    public void Texture_of_an_unreadable_qr_code_is_not_taken_for_a_1d_code()
    {
        // A QR code too damaged to decode in a poor photo; its module texture used to come out as a UPC-A.
        var image = new Degraded
        {
            QrLevel = ErrorCorrectionLevel.M, Logo = 0.3, PixelsPerModule = 3, Angle = 4, Blur = 1.2, Noise = 12, Seed = 1,
        }.Render(ZxFormat.QR_CODE, QrBill);

        Reader.Read(image).Should().NotContain(r => r.Format != BarcodeFormat.QrCode);
    }

    [Fact]
    public void Points_of_a_deskewed_code_are_in_the_original_image()
    {
        var image = new Degraded { Angle = 30, Blur = 0.5 }.Render(ZxFormat.PDF_417, "PDF417 payload 1234567890");

        var hit = Reader.Read(image).Should().ContainSingle().Subject;

        hit.Points.Should().NotBeEmpty();
        // The symbol sits in the middle of the canvas; points mapped wrongly would land far off or outside.
        foreach (var p in hit.Points)
        {
            p.X.Should().BeInRange(image.Width * 0.1f, image.Width * 0.9f);
            p.Y.Should().BeInRange(image.Height * 0.1f, image.Height * 0.9f);
        }
    }

    [Fact]
    public void Survives_images_that_crash_zxings_maxicode_reader()
    {
        // ZXing's MaxiCode reader threw IndexOutOfRangeException on this clean picture, failing the whole read.
        var image = new Degraded { PixelsPerModule = 3 }.Render(ZxFormat.CODE_128, "SHIP-00012345");

        var hit = Reader.Read(image).Should().ContainSingle().Subject;

        hit.Text.Should().Be("SHIP-00012345");
    }

    [Fact]
    public void Clean_up_can_be_turned_off()
    {
        var image = Condition("faded").Render(ZxFormat.CODE_128, "SHIP-00012345");
        var reader = new BarcodeReader(new BarcodeReaderOptions { EnhancePoorImages = false });

        reader.Read(image).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Finds_nothing_in_noise_and_scribbles(int seed)
    {
        var rnd = new Random(seed);
        int w = 400 + rnd.Next(400), h = 400 + rnd.Next(400);
        var pixels = new byte[w * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)Math.Clamp(128 + (int)((rnd.NextDouble() - 0.5) * 2 * (40 + 20 * seed)), 0, 255);
        for (var s = 0; s < 40; s++)
        {
            int x0 = rnd.Next(w), y0 = rnd.Next(h), sw = rnd.Next(1, 10), sh = rnd.Next(1, 120);
            var v = (byte)rnd.Next(256);
            for (var y = y0; y < Math.Min(h, y0 + sh); y++)
                for (var x = x0; x < Math.Min(w, x0 + sw); x++) pixels[y * w + x] = v;
        }

        Reader.Read(new GrayImage(w, h, pixels)).Should().BeEmpty();
    }
}
