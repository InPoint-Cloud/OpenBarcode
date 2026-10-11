using System;
using System.Collections.Generic;
using InPoint.Cloud.OpenBarcode.Imaging;
using ZXing;
using ZXing.Common;
using ZxFormat = ZXing.BarcodeFormat;

namespace InPoint.Cloud.OpenBarcode.Decoding
{
    /// <summary>A barcode found in one bitmap, before it is tagged with where the bitmap came from.</summary>
    internal readonly struct RawHit
    {
        public RawHit(string text, BarcodeFormat format, byte[]? rawBytes, BarcodePoint[] points)
        {
            Text = text;
            Format = format;
            RawBytes = rawBytes;
            Points = points;
        }

        public string Text { get; }
        public BarcodeFormat Format { get; }
        public byte[]? RawBytes { get; }
        public BarcodePoint[] Points { get; }

        /// <summary>Returns a copy with every point mapped through <paramref name="map"/>.</summary>
        public RawHit Transform(Affine map)
        {
            if (map.IsIdentity) return this;
            var points = new BarcodePoint[Points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                map.Apply(Points[i].X, Points[i].Y, out double x, out double y);
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
        private static readonly double[] SkewAngles = { 45, 135 };
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
            var hits = _formats.Count > 0 ? DecodePlain(image) : new List<RawHit>();
            if (enhance && _formats.Count > 0)
            {
                var ballot = new Ballot();
                if (hits.Exists(h => (h.Format & WeaklyChecked) != 0))
                {
                    // Noise and texture can pass for a short 1D code. Keep such a read only if a second look,
                    // with a different binarizer and corrected lighting, finds it too.
                    ballot.Add(hits);
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
                int pad = PadFor(image);
                hits = Run(image.Pad(pad), hybrid: true, Map(1, pad), _formats, _options.ReadMultiple);
            }
            int maxSide = Math.Max(image.Width, image.Height);
            if (hits.Count == 0 && maxSide > DownscaleThreshold)
            {
                // Noisy high-resolution scans sometimes decode better at half size.
                var small = image.DownscaleBox(2);
                int smallPad = PadFor(small);
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

        /// <summary>Weakly checked codes long enough that a second read of the same picture confirms them.</summary>
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
            return new List<RawHit>();
        }

        /// <summary>An independent look at an image for confirming weakly checked reads.</summary>
        private List<RawHit> SecondOpinion(GrayImage image)
        {
            var (prepared, map) = Prepare(image);
            var hits = Run(prepared, hybrid: false, map, _formats, _options.ReadMultiple);
            var normalized = ImageFilters.NormalizeLighting(image);
            int pad = PadFor(normalized);
            hits.AddRange(Run(normalized.Pad(pad), hybrid: true, Map(1, pad), _formats, _options.ReadMultiple));
            if (_options.AutoRotate && _linearFormats.Count > 0)
                hits.AddRange(RunRotated(normalized.Pad(pad), Map(1, pad)));
            return hits;
        }

        /// <summary>Counts in how many passes each code was read, so weakly checked codes can require two.</summary>
        private sealed class Ballot
        {
            private readonly Dictionary<string, (RawHit Hit, int Count)> _votes = new();

            private static string Key(RawHit hit) => hit.Format + "\n" + hit.Text;

            /// <summary>Records one pass; a code read several times within the pass counts once.</summary>
            public void Add(List<RawHit> pass)
            {
                var seen = new HashSet<string>();
                foreach (var hit in pass)
                {
                    string key = Key(hit);
                    if (!seen.Add(key)) continue;
                    _votes[key] = _votes.TryGetValue(key, out var v) ? (v.Hit, v.Count + 1) : (hit, 1);
                }
            }

            private bool IsTrusted(RawHit hit) =>
                (hit.Format & WeaklyChecked) == 0 || (_votes.TryGetValue(Key(hit), out var v) && v.Count >= 2);

            /// <summary>
            /// The hits of the latest pass that can be trusted, plus weakly checked codes it confirmed that an
            /// earlier pass found first. Empty when the pass found nothing trustworthy.
            /// </summary>
            public List<RawHit> Trusted(List<RawHit> pass)
            {
                var result = pass.FindAll(IsTrusted);
                if (result.Count == 0) return result;
                foreach (var vote in _votes.Values)
                    if (vote.Count >= 2 && !result.Exists(h => Key(h) == Key(vote.Hit)))
                        result.Add(vote.Hit);
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
            int maxSide = Math.Max(work.Width, work.Height);

            var normalized = ImageFilters.NormalizeLighting(work);
            yield return DecodeVariant(normalized, toImage, ballot);

            // ZXing's Aztec and PDF417 detectors give up beyond about 10 degrees of tilt, and 1D readers beyond
            // about 15. Turn the picture so the symbol's edges line up with the axes.
            double tilt = ImageFilters.DominantAngle(normalized, out double confidence);
            if (Math.Abs(tilt) >= MinDeskewAngle && confidence >= MinDeskewConfidence)
            {
                var level = ImageFilters.Rotate(normalized, -tilt, out var toWork);
                yield return DecodeVariant(level, toImage.After(toWork), ballot);
            }

            // A window a few modules wide: wide enough to span the widest bar, small enough to follow shadows.
            int window = Clamp(maxSide / 20, 15, 81);
            yield return DecodeVariant(ImageFilters.AdaptiveThreshold(normalized, window), toImage, ballot);

            // Sensor noise, JPEG artefacts and specks.
            yield return DecodeVariant(ImageFilters.GaussianBlur(ImageFilters.Median3(normalized), 1.0), toImage, ballot);

            if (_linearFormats.Count > 0)
            {
                // Heavy noise on 1D codes: average along the bars only, so neighbouring bars stay apart.
                int along = Math.Max(3, maxSide / 100);
                int pad = PadFor(work);
                var upright = ImageFilters.BoxBlur(normalized, 0, along).Pad(pad);
                var hits = Run(upright, hybrid: true, toImage.After(Map(1, pad)), _linearFormats, _options.ReadMultiple);
                Recheck(upright, toImage.After(Map(1, pad)), hits, ballot, rotated: false);
                if (_options.AutoRotate)
                {
                    var sideways = ImageFilters.BoxBlur(normalized, along, 0).Pad(pad);
                    var sidewaysHits = RunRotated(sideways, toImage.After(Map(1, pad)));
                    Recheck(sideways, toImage.After(Map(1, pad)), sidewaysHits, ballot);
                    hits.AddRange(sidewaysHits);
                }
                yield return hits;
            }

            // Out of focus: steepen the edges.
            yield return DecodeVariant(ImageFilters.Sharpen(normalized, 2.0, 1.5f), toImage, ballot);

            // Last resort for 1D and PDF417 codes the tilt estimate missed: try the diagonals.
            if (_options.AutoRotate && _rowScanFormats.Count > 0)
            {
                foreach (double angle in SkewAngles)
                {
                    var rotated = ImageFilters.Rotate(normalized, angle, out var toWork);
                    int pad = PadFor(rotated);
                    var map = toImage.After(toWork).After(Map(1, pad));
                    var hits = Run(rotated.Pad(pad), hybrid: true, map, _rowScanFormats, _options.ReadMultiple);
                    Recheck(rotated.Pad(pad), map, hits, ballot, rotated: false);
                    yield return hits;
                }
            }
        }

        /// <summary>One clean-up variant: the full pass, the sideways 1D pass and the 2D region search.</summary>
        private List<RawHit> DecodeVariant(GrayImage variant, Affine toImage, Ballot ballot)
        {
            int maxSide = Math.Max(variant.Width, variant.Height);
            if (maxSide < MinUsefulSize)
            {
                int factor = Math.Min(4, (MinUsefulSize + maxSide - 1) / maxSide);
                variant = ImageFilters.UpscaleBilinear(variant, factor);
                toImage = toImage.After(Scale(1.0 / factor));
            }
            int pad = PadFor(variant);
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

        private List<RawHit> RunRotated(GrayImage prepared, Affine map, bool hybrid = true)
        {
            // Source (x, y) lands at (H - 1 - y, x), so rotated (u, v) came from (v, H - 1 - u).
            var unrotate = new Affine(0, 1, 0, -1, 0, prepared.Height - 1);
            return Run(prepared.RotateClockwise(), hybrid, map.After(unrotate), _linearFormats, _options.ReadMultiple);
        }

        /// <summary>
        /// When a clean-up pass read nothing but weakly checked codes, reads the same bitmap again with ZXing's
        /// other binarizer, so an EAN-13 or UPC-A that only this clean-up brings out (a blurred one, say) gets
        /// its second vote. Sideways too when <c>rotated</c>, for hits from the quarter-turn pass.
        /// </summary>
        private void Recheck(GrayImage prepared, Affine map, List<RawHit> hits, Ballot ballot, bool rotated = true)
        {
            // Only the long retail codes: short ones (UPC-E, EAN-8) come out of noise and of other 1D symbols
            // the same way on every binarizer, so a re-read of the same picture proves nothing for them.
            var claims = hits.FindAll(h => (h.Format & LongRetail) != 0);
            if (claims.Count == 0 || hits.Exists(h => (h.Format & WeaklyChecked) == 0)) return;
            var again = Run(prepared, hybrid: false, map, _linearFormats, _options.ReadMultiple);
            if (rotated && _options.AutoRotate) again.AddRange(RunRotated(prepared, map, hybrid: false));
            ballot.Add(again.FindAll(a => claims.Exists(c => c.Format == a.Format && c.Text == a.Text)));
        }

        /// <summary>
        /// Drops duplicates and partial reads: a hit whose centre lies inside a larger hit is usually a fragment
        /// of it (for example a UPC-E read from half of an EAN-13).
        /// </summary>
        private static List<RawHit> RemoveOverlaps(List<RawHit> hits)
        {
            if (hits.Count < 2) return hits;
            var ordered = new List<(RawHit Hit, float MinX, float MinY, float MaxX, float MaxY, float Size)>();
            foreach (var hit in hits)
            {
                if (hit.Points.Length == 0) { ordered.Add((hit, 0, 0, 0, 0, -1)); continue; }
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in hit.Points)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
                ordered.Add((hit, minX, minY, maxX, maxY, Math.Max(maxX - minX, maxY - minY)));
            }
            ordered.Sort((a, b) => b.Size.CompareTo(a.Size));

            var kept = new List<(RawHit Hit, float MinX, float MinY, float MaxX, float MaxY, float Size)>();
            var seen = new HashSet<string>();
            foreach (var h in ordered)
            {
                if (!seen.Add(h.Hit.Format + "\n" + h.Hit.Text)) continue;
                if (h.Size >= 0)
                {
                    float cx = (h.MinX + h.MaxX) / 2, cy = (h.MinY + h.MaxY) / 2;
                    bool inside = false;
                    foreach (var k in kept)
                    {
                        if (k.Size <= 0) continue;
                        float margin = Math.Max(10, k.Size / 10);
                        if (cx >= k.MinX - margin && cx <= k.MaxX + margin && cy >= k.MinY - margin && cy <= k.MaxY + margin)
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (inside || IsFragmentOfKept(h.Hit, kept)) continue;
                }
                kept.Add(h);
            }

            // Restore discovery order.
            var result = new List<RawHit>(kept.Count);
            foreach (var hit in hits)
                foreach (var k in kept)
                    if (ReferenceEquals(k.Hit.Points, hit.Points)) { result.Add(hit); break; }
            return result;
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
            var seen = new HashSet<string>();
            foreach (var h in found) seen.Add(h.Format + "\n" + h.Text);
            var taken = new List<RawHit>(found);
            var inImage = new List<RawHit>();

            foreach (var region in regions)
            {
                if (Covered(region, inImage) || (toImage.IsIdentity && Covered(region, taken))) continue;
                int margin = Math.Max(8, Math.Max(region.Width, region.Height) / 4);
                var (crop, ox, oy) = RegionFinder.Crop(image, region, margin);
                // The crop keeps the page's resolution, so it only needs the quiet-zone padding, not an upscale.
                int pad = PadFor(crop);
                var cropMap = new Affine(1, 0, ox - pad, 0, 1, oy - pad);
                var locals = Run(crop.Pad(pad), hybrid: true, cropMap, _centreSeekingFormats, multiple: false);
                if (locals.Count == 0 && sampleDataMatrix && (_options.Formats & BarcodeFormat.DataMatrix) != 0
                    && DataMatrixSampler.Read(crop) is DataMatrixSampler.Result sampled)
                {
                    var corners = Array.ConvertAll(sampled.Corners, p => new BarcodePoint(p.X + ox, p.Y + oy));
                    locals.Add(new RawHit(sampled.Text, BarcodeFormat.DataMatrix, sampled.RawBytes, corners));
                }
                foreach (var local in locals)
                {
                    inImage.Add(local);
                    var hit = local.Transform(toImage);
                    taken.Add(hit);
                    if (seen.Add(hit.Format + "\n" + hit.Text)) yield return hit;
                }
                if (!_options.ReadMultiple && taken.Count > found.Count) yield break;
            }
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
            int maxSide = Math.Max(image.Width, image.Height);
            int scale = maxSide < MinUsefulSize ? Math.Min(8, (MinUsefulSize + maxSide - 1) / maxSide) : 1;
            var scaled = image.UpscaleNearest(scale);
            int pad = PadFor(scaled);
            return (scaled.Pad(pad), Map(scale, pad));
        }

        private static int PadFor(GrayImage image) => Clamp(Math.Max(image.Width, image.Height) / 8, 16, 160);

        /// <summary>Maps a bitmap that was scaled by <paramref name="scale"/> and then padded back to the original.</summary>
        private static Affine Map(double scale, int pad) => new Affine(1 / scale, 0, -pad / scale, 0, 1 / scale, -pad / scale);

        private static Affine Scale(double factor) => new Affine(factor, 0, 0, 0, factor, 0);

        /// <summary>Short symbologies that ZXing also "finds" in part of a longer barcode's bars.</summary>
        private const BarcodeFormat FragmentProne =
            BarcodeFormat.UpcE | BarcodeFormat.Ean8 | BarcodeFormat.Msi | BarcodeFormat.Codabar | BarcodeFormat.Itf;

        /// <summary>
        /// True when a fragment-prone 1D hit lies along a longer 1D hit's scan line, within the height its bars
        /// plausibly have.
        /// </summary>
        private static bool IsFragmentOfKept(RawHit hit, List<(RawHit Hit, float MinX, float MinY, float MaxX, float MaxY, float Size)> kept)
        {
            if ((hit.Format & FragmentProne) == 0 || hit.Points.Length != 2) return false;
            foreach (var k in kept)
            {
                if ((k.Hit.Format & BarcodeFormat.AllLinear) == 0 || k.Hit.Points.Length != 2) continue;
                var a = k.Hit.Points[0];
                var b = k.Hit.Points[1];
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1) continue;
                double ux = dx / length, uy = dy / length;
                bool along = true;
                foreach (var p in hit.Points)
                {
                    double t = (p.X - a.X) * ux + (p.Y - a.Y) * uy;        // position along the longer code
                    double d = Math.Abs(-(p.X - a.X) * uy + (p.Y - a.Y) * ux); // distance from its scan line
                    if (t < -0.05 * length || t > 1.05 * length || d > 0.5 * length) { along = false; break; }
                }
                if (along) return true;
            }
            return false;
        }

        private List<RawHit> Run(GrayImage image, bool hybrid, Affine map, IList<ZxFormat> formats, bool multiple)
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

            var source = new RGBLuminanceSource(image.Pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);

            Result[]? results;
            try
            {
                if (multiple)
                {
                    results = reader.DecodeMultiple(source);
                }
                else
                {
                    var single = reader.Decode(source);
                    results = single == null ? null : new[] { single };
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is NullReferenceException || ex is InvalidOperationException)
            {
                // ZXing's detectors occasionally index outside the bitmap on unusual input. Treat that pass as
                // having found nothing rather than failing the whole read.
                return new List<RawHit>();
            }

            var hits = new List<RawHit>();
            if (results == null) return hits;
            foreach (var r in results)
            {
                if (r == null || r.Text == null) continue;
                var format = FromZxing(r.BarcodeFormat);
                if (format == BarcodeFormat.None) continue;
                var points = new List<BarcodePoint>(r.ResultPoints?.Length ?? 0);
                if (r.ResultPoints != null)
                    foreach (var p in r.ResultPoints)
                        if (p != null) points.Add(ToPoint(map, p.X, p.Y));
                hits.Add(new RawHit(r.Text, format, r.RawBytes, points.ToArray()));
            }
            return hits;
        }

        private static BarcodePoint ToPoint(Affine map, double x, double y)
        {
            map.Apply(x, y, out double rx, out double ry);
            return new BarcodePoint((float)rx, (float)ry);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        private static readonly (BarcodeFormat Ours, ZxFormat Theirs)[] FormatMap =
        {
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
        };

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
}
