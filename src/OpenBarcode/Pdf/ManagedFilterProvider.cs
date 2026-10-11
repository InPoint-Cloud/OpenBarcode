using System.Collections.Generic;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Filters.Jbig2.PdfboxJbig2;

namespace InPoint.Cloud.OpenBarcode.Pdf;

/// <summary>PdfPig's built-in stream filters plus a managed JBIG2 decoder, which scanners use for bilevel pages.</summary>
internal sealed class ManagedFilterProvider : BaseFilterProvider
{
    public static readonly IFilterProvider Instance = new ManagedFilterProvider();

    private ManagedFilterProvider() : base(CreateFilters()) { }

    private static Dictionary<string, IFilter> CreateFilters()
    {
        var ascii85 = new Ascii85Filter();
        var asciiHex = new AsciiHexDecodeFilter();
        var ccitt = new CcittFaxDecodeFilter();
        var dct = new DctDecodeFilter();
        var flate = new FlateFilter();
        var jbig2 = new PdfboxJbig2DecodeFilter();
        var jpx = new JpxDecodeFilter();
        var runLength = new RunLengthFilter();
        var lzw = new LzwFilter();

        return new Dictionary<string, IFilter>
        {
            ["ASCII85Decode"] = ascii85,
            ["A85"] = ascii85,
            ["ASCIIHexDecode"] = asciiHex,
            ["AHx"] = asciiHex,
            ["CCITTFaxDecode"] = ccitt,
            ["CCF"] = ccitt,
            ["DCTDecode"] = dct,
            ["DCT"] = dct,
            ["FlateDecode"] = flate,
            ["Fl"] = flate,
            ["JBIG2Decode"] = jbig2,
            ["JPXDecode"] = jpx,
            ["RunLengthDecode"] = runLength,
            ["RL"] = runLength,
            ["LZWDecode"] = lzw,
            ["LZW"] = lzw,
        };
    }
}
