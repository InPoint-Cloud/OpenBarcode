using InPoint.Cloud.OpenBarcode.Imaging;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

public class CancellationTests
{
    private static readonly BarcodeReader Reader = new();
    private static readonly string[] PageTexts = ["page 1", "page 2", "page 3"];

    private static byte[] ThreePagePdf()
    {
        var pdf = new MiniPdf();
        for (var i = 1; i <= 3; i++)
        {
            var page = new MiniPdf.PageBuilder();
            page.FilledBarcode(Fixtures.Matrix(ZxFormat.QR_CODE, $"page {i}"), 100, 500, module: 3);
            pdf.AddPage(page);
        }
        return pdf.Build();
    }

    [Fact]
    public void Enumerate_returns_the_same_results_as_read()
    {
        var data = ThreePagePdf();

        Reader.Enumerate(data).Select(r => r.Text).Should().Equal(PageTexts);
    }

    [Fact]
    public void Enumerate_returns_each_page_before_reading_the_next()
    {
        using var cancellation = new CancellationTokenSource();
        var seen = new List<string>();

        // Cancelling after the first result stops before page 2 is read, so page 1 came back on its own.
        var enumerate = () =>
        {
            foreach (var result in Reader.Enumerate(ThreePagePdf(), cancellationToken: cancellation.Token))
            {
                seen.Add(result.Text);
                cancellation.Cancel();
            }
        };

        enumerate.Should().Throw<OperationCanceledException>();
        seen.Should().Equal("page 1");
    }

    [Fact]
    public void Enumerate_checks_its_arguments_straight_away()
    {
        FluentActions.Invoking(() => Reader.Enumerate(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => Reader.EnumerateFile(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Enumerate_file_reads_lazily_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openbarcode-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, ThreePagePdf());
        try
        {
            Reader.EnumerateFile(path).Select(r => r.PageNumber).Should().Equal(1, 2, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_cancelled_token_stops_every_kind_of_read()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var image = Fixtures.Image(ZxFormat.QR_CODE, "never read");
        var png = Fixtures.Png(image);
        var pdf = ThreePagePdf();

        using var scope = new AssertionScope();
        FluentActions.Invoking(() => Reader.Read(png, null, token)).Should().Throw<OperationCanceledException>();
        FluentActions.Invoking(() => Reader.ReadImage(png, token)).Should().Throw<OperationCanceledException>();
        FluentActions.Invoking(() => Reader.ReadPdf(pdf, null, token)).Should().Throw<OperationCanceledException>();
        FluentActions.Invoking(() => Reader.Read(image, token)).Should().Throw<OperationCanceledException>();
        FluentActions.Invoking(() => Reader.Read(new MemoryStream(png), token)).Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Cancelling_stops_the_search_inside_one_image()
    {
        // A noisy picture without a barcode runs every clean-up pass, about 11 s on one core; cancelling must not
        // wait for all of them.
        var blank = new GrayImage(2000, 2000);
        var rnd = new Random(7);
        for (var i = 0; i < blank.Pixels.Length; i++) blank.Pixels[i] = (byte)rnd.Next(100, 200);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        FluentActions.Invoking(() => Reader.Read(blank, cancellation.Token)).Should().Throw<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
