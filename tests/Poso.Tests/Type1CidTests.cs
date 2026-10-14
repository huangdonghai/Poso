using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Bit-level regression for the CID-keyed binary Type 1 parser (<see cref="Type1CidProgram"/>). There is
/// no real end-to-end CID-Type1 PDF in the corpus (pdf.js only exercises it with handcrafted byte streams),
/// so the oracle is pdf.js's own <c>type1_parser_spec.js</c> vectors, reproduced here byte-for-byte with the
/// same expected outcomes. The charstring->Type 2 conversion these feed is already MuPDF-validated by the
/// name-keyed <see cref="NativeType1Tests"/>.
/// </summary>
public class Type1CidTests
{
    // "0 500 hsbw endchar"
    private static readonly byte[] Notdef = [0x8b, 0xf8, 0x88, 0x0d, 0x0e];
    // "0 250 hsbw endchar"
    private static readonly byte[] Cid2 = [0x8b, 0xf7, 0x8e, 0x0d, 0x0e];

    [Fact]
    public void ParsesBasicCidKeyedProgram()
    {
        // CIDMap: CID 0 has data, CID 1 is empty, CID 2 has data.
        byte[] binary = [4, 9, 9, 14, .. Notdef, .. Cid2];
        byte[] data = Stream(binary, cidCount: 3);

        Assert.True(Type1CidProgram.TryCreate(data, out Type1CidProgram? program) && program is not null);
        IReadOnlyList<Type1Glyph> glyphs = program!.Glyphs;
        Assert.Equal(3, glyphs.Count);
        Assert.Equal([".notdef", "cid1", "cid2"], glyphs.Select(g => g.Name));
        Assert.Equal(500, glyphs[0].Width);
        Assert.Equal(500, glyphs[1].Width); // empty interval reuses the notdef glyph
        Assert.Equal(250, glyphs[2].Width);
        Assert.Equal(glyphs[0].Charstring, glyphs[1].Charstring);
    }

    [Fact]
    public void ParsesHexEncodedDataSection()
    {
        byte[] binary = [4, 9, 9, 14, .. Notdef, .. Cid2];
        byte[] data = Stream(binary, cidCount: 3, dataFormat: "Hex", hex: Hex(binary));

        Assert.True(Type1CidProgram.TryCreate(data, out Type1CidProgram? program) && program is not null);
        Assert.Equal(250, program!.Glyphs[2].Width);
    }

    [Fact]
    public void DecodesHexDataWithWhitespace()
    {
        byte[] binary = [4, 9, 9, 14, .. Notdef];
        string hexWithSpaces = string.Join(" ", Chunk(Hex(binary), 2));
        byte[] data = Stream(binary, cidCount: 1, dataFormat: "Hex", hex: hexWithSpaces);

        Assert.True(Type1CidProgram.TryCreate(data, out Type1CidProgram? program) && program is not null);
        Assert.Equal(500, program!.Glyphs[0].Width);
    }

    [Fact]
    public void RejectsMultipleFdIndices()
    {
        // CIDMap: CID 0 selects FD index 1, which is unsupported.
        byte[] binary = [1, 4, 0, 9, .. Notdef];
        byte[] data = Stream(binary, cidCount: 1, fdBytes: 1);

        Assert.False(Type1CidProgram.TryCreate(data, out _));
    }

    [Fact]
    public void UsesSubrsForWidth()
    {
        // "0 333 hsbw return" - a callable subroutine.
        byte[] subr0 = [0x8b, 0xf7, 0xe1, 0x0d, 0x0b];
        // "0 callsubr endchar" - delegates the width to subr 0.
        byte[] cid1 = [0x8b, 0x0a, 0x0e];
        int subrStart = 5;
        int cid0Start = subrStart + subr0.Length;
        int cid1Start = cid0Start + Notdef.Length;
        byte[] binary = [(byte)cid0Start, (byte)cid1Start, (byte)(cid1Start + cid1.Length), (byte)subrStart, (byte)cid0Start, .. subr0, .. Notdef, .. cid1];
        byte[] data = Stream(binary, cidCount: 2, subrMapOffset: 3, subrCount: 1, sdBytes: 1);

        Assert.True(Type1CidProgram.TryCreate(data, out Type1CidProgram? program) && program is not null);
        Assert.Equal(500, program!.Glyphs[0].Width);
        Assert.Equal(333, program.Glyphs[1].Width);
    }

    [Fact]
    public void DecryptsCharstringsWhenLenIvPositive()
    {
        byte[] cipher = EncryptCharString(Notdef, 4);
        byte[] binary = [2, (byte)(2 + cipher.Length), .. cipher];
        byte[] data = Stream(binary, cidCount: 1, lenIV: 4);

        Assert.True(Type1CidProgram.TryCreate(data, out Type1CidProgram? program) && program is not null);
        Assert.Equal(500, program!.Glyphs[0].Width);
    }

    [Fact]
    public void RejectsTruncatedCidMap()
    {
        byte[] data = Stream([0, 0], cidCount: 3);
        Assert.False(Type1CidProgram.TryCreate(data, out _));
    }

    [Theory]
    [InlineData("Binary 4 StartData ")]            // missing the "(Binary)"/"(Hex)" parenthesised tag
    [InlineData("(Binary) abc StartData ")]        // non-numeric length
    [InlineData("(Ascii) 4 StartData ")]           // unsupported data type
    [InlineData("(Binary) 0 StartData ")]          // zero length
    public void RejectsMalformedStartData(string tail)
    {
        const string header = "%!PS-Adobe-3.0 Resource-CIDFont\n/CIDMapOffset 0 def /FDBytes 0 def /GDBytes 1 def\n"
            + "/CIDCount 1 def\n/Private 5 dict dup begin /lenIV -1 def end def\n";
        byte[] data = Concat(header + tail, [0, 0, 0, 0]);
        Assert.False(Type1CidProgram.TryCreate(data, out _));
    }

    [Fact]
    public void RejectsOversizedHexLength()
    {
        const string header = "%!PS-Adobe-3.0 Resource-CIDFont\n/CIDMapOffset 0 def /FDBytes 0 def /GDBytes 1 def\n"
            + "/CIDCount 1 def\n/Private 5 dict dup begin /lenIV -1 def end def\n";
        byte[] data = Concat(header + "(Hex) 1073741824 StartData ", Encoding.ASCII.GetBytes("00"));
        Assert.False(Type1CidProgram.TryCreate(data, out _));
    }

    [Fact]
    public void CidConversionMapsGidToCid()
    {
        byte[] binary = [4, 9, 9, 14, .. Notdef, .. Cid2];
        Assert.True(Type1CidProgram.TryCreate(Stream(binary, cidCount: 3), out Type1CidProgram? program) && program is not null);

        byte[] cff = Type1ToCff.Build(program!, "TestCid", prependNotdef: false);
        Assert.True(CffProgram.TryCreate(cff, out CffProgram? cffProg) && cffProg is not null);
        Assert.Equal(3, cffProg!.GlyphCount);
        Assert.True(cffProg.TryGetGlyphId("cid2", out ushort gid2));
        Assert.Equal(2, gid2); // GID == CID under the Identity mapping
        Assert.True(cffProg.TryGetGlyphId("cid1", out ushort gid1));
        Assert.Equal(1, gid1);
    }

    /// <summary>
    /// End-to-end wiring check: a handcrafted CID-keyed Type 1 program behind a Type0 / CIDFontType0
    /// descendant renders through the full decode -> CID -> GID -> Type1->CFF -> Skia pipeline. Each CID
    /// glyph is a filled 40x50 pt rectangle, so exactly two boxes (4000 px) appear, separated by the
    /// /W advance, with the gap between them and the page margins left white.
    /// NOTE: MuPDF/FreeType reject this simplified (pdf.js-style) CID-Type1 structure and render nothing,
    /// so this is a deterministic self-consistency assertion; the bit-level PARSE is pinned by the pdf.js
    /// vectors above.
    /// </summary>
    [Fact]
    public void CidKeyedType1_RendersTwoGlyphsEndToEnd()
    {
        byte[] pdf = PdfFixtures.CreateCidKeyedType1Pdf();
        using PdfDocument document = PdfDocument.FromArray(pdf);
        IGraphicsFactory factory = SkiaGraphicsFactory.Default;
        using IGraphicsContext context = factory.CreateContext(200, 120);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        int w = image.Width, h = image.Height;
        byte[] px = new byte[w * h * 4];
        image.CopyPixels(px);

        int ink = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Dark(px, w, x, y))
                {
                    ink++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        // Two 40x50 px filled rectangles = 4000 ink pixels (edges are pixel-aligned at scale 1).
        Assert.InRange(ink, 3800, 4200);
        Assert.InRange(minX, 18, 22);
        Assert.InRange(maxX, 117, 121);
        Assert.InRange(minY, 8, 12);
        Assert.InRange(maxY, 57, 61);
        Assert.True(Dark(px, w, 40, 35), "first CID glyph (cid1) must be inked");
        Assert.True(Dark(px, w, 100, 35), "second CID glyph (cid2) must be inked after the /W advance");
        Assert.False(Dark(px, w, 70, 35), "the gap between the two glyphs must be white");
        Assert.False(Dark(px, w, 150, 90), "page margin must stay white");
    }

    private static bool Dark(byte[] px, int width, int x, int y)
    {
        int o = ((y * width) + x) * 4;
        return px[o] < 200 || px[o + 1] < 200 || px[o + 2] < 200;
    }

    // ---- helpers mirroring pdf.js createCidKeyedFontStream / encryptCharString ----

    private static byte[] Stream(
        byte[] binary,
        int cidCount,
        int fdBytes = 0,
        int gdBytes = 1,
        int subrMapOffset = 0,
        int sdBytes = 0,
        int subrCount = 0,
        int lenIV = -1,
        string dataFormat = "Binary",
        string? hex = null)
    {
        string header =
            "%!PS-Adobe-3.0 Resource-CIDFont\n" +
            "/CIDMapOffset 0 def\n" +
            $"/FDBytes {fdBytes} def\n" +
            $"/GDBytes {gdBytes} def\n" +
            $"/CIDCount {cidCount} def\n" +
            $"/SubrMapOffset {subrMapOffset} def\n" +
            $"/SDBytes {sdBytes} def\n" +
            $"/SubrCount {subrCount} def\n" +
            "/Private 5 dict dup begin\n" +
            $"/lenIV {lenIV} def\n" +
            "end def\n" +
            $"({dataFormat}) {binary.Length} StartData ";

        return dataFormat == "Hex"
            ? Concat(header, Encoding.ASCII.GetBytes(hex ?? Hex(binary)))
            : Concat(header, binary);
    }

    private static byte[] Concat(string prefix, byte[] tail)
    {
        byte[] head = Encoding.ASCII.GetBytes(prefix);
        var result = new byte[head.Length + tail.Length];
        head.CopyTo(result, 0);
        tail.CopyTo(result, head.Length);
        return result;
    }

    // Inverse of the Type 1 charstring cipher (mirrors pdf.js encryptCharString): the leading lenIV
    // plaintext bytes are zero padding, matching the discard the decrypter skips.
    private static byte[] EncryptCharString(byte[] plain, int lenIV)
    {
        const int c1 = 52845;
        const int c2 = 22719;
        int r = 4330;
        var outBytes = new byte[plain.Length + lenIV];
        for (int i = 0; i < outBytes.Length; i++)
        {
            int src = i < lenIV ? 0 : plain[i - lenIV];
            int cipher = (src ^ (r >> 8)) & 0xff;
            outBytes[i] = (byte)cipher;
            r = ((cipher + r) * c1 + c2) & 0xffff;
        }

        return outBytes;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (int i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }
}
