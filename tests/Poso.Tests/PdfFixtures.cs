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

    private static byte[] BrotliCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A 100x100 page whose content stream takes the non-standard /BrotliDecode filter - a prototype
    /// extension pdf.js honours and some generators use for every stream (object and xref streams too, as
    /// in pdf.js Brotli-Prototype-FileA.pdf). Without the filter the page has no content at all.
    /// </summary>
    public static byte[] CreateBrotliContentPdf()
    {
        byte[] content = BrotliCompress(Encoding.ASCII.GetBytes("1 0 0 rg 0 0 100 100 re f"));
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>"),
            Stream(4, "/Filter /BrotliDecode", content));
    }

    /// <summary>
    /// A 100x100 page stroking one horizontal line with an odd-length dash array. The spec says a dash
    /// pattern comes in pairs; this one carries three entries, which a back end that asserts the pairing
    /// (Skia) refuses with an exception that would otherwise lose the whole page.
    /// </summary>
    public static byte[] CreateOddDashPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("0 0 0 RG 1 w [7 3 7] 0 d 10 50 m 90 50 l S")));

    /// <summary>
    /// The same document as <see cref="CreateSimplePdf"/> but with the page dictionary closed by a single
    /// <c>&gt;</c> instead of <c>&gt;&gt;</c>. A lone <c>&gt;</c> does end a dictionary, and producers emit
    /// it; requiring the doubled form loses the page.
    /// </summary>
    public static byte[] CreateLoneAngleBracketDictionaryPdf()
    {
        byte[] wellFormed = CreateSimplePdf();
        string text = Encoding.ASCII.GetString(wellFormed);
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << >> >>";
        if (!text.Contains(page, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the fixture drifted");
        }

        return Encoding.ASCII.GetBytes(text.Replace(page, page[..^1], StringComparison.Ordinal));
    }

    /// <summary>
    /// A page that clips to the outlines of <c>Tr 5</c> text: two show-text runs inside one
    /// <c>BT</c>/<c>ET</c> pair, stroked red, then a blue rectangle over the whole page. The two runs
    /// accumulate into a single clip that is applied at <c>ET</c>, so the blue shows inside either
    /// outline; clipping per run would intersect them and leave the rectangle unpainted.
    /// </summary>
    public static byte[] CreateAccumulatedTextClipPdf()
    {
        string widths = string.Join(" ", Enumerable.Repeat("20", 95));
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        string content = "15 w 1 0 0 RG BT /F1 60 Tf 5 Tr 5 20 Td (b) Tj 105 0 Td (b) Tj ET\n"
            + "0 0 1 rg 0 0 200 100 re f";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, font));
    }

    /// <summary>
    /// A 20x20 page whose MediaBox is 2x2 with <c>/UserUnit 10</c>: the box says one thing, the page is
    /// really twenty across, and content is still written in the box's coordinates.
    /// </summary>
    public static byte[] CreateUserUnitPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2 2] /UserUnit 10 "
            + "/Contents 4 0 R /Resources << >> >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 0 0 2 2 re f")));

    /// <summary>
    /// A 250x50 page whose /MediaBox elements are indirect objects (<c>[4 0 R 5 0 R 6 0 R 7 0 R]</c>, the
    /// shape pdf.js issue7872.pdf has). The box is legal, but reading an element as a number without
    /// resolving it leaves the page with no usable box at all and it falls back to Letter.
    /// </summary>
    public static byte[] CreateIndirectMediaBoxPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [4 0 R 5 0 R 6 0 R 7 0 R] "
            + "/Contents 8 0 R /Resources << >> >>"),
        new PdfObjectDefinition(4, "0"),
        new PdfObjectDefinition(5, "0"),
        new PdfObjectDefinition(6, "250"),
        new PdfObjectDefinition(7, "50"),
        Stream(8, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 0 0 250 50 re f")));

    /// <summary>
    /// The same defect one level down, on a form XObject (pdf.js franz_2.pdf): the outer form's
    /// <c>/BBox</c> is <c>[0 0 200 7 0 R]</c>, and the nested form it draws fills the page with gray.
    /// Unresolved, the box collapses to zero height, and what survives then depends on what a back end
    /// does with an empty clip - Skia drops it and keeps the gray by accident, Peach keeps the zero-area
    /// clip and loses the page.
    /// </summary>
    public static byte[] CreateIndirectFormBBoxPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50] /Contents 6 0 R "
            + "/Resources << /XObject << /Im17 4 0 R >> >> >>"),
        Stream(4, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 200 7 0 R] "
            + "/Resources << /XObject << /Fm1 5 0 R >> >>", Encoding.ASCII.GetBytes("q /Fm1 Do Q")),
        Stream(5, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 521 7 0 R]",
            Encoding.ASCII.GetBytes("0.5 0.5 0.5 rg 0 87.96 521 -87.96 re f")),
        Stream(6, string.Empty, Encoding.ASCII.GetBytes("1 0 0 1 0 0 cm /Im17 Do")),
        new PdfObjectDefinition(7, "50"));

    /// <summary>A minimal CID-keyed CFF program (Top DICT has ROS, no FDArray - the reader only needs the ROS,
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

    /// <summary>
    /// A page tree with one real page and one intermediate node that lists itself in its own
    /// <c>/Kids</c>, the shape <c>Pages-tree-refs.pdf</c> in the pdf.js suite has. Walking that cycle
    /// without a path guard runs until the stack overflows and the process dies, so the whole render run
    /// goes with it rather than the one branch that closes the loop.
    /// </summary>
    public static byte[] CreateCyclicPageTreePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [4 0 R 3 0 R] /Count 2 >>"),
        new PdfObjectDefinition(3, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 5 0 R /Resources << >> >>"),
        Stream(5, string.Empty, Encoding.ASCII.GetBytes("1 0 0 RG 1 w 10 50 m 190 50 l S")));

    /// <summary>
    /// A 100x50 page that sets clipping-only text render mode (<c>7 Tr</c>) with a non-embedded base-14
    /// font, shows a word, then fills the whole page blue. The clip must survive <c>ET</c>, so blue
    /// appears only inside the glyph outlines and the far corner stays white.
    /// </summary>
    public static byte[] CreateClippingOnlyTextPdf()
    {
        string content = "BT 7 Tr 10 20 TD /F1 20 Tf (waves) Tj ET\n0 0 1 rg 0 0 100 50 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 50] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold >>"));
    }

    /// <summary>
    /// A 2x2 image in a two-colorant <c>DeviceN</c> space whose tint transform maps the colorants onto the
    /// red and green channels of RGB. Reading the samples as base components - what the old approximation
    /// did - walks the raster half a pixel at a time and yields greys instead.
    /// </summary>
    public static byte[] CreateDeviceNImagePdf()
    {
        byte[] samples = [255, 0, 0, 255, 128, 128, 0, 0];
        string content = "q 100 0 0 100 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace 6 0 R /BitsPerComponent 8 /Length {samples.Length} >>", samples),
            new PdfObjectDefinition(6, "[ /DeviceN [ /C /M ] /DeviceRGB 7 0 R ]"),
            Stream(7, "/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ 0 }")));
    }

    /// <summary>
    /// A 100x100 page that fills through an ICCBased space whose stream declares <c>/N 1000000</c>.
    /// The component count sizes the buffer a colour operator collects its operands in, so a file that
    /// asks for a million of them gets a million floats of stack and overflows it before anything is
    /// drawn. The blue rectangle after the fill is what proves the page survived.
    /// </summary>
    public static byte[] CreateBogusComponentCountPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 5 0 R /Resources << /ColorSpace << /CS0 [ /ICCBased 4 0 R ] >> >> >>"),
        Stream(4, "/N 1000000", "not an icc profile"u8.ToArray()),
        Stream(5, string.Empty, "/CS0 cs 0.5 sc 0 0 100 100 re f\n0 0 1 rg 10 10 20 20 re f"u8.ToArray()));

    /// <summary>A simple two-page document; page 2 is rotated 90 degrees.</summary>
    public static byte[] CreateSimplePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << >> >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 50 Td (Hello Poso) Tj ET")),
        new PdfObjectDefinition(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 150] /Rotate 90 /Contents 6 0 R /Resources << >> >>"),
        Stream(6, string.Empty, Encoding.ASCII.GetBytes("0 0 1 rg 10 10 50 50 re f")));

    /// <summary>
    /// The table names the wrong object for a page: object 3's entry carries object 2's offset, so reading
    /// <c>3 0 R</c> parses the node stored as <c>2 0 obj</c>. pdf.js issue7229.pdf has exactly this shape -
    /// its first subsection declares that it starts at object 1 where the file's objects start at 0, which
    /// shifts every entry one object - and the page it loses is gone from the document entirely. An entry
    /// can only be caught by the object naming itself, and the repair is the scan.
    /// </summary>
    public static byte[] CreateShiftedXrefPdf()
    {
        byte[] wellFormed = CreateSimplePdf();
        string text = Encoding.ASCII.GetString(wellFormed);
        // "startxref\n" also contains "xref\n", so anchor on the line break in front of it.
        int table = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        if (table < 0)
        {
            throw new InvalidOperationException("the fixture drifted");
        }

        string[] lines = text[(table + 1)..].Split('\n');
        if (!lines[5].EndsWith(" n ", StringComparison.Ordinal) || !lines[4].EndsWith(" n ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the fixture drifted");
        }

        lines[5] = lines[4]; // object 3 borrows object 2's offset
        return Encoding.ASCII.GetBytes(text[..(table + 1)] + string.Join('\n', lines));
    }

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
    /// A 200x100 page whose symbolic /Type1 font embeds the given program as <c>FontFile3</c> with
    /// /Subtype /Type1C - the shape a producer that converts Type 1 to CFF emits. Lets a test hand the
    /// reader a program in exactly the container form it finds in the wild (bare CFF, or an 'OTTO'
    /// sfnt that carries nothing but that CFF). Code 97 is the glyph named 'a'.
    /// </summary>
    public static byte[] CreateType1CffPdf(byte[] fontProgram, string fontName = "CffOnlyTest")
    {
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /{fontName} /Flags 4 "
            + "/FirstChar 97 /LastChar 97 /Widths [600] /FontDescriptor 6 0 R >>";
        string descriptor = $"<< /Type /FontDescriptor /FontName /{fontName} /Flags 4 /FontBBox [0 -200 700 900] "
            + "/Ascent 750 /Descent -250 /ItalicAngle 0 /StemV 80 /FontFile3 7 0 R /Subtype /Type1C >>";
        string content = "BT /F1 60 Tf 30 30 Td (a) Tj ET";

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
    /// The same container, with a font that paints an ordinary letter and a <c>seac</c> composite side by
    /// side: "E" then E-acute, the second being base plus accent and carrying no outline of its own. A back
    /// end that does not expand composites paints the first and nothing for the second.
    /// </summary>
    public static byte[] CreateSeacCffPdf(byte[] fontProgram, string fontName = "SeacTest")
    {
        // FirstChar..LastChar must hold one width each: 69 ("E") and 201 (E-acute) with the rest zero.
        const int FirstChar = 69, LastChar = 201;
        var widths = new string[LastChar - FirstChar + 1];
        Array.Fill(widths, "0");
        widths[0] = "700";
        widths[128 - FirstChar] = "700";

        // A bare CFF is addressed by glyph name, so the code the content stream shows has to be tied to
        // "Eacute" through /Differences - the program's built-in encoding says nothing about C1 codes.
        string font = $"<< /Type /Font /Subtype /Type1 /BaseFont /{fontName} /Flags 4 "
            + $"/FirstChar {FirstChar} /LastChar {LastChar} /Widths [{string.Join(' ', widths)}] "
            + "/Encoding << /Type /Encoding /Differences [128 /Eacute] >> "
            + "/FontDescriptor 6 0 R >>";
        string descriptor = $"<< /Type /FontDescriptor /FontName /{fontName} /Flags 4 /FontBBox [0 -200 700 900] "
            + "/Ascent 750 /Descent -250 /ItalicAngle 0 /StemV 80 /FontFile3 7 0 R /Subtype /Type1C >>";
        // \311 is the octal escape for 0xC9, the code the font maps to E-acute.
        string content = "BT /F1 40 Tf 10 30 Td (E) Tj ET BT /F1 40 Tf 100 30 Td (\\200) Tj ET";

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
    public static byte[] CreateIndirectReferenceType0Pdf(byte[]? program = null)
    {
        program ??= BuildCidType1Program();
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

    /// <summary>
    /// Two form XObjects that each paint the other at half scale, which is the shape of
    /// <c>issue19800.pdf</c> in the pdf.js suite. Every level paints a rectangle before re-entering, so a
    /// renderer that answers the cycle by refusing the page throws away the rectangles that were already
    /// drawn - and a file like this one is drawn by producers that lost track of their XObjects.
    /// </summary>
    public static byte[] CreateMutualFormCyclePdf()
    {
        string resources = "/Resources << /XObject << /X1 6 0 R /X0 7 0 R >> >>";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {resources} /Contents 4 0 R >>"),
            Stream(4, string.Empty, "/X1 Do"u8.ToArray()),
            Stream(6, $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] {resources}",
                "0 0 1 rg 0 0 40 40 re f q 0.5 0 0 0.5 50 50 cm /X0 Do Q"u8.ToArray()),
            Stream(7, $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] {resources}",
                "1 0 0 rg 0 0 40 40 re f q 0.5 0 0 0.5 50 50 cm /X1 Do Q"u8.ToArray()));
    }

    /// <summary>
    /// A 100x100 page covered by a 2x2 raw (Flate-less) DeviceCMYK image whose every sample is
    /// (141,142,156,78) - a mid-dark photograph tone. MuPDF paints it (99,88,80); the one-minus-black
    /// shortcut paints (36,35,21), so one pixel tells the two conversions apart.
    /// </summary>
    public static byte[] CreateRawCmykImagePdf()
    {
        byte[] samples =
        {
            141, 142, 156, 78, 141, 142, 156, 78,
            141, 142, 156, 78, 141, 142, 156, 78,
        };
        string content = "q 100 0 0 100 0 0 cm /Im1 Do Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Length {samples.Length} >>", samples));
    }

    /// <summary>
    /// A 200x100 page that shows one word in a native Type 1 program whose font dict carries no
    /// /Encoding - so the codes fall back to StandardEncoding - and whose /Widths start at
    /// <c>/FirstChar 11</c>, the way an AMS Computer Modern subset arrives from Ghostscript.
    /// </summary>
    public static byte[] CreateType1WordPdf(byte[] fontProgram, string word)
    {
        string widths = string.Join(" ", Enumerable.Repeat(500, 122 - 11 + 1));
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /TestCmbx12 "
            + "/FirstChar 11 /LastChar 122 /Widths [" + widths + "] /FontDescriptor 6 0 R >>";
        string descriptor = "<< /Type /FontDescriptor /FontName /TestCmbx12 /Flags 4 "
            + "/FontBBox [-53 -251 1139 750] /Ascent 694 /Descent -194 /ItalicAngle 0 /CapHeight 686 "
            + "/StemV 884 /FontFile 7 0 R >>";
        string content = $"BT /F1 20 Tf 10 20 Td ({word}) Tj ET";

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
    /// A 200x100 page filled by a 2x2 image whose first sample row is black and second row white, placed either
    /// with the upright unit-square matrix or with a mirrored one. Pins which edge of the placement the first
    /// sample row lands on.
    /// </summary>
    public static byte[] CreateRowOrderImagePdf(bool mirrored)
    {
        byte[] pixels = [0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255];
        string content = mirrored ? "q 200 0 0 -100 0 100 cm /Im1 Do Q" : "q 200 0 0 100 0 0 cm /Im1 Do Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>", pixels));
    }

    /// <summary>
    /// A 3x4 image whose Indexed colour space carries its colour table as a compressed <b>stream</b> over a
    /// DeviceCMYK base (PDF 32000-1:2008 8.7.2 permits a string or a stream, and generators use the stream
    /// form once the table is large). The three indices are white, black and red. Reading only the string
    /// form left the table empty, which painted every index - including the white background - black.
    /// </summary>
    public static byte[] CreateIndexedStreamPalettePdf()
    {
        byte[] palette = [
            0, 0, 0, 0, // CMYK all zero: white
            0, 0, 0, 255, // full key: black
            0, 255, 255, 0, // magenta and yellow: red
        ];
        byte[] samples = [0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2];

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 3 4] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 3 0 0 4 0 0 cm /Im1 Do Q")),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 3 /Height 4 /BitsPerComponent 8 /ColorSpace 6 0 R /Filter /FlateDecode /Length {Flate(samples).Length} >>", Flate(samples)),
            new PdfObjectDefinition(6, "[ /Indexed /DeviceCMYK 2 7 0 R ]"),
            Stream(7, "/Filter /FlateDecode", Flate(palette)));
    }

    /// <summary>A page that draws an embedded DCTDecode (JPEG) image 1:1, on a page matching its pixel size.</summary>
    public static byte[] CreateDctDecodeImagePdf(byte[] jpeg, int width, int height, string colorSpace = "/DeviceCMYK", string? decode = null)
    {
        string content = $"q {width} 0 0 {height} 0 0 cm /Im1 Do Q";

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent 8{(decode is null ? string.Empty : " /Decode " + decode)} /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg));
    }

    /// <summary>
    /// A white page with a solid-colour DCTDecode image over the whole square, and an <c>/SMask</c> whose
    /// coverage is <paramref name="coverage"/>. <paramref name="maskIsJpeg"/> picks between a plain
    /// grayscale mask and one that is itself JPEG-encoded: a codec never returns samples, so the second
    /// has to go back through the image pipeline before it can mask anything.
    /// </summary>
    public static byte[] CreateSoftMaskedDctImagePdf(byte[] jpeg, int side, byte coverage, bool maskIsJpeg = false)
    {
        string content = $"1 1 1 rg 0 0 {side} {side} re f q {side} 0 0 {side} 0 0 cm /Im1 Do Q";
        byte[] mask = Flate(Enumerable.Repeat((byte)coverage, side * side).ToArray());
        string maskDictionary = $"/Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceGray "
            + $"/BitsPerComponent 8 /Filter /FlateDecode /Length {mask.Length}";
        if (maskIsJpeg)
        {
            mask = jpeg;
            maskDictionary = $"/Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceRGB "
                + $"/BitsPerComponent 8 /Filter /DCTDecode /Length {mask.Length}";
        }

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {side} {side}] "
                + "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} "
                + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /SMask 6 0 R /Length {jpeg.Length} >>", jpeg),
            Stream(6, maskDictionary, mask));
    }

    /// <summary>
    /// A green page with a DCTDecode image over its right half and a 1-bit <c>/Mask</c> at half the
    /// image's resolution over that. A codec never sees the image dictionary, so a back-end-decoded
    /// raster has to have the mask folded in afterwards; without that the image paints its whole
    /// rectangle. The mask paints its own left half and masks its right half, and the two are sampled
    /// across to the image grid, so the test also pins the resolution mismatch down.
    /// </summary>
    public static byte[] CreateMaskedDctImagePdf(byte[] jpeg, int width, int height)
    {
        string content = $"0 1 0 rg 0 0 {width * 2} {height} re f q {width} 0 0 {height} {width} 0 cm /Im1 Do Q";
        int maskRows = height / 2;
        int maskRowBytes = (width / 2 + 7) / 8;
        byte[] mask = new byte[maskRows * maskRowBytes];
        for (int y = 0; y < maskRows; y++)
        {
            for (int b = 0; b < maskRowBytes / 2; b++)
            {
                mask[(y * maskRowBytes) + b] = 0xFF;  // the mask's left half paints
            }
        }

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width * 2} {height}] "
                + "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
                + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Mask 6 0 R /Length {jpeg.Length} >>", jpeg),
            Stream(6, $"/Type /XObject /Subtype /Image /ImageMask true /Width {width / 2} "
                + $"/Height {maskRows} /BitsPerComponent 1 /Length {mask.Length}", mask));
    }

    /// <summary>
    /// A page that draws a JPEG 2000 image 1:1 on a page matching its pixel size. No /ColorSpace is
    /// written: for JPXDecode the colour space comes from the codestream itself (PDF 32000-1:2008 11.4.4),
    /// which is how the book covers that used to render blank declare their images.
    /// </summary>
    public static byte[] CreateJpxImagePdf(byte[] jpx, int width, int height, int smaskInData = 0, string? matte = null)
    {
        string content = $"q {width} 0 0 {height} 0 0 cm /Im1 Do Q";
        string alpha = smaskInData > 0 ? $" /ColorSpace /DeviceRGB /SMaskInData {smaskInData}" : string.Empty;
        string matteEntry = matte is not null ? $" /Matte [{matte}]" : string.Empty;

        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /BitsPerComponent 8 /Filter /JPXDecode{alpha}{matteEntry} /Length {jpx.Length} >>", jpx));
    }

    /// <summary>
    /// A 1200x2 page painted by one <c>sh</c> axial shading that runs red to blue across its whole width.
    /// At 7x that device surface is 8400 pixels across, past the size at which the rasterizer gives up on a
    /// single image, so the shading has to be laid down in pieces - pdf.js issue14165.pdf is the same shape
    /// and lost its gradient entirely at that zoom.
    /// </summary>
    public static byte[] CreateWideAxialShadingPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 2] "
            + "/Resources << /Shading << /S1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q /S1 sh Q")),
        new PdfObjectDefinition(5, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 1 1200 1] "
            + "/Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>"));

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
    /// A page whose <c>scn</c> names an /Indexed colour space with a lookup shorter than one entry. The
    /// index has nothing to clamp to, and pdf.js issue1985.pdf's truncated palette took that as a reason to
    /// throw out of Math.Clamp and lose the whole page.
    /// </summary>
    public static byte[] CreateShortIndexedLookupPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] "
            // 2 bytes for a 3-component space: longer than nothing, shorter than one entry, which is the
            // shape that leaves maxIndex at -1.
            + "/Resources << /ColorSpace << /Cs1 [/Indexed /DeviceRGB 255 <AABB>] >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("/Cs1 cs 1.0 scn 10 10 80 40 re f")));

    /// <summary>
    /// A dictionary with a stray keyword where a key belongs. pdf.js issue11549_reduced.pdf has one and a
    /// strict reader loses the whole document over it; skipping the token keeps every readable key, which
    /// is what the reference readers do.
    /// </summary>
    public static byte[] CreateDictionaryWithAStrayKeywordPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50] null "
            + "/Contents 4 0 R /Resources << >> >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 10 10 80 30 re f")));

    /// <summary>
    /// A document whose trailer /Root names the /Info dictionary instead of the catalog - pdf.js
    /// issue9418.pdf's incremental update does exactly that. The scan finds the real catalog, and the
    /// trailer has to prefer it over a /Root that names something else.
    /// </summary>
    public static byte[] CreateRootPointingAtInfoPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Producer (fixture) >>"),
        new PdfObjectDefinition(2, "<< /Type /Catalog /Pages 3 0 R >>"),
        new PdfObjectDefinition(3, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>"),
        new PdfObjectDefinition(4, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 50] "
            + "/Contents 5 0 R /Resources << >> >>"),
        Stream(5, string.Empty, Encoding.ASCII.GetBytes("0 0 0 rg 10 10 80 30 re f")));

    /// <summary>A document whose xref sends object 3 past the end of the file.</summary>
    public static byte[] CreateXrefOffsetPastEndPdf()
    {
        byte[] wellFormed = CreateSimplePdf();
        string text = Encoding.ASCII.GetString(wellFormed);
        int table = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        if (table < 0)
        {
            throw new InvalidOperationException("the fixture drifted");
        }

        // Object 3's entry carries an offset far beyond the last byte.
        string[] lines = text[(table + 1)..].Split('\n');
        if (!lines[5].EndsWith(" n ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the fixture drifted");
        }

        lines[5] = "9999999999 00000 n ";
        return Encoding.ASCII.GetBytes(text[..(table + 1)] + string.Join('\n', lines));
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
    /// A page that clips to a glyph outline via text rendering mode 7 (Tr 7, clip and paint nothing),
    /// then fills the whole page red. The clip must persist, so red appears only inside the 'b' outline
    /// and the far top-right corner stays white.
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
    /// A page (200x100) over a light-gray background with four annotations: a /Square whose /AP
    /// appearance paints a red rectangle into its Rect, a /Highlight markup (yellow /C) over the
    /// upper-right area via /QuadPoints, a form-field /Widget with no /AP at all, and a /Widget whose
    /// /AP /N is a state dictionary (green /Off, blue /Yes) with /AS /Off.
    /// </summary>
    public static byte[] CreateAnnotationPdf()
    {
        byte[] red = Encoding.ASCII.GetBytes("1 0 0 rg 0 0 10 10 re f");
        byte[] green = Encoding.ASCII.GetBytes("0 1 0 rg 0 0 10 10 re f");
        byte[] blue = Encoding.ASCII.GetBytes("0 0 1 rg 0 0 10 10 re f");
        byte[] content = Encoding.ASCII.GetBytes("0.9 0.9 0.9 rg 0 0 200 100 re f");

        PdfObjectDefinition Form(int number, byte[] body) =>
            new(number, $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> /Length {body.Length} >>", body);
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [6 0 R 8 0 R 9 0 R 10 0 R] >>"),
            Stream(4, string.Empty, content),
            new PdfObjectDefinition(6, "<< /Type /Annot /Subtype /Square /Rect [20 20 60 60] /AP << /N 7 0 R >> >>"),
            Form(7, red),
            new PdfObjectDefinition(8, "<< /Type /Annot /Subtype /Highlight /Rect [80 40 150 60] /C [1 1 0] /QuadPoints [80 40 150 40 150 60 80 60] >>"),
            new PdfObjectDefinition(9, "<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [20 70 60 90] >>"),
            new PdfObjectDefinition(10, "<< /Type /Annot /Subtype /Widget /FT /Btn /Rect [80 70 120 90] /AS /Off /AP << /N << /Off 11 0 R /Yes 12 0 R >> >> >>"),
            Form(11, green),
            Form(12, blue));
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
    /// A page whose <c>/MediaBox</c> is an indirect reference. The inheritable-attribute map holds the
    /// reference unresolved, so reading the box as a type test misses it and the page comes out at the
    /// default letter size.
    /// </summary>
    public static byte[] CreateIndirectPageBoxPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox 4 0 R >>"),
        new PdfObjectDefinition(4, "[0 0 300 200]"));

    /// <summary>
    /// Four pages that exercise the page boxes: one whose <c>/MediaBox</c> is empty, one whose
    /// <c>/CropBox</c> is, one whose <c>/CropBox</c> lies wholly outside its <c>/MediaBox</c>, and one
    /// with an ordinary crop inside the media box. MuPDF renders the first three at its default
    /// 612x792 and the last at the crop; a renderer that trusts the declared boxes gets three
    /// zero-sized pages, which no back end can even allocate.
    /// </summary>
    public static byte[] CreatePageBoxVariantsPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 0 0] >>"),
        new PdfObjectDefinition(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 800 600] /CropBox [0 0 0 0] >>"),
        new PdfObjectDefinition(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /CropBox [600 -200 1000 0] >>"),
        new PdfObjectDefinition(6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [36 36 576 756] >>"));

    /// <summary>
    /// A 100x100 page painted with a shading pattern through a colour space written as <c>[/Pattern]</c>
    /// with no base space - legal, and meaning the base is whatever is current. Requiring a second array
    /// entry left such a space unrecognised, so the pattern was never selected and a corpus page painted a
    /// solid colour where MuPDF paints a blue-to-green ramp.
    /// </summary>
    public static byte[] CreateBarePatternSpaceShadingPdf()
    {
        string content = "[/Pattern] cs /ShP scn 0 0 100 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /ShP 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Domain [0 1] /Extend [true true] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 1 0] /E 1 >>"));
    }

    /// <summary>
    /// A blue-to-green axial shading driven by a stitching function whose operands are every written as an
    /// indirect reference (<c>/Functions 10 0 R</c> to the array <c>[11 0 R]</c>, <c>/Encode</c> and
    /// <c>/Bounds</c> likewise) - the shape pdf.js issue7821.pdf uses. Reading <c>/Functions</c> as a plain
    /// inline array returns null for an indirect reference, so the whole stitching function - and the
    /// shading through it - silently disappears. It must resolve and paint the same ramp as the inline form.
    /// </summary>
    public static byte[] CreateStitchingIndirectShadingPdf()
    {
        string content = "[/Pattern /DeviceRGB] cs /ShP scn 0 0 100 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /ShP 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Domain [0 1] /Extend [true true] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 3 /Domain [0 1] /Bounds 8 0 R /Encode 9 0 R /Functions 10 0 R >>"),
            new PdfObjectDefinition(8, "[]"),
            new PdfObjectDefinition(9, "[0 1]"),
            new PdfObjectDefinition(10, "[11 0 R]"),
            new PdfObjectDefinition(11, "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 1 0] /E 1 >>"));
    }

    /// <summary>
    /// A 100x100 page painted with a <c>ShadingType 1</c> (function-based) shading whose function returns
    /// the shading-space x as red and nothing else, placed by <c>/Matrix [100 0 0 100 0 0]</c>. The ramp
    /// proves both the matrix mapping and the two-input function call; the corpus file that uses nine of
    /// these paints every one of them white.
    /// </summary>
    /// <summary>
    /// A 100x100 page painted with an axial shading in a five-colourant <c>DeviceN</c> space. Its PostScript
    /// function turns the single parameter into five colourant values, and the tint transform maps those to
    /// DeviceCMYK (cyan = the first colourant, rest zero). Because the tint takes five inputs - more than the
    /// four a CMYK base emits - a shading pipeline that sizes the intermediate colour to four starves the
    /// tint and paints the whole wash white (pdf.js postscript_type4_many_outputs.pdf). Must paint cyan.
    /// </summary>
    public static byte[] CreateDeviceN5ShadingPdf()
    {
        string content = "q 0 0 100 100 re W n /SH1 sh Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /SH1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, "<< /ShadingType 2 /ColorSpace 6 0 R /Coords [0 0 100 0] /Domain [0 1] /Function 7 0 R /Extend [true true] >>"),
            new PdfObjectDefinition(6, "[/DeviceN [/c0 /c1 /c2 /c3 /c4] /DeviceCMYK 8 0 R]"),
            // axial colour function: 1 input t -> five duplicated colourants.
            Stream(7, "/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ dup dup dup dup }")),
            // tint transform: drop the four trailing colourants, keep the first as cyan, zero the rest.
            Stream(8, "/FunctionType 4 /Domain [0 1 0 1 0 1 0 1 0 1] /Range [0 1 0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ pop pop pop pop 0 0 0 }")));
    }

    /// <summary>
    /// A 100x100 page whose only content is a shading painted inside an optional-content group that the
    /// catalog's /OCProperties /D /OFF disables. The shading must not paint at all - pdf.js
    /// issue11144_reduced.pdf's OFF layer painted a whole-page radial wash because the sh operator ignored
    /// the suppressed-content state (and the matching clip skip left it unclipped).
    /// </summary>
    public static byte[] CreateHiddenOcgShadingPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >> >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> /Properties << /MC0 7 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("/OC /MC0 BDC /S0 sh EMC")),
        new PdfObjectDefinition(5, "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Domain [0 1] /Extend [true true] /Function 6 0 R >>"),
        new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0] /C1 [1] /Domain [0 1] /N 1 >>"),
        new PdfObjectDefinition(7, "<< /Type /OCG /Name (Hidden) >>"));

    public static byte[] CreateFunctionShadingPdf()
    {
        string content = "q 0 0 100 100 re W n /SH1 sh Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /SH1 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [100 0 0 100 0 0] /Function 7 0 R >>"),
            Stream(7, "/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]",
                Encoding.ASCII.GetBytes("{ pop dup 0 0 }")));
    }

    /// <summary>
    /// A 200x200 page drawing a red square while an ExtGState <c>/SMask</c> is in effect. The mask group
    /// paints its left half black and its right half white under a luminosity mask, so the square appears
    /// on the right only - a luminosity mask's coverage is the luminance it reads.
    /// </summary>
    public static byte[] CreateSoftMaskedFormPdf()
    {
        byte[] square = Encoding.ASCII.GetBytes("1 0 0 rg 0 0 200 200 re f");
        byte[] maskContent = Encoding.ASCII.GetBytes("0 g 0 0 100 200 re f 1 g 100 0 100 200 re f");
        string content = "q /GS1 gs /Sq Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /XObject << /Sq 5 0 R >> /ExtGState << /GS1 7 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceRGB >>", square),
            Stream(6, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>", maskContent),
            new PdfObjectDefinition(7, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /S /Luminosity /G 6 0 R >> >>"));
    }

    /// <summary>
    /// A 200x200 page that draws a red square from a form while an ExtGState soft mask whose group is
    /// black on the left and white on the right is in effect, and whose form content clears the mask with
    /// its own <c>/SMask /None</c> before painting.
    /// <para>
    /// With the form declared as a transparency group the mask was bound when <c>Do</c> met it, so the
    /// group still comes out masked (MuPDF renders the left half white and the right half red). Without
    /// the group there is no boundary for it to bind to, the state travels into the content, and the
    /// form's own reset takes effect - so the square comes out whole.
    /// </para>
    /// </summary>
    public static byte[] CreateSoftMaskClearedInsideAFormPdf(bool transparencyGroup)
    {
        byte[] square = Encoding.ASCII.GetBytes("/GS2 gs 1 0 0 rg 0 0 200 200 re f");
        byte[] maskContent = Encoding.ASCII.GetBytes("0 g 0 0 100 200 re f 1 g 100 0 100 200 re f");
        string group = transparencyGroup ? "/Group << /Type /Group /S /Transparency /CS /DeviceRGB >>" : string.Empty;
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /XObject << /Fm0 5 0 R >> /ExtGState << /GS1 7 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q /GS1 gs /Fm0 Do Q")),
            Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] " + group
                + "/Resources << /ExtGState << /GS2 8 0 R >> >>", square),
            Stream(6, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>", maskContent),
            new PdfObjectDefinition(7, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /S /Luminosity /G 6 0 R >> >>"),
            new PdfObjectDefinition(8, "<< /Type /ExtGState /SMask /None >>"));
    }

    /// <summary>
    /// A 200x200 page whose whole visible content is a blue square drawn from a transparency-group form
    /// that covers only the top-right quadrant (its BBox is (100,100)-(200,200)), while a 50% luminosity
    /// soft mask is in effect. Half-strength blue over white is the expected result; the point is the
    /// offset.
    /// <para>
    /// A layer surface that starts at a device origin of (100, 0) has to shift the parent matrix in
    /// device space. Shifting it in user space instead moves the content by the origin scaled and flipped
    /// by the page matrix - off a surface this size, so the group vanishes and the page comes out white.
    /// </para>
    /// </summary>
    public static byte[] CreateOffsetTransparencyGroupPdf()
    {
        byte[] square = Encoding.ASCII.GetBytes("0 0 1 rg 100 100 100 100 re f");
        byte[] maskContent = Encoding.ASCII.GetBytes("0.5 g 100 100 100 100 re f");
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /XObject << /Fm0 5 0 R >> /ExtGState << /GS1 7 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q /GS1 gs /Fm0 Do Q")),
            Stream(5, "/Type /XObject /Subtype /Form /BBox [100 100 200 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceRGB >>", square),
            Stream(6, "/Type /XObject /Subtype /Form /BBox [100 100 200 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>", maskContent),
            new PdfObjectDefinition(7, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /S /Luminosity /G 6 0 R >> >>"));
    }

    /// <summary>
    /// A 200x200 page that draws an image which carries its own <c>/SMask</c> while an ExtGState soft mask
    /// that paints nothing - so whose luminosity coverage is its all-black backdrop, zero - is in effect.
    /// MuPDF lets the image's own mask stand in for the group mask instead of multiplying both, so the red
    /// square stays visible; reading the two as stacked erases it.
    /// </summary>
    public static byte[] CreateSoftMaskedImagePdf()
    {
        byte[] red = new byte[4 * 4 * 3];
        for (int i = 0; i < red.Length; i += 3)
        {
            red[i] = 255;
        }

        byte[] opaque = new byte[4 * 4];
        for (int i = 0; i < opaque.Length; i++)
        {
            opaque[i] = 255;
        }

        string content = "q /GS1 gs 100 0 0 100 50 50 cm /Im0 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /XObject << /Im0 5 0 R >> /ExtGState << /GS1 7 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB "
                + "/BitsPerComponent 8 /SMask 8 0 R", red),
            Stream(6, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 200 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>",
                Encoding.ASCII.GetBytes("q Q")),
            new PdfObjectDefinition(7, "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >>"),
            Stream(8, "/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray "
                + "/BitsPerComponent 8", opaque));
    }

    /// <summary>
    /// A 200x200 page that fills the whole box with blue while an <c>/Alpha</c> soft mask whose group only
    /// covers the left half is in effect. The mask has to reach a plain path - no form XObject is involved.
    /// With <paramref name="transferFunction"/> the <c>/TR</c> maps 0 to 0.5, and the half the group does
    /// not cover then comes out at half strength: the group leaves transparent there, and an alpha mask's
    /// value is the transfer function applied to that, which is what PDFium paints and what pdf.js
    /// documents for its out-of-bounds alpha. Without the function the same half stays erased.
    /// </summary>
    public static byte[] CreateSoftMaskedRectPdf(bool transferFunction = true)
    {
        byte[] maskContent = Encoding.ASCII.GetBytes("1 g 0 0 100 200 re f");
        string content = "q /GS1 gs 0 0 1 rg 0 0 200 200 re f Q";
        string transfer = transferFunction
            ? "/TR << /FunctionType 2 /Domain [0 1] /C0 [0.5] /C1 [1] /Range [0 1] >> "
            : string.Empty;
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /ExtGState << /GS1 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 100 200] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>", maskContent),
            new PdfObjectDefinition(6, "<< /Type /ExtGState /SMask << /Type /Mask /S /Alpha /G 5 0 R "
                + transfer + ">> >>"));
    }

    /// <summary>
    /// An annotation whose appearance fills itself through a PatternType 2 shading. The appearance's
    /// coordinate system is its own - BBox [0 0 100 50] placed at Rect [400 25 500 75] on a 600-wide
    /// page - and a shading pattern's /Matrix is measured from the content stream's default space, so
    /// the placement has to be in force while the pattern is resolved. Without it the axis stays at the
    /// page origin, the whole visible rectangle lands past its end, and the stamp reads one flat colour.
    /// pdf.js issue7821.pdf is this shape.
    /// </summary>
    public static byte[] CreateAppearanceShadingPatternPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 100] /Annots [6 0 R] "
            + "/Resources << >> >>"),
        Stream(5, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 100 50] "
            + "/Resources << /Pattern << /P0 7 0 R >> >>",
            Encoding.ASCII.GetBytes("/Pattern cs /P0 scn 0 0 100 50 re f")),
        new PdfObjectDefinition(6, "<< /Type /Annot /Subtype /Square /Rect [400 25 500 75] "
            + "/AP << /N 5 0 R >> >>"),
        new PdfObjectDefinition(7, "<< /Type /Pattern /PatternType 2 /Shading 8 0 R /Matrix [1 0 0 1 0 0] >>"),
        new PdfObjectDefinition(8, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] "
            + "/Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>"));

    /// <summary>
    /// A page that paints green, draws a form whose entire content is <c>1 0 0 1 0 0 cm q </c> - an
    /// unbalanced save, which real producers do emit - and then paints red over the left half. Both
    /// colours have to survive: the interpreter's own q/Q pairing sits under the page's transparency
    /// group, so a leaked save level makes the group's Restore pop the leak instead of the group and
    /// the whole page comes out blank.
    /// </summary>
    public static byte[] CreateLeakedSaveFormPdf()
    {
        byte[] leak = Encoding.ASCII.GetBytes("1 0 0 1 0 0 cm q ");
        string content = "0 1 0 rg 0 0 200 200 re f /Leak Do 1 0 0 rg 0 0 100 200 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /XObject << /Leak 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 200 200]", leak));
    }

    /// <summary>
    /// A 200x200 page filled with a Separation colour space whose tint transform is a PostScript
    /// function that ends in <c>3 -1 roll</c>. The roll reorders <c>[tint 0 1]</c> into
    /// <c>[0 1 tint]</c>, so the fill must be cyan-blue (0, 0.5, 1) at tint 0.5; a reader that gives up
    /// on the operator leaves the unrolled order and paints purple (0.5, 0, 1) instead.
    /// <paramref name="braces"/> wraps the program in <c>{ }</c>, which is how Adobe's own producers
    /// write a type 4 stream even though the specification's examples do not.
    /// </summary>
    public static byte[] CreateRollTintTransformPdf(bool braces = false)
    {
        byte[] program = Encoding.ASCII.GetBytes(braces ? "{0 1 3 -1 roll}" : "0 1 3 -1 roll");
        string content = "[/Separation /Spot /DeviceRGB 9 0 R] cs 0.5 scn 0 0 200 200 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(9, "/Type /Function /FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1]", program));
    }

    /// <summary>
    /// A 200x200 page painted with a constant DeviceCMYK axial shading (C 0.46, M 0, Y 0.4775, K 0 - the
    /// tints of a cover wash whose green channel the naive <c>1 - (c + k)</c> conversion pins at full
    /// strength while every viewer's SWOP-based default conversion does not).
    /// </summary>
    public static byte[] CreateCmykShadingPdf()
    {
        string content = "q 0 0 200 200 re W n /Sh0 sh Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Shading << /Sh0 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 200 0] "
                + "/Domain [0 1] /Extend [true true] /Function 7 0 R >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0.46 0 0.4775 0] /C1 [0.46 0 0.4775 0] /N 1 >>"));
    }

    /// <summary>
    /// A 200x200 page painted with a small radial shading (end circle radius 20 at the centre) carrying
    /// <c>/Extend [true true]</c> and a constant black function, over a white background. Everything
    /// outside the end circle must still be black; a reader that misses the boolean Extend entries
    /// leaves the corners white.
    /// </summary>
    public static byte[] CreateExtendedRadialShadingPdf()
    {
        string content = "q 0 0 200 200 re W n /Sh0 sh Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Shading << /Sh0 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /ShadingType 3 /ColorSpace /DeviceGray /Coords [100 100 0 100 100 20] "
                + "/Domain [0 1] /Extend [true true] /Function 7 0 R >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0] /N 1 >>"));
    }

    /// <summary>
    /// A 200x100 page painted with the <c>sh</c> operator from a named shading whose colour space is a
    /// <b>Separation over DeviceRGB</b>: the shading function ramps one tint and only the tint transform
    /// turns it into a colour, so the page must go red (tint 0) to green (tint 1). Reading the tint as a
    /// base component instead gives black to red.
    /// </summary>
    public static byte[] CreateSeparationShadingPdf()
    {
        string content = "q 0 0 200 100 re W n /Sh0 sh Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] "
                + "/Resources << /Shading << /Sh0 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /ShadingType 2 /ColorSpace [/Separation /All /DeviceRGB 8 0 R] "
                + "/Coords [0 0 200 0] /Domain [0 1] /Function 7 0 R >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>"),
            new PdfObjectDefinition(8, "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >>"));
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
    /// <paramref name="codestream"/> is the raw /CCITTFaxDecode data. <paramref name="blackIs1"/> selects the
    /// /BlackIs1 flag: it only chooses which bit is black, so the same codestream rendered at false must be the
    /// exact pixel complement of the true render.
    /// </summary>
    public static byte[] CreateCcittImagePdf(byte[] codestream, bool blackIs1 = true)
    {
        string parms = $"<</K -1/Columns 24/Rows 12/BlackIs1 {(blackIs1 ? "true" : "false")}>>";
        string content = "q 200 0 0 100 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 24 /Height 12 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms {parms} /Length {codestream.Length} >>", codestream));
    }

    /// <summary>
    /// A 24x12pt page holding the codestream as a 1-bit /ImageMask XObject filling the page, decoded with
    /// the default /BlackIs1 (false). Used to pin the stencil polarity of fax data.
    /// </summary>
    public static byte[] CreateCcittMaskPdf(byte[] codestream)
    {
        string content = "q 0 0 0 rg 24 0 0 12 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 24 12] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 24 /Height 12 /BitsPerComponent 1 /ImageMask true /Filter /CCITTFaxDecode /DecodeParms <</K -1 /Columns 24 /Rows 12>> /Length {codestream.Length} >>", codestream));
    }

    /// <summary>
    /// A 4x4pt page whose <c>/XObject</c> resource dictionary is itself an indirect reference - legal for
    /// any PDF value, and what some generators emit. Read without resolving it, the <c>Do</c> finds no
    /// image and the page stays blank without a single diagnostic.
    /// </summary>
    public static byte[] CreateIndirectXObjectPdf()
    {
        byte[] pixels = new byte[4 * 4 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 255;
        }

        string content = "q 4 0 0 4 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 4 4] /Resources << /XObject 6 0 R >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>", pixels),
            new PdfObjectDefinition(6, "<< /Im1 5 0 R >>"));
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
    /// Like <see cref="CreateJbig2ImagePdf"/> but as a 1-bit /ImageMask stencil painted with the current fill
    /// colour (red here), so the test can tell the mask's ink from its transparent background.
    /// </summary>
    public static byte[] CreateJbig2MaskPdf(byte[] jbig2)
    {
        string content = "q 1 0 0 rg 200 0 0 100 0 0 cm /Im1 Do Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(5, $"<< /Type /XObject /Subtype /Image /Width 40 /Height 20 /BitsPerComponent 1 /ImageMask true /Filter /JBIG2Decode /Length {jbig2.Length} >>", jbig2));
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
    /// The same two layers, written the way pdf.js issue269_2.pdf writes them: the /OFF list is an indirect
    /// reference to its own array, and the marked content names a membership dictionary instead of a layer.
    /// Both have to be looked through - reading /OFF as a literal array leaves it empty and every layer on,
    /// and looking a membership dictionary up as if it were a layer answers that it is not in /OFF, so the
    /// off layer paints over the on one.
    /// </summary>
    public static byte[] CreateOcgIndirectOffPdf()
    {
        string content = "/OC /P0 BDC\n1 0 0 rg 10 10 60 60 re f\nEMC\n/OC /P1 BDC\n0 0 1 rg 120 10 60 60 re f\nEMC";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R "
                + "/OCProperties << /OCGs [6 0 R 7 0 R] /D << /OFF 8 0 R >> >> >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] "
                + "/Resources << /Properties << /P0 6 0 R /P1 9 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /Type /OCG /Name (Red) >>"),
            new PdfObjectDefinition(7, "<< /Type /OCG /Name (Blue) >>"),
            new PdfObjectDefinition(8, "[7 0 R]"),
            new PdfObjectDefinition(9, "<< /Type /OCMD /OCGs [7 0 R] >>"));
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
    /// A 100x100 page painted with one <c>ShadingType 7</c> tensor-product patch. Its 16 control points lie
    /// on a uniform grid, so the surface is exactly the square, and its four corner colours make the left
    /// edge black and the right edge white - which pins down both the stream's boundary-then-interior point
    /// order and which corners the four colours belong to.
    /// </summary>
    public static byte[] CreateType7PatchPdf() => CreateType7PatchPdfCore(withFunction: true);

    /// <summary>
    /// The same Type 7 patch as <see cref="CreateType7PatchPdf"/> but with the <c>/Function</c> entry omitted.
    /// Real-world mesh shadings (personwithdog.pdf and others in the pdf.js suite) are written this way,
    /// relying on the sampled components as direct colourspace operands; the rendering must match the
    /// version that spells out the (identity) function explicitly.
    /// </summary>
    public static byte[] CreateType7PatchNoFunctionPdf() => CreateType7PatchPdfCore(withFunction: false);

    private static byte[] CreateType7PatchPdfCore(bool withFunction)
    {
        var bits = new List<int>();
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }

        // Raw 0/85/170/255 through /Decode [0 100 ...] are 0, 33.3, 66.7, 100: a uniform 4x4 grid.
        (int x, int y) Pole(int i, int j) => (new[] { 0, 85, 170, 255 }[i], new[] { 0, 85, 170, 255 }[j]);

        Put(0, 8); // flag 0: every point and colour follows.
        // Table 76 order: the bottom edge, up the right, back along the top, down the left, then the interior.
        int[] boundary = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        foreach (int index in boundary)
        {
            (int i, int j) = index switch
            {
                0 => (0, 0), 1 => (0, 1), 2 => (0, 2), 3 => (0, 3),
                4 => (1, 3), 5 => (2, 3), 6 => (3, 3),
                7 => (3, 2), 8 => (3, 1), 9 => (3, 0),
                10 => (2, 0), _ => (1, 0),
            };
            (int x, int y) = Pole(i, j);
            Put(x, 8);
            Put(y, 8);
        }

        foreach ((int i, int j) in new[] { (1, 1), (1, 2), (2, 2), (2, 1) })
        {
            (int x, int y) = Pole(i, j);
            Put(x, 8);
            Put(y, 8);
        }

        // The four corner colours, at points 0, 3, 6 and 9: left edge black, right edge white.
        Put(0, 8); Put(0, 8); Put(255, 8); Put(255, 8);

        var data = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i] != 0)
            {
                data[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }

        string shading = "/ShadingType 7 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1]" + (withFunction ? " /Function 6 0 R" : string.Empty);
        return withFunction
            ? Build(1,
                new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
                Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
                Stream(5, shading, data),
                new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0] /C1 [1] /Domain [0 1] /N 1 >>"))
            : Build(1,
                new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
                new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
                Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
                Stream(5, shading, data));
    }

    /// <summary>
    /// A 100x100 page painted with a <c>ShadingType 4</c> free-form Gouraud-shaded triangle mesh: two
    /// flag-0 triangles tiling the square, black along the left edge and white along the right, so the
    /// ramp runs left to right. This exercises the flag-per-vertex reader and the triangle strip - the
    /// previous code wrongly parsed type 4 as a 16-point tensor patch, blanking real files like
    /// pdf.js issue2948.pdf (a 76% difference against MuPDF) and coons tests.
    /// </summary>
    public static byte[] CreateType4TrianglePdf()
    {
        // Each vertex is flag(8) + x(8) + y(8) + grey(8); /Decode maps raw 0..255 to 0..100 and grey 0..1.
        byte[] data =
        [
            // triangle 1: (0,0,black) (100,0,white) (100,100,white)
            0x00, 0x00, 0x00, 0x00,   0x00, 0xFF, 0x00, 0xFF,   0x00, 0xFF, 0xFF, 0xFF,
            // triangle 2: (0,0,black) (100,100,white) (0,100,black)
            0x00, 0x00, 0x00, 0x00,   0x00, 0xFF, 0xFF, 0xFF,   0x00, 0x00, 0xFF, 0x00,
        ];
        string shading = "/ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1] /Function 6 0 R";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
            Stream(5, shading, data),
            new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0] /C1 [1] /Domain [0 1] /N 1 >>"));
    }

    /// <summary>
    /// A 100x100 page painted with one <c>ShadingType 6</c> constant-colour tensor-product patch: a single
    /// flag-0 patch whose 12 boundary control points form a uniform grid over the page (so the cubic surface
    /// is the flat square) with all four corner colours black. Type 6 supplies only the 12 boundary points and
    /// four colours - the four interior poles are computed - so this exercises the reader and the computed
    /// interior that the earlier Coons approximation got wrong (pdf.js coons-allflags-withfunction.pdf).
    /// </summary>
    public static byte[] CreateType6PatchPdf()
    {
        // flag(0) then 12 boundary points (x,y, 8-bit raw; /Decode [0 100 ...] maps raw 255 -> 100),
        // walking the Table 76 boundary order, then four corner colours (all black).
        byte[] data =
        [
            0x00,                                             // flag 0: new patch, all 12 points follow.
            0x00, 0x00,  0x55, 0x00,  0xAA, 0x00,  0xFF, 0x00, // bottom edge (v0..v3)
            0xFF, 0x55,  0xFF, 0xAA,  0xFF, 0xFF,              // right edge (v4..v6)
            0xAA, 0xFF,  0x55, 0xFF,  0x00, 0xFF,              // top edge (v7..v9)
            0x00, 0xAA,  0x00, 0x55,                           // left edge (v10..v11)
            0x00, 0x00, 0x00, 0x00,                            // four corner colours: black.
        ];
        string shading = "/ShadingType 6 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1] /Function 6 0 R";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
            Stream(5, shading, data),
            new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0] /C1 [1] /Domain [0 1] /N 1 >>"));
    }

    /// <summary>
    /// A page with nothing on it plus an indirect Type 4 function whose program leaves its inputs on the
    /// stack (the shape a DeviceN tint transform is written in). Object 5 is the function: evaluating it
    /// must yield the same channels whatever width the caller's buffer happens to have.
    /// </summary>
    public static byte[] CreateLeftoverStackFunctionPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q Q")),
        Stream(5, "/FunctionType 4 /Domain [0 1 0 1 0 1] /Range [0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ exch 0.5 mul dup dup }")));

    /// <summary>
    /// A 100x100 page filled edge to edge with a shading pattern whose axial shading declares a smaller
    /// <c>/BBox</c> [20 20 80 80] and <c>/Extend [true true]</c>. The extend flags would flood the whole
    /// clip, so only the box stops it - the corners stay white. (pdf.js issue8092.pdf paints a whole page
    /// with a pattern whose boxed shading extends both ways.)
    /// </summary>
    public static byte[] CreateAxialShadingBBoxPdf() => Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> /ColorSpace << /Cs1 8 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("/Cs1 cs 1 1 1 rg 0 0 100 100 re f /Pattern cs /P0 scn 0 0 100 100 re f")),
            new PdfObjectDefinition(5, "<< /Type /Pattern /PatternType 2 /Shading 6 0 R >>"),
            new PdfObjectDefinition(6, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 50 80 50] /Function 7 0 R /BBox [20 20 80 80] /Extend [true true] >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>"),
            new PdfObjectDefinition(8, "[/Pattern /DeviceRGB]"));

    /// <summary>
    /// A 100x100 page with the same Type3 glyph shown twice: once under Tr 0 (fill) and once under Tr 3
    /// (invisible). A Type3 glyph paints by running its own content stream, so the render mode has to gate
    /// that run - pdf.js issue5421.pdf showed its "3 Tr" line because the procedure executed anyway.
    /// </summary>
    public static byte[] CreateType3InvisibleTextPdf()
    {
        byte[] glyph = Encoding.ASCII.GetBytes("800 0 0 0 800 800 d0\n0 0 0 rg\n0 0 m 800 0 l 800 800 l 0 800 l h f");
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("BT 0 Tr /F1 60 Tf 10 10 Td (A) Tj ET BT 3 Tr /F1 60 Tf 60 60 Td (A) Tj ET")),
            new PdfObjectDefinition(5, "<< /Type /Font /Subtype /Type3 /FontMatrix [.001 0 0 .001 0 0] /FirstChar 65 /LastChar 65 /Widths [800] /Encoding << /Type /Encoding /Differences [65 /A] >> /CharProcs << /A 6 0 R >> >>"),
            Stream(6, string.Empty, glyph));
    }

    /// <summary>
    /// A linearized-shape document: the /xref section is an empty placeholder and the trailer points at a
    /// catalog that lives INSIDE an object stream. Reading it needs both fallbacks the corpus files proved
    /// necessary - rebuild the object table by scanning, then expand the object streams the scan found -
    /// because the compressed catalog has no entry anywhere (pdf.js empty_protected.pdf and its family).
    /// </summary>
    public static byte[] CreateCompressedCatalogAfterRebuildPdf()
    {
        // The object stream holds objects 2 (catalog) and 3 (page tree): "num offset" pairs, then the
        // objects at those offsets relative to /First.
        byte[] header = Encoding.ASCII.GetBytes("2 0 3 33 ");
        byte[] catalog = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 3 0 R >>");
        byte[] pages = Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        byte[] streamBody = [.. header, .. catalog, .. pages];
        int first = header.Length;

        string pageObj = "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 100 100] /Contents 5 0 R /Resources << >> >>";
        byte[] contentBytes = Encoding.ASCII.GetBytes("1 0 0 rg 10 10 80 80 re f");
        string contentObj = $"<< /Length {contentBytes.Length} >>";

        var builder = new StringBuilder();
        builder.Append("%PDF-1.7\n");
        builder.Append("1 0 obj << /Type /ObjStm /N 2 /First ").Append(first)
               .Append(" /Length ").Append(streamBody.Length).Append(" >>\nstream\n");
        string tailAfterStream = "\nendstream\nendobj\n"
            + "4 0 obj " + pageObj + " endobj\n"
            + "5 0 obj " + contentObj + "\nstream\n";
        string afterContent = "\nendstream\nendobj\n"
            + "xref\n0 0\ntrailer << /Size 6 /Root 2 0 R >>\nstartxref\n0\n%%EOF\n";

        byte[] prefix = Encoding.ASCII.GetBytes(builder.ToString());
        byte[] middle = Encoding.ASCII.GetBytes(tailAfterStream);
        byte[] suffix = Encoding.ASCII.GetBytes(afterContent);

        byte[] all = new byte[prefix.Length + streamBody.Length + middle.Length + contentBytes.Length + suffix.Length];
        int at = 0;
        prefix.CopyTo(all, at); at += prefix.Length;
        streamBody.CopyTo(all, at); at += streamBody.Length;
        middle.CopyTo(all, at); at += middle.Length;
        contentBytes.CopyTo(all, at); at += contentBytes.Length;
        suffix.CopyTo(all, at);
        return all;
    }

    /// <summary>
    /// A 100x100 page with one ShadingType 5 lattice whose vertices are 32 bits per coordinate
    /// (pdf.js personwithdog.pdf declares BitsPerCoordinate 32). Reading a 32-bit coordinate through the
    /// int-returning bit reader overflows to a negative value, the mesh lands off-page and the page stays
    /// blank. Row 0 is black, row 1 white, so a correct read paints a visible ramp.
    /// </summary>
    public static byte[] CreateType5Mesh32BitPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
        Stream(5, "/ShadingType 5 /ColorSpace /DeviceRGB /BitsPerCoordinate 32 /BitsPerComponent 8 /VerticesPerRow 2 /Decode [0 100 0 100 0 1] /Function 6 0 R", new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        }),
        new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0 0 0] /C1 [1 1 1] /Domain [0 1] /N 1 >>"));

    /// <summary>
    /// A 100x100 page painted with one <c>ShadingType 5</c> lattice-form Gouraud triangle mesh: two rows of
    /// two vertices, so a single quad that becomes a left-to-right black-to-white ramp. The row's first
    /// vertex is (0,0) and last (0,100) at colour 0; the second row is (100,0) and (100,100) at colour 1 -
    /// which pins down the row-major read order and the quad stitching between consecutive rows.
    /// </summary>
    public static byte[] CreateType5LatticePdf()
    {
        // Each vertex: x, y, colour, all 8 bits. Row 0 black on the left edge, row 1 white on the right.
        byte[] data =
        [
            0x00, 0x00, 0x00,   0x00, 0xFF, 0x00,   // row 0: (0,0,black) (0,100,black)
            0xFF, 0x00, 0xFF,   0xFF, 0xFF, 0xFF,   // row 1: (100,0,white) (100,100,white)
        ];
        string shading = "/ShadingType 5 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /VerticesPerRow 2 /Decode [0 100 0 100 0 1] /Function 6 0 R";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 0 0 100 100 re W n /S0 sh Q")),
            Stream(5, shading, data),
            new PdfObjectDefinition(6, "<< /FunctionType 2 /C0 [0 0 0] /C1 [1 1 1] /Domain [0 1] /N 1 >>"));
    }

    /// <summary>
    /// A 100x100 page that draws a form (a red square) under an ExtGState with ca .5, then paints a full
    /// green square afterwards. The form's content opens a q but never closes it - legal, and common. That
    /// stray q must not strand the form's saved state (with its 0.5 alpha): if it does, the page-level Q
    /// restores 0.5 and the following green square paints translucent. pdf.js issue1905_reduced.pdf washed
    /// every image after exactly such a form.
    /// </summary>
    public static byte[] CreateFormWithUnclosedStatePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /G1 6 0 R >> /XObject << /Fm1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q /G1 gs /Fm1 Do Q 0 1 0 rg 0 0 100 100 re f")),
        Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]", Encoding.ASCII.GetBytes("q 1 0 0 rg 0 0 100 100 re f")),
        new PdfObjectDefinition(6, "<< /ca .5 /CA .5 >>"));

    /// <summary>
    /// A 100x100 page where a Type3 font carries NO /Resources and its glyph procedure draws an "X" using
    /// a font defined in the page's resources. A glyph procedure inherits the resources in force where the
    /// text was shown (pdf.js pr4922.pdf), so the X must appear.
    /// </summary>
    public static byte[] CreateType3InheritedResourcesPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R /T3 6 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("BT 10 10 Td /T3 60 Tf (A) Tj ET")),
        new PdfObjectDefinition(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        new PdfObjectDefinition(6, "<< /Type /Font /Subtype /Type3 /FontMatrix [1 0 0 1 0 0] /FirstChar 65 /LastChar 65 /Widths [1] /Encoding << /Type /Encoding /Differences [65 /A] >> /CharProcs << /A 7 0 R >> >>"),
        Stream(7, string.Empty, Encoding.ASCII.GetBytes("1 0 0 0 1 1 d1\nBT /F1 0.6 Tf 0 0 Td (X) Tj ET")));

    /// <summary>
    /// A 100x100 page filled with a tiling pattern whose /Matrix shears the lattice ([1 .5 0 1 0 0]) and
    /// whose 10x10 black cells sit 20 apart. Correct placement paints parallelograms, so the point (5,18)
    /// - inside the bounding box of a cell but outside the sheared one - must stay white (pdf.js
    /// issue2177.pdf uses [1.4 1 -.5 .7 0 0]).
    /// </summary>
    public static byte[] CreateShearedTilingPatternPdf()
    {
        byte[] cell = Encoding.ASCII.GetBytes("0 0 0 rg 0 0 10 10 re f");
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> /ColorSpace << /Cs1 7 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("/Cs1 cs /P0 scn 0 0 100 100 re f")),
            Stream(5, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 20 /YStep 20 /Matrix [1 0.5 0 1 0 0]", cell),
            new PdfObjectDefinition(7, "[/Pattern /DeviceRGB]"));
    }

    /// <summary>
    /// A 100x100 page showing one Type3 glyph whose procedure declares d1 (a stencil) and fills itself
    /// WHITE. A d1 glyph is painted with the text's colour and its own colour operators are ignored, so the
    /// square must appear black - pdf.js issue12705.pdf's glyphs are exactly this.
    /// </summary>
    public static byte[] CreateType3StencilGlyphPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("BT 10 40 Td /F1 60 Tf (A) Tj ET")),
        new PdfObjectDefinition(5, "<< /Type /Font /Subtype /Type3 /FontMatrix [.001 0 0 .001 0 0] /FirstChar 65 /LastChar 65 /Widths [800] /Encoding << /Type /Encoding /Differences [65 /A] >> /CharProcs << /A 6 0 R >> >>"),
        Stream(6, string.Empty, Encoding.ASCII.GetBytes("800 0 0 0 800 800 d1\n1 1 1 rg\n0 0 m 800 0 l 800 800 l 0 800 l h f")));

    /// <summary>
    /// A 100x100 page whose only marks are two annotations with no appearance stream: a red-stroked Square
    /// and a blue-stroked, green-filled Circle (ISO 32000-1 12.5.5.2/12.5.5.3 leave their drawing to the
    /// viewer) - the shape of pdf.js annotation-square-circle-without-appearance.pdf.
    /// </summary>
    public static byte[] CreateSquareCircleAnnotationPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R 6 0 R] >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("1 1 1 rg 0 0 100 100 re f")),
        new PdfObjectDefinition(5, "<< /Type /Annot /Subtype /Square /Rect [10 10 40 40] /C [1 0 0] /Border [0 0 4] /F 4 >>"),
        new PdfObjectDefinition(6, "<< /Type /Annot /Subtype /Circle /Rect [60 60 90 90] /C [0 0 1] /IC [0 1 0] /Border [0 0 2] /F 4 >>"));

    /// <summary>
    /// A 100x100 page drawing a 2x2 red image under an 8x8 stencil mask whose left half is set. The mask is
    /// finer than the image, so it must keep its own resolution - the left half of the painted square is red
    /// and the right half stays white. Reading it at the image's 2x2 grid lost that split entirely (pdf.js
    /// issue4246.pdf: a 1000x800 stencil over a 50x40 image).
    /// </summary>
    public static byte[] CreateFineStencilMaskPdf()
    {
        byte[] image = new byte[2 * 2 * 3];
        for (int i = 0; i < 4; i++)
        {
            image[i * 3] = 255; // red
        }

        byte[] mask = Enumerable.Repeat((byte)0xF0, 8).ToArray(); // left four columns set
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 100 0 0 100 0 0 cm /Im1 Do Q")),
            Stream(5, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask 6 0 R", image),
            Stream(6, "/Type /XObject /Subtype /Image /Width 8 /Height 8 /ImageMask true /BitsPerComponent 1", mask));
    }

    /// <summary>
    /// A 200x50 page drawing "ABC" with a TrueType font whose embedded /FontFile2 is garbage. The back end
    /// must fall back to a system face and still paint the text (pdf.js bug1200096.pdf's subset program does
    /// not load, and its Skia render came out blank).
    /// </summary>
    public static byte[] CreateTextWithBrokenEmbeddedFontPdf()
    {
        string widths = string.Join(' ', Enumerable.Repeat("500", 95));
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50] /Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>"),
            new PdfObjectDefinition(4, $"<< /Type /Font /Subtype /TrueType /BaseFont /KVIQXV#20Times#20New#20Roman /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths 5 0 R /FontDescriptor 6 0 R >>"),
            new PdfObjectDefinition(5, $"[{widths}]"),
            new PdfObjectDefinition(6, "<< /Type /FontDescriptor /FontName /KVIQXV#20Times#20New#20Roman /Flags 32 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>"),
            Stream(7, string.Empty, Enumerable.Repeat((byte)0x5A, 64).ToArray()),
            Stream(8, string.Empty, Encoding.ASCII.GetBytes("BT 10 20 TD /F1 20 Tf (ABC) Tj ET")));
    }

    /// <summary>
    /// A 100x100 page that strokes a square while an ExtGState /SMask paints its luminosity group with
    /// a named shading through the {@code sh} operator: the mask form carries /Matrix [50 0 0 100 0 0] over
    /// a unit BBox and its whole content is "/Sh sh", so the black square must fade out towards the right
    /// (the shape of pdf.js issue18529.pdf, where the mask comes out uniformly white and nothing fades).
    /// </summary>
    public static byte[] CreateShadingSoftMaskPdf()
    {
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /GS0 4 0 R >> >> /Contents 5 0 R >>"),
            new PdfObjectDefinition(4, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >>"),
            Stream(5, string.Empty, Encoding.ASCII.GetBytes("1 0 0 -1 0 100 cm 2 w 0 0 0 RG /GS0 gs 5 5 90 90 re S")),
            Stream(6, "/Type /XObject /Subtype /Form /FormType 1 /BBox [-0.05 0 1.05 1] /Matrix [50 0 0 100 0 0] /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << /Shading << /Sh 7 0 R >> >>",
                Encoding.ASCII.GetBytes("/Sh sh")),
            new PdfObjectDefinition(7, "<< /ShadingType 2 /ColorSpace [/DeviceGray] /Coords [0 0 1 0] /Function 8 0 R /Extend [true true] >>"),
            new PdfObjectDefinition(8, "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>"));
    }

    /// <summary>
    /// The same soft-masked stroke as <see cref="CreateShadingSoftMaskPdf"/> with both resource category
    /// dictionaries moved behind indirect references - /ExtGState 9 0 R and /Shading 11 0 R - which
    /// ISO 32000-1 7.3.1 allows for any resource and pdf.js issue18529.pdf does exactly. A lookup that only
    /// accepts the inline form finds neither the gs (so no soft mask, the stroke stays full strength) nor
    /// the shading (so the mask paints nothing and the stroke disappears entirely).
    /// </summary>
    public static byte[] CreateIndirectResourceCategoriesPdf()
    {
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 5 0 R >>"),
            new PdfObjectDefinition(4, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >>"),
            Stream(5, string.Empty, Encoding.ASCII.GetBytes("2 w 0 0 0 RG /GS0 gs 5 90 90 5 re S")),
            Stream(6, "/Type /XObject /Subtype /Form /FormType 1 /BBox [-0.05 0 1.05 1] /Matrix [50 0 0 100 0 0] " +
                "/Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << /Shading 11 0 R >>",
                Encoding.ASCII.GetBytes("/Sh sh")),
            new PdfObjectDefinition(7, "<< /ShadingType 2 /ColorSpace [/DeviceGray] /Coords [0 0 1 0] /Function 8 0 R /Extend [true true] >>"),
            new PdfObjectDefinition(8, "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>"),
            new PdfObjectDefinition(9, "<< /ExtGState 10 0 R >>"),
            new PdfObjectDefinition(10, "<< /GS0 4 0 R >>"),
            new PdfObjectDefinition(11, "<< /Sh 7 0 R >>"));
    }

    /// <summary>
    /// issue18529.pdf reproduced number for number: a 65x50 page that strokes a 45x30 rectangle under a
    /// luminosity /SMask whose group is a form with /Matrix [45 0 0 30 10 10], a BBox reaching past the unit
    /// square on both axes, a /Group whose CS is the array form, and content that is only "/Sh sh" over an
    /// extended DeviceGray axial shading. The stroke has to fade out left to right.
    /// </summary>
    public static byte[] CreateShadingSoftMaskStrokeReplicaPdf()
    {
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 65 50] /Resources << /ExtGState << /Gs0 4 0 R >> >> /Contents 5 0 R /Group << /Type /Group /S /Transparency /CS [/DeviceRGB] >> >>"),
            new PdfObjectDefinition(4, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >>"),
            Stream(5, string.Empty, Encoding.ASCII.GetBytes("1 0 0 -1 0 50 cm\n5 w\n1 0 0 RG\n1 1 1 rg\n/Gs0 gs\n10 10 45 30 re\nS")),
            Stream(6, "/Type /XObject /Subtype /Form /Length 8 /Matrix [45 0 0 30 10 10] " +
                "/BBox [-.05555556 -.08333333 1.0555556 1.0833334] " +
                "/Group << /Type /Group /S /Transparency /CS [/DeviceGray] >> " +
                "/Resources << /Shading << /Sh 7 0 R >> >>",
                Encoding.ASCII.GetBytes("/Sh sh")),
            new PdfObjectDefinition(7, "<< /ShadingType 2 /ColorSpace [/DeviceGray] /AntiAlias true /Function 8 0 R /Coords [0 0 1 0] /Extend [true true] >>"),
            new PdfObjectDefinition(8, "<< /FunctionType 2 /Range [0 1] /C0 [1] /C1 [0] /Domain [0 1] /N 1 >>"));
    }

    /// <summary>
    /// pdf.js knockout_inner_backdrop.pdf: an isolated knockout group (/K true) that fills itself blue and
    /// then draws a nested NON-isolated group (/K false /I false) whose two opaque rects multiply with each
    /// other. A knockout group clears what it already holds beneath each new element, so those rects must
    /// blend with an emptied backdrop - red stays red, green stays green, and only their overlap goes black.
    /// Blending them with the blue instead turns the whole nested group black.
    /// </summary>
    public static byte[] CreateKnockoutGroupWithNestedBlendPdf()
    {
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /OuterGroup 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("q\n/OuterGroup Do\nQ")),
            Stream(5, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 200 200] " +
                "/Group << /Type /Group /S /Transparency /K true /I true >> " +
                "/Resources << /XObject << /InnerGroup 6 0 R >> >>",
                Encoding.ASCII.GetBytes("0 0 1 rg\n0 0 200 200 re f\n/InnerGroup Do")),
            Stream(6, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 200 200] " +
                "/Group << /Type /Group /S /Transparency /K false /I false >> " +
                "/Resources << /ExtGState << /GSM 7 0 R >> >>",
                Encoding.ASCII.GetBytes("/GSM gs\n1 0 0 rg\n20 20 130 130 re f\n0 1 0 rg\n50 50 130 130 re f")),
            new PdfObjectDefinition(7, "<< /Type /ExtGState /BM /Multiply >>"));
    }

    /// <summary>
    /// A 200x50 page drawing "ABC" with a WinAnsi TrueType font that embeds the given program, so a test can
    /// hand over a subset with tables removed - pdf.js TrueType_without_cmap.pdf ships one with no /cmap.
    /// </summary>
    public static byte[] CreateTextWithFontProgramPdf(byte[] program)
    {
        string widths = string.Join(' ', Enumerable.Repeat("500", 95));
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50] /Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>"),
            new PdfObjectDefinition(4, "<< /Type /Font /Subtype /TrueType /BaseFont /LiberationSans /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths 5 0 R /FontDescriptor 6 0 R >>"),
            new PdfObjectDefinition(5, $"[{widths}]"),
            new PdfObjectDefinition(6, "<< /Type /FontDescriptor /FontName /LiberationSans /Flags 32 /FontBBox [0 -200 1000 800] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>"),
            Stream(7, string.Empty, program),
            Stream(8, string.Empty, Encoding.ASCII.GetBytes("BT 10 20 TD /F1 20 Tf (ABC) Tj ET")));
    }

    /// <summary>
    /// A 100x100 page where a text object in render mode 4 (fill and clip) shows a single space - a glyph
    /// with no outline - and is followed by a full-page red fill. The text object still replaces the
    /// clipping path, and an empty clip paints nothing, so the page must stay white (the shape of pdf.js
    /// recursiveCompositGlyf.pdf, where both references render the page blank).
    /// </summary>
    public static byte[] CreateEmptyTextClipPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("BT /F1 40 Tf 4 Tr ( ) Tj ET 1 0 0 rg 0 0 100 100 re f")),
        new PdfObjectDefinition(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

    /// <summary>
    /// A 100x100 yellow page with a form that declares a transparency group WITHOUT /I (so non-isolated,
    /// the spec default) and knockout, whose content multiplies a cyan rectangle with what is beneath it.
    /// A non-isolated group blends against the page, so the block must come out green - the shape of pdf.js
    /// knockout_blend_multiply.pdf.
    /// </summary>
    public static byte[] CreateNonIsolatedBlendGroupPdf()
    {
        byte[] formContent = Encoding.ASCII.GetBytes("q /M gs 0 1 1 rg 20 20 60 60 re f Q");
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /G1 5 0 R >> /ExtGState << /M 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("1 1 0 rg 0 0 100 100 re f /G1 Do")),
            Stream(5, "/Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 100 100] /Resources << /ExtGState << /M 6 0 R >> >> /Group << /Type /Group /S /Transparency /CS /DeviceRGB /K true >>", formContent),
            new PdfObjectDefinition(6, "<< /Type /ExtGState /BM /Multiply >>"));
    }

    /// <summary>
    /// A 100x100 page filled with an axial shading defined in <c>/CalRGB</c> with <c>/Gamma [1 1 1]</c> - i.e.
    /// linear RGB - whose function returns a flat 0.5. Those samples are not sRGB values: after the white
    /// point, matrix and sRGB transfer they land near 187, while reading them straight as encoded sRGB gives
    /// a visibly darker 128.
    /// </summary>
    public static byte[] CreateCalRgbShadingPdf()
    {
        string shading = "/ShadingType 2 /Coords [0 50 100 50] /Extend [true true] /Function 7 0 R " +
            "/ColorSpace [/CalRGB << /WhitePoint [0.9505 1 1.0889] " +
            "/Matrix [0.4124 0.3576 0.1805 0.2126 0.7152 0.0722 0.0193 0.1192 0.9505] /Gamma [1 1 1] >>]";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> /ColorSpace << /Cs1 8 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("/Cs1 cs /P0 scn 0 0 100 100 re f")),
            new PdfObjectDefinition(5, "<< /Type /Pattern /PatternType 2 /Shading 6 0 R >>"),
            new PdfObjectDefinition(6, $"<< {shading} >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0.5 0.5 0.5] /C1 [0.5 0.5 0.5] /N 1 >>"),
            new PdfObjectDefinition(8, "[/Pattern /DeviceRGB]"));
    }

    /// <summary>
    /// A 100x100 page whose only mark is an ImageMask XObject drawn while the fill colour is a shading
    /// pattern: the mask is a stencil, so the gradient must show through only where its bits are set (the
    /// top-left quarter here) and the rest stays white - pdf.js issue13372.pdf draws a 646x761 bilevel mask
    /// over an axial shading pattern.
    /// </summary>
    public static byte[] CreateImageMaskWithShadingPatternPdf()
    {
        // An ImageMask without /Decode paints where its bits are CLEAR (ISO 32000-1 8.9.6.2), so 0x30 keeps
        // the first two columns of the top two rows painting and 0xFF leaves the bottom rows empty.
        byte[] maskData = [0x30, 0x30, 0xFF, 0xFF];
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 7 0 R >> /ColorSpace << /Cs1 10 0 R >> /XObject << /Im1 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("/Cs1 cs /P0 scn q 100 0 0 100 0 0 cm /Im1 Do Q")),
            Stream(6, "/Type /XObject /Subtype /Image /ImageMask true /Width 4 /Height 4 /BitsPerComponent 1", maskData),
            new PdfObjectDefinition(7, "<< /Type /Pattern /PatternType 2 /Shading 8 0 R >>"),
            new PdfObjectDefinition(8, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Function 9 0 R /Extend [true true] >>"),
            new PdfObjectDefinition(9, "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>"),
            new PdfObjectDefinition(10, "[/Pattern /DeviceRGB]"));
    }

    /// <summary>
    /// A 100x100 page filled by a tiling pattern whose cell shows one ZapfDingbats glyph through
    /// /Differences [1 /a109] - a109 is the spade U+2660. The glyph name must resolve through the Adobe
    /// Glyph List and the family must map the Dingbats block, or nothing paints (pdf.js issue15716.pdf).
    /// </summary>
    public static byte[] CreateTilingDingbatPdf()
    {
        byte[] cell = Encoding.ASCII.GetBytes("BT /F1 40 Tf 10 20 Td (\u0001) Tj ET");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << /Font << /F1 6 0 R >> >>";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("[/Pattern /DeviceRGB] cs /P0 scn 0 0 100 100 re f")),
            Stream(5, patternDict, cell),
            new PdfObjectDefinition(6, "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats /Encoding << /Type /Encoding /Differences [1 /a109] >> >>"));
    }

    public static byte[] CreateTilingTextPdf()
    {
        byte[] cell = Encoding.ASCII.GetBytes("BT /F1 40 Tf 10 20 Td (A) Tj ET");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << /Font << /F1 6 0 R >> >>";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes("[/Pattern /DeviceRGB] cs /P0 scn 0 0 100 100 re f")),
            Stream(5, patternDict, cell),
            new PdfObjectDefinition(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
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
    /// A 100x100 page whose content is one 1x1 inline image filled with the three bytes 'z','E','I'
    /// (RGB 122,69,73). The data deliberately contains the characters "EI" not preceded by white space (so
    /// it is data, not the terminator), and the single white-space after <c>ID</c> is not part of it. Reading
    /// that separator as data shifted the samples by a byte (pdf.js issue11124.pdf decoded off by one).
    /// </summary>
    public static byte[] CreateInlineImageWithEmbeddedEiPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 100 0 0 100 0 0 cm\nBI /W 1 /H 1 /CS /RGB /BPC 8\nID\nzEI\nEI Q")));

    /// <summary>
    /// A 100x100 page showing a 2x2 Indexed image whose palette is over a <c>/Lab</c> base. Every sample
    /// is index 1, whose Lab bytes are L*=50, a*=+80, b*=+60 (red-orange). Read as raw RGB the same bytes
    /// are a pale cyan; only a real Lab conversion makes the fill red-dominant - pdf.js
    /// issue10339_reduced.pdf's blue Lab gradient came out brown until the palette was converted.
    /// </summary>
    public static byte[] CreateIndexedLabImagePdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 100 0 0 100 0 0 cm /Im1 Do Q")),
        new PdfObjectDefinition(5, "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace [/Indexed [/Lab <</WhitePoint[0.9505 1 1.089]/Range[-128 127 -128 127]>>] 1 <FF808080D0BC>] /Length 4 >>", new byte[] { 1, 1, 1, 1 }));

    /// <summary>
    /// The Lab palette of <see cref="CreateIndexedLabImagePdf"/> shown with both of its entries: index 0 is
    /// L*=100, a=0, b=0 - the reference white itself - declared against the D50 illuminant PDF defines
    /// CIELab on. A conversion that treats that white as if it were the output's own paints it yellow.
    /// </summary>
    public static byte[] CreateIndexedLabWhitePointPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q 100 0 0 100 0 0 cm /Im1 Do Q")),
        new PdfObjectDefinition(5, "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
            + "/ColorSpace [/Indexed [/Lab <</WhitePoint[0.9642 1 0.8249]/Range[-128 127 -128 127]>>] 1 <FF808080D0BC>] "
            + "/Length 4 >>", new byte[] { 0, 0, 0, 1 }));

    /// <summary>
    /// A 100x100 page that draws one form which paints the page red. The form carries its own /OC - an OCMD
    /// whose single OCG is switched off by /OCProperties /D /OFF - so the form must not paint at all. An
    /// XObject-level /OC that is ignored buries the page under the form's content: pdf.js
    /// issue12007_reduced.pdf painted two large image forms that way (76% ink against a blank reference).
    /// </summary>
    public static byte[] CreateFormWithHiddenOptionalContentPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [8 0 R] /D << /OFF [8 0 R] >> >> >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Fm1 5 0 R >> >> /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("q /Fm1 Do Q")),
        Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /OC 7 0 R", Encoding.ASCII.GetBytes("1 0 0 rg 0 0 100 100 re f")),
        new PdfObjectDefinition(7, "<< /Type /OCMD /OCGs [8 0 R] >>"),
        new PdfObjectDefinition(8, "<< /Type /OCG /Name (Hidden) >>"));

    /// <summary>
    /// A 200x100 page whose whole content fills the page black under a clip that begins with a lone W
    /// before the rectangle is built (pdf.js clippath.pdf). The clip has to come from the rectangle the
    /// path ends up with - MuPDF and PDFium clip when the path is consumed - so only the 40..160 x 20..80
    /// rectangle paints; clipping eagerly on the then-empty path left the page black.
    /// </summary>
    public static byte[] CreateDeferredClipPdf() => Build(1,
        new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R >>"),
        Stream(4, string.Empty, Encoding.ASCII.GetBytes("W\n40 20 m\n160 20 l\n160 80 l\n40 80 l\nh\nn\n0 0 0 sc\n0 0 200 100 re\nf")));

    /// <summary>
    /// A tiling pattern whose /XStep and /YStep (500) are far larger than the 20x20 cell - pdf.js
    /// tiling-pattern-large-steps.pdf. The cell then paints once at each lattice point with a big gap
    /// between; sizing the raster tile by the step (as the old code did, clamped to a cap) drew nothing.
    /// The red left half of the origin cell must show over the white page.
    /// </summary>
    public static byte[] CreateTilingLargeStepsPdf()
    {
        byte[] cellContent = Encoding.ASCII.GetBytes("1 1 1 rg 0 0 20 20 re f\n1 0 0 rg 0 0 10 20 re f\n");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 500 /YStep 500";
        string content = "1 1 1 rg 0 0 100 100 re f\n[/Pattern /DeviceRGB] cs /P0 scn 0 0 100 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, patternDict, cellContent));
    }

    /// <summary>
    /// The same tiling page, but the cell's <c>/Resources</c> is an <b>indirect</b> dictionary and the cell
    /// paints through an image XObject it declares - the shape generators use for the cell artwork. Reading
    /// the resources without resolving the reference left the cell's <c>Do</c> unresolvable and the whole
    /// fill blank.
    /// </summary>
    public static byte[] CreateTilingWithIndirectResourcesPdf()
    {
        byte[] samples = new byte[40]; // 10 columns x 20 rows, 1 bit per sample: ink (0) on the left half
        for (int row = 0; row < 20; row++)
        {
            samples[(row * 2)] = 0x07;
            samples[(row * 2) + 1] = 0xF0;
        }

        byte[] cellContent = Encoding.ASCII.GetBytes("q 20 0 0 20 0 0 cm /Im1 Do Q\n");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources 6 0 R";
        string content = "[/Pattern /DeviceRGB] cs /P0 scn 0 0 100 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, patternDict, cellContent),
            new PdfObjectDefinition(6, "<< /XObject << /Im1 7 0 R >> >>"),
            new PdfObjectDefinition(7, $"<< /Type /XObject /Subtype /Image /Width 10 /Height 20 /ColorSpace /DeviceGray /BitsPerComponent 1 /Length {samples.Length} >>", samples));
    }

    /// <summary>
    /// The tiling page again, but the pattern fill is followed by a <c>rg</c> colour and a second solid
    /// rectangle over the left half. A device colour operator replaces the current pattern colour, so the
    /// second rectangle must be flat blue - a tiling selection that survives <c>rg</c> repaints it with the
    /// red/white cell instead, which is how a book's solid chapter header band came out as a dot grid.
    /// </summary>
    public static byte[] CreateTilingThenSolidFillPdf()
    {
        byte[] cellContent = Encoding.ASCII.GetBytes("1 1 1 rg 0 0 20 20 re f\n1 0 0 rg 0 0 10 20 re f\n");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20";
        string content = "[/Pattern /DeviceRGB] cs /P0 scn 0 0 100 100 re f\n0 0 1 rg 0 0 50 100 re f";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, patternDict, cellContent));
    }

    /// <summary>
    /// A 200x100 page whose only mark is a shading-pattern fill inside a window well away from the page
    /// origin, drawn under an ExtGState <c>/SMask</c> whose group is opaque everywhere. The fill lands on
    /// a masked layer whose surface starts at (100,40) rather than (0,0): a shading raster sized from the
    /// clip has to be placed in the device space the layer's own matrix works in, or it is drawn one
    /// layer origin away and the window comes out blank.
    /// </summary>
    public static byte[] CreateOffsetMaskedShadingFillPdf()
    {
        byte[] maskContent = Encoding.ASCII.GetBytes("1 g 0 0 200 100 re f");
        string content = "q 100 40 90 20 re W n /GS1 gs [/Pattern /DeviceRGB] cs /ShP scn 100 40 90 20 re f Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] "
                + "/Resources << /Pattern << /ShP 6 0 R >> /ExtGState << /GS1 8 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Domain [0 1] /Extend [true true] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /E 1 >>"),
            new PdfObjectDefinition(8, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /S /Luminosity /G 9 0 R >> >>"),
            Stream(9, "/Type /XObject /Subtype /Form /BBox [0 0 200 100] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>", maskContent));
    }

    /// <summary>
    /// A 200x100 page whose only mark is a 20pt-wide horizontal band stroked with a Type 2 (axial)
    /// shading pattern: the DeviceGray ramp from black at user x=0 to white at user x=200 that
    /// <see cref="CreateAxialShadingPdf"/> fills with. A pattern selected in the stroking colour space
    /// used to be ignored, so the band kept the default black stroke colour.
    /// </summary>
    public static byte[] CreateShadingStrokePdf()
    {
        string content = "[/Pattern /DeviceRGB] CS /ShP SCN 20 w 10 50 m 190 50 l S";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Pattern << /ShP 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Domain [0 1] /Extend [true true] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /E 1 >>"));
    }

    /// <summary>
    /// The same band stroked with a Type 1 tiling pattern instead of a shading: each 20pt cell is white
    /// with its left half red, so the band alternates red and white along its length.
    /// </summary>
    public static byte[] CreateTilingStrokePdf()
    {
        byte[] cellContent = Encoding.ASCII.GetBytes("1 1 1 rg 0 0 20 20 re f\n1 0 0 rg 0 0 10 20 re f\n");
        string patternDict = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20";
        string content = "[/Pattern /DeviceRGB] CS /P0 SCN 20 w 10 50 m 190 50 l S";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            Stream(5, patternDict, cellContent));
    }

    /// <summary>
    /// The shaded band again, this time under an ExtGState <c>/SMask</c> whose group is opaque
    /// everywhere, with the clip limited to a window well away from the page origin. The stroke therefore
    /// lands on a masked layer whose surface starts at (100,40) rather than (0,0): a shading raster sized
    /// from the clip has to be placed in the device space the layer's own matrix works in, or it lands one
    /// layer origin away and the band comes out blank.
    /// </summary>
    public static byte[] CreateOffsetMaskedShadingStrokePdf()
    {
        byte[] maskContent = Encoding.ASCII.GetBytes("1 g 0 0 200 100 re f");
        string content = "q 100 40 90 20 re W n /GS1 gs [/Pattern /DeviceRGB] CS /ShP SCN 20 w 110 50 m 200 50 l S Q";
        return Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] "
                + "/Resources << /Pattern << /ShP 6 0 R >> /ExtGState << /GS1 8 0 R >> >> /Contents 4 0 R >>"),
            Stream(4, string.Empty, Encoding.ASCII.GetBytes(content)),
            new PdfObjectDefinition(6, "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Domain [0 1] /Extend [true true] /Function 7 0 R >> >>"),
            new PdfObjectDefinition(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /E 1 >>"),
            new PdfObjectDefinition(8, "<< /Type /ExtGState /ca 1 /CA 1 /SMask << /S /Luminosity /G 9 0 R >> >>"),
            Stream(9, "/Type /XObject /Subtype /Form /BBox [0 0 200 100] "
                + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >>", maskContent));
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
