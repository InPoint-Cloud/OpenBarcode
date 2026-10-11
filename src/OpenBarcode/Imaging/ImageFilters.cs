using System;

namespace InPoint.Cloud.OpenBarcode.Imaging
{
    /// <summary>
    /// Clean-up filters for photos and poor scans: lighting correction, adaptive thresholding, noise removal,
    /// sharpening and resampling. Every filter returns a new image and leaves its input untouched.
    /// </summary>
    internal static class ImageFilters
    {
        /// <summary>
        /// Evens out lighting and stretches contrast locally: each pixel is rescaled between the darkest and
        /// lightest values around it, so faint print, dark photos and shadows all end up black on white.
        /// </summary>
        /// <remarks>
        /// Local differences smaller than about fourteen times the noise level (at least 12 grey levels) are not
        /// stretched to full contrast, so blank paper stays light instead of turning into amplified grain.
        /// </remarks>
        public static GrayImage NormalizeLighting(GrayImage image)
        {
            int w = image.Width, h = image.Height;
            float minRange = (float)Math.Min(96, Math.Max(12, 14 * NoiseLevel(image)));
            int block = Clamp(Math.Max(w, h) / 24, 8, 64);
            int bw = (w + block - 1) / block, bh = (h + block - 1) / block;
            var lo = new byte[bw * bh];
            var hi = new byte[bw * bh];
            var px = image.Pixels;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int min = 255, max = 0;
                    int y1 = Math.Min(h, (by + 1) * block), x1 = Math.Min(w, (bx + 1) * block);
                    for (int y = by * block; y < y1; y++)
                    {
                        int row = y * w;
                        for (int x = bx * block; x < x1; x++)
                        {
                            int v = px[row + x];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                    }
                    lo[by * bw + bx] = (byte)min;
                    hi[by * bw + bx] = (byte)max;
                }

            // Widen each block's range to its neighbours, so a block that falls inside a wide bar or a large
            // module still sees the paper next to it.
            var lo2 = new float[bw * bh];
            var hi2 = new float[bw * bh];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int min = 255, max = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = bx + dx, ny = by + dy;
                            if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                            min = Math.Min(min, lo[ny * bw + nx]);
                            max = Math.Max(max, hi[ny * bw + nx]);
                        }
                    lo2[by * bw + bx] = min;
                    hi2[by * bw + bx] = max;
                }

            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                float fy = Math.Max(0, Math.Min(bh - 1, (y + 0.5f) / block - 0.5f));
                int y0 = (int)fy, yb = Math.Min(bh - 1, y0 + 1);
                float ty = fy - y0;
                for (int x = 0; x < w; x++)
                {
                    float fx = Math.Max(0, Math.Min(bw - 1, (x + 0.5f) / block - 0.5f));
                    int x0 = (int)fx, xb = Math.Min(bw - 1, x0 + 1);
                    float tx = fx - x0;
                    float l = Lerp(Lerp(lo2[y0 * bw + x0], lo2[y0 * bw + xb], tx), Lerp(lo2[yb * bw + x0], lo2[yb * bw + xb], tx), ty);
                    float u = Lerp(Lerp(hi2[y0 * bw + x0], hi2[y0 * bw + xb], tx), Lerp(hi2[yb * bw + x0], hi2[yb * bw + xb], tx), ty);
                    float range = u - l;
                    int v = px[y * w + x];
                    // Measured from the light end, so a flat area stays light and only its real contrast is stretched.
                    dst[y * w + x] = ToByte(255 - (u - v) * 255f / Math.Max(range, minRange));
                }
            }
            return new GrayImage(w, h, dst);
        }

        /// <summary>
        /// Estimates the standard deviation of pixel noise from the median difference between horizontal
        /// neighbours; edges are too rare to move the median.
        /// </summary>
        public static double NoiseLevel(GrayImage image)
        {
            var histogram = new int[256];
            int w = image.Width, h = image.Height, step = Math.Max(1, h / 256);
            var px = image.Pixels;
            long count = 0;
            for (int y = 0; y < h; y += step)
            {
                int row = y * w;
                for (int x = 1; x < w; x++)
                {
                    histogram[Math.Abs(px[row + x] - px[row + x - 1])]++;
                    count++;
                }
            }
            long seen = 0;
            for (int d = 0; d < 256; d++)
            {
                seen += histogram[d];
                // For Gaussian noise the median absolute difference of neighbours is about 0.95 sigma.
                if (seen * 2 >= count) return d / 0.954;
            }
            return 0;
        }

        /// <summary>
        /// Thresholds against the mean of a square window around each pixel. Unlike ZXing's row binarizer for
        /// 1D codes, this copes with lighting that changes along the bars.
        /// </summary>
        public static GrayImage AdaptiveThreshold(GrayImage image, int window)
        {
            int w = image.Width, h = image.Height;
            var integral = Integral(image);
            int r = Math.Max(1, window / 2);
            var px = image.Pixels;
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int ya = Math.Max(0, y - r), yb = Math.Min(h, y + r + 1);
                for (int x = 0; x < w; x++)
                {
                    int xa = Math.Max(0, x - r), xb = Math.Min(w, x + r + 1);
                    long sum = integral[yb * (w + 1) + xb] - integral[ya * (w + 1) + xb] - integral[yb * (w + 1) + xa] + integral[ya * (w + 1) + xa];
                    int area = (xb - xa) * (yb - ya);
                    dst[y * w + x] = px[y * w + x] * area < sum ? (byte)0 : (byte)255;
                }
            }
            return new GrayImage(w, h, dst);
        }

        /// <summary>Separable Gaussian blur; removes sensor noise and JPEG blocking.</summary>
        public static GrayImage GaussianBlur(GrayImage image, double sigma) => GaussianBlur(image, sigma, sigma);

        /// <summary>
        /// Gaussian blur with separate horizontal and vertical strength. Blurring only along the bars of a 1D
        /// code averages away noise without merging neighbouring bars.
        /// </summary>
        public static GrayImage GaussianBlur(GrayImage image, double sigmaX, double sigmaY)
        {
            var kx = Kernel(sigmaX, out int rx);
            var ky = Kernel(sigmaY, out int ry);
            int w = image.Width, h = image.Height;
            var px = image.Pixels;
            var tmp = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int i = -rx; i <= rx; i++) acc += kx[i + rx] * px[row + Clamp(x + i, 0, w - 1)];
                    tmp[row + x] = acc;
                }
            }
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int i = -ry; i <= ry; i++) acc += ky[i + ry] * tmp[Clamp(y + i, 0, h - 1) * w + x];
                    dst[y * w + x] = ToByte(acc);
                }
            return new GrayImage(w, h, dst);
        }

        private static float[] Kernel(double sigma, out int radius)
        {
            if (sigma <= 0)
            {
                radius = 0;
                return new[] { 1f };
            }
            radius = Math.Max(1, (int)Math.Ceiling(sigma * 2.5));
            var kernel = new float[2 * radius + 1];
            float total = 0;
            for (int i = -radius; i <= radius; i++) total += kernel[i + radius] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
            return kernel;
        }

        /// <summary>3x3 median filter; removes isolated specks (salt-and-pepper noise, dust on scans).</summary>
        public static GrayImage Median3(GrayImage image)
        {
            int w = image.Width, h = image.Height;
            var px = image.Pixels;
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int r0 = Math.Max(0, y - 1) * w, r1 = y * w, r2 = Math.Min(h - 1, y + 1) * w;
                for (int x = 0; x < w; x++)
                {
                    int xl = x > 0 ? x - 1 : 0, xr = x < w - 1 ? x + 1 : x;
                    int p0 = px[r0 + xl], p1 = px[r0 + x], p2 = px[r0 + xr];
                    int p3 = px[r1 + xl], p4 = px[r1 + x], p5 = px[r1 + xr];
                    int p6 = px[r2 + xl], p7 = px[r2 + x], p8 = px[r2 + xr];
                    // Paeth's 19-exchange median-of-9 network.
                    Sort(ref p1, ref p2); Sort(ref p4, ref p5); Sort(ref p7, ref p8);
                    Sort(ref p0, ref p1); Sort(ref p3, ref p4); Sort(ref p6, ref p7);
                    Sort(ref p1, ref p2); Sort(ref p4, ref p5); Sort(ref p7, ref p8);
                    Sort(ref p0, ref p3); Sort(ref p5, ref p8); Sort(ref p4, ref p7);
                    Sort(ref p3, ref p6); Sort(ref p1, ref p4); Sort(ref p2, ref p5);
                    Sort(ref p4, ref p7); Sort(ref p4, ref p2); Sort(ref p6, ref p4);
                    Sort(ref p4, ref p2);
                    dst[r1 + x] = (byte)p4;
                }
            }
            return new GrayImage(w, h, dst);
        }

        private static void Sort(ref int a, ref int b)
        {
            if (a > b)
            {
                int t = a;
                a = b;
                b = t;
            }
        }

        /// <summary>
        /// Box blur with separate horizontal and vertical radius, in constant time per pixel. Averaging along
        /// the bars of a 1D code removes noise without merging neighbouring bars.
        /// </summary>
        public static GrayImage BoxBlur(GrayImage image, int radiusX, int radiusY)
        {
            int w = image.Width, h = image.Height;
            var src = image.Pixels;
            var tmp = new byte[w * h];
            if (radiusX > 0)
            {
                int n = 2 * radiusX + 1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w, sum = 0;
                    for (int i = -radiusX; i <= radiusX; i++) sum += src[row + Clamp(i, 0, w - 1)];
                    for (int x = 0; x < w; x++)
                    {
                        tmp[row + x] = (byte)((sum + n / 2) / n);
                        sum += src[row + Math.Min(w - 1, x + radiusX + 1)] - src[row + Math.Max(0, x - radiusX)];
                    }
                }
            }
            else
            {
                Buffer.BlockCopy(src, 0, tmp, 0, w * h);
            }
            if (radiusY <= 0) return new GrayImage(w, h, tmp);

            var dst = new byte[w * h];
            int m = 2 * radiusY + 1;
            var sums = new int[w];
            for (int i = -radiusY; i <= radiusY; i++)
            {
                int row = Clamp(i, 0, h - 1) * w;
                for (int x = 0; x < w; x++) sums[x] += tmp[row + x];
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * w, add = Math.Min(h - 1, y + radiusY + 1) * w, sub = Math.Max(0, y - radiusY) * w;
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = (byte)((sums[x] + m / 2) / m);
                    sums[x] += tmp[add + x] - tmp[sub + x];
                }
            }
            return new GrayImage(w, h, dst);
        }

        /// <summary>Unsharp mask: steepens the soft edges of an out-of-focus or motion-blurred code.</summary>
        public static GrayImage Sharpen(GrayImage image, double sigma, float amount)
        {
            var blurred = GaussianBlur(image, sigma);
            var px = image.Pixels;
            var dst = new byte[px.Length];
            for (int i = 0; i < dst.Length; i++)
                dst[i] = ToByte(px[i] + amount * (px[i] - blurred.Pixels[i]));
            return new GrayImage(image.Width, image.Height, dst);
        }

        /// <summary>Bilinear upscale. Unlike nearest-neighbour it keeps the grey edge pixels of photos and scans in proportion.</summary>
        public static GrayImage UpscaleBilinear(GrayImage image, int factor)
        {
            if (factor <= 1) return image;
            int w = image.Width * factor, h = image.Height * factor;
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                float sy = (y + 0.5f) / factor - 0.5f;
                for (int x = 0; x < w; x++)
                    dst[y * w + x] = ToByte(Sample(image, (x + 0.5f) / factor - 0.5f, sy));
            }
            return new GrayImage(w, h, dst);
        }

        /// <summary>
        /// Rotates about the centre by <paramref name="degrees"/> (clockwise on screen), growing the canvas so
        /// nothing is cut off. Uncovered corners are white.
        /// </summary>
        public static GrayImage Rotate(GrayImage image, double degrees, out Affine toSource)
        {
            double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            int w = image.Width, h = image.Height;
            int nw = (int)Math.Ceiling(Math.Abs(w * c) + Math.Abs(h * s));
            int nh = (int)Math.Ceiling(Math.Abs(w * s) + Math.Abs(h * c));
            double cx = w / 2.0, cy = h / 2.0, ncx = nw / 2.0, ncy = nh / 2.0;
            // Destination (u, v) -> source (x, y): rotate back by -degrees about the centres.
            toSource = new Affine(c, s, cx - c * ncx - s * ncy, -s, c, cy + s * ncx - c * ncy);
            var dst = new byte[nw * nh];
            for (int v = 0; v < nh; v++)
                for (int u = 0; u < nw; u++)
                {
                    toSource.Apply(u + 0.5, v + 0.5, out double x, out double y);
                    dst[v * nw + u] = ToByte(Sample(image, (float)(x - 0.5), (float)(y - 0.5)));
                }
            return new GrayImage(nw, nh, dst);
        }

        /// <summary>
        /// Estimates how far the strongest edges are turned away from the image axes, in degrees within
        /// [-45, 45). Bars, modules and finder patterns all have edges at right angles to each other, so the
        /// histogram of edge directions folded onto 90 degrees peaks at the symbol's tilt.
        /// </summary>
        /// <param name="image">The image.</param>
        /// <param name="confidence">Share of edge strength near the peak, from about 0.1 (no preferred direction) to 1.</param>
        public static double DominantAngle(GrayImage image, out double confidence)
        {
            const int Bins = 90;
            var histogram = new double[Bins];
            int w = image.Width, h = image.Height;
            var px = image.Pixels;
            double total = 0;
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    // Sobel.
                    int gx = px[i - w + 1] + 2 * px[i + 1] + px[i + w + 1] - px[i - w - 1] - 2 * px[i - 1] - px[i + w - 1];
                    int gy = px[i + w - 1] + 2 * px[i + w] + px[i + w + 1] - px[i - w - 1] - 2 * px[i - w] - px[i - w + 1];
                    int m = Math.Abs(gx) + Math.Abs(gy);
                    if (m < 64) continue;
                    double angle = Math.Atan2(gy, gx) * 180 / Math.PI; // -180..180
                    angle = ((angle % 90) + 90) % 90;                    // 0..90
                    double weight = (double)m * m;
                    // Split the vote between the two nearest bins.
                    double pos = angle - 0.5;
                    int b0 = (int)Math.Floor(pos);
                    double t = pos - b0;
                    histogram[((b0 % Bins) + Bins) % Bins] += weight * (1 - t);
                    histogram[(((b0 + 1) % Bins) + Bins) % Bins] += weight * t;
                    total += weight;
                }
            }
            confidence = 0;
            if (total <= 0) return 0;

            int best = 0;
            double bestScore = -1;
            for (int b = 0; b < Bins; b++)
            {
                double score = 0;
                for (int d = -2; d <= 2; d++) score += histogram[((b + d) % Bins + Bins) % Bins];
                if (score > bestScore) { bestScore = score; best = b; }
            }
            // Refine with the centroid of the peak.
            double sum = 0, moment = 0;
            for (int d = -2; d <= 2; d++)
            {
                double v = histogram[((best + d) % Bins + Bins) % Bins];
                sum += v;
                moment += v * d;
            }
            confidence = bestScore / total;
            double result = best + 0.5 + (sum > 0 ? moment / sum : 0);
            if (result >= 45) result -= 90;
            return result;
        }

        /// <summary>Bilinear sample; white outside the image.</summary>
        private static float Sample(GrayImage image, float x, float y)
        {
            int w = image.Width, h = image.Height;
            if (x < -1 || y < -1 || x > w || y > h) return 255;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            var px = image.Pixels;
            float a, b, c, d;
            if (x0 >= 0 && y0 >= 0 && x0 + 1 < w && y0 + 1 < h)
            {
                int i = y0 * w + x0;
                a = px[i]; b = px[i + 1]; c = px[i + w]; d = px[i + w + 1];
            }
            else
            {
                a = At(px, w, h, x0, y0); b = At(px, w, h, x0 + 1, y0);
                c = At(px, w, h, x0, y0 + 1); d = At(px, w, h, x0 + 1, y0 + 1);
            }
            return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }

        private static float At(byte[] px, int w, int h, int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 255f : px[y * w + x];

        private static long[] Integral(GrayImage image)
        {
            int w = image.Width, h = image.Height;
            var sums = new long[(w + 1) * (h + 1)];
            var px = image.Pixels;
            for (int y = 0; y < h; y++)
            {
                long row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += px[y * w + x];
                    sums[(y + 1) * (w + 1) + x + 1] = sums[y * (w + 1) + x + 1] + row;
                }
            }
            return sums;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }

    /// <summary>A 2D affine map: (x, y) -> (A x + B y + C, D x + E y + F).</summary>
    internal readonly struct Affine
    {
        public Affine(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }

        public static Affine Identity => new Affine(1, 0, 0, 0, 1, 0);

        public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 0 && E == 1 && F == 0;

        public double A { get; }
        public double B { get; }
        public double C { get; }
        public double D { get; }
        public double E { get; }
        public double F { get; }

        public void Apply(double x, double y, out double rx, out double ry)
        {
            rx = A * x + B * y + C;
            ry = D * x + E * y + F;
        }

        /// <summary>Applies <paramref name="first"/>, then this map.</summary>
        public Affine After(Affine first) => new Affine(
            A * first.A + B * first.D, A * first.B + B * first.E, A * first.C + B * first.F + C,
            D * first.A + E * first.D, D * first.B + E * first.E, D * first.C + E * first.F + F);
    }
}
