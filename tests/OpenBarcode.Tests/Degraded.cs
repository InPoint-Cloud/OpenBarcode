using InPoint.Cloud.OpenBarcode.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Tests;

/// <summary>
/// Simulates poor pictures of a barcode: a camera-like resample (scale, tilt, keystone), blur, faded or
/// dark print, uneven light and sensor noise. Deterministic for a given seed.
/// </summary>
internal sealed class Degraded
{
    public double PixelsPerModule { get; init; } = 3;
    public double Angle { get; init; }
    public double Keystone { get; init; }
    public double Blur { get; init; }
    public float Ink { get; init; } = 0.05f;
    public float Paper { get; init; } = 0.95f;
    public double LightFalloff { get; init; }
    public double Noise { get; init; }
    public double Specks { get; init; }
    public int Seed { get; init; }
    /// <summary>QR error correction level; ZXing's writer uses L when none is given.</summary>
    public ErrorCorrectionLevel? QrLevel { get; init; }
    /// <summary>Side of a logo printed over the middle of the symbol, as a share of the symbol's side.</summary>
    public double Logo { get; init; }

    private const int Oversample = 8;

    public GrayImage Render(ZxFormat format, string text)
    {
        var symbol = Symbol(format, text, QrLevel);
        if (Logo > 0) PaintLogo(symbol);
        var image = Project(symbol);
        image = GaussianBlur(image, Blur);
        ApplyToneAndNoise(image);
        var bytes = new byte[image.Length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp((int)Math.Round(image[i] * 255), 0, 255);
        return new GrayImage(_width, _height, bytes);
    }

    private int _width, _height;

    /// <summary>The symbol at 8 pixels per module with a 10-module quiet zone; 0 = ink, 1 = paper.</summary>
    private static (float[] Pixels, int Width, int Height) Symbol(ZxFormat format, string text, ErrorCorrectionLevel? level)
    {
        var options = new EncodingOptions { Margin = 0, PureBarcode = true };
        if (level != null) options.Hints[EncodeHintType.ERROR_CORRECTION] = level;
        var writer = new BarcodeWriterGeneric { Format = format, Options = options };
        var m = writer.Encode(text);
        const int quiet = 10;
        var linear = m.Height == 1;
        var rows = linear ? 40 : m.Height;
        int w = (m.Width + 2 * quiet) * Oversample, h = (rows + 2 * quiet) * Oversample;
        var px = new float[w * h];
        Array.Fill(px, 1f);
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < m.Width; x++)
                if (m[x, linear ? 0 : y])
                    for (var yy = 0; yy < Oversample; yy++)
                        for (var xx = 0; xx < Oversample; xx++)
                            px[((y + quiet) * Oversample + yy) * w + (x + quiet) * Oversample + xx] = 0f;
        return (px, w, h);
    }

    /// <summary>A Swiss-cross style logo: a white frame around a dark square with a white cross, centred.</summary>
    private void PaintLogo((float[] Pixels, int Width, int Height) symbol)
    {
        const int quiet = 10 * Oversample;
        double side = (symbol.Width - 2 * quiet) * Logo, centre = symbol.Width / 2.0;
        int from = (int)(centre - side / 2), to = (int)(centre + side / 2);
        for (var y = from; y < to; y++)
            for (var x = from; x < to; x++)
            {
                double u = (x - from) / side - 0.5, v = (y - from) / side - 0.5;
                var frame = Math.Abs(u) > 0.42 || Math.Abs(v) > 0.42;
                var cross = (Math.Abs(u) < 0.09 && Math.Abs(v) < 0.28) || (Math.Abs(v) < 0.09 && Math.Abs(u) < 0.28);
                symbol.Pixels[y * symbol.Width + x] = frame || cross ? 1f : 0f;
            }
    }

    private float[] Project((float[] Pixels, int Width, int Height) src)
    {
        var scale = PixelsPerModule / Oversample;
        double ow = src.Width * scale, oh = src.Height * scale;
        var size = (int)Math.Sqrt(ow * ow + oh * oh) + 80;
        _width = _height = size;
        var dst = new float[size * size];
        double a = Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        var centre = size / 2.0;
        const int ss = 3;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float acc = 0;
                for (var j = 0; j < ss; j++)
                    for (var i = 0; i < ss; i++)
                    {
                        double px = x + (i + 0.5) / ss - centre, py = y + (j + 0.5) / ss - centre;
                        px /= 1 + Keystone * (py / centre);
                        var ux = (c * px + s * py) / scale + src.Width / 2.0;
                        var uy = (-s * px + c * py) / scale + src.Height / 2.0;
                        acc += Sample(src, ux, uy);
                    }
                dst[y * size + x] = acc / (ss * ss);
            }
        return dst;
    }

    private static float Sample((float[] Pixels, int Width, int Height) src, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        float At(int xx, int yy) => xx < 0 || yy < 0 || xx >= src.Width || yy >= src.Height ? 1f : src.Pixels[yy * src.Width + xx];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    private float[] GaussianBlur(float[] src, double sigma)
    {
        if (sigma <= 0) return src;
        int r = (int)Math.Ceiling(sigma * 3), w = _width, h = _height;
        var k = new float[2 * r + 1];
        float sum = 0;
        for (var i = -r; i <= r; i++) sum += k[i + r] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float acc = 0;
                for (var i = -r; i <= r; i++) acc += k[i + r] * src[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = acc / sum;
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float acc = 0;
                for (var i = -r; i <= r; i++) acc += k[i + r] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                dst[y * w + x] = acc / sum;
            }
        return dst;
    }

    private void ApplyToneAndNoise(float[] px)
    {
        var rnd = new Random(Seed);
        for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                var i = y * _width + x;
                var v = Ink + (Paper - Ink) * px[i];
                // Light falls off from the top-left corner towards the bottom-right.
                v *= (float)(1 - LightFalloff * (x + y) / (double)(_width + _height));
                if (Noise > 0)
                {
                    double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble();
                    v += (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * Noise / 255);
                }
                if (Specks > 0 && rnd.NextDouble() < Specks) v = rnd.Next(2);
                px[i] = v;
            }
    }
}
