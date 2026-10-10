using System.Diagnostics;
using System.Text.Json;
using OpenBarcode;

const string Usage = """
    Usage: openbarcode [options] <file>...

    Reads barcodes from PDF and image files (PNG, JPEG, BMP, GIF, TIFF, TGA, PSD, PBM/PGM/PPM).

    Options:
      -f, --formats <list>   Comma-separated formats to look for, e.g. qrcode,code128,ean13.
                             Groups: default (all but msi), all, linear, 2d.
          --json             Print results as JSON.
          --fast             Quicker, less thorough search (no rotation, no inversion, no try-harder).
          --first            Stop at the first barcode in each image.
          --dpi <n>          Resolution for rendering PDF vector graphics (default 300).
          --pages <a-b>      PDF page range, e.g. 2 or 1-3.
          --no-images        Do not decode images embedded in PDFs.
          --no-vectors       Do not render PDF vector graphics.
          --password <pw>    Password for encrypted PDFs.
      -v, --verbose          Report skipped PDF images and timing on stderr.
      -h, --help             Show this help.

    Exit code: 0 when at least one barcode was found, 1 when none, 2 on errors.
    """;

var options = new BarcodeReaderOptions();
var files = new List<string>();
bool json = false, verbose = false;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value.");
        switch (a)
        {
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            case "-f" or "--formats": options.Formats = ParseFormats(Next()); break;
            case "--json": json = true; break;
            case "--fast": options.TryHarder = false; options.AutoRotate = false; options.TryInverted = false; break;
            case "--first": options.ReadMultiple = false; break;
            case "--dpi": options.PdfRenderDpi = int.Parse(Next()); break;
            case "--pages":
                var range = Next().Split('-');
                options.PdfFirstPage = int.Parse(range[0]);
                options.PdfLastPage = int.Parse(range.Length > 1 ? range[1] : range[0]);
                break;
            case "--no-images": options.PdfReadImages = false; break;
            case "--no-vectors": options.PdfReadVectorGraphics = false; break;
            case "--password": options.PdfPassword = Next(); break;
            case "-v" or "--verbose": verbose = true; break;
            default:
                if (a.StartsWith('-') && a.Length > 1) throw new ArgumentException($"Unknown option {a}.");
                files.Add(a);
                break;
        }
    }
    if (files.Count == 0) throw new ArgumentException("No input files.");
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

var reader = new BarcodeReader(options);
var report = new List<object>();
int found = 0, failed = 0;

foreach (var file in files)
{
    var watch = Stopwatch.StartNew();
    try
    {
        var data = File.ReadAllBytes(file);
        var warnings = new List<string>();
        var results = reader.Read(data, warnings);
        found += results.Count;

        if (verbose)
        {
            foreach (var w in warnings) Console.Error.WriteLine($"{file}: {w}");
            Console.Error.WriteLine($"{file}: {results.Count} barcode(s) in {watch.ElapsedMilliseconds} ms");
        }

        foreach (var r in results)
        {
            if (json)
            {
                report.Add(new
                {
                    file,
                    page = r.PageNumber,
                    frame = r.FrameIndex,
                    format = r.Format.ToString(),
                    source = r.Source.ToString(),
                    text = r.Text,
                    points = r.Points.Select(p => new[] { p.X, p.Y }),
                });
            }
            else
            {
                string where = r.PageNumber.HasValue ? $"{file}:{r.PageNumber}" : r.FrameIndex.HasValue ? $"{file}#{r.FrameIndex}" : file;
                Console.WriteLine($"{where}\t{r.Format}\t{r.Text}");
            }
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException
                                   or UglyToad.PdfPig.Core.PdfDocumentFormatException)
    {
        failed++;
        Console.Error.WriteLine($"{file}: {ex.Message}");
    }
}

if (json)
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

return failed > 0 ? 2 : found > 0 ? 0 : 1;

static BarcodeFormat ParseFormats(string list)
{
    var result = BarcodeFormat.None;
    foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string name = raw.Replace("-", "").Replace("_", "").ToLowerInvariant();
        result |= name switch
        {
            "all" => BarcodeFormat.All,
            "default" => BarcodeFormat.Default,
            "linear" or "1d" => BarcodeFormat.AllLinear & ~BarcodeFormat.Msi,
            "2d" => BarcodeFormat.All2D,
            "qr" => BarcodeFormat.QrCode,
            "ean" => BarcodeFormat.Ean13 | BarcodeFormat.Ean8,
            "upc" => BarcodeFormat.UpcA | BarcodeFormat.UpcE,
            "gs1128" => BarcodeFormat.Code128,
            _ => Enum.TryParse<BarcodeFormat>(name, ignoreCase: true, out var f) && f != BarcodeFormat.None
                ? f
                : throw new ArgumentException($"Unknown format '{raw}'."),
        };
    }
    return result;
}
