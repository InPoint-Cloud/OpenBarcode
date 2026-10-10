using System.Globalization;
using System.Text;
using ZXing.Common;

namespace OpenBarcode.Tests;

/// <summary>
/// A tiny PDF writer for tests. It gives exact control over content-stream operators and image
/// dictionaries, which a general-purpose PDF library hides.
/// </summary>
internal sealed class MiniPdf
{
    private readonly List<byte[]> _objects = new();
    private readonly List<int> _pageIds = new();

    public sealed class PageBuilder
    {
        internal readonly StringBuilder Content = new();
        internal readonly Dictionary<string, int> Images = new();
        public double Width { get; init; } = 595;
        public double Height { get; init; } = 842;

        public PageBuilder Op(string op) { Content.Append(op).Append('\n'); return this; }

        /// <summary>Draws a barcode as filled rectangles, one per run of black modules.</summary>
        public PageBuilder FilledBarcode(BitMatrix m, double x, double y, double module, double barHeight = 50, bool singlePath = false)
        {
            bool linear = m.Height == 1;
            Op("0 g");
            for (int row = 0; row < m.Height; row++)
            {
                for (int col = 0; col < m.Width;)
                {
                    if (!m[col, row]) { col++; continue; }
                    int start = col;
                    while (col < m.Width && m[col, row]) col++;
                    double rx = x + start * module, rw = (col - start) * module;
                    double ry = linear ? y : y + (m.Height - 1 - row) * module;
                    double rh = linear ? barHeight : module;
                    Op(F($"{rx} {ry} {rw} {rh} re") + (singlePath ? "" : " f"));
                }
            }
            if (singlePath) Op("f");
            return this;
        }

        /// <summary>Draws a 1D barcode as stroked vertical lines, one per module column.</summary>
        public PageBuilder StrokedBarcode(BitMatrix m, double x, double y, double module, double barHeight = 50)
        {
            Op("0 G").Op(F($"{module} w"));
            for (int col = 0; col < m.Width; col++)
                if (m[col, 0])
                {
                    double cx = x + (col + 0.5) * module;
                    Op(F($"{cx} {y} m {cx} {y + barHeight} l S"));
                }
            return this;
        }

        public PageBuilder DrawImage(string name, double x, double y, double w, double h) =>
            Op("q").Op(F($"{w} 0 0 {h} {x} {y} cm")).Op($"/{name} Do").Op("Q");
    }

    public int AddImage(string dictionary, byte[] data) =>
        AddObject(Encoding.ASCII.GetBytes($"<< /Type /XObject /Subtype /Image {dictionary} /Length {data.Length} >>\nstream\n"), data,
            Encoding.ASCII.GetBytes("\nendstream"));

    public void AddPage(PageBuilder page)
    {
        var content = Encoding.ASCII.GetBytes(page.Content.ToString());
        int contentId = AddObject(Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n"), content, Encoding.ASCII.GetBytes("\nendstream"));
        var xobjects = string.Join(" ", page.Images.Select(kv => $"/{kv.Key} {kv.Value} 0 R"));
        // The parent reference is patched in Build: the page tree object is always object 1.
        int pageId = AddObject(Encoding.ASCII.GetBytes(F(
            $"<< /Type /Page /Parent 1 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Resources << /XObject << {xobjects} >> >> /Contents {contentId} 0 R >>")));
        _pageIds.Add(pageId);
    }

    public byte[] Build()
    {
        var all = new List<byte[]>();
        var kids = string.Join(" ", _pageIds.Select(id => $"{id} 0 R"));
        all.Add(Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{kids}] /Count {_pageIds.Count} >>"));
        all.AddRange(_objects);
        int catalogId = all.Count + 1;
        all.Add(Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 1 0 R >>"));

        var ms = new MemoryStream();
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        W("%PDF-1.7\n%\xE2\xE3\n");
        var offsets = new List<long>();
        for (int i = 0; i < all.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n");
            ms.Write(all[i]);
            W("\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {all.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {all.Count + 1} /Root {catalogId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Object ids start at 2 because the page tree is written first as object 1.</summary>
    private int AddObject(params byte[][] parts)
    {
        _objects.Add(parts.SelectMany(p => p).ToArray());
        return _objects.Count + 1;
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
