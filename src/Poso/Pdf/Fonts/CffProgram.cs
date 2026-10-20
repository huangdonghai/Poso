namespace Poso.Pdf.Fonts;

/// <summary>The subset of a CFF program the OpenType wrapper needs: glyph count and design-space metrics.
/// Implemented by both the name-keyed <see cref="CffProgram"/> and the CID-keyed <see cref="CidCffProgram"/>.</summary>
internal interface ICffMetrics
{
    int GlyphCount { get; }
    float[] FontMatrix { get; }
    float[] FontBBox { get; }
}

/// <summary>
/// Reader for a name-keyed CFF program (PDF <c>FontFile</c> Type1, or <c>FontFile3</c> with
/// /Subtype /Type1C or /CFF). It parses the glyph count, the design space and the charset, which
/// maps glyph IDs to PostScript glyph names.
/// </summary>
/// <remarks>
/// PDF addresses the glyphs of a simple font by name (from /Encoding plus /Differences) while a CFF
/// numbers its glyphs and names them through string IDs. The charset plus
/// <see cref="CffStandardStrings"/> is the bridge between the two, and it is the only dependable way
/// to pick the right outline out of an embedded subset font.
/// </remarks>
public sealed class CffProgram : ICffMetrics
{
    /// <summary>CFF FontMatrix when the program omits the operator (the spec default scales by 1/1000).</summary>
    private static readonly float[] DefaultFontMatrix = [0.001f, 0f, 0f, 0f, 0.001f, 0f];

    private readonly Dictionary<string, ushort> _nameToGlyph;
    private readonly CffIndex _charStrings;
    private readonly byte[] _program;

    private CffProgram(byte[] program, CffIndex charStrings, int glyphCount, float[] fontMatrix, float[] fontBBox, string[] strings, int[] charsetSids, int[] codeToGlyph)
    {
        _program = program;
        _charStrings = charStrings;
        GlyphCount = glyphCount;
        FontMatrix = fontMatrix;
        FontBBox = fontBBox;
        Strings = strings;
        CodeToGlyph = codeToGlyph;

        _nameToGlyph = new Dictionary<string, ushort>(glyphCount, StringComparer.Ordinal);
        for (int gid = 0; gid < charsetSids.Length; gid++)
        {
            string? name = GetName(charsetSids[gid]);
            if (!string.IsNullOrEmpty(name))
            {
                // The first glyph carrying a name wins: a subset can list the same name twice
                _nameToGlyph.TryAdd(name!, (ushort)gid);
            }
        }
    }

    /// <summary>Number of glyphs, i.e. the size of the CharStrings INDEX.</summary>
    public int GlyphCount { get; }

    /// <summary>CFF FontMatrix as [xx, xy, yx, yy, dx, dy] in design space units.</summary>
    public float[] FontMatrix { get; }

    /// <summary>CFF FontBBox as [llx, lly, urx, ury] in design space units.</summary>
    public float[] FontBBox { get; }

    /// <summary>Font specific strings, i.e. the strings addressed by SIDs at or above <see cref="CffStandardStrings.Count"/>.</summary>
    public string[] Strings { get; }

    /// <summary>The program's built-in encoding: character code to glyph index, or -1 when the code is undefined.</summary>
    public int[] CodeToGlyph { get; }

    /// <summary>Maps a PostScript glyph name to the glyph ID within this program.</summary>
    public bool TryGetGlyphId(string name, out ushort glyphId) => _nameToGlyph.TryGetValue(name, out glyphId);

    /// <summary>
    /// A glyph built by the Type 1 <c>seac</c> form: a base glyph and an accent glyph placed at an
    /// offset, both addressed through the standard encoding. A CFF charstring that ends in a five
    /// operand <c>endchar</c> carries exactly that - it draws no outline of its own - and a font engine
    /// that does not expand it paints nothing. Also known as a composite or accented glyph.
    /// </summary>
    public readonly record struct SeacGlyph(ushort BaseGlyph, ushort AccentGlyph, int AccentX, int AccentY);

    /// <summary>
    /// Reads the <c>seac</c> form out of the charstring of <paramref name="glyphId"/>. Returns false for
    /// an ordinary glyph, for a program this reader cannot address, and for a composite whose base or
    /// accent glyph is not in the program.
    /// </summary>
    public bool TryGetSeacGlyph(int glyphId, out SeacGlyph seac)
    {
        seac = default;
        if (glyphId < 0 || glyphId >= _charStrings.Count || !TryReadSeacOperands(_charStrings, glyphId, out int[]? operands) ||
            operands is not { Length: 5 })
        {
            return false;
        }

        // asb adx ady bchar achar endchar. bchar and achar are standard-encoding codes, so they have to
        // be turned into glyph ids through the charset rather than used directly.
        if (!TryGetGlyphIdForStandardCode(operands[3], out ushort baseGlyph) ||
            !TryGetGlyphIdForStandardCode(operands[4], out ushort accentGlyph))
        {
            return false;
        }

        seac = new SeacGlyph(baseGlyph, accentGlyph, operands[1], operands[2]);
        return true;
    }

    /// <summary>
    /// A standard-encoding code as a glyph id: the code names a standard string, the charset says which
    /// glyph carries that name.
    /// </summary>
    private bool TryGetGlyphIdForStandardCode(int code, out ushort glyphId)
    {
        glyphId = 0;
        string? name = StandardEncodingName(code);
        return name is not null && TryGetGlyphId(name, out glyphId);
    }

    private static string? StandardEncodingName(int code) =>
        code >= 0 && code < PdfEncodings.Standard.Length ? PdfEncodings.Standard[code] : null;

    /// <summary>
    /// The operands of a trailing <c>endchar</c>, when it is the five-operand <c>seac</c> form. The
    /// charstring is walked with the Type 2 grammar so that a hintmask's mask bytes are stepped over
    /// rather than mistaken for numbers.
    /// </summary>
    private bool TryReadSeacOperands(CffIndex charStrings, int glyphId, out int[]? operands)
    {
        operands = null;
        byte[] charString = charStrings.ReadItem(_program, glyphId);
        var stack = new List<float>(8);
        int[]? lastEndChar = null;

        for (int i = 0; i < charString.Length;)
        {
            byte b = charString[i];
            if (b == 28)
            {
                if (i + 2 >= charString.Length)
                {
                    return false;
                }

                stack.Add((short)((charString[i + 1] << 8) | charString[i + 2]));
                i += 3;
                continue;
            }

            if (b == 255)
            {
                if (i + 4 >= charString.Length)
                {
                    return false;
                }

                stack.Add((int)(uint)(charString[i + 1] << 24 | charString[i + 2] << 16 | charString[i + 3] << 8 | charString[i + 4]) / 65536f);
                i += 5;
                continue;
            }

            if (b is >= 32 and <= 246)
            {
                stack.Add(b - 139);
                i++;
                continue;
            }

            if (b is >= 247 and <= 250)
            {
                if (i + 1 >= charString.Length)
                {
                    return false;
                }

                stack.Add(((b - 247) * 256) + charString[i + 1] + 108);
                i += 2;
                continue;
            }

            if (b is >= 251 and <= 254)
            {
                if (i + 1 >= charString.Length)
                {
                    return false;
                }

                stack.Add(-((b - 251) * 256) - charString[i + 1] - 108);
                i += 2;
                continue;
            }

            // An operator. hintmask and cntrmask are followed by one bit per hint, so step over the mask.
            int op = b;
            i++;
            if (op == 12)
            {
                if (i >= charString.Length)
                {
                    return false;
                }

                i++; // two-byte operator; none of them carries a seac
            }
            else if (op is 19 or 20)
            {
                i += (stack.Count + 7) / 8;
            }
            else if (op == 14)
            {
                lastEndChar = new int[stack.Count];
                for (int k = 0; k < stack.Count; k++)
                {
                    lastEndChar[k] = (int)MathF.Round(stack[k]);
                }
            }

            stack.Clear();
        }

        if (lastEndChar is not { Length: 5 })
        {
            return false;
        }

        operands = lastEndChar;
        return true;
    }

    /// <summary>
    /// Maps a character code through the program's own encoding. Subsetters that rename glyphs
    /// (so the charset no longer speaks Adobe glyph names) still keep this table in step with the
    /// encoding the PDF declares, which makes it the fallback for <see cref="TryGetGlyphId"/>.
    /// </summary>
    public bool TryGetGlyphIdForCode(int code, out ushort glyphId)
    {
        glyphId = 0;
        if (code < 0 || code >= CodeToGlyph.Length)
        {
            return false;
        }

        int index = CodeToGlyph[code];
        if (index <= 0 || index >= GlyphCount)
        {
            return false;
        }

        glyphId = (ushort)index;
        return true;
    }

    /// <summary>Resolves a string ID to a glyph name; null when the SID is out of range.</summary>
    public string? GetName(int sid)
    {
        if (sid < 0)
        {
            return null;
        }

        if (sid < CffStandardStrings.Count)
        {
            return CffStandardStrings.Names[sid];
        }

        int index = sid - CffStandardStrings.Count;
        return index < Strings.Length ? Strings[index] : null;
    }

    /// <summary>Parses a name-keyed CFF; returns false for CID-keyed or malformed programs.</summary>
    public static bool TryCreate(byte[] data, out CffProgram? program)
    {
        program = null;
        if (data is null || data.Length < 22 || data[0] != 1 || data[1] != 0)
        {
            return false;
        }

        int position = 4;
        if (!CffIndex.TryRead(data, ref position, out CffIndex nameIndex) ||
            !CffIndex.TryRead(data, ref position, out CffIndex topIndex) ||
            !CffIndex.TryRead(data, ref position, out CffIndex stringIndex) ||
            !CffIndex.TryRead(data, ref position, out _))
        {
            return false;
        }

        if (!CffDict.TryParse(topIndex.ReadItem(data, 0), out CffDict? dict))
        {
            return false;
        }

        // ROS makes the program CID-keyed, which this name-keyed reader cannot address. Operator 14 is
        // XUID, the Type 1 cross-use identifier inherited by converted fonts, and says nothing about the
        // charstrings, so it must not reject the program.
        if (dict.Contains((12 << 8) | 30) || dict.Contains(30))
        {
            return false;
        }

        int charStringsOffset = (int)dict.Get(17);
        if (charStringsOffset <= 0 || charStringsOffset >= data.Length)
        {
            return false;
        }

        int cursor = charStringsOffset;
        if (!CffIndex.TryRead(data, ref cursor, out CffIndex charStrings) || charStrings.Count < 1 || charStrings.Count > ushort.MaxValue)
        {
            return false;
        }

        float[]? matrix = dict.GetArray(7);
        float[]? bbox = dict.GetArray(5);
        int[] sids = ReadCharset(data, (int)dict.Get(15), charStrings.Count);
        int[] codeToGlyph = ReadEncoding(data, (int)dict.Get(16), charStrings.Count);

        program = new CffProgram(
            data,
            charStrings,
            charStrings.Count,
            matrix is { Length: >= 4 } ? ExpandMatrix(matrix) : DefaultFontMatrix,
            bbox is { Length: 4 } ? bbox : [0f, 0f, 0f, 0f],
            stringIndex.ReadStrings(data),
            sids,
            codeToGlyph);
        return true;
    }

    /// <summary>
    /// Reads the built-in encoding: format 0 lists codes whose glyph index is their position plus one
    /// (glyph 0 is .notdef), format 1 lists code and glyph index pairs, optionally followed by a
    /// supplement for codes above 255 that a simple PDF font cannot address.
    /// </summary>
    private static int[] ReadEncoding(byte[] data, int offset, int glyphCount)
    {
        int[] codeToGlyph = new int[256];
        Array.Fill(codeToGlyph, -1);

        if (offset <= 0 || offset >= data.Length)
        {
            // No encoding operator: the standard CFF encoding, i.e. glyph index equals the code's
            // position in the StandardEncoding order. Leave it unused and let the caller fall back.
            return codeToGlyph;
        }

        int position = offset;
        byte format = data[position++];
        if (position >= data.Length)
        {
            return codeToGlyph;
        }

        int count = data[position++];
        if (format == 0)
        {
            for (int i = 0; i < count && position < data.Length; i++, position++)
            {
                int code = data[position];
                if (code < codeToGlyph.Length && i + 1 < glyphCount)
                {
                    codeToGlyph[code] = i + 1;
                }
            }

            return codeToGlyph;
        }

        for (int i = 0; i < count; i++)
        {
            if (position + 2 >= data.Length)
            {
                break;
            }

            int code = data[position++];
            int glyph = data[position++];
            if (code < codeToGlyph.Length && glyph > 0 && glyph < glyphCount)
            {
                codeToGlyph[code] = glyph;
            }
        }

        return codeToGlyph;
    }

    private static float[] ExpandMatrix(float[] matrix) => matrix.Length >= 6
        ? [matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]]
        : [matrix[0], matrix[1], 0f, matrix[2], 0f, matrix[3]];

    /// <summary>
    /// Reads the charset: format 0 lists one SID per glyph, formats 1 and 2 list runs of consecutive
    /// SIDs. A missing charset means the predefined ISO Adobe charset, which is the identity mapping.
    /// </summary>
    private static int[] ReadCharset(byte[] data, int offset, int glyphCount)
    {
        int[] sids = new int[glyphCount];

        if (offset <= 0 || offset >= data.Length)
        {
            for (int gid = 1; gid < glyphCount; gid++)
            {
                sids[gid] = gid;
            }

            return sids;
        }

        byte format = data[offset];
        int position = offset + 1;
        int gidIndex = 1;

        while (gidIndex < glyphCount)
        {
            if (position + 1 >= data.Length)
            {
                break;
            }

            int first = (data[position] << 8) | data[position + 1];
            position += 2;

            int left;
            if (format == 0)
            {
                sids[gidIndex++] = first;
                continue;
            }

            if (format == 1)
            {
                if (position >= data.Length)
                {
                    break;
                }

                left = data[position++];
            }
            else
            {
                if (position + 1 >= data.Length)
                {
                    break;
                }

                left = (data[position] << 8) | data[position + 1];
                position += 2;
            }

            for (int k = 0; k <= left && gidIndex < glyphCount; k++)
            {
                sids[gidIndex++] = first + k;
            }
        }

        return sids;
    }
}
