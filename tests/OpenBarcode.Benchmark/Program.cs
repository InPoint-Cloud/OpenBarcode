using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InPoint.Cloud.OpenBarcode;
using InPoint.Cloud.OpenBarcode.Benchmark;
using InPoint.Cloud.OpenBarcode.Imaging;

const string Usage = """
    Usage: dotnet run -c Release --project tests/OpenBarcode.Benchmark -- [options]

    Reads 462 simulated poor pictures of barcodes (11 conditions × 7 symbologies × 6 severities) and 24 pictures
    without a barcode, and reports how many were read and how many reads were wrong.

    Options:
      --baseline <file>   Compare with a saved result. Exits with 1 when fewer pictures are read or there are more
                          wrong reads than in the baseline.
      --save <file>       Save this run's result as a baseline.
      --no-enhance        Run with EnhancePoorImages off.
      --verbose           List every picture that was not read or was read wrongly.
    """;

string? baselinePath = null, savePath = null;
var enhance = true;
var verbose = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--baseline": baselinePath = args[++i]; break;
        case "--save": savePath = args[++i]; break;
        case "--no-enhance": enhance = false; break;
        case "--verbose": verbose = true; break;
        default: Console.Error.WriteLine(Usage); return 2;
    }
}

var reader = new BarcodeReader(new BarcodeReaderOptions { EnhancePoorImages = enhance });
var cases = Corpus.Cases().ToList();
var outcomes = new ConcurrentBag<Outcome>();
var clock = Stopwatch.StartNew();

Parallel.ForEach(cases, c =>
{
    IReadOnlyList<BarcodeResult> results;
    try
    {
        results = reader.Read(c.Render());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{c.Id}: {ex.GetType().Name}: {ex.Message}");
        results = [];
    }
    var read = c.Text != null && results.Any(r => r.Text == c.Text);
    var wrong = results.Count(r => r.Text != c.Text);
    outcomes.Add(new Outcome(c, read, wrong, [.. results.Where(r => r.Text != c.Text).Select(r => $"{r.Format} \"{r.Text}\"")]));
});

clock.Stop();
var all = outcomes.OrderBy(o => o.Case.Id, StringComparer.Ordinal).ToList();
var result = new BenchmarkResult(
    all.Count(o => o.Read),
    all.Count(o => o.Case.Text != null),
    all.Sum(o => o.Wrong),
    [.. all.Where(o => o.Read).Select(o => o.Case.Id)]);

PrintTable(all, [.. cases.Select(c => c.Condition).Distinct()]);
Console.WriteLine();
Console.WriteLine($"Read {result.Read} of {result.Total} pictures, {result.WrongReads} wrong reads, in {clock.Elapsed.TotalSeconds:F0} s.");

if (verbose)
{
    foreach (var o in all.Where(o => o.Wrong > 0))
        Console.WriteLine($"  wrong: {o.Case.Id}: {string.Join(", ", o.WrongTexts)}");
    foreach (var o in all.Where(o => o.Case.Text != null && !o.Read))
        Console.WriteLine($"  not read: {o.Case.Id}");
}

var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (savePath != null)
{
    File.WriteAllText(savePath, JsonSerializer.Serialize(result, options) + "\n");
    Console.WriteLine($"Saved to {savePath}.");
}

if (baselinePath == null) return 0;

var baseline = JsonSerializer.Deserialize<BenchmarkResult>(File.ReadAllText(baselinePath), options)
    ?? throw new InvalidDataException("Empty baseline.");
var lost = baseline.ReadIds.Except(result.ReadIds).ToList();
var gained = result.ReadIds.Except(baseline.ReadIds).ToList();
Console.WriteLine($"Baseline: read {baseline.Read} of {baseline.Total}, {baseline.WrongReads} wrong reads.");
if (gained.Count > 0) Console.WriteLine($"Now read ({gained.Count}): {string.Join(", ", gained)}");
if (lost.Count > 0) Console.WriteLine($"No longer read ({lost.Count}): {string.Join(", ", lost)}");

var failed = result.Read < baseline.Read || result.WrongReads > baseline.WrongReads;
Console.WriteLine(failed
    ? "FAILED: fewer pictures read or more wrong reads than the baseline."
    : "OK: no fewer pictures read and no more wrong reads than the baseline.");
return failed ? 1 : 0;

static void PrintTable(List<Outcome> all, List<string> conditions)
{
    var symbologies = Corpus.Symbologies.Select(s => s.Name).ToList();
    Console.WriteLine("| Condition | " + string.Join(" | ", symbologies) + " | Total | Wrong |");
    Console.WriteLine("|---|" + string.Concat(symbologies.Select(_ => "---|")) + "---|---|");
    foreach (var condition in conditions)
    {
        var row = all.Where(o => o.Case.Condition == condition).ToList();
        var cells = symbologies.Select(s =>
        {
            var cell = row.Where(o => o.Case.Symbology == s).ToList();
            return cell.Count == 0 ? "" : $"{cell.Count(o => o.Read)}/{cell.Count}";
        });
        var positives = row.Where(o => o.Case.Text != null).ToList();
        var total = positives.Count == 0 ? "" : $"{positives.Count(o => o.Read)}/{positives.Count}";
        Console.WriteLine($"| {condition} | {string.Join(" | ", cells)} | {total} | {row.Sum(o => o.Wrong)} |");
    }
}

internal sealed record Outcome(BenchCase Case, bool Read, int Wrong, List<string> WrongTexts);

internal sealed record BenchmarkResult(int Read, int Total, int WrongReads, List<string> ReadIds);
