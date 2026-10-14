using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Serializes a parsed <see cref="Type1Program"/> (whose charstrings are already Type 2) into a bare
/// CFF font binary. The result is fed back through <see cref="CffProgram.TryCreate"/> and
/// <see cref="CffOpenType.Build"/>, so a native Type 1 (FontFile) program renders through exactly the
/// same Skia path as a Type1C/CFF font - no separate outline renderer is needed.
/// </summary>
internal static class Type1ToCff
{
    public static byte[] Build(IType1Program program, string fontName, bool prependNotdef = true)
    {
        string name = SanitizeName(fontName);

        // Assign a string ID to each glyph name: standard SIDs stay implicit, the rest go in the String INDEX.
        var customStrings = new List<string>();
        var sidByName = new Dictionary<string, int>(System.StringComparer.Ordinal);
        int SidFor(string glyph)
        {
            if (sidByName.TryGetValue(glyph, out int sid))
            {
                return sid;
            }

            int standard = System.Array.IndexOf(CffStandardStrings.Names, glyph);
            if (standard >= 0)
            {
                sidByName[glyph] = standard;
                return standard;
            }

            sid = CffStandardStrings.Count + customStrings.Count;
            customStrings.Add(glyph);
            sidByName[glyph] = sid;
            return sid;
        }

        var glyphs = program.Glyphs;
        // Name-keyed programs have no leading .notdef, so a synthetic one is prepended at glyph 0.
        // CID programs already carry cid0/.notdef at index 0, which must map to GID 0 so CID == GID
        // under the Type 0 / Identity mapping.
        int glyphCount = prependNotdef ? glyphs.Count + 1 : glyphs.Count;

        var charstrings = new List<byte[]>(glyphCount);
        if (prependNotdef)
        {
            charstrings.Add(new byte[] { 0x8b, 0x0e }); // synthetic .notdef ("0 endchar")
        }
        foreach (Type1Glyph g in glyphs)
        {
            charstrings.Add(g.Charstring);
        }

        // charset format 0: the SID of every glyph after .notdef (glyph 0 is implicit).
        var charset = new List<byte> { 0 }; // format 0
        for (int i = prependNotdef ? 0 : 1; i < glyphs.Count; i++)
        {
            int sid = SidFor(glyphs[i].Name);
            charset.Add((byte)(sid >> 8));
            charset.Add((byte)(sid & 0xff));
        }

        byte[] nameIndex = BuildIndex(new List<byte[]> { Encoding.ASCII.GetBytes(name) });
        byte[] stringIndex = BuildIndex(customStrings.ConvertAll(s => Encoding.ASCII.GetBytes(s)));
        byte[] globalSubrIndex = BuildIndex(new List<byte[]>());
        byte[] charStringsIndex = BuildIndex(charstrings);
        byte[] charsetBytes = charset.ToArray();
        byte[] privateDict = [139, 20, 139, 21]; // defaultWidthX 0, nominalWidthX 0

        // The top dict carries absolute offsets into the layout below it; its own size feeds back into
        // those offsets, so iterate until the leading length stabilises (converges in a couple of passes).
        int fixedBeforeTop = 4 + nameIndex.Length;
        int afterTop = stringIndex.Length + globalSubrIndex.Length;
        int topLen = 64;
        byte[] topDict;
        while (true)
        {
            int charstringsOff = fixedBeforeTop + topLen + afterTop;
            int charsetOff = charstringsOff + charStringsIndex.Length;
            int privateOff = charsetOff + charsetBytes.Length;

            topDict = BuildTopDict(program, charstringsOff, charsetOff, privateDict.Length, privateOff);
            int newTopLen = BuildIndex([topDict]).Length;
            if (newTopLen == topLen)
            {
                topLen = newTopLen;
                break;
            }

            topLen = newTopLen;
        }

        int csOff = fixedBeforeTop + topLen + afterTop;
        int chOff = csOff + charStringsIndex.Length;
        int prOff = chOff + charsetBytes.Length;

        using var ms = new MemoryStream();
        ms.WriteByte(1); // major
        ms.WriteByte(0); // minor
        ms.WriteByte(4); // header size
        ms.WriteByte(4); // first offset size
        ms.Write(nameIndex);
        ms.Write(BuildIndex([topDict]));
        ms.Write(stringIndex);
        ms.Write(globalSubrIndex);
        ms.Write(charStringsIndex);
        ms.Write(charsetBytes);
        ms.Write(privateDict);
        _ = csOff;
        _ = chOff;
        _ = prOff;
        return ms.ToArray();
    }

    private static byte[] BuildTopDict(IType1Program program, int charstringsOff, int charsetOff, int privateLen, int privateOff)
    {
        var d = new List<byte>();

        // FontBBox (5)
        foreach (float v in program.FontBBox)
        {
            WriteNumber(d, v);
        }

        d.Add(5);

        // FontMatrix (7)
        foreach (float v in program.FontMatrix)
        {
            WriteNumber(d, v);
        }

        d.Add(7);

        // charset (15), CharStrings (17), Private (18: length then offset)
        WriteInt(d, charsetOff);
        d.Add(15);
        WriteInt(d, charstringsOff);
        d.Add(17);
        WriteInt(d, privateLen);
        WriteInt(d, privateOff);
        d.Add(18);

        return d.ToArray();
    }

    private static byte[] BuildIndex(List<byte[]> items)
    {
        using var ms = new MemoryStream();
        int count = items.Count;
        ms.WriteByte((byte)(count >> 8));
        ms.WriteByte((byte)(count & 0xff));
        if (count == 0)
        {
            return ms.ToArray();
        }

        int dataLen = 0;
        foreach (byte[] item in items)
        {
            dataLen += item.Length;
        }

        int maxOffset = dataLen + 1;
        int offSize = maxOffset <= 0xff ? 1 : maxOffset <= 0xffff ? 2 : maxOffset <= 0xffffff ? 3 : 4;
        ms.WriteByte((byte)offSize);

        int offset = 1;
        foreach (byte[] item in items)
        {
            WriteBigEndian(ms, offset, offSize);
            offset += item.Length;
        }

        WriteBigEndian(ms, offset, offSize);
        foreach (byte[] item in items)
        {
            ms.Write(item);
        }

        return ms.ToArray();
    }

    private static void WriteBigEndian(Stream s, int value, int size)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            s.WriteByte((byte)((value >> (i * 8)) & 0xff));
        }
    }

    private static void WriteInt(List<byte> d, int value)
    {
        if (value >= -107 && value <= 107)
        {
            d.Add((byte)(value + 139));
        }
        else if (value >= 108 && value <= 1131)
        {
            int v = value - 108;
            d.Add((byte)(247 + (v >> 8)));
            d.Add((byte)(v & 0xff));
        }
        else if (value >= -1131 && value <= -108)
        {
            int v = -value - 108;
            d.Add((byte)(251 + (v >> 8)));
            d.Add((byte)(v & 0xff));
        }
        else if (value >= short.MinValue && value <= short.MaxValue)
        {
            d.Add(28);
            d.Add((byte)((value >> 8) & 0xff));
            d.Add((byte)(value & 0xff));
        }
        else
        {
            d.Add(29);
            d.Add((byte)((value >> 24) & 0xff));
            d.Add((byte)((value >> 16) & 0xff));
            d.Add((byte)((value >> 8) & 0xff));
            d.Add((byte)(value & 0xff));
        }
    }

    private static void WriteNumber(List<byte> d, double value)
    {
        if (value == System.Math.Floor(value) && value >= int.MinValue && value <= int.MaxValue)
        {
            WriteInt(d, (int)value);
            return;
        }

        WriteReal(d, value);
    }

    private static void WriteReal(List<byte> d, double value)
    {
        // CFF bcd real (operator 30): two 4-bit nibbles per byte, digits 0-9, 0xA = '.', 0xB = exponent
        // marker, 0xC = exponent-minus / leading negative sign, 0xF = end-of-number. A leading 0xC marks
        // a negative number; positives begin directly with their first digit. The number MUST end with a
        // 0xF terminator, and the total nibble count (terminator included) must be even - pad with 0xF.
        string text = value.ToString("0.############", CultureInfo.InvariantCulture);
        if (text.Contains('E') || text.Contains('e'))
        {
            text = value.ToString("F12", CultureInfo.InvariantCulture); // avoid exponent form for the bcd reader
        }

        text = text.TrimEnd('0').TrimEnd('.');
        if (text.Length == 0 || text == "-")
        {
            text = "0";
        }

        var nibbles = new List<int>();
        if (text.StartsWith("-", StringComparison.Ordinal))
        {
            nibbles.Add(0xc);
            text = text[1..];
        }

        foreach (char c in text)
        {
            switch (c)
            {
                case '.':
                    nibbles.Add(0xa);
                    break;
                case >= '0' and <= '9':
                    nibbles.Add(c - '0');
                    break;
            }
        }

        nibbles.Add(0xf); // end-of-number terminator
        if (nibbles.Count % 2 != 0)
        {
            nibbles.Add(0xf); // pad the final byte
        }

        d.Add(30);
        for (int i = 0; i < nibbles.Count; i += 2)
        {
            d.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "PosoType1";
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            // PostScript names exclude whitespace, delimiters and the parenthesis/bracket group.
            if (!char.IsWhiteSpace(c) && c is not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%'))
            {
                sb.Append(c);
            }
        }

        string cleaned = sb.ToString();
        if (cleaned.Length == 0)
        {
            cleaned = "PosoType1";
        }

        return cleaned.Length > 127 ? cleaned[..127] : cleaned;
    }
}
