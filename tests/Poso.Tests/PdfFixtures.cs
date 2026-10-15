using System.Collections.Generic;
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

    /// <summary>
    /// A minimal CID-keyed CFF program (Top DICT has ROS, no FDArray - the reader only needs the ROS,
    /// charset and CharStrings). Its charset is deliberately NON-identity (GID 1..4 -> CID 300,100,250,7)
    /// so a test can prove the CID->GID map is read from the charset rather than assumed to be identity.
    /// Returns the raw CFF bytes plus the expected glyph count and the (CID -> GID) pairs.
    /// </summary>
    public static byte[] BuildCidKeyedCffProgram(out int glyphCount, out (int Cid, int Gid)[] cidToGid)
    {
        glyphCount = 5; // GID 0..4; the charset covers GID 1..4 (the four CIDs below)
        cidToGid = [(300, 1), (100, 2), (250, 3), (7, 4)];

        // Top DICT: ROS(12 30) with dummy operands, charset offset (op 15), CharStrings offset (op 17).
        // Offsets are computed from the fixed layout below: charset at 32, CharStrings at 41.
        var dict = new List<byte> { 0xef, 0xef, 0xef, 0x0c, 0x1e };        // 100 100 100 ROS
        dict.AddRange([0x1c, 0x00, 0x20, 0x0f]);                            // 32 charset
        dict.AddRange([0x1c, 0x00, 0x29, 0x11]);                            // 41 CharStrings

        var cff = new List<byte> { 0x01, 0x00, 0x04, 0x04 };               // header
        cff.AddRange([0x00, 0x01, 0x01, 0x01, 0x02, (byte)'T']);           // Name INDEX (1 name)
        cff.AddRange([0x00, 0x01, 0x01, 0x01, (byte)(dict.Count + 1)]);    // Top INDEX header + offsets
        cff.AddRange(dict);                                               // Top DICT data
        cff.AddRange([0x00, 0x00]);                                       // String INDEX (empty)
        cff.AddRange([0x00, 0x00]);                                       // Global subr INDEX (empty)
        // charset format 0: one 2-byte CID per glyph (GID 1..n)
        cff.Add(0x00);
        foreach ((int cid, _) in cidToGid)
        {
            cff.Add((byte)(cid >> 8));
            cff.Add((byte)(cid & 0xff));
        }
        // CharStrings INDEX: 5 one-byte charstrings (bare 'endchar')
        cff.AddRange([0x00, 0x05, 0x01, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06]);
        for (int i = 0; i < 5; i++)
        {
            cff.Add(0x0e);
        }

        return cff.ToArray();
    }

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

    /// <summary>
    /// A three-line text page exercising line-positioning operators: baseline via Td, then a
    /// TD (which also sets the leading) for line 2, then T* (which must step by that leading) for
    /// line 3. Guards the TD leading-sign bug where T* moved the wrong way and stacked line 3 on line 1.
    /// </summary>
    public static byte[] CreateTextLeadingPdf()
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = "BT /F1 20 Tf 20 200 Td (L1) Tj 0 -20 TD (L2) Tj T* (L3) Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 250] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font));
    }

    /// <summary>
    /// A single line of wide glyphs drawn at a large Tf size. Guards the double-scaling bug where the
    /// glyph pen was fed the user-space advance (widthEm * fontSize) and then scaled by the text-space
    /// matrix again, so spacing blew up proportionally to fontSize squared (visible on the PBRT
    /// "This page intentionally left blank" line, which uses /F2 10 Tf).
    /// </summary>
    public static byte[] CreateScaledTextPdf()
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = "BT /F1 40 Tf 20 50 Td (MMMMM) Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font));
    }

    /// <summary>
    /// A single 'O' drawn in stroke-only text mode (Tr 1) at a large font size with a thin line width.
    /// Guards the text-stroke-width bug where the user-space line width was scaled by the em-to-user
    /// text matrix, fattening a thin outline into a blob that fills the letter's counter (visible on the
    /// PBRT "CHAPTER ONE" title, which strokes its heading at a 36x text-matrix scale).
    /// </summary>
    public static byte[] CreateStrokedTextPdf()
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = "1 0 0 1 20 20 cm BT 0.5 w 1 Tr /F1 40 Tf 0 0 Td (O) Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 120 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
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

    /// <summary>
    /// A 200x100 page drawing a single 'x' (code 120 under StandardEncoding) with a real embedded
    /// <b>native binary Type 1</b> program (PDF <c>FontFile</c>, eexec-encrypted) supplied as
    /// <paramref name="fontProgram"/>. Unlike the other text fixtures this font is neither a Standard-14
    /// name nor a CFF/OpenType container, so it exercises the Type1 -> CFF conversion path end to end.
    /// </summary>
    public static byte[] CreateNativeType1Pdf(byte[] fontProgram)
    {
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /TestLucidaX /Encoding /StandardEncoding "
            + "/FirstChar 120 /LastChar 120 /Widths [600] /FontDescriptor 6 0 R >>";
        string descriptor = "<< /Type /FontDescriptor /FontName /TestLucidaX /Flags 4 "
            + "/FontBBox [0 -211 743 940] /Ascent 772 /Descent -193 /ItalicAngle 0 /CapHeight 723 "
            + "/XHeight 530 /StemV 97 /FontFile 7 0 R >>";
        string content = "BT /F1 60 Tf 30 30 Td (x) Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font),
            new PdfObjectDefinition(6, descriptor),
            Stream(7, string.Empty, fontProgram));
    }

    /// <summary>
    /// Like <see cref="CreateNativeType1Pdf"/> but the font is <b>symbolic</b> (/Flags 4) and its
    /// /Encoding is a dictionary carrying /Differences that do NOT cover the drawn code (120). Per
    /// PDF 32000-1 Table 122 the codes outside /Differences still fall back to StandardEncoding, so
    /// code 120 must resolve to the glyph named 'x'. Guards the bug where a symbolic font with an
    /// /Encoding dictionary left those codes unmapped (blank), which dropped lone page-number digits.
    /// </summary>
    public static byte[] CreateSymbolicType1EncodingFallbackPdf(byte[] fontProgram)
    {
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /TestLucidaX /FirstChar 120 /LastChar 120 "
            + "/Widths [600] /FontDescriptor 6 0 R /Encoding 8 0 R >>";
        string descriptor = "<< /Type /FontDescriptor /FontName /TestLucidaX /Flags 4 "
            + "/FontBBox [0 -211 743 940] /Ascent 772 /Descent -193 /ItalicAngle 0 /CapHeight 723 "
            + "/XHeight 530 /StemV 97 /FontFile 7 0 R >>";
        string encoding = "<< /Type /Encoding /Differences [ 20 /fi 21 /fl ] >>"; // code 120 untouched
        string content = "BT /F1 60 Tf 30 30 Td (x) Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font),
            new PdfObjectDefinition(6, descriptor),
            Stream(7, string.Empty, fontProgram),
            new PdfObjectDefinition(8, encoding));
    }

    /// <summary>
    /// A 200x120 page drawing two CID glyphs (codes 1 and 2 under /Identity-H) using a handcrafted
    /// <b>CID-keyed binary Type 1</b> program (Adobe TN 5014) embedded as <c>FontFile</c> behind a
    /// Type0 / CIDFontType0 descendant. Each CID glyph is a filled rectangle, so the whole Type 0 decode
    /// -> CID -> GID -> Type1->CFF -> Skia rasterize pipeline is exercised end to end.
    /// </summary>
    public static byte[] CreateCidKeyedType1Pdf()
    {
        byte[] program = BuildCidType1Program();
        string type0 = "<< /Type /Font /Subtype /Type0 /BaseFont /CidTest /Encoding /Identity-H /DescendantFonts [6 0 R] >>";
        string cid = "<< /Type /Font /Subtype /CIDFontType0 /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + "/FontDescriptor 7 0 R /DW 1000 /W [ 1 [600] 2 [600] ] >>";
        string descriptor = "<< /Type /FontDescriptor /FontName /CidTest /Flags 4 /FontBBox [0 0 1000 1000] "
            + "/Ascent 800 /Descent -200 /ItalicAngle 0 /StemV 80 /FontFile 8 0 R >>";
        string content = "BT /F1 100 Tf 10 60 Td <00010002> Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 120] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, type0),
            new PdfObjectDefinition(6, cid),
            new PdfObjectDefinition(7, descriptor),
            Stream(8, string.Empty, program));
    }

    /// <summary>
    /// The CID-keyed Type 1 page again, but with every font attribute that is legal as an indirect
    /// reference written as one: <c>/DescendantFonts</c> refers to the array, and the array's CIDFont
    /// refers to its <c>/W</c>. Repaired and subsetted producers emit exactly this shape. A reader that
    /// only accepts an inline array loses the descendant font, and with it the embedded program, the
    /// widths and the ToUnicode mapping - the page then renders as random glyphs of a system substitute.
    /// The ToUnicode CMap uses hex destinations, which must be hex-decoded before being read as UTF-16.
    /// </summary>
    public static byte[] CreateIndirectReferenceType0Pdf()
    {
        byte[] program = BuildCidType1Program();
        string toUnicode =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
            + "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"
            + "2 beginbfchar\n<0001> <0041>\n<0002> <4E00>\nendbfchar\n"
            + "1 beginbfrange\n<0010> <0011> <0061>\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        string type0 = "<< /Type /Font /Subtype /Type0 /BaseFont /CidTest /Encoding /Identity-H "
            + "/DescendantFonts 6 0 R /ToUnicode 10 0 R >>";
        string descendantArray = "[ << /Type /Font /Subtype /CIDFontType0 /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + "/FontDescriptor 7 0 R /DW 1000 /W 9 0 R >> ]";
        string descriptor = "<< /Type /FontDescriptor /FontName /CidTest /Flags 4 /FontBBox [0 0 1000 1000] "
            + "/Ascent 800 /Descent -200 /ItalicAngle 0 /StemV 80 /FontFile 8 0 R >>";
        string content = "BT /F1 100 Tf 10 60 Td <00010002> Tj ET";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 120] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, type0),
            new PdfObjectDefinition(6, descendantArray),
            new PdfObjectDefinition(7, descriptor),
            Stream(8, string.Empty, program),
            new PdfObjectDefinition(9, "[ 1 [600] 2 [600] ]"),
            Stream(10, string.Empty, Encoding.ASCII.GetBytes(toUnicode)));
    }

    // Assembles a minimal CID-keyed Type 1 program: cleartext header + CIDMap + plaintext (lenIV -1)
    // charstrings. CID 0 is an empty .notdef; CID 1 and 2 are the same filled rectangle path.
    private static byte[] BuildCidType1Program()
    {
        byte[] notdef = [0x8b, 0x0e]; // "0 endchar"
        byte[] rect = RectCharstring(600, 100, 0, 400, 500);

        int cidMapLen = (3 + 1) * 1; // CIDCount + 1 entries, GDBytes 1, FDBytes 0
        int dataStart = cidMapLen;
        int e0 = dataStart;
        int e1 = e0 + notdef.Length;
        int e2 = e1 + rect.Length;
        int e3 = e2 + rect.Length;

        using var bin = new MemoryStream();
        bin.WriteByte((byte)e0);
        bin.WriteByte((byte)e1);
        bin.WriteByte((byte)e2);
        bin.WriteByte((byte)e3);
        bin.Write(notdef);
        bin.Write(rect);
        bin.Write(rect);
        byte[] binary = bin.ToArray();

        string header =
            "%!PS-Adobe-3.0 Resource-CIDFont\n" +
            "/CIDMapOffset 0 def\n/FDBytes 0 def\n/GDBytes 1 def\n/CIDCount 3 def\n" +
            "/FontMatrix [0.001 0 0 0.001 0 0] def\n/FontBBox [0 0 1000 1000] def\n" +
            "/Private 5 dict dup begin /lenIV -1 def end def\n" +
            $"(Binary) {binary.Length} StartData ";

        byte[] head = Encoding.ASCII.GetBytes(header);
        var result = new byte[head.Length + binary.Length];
        head.CopyTo(result, 0);
        binary.CopyTo(result, head.Length);
        return result;
    }

    // A Type 1 charstring for an axis-aligned filled rectangle: sbx wx hsbw, x y rmoveto, four rlineto, endchar.
    private static byte[] RectCharstring(int width, int x, int y, int w, int h)
    {
        var cs = new List<byte>();
        void Num(int v)
        {
            // pdf.js Type 1 / Type 2 charstring operand encoding (the form the converter decodes).
            if (v is >= -107 and <= 107)
            {
                cs.Add((byte)(v + 139));
            }
            else if (v is >= 108 and <= 779)
            {
                int t = v - 108;
                cs.Add((byte)((t >> 8) + 247));
                cs.Add((byte)(t & 0xff));
            }
            else if (v is >= -779 and <= -108)
            {
                int t = -v - 108;
                cs.Add((byte)((t >> 8) + 251));
                cs.Add((byte)(t & 0xff));
            }
            else
            {
                cs.Add(29);
                cs.Add((byte)((v >> 24) & 0xff));
                cs.Add((byte)((v >> 16) & 0xff));
                cs.Add((byte)((v >> 8) & 0xff));
                cs.Add((byte)(v & 0xff));
            }
        }

        Num(0); Num(width); cs.Add(13); // hsbw (sbx=0, wx=width)
        Num(x); Num(y); cs.Add(21); // rmoveto to the box corner
        Num(w); Num(0); cs.Add(5); // rlineto right
        Num(0); Num(h); cs.Add(5); // rlineto up
        Num(-w); Num(0); cs.Add(5); // rlineto left
        Num(0); Num(-h); cs.Add(5); // rlineto down (closes)
        cs.Add(14); // endchar
        return cs.ToArray();
    }

    /// <summary>
    /// A minimal PDF whose cross-reference is an XREF <b>STREAM</b> object (<c>/Type/XRef</c>, the PDF-1.5
    /// spelling with a capital R) and has no classic <c>trailer</c> keyword. Guards the bug where
    /// PdfXref.ParseXrefStream compared the type against "Xref" and rejected valid streams, forcing the
    /// scan-recovery which (having no trailer) failed to locate the catalog -> the file could not open.
    /// </summary>
    public static byte[] CreateXrefStreamPdf()
    {
        using var ms = new MemoryStream();
        void W(string s)
        {
            byte[] b = Encoding.GetEncoding("ISO-8859-1").GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        void WB(byte[] b) => ms.Write(b, 0, b.Length);

        W("%PDF-1.5\n%\u00E2\u00E3\u00CF\u00D3\n");

        long[] off = new long[6];
        off[1] = ms.Length; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        off[2] = ms.Length; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        off[3] = ms.Length; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>\nendobj\n");
        const string content = "0.5 w 10 10 m 100 50 l 150 20 l S\n";
        off[4] = ms.Length;
        W($"4 0 obj\n<< /Length {Encoding.GetEncoding("ISO-8859-1").GetByteCount(content)} >>\nstream\n");
        W(content);
        W("endstream\nendobj\n");

        // Cross-reference stream body: /W [1 2 1] -> 1-byte type, 2-byte offset, 1-byte generation, 6 rows.
        off[5] = ms.Length;
        using (var xref = new MemoryStream())
        {
            void Row(int type, long position, int gen)
            {
                xref.WriteByte((byte)type);
                xref.WriteByte((byte)((position >> 8) & 0xff));
                xref.WriteByte((byte)(position & 0xff));
                xref.WriteByte((byte)gen);
            }

            Row(0, 0, 0); // object 0: free
            Row(1, off[1], 0);
            Row(1, off[2], 0);
            Row(1, off[3], 0);
            Row(1, off[4], 0);
            Row(1, off[5], 0); // the xref stream object points at itself

            byte[] body = xref.ToArray();
            W($"5 0 obj\n<< /Type /XRef /Size 6 /Root 1 0 R /W [1 2 1] /Length {body.Length} >>\nstream\n");
            WB(body);
        }
        W("\nendstream\nendobj\n");
        W($"startxref\n{off[5]}\n%%EOF\n");
        return ms.ToArray();
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

    /// <summary>A page that draws an embedded DCTDecode (JPEG) image 1:1, on a page matching its pixel size.</summary>
    public static byte[] CreateDctDecodeImagePdf(byte[] jpeg, int width, int height)
    {
        string content = $"q {width} 0 0 {height} 0 0 cm /Im1 Do Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg));
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
    /// An outline whose bookmark resolves its target through a <c>/Names /Dests</c> name tree reached via
    /// <b>indirect references</b> (catalog -&gt; /Names -&gt; /Dests -&gt; tree node -&gt; destination array), with the
    /// action form <c>/A &lt;&lt; /S /GoTo /D (name) &gt;&gt;</c>. Guards the bug where PdfCatalog looked up the
    /// name tree with a non-resolving <c>GetDictionary("Dests")</c>, so an indirect /Dests value was skipped
    /// and every named destination came back null (bookmarks did nothing). Real generators (Word, scanned
    /// books) emit exactly this indirect layout.
    /// </summary>
    public static byte[] CreateOutlineNamedTreePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R /Names 20 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << >> >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 10 10 50 20 re f")),
        new PdfObjectDefinition(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 6 0 R /Resources << >> >>"),
        Stream(6, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 10 10 50 20 re f")),
        new PdfObjectDefinition(7, "<< /Type /Outlines /First 8 0 R /Last 8 0 R /Count 1 >>"),
        new PdfObjectDefinition(8, "<< /Title (Chapter 2) /Parent 7 0 R /A << /S /GoTo /D (chap2) >> >>"),
        new PdfObjectDefinition(20, "<< /Dests 21 0 R >>"),
        new PdfObjectDefinition(21, "<< /Names [(chap2) 22 0 R] >>"),
        new PdfObjectDefinition(22, "[5 0 R /XYZ 0 792 0]"));

    /// <summary>
    /// A page whose content stream uses a shading operator (sh). Lenient rendering skips the
    /// operator and still paints the following rectangle; strict rendering throws.
    /// </summary>
    public static byte[] CreateShadingPdf()
    {
        string content = "sh\n0 0 1 rg 5 5 30 30 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)));
    }

    /// <summary>
    /// A page that selects a Pattern color space and colors with scn. Pattern is skipped (painted
    /// black) in lenient mode; strict mode throws.
    /// </summary>
    public static byte[] CreatePatternColorPdf()
    {
        string content = "/Pattern cs 0 scn\n0 0 1 rg 5 5 30 30 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)));
    }

    /// <summary>
    /// A page drawing a JPXDecode image XObject plus a solid rectangle. The unsupported codec is
    /// skipped (image omitted) in lenient mode; strict mode throws.
    /// </summary>
    public static byte[] CreateJpxImagePdf()
    {
        byte[] junk = { 0x00, 0x01, 0x02, 0x03 };
        string content = "q 20 20 60 40 cm /ImJ Do Q\n1 0 0 rg 5 5 20 20 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /ImJ 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode /Length {junk.Length} >>", junk));
    }

    /// <summary>
    /// A page whose content stream carries an unknown filter. Lenient mode skips the filter and
    /// returns the (plain ASCII) bytes unchanged so the page still renders; strict mode throws.
    /// </summary>
    public static byte[] CreateUnknownFilterPdf()
    {
        byte[] content = Encoding.ASCII.GetBytes("0 0 1 rg 5 5 30 30 re f");
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>"),
            Stream(4, "/Filter /WeirdNotARealFilter", content));
    }

    /// <summary>
    /// A page that clips to a glyph outline via text rendering mode 7 (Tr 7), then fills the whole
    /// page red. The clip must persist, so red appears only inside the 'b' outline and the far
    /// top-right corner stays white.
    /// </summary>
    public static byte[] CreateTextClipPdf()
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = "BT /F1 60 Tf 60 20 Td 7 Tr (b) Tj ET\n1 0 0 rg 0 0 200 100 re f";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font));
    }

    /// <summary>
    /// Two overlapping rectangles on a 200x100 page. When <paramref name="multiply"/> is set the
    /// second (blue) rectangle is painted with an ExtGState blend mode of /Multiply over the first
    /// (green) one; the overlap is then near-black. Without it the blue simply covers the green.
    /// </summary>
    public static byte[] CreateBlendModePdf(bool multiply)
    {
        string content = multiply
            ? "0 1 0 rg 20 20 60 60 re f\n/GS1 gs 0 0 1 rg 40 40 60 60 re f"
            : "0 1 0 rg 20 20 60 60 re f\n0 0 1 rg 40 40 60 60 re f";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /ExtGState << /GS1 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /BM /Multiply >>"));
    }

    /// <summary>
    /// A page (200x100) over a light-gray background with two annotations: a /Square whose /AP
    /// appearance paints a red rectangle into its Rect, and a /Highlight markup (yellow /C) over
    /// the upper-right area via /QuadPoints.
    /// </summary>
    public static byte[] CreateAnnotationPdf()
    {
        byte[] appearance = Encoding.ASCII.GetBytes("1 0 0 rg 0 0 10 10 re f");
        byte[] content = Encoding.ASCII.GetBytes("0.9 0.9 0.9 rg 0 0 200 100 re f");

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [6 0 R 8 0 R] >>"),
            Stream(4, string.Empty, content),
            new PdfObjectDefinition(6, "<< /Type /Annot /Subtype /Square /Rect [20 20 60 60] /AP << /N 7 0 R >> >>"),
            new PdfObjectDefinition(7, $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> /Length {appearance.Length} >>", appearance),
            new PdfObjectDefinition(8, "<< /Type /Annot /Subtype /Highlight /Rect [80 40 150 60] /C [1 1 0] /QuadPoints [80 40 150 40 150 60 80 60] >>"));
    }

    /// <summary>
    /// A page using a /Separation color space whose tint transform (a Type 2 exponential, out = t^2)
    /// maps onto /DeviceGray. Painting at tint 0.5 yields gray ~0.25 (not the tint 0.5 an identity
    /// approximation would give), proving the tint function is evaluated. A red control rectangle
    /// follows in DeviceRGB.
    /// </summary>
    public static byte[] CreateSeparationPdf()
    {
        string content = "/Sep cs 0.5 scn 10 10 50 50 re f\n1 0 0 rg 120 10 50 50 re f";
        string colorSpace = "[/Separation /Pantone /DeviceGray << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /E 2 >>]";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /ColorSpace << /Sep {colorSpace} >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)));
    }

    /// <summary>
    /// A page filled with an axial (Type 2) shading pattern: a DeviceGray gradient from black at
    /// user x=0 to white at user x=200 (a Type 2 linear function ramp).
    /// </summary>
    public static byte[] CreateAxialShadingPdf()
    {
        string content = "[/Pattern /DeviceRGB] cs /ShP scn 0 0 200 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Pattern << /ShP 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Domain [0 1] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /E 1 >>"));
    }

    /// <summary>
    /// A page filled with a radial (Type 3) shading pattern: a concentric DeviceGray gradient from
    /// black at the centre (user 100,50) fading outward (radius 200).
    /// </summary>
    public static byte[] CreateRadialShadingPdf()
    {
        string content = "[/Pattern /DeviceRGB] cs /ShR scn 0 0 200 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Pattern << /ShR 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 3 /ColorSpace /DeviceGray /Coords [100 50 0 100 50 200] /Domain [0 1] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /E 1 >>"));
    }

    /// <summary>
    /// A page drawing a 24x12 CCITT Group 4 (K=-1) bilevel image scaled to fill a 200x100 page.
    /// <paramref name="codestream"/> is the raw /CCITTFaxDecode data.
    /// </summary>
    public static byte[] CreateCcittImagePdf(byte[] codestream)
    {
        string parms = "<</K -1/Columns 24/Rows 12/BlackIs1 true>>";
        string content = "q 200 0 0 100 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 24 /Height 12 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms {parms} /Length {codestream.Length} >>", codestream));
    }

    /// <summary>
    /// A 200x100 page drawing a 40x20 JBIG2 bilevel image (page-information + a single MMR generic
    /// region) scaled to fill the page. <paramref name="jbig2"/> is the complete JBIG2 page stream.
    /// </summary>
    public static byte[] CreateJbig2ImagePdf(byte[] jbig2)
    {
        string content = "q 200 0 0 100 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 40 /Height 20 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode /Length {jbig2.Length} >>", jbig2));
    }

    /// <summary>
    /// Like <see cref="CreateJbig2ImagePdf"/> but with the JBIG2 page-information segment moved into a
    /// separate shared-data stream referenced through /DecodeParms &lt;&lt; /JBIG2Globals N 0 R &gt;&gt;, exercising
    /// the globals concatenation in the image decode path.
    /// </summary>
    public static byte[] CreateJbig2GlobalsPdf(byte[] regionSegment, byte[] globalsSegment)
    {
        string content = "q 200 0 0 100 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 40 /Height 20 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode /DecodeParms << /JBIG2Globals 6 0 R >> /Length {regionSegment.Length} >>", regionSegment),
            Stream(6, string.Empty, globalsSegment));
    }

    /// <summary>
    /// A 200x100 page with two optional-content layers drawn as marked content: "Red" (OCG on,
    /// a red rectangle at user 10..70) and "Blue" (OCG off, a blue rectangle at user 120..180).
    /// /OCProperties /D has ON [Red] OFF [Blue].
    /// </summary>
    public static byte[] CreateOcgPdf()
    {
        string content = "/OC /P0 BDC\n1 0 0 rg 10 10 60 60 re f\nEMC\n/OC /P1 BDC\n0 0 1 rg 120 10 60 60 re f\nEMC";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R 7 0 R] /D << /ON [6 0 R] /OFF [7 0 R] >> >> >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Properties << /P0 6 0 R /P1 7 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /Type /OCG /Name (Red) >>"),
            new PdfObjectDefinition(7, "<< /Type /OCG /Name (Blue) >>"));
    }

    /// <summary>
    /// A page filling a rectangle with a /Lab color space color (L=60, a=60, b=-60; D65 white point),
    /// exercising the CIELAB -> sRGB conversion.
    /// </summary>
    public static byte[] CreateLabPdf()
    {
        string content = "/LabCs cs 60 60 -60 scn 10 10 60 60 re f";
        string lab = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>]";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /ColorSpace << /LabCs {lab} >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)));
    }

    /// <summary>
    /// A 100x100 page: white background, then a Form XObject painted under ExtGState /ca 0.5. The form
    /// declares a transparency group (/Group /S /Transparency) and fills two OPAQUE overlapping rectangles
    /// (red x[0,80], then blue x[40,100], both y[0,60]). Because the group is composited as a single unit
    /// at the boundary opacity, the overlap renders as pure blue @50% (128,128,255) rather than a layered
    /// red-then-blue blend (which would be purple ~ (128,64,191)). Setting <paramref name="withGroup"/> to
    /// false drops the /Group entry for the contrast case.
    /// </summary>
    public static byte[] CreateTransparencyGroupPdf(bool withGroup = true)
    {
        byte[] formContent = Encoding.ASCII.GetBytes("1 0 0 rg 0 0 80 60 re f 0 0 1 rg 40 0 60 60 re f");
        byte[] pageContent = Encoding.ASCII.GetBytes("1 1 1 rg 0 0 100 100 re f /GS0 gs /F0 Do");
        string group = withGroup ? " /Group << /S /Transparency /CS /DeviceRGB >>" : string.Empty;
        string formDict = $"/Type /XObject /Subtype /Form /BBox [0 0 100 100]{group}";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /GS0 5 0 R >> /XObject << /F0 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, pageContent),
            new PdfObjectDefinition(5, "<< /ca 0.5 /CA 0.5 >>"),
            Stream(6, formDict, formContent));
    }

    /// <summary>
    /// A document with an AcroForm exercising field typing, attribute inheritance, dotted names, widget
    /// rectangles and choice options:
    ///   - "name"      text field (value "John Doe") with a child widget supplying /Rect
    ///   - "person"    text group (/FT /Tx, /V "DefaultV") with children "first" (/V "Ada") and "last"
    ///                 (no own /V -> inherits "DefaultV")
    ///   - "color"     dropdown (/Ch + combo flag) with mixed /Opt entries, value "g"
    ///   - "agree"     check box that is itself the widget, value /On
    ///   - "gender"    radio group (/Btn + radio flag), value "male"
    ///   - "langs"     list box (/Ch, no combo flag), value "b"
    /// </summary>
    public static byte[] CreateAcroFormPdf()
    {
        byte[] content = Encoding.ASCII.GetBytes("q 1 0 0 rg 5 5 10 10 re f Q");
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 750] /Annots [11 0 R 16 0 R 17 0 R 19 0 R] /Contents 4 0 R >>"),
            Stream(4, string.Empty, content),
            new PdfObjectDefinition(5, "<< /Fields [10 0 R 12 0 R 15 0 R 17 0 R 18 0 R 20 0 R] /DA (/Helv 0 Tf 0 g) >>"),

            // name (text) + widget
            new PdfObjectDefinition(10, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (John Doe) /Kids [11 0 R] >>"),
            new PdfObjectDefinition(11, "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [50 700 250 720] >>"),

            // person group (inherits /FT and /V down) + two text children
            new PdfObjectDefinition(12, "<< /FT /Tx /T (person) /V (DefaultV) /Kids [13 0 R 14 0 R] >>"),
            new PdfObjectDefinition(13, "<< /Parent 12 0 R /T (first) /V (Ada) >>"),
            new PdfObjectDefinition(14, "<< /Parent 12 0 R /T (last) >>"),

            // color dropdown with mixed options + widget
            new PdfObjectDefinition(15, "<< /FT /Ch /Ff 131072 /T (color) /V (g) /Opt [(Plain)[(r)(Red)][(g)(Green)]] /Kids [16 0 R] >>"),
            new PdfObjectDefinition(16, "<< /Type /Annot /Subtype /Widget /Parent 15 0 R /Rect [50 650 200 700] >>"),

            // agree check box (field is also the widget)
            new PdfObjectDefinition(17, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /On /Rect [50 600 62 612] >>"),

            // gender radio group + one widget child
            new PdfObjectDefinition(18, "<< /FT /Btn /Ff 32768 /T (gender) /V (male) /Kids [19 0 R] >>"),
            new PdfObjectDefinition(19, "<< /Type /Annot /Subtype /Widget /Parent 18 0 R /Rect [50 550 70 570] >>"),

            // langs list box
            new PdfObjectDefinition(20, "<< /FT /Ch /T (langs) /V (b) /Opt [(a)(b)(c)] >>"));
    }

    /// <summary>
    /// A 100x100 page drawing a 2x2 RGB image scaled to fill the page, with a 2x2 DeviceGray /SMask:
    /// (0,0)=red/alpha0, (1,0)=green/alpha255, (0,1)=blue/alpha128, (1,1)=white/alpha0. Device top-left
    /// maps to image (0,0). Background is expected to show through the transparent corners.
    /// </summary>
    public static byte[] CreateImageSoftMaskPdf()
    {
        byte[] rgb =
        {
            255, 0, 0, 0, 255, 0,       // row 0: red, green
            0, 0, 255, 255, 255, 255,   // row 1: blue, white
        };
        byte[] mask = { 0, 255, 128, 0 }; // alpha: 0, 255, 128, 0
        string imageDict = "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R";
        string maskDict = "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8";
        string content = "q 100 0 0 100 0 0 cm /Im Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, imageDict, rgb),
            Stream(6, maskDict, mask));
    }

    /// <summary>
    /// A 100x100 page painted with a Type 7 mesh triangle (lattice) using a single-argument grey
    /// function. Vertices: (0,0)=black, (100,0)=white, (0,100)=mid-grey, filled with Gouraud shading.
    /// </summary>
    public static byte[] CreateMeshTrianglePdf()
    {
        var bits = new List<int>();
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }

        Put(1, 2); // flag = new triangle
        Put(0, 8); Put(0, 8); Put(0, 8);       // vertex 0: coord (0,0), colour t=0 (raw 0 -> black)
        Put(255, 8); Put(0, 8); Put(255, 8);   // vertex 1: coord (100,0) [raw 255], colour t=1 (raw 255 -> white)
        Put(0, 8); Put(255, 8); Put(128, 8);   // vertex 2: coord (0,100) [raw 255], colour t~0.5 (raw 128 -> grey)

        var data = new byte[((bits.Count + 7) / 8)];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i] != 0)
            {
                data[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }

        string shading = "/ShadingType 7 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 2 /Decode [0 100 0 100 0 1] /Function 6 0 R";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
            Stream(5, shading, data),
            new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0] /C1 [1] /Domain [0 1] /N 1 >>"));
    }

    /// <summary>
    /// A 100x100 page filled with a Type 1 tiling pattern. Each 20x20 cell is white with its left
    /// half (pattern x 0..10) painted red, tiled across the whole page.
    /// </summary>
    public static byte[] CreateTilingPdf()
    {
        byte[] cellContent = Encoding.ASCII.GetBytes("1 1 1 rg 0 0 20 20 re f\n1 0 0 rg 0 0 10 20 re f\n");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20";
        string content = "[/Pattern /DeviceRGB] cs /P0 scn 0 0 100 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, patternDict, cellContent));
    }

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
