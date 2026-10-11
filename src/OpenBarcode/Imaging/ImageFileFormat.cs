namespace InPoint.Cloud.OpenBarcode.Imaging;

/// <summary>File types recognised from their leading bytes.</summary>
public enum ImageFileFormat
{
    /// <summary>Not recognised.</summary>
    Unknown,
    /// <summary>PDF document.</summary>
    Pdf,
    /// <summary>PNG image.</summary>
    Png,
    /// <summary>JPEG image.</summary>
    Jpeg,
    /// <summary>Windows bitmap.</summary>
    Bmp,
    /// <summary>GIF image (first frame).</summary>
    Gif,
    /// <summary>TIFF image, all pages.</summary>
    Tiff,
    /// <summary>Photoshop document (composite image).</summary>
    Psd,
    /// <summary>Netpbm image (PBM, PGM, PPM).</summary>
    Pnm,
    /// <summary>Truevision TGA. Has no signature, so it is tried last.</summary>
    Tga,
}

internal static class FormatSniffer
{
    public static ImageFileFormat Detect(byte[] data)
    {
        var n = data.Length;
        if (n >= 4)
        {
            // PDF headers may be preceded by junk; the spec allows the header within the first 1024 bytes.
            var limit = System.Math.Min(n - 4, 1024);
            for (var i = 0; i <= limit; i++)
                if (data[i] == '%' && data[i + 1] == 'P' && data[i + 2] == 'D' && data[i + 3] == 'F')
                    return ImageFileFormat.Pdf;
        }
        if (n >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return ImageFileFormat.Png;
        if (n >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ImageFileFormat.Jpeg;
        if (n >= 2 && data[0] == 'B' && data[1] == 'M') return ImageFileFormat.Bmp;
        if (n >= 4 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8') return ImageFileFormat.Gif;
        if (n >= 4 && ((data[0] == 'I' && data[1] == 'I' && (data[2] == 42 || data[2] == 43) && data[3] == 0) ||
                       (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && (data[3] == 42 || data[3] == 43))))
            return ImageFileFormat.Tiff;
        if (n >= 4 && data[0] == '8' && data[1] == 'B' && data[2] == 'P' && data[3] == 'S') return ImageFileFormat.Psd;
        if (n >= 3 && data[0] == 'P' && data[1] >= '1' && data[1] <= '6' && IsSpace(data[2])) return ImageFileFormat.Pnm;
        if (n >= 18 && (data[2] == 1 || data[2] == 2 || data[2] == 3 || data[2] == 9 || data[2] == 10 || data[2] == 11) && data[1] <= 1)
            return ImageFileFormat.Tga;
        return ImageFileFormat.Unknown;
    }

    private static bool IsSpace(byte b) => b == ' ' || b == '\n' || b == '\r' || b == '\t';
}
