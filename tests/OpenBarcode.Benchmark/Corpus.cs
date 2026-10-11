using InPoint.Cloud.OpenBarcode.Imaging;
using InPoint.Cloud.OpenBarcode.Tests;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Benchmark;

/// <summary>One picture: a symbol rendered under a condition, or a picture with no barcode when Text is null.</summary>
internal sealed record BenchCase(string Id, string Condition, string Symbology, string? Text, Func<GrayImage> Render);

/// <summary>
/// The benchmark pictures. Each condition is rendered at six severities, from mild (0) to severe (5), so the
/// table shows where reading starts to fail. Everything is deterministic; changing a case changes the baseline.
/// </summary>
internal static class Corpus
{
    public static readonly (string Name, ZxFormat Format, string Text)[] Symbologies =
    [
        ("QR", ZxFormat.QR_CODE, "https://example.com/order?id=4711"),
        ("DataMatrix", ZxFormat.DATA_MATRIX, "LOT 2026-10 SN 000123"),
        ("Aztec", ZxFormat.AZTEC, "Aztec ticket 0815-4711"),
        ("PDF417", ZxFormat.PDF_417, "PDF417 payload 1234567890"),
        ("Code128", ZxFormat.CODE_128, "SHIP-00012345"),
        ("Code39", ZxFormat.CODE_39, "PART42X"),
        ("EAN13", ZxFormat.EAN_13, "4006381333931"),
        ("EAN8", ZxFormat.EAN_8, "96385074"),
        ("UPCE", ZxFormat.UPC_E, "01234565"),
    ];

    private const int Severities = 6;

    /// <summary>Interpolates from <paramref name="mild"/> at severity 0 to <paramref name="severe"/> at the last severity.</summary>
    private static double Lerp(double mild, double severe, int s) => mild + (severe - mild) * s / (Severities - 1);

    private static readonly (string Name, Func<int, Degraded> At)[] Conditions =
    [
        ("small modules", s => new Degraded { PixelsPerModule = Lerp(2.2, 1.2, s), Seed = s }),
        ("blurry", s => new Degraded { PixelsPerModule = 4, Blur = Lerp(1.2, 3.2, s), Seed = s }),
        ("faded", s => new Degraded { Ink = (float)Lerp(0.40, 0.56, s), Paper = 0.62f, Blur = 0.5, Noise = 2, Seed = s }),
        ("dark", s => new Degraded { Ink = 0.02f, Paper = (float)Lerp(0.30, 0.12, s), Blur = 0.5, Noise = 3, Seed = s }),
        ("shadowed", s => new Degraded { LightFalloff = Lerp(0.6, 0.95, s), Blur = 0.5, Noise = 3, Seed = s }),
        ("noisy", s => new Degraded { Ink = 0.1f, Paper = 0.9f, Blur = 0.5, Noise = Lerp(30, 90, s), Seed = s }),
        ("specks", s => new Degraded { Specks = Lerp(0.03, 0.13, s), Seed = s }),
        ("tilted", s => new Degraded { Angle = Lerp(12, 42, s), Blur = 0.5, Seed = s }),
        ("perspective", s => new Degraded { Angle = 5, Keystone = Lerp(0.15, 0.45, s), Blur = 0.5, Seed = s }),
        ("phone photo", s => new Degraded
        {
            PixelsPerModule = Lerp(3.5, 2.2, s), Angle = Lerp(5, 20, s), Keystone = Lerp(0.05, 0.2, s), Blur = Lerp(0.6, 1.4, s),
            Ink = 0.15f, Paper = 0.75f, LightFalloff = 0.3, Noise = Lerp(8, 24, s), Seed = s,
        }),
        ("blurry and noisy", s => new Degraded { PixelsPerModule = 3, Blur = Lerp(0.8, 1.8, s), Ink = 0.1f, Paper = 0.9f, Noise = Lerp(15, 45, s), Seed = s }),
    ];

    public static IEnumerable<BenchCase> Cases()
    {
        foreach (var (condition, at) in Conditions)
            foreach (var (name, format, text) in Symbologies)
                for (var s = 0; s < Severities; s++)
                {
                    var degraded = at(s);
                    yield return new BenchCase($"{condition}/{name}/{s}", condition, name, text, () => degraded.Render(format, text));
                }

        // Pictures without a barcode: anything read here is a false read.
        for (var seed = 0; seed < 24; seed++)
        {
            var copy = seed;
            yield return new BenchCase($"no barcode/{seed}", "no barcode", "", null, () => Clutter(copy));
        }
    }

    /// <summary>Grainy gray with bar-like strokes and blobs, the kind of texture 1D readers mistake for bars.</summary>
    private static GrayImage Clutter(int seed)
    {
        var rnd = new Random(1000 + seed);
        int w = 400 + rnd.Next(500), h = 400 + rnd.Next(500);
        var noise = 20 + seed * 4;
        var pixels = new byte[w * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)Math.Clamp(150 + (int)((rnd.NextDouble() - 0.5) * 2 * noise), 0, 255);
        for (var k = 0; k < 60; k++)
        {
            int x0 = rnd.Next(w), y0 = rnd.Next(h), sw = rnd.Next(1, 12), sh = rnd.Next(1, 150);
            if (rnd.Next(2) == 0) (sw, sh) = (sh, sw);
            var v = (byte)rnd.Next(256);
            for (var y = y0; y < Math.Min(h, y0 + sh); y++)
                for (var x = x0; x < Math.Min(w, x0 + sw); x++) pixels[y * w + x] = v;
        }
        return new GrayImage(w, h, pixels);
    }
}
