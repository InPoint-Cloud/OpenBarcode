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
    }

    /// <summary>Runs ZXing over a grayscale bitmap with a few preprocessing passes.</summary>
    internal sealed class ZxingDecoder
    {
        private const int MinUsefulSize = 600;
        private const int DownscaleThreshold = 2400;
        private const int MaxRegions = 32;
        private const BarcodeFormat CentreSeeking = BarcodeFormat.DataMatrix | BarcodeFormat.Aztec;

        private readonly BarcodeReaderOptions _options;
        private readonly IList<ZxFormat> _formats;
        private readonly IList<ZxFormat> _centreSeekingFormats;
        private readonly IList<ZxFormat> _linearFormats;

        public ZxingDecoder(BarcodeReaderOptions options)
        {
            _options = options;
            _formats = ToZxing(options.Formats);
            if (_formats.Count == 0) throw new ArgumentException("No barcode formats selected.", nameof(options));
            _centreSeekingFormats = ToZxing(options.Formats & CentreSeeking);
            _linearFormats = ToZxing(options.Formats & BarcodeFormat.AllLinear);
        }

        public List<RawHit> Decode(GrayImage image)
        {
            var (prepared, scale, pad) = Prepare(image);
            var map = new PointMap(scale, pad, 0, 0);

            var hits = Run(prepared, hybrid: true, map, _formats, _options.ReadMultiple);
            if (hits.Count == 0)
                hits = Run(prepared, hybrid: false, map, _formats, _options.ReadMultiple);
            int maxSide = Math.Max(image.Width, image.Height);
            if (hits.Count == 0 && maxSide > DownscaleThreshold)
            {
                // Noisy high-resolution scans sometimes decode better at half size.
                var small = image.DownscaleBox(2);
                int smallPad = PadFor(small);
                hits = Run(small.Pad(smallPad), hybrid: true, new PointMap(0.5, smallPad, 0, 0), _formats, _options.ReadMultiple);
            }

            // 2D detectors are rotation invariant and 1D readers try both directions along a row, so one extra
            // pass over the image turned by 90 degrees, restricted to 1D formats, covers sideways barcodes.
            if (_options.AutoRotate && _linearFormats.Count > 0 && (hits.Count == 0 || _options.ReadMultiple))
            {
                var rotated = prepared.RotateClockwise();
                var rotatedMap = new PointMap(scale, pad, 0, 0, rotatedHeight: prepared.Height);
                hits.AddRange(Run(rotated, hybrid: true, rotatedMap, _linearFormats, _options.ReadMultiple));
            }

            if (_centreSeekingFormats.Count > 0 && (_options.ReadMultiple || hits.Count == 0))
                hits.AddRange(DecodeRegions(image, hits));
            return RemoveOverlaps(hits);
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
        private IEnumerable<RawHit> DecodeRegions(GrayImage image, List<RawHit> found)
        {
            var regions = RegionFinder.Find(image, MaxRegions);
            if (regions.Count <= 1 && found.Count > 0) yield break;
            var seen = new HashSet<string>();
            foreach (var h in found) seen.Add(h.Format + "\n" + h.Text);
            var taken = new List<RawHit>(found);

            foreach (var region in regions)
            {
                if (Covered(region, taken)) continue;
                int margin = Math.Max(8, Math.Max(region.Width, region.Height) / 4);
                var (crop, ox, oy) = RegionFinder.Crop(image, region, margin);
                // The crop keeps the page's resolution, so it only needs the quiet-zone padding, not an upscale.
                int pad = PadFor(crop);
                foreach (var hit in Run(crop.Pad(pad), hybrid: true, new PointMap(1, pad, ox, oy), _centreSeekingFormats, multiple: false))
                {
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
        private static (GrayImage Image, double Scale, int Pad) Prepare(GrayImage image)
        {
            int maxSide = Math.Max(image.Width, image.Height);
            int scale = maxSide < MinUsefulSize ? Math.Min(8, (MinUsefulSize + maxSide - 1) / maxSide) : 1;
            var scaled = image.UpscaleNearest(scale);
            int pad = PadFor(scaled);
            return (scaled.Pad(pad), scale, pad);
        }

        private static int PadFor(GrayImage image) => Clamp(Math.Max(image.Width, image.Height) / 8, 16, 160);

        /// <summary>Maps points in a prepared bitmap back to the caller's image.</summary>
        private readonly struct PointMap
        {
            public PointMap(double scale, int pad, int offsetX, int offsetY, int rotatedHeight = 0)
            {
                Scale = scale; Pad = pad; OffsetX = offsetX; OffsetY = offsetY; RotatedHeight = rotatedHeight;
            }

            /// <summary>When non-zero, points come from the bitmap rotated clockwise; this is its pre-rotation height.</summary>
            public int RotatedHeight { get; }
            public double Scale { get; }
            public int Pad { get; }
            public int OffsetX { get; }
            public int OffsetY { get; }
            public BarcodePoint Apply(float x, float y)
            {
                if (RotatedHeight > 0)
                {
                    float ux = y, uy = RotatedHeight - 1 - x;
                    x = ux;
                    y = uy;
                }
                return new BarcodePoint((float)((x - Pad) / Scale + OffsetX), (float)((y - Pad) / Scale + OffsetY));
            }
        }

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

        private List<RawHit> Run(GrayImage image, bool hybrid, PointMap map, IList<ZxFormat> formats, bool multiple)
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
            if (multiple)
            {
                results = reader.DecodeMultiple(source);
            }
            else
            {
                var single = reader.Decode(source);
                results = single == null ? null : new[] { single };
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
                        if (p != null) points.Add(map.Apply(p.X, p.Y));
                hits.Add(new RawHit(r.Text, format, r.RawBytes, points.ToArray()));
            }
            return hits;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        private static readonly (BarcodeFormat Ours, ZxFormat Theirs)[] Map =
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
            foreach (var (ours, theirs) in Map)
                if ((formats & ours) != 0) list.Add(theirs);
            return list;
        }

        internal static BarcodeFormat FromZxing(ZxFormat format)
        {
            foreach (var (ours, theirs) in Map)
                if (theirs == format) return ours;
            return BarcodeFormat.None;
        }
    }
}
