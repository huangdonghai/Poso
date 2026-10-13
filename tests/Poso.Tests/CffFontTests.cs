using Poso.Pdf.Fonts;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Tests for the CFF reader that maps PDF glyph names onto glyph IDs of an embedded
/// Type1/Type1C program (the mapping a bare CFF needs before any back end can use it).
/// </summary>
public class CffFontTests
{
    [Fact]
    public void StandardStrings_MatchTheCffSpecification()
    {
        Assert.Equal(391, CffStandardStrings.Count);
        Assert.Equal(CffStandardStrings.Count, CffStandardStrings.Names.Length);
        Assert.Equal(".notdef", CffStandardStrings.Names[0]);
        Assert.Equal("space", CffStandardStrings.Names[1]);
        Assert.Equal("quotedbl", CffStandardStrings.Names[3]);
        Assert.Equal("a", CffStandardStrings.Names[66]);
        Assert.Equal("quotedblright", CffStandardStrings.Names[119]);
        Assert.Equal("Semibold", CffStandardStrings.Names[390]);
    }

    [Fact]
    public void TryCreate_ReadsCharsetNamesAndGlyphCount()
    {
        byte[] cff = CffFixture.Build();

        Assert.True(CffProgram.TryCreate(cff, out CffProgram? program));
        Assert.NotNull(program);

        // .notdef, space and 'a' as declared by the format 0 charset
        Assert.Equal(3, program!.GlyphCount);
        Assert.True(program.TryGetGlyphId("space", out ushort space));
        Assert.Equal(1, space);
        Assert.True(program.TryGetGlyphId("a", out ushort a));
        Assert.Equal(2, a);
        Assert.False(program.TryGetGlyphId("hyphen", out _));
        Assert.Equal("a", program.GetName(66));
    }

    [Fact]
    public void TryCreate_ReadsFontSpecificStringsAboveTheStandardRange()
    {
        byte[] cff = CffFixture.Build(customName: "g42");

        Assert.True(CffProgram.TryCreate(cff, out CffProgram? program));
        Assert.NotNull(program);
        Assert.True(program!.TryGetGlyphId("g42", out ushort glyphId));
        Assert.Equal(2, glyphId);
    }

    [Fact]
    public void TryCreate_RejectsCidKeyedEncryptedAndForeignData()
    {
        Assert.False(CffProgram.TryCreate(CffFixture.Build(cidKeyed: true), out _));
        Assert.False(CffProgram.TryCreate(CffFixture.Build(encrypted: true), out _));
        Assert.False(CffProgram.TryCreate([1, 2, 3, 4, 5], out _));
        Assert.False(CffProgram.TryCreate(new byte[9000], out _));
    }

    [Fact]
    public void TryGetGlyphIdForCode_UsesTheBuiltInEncoding()
    {
        byte[] cff = CffFixture.Build(withEncoding: true);

        Assert.True(CffProgram.TryCreate(cff, out CffProgram? program));
        Assert.NotNull(program);

        // The fixture encoding lists codes 32 and 97 for glyphs 1 and 2
        Assert.True(program!.TryGetGlyphIdForCode(32, out ushort space));
        Assert.Equal(1, space);
        Assert.True(program.TryGetGlyphIdForCode(97, out ushort a));
        Assert.Equal(2, a);
        Assert.False(program.TryGetGlyphIdForCode(65, out _));
        Assert.False(program.TryGetGlyphIdForCode(-1, out _));
    }
}

/// <summary>Assembles a tiny name-keyed CFF so the reader can be tested without a real font file.</summary>
internal static class CffFixture
{
    public static byte[] Build(bool withEncoding = false, bool cidKeyed = false, bool encrypted = false, string customName = "a")
    {
        // Glyph names: gid 1 is the standard string "space", gid 2 is either "a" (SID 66) or a
        // font specific string placed beyond the standard range
        int gid2Sid = customName == "a" ? 66 : CffStandardStrings.Count;

        var strings = new List<string>();
        if (customName != "a")
        {
            strings.Add(customName);
        }

        byte[] nameIndex = Index([System.Text.Encoding.ASCII.GetBytes("Test")]);
        byte[] stringIndex = Index([.. strings.Select(name => System.Text.Encoding.ASCII.GetBytes(name))]);
        byte[] globalIndex = Index([]);
        byte[] encoding = withEncoding ? [0, 2, 32, 97] : [];
        byte[] charset = [0, (byte)(1 >> 8), (byte)1, (byte)(gid2Sid >> 8), (byte)gid2Sid];
        byte[] charStrings = Index([[139, 14], [139, 14], [239, 239, 21, 139, 247, 92, 247, 92, 139, 139, 251, 92, 5, 14]]);
        byte[] privateDict = [247, 92, 20];

        // Offsets are absolute from the start of the program. Fixed() keeps every offset operand
        // five bytes wide, so measuring the probe dict and filling it in later cannot disagree.
        int afterHeader = 4 + nameIndex.Length;
        byte[] probe = TopDict(withEncoding, cidKeyed, encrypted, 0, 0, 0, privateDict.Length, 0);
        int afterTopDict = afterHeader + probe.Length + 5; // Index() of a single item adds five bytes
        int afterGlobals = afterTopDict + stringIndex.Length + globalIndex.Length;
        int encodingOffset = withEncoding ? afterGlobals : 0;
        int charsetOffset = afterGlobals + encoding.Length;
        int charStringsOffset = charsetOffset + charset.Length;
        int privateOffset = charStringsOffset + charStrings.Length;

        byte[] topDict = TopDict(withEncoding, cidKeyed, encrypted, encodingOffset, charsetOffset, charStringsOffset, privateDict.Length, privateOffset);
        if (topDict.Length != probe.Length)
        {
            throw new InvalidOperationException("the fixture top dict changed length between passes");
        }

        var data = new List<byte> { 1, 0, 4, 4 };
        data.AddRange(nameIndex);
        data.AddRange(Index([topDict]));
        data.AddRange(stringIndex);
        data.AddRange(globalIndex);
        data.AddRange(encoding);
        data.AddRange(charset);
        data.AddRange(charStrings);
        data.AddRange(privateDict);
        return data.ToArray();
    }

    private static byte[] TopDict(bool withEncoding, bool cidKeyed, bool encrypted, int encodingOffset, int charsetOffset, int charStringsOffset, int privateLength, int privateOffset)
    {
        var dict = new List<byte>();
        dict.AddRange(Numbers([0, 0, 500, 700]));
        dict.Add(5); // FontBBox

        if (withEncoding)
        {
            dict.AddRange(Fixed(encodingOffset));
            dict.Add(16); // Encoding
        }

        if (cidKeyed)
        {
            dict.AddRange(Fixed(4000));
            dict.AddRange(Fixed(4001));
            dict.AddRange(Numbers([0]));
            dict.AddRange([12, 30]); // ROS
        }

        if (encrypted)
        {
            dict.AddRange(Numbers([42]));
            dict.Add(14); // Encrypt
        }

        dict.AddRange(Fixed(charsetOffset));
        dict.Add(15); // charset

        dict.AddRange(Fixed(charStringsOffset));
        dict.Add(17); // CharStrings

        dict.AddRange(Numbers([privateLength]));
        dict.AddRange(Fixed(privateOffset));
        dict.Add(18); // Private

        return dict.ToArray();
    }

    private static List<byte> Numbers(int[] values)
    {
        var bytes = new List<byte>();
        foreach (int value in values)
        {
            if (value is >= -107 and <= 107)
            {
                bytes.Add((byte)(value + 139));
            }
            else if (value is > 107 and <= 1131)
            {
                bytes.Add((byte)(247 + ((value - 108) >> 8)));
                bytes.Add((byte)((value - 108) & 0xFF));
            }
            else
            {
                bytes.Add((byte)(251 + ((-value - 108) >> 8)));
                bytes.Add((byte)((-value - 108) & 0xFF));
            }
        }

        return bytes;
    }

    /// <summary>Always the five byte form, so an operand's width never depends on its value.</summary>
    private static List<byte> Fixed(int value)
    {
        if (value < 0)
        {
            return [28, (byte)(value >> 8), (byte)value];
        }

        return [29, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] Index(IReadOnlyList<byte[]> items)
    {
        var bytes = new List<byte> { (byte)(items.Count >> 8), (byte)items.Count };
        if (items.Count == 0)
        {
            return bytes.ToArray();
        }

        bytes.Add(1); // offSize
        int position = 1;
        foreach (byte[] item in items)
        {
            bytes.Add((byte)position);
            position += item.Length;
        }

        bytes.Add((byte)position);
        foreach (byte[] item in items)
        {
            bytes.AddRange(item);
        }

        return bytes.ToArray();
    }
}
