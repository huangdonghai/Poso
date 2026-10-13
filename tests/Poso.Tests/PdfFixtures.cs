using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Poso.Tests;

/// <summary>Object definition: object number plus dictionary/plain body, with optional stream data.</summary>
public readonly record struct PdfObjectDefinition(int Number, string Body, byte[]? StreamData = null);

/// <summary>Builds test PDFs in memory: traditional xref table, Flate compressed stream, object stream + xref stream variants.</summary>
public static class PdfFixtures
{
    public static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    public static PdfObjectDefinition Stream(int number, string extraDictionary, byte[] data) =>
        new(number, $"<< /Length {data.Length}{(extraDictionary.Length > 0 ? " " + extraDictionary : string.Empty)} >>", data);

    /// <summary>Builds a PDF using a traditional xref table (the catalog is object 1 by convention).</summary>
    public static byte[] Build(int rootNumber, params PdfObjectDefinition[] objects)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        var offsets = new Dictionary<int, int>();
        foreach (PdfObjectDefinition definition in objects)
        {
            offsets[definition.Number] = (int)stream.Position;
            WriteAscii(stream, $"{definition.Number} 0 obj\n{definition.Body}\n");
            if (definition.StreamData is { } streamData)
            {
                WriteAscii(stream, "stream\n");
                stream.Write(streamData, 0, streamData.Length);
                WriteAscii(stream, "\nendstream\n");
            }

            WriteAscii(stream, "endobj\n");
        }

        int maxNumber = objects.Max(o => o.Number);
        int xrefOffset = (int)stream.Position;
        WriteAscii(stream, $"xref\n0 {maxNumber + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= maxNumber; i++)
        {
            WriteAscii(stream, offsets.TryGetValue(i, out int offset) ? $"{offset:D10} 00000 n \n" : "0000000000 65535 f \n");
        }

        WriteAscii(stream, $"trailer\n<< /Size {maxNumber + 1} /Root {rootNumber} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return stream.ToArray();
    }

    /// <summary>A simple two-page document; page 2 is rotated 90 degrees.</summary>
    public static byte[] CreateSimplePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << >> >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 50 Td (Hello Poso) Tj ET")),
        new PdfObjectDefinition(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 150] /Rotate 90 /Contents 6 0 R /Resources << >> >>"),
        Stream(6, string.Empty, Encoding.ASCII.GetBytes("0 0 1 rg 10 10 50 50 re f")));

    /// <summary>
    /// A drawing test page (200x100): a filled rectangle, a stroked line and text using a
    /// simple Type1 font with WinAnsiEncoding.
    /// </summary>
    public static byte[] CreateGraphicsPdf()
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = "0.2 0.4 0.9 rg 10 10 80 40 re f\n" +
                         "0.9 0.1 0.1 RG 2 w 20 70 m 180 70 l S\n" +
                         "BT /F1 12 Tf 20 50 Td (Hello Poso) Tj ET\n" +
                         "q 1 0 0 1 5 5 cm 0 0 1 rg 5 5 10 10 re f Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font));
    }

    /// <summary>A page holding a single glyph of a Helvetica-like font, baseline at user y=20.</summary>
    public static byte[] CreateGlyphPdf(char glyph)
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = $"BT /F1 60 Tf 60 20 Td ({glyph}) Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font));
    }

    /// <summary>A page with an uncompressed 2x2 RGB image XObject scaled into a 100x50 rect.</summary>
    public static byte[] CreateImagePdf()
    {
        byte[] pixels =
        {
            255, 0, 0, 0, 255, 0, // red, green
            0, 0, 255, 255, 255, 0, // blue, yellow
        };
        string content = "q 100 0 0 50 0 0 cm /Im1 Do Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>", pixels));
    }

    /// <summary>A document whose content stream is Flate compressed.</summary>
    public static byte[] CreateFlatePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << >> >>"),
        Stream(4, "/Filter /FlateDecode", Flate(Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 40 Td (Compressed Content) Tj ET"))));

    /// <summary>
    /// A page with an /Indexed image whose palette lookup is truncated (only 4 bytes for
    /// hival 255): decoders must tolerate the malformed palette instead of crashing.
    /// </summary>
    public static byte[] CreateTruncatedIndexedPdf()
    {
        byte[] indices = { 0, 255, 1, 128 }; // 2x2, 8 bits per component
        string content = "q 100 0 0 50 0 0 cm /Im1 Do Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace [/Indexed /DeviceRGB 255 <AABBCCDD>] /BitsPerComponent 8 /Length {indices.Length} >>", indices));
    }

    /// <summary>
    /// A document with an outline (two top-level items, one with a child, one via a named
    /// destination) and two link annotations on page 1 (a URI link and an internal GoTo).
    /// </summary>
    public static byte[] CreateOutlinePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R /Dests << /chap2 [5 0 R /XYZ 0 792 0] >> >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Annots [10 0 R 11 0 R] >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 10 10 50 20 re f")),
        new PdfObjectDefinition(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 6 0 R >>"),
        Stream(6, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 10 10 50 20 re f")),
        new PdfObjectDefinition(7, "<< /Type /Outlines /First 8 0 R /Last 9 0 R /Count 2 >>"),
        new PdfObjectDefinition(8, "<< /Title (Chapter 1) /Parent 7 0 R /Next 9 0 R /Dest [3 0 R /Fit] >>"),
        new PdfObjectDefinition(9, "<< /Title (Chapter 2) /Parent 7 0 R /Prev 8 0 R /First 12 0 R /Last 12 0 R /Count 1 /Dest (chap2) >>"),
        new PdfObjectDefinition(10, "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /Border [0 0 0] /A << /S /URI /URI (https://example.com) >> >>"),
        new PdfObjectDefinition(11, "<< /Type /Annot /Subtype /Link /Rect [10 40 100 60] /Border [0 0 0] /Dest [5 0 R /Fit] >>"),
        new PdfObjectDefinition(12, "<< /Title (Section 2.1) /Parent 9 0 R /Dest [5 0 R /XYZ 0 792 1] >>"));

    /// <summary>
    /// A document that stores page objects in an object stream (ObjStm) and describes the
    /// cross-reference with an xref stream.
    /// Object layout: 1=Catalog, 3/4 inside object stream 5, 6=content stream, 7=xref stream.
    /// </summary>
    public static byte[] CreateObjectStreamPdf()
    {
        string pageObject = "<< /Type /Page /Parent 4 0 R /MediaBox [0 0 120 60] /Resources << >> /Contents 6 0 R >>";
        string pagesObject = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        string header = $"3 0 4 {pageObject.Length + 1}\n";
        byte[] objectStreamData = Encoding.ASCII.GetBytes(header + pageObject + "\n" + pagesObject);
        byte[] content = Encoding.ASCII.GetBytes("BT /F1 10 Tf 5 30 Td (InObjStm) Tj ET");

        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        var offsets = new Dictionary<int, int>();
        void WriteObject(int number, string body, byte[]? streamData = null)
        {
            offsets[number] = (int)stream.Position;
            WriteAscii(stream, $"{number} 0 obj\n{body}\n");
            if (streamData is not null)
            {
                WriteAscii(stream, "stream\n");
                stream.Write(streamData, 0, streamData.Length);
                WriteAscii(stream, "\nendstream\n");
            }

            WriteAscii(stream, "endobj\n");
        }

        WriteObject(1, "<< /Type /Catalog /Pages 4 0 R >>");
        WriteObject(5, $"<< /Type /ObjStm /N 2 /First {header.Length} /Length {objectStreamData.Length} >>", objectStreamData);
        WriteObject(6, $"<< /Length {content.Length} >>", content);

        // xref stream (object 7): W [1 2 1], 8 entries
        int xrefOffset = (int)stream.Position;
        offsets[7] = xrefOffset;
        byte[] xrefData = BuildXrefStreamData(offsets, size: 8, xrefStreamObjectNumber: 7);
        byte[] xrefCompressed = Flate(xrefData);
        WriteObject(7, $"<< /Type /Xref /Size 8 /Index [0 8] /W [1 2 1] /Filter /FlateDecode /Root 1 0 R /Length {xrefCompressed.Length} >>", xrefCompressed);

        WriteAscii(stream, $"startxref\n{xrefOffset}\n%%EOF\n");
        return stream.ToArray();
    }

    private static byte[] BuildXrefStreamData(Dictionary<int, int> offsets, int size, int xrefStreamObjectNumber)
    {
        var data = new List<byte>(size * 4);
        for (int i = 0; i < size; i++)
        {
            if (i == 0)
            {
                WriteEntry(data, 0, 0, 255); // Free list head entry
            }
            else if (offsets.TryGetValue(i, out int offset))
            {
                WriteEntry(data, 1, offset, 0);
            }
            else if (i == 3)
            {
                WriteEntry(data, 2, 5, 0); // Index 0 in object stream 5
            }
            else if (i == 4)
            {
                WriteEntry(data, 2, 5, 1); // Index 1 in object stream 5
            }
            else
            {
                WriteEntry(data, 0, 0, 255);
            }
        }

        return data.ToArray();
    }

    private static void WriteEntry(List<byte> data, byte type, int field2, byte field3)
    {
        data.Add(type);
        data.Add((byte)(field2 >> 8));
        data.Add((byte)field2);
        data.Add(field3);
    }

    private static void WriteAscii(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
