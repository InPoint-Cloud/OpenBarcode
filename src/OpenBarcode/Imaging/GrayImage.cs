using System;

namespace OpenBarcode.Imaging
{
    /// <summary>An 8-bit grayscale bitmap (0 = black, 255 = white), row-major, top-down, no padding.</summary>
    public sealed class GrayImage
    {
        /// <summary>Wraps an existing luminance buffer of exactly <paramref name="width"/> x <paramref name="height"/> bytes.</summary>
        public GrayImage(int width, int height, byte[] pixels)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (pixels.Length < (long)width * height) throw new ArgumentException("Buffer is smaller than width * height.", nameof(pixels));
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        /// <summary>Creates a white image.</summary>
        public GrayImage(int width, int height) : this(width, height, CreateWhite(width, height)) { }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>Luminance values, <see cref="Width"/> bytes per row.</summary>
        public byte[] Pixels { get; }

        /// <summary>Converts interleaved 8-bit pixels to grayscale. Alpha, when present, is composited over white.</summary>
        /// <param name="data">Pixel buffer.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="layout">Channel order of <paramref name="data"/>.</param>
        /// <param name="stride">Bytes per row, or 0 for tightly packed rows.</param>
        public static GrayImage FromPixels(byte[] data, int width, int height, PixelLayout layout, int stride = 0)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int bpp = layout switch
            {
                PixelLayout.Gray8 => 1,
                PixelLayout.GrayAlpha16 => 2,
                PixelLayout.Rgb24 or PixelLayout.Bgr24 => 3,
                _ => 4,
            };
            if (stride == 0) stride = width * bpp;
            if (data.Length < (long)stride * (height - 1) + (long)width * bpp)
                throw new ArgumentException("Buffer is too small for the given size.", nameof(data));

            var gray = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int src = y * stride;
                int dst = y * width;
                for (int x = 0; x < width; x++, src += bpp, dst++)
                {
                    int r, g, b, a = 255;
                    switch (layout)
                    {
                        case PixelLayout.Gray8: r = g = b = data[src]; break;
                        case PixelLayout.GrayAlpha16: r = g = b = data[src]; a = data[src + 1]; break;
                        case PixelLayout.Rgb24: r = data[src]; g = data[src + 1]; b = data[src + 2]; break;
                        case PixelLayout.Bgr24: b = data[src]; g = data[src + 1]; r = data[src + 2]; break;
                        case PixelLayout.Rgba32: r = data[src]; g = data[src + 1]; b = data[src + 2]; a = data[src + 3]; break;
                        case PixelLayout.Bgra32: b = data[src]; g = data[src + 1]; r = data[src + 2]; a = data[src + 3]; break;
                        case PixelLayout.Argb32: a = data[src]; r = data[src + 1]; g = data[src + 2]; b = data[src + 3]; break;
                        default: throw new ArgumentOutOfRangeException(nameof(layout));
                    }
                    gray[dst] = Composite(Luma(r, g, b), a);
                }
            }
            return new GrayImage(width, height, gray);
        }

        /// <summary>ITU-R BT.601 luma, integer approximation.</summary>
        internal static byte Luma(int r, int g, int b) => (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);

        /// <summary>Composites a gray value with alpha over a white background.</summary>
        internal static byte Composite(int gray, int alpha) =>
            alpha == 255 ? (byte)gray : (byte)((gray * alpha + 255 * (255 - alpha) + 127) / 255);

        /// <summary>Returns a copy with a white border of <paramref name="border"/> pixels on every side.</summary>
        public GrayImage Pad(int border)
        {
            if (border <= 0) return this;
            int w = Width + 2 * border, h = Height + 2 * border;
            var result = new GrayImage(w, h);
            for (int y = 0; y < Height; y++)
                Buffer.BlockCopy(Pixels, y * Width, result.Pixels, (y + border) * w + border, Width);
            return result;
        }

        /// <summary>Returns a copy scaled up by an integer factor (nearest neighbour, keeps edges sharp).</summary>
        public GrayImage UpscaleNearest(int factor)
        {
            if (factor <= 1) return this;
            int w = Width * factor, h = Height * factor;
            var dst = new byte[w * h];
            for (int y = 0; y < Height; y++)
            {
                int row = y * factor * w;
                for (int x = 0; x < Width; x++)
                {
                    byte v = Pixels[y * Width + x];
                    for (int i = 0; i < factor; i++) dst[row + x * factor + i] = v;
                }
                for (int i = 1; i < factor; i++) Buffer.BlockCopy(dst, row, dst, row + i * w, w);
            }
            return new GrayImage(w, h, dst);
        }

        /// <summary>Returns a copy scaled down by an integer factor using box averaging.</summary>
        public GrayImage DownscaleBox(int factor)
        {
            if (factor <= 1) return this;
            int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
            var dst = new byte[w * h];
            int area = factor * factor;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int row = (y * factor + dy) * Width + x * factor;
                        for (int dx = 0; dx < factor; dx++) sum += Pixels[row + dx];
                    }
                    dst[y * w + x] = (byte)(sum / area);
                }
            }
            return new GrayImage(w, h, dst);
        }

        /// <summary>Returns a copy rotated 90 degrees clockwise: source (x, y) lands at (Height - 1 - y, x).</summary>
        public GrayImage RotateClockwise()
        {
            var dst = new byte[Pixels.Length];
            int h = Height;
            for (int y = 0; y < Height; y++)
            {
                int src = y * Width, col = h - 1 - y;
                for (int x = 0; x < Width; x++) dst[x * h + col] = Pixels[src + x];
            }
            return new GrayImage(Height, Width, dst);
        }

        private static byte[] CreateWhite(int width, int height)
        {
            var buffer = new byte[checked(width * height)];
#if NETSTANDARD2_0
            for (int i = 0; i < buffer.Length; i++) buffer[i] = 255;
#else
            Array.Fill(buffer, (byte)255);
#endif
            return buffer;
        }
    }

    /// <summary>Channel layouts accepted by <see cref="GrayImage.FromPixels"/>.</summary>
    public enum PixelLayout
    {
        /// <summary>One byte of luminance per pixel.</summary>
        Gray8,
        /// <summary>Luminance then alpha.</summary>
        GrayAlpha16,
        /// <summary>Red, green, blue.</summary>
        Rgb24,
        /// <summary>Blue, green, red (System.Drawing / GDI order).</summary>
        Bgr24,
        /// <summary>Red, green, blue, alpha.</summary>
        Rgba32,
        /// <summary>Blue, green, red, alpha (System.Drawing / SkiaSharp default on little-endian).</summary>
        Bgra32,
        /// <summary>Alpha, red, green, blue.</summary>
        Argb32,
    }
}
