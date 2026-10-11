using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InPoint.Cloud.OpenBarcode;

const string Usage = """
    Usage: openbarcode [options] <file>...

    Reads barcodes from PDF and image files (PNG, JPEG, BMP, GIF, TIFF, TGA, PSD, PBM/PGM/PPM).

    Options:
      -f, --formats <list>   Comma-separated formats to look for, e.g. qrcode,code128,ean13.
                             Groups: default (all but msi), all, linear, 2d.
          --json             Print results as JSON.
          --fast             Quicker, less thorough search (no rotation, inversion, try-harder or clean-up).
          --first            Stop at the first barcode in each image.
          --no-enhance       Skip the clean-up passes for poor photos and scans (faster on clean input).
          --dpi <n>          Resolution for rendering PDF vector graphics (default 300).
          --pages <a-b>      PDF page range, e.g. 2 or 1-3.
          --no-images        Do not decode images embedded in PDFs.
          --no-vectors       Do not render PDF vector graphics.
          --password <pw>    Password for encrypted PDFs.
      -v, --verbose          Report skipped PDF images and timing on stderr.
      -h, --help             Show this help.

    Exit code: 0 when at least one barcode was found, 1 when none, 2 on errors.
    """;

CommandLine command;
BarcodeReader reader;

try
{
    command = CommandLine.Parse(args);
    if (command.Help)
    {
        Console.WriteLine(Usage);
        return 0;
    }
    // Validates the options, e.g. a --dpi out of range.
    reader = new BarcodeReader(command.Options);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

var report = new List<object>();
int found = 0, failed = 0;

foreach (var file in command.Files)
{
    try
    {
        found += ReadFile(reader, file, command, report);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException
                                   or UglyToad.PdfPig.Core.PdfDocumentFormatException)
    {
        failed++;
        Console.Error.WriteLine($"{file}: {ex.Message}");
    }
}

if (command.Json)
    Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions.Indented));

if (failed > 0) return 2;
return found > 0 ? 0 : 1;

/// <summary>Reads one file and prints its barcodes, or adds them to the JSON report.</summary>
/// <returns>The number of barcodes found.</returns>
static int ReadFile(BarcodeReader reader, string file, CommandLine command, List<object> report)
{
    var watch = Stopwatch.StartNew();
    var data = File.ReadAllBytes(file);
    var warnings = new List<string>();
    var results = reader.Read(data, warnings);

    if (command.Verbose)
    {
        foreach (var w in warnings) Console.Error.WriteLine($"{file}: {w}");
        Console.Error.WriteLine($"{file}: {results.Count} barcode(s) in {watch.ElapsedMilliseconds} ms");
    }

    foreach (var r in results)
    {
        if (command.Json)
            report.Add(ToJson(file, r));
        else
            Console.WriteLine($"{Where(file, r)}\t{r.Format}\t{r.Text}");
    }
    return results.Count;
}

static object ToJson(string file, BarcodeResult r) => new
{
    file,
    page = r.PageNumber,
    frame = r.FrameIndex,
    format = r.Format.ToString(),
    source = r.Source.ToString(),
    text = r.Text,
    points = r.Points.Select(p => new[] { p.X, p.Y }),
};

/// <summary>The file, with the page or frame the barcode is on when there is one.</summary>
static string Where(string file, BarcodeResult r)
{
    if (r.PageNumber.HasValue) return $"{file}:{r.PageNumber}";
    if (r.FrameIndex.HasValue) return $"{file}#{r.FrameIndex}";
    return file;
}

/// <summary>The parsed command line.</summary>
internal sealed class CommandLine
{
    public BarcodeReaderOptions Options { get; } = new();

    public List<string> Files { get; } = [];

    public bool Json { get; private set; }

    public bool Verbose { get; private set; }

    /// <summary>Whether help was asked for; the remaining arguments are then left unparsed.</summary>
    public bool Help { get; private set; }

    public static CommandLine Parse(string[] args)
    {
        var command = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value.");
            command.Apply(a, Next);
            if (command.Help) return command;
        }
        if (command.Files.Count == 0) throw new ArgumentException("No input files.");
        return command;
    }

    /// <summary>Applies one argument; <paramref name="next"/> consumes the option's value.</summary>
    private void Apply(string a, Func<string> next)
    {
        switch (a)
        {
            case "-h" or "--help": Help = true; break;
            case "-f" or "--formats": Options.Formats = ParseFormats(next()); break;
            case "--json": Json = true; break;
            case "--fast": Options.TryHarder = false; Options.AutoRotate = false; Options.TryInverted = false; Options.EnhancePoorImages = false; break;
            case "--first": Options.ReadMultiple = false; break;
            case "--no-enhance": Options.EnhancePoorImages = false; break;
            case "--dpi": Options.PdfRenderDpi = ParseInt(next()); break;
            case "--pages": ParsePages(next()); break;
            case "--no-images": Options.PdfReadImages = false; break;
            case "--no-vectors": Options.PdfReadVectorGraphics = false; break;
            case "--password": Options.PdfPassword = next(); break;
            case "-v" or "--verbose": Verbose = true; break;
            default:
                if (a.StartsWith('-') && a.Length > 1) throw new ArgumentException($"Unknown option {a}.");
                Files.Add(a);
                break;
        }
    }

    private void ParsePages(string value)
    {
        var range = value.Split('-');
        Options.PdfFirstPage = ParseInt(range[0]);
        Options.PdfLastPage = ParseInt(range.Length > 1 ? range[1] : range[0]);
    }

    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static BarcodeFormat ParseFormats(string list)
    {
        var result = BarcodeFormat.None;
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.Replace("-", "").Replace("_", "").ToLowerInvariant();
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
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
