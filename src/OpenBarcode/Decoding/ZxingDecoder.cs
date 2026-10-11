using System;
using System.Collections.Generic;
using InPoint.Cloud.OpenBarcode.Imaging;
using ZXing;
using ZXing.Common;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Decoding;

/// <summary>A barcode found in one bitmap, before it is tagged with where the bitmap came from.</summary>
internal readonly struct RawHit(string text, BarcodeFormat format, byte[]? rawBytes, BarcodePoint[] points)
{
    public string Text { get; } = text;
    public BarcodeFormat Format { get; } = format;
    public byte[]? RawBytes { get; } = rawBytes;
    public BarcodePoint[] Points { get; } = points;

    /// <summary>Identifies the code regardless of where it was found.</summary>
    public string Key => Format + "\n" + Text;

    /// <summary>Returns a copy with every point mapped through <paramref name="map"/>.</summary>
    public RawHit Transform(Affine map)
    {
        if (map.IsIdentity) return this;
        var points = new BarcodePoint[Points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            map.Apply(Points[i].X, Points[i].Y, out var x, out var y);
            points[i] = new BarcodePoint((float)x, (float)y);
        }
        return new RawHit(Text, Format, RawBytes, points);
    }
}

/// <summary>Runs ZXing over a grayscale bitmap with a few preprocessing passes.</summary>
internal sealed class ZxingDecoder
{
    private const int MinUsefulSize = 600;
    private const int DownscaleThreshold = 2400;
    private const int MaxRegions = 32;
    private const BarcodeFormat CentreSeeking = BarcodeFormat.DataMatrix | BarcodeFormat.Aztec;
    /// <summary>Diagonals tried when the tilt estimate fails; 1D readers tolerate about 15 degrees either way.</summary>
    private static readonly double[] SkewAngles = [45, 135];
    private const double MinDeskewAngle = 3;
    private const double MinDeskewConfidence = 0.12;

    private readonly BarcodeReaderOptions _options;
    private readonly IList<ZxFormat> _formats;
    private readonly IList<ZxFormat> _centreSeekingFormats;
    private readonly IList<ZxFormat> _linearFormats;
    /// <summary>1D formats plus PDF417, whose detector also scans along rows and so needs the image level.</summary>
    private readonly IList<ZxFormat> _rowScanFormats;
    private readonly bool _maxiCode;

    public ZxingDecoder(BarcodeReaderOptions options)
    {
        _options = options;
        // ZXing's MaxiCode reader only handles tightly cropped symbols and throws IndexOutOfRangeException
        // on many other images, which would abort the whole pass. It gets a guarded pass of its own.
        _maxiCode = (options.Formats & BarcodeFormat.MaxiCode) != 0;
        _formats = ToZxing(options.Formats & ~BarcodeFormat.MaxiCode);
        if (_formats.Count == 0 && !_maxiCode) throw new ArgumentException("No barcode formats selected.", nameof(options));
        _centreSeekingFormats = ToZxing(options.Formats & CentreSeeking);
        _linearFormats = ToZxing(options.Formats & BarcodeFormat.AllLinear);
        _rowScanFormats = ToZxing(options.Formats & (BarcodeFormat.AllLinear | BarcodeFormat.Pdf417));
    }

    /// <summary>Finds the barcodes in one bitmap.</summary>
    /// <param name="image">The bitmap.</param>
    /// <param name="enhance">Whether clean-up passes may run when the plain passes find nothing.</param>
    public List<RawHit> Decode(GrayImage image, bool enhance)
    {
        var hits = _formats.Count > 0 ? DecodePlain(image) : [];
        if (enhance && _formats.Count > 0)
        {
            var ballot = new Ballot();
            if (hits.Exists(IsWeak))
            {
                // Noise and texture can pass for a short 1D code. Keep such a read only if a second look,
                // with a different binarizer and corrected lighting, finds it too.
                // A read that does not repeat on a line beside it gets no vote here; a short code made up from
                // the texture of another code gets none anywhere.
                var repeated = hits.FindAll(h => !IsWeak(h) || RepeatsOnParallelLine(image, h));
                foreach (var hit in hits)
                    if ((hit.Format & ShortRetail) != 0 && !repeated.Contains(hit)) ballot.Reject(hit);
                ballot.Add(repeated);
                ballot.Add(SecondOpinion(image));
                hits = ballot.Trusted(hits);
            }
            if (hits.Count == 0)
                hits = DecodeEnhanced(image, ballot);
        }
        if (_maxiCode && (hits.Count == 0 || _options.ReadMultiple))
        {
            var (prepared, map) = Prepare(image);
            hits.AddRange(Run(prepared, hybrid: true, map, MaxiCodeOnly, _options.ReadMultiple));
        }
        return RemoveOverlaps(hits);
    }

    private static readonly IList<ZxFormat> MaxiCodeOnly = new[] { ZxFormat.MAXICODE };

    /// <summary>The passes that suit clean images: scans, screenshots, rendered PDFs.</summary>
    private List<RawHit> DecodePlain(GrayImage image)
    {
        var (prepared, map) = Prepare(image);

        var hits = Run(prepared, hybrid: true, map, _formats, _options.ReadMultiple);
        if (hits.Count == 0)
            hits = Run(prepared, hybrid: false, map, _formats, _options.ReadMultiple);
        if (hits.Count == 0 && prepared.Width > image.Width + 2 * PadFor(image))
        {
            // Nearest-neighbour upscaling suits crisp crops with one-pixel modules, but it distorts the grey
            // edges of photos and anti-aliased renders, so also try the image at its own size.
            var pad = PadFor(image);
            hits = Run(image.Pad(pad), hybrid: true, Map(1, pad), _formats, _options.ReadMultiple);
        }
        var maxSide = Math.Max(image.Width, image.Height);
        if (hits.Count == 0 && maxSide > DownscaleThreshold)
        {
            // Noisy high-resolution scans sometimes decode better at half size.
            var small = image.DownscaleBox(2);
            var smallPad = PadFor(small);
            hits = Run(small.Pad(smallPad), hybrid: true, Map(0.5, smallPad), _formats, _options.ReadMultiple);
        }

        // 2D detectors are rotation invariant and 1D readers try both directions along a row, so one extra
        // pass over the image turned by 90 degrees, restricted to 1D formats, covers sideways barcodes.
        if (_options.AutoRotate && _linearFormats.Count > 0 && (hits.Count == 0 || _options.ReadMultiple))
            hits.AddRange(RunRotated(prepared, map));

        if (_centreSeekingFormats.Count > 0 && (_options.ReadMultiple || hits.Count == 0))
            hits.AddRange(DecodeRegions(image, Affine.Identity, hits));
        return hits;
    }

    /// <summary>
    /// Symbologies with at most a single check digit. On noisy pictures ZXing assembles them from random
    /// texture, so with clean-up enabled one is only reported when two different passes agree.
    /// </summary>
    private const BarcodeFormat WeaklyChecked = BarcodeFormat.UpcE | BarcodeFormat.Ean8 | BarcodeFormat.Ean13 |
        BarcodeFormat.UpcA | BarcodeFormat.Codabar | BarcodeFormat.Itf | BarcodeFormat.Msi |
        BarcodeFormat.DataBar | BarcodeFormat.DataBarExpanded;

    /// <summary>Variable-length 1D symbologies, which the fine texture of a 2D code can pass for when only a character or two long.</summary>
    private const BarcodeFormat VariableLength = BarcodeFormat.Code39 | BarcodeFormat.Code93 | BarcodeFormat.Code128 | BarcodeFormat.Codabar;

    /// <summary>Whether a read needs a second pass to agree before it is trusted.</summary>
    private static bool IsWeak(RawHit hit) =>
        (hit.Format & WeaklyChecked) != 0 || ((hit.Format & VariableLength) != 0 && hit.Text.Length <= 2);

    /// <summary>Weakly checked codes so short that the texture of a 2D code or of a tilted 1D code passes for one.</summary>
    private const BarcodeFormat ShortRetail = BarcodeFormat.UpcE | BarcodeFormat.Ean8;

    /// <summary>Weakly checked codes wide enough for <see cref="Recheck"/> to tell their bars from texture.</summary>
    private const BarcodeFormat LongRetail = BarcodeFormat.Ean13 | BarcodeFormat.UpcA;

    /// <summary>
    /// Clean-up passes for photos and poor scans, tried one after another until one finds something
    /// trustworthy: lighting correction, deskewing, adaptive thresholding, denoising, sharpening and, for 1D
    /// codes, diagonal orientations.
    /// </summary>
    private List<RawHit> DecodeEnhanced(GrayImage image, Ballot ballot)
    {
        foreach (var hits in EnhancedPasses(image, ballot))
        {
            ballot.Add(hits);
            var trusted = ballot.Trusted(hits);
            if (trusted.Count > 0) return trusted;
        }
        return [];
    }

    /// <summary>An independent look at an image for confirming weakly checked reads.</summary>
    private List<RawHit> SecondOpinion(GrayImage image)
    {
        var (prepared, map) = Prepare(image);
        var hits = Run(prepared, hybrid: false, map, _formats, _options.ReadMultiple);
        var normalized = ImageFilters.NormalizeLighting(image);
        var pad = PadFor(normalized);
        var padded = normalized.Pad(pad);
        hits.AddRange(Run(padded, hybrid: true, Map(1, pad), _formats, _options.ReadMultiple));
        if (_options.AutoRotate && _linearFormats.Count > 0)
            hits.AddRange(RunRotated(padded, Map(1, pad)));
        return hits;
    }

    /// <summary>Counts in how many passes each code was read, so weakly checked codes can require two.</summary>
    private sealed class Ballot
    {
        private readonly Dictionary<string, (RawHit Hit, int Count)> _votes = [];

        /// <summary>Records one pass; a code read several times within the pass counts once.</summary>
        public void Add(List<RawHit> pass)
        {
            var seen = new HashSet<string>();
            foreach (var hit in pass)
            {
                var key = hit.Key;
                if (!seen.Add(key)) continue;
                _votes[key] = _votes.TryGetValue(key, out var v) ? (v.Hit, v.Count + 1) : (hit, 1);
            }
        }

        private readonly HashSet<string> _rejected = [];

        /// <summary>Never trusts this code, however many passes read it.</summary>
        public void Reject(RawHit hit) => _rejected.Add(hit.Key);

        private bool IsTrusted(RawHit hit) =>
            !IsWeak(hit) || (!_rejected.Contains(hit.Key) && _votes.TryGetValue(hit.Key, out var v) && v.Count >= 2);

        /// <summary>
        /// The hits of the latest pass that can be trusted, plus weakly checked codes it confirmed that an
        /// earlier pass found first. Empty when the pass found nothing trustworthy.
        /// </summary>
        public List<RawHit> Trusted(List<RawHit> pass)
        {
            var result = pass.FindAll(IsTrusted);
            if (result.Count == 0) return result;
            foreach (var (hit, count) in _votes.Values)
                if (count >= 2 && !_rejected.Contains(hit.Key) && !result.Exists(h => h.Key == hit.Key))
                    result.Add(hit);
            return result;
        }
    }

    private IEnumerable<List<RawHit>> EnhancedPasses(GrayImage image, Ballot ballot)
    {
        // Large scans and photos are cleaned up at half size: averaging pixels already removes noise, and the
        // passes cost a quarter of the time, which matters on pages that hold no barcode at all.
        var work = image;
        var toImage = Affine.Identity;
        if (Math.Max(image.Width, image.Height) > DownscaleThreshold)
        {
            work = image.DownscaleBox(2);
            toImage = Scale(2);
        }
        var maxSide = Math.Max(work.Width, work.Height);

        var normalized = ImageFilters.NormalizeLighting(work);
        yield return DecodeVariant(normalized, toImage, ballot);

        // ZXing's Aztec and PDF417 detectors give up beyond about 10 degrees of tilt, and 1D readers beyond
        // about 15. Turn the picture so the symbol's edges line up with the axes.
        var tilt = ImageFilters.DominantAngle(normalized, out var confidence);
        var level = normalized;
        var levelToImage = toImage;
        if (Math.Abs(tilt) >= MinDeskewAngle && confidence >= MinDeskewConfidence)
        {
            level = ImageFilters.Rotate(normalized, -tilt, out var toWork);
            levelToImage = toImage.After(toWork);
            yield return DecodeVariant(level, levelToImage, ballot);
        }

        // A window a few modules wide: wide enough to span the widest bar, small enough to follow shadows.
        var window = ImageFilters.Clamp(maxSide / 20, 15, 81);
        yield return DecodeVariant(ImageFilters.AdaptiveThreshold(normalized, window), toImage, ballot);

        // Sensor noise, JPEG artefacts and specks.
        yield return DecodeVariant(ImageFilters.GaussianBlur(ImageFilters.Median3(normalized), 1.0), toImage, ballot);

        if (_linearFormats.Count > 0)
            yield return DecodeAlongBars(level, levelToImage, ballot);

        // Out of focus: steepen the edges.
        yield return DecodeVariant(ImageFilters.Sharpen(normalized, 2.0, 1.5f), toImage, ballot);

        // Last resort for 1D and PDF417 codes the tilt estimate missed: try the diagonals.
        if (_options.AutoRotate && _rowScanFormats.Count > 0)
        {
            foreach (var angle in SkewAngles)
            {
                var rotated = ImageFilters.Rotate(normalized, angle, out var toWork);
                var pad = PadFor(rotated);
                var prepared = rotated.Pad(pad);
                var map = toImage.After(toWork).After(Map(1, pad));
                var hits = Run(prepared, hybrid: true, map, _rowScanFormats, _options.ReadMultiple);
                Recheck(prepared, map, hits, ballot);
                yield return hits;
            }
        }
    }

    /// <summary>
    /// Heavy noise on 1D codes: averages along the bars only, so neighbouring bars stay apart, then reads the
    /// averaged picture, and its rows thresholded midway between their local extremes. ZXing's own row
    /// binarizer sharpens each row, which brings the noise back, and its threshold drifts towards black when
    /// blur and ink spread narrow the gaps between bars.
    /// </summary>
    private List<RawHit> DecodeAlongBars(GrayImage image, Affine toImage, Ballot ballot)
    {
        var maxSide = Math.Max(image.Width, image.Height);
        var along = Math.Max(3, maxSide / 100);
        var window = ImageFilters.Clamp(maxSide / 25, 15, 81);

        var hits = new List<RawHit>();
        var confirmed = new List<RawHit>();
        ReadAlongRows(ImageFilters.BoxBlur(image, 0, along), toImage, along, window, hits, confirmed);
        if (_options.AutoRotate)
        {
            // Bars lying horizontally: the same on the picture turned a quarter clockwise.
            var sideways = ImageFilters.BoxBlur(image, along, 0).RotateClockwise();
            var unrotate = new Affine(0, 1, 0, -1, 0, image.Height - 1);
            ReadAlongRows(sideways, toImage.After(unrotate), along, window, hits, confirmed);
        }

        // Averaging along bars that fan out in a photo taken at an angle merges thin gaps differently across
        // the code, which can turn a few digits into others that still pass the check digit. When the same
        // place reads as two different codes, neither is trusted.
        var disputed = hits.FindAll(h => IsWeak(h) && hits.Exists(o => o.Format == h.Format && o.Text != h.Text && Overlap(o, h)));
        hits.RemoveAll(disputed.Contains);
        confirmed.RemoveAll(disputed.Contains);
        ballot.Add(confirmed);
        return hits;
    }

    private void ReadAlongRows(GrayImage averaged, Affine toImage, int along, int window, List<RawHit> hits, List<RawHit> confirmed)
    {
        var (grey, toAveraged) = Prepare(averaged);
        var map = toImage.After(toAveraged);
        // Rows this far apart were averaged from different pixels, so they are independent looks at the bars.
        var apart = (int)Math.Ceiling((2 * along + 2) / toAveraged.A);
        var found = Run(grey, hybrid: true, map, _linearFormats, _options.ReadMultiple);
        confirmed.AddRange(ConfirmOnOtherRows(grey, map, found, apart));
        if (found.Count == 0 || _options.ReadMultiple)
        {
            // Thresholded at the picture's own size, then enlarged like the grey one, so thin bars keep their width.
            var minRange = (int)Math.Max(16, 4 * ImageFilters.NoiseLevel(averaged));
            var (binary, _) = Prepare(ImageFilters.RowMidrangeThreshold(averaged, window, minRange));
            var more = Run(binary, hybrid: true, map, _linearFormats, _options.ReadMultiple);
            confirmed.AddRange(ConfirmOnOtherRows(binary, map, more, apart));
            found.AddRange(more);
        }
        hits.AddRange(found);
    }

    /// <summary>
    /// The weakly checked reads that a band of rows <paramref name="apart"/> pixels above or below the row they
    /// were read on gives again. A misread from noise does not repeat on rows averaged from other pixels, and
    /// neither does a code made up from texture; real bars do.
    /// </summary>
    private List<RawHit> ConfirmOnOtherRows(GrayImage prepared, Affine map, List<RawHit> hits, int apart)
    {
        var toPrepared = map.Inverse();
        var confirmed = new List<RawHit>();
        foreach (var claim in hits)
        {
            if (!IsWeak(claim) || claim.Points.Length != 2) continue;
            toPrepared.Apply(claim.Points[0].X, claim.Points[0].Y, out _, out var y0);
            toPrepared.Apply(claim.Points[1].X, claim.Points[1].Y, out _, out var y1);
            var row = (int)Math.Round((y0 + y1) / 2);
            foreach (var side in new[] { -apart, apart })
            {
                // Rows from half the distance to one and a half times it, none of them averaged with the read row.
                var band = RowBand(prepared, row + side, apart / 2);
                if (band == null) continue;
                var again = Run(band, hybrid: true, Affine.Identity, _linearFormats, multiple: true);
                if (again.Exists(a => a.Format == claim.Format && a.Text == claim.Text))
                {
                    confirmed.Add(claim);
                    break;
                }
            }
        }
        return confirmed;
    }

    /// <summary>
    /// Whether a 1D read repeats on a line parallel to the one it was read on, a little to either side. Real
    /// bars carry on across the code; the texture of a 2D code, or of a tilted 1D code read the wrong way,
    /// changes from one module row to the next, and the same line read again with another binarizer would
    /// just repeat the false read.
    /// </summary>
    private bool RepeatsOnParallelLine(GrayImage image, RawHit claim)
    {
        if (claim.Points.Length < 2) return true;
        // The ends of the code: the two points farthest apart (an EAN with an add-on has four).
        var (a, b) = (claim.Points[0], claim.Points[1]);
        foreach (var p in claim.Points)
            foreach (var q in claim.Points)
                if (Distance(p, q) > Distance(a, b)) (a, b) = (p, q);
        double x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y, length = Distance(a, b);
        if (length < 20) return true;
        // Not so far that the line leaves a tilted code read along a row, nor so near that the strip takes in the row read.
        var offset = Math.Max(StripRows / 2 + 1, ParallelLineOffset * length);
        foreach (var side in new[] { -offset, offset })
        {
            var strip = ImageFilters.Strip(image, x0, y0, x1, y1, side, StripRows, StripMargin * length);
            var again = Run(strip, hybrid: true, Affine.Identity, _linearFormats, multiple: true);
            if (again.Exists(h => h.Format == claim.Format && h.Text == claim.Text)) return true;
        }
        return false;
    }

    /// <summary>How far to either side <see cref="RepeatsOnParallelLine"/> looks, as a share of the code's width.</summary>
    private const double ParallelLineOffset = 0.015;

    private static double Distance(BarcodePoint a, BarcodePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Room left beyond each end of the parallel line for the quiet zone, as a share of the code's width.</summary>
    private const double StripMargin = 0.2;

    private const int StripRows = 15;

    /// <summary>Whether the bounding boxes of two hits' points overlap.</summary>
    private static bool Overlap(RawHit a, RawHit b)
    {
        Bounds(a, out var ax0, out var ay0, out var ax1, out var ay1);
        Bounds(b, out var bx0, out var by0, out var bx1, out var by1);
        // 1D hits are a line; give them some height so hits on nearby rows of the same code meet.
        var slack = Math.Max(Math.Max(ax1 - ax0, ay1 - ay0), Math.Max(bx1 - bx0, by1 - by0)) / 4;
        return ax0 - slack <= bx1 && bx0 - slack <= ax1 && ay0 - slack <= by1 && by0 - slack <= ay1;
    }

    private static void Bounds(RawHit hit, out float x0, out float y0, out float x1, out float y1)
    {
        x0 = y0 = float.MaxValue;
        x1 = y1 = float.MinValue;
        foreach (var p in hit.Points)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
    }

    /// <summary>Rows <paramref name="centre"/> ± <paramref name="half"/> of an image, or null if outside it.</summary>
    private static GrayImage? RowBand(GrayImage image, int centre, int half)
    {
        int top = centre - half, bottom = centre + half + 1;
        if (top < 0 || bottom > image.Height) return null;
        var pixels = new byte[(bottom - top) * image.Width];
        Array.Copy(image.Pixels, top * image.Width, pixels, 0, pixels.Length);
        return new GrayImage(image.Width, bottom - top, pixels);
    }

    /// <summary>One clean-up variant: the full pass, the sideways 1D pass and the 2D region search.</summary>
    private List<RawHit> DecodeVariant(GrayImage variant, Affine toImage, Ballot ballot)
    {
        var maxSide = Math.Max(variant.Width, variant.Height);
        if (maxSide < MinUsefulSize)
        {
            var factor = Math.Min(4, (MinUsefulSize + maxSide - 1) / maxSide);
            variant = ImageFilters.UpscaleBilinear(variant, factor);
            toImage = toImage.After(Scale(1.0 / factor));
        }
        var pad = PadFor(variant);
        var prepared = variant.Pad(pad);
        var map = toImage.After(Map(1, pad));

        var hits = Run(prepared, hybrid: true, map, _formats, _options.ReadMultiple);
        if (_options.AutoRotate && _linearFormats.Count > 0 && (hits.Count == 0 || _options.ReadMultiple))
            hits.AddRange(RunRotated(prepared, map));
        Recheck(prepared, map, hits, ballot);
        if (_centreSeekingFormats.Count > 0 && (_options.ReadMultiple || hits.Count == 0))
            hits.AddRange(DecodeRegions(variant, toImage, hits, sampleDataMatrix: true));
        return hits;
    }

    private List<RawHit> RunRotated(GrayImage prepared, Affine map)
    {
        // Source (x, y) lands at (H - 1 - y, x), so rotated (u, v) came from (v, H - 1 - u).
        var unrotate = new Affine(0, 1, 0, -1, 0, prepared.Height - 1);
        return Run(prepared.RotateClockwise(), hybrid: true, map.After(unrotate), _linearFormats, _options.ReadMultiple);
    }

    /// <summary>
    /// When a clean-up pass read nothing but weakly checked codes, gives each EAN-13 or UPC-A its second vote
    /// if the pattern it was read from carries on a little above or below the line it was read on, as real
    /// bars do, so one that only this clean-up brings out (a blurred one, say) is kept. The texture of a QR
    /// code or of print that happened to pass for a code changes from one module row to the next. Simply
    /// re-reading the same line, even with another binarizer, would repeat such a false read.
    /// </summary>
    private static void Recheck(GrayImage prepared, Affine map, List<RawHit> hits, Ballot ballot)
    {
        if (!hits.TrueForAll(IsWeak)) return;
        var toPrepared = map.Inverse();
        var confirmed = new List<RawHit>();
        foreach (var claim in hits)
        {
            if ((claim.Format & LongRetail) == 0 || claim.Points.Length != 2) continue;
            toPrepared.Apply(claim.Points[0].X, claim.Points[0].Y, out var x0, out var y0);
            toPrepared.Apply(claim.Points[1].X, claim.Points[1].Y, out var x1, out var y1);
            double dx = x1 - x0, dy = y1 - y0, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 20) continue;
            var line = Profile(prepared, x0, y0, x1, y1, 0);
            foreach (var side in BarContinuity)
            {
                if (Continuity(prepared, line, x0, y0, x1, y1, side * length) >= MinBarCorrelation)
                {
                    confirmed.Add(claim);
                    break;
                }
            }
        }
        ballot.Add(confirmed);
    }

    /// <summary>Offsets across the bars, as a share of the code's width, at which the bars must carry on.</summary>
    private static readonly double[] BarContinuity = [-0.06, 0.06];

    private const double MinBarCorrelation = 0.75;

    /// <summary>
    /// Best correlation between <paramref name="line"/> and the line moved sideways by <paramref name="offset"/>
    /// pixels, letting each end of the moved line slide a little along it: in a photo taken at an angle the
    /// bars fan out, so they shift and spread from one row to the next.
    /// </summary>
    private static double Continuity(GrayImage image, double[] line, double x0, double y0, double x1, double y1, double offset)
    {
        double dx = x1 - x0, dy = y1 - y0, length = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / length, uy = dy / length;
        var step = length * EndSlide / SlideSteps;
        var best = double.MinValue;
        for (var a = -SlideSteps; a <= SlideSteps; a++)
            for (var b = -SlideSteps; b <= SlideSteps; b++)
            {
                var moved = Profile(image, x0 + a * step * ux, y0 + a * step * uy, x1 + b * step * ux, y1 + b * step * uy, offset, line.Length);
                best = Math.Max(best, Correlation(line, moved));
            }
        return best;
    }

    /// <summary>How far each end may slide, as a share of the code's width, and in how many steps each way.</summary>
    private const double EndSlide = 0.015;

    private const int SlideSteps = 4;

    /// <summary>Grey values along a line, moved sideways by <paramref name="offset"/> pixels.</summary>
    private static double[] Profile(GrayImage image, double x0, double y0, double x1, double y1, double offset, int count = 0)
    {
        double dx = x1 - x0, dy = y1 - y0, length = Math.Sqrt(dx * dx + dy * dy);
        double nx = -dy / length * offset, ny = dx / length * offset;
        if (count == 0) count = (int)length;
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = (i + 0.5) / count;
            int x = (int)Math.Round(x0 + t * dx + nx), y = (int)Math.Round(y0 + t * dy + ny);
            values[i] = x < 0 || y < 0 || x >= image.Width || y >= image.Height ? 255 : image.Pixels[y * image.Width + x];
        }
        return values;
    }

    private static double Correlation(double[] a, double[] b)
    {
        double meanA = 0, meanB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            meanA += a[i];
            meanB += b[i];
        }
        meanA /= a.Length;
        meanB /= b.Length;
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            ab += (a[i] - meanA) * (b[i] - meanB);
            aa += (a[i] - meanA) * (a[i] - meanA);
            bb += (b[i] - meanB) * (b[i] - meanB);
        }
        return aa == 0 || bb == 0 ? 0 : ab / Math.Sqrt(aa * bb);
    }

    /// <summary>
    /// Drops duplicates and partial reads: a hit whose centre lies inside a larger hit is usually a fragment
    /// of it (for example a UPC-E read from half of an EAN-13).
    /// </summary>
    private static List<RawHit> RemoveOverlaps(List<RawHit> hits)
    {
        if (hits.Count < 2) return hits;
        var ordered = hits.ConvertAll(Box.Around);
        ordered.Sort((a, b) => b.Size.CompareTo(a.Size));

        var kept = new List<Box>();
        var seen = new HashSet<string>();
        foreach (var box in ordered)
        {
            if (!seen.Add(box.Hit.Key)) continue;
            if (box.Size >= 0 && (kept.Exists(box.CentreLiesIn) || IsFragmentOfKept(box.Hit, kept))) continue;
            kept.Add(box);
        }

        // Restore discovery order.
        return hits.FindAll(hit => kept.Exists(k => ReferenceEquals(k.Hit.Points, hit.Points)));
    }

    /// <summary>A hit with the bounding box of its points; <see cref="Size"/> is -1 when it has no points.</summary>
    private readonly struct Box(RawHit hit, float minX, float minY, float maxX, float maxY, float size)
    {
        public RawHit Hit { get; } = hit;
        public float MinX { get; } = minX;
        public float MinY { get; } = minY;
        public float MaxX { get; } = maxX;
        public float MaxY { get; } = maxY;
        public float Size { get; } = size;

        public static Box Around(RawHit hit)
        {
            if (hit.Points.Length == 0) return new Box(hit, 0, 0, 0, 0, -1);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in hit.Points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return new Box(hit, minX, minY, maxX, maxY, Math.Max(maxX - minX, maxY - minY));
        }

        /// <summary>Whether this box's centre lies inside <paramref name="other"/>, with a small margin.</summary>
        public bool CentreLiesIn(Box other)
        {
            if (other.Size <= 0) return false;
            float cx = (MinX + MaxX) / 2, cy = (MinY + MaxY) / 2;
            var margin = Math.Max(10, other.Size / 10);
            return cx >= other.MinX - margin && cx <= other.MaxX + margin && cy >= other.MinY - margin && cy <= other.MaxY + margin;
        }
    }

    /// <summary>Crops each ink blob that no barcode has been found in yet and looks for Data Matrix / Aztec there.</summary>
    /// <param name="image">The bitmap to crop from.</param>
    /// <param name="toImage">Maps <paramref name="image"/> coordinates to the caller's image.</param>
    /// <param name="found">Hits so far, in the caller's coordinates.</param>
    /// <param name="sampleDataMatrix">Whether to fall back to <see cref="DataMatrixSampler"/> where ZXing finds nothing.</param>
    private IEnumerable<RawHit> DecodeRegions(GrayImage image, Affine toImage, List<RawHit> found, bool sampleDataMatrix = false)
    {
        var regions = RegionFinder.Find(image, MaxRegions);
        if (regions.Count <= 1 && found.Count > 0) yield break;
        var seen = new HashSet<string>(found.ConvertAll(h => h.Key));
        var taken = new List<RawHit>(found);
        var inImage = new List<RawHit>();

        foreach (var region in regions)
        {
            if (Covered(region, inImage) || (toImage.IsIdentity && Covered(region, taken))) continue;
            foreach (var local in DecodeRegion(image, region, sampleDataMatrix))
            {
                inImage.Add(local);
                var hit = local.Transform(toImage);
                taken.Add(hit);
                if (seen.Add(hit.Key)) yield return hit;
            }
            if (!_options.ReadMultiple && taken.Count > found.Count) yield break;
        }
    }

    /// <summary>Looks for Data Matrix / Aztec in a crop around one region; hits are in <paramref name="image"/> coordinates.</summary>
    private List<RawHit> DecodeRegion(GrayImage image, Region region, bool sampleDataMatrix)
    {
        var margin = Math.Max(8, Math.Max(region.Width, region.Height) / 4);
        var (crop, ox, oy) = RegionFinder.Crop(image, region, margin);
        // The crop keeps the page's resolution, so it only needs the quiet-zone padding, not an upscale.
        var pad = PadFor(crop);
        var cropMap = new Affine(1, 0, ox - pad, 0, 1, oy - pad);
        var locals = Run(crop.Pad(pad), hybrid: true, cropMap, _centreSeekingFormats, multiple: false);
        if (locals.Count == 0 && sampleDataMatrix && (_options.Formats & BarcodeFormat.DataMatrix) != 0
            && DataMatrixSampler.Read(crop) is DataMatrixSampler.Result sampled)
        {
            var corners = Array.ConvertAll(sampled.Corners, p => new BarcodePoint(p.X + ox, p.Y + oy));
            locals.Add(new RawHit(sampled.Text, BarcodeFormat.DataMatrix, sampled.RawBytes, corners));
        }
        return locals;
    }

    private static bool Covered(Region region, List<RawHit> hits)
    {
        foreach (var hit in hits)
            foreach (var p in hit.Points)
                if (region.Contains(p.X, p.Y)) return true;
        return false;
    }

    /// <summary>
    /// Barcode images cut tightly from a document often have no quiet zone and tiny modules.
    /// A white border and an integer upscale make them look like what the detectors expect.
    /// </summary>
    private static (GrayImage Image, Affine ToImage) Prepare(GrayImage image)
    {
        var maxSide = Math.Max(image.Width, image.Height);
        var scale = maxSide < MinUsefulSize ? Math.Min(8, (MinUsefulSize + maxSide - 1) / maxSide) : 1;
        var scaled = image.UpscaleNearest(scale);
        var pad = PadFor(scaled);
        return (scaled.Pad(pad), Map(scale, pad));
    }

    private static int PadFor(GrayImage image) => ImageFilters.Clamp(Math.Max(image.Width, image.Height) / 8, 16, 160);

    /// <summary>Maps a bitmap that was scaled by <paramref name="scale"/> and then padded back to the original.</summary>
    private static Affine Map(double scale, int pad) => new(1 / scale, 0, -pad / scale, 0, 1 / scale, -pad / scale);

    private static Affine Scale(double factor) => new(factor, 0, 0, 0, factor, 0);

    /// <summary>Short symbologies that ZXing also "finds" in part of a longer barcode's bars.</summary>
    private const BarcodeFormat FragmentProne =
        BarcodeFormat.UpcE | BarcodeFormat.Ean8 | BarcodeFormat.Msi | BarcodeFormat.Codabar | BarcodeFormat.Itf;

    /// <summary>
    /// True when a fragment-prone 1D hit lies along a longer 1D hit's scan line, within the height its bars
    /// plausibly have.
    /// </summary>
    private static bool IsFragmentOfKept(RawHit hit, List<Box> kept)
    {
        if ((hit.Format & FragmentProne) == 0 || hit.Points.Length != 2) return false;
        return kept.Exists(box => LiesAlong(hit, box.Hit));
    }

    /// <summary>Whether every point of <paramref name="hit"/> lies near the scan line of the 1D hit <paramref name="longer"/>.</summary>
    private static bool LiesAlong(RawHit hit, RawHit longer)
    {
        if ((longer.Format & BarcodeFormat.AllLinear) == 0 || longer.Points.Length != 2) return false;
        var a = longer.Points[0];
        var b = longer.Points[1];
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return false;
        double ux = dx / length, uy = dy / length;
        foreach (var p in hit.Points)
        {
            var t = (p.X - a.X) * ux + (p.Y - a.Y) * uy;        // position along the longer code
            var d = Math.Abs(-(p.X - a.X) * uy + (p.Y - a.Y) * ux); // distance from its scan line
            if (t < -0.05 * length || t > 1.05 * length || d > 0.5 * length) return false;
        }
        return true;
    }

    private List<RawHit> Run(GrayImage image, bool hybrid, Affine map, IList<ZxFormat> formats, bool multiple)
    {
        var reader = CreateReader(hybrid, formats);
        var source = new RGBLuminanceSource(image.Pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        if (!TryDecode(reader, source, multiple, out var results)) return [];

        var hits = new List<RawHit>();
        if (formats.Contains(ZxFormat.QR_CODE) && !ContainsQr(results))
        {
            var logo = ReadQrWithLogo(source, hybrid, map);
            if (logo.HasValue) hits.Add(logo.Value);
        }
        if (results == null) return hits;
        foreach (var r in results)
        {
            if (r == null || string.IsNullOrEmpty(r.Text)) continue;
            var format = FromZxing(r.BarcodeFormat);
            if (format == BarcodeFormat.None) continue;
            hits.Add(new RawHit(r.Text, format, r.RawBytes, ToPoints(map, r.ResultPoints)));
        }
        return hits;
    }

    private static bool ContainsQr(Result[]? results) =>
        results != null && !Array.TrueForAll(results, r => r?.BarcodeFormat != ZxFormat.QR_CODE);

    /// <summary>Runs the reader; false when ZXing failed on the input (see <see cref="IsZxingFailure"/>).</summary>
    private static bool TryDecode(BarcodeReaderGeneric reader, LuminanceSource source, bool multiple, out Result[]? results)
    {
        try
        {
            if (multiple)
            {
                results = reader.DecodeMultiple(source);
            }
            else
            {
                var single = reader.Decode(source);
                results = single == null ? null : [single];
            }
            return true;
        }
        catch (Exception ex) when (IsZxingFailure(ex))
        {
            results = null;
            return false;
        }
    }

    private BarcodeReaderGeneric CreateReader(bool hybrid, IList<ZxFormat> formats)
    {
        var reader = new BarcodeReaderGeneric(null,
            hybrid ? (Func<LuminanceSource, Binarizer>)(s => new HybridBinarizer(s)) : s => new GlobalHistogramBinarizer(s),
            null)
        {
            // Orientation is handled in Decode: ZXing's own AutoRotate re-runs every detector four times.
            AutoRotate = false,
        };
        reader.Options.TryHarder = _options.TryHarder;
        // ZXing's 1D readers would otherwise retry every failed sub-region rotated; Decode does one rotated pass instead.
        reader.Options.Hints[DecodeHintType.TRY_HARDER_WITHOUT_ROTATION] = true;
        reader.Options.TryInverted = _options.TryInverted;
        reader.Options.PossibleFormats = formats;
        reader.Options.ReturnCodabarStartEnd = false;
        if (_options.CharacterSet != null) reader.Options.CharacterSet = _options.CharacterSet;
        return reader;
    }

    /// <summary>
    /// Second attempt at a QR code that ZXing located but could not decode, treating a central logo as
    /// unknown modules rather than wrong ones.
    /// </summary>
    private RawHit? ReadQrWithLogo(LuminanceSource source, bool hybrid, Affine map)
    {
        try
        {
            var bits = new BinaryBitmap(hybrid ? new HybridBinarizer(source) : (Binarizer)new GlobalHistogramBinarizer(source)).BlackMatrix;
            if (bits == null) return null;
            var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = _options.TryHarder };
            if (_options.CharacterSet != null) hints[DecodeHintType.CHARACTER_SET] = _options.CharacterSet;
            var detected = new ZXing.QrCode.Internal.Detector(bits).detect(hints);
            if (detected == null) return null;
            var result = QrLogoDecoder.Decode(detected.Bits, hints);
            if (result?.Text == null) return null;
            return new RawHit(result.Text, BarcodeFormat.QrCode, result.RawBytes, ToPoints(map, detected.Points));
        }
        catch (Exception ex) when (IsZxingFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// ZXing's detectors occasionally index outside the bitmap or trip over their own state on unusual input.
    /// Such a pass counts as having found nothing rather than failing the whole read.
    /// </summary>
    private static bool IsZxingFailure(Exception ex) =>
        ex is IndexOutOfRangeException or ArgumentException or NullReferenceException or InvalidOperationException;

    /// <summary>Maps ZXing's result points to the caller's image, skipping missing ones.</summary>
    private static BarcodePoint[] ToPoints(Affine map, ResultPoint[]? points)
    {
        var result = new List<BarcodePoint>(points?.Length ?? 0);
        if (points != null)
            foreach (var p in points)
            {
                if (p == null) continue;
                map.Apply(p.X, p.Y, out var x, out var y);
                result.Add(new BarcodePoint((float)x, (float)y));
            }
        return [.. result];
    }

    private static readonly (BarcodeFormat Ours, ZxFormat Theirs)[] FormatMap =
    [
        (BarcodeFormat.QrCode, ZxFormat.QR_CODE),
        (BarcodeFormat.DataMatrix, ZxFormat.DATA_MATRIX),
        (BarcodeFormat.Pdf417, ZxFormat.PDF_417),
        (BarcodeFormat.Aztec, ZxFormat.AZTEC),
        (BarcodeFormat.Code128, ZxFormat.CODE_128),
        (BarcodeFormat.Code39, ZxFormat.CODE_39),
        (BarcodeFormat.Code93, ZxFormat.CODE_93),
        (BarcodeFormat.Codabar, ZxFormat.CODABAR),
        (BarcodeFormat.Ean13, ZxFormat.EAN_13),
        (BarcodeFormat.Ean8, ZxFormat.EAN_8),
        (BarcodeFormat.UpcA, ZxFormat.UPC_A),
        (BarcodeFormat.UpcE, ZxFormat.UPC_E),
        (BarcodeFormat.Itf, ZxFormat.ITF),
        (BarcodeFormat.Msi, ZxFormat.MSI),
        (BarcodeFormat.DataBar, ZxFormat.RSS_14),
        (BarcodeFormat.DataBarExpanded, ZxFormat.RSS_EXPANDED),
        (BarcodeFormat.MaxiCode, ZxFormat.MAXICODE),
    ];

    internal static IList<ZxFormat> ToZxing(BarcodeFormat formats)
    {
        var list = new List<ZxFormat>();
        foreach (var (ours, theirs) in FormatMap)
            if ((formats & ours) != 0) list.Add(theirs);
        return list;
    }

    internal static BarcodeFormat FromZxing(ZxFormat format)
    {
        foreach (var (ours, theirs) in FormatMap)
            if (theirs == format) return ours;
        return BarcodeFormat.None;
    }
}
