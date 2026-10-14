using System.Collections.Generic;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Pdf.Objects;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M5: CID font glyph mapping. /CIDToGIDMap (CIDFontType2) maps a CID to the embedded font's glyph
/// ID; /Identity (and CIDFontType0) fall back to the CID itself.
/// </summary>
public class FontFidelityTests
{
    [Fact]
    public void CidToGidMap_NonIdentity_MapsCidToGlyphId()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());

        byte[] map = { 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x12, 0x34 };
        var cidToGid = new PdfStream(new PdfDictionary(), map);

        Type0PdfFont font = BuildType0(document, new PdfName("CIDFontType2"), cidToGid);

        Assert.Equal((ushort)5, font.GetGlyphId(0));
        Assert.Equal((ushort)6, font.GetGlyphId(1));
        Assert.Equal((ushort)7, font.GetGlyphId(2));
        Assert.Equal((ushort)0x1234, font.GetGlyphId(3));

        // Beyond the map: fall back to the CID itself.
        Assert.Equal((ushort)99, font.GetGlyphId(99));
    }

    [Fact]
    public void CidToGidMap_Identity_MapsCidToItself()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());

        Type0PdfFont font = BuildType0(document, new PdfName("CIDFontType2"), new PdfName("Identity"));

        Assert.Equal((ushort)42, font.GetGlyphId(42));
    }

    [Fact]
    public void CidFontType0_IgnoresCidToGidMap_AndUsesIdentity()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());

        // A CID-to-GID map is meaningless for CIDFontType0 (CFF): the CID must map to itself.
        byte[] map = { 0x00, 0x05 };
        Type0PdfFont font = BuildType0(document, new PdfName("CIDFontType0"), new PdfStream(new PdfDictionary(), map));

        Assert.Equal((ushort)0, font.GetGlyphId(0));
        Assert.Equal((ushort)5, font.GetGlyphId(5));
    }

    private static Type0PdfFont BuildType0(PdfDocument document, PdfName descendantSubtype, PdfObject cidToGidMap)
    {
        var descendant = new PdfDictionary();
        descendant["Type"] = new PdfName("Font");
        descendant["Subtype"] = descendantSubtype;
        descendant["BaseFont"] = new PdfName("TestCid");
        descendant["CIDToGIDMap"] = cidToGidMap;

        var type0 = new PdfDictionary();
        type0["Type"] = new PdfName("Font");
        type0["Subtype"] = new PdfName("Type0");
        type0["BaseFont"] = new PdfName("TestCid");
        type0["Encoding"] = new PdfName("Identity-H");
        type0["DescendantFonts"] = new PdfArray(new PdfObject[] { descendant });

        return new Type0PdfFont(document, type0, "TestCid");
    }
}
