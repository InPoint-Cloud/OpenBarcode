using System;

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
        if (HasPdfHeader(data)) return ImageFileFormat.Pdf;
        if (data.Length >= 8 && StartsWith(data, "\u0089PNG")) return ImageFileFormat.Png;
        if (StartsWith(data, "\u00FF\u00D8\u00FF")) return ImageFileFormat.Jpeg;
        if (StartsWith(data, "BM")) return ImageFileFormat.Bmp;
        if (StartsWith(data, "GIF8")) return ImageFileFormat.Gif;
        if (IsTiff(data)) return ImageFileFormat.Tiff;
        if (StartsWith(data, "8BPS")) return ImageFileFormat.Psd;
        if (IsPnm(data)) return ImageFileFormat.Pnm;
        if (IsTga(data)) return ImageFileFormat.Tga;
        return ImageFileFormat.Unknown;
    }

    /// <summary>PDF headers may be preceded by junk; the spec allows the header within the first 1024 bytes.</summary>
    private static bool HasPdfHeader(byte[] data)
    {
        var limit = Math.Min(data.Length - 4, 1024);
        for (var i = 0; i <= limit; i++)
            if (data[i] == '%' && data[i + 1] == 'P' && data[i + 2] == 'D' && data[i + 3] == 'F')
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="data"/> begins with the bytes of <paramref name="prefix"/>, one per character.</summary>
    private static bool StartsWith(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }

    /// <summary>Little-endian ("II") or big-endian ("MM") TIFF or BigTIFF.</summary>
    private static bool IsTiff(byte[] data) =>
        data.Length >= 4 && ((StartsWith(data, "II") && IsTiffVersion(data[2]) && data[3] == 0) ||
                             (StartsWith(data, "MM") && data[2] == 0 && IsTiffVersion(data[3])));

    private static bool IsTiffVersion(byte b) => b == 42 || b == 43;

    private static bool IsPnm(byte[] data) =>
        data.Length >= 3 && data[0] == 'P' && data[1] >= '1' && data[1] <= '6' && IsSpace(data[2]);

    /// <summary>A known TGA image type and a colour map flag of 0 or 1.</summary>
    private static bool IsTga(byte[] data) =>
        data.Length >= 18 && Array.IndexOf(TgaImageTypes, data[2]) >= 0 && data[1] <= 1;

    private static readonly byte[] TgaImageTypes = [1, 2, 3, 9, 10, 11];

    private static bool IsSpace(byte b) => b == ' ' || b == '\n' || b == '\r' || b == '\t';
}
