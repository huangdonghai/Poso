using Poso.Graphics;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Reads glyph outlines out of an embedded CFF program, both the name-keyed and the CID-keyed form.
/// </summary>
/// <remarks>
/// This is deliberately a separate reader from <see cref="CffProgram"/> and <see cref="CidCffProgram"/>:
/// those two only need the metrics and the names an OpenType wrapper and the PDF encoding have to agree
/// on, while painting a glyph needs the charstrings plus the subroutine set each one resolves against.
/// A CID-keyed program can give every font dict its own local subroutines and its own FontMatrix, so the
/// FDSelect is part of what a charstring is run against - not just the charstring bytes.
/// </remarks>
internal sealed class CffOutlines : OutlineSource
{
    /// <summary>CFF FontMatrix in spec order [xx, xy, yx, yy, dx, dy]; the default scales by 1/1000.</summary>
    private static readonly float[] DefaultFontMatrix = [0.001f, 0f, 0f, 0.001f, 0f, 0f];

    private readonly byte[] _program;
    private readonly CffIndex _charStrings;
    private readonly CffIndex _globalSubrs;
    private readonly FontProgram[] _fonts;
    private readonly byte[]? _fdSelect;
    private readonly int[] _charsetSids;
    private readonly string[] _strings;

    private readonly record struct FontProgram(CffIndex LocalSubrs, float[] FontMatrix);

    private CffOutlines(
        byte[] program,
        CffIndex charStrings,
        CffIndex globalSubrs,
        FontProgram[] fonts,
        byte[]? fdSelect,
        int[] charsetSids,
        string[] strings)
    {
        _program = program;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _fonts = fonts;
        _fdSelect = fdSelect;
        _charsetSids = charsetSids;
        _strings = strings;
    }

    public override int GlyphCount => _charStrings.Count;

    /// <summary>Parses a bare CFF (offset 0 holds the 1,0 header a non-container program starts with).</summary>
    public static bool TryCreate(byte[] data, out OutlineSource? source)
    {
        source = null;
        if (data is null || data.Length < 22 || data[0] != 1 || data[1] != 0)
        {
            return false;
        }

        int position = data[2]; // header size
        if (position <= 0 || position >= data.Length)
        {
            return false;
        }

        if (!CffIndex.TryRead(data, ref position, out _) ||
            !CffIndex.TryRead(data, ref position, out CffIndex topIndex) ||
            !CffIndex.TryRead(data, ref position, out CffIndex stringIndex) ||
            !CffIndex.TryRead(data, ref position, out CffIndex globalSubrs))
        {
            return false;
        }

        if (topIndex.Count < 1 || !CffDict.TryParse(topIndex.ReadItem(data, 0), out CffDict top))
        {
            return false;
        }

        // Operator 30 is the Type 1 encryption key, i.e. a program this reader cannot read.
        if (top.Contains(30))
        {
            return false;
        }

        int charStringsOffset = (int)top.Get(17);
        if (charStringsOffset <= 0 || charStringsOffset >= data.Length)
        {
            return false;
        }

        int cursor = charStringsOffset;
        if (!CffIndex.TryRead(data, ref cursor, out CffIndex charStrings) || charStrings.Count < 1)
        {
            return false;
        }

        float[]? topMatrix = top.GetArray(7);
        float[] matrix = topMatrix is { Length: >= 4 } ? Expand(topMatrix) : DefaultFontMatrix;
        string[] strings = stringIndex.ReadStrings(data);
        int[] sids = ReadCharset(data, (int)top.Get(15), charStrings.Count);

        bool cidKeyed = top.Contains((12 << 8) | 30);
        FontProgram[] fonts;
        byte[]? fdSelect = null;

        if (cidKeyed)
        {
            int fdArrayOffset = (int)top.Get((12 << 8) | 36);
            if (fdArrayOffset <= 0 || fdArrayOffset >= data.Length ||
                !CffIndex.TryRead(data, ref fdArrayOffset, out CffIndex fdArray) || fdArray.Count < 1)
            {
                return false;
            }

            fonts = new FontProgram[fdArray.Count];
            for (int i = 0; i < fdArray.Count; i++)
            {
                if (!CffDict.TryParse(fdArray.ReadItem(data, i), out CffDict fd))
                {
                    fonts[i] = new FontProgram(default, matrix);
                    continue;
                }

                float[]? fdMatrix = fd.GetArray(7);
                fonts[i] = new FontProgram(
                    ReadLocalSubrs(data, fd),
                    fdMatrix is { Length: >= 4 } ? Expand(fdMatrix) : matrix);
            }

            fdSelect = ReadFdSelect(data, (int)top.Get((12 << 8) | 37), charStrings.Count, fdArray.Count);
            if (fdSelect is null)
            {
                return false;
            }
        }
        else
        {
            fonts = [new FontProgram(ReadLocalSubrs(data, top), matrix)];
        }

        source = new CffOutlines(data, charStrings, globalSubrs, fonts, fdSelect, sids, strings);
        return true;
    }

    /// <summary>Reads the local subroutine INDEX a DICT's Private entry points at.</summary>
    private static CffIndex ReadLocalSubrs(byte[] data, CffDict dict)
    {
        // Private is [size, offset]; Subrs inside the Private DICT is relative to that offset.
        float[]? priv = dict.GetArray(18);
        if (priv is not { Length: >= 2 })
        {
            return default;
        }

        int offset = (int)priv[1];
        if (offset <= 0 || offset >= data.Length)
        {
            return default;
        }

        int end = Math.Min(data.Length, offset + (int)priv[0]);
        if (offset + 2 >= data.Length || !CffDict.TryParse(data[offset..end], out CffDict privateDict))
        {
            return default;
        }

        int subrsOffset = offset + (int)privateDict.Get(19);
        if (subrsOffset <= offset || subrsOffset >= data.Length ||
            !CffIndex.TryRead(data, ref subrsOffset, out CffIndex subrs))
        {
            return default;
        }

        return subrs;
    }

    /// <summary>
    /// The FDSelect table: glyph id to font dict index. Format 0 is one byte per glyph, format 3 is a
    /// list of (first glyph, fd) runs. Returned expanded because every glyph needs its own answer.
    /// </summary>
    private static byte[]? ReadFdSelect(byte[] data, int offset, int glyphCount, int fontCount)
    {
        if (offset <= 0 || offset >= data.Length || glyphCount < 1)
        {
            return null;
        }

        byte[] map = new byte[glyphCount];
        byte format = data[offset++];

        if (format == 0)
        {
            if (offset + glyphCount > data.Length)
            {
                return null;
            }

            Array.Copy(data, offset, map, 0, glyphCount);
            return map;
        }

        if (format != 3 || offset + 5 > data.Length)
        {
            return null;
        }

        int ranges = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        if (offset + (ranges * 3) + 2 > data.Length)
        {
            return null;
        }

        for (int i = 0; i < ranges; i++)
        {
            int first = (data[offset] << 8) | data[offset + 1];
            int fd = data[offset + 2];
            offset += 3;

            int next = i + 1 < ranges
                ? (data[offset + (i * 3)] << 8) | data[offset + (i * 3) + 1]
                : glyphCount;
            if (fd >= fontCount)
            {
                continue;
            }

            for (int gid = Math.Max(0, first); gid < Math.Min(glyphCount, next); gid++)
            {
                map[gid] = (byte)fd;
            }
        }

        return map;
    }

    /// <summary>
    /// The charset: one string id per glyph, formats 0/1/2. A missing charset means the predefined ISO
    /// Adobe ordering, where the string id equals the glyph id.
    /// </summary>
    private static int[] ReadCharset(byte[] data, int offset, int glyphCount)
    {
        int[] sids = new int[glyphCount];
        for (int gid = 1; gid < glyphCount; gid++)
        {
            sids[gid] = gid;
        }

        if (offset <= 0 || offset >= data.Length)
        {
            return sids;
        }

        byte format = data[offset++];
        int glyph = 1;
        while (glyph < glyphCount && offset + 1 < data.Length)
        {
            int first = (data[offset] << 8) | data[offset + 1];
            offset += 2;

            if (format == 0)
            {
                sids[glyph++] = first;
                continue;
            }

            int left;
            if (format == 1)
            {
                if (offset >= data.Length)
                {
                    break;
                }

                left = data[offset++];
            }
            else
            {
                if (offset + 1 >= data.Length)
                {
                    break;
                }

                left = (data[offset] << 8) | data[offset + 1];
                offset += 2;
            }

            for (int k = 0; k <= left && glyph < glyphCount; k++)
            {
                sids[glyph++] = first + k;
            }
        }

        return sids;
    }

    /// <summary>
    /// Maps a CID to the glyph id the charstrings are ordered by. A CID-keyed charset stores CIDs where a
    /// name-keyed one stores string ids; the position of the CID in that list is the glyph id.
    /// </summary>
    public bool TryGetGlyphIdForCid(int cid, out ushort glyphId)
    {
        for (int gid = 0; gid < _charsetSids.Length; gid++)
        {
            if (_charsetSids[gid] == cid)
            {
                glyphId = (ushort)gid;
                return true;
            }
        }

        glyphId = 0;
        return false;
    }

    public override bool TryGetGlyphIdForName(string name, out ushort glyphId)
    {
        for (int gid = 0; gid < _charsetSids.Length; gid++)
        {
            if (string.Equals(GetName(_charsetSids[gid]), name, StringComparison.Ordinal))
            {
                glyphId = (ushort)gid;
                return true;
            }
        }

        glyphId = 0;
        return false;
    }

    private string? GetName(int sid)
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
        return index < _strings.Length ? _strings[index] : null;
    }

    public override bool TryGetOutline(int glyphId, IPathBuilder path)
    {
        if (glyphId < 0 || glyphId >= _charStrings.Count)
        {
            return false;
        }

        int fontIndex = _fdSelect is { Length: > 0 } select
            ? (glyphId < select.Length ? select[glyphId] : 0)
            : 0;
        if (fontIndex >= _fonts.Length)
        {
            fontIndex = 0;
        }

        FontProgram font = _fonts[fontIndex];
        byte[] charString = _charStrings.ReadItem(_program, glyphId);
        if (charString.Length == 0)
        {
            return false;
        }

        // A seac charstring draws no outline of its own: it names a base and an accent glyph that have to
        // be run separately, the accent at the offset the operands give (TN 5177 4.3).
        if (TryReadSeac(charString, out ushort baseGlyph, out ushort accentGlyph, out int accentX, out int accentY))
        {
            bool drew = TryGetOutline(baseGlyph, path);
            if (drew)
            {
                var accent = new PathBuilder();
                if (TryGetOutline(accentGlyph, accent))
                {
                    AppendTranslated(accent, accentX, accentY, font.FontMatrix, path);
                }
            }

            return drew;
        }

        return Type2Charstring.TryRun(charString, _program, _globalSubrs, font.LocalSubrs, font.FontMatrix, path);
    }

    /// <summary>
    /// Re-emits an already-parsed outline shifted by (dx, dy) design units. The offset is given in the
    /// same design space as the charstrings, so it passes through the same FontMatrix the outline did.
    /// </summary>
    private static void AppendTranslated(IPathBuilder source, float dx, float dy, float[] matrix, IPathBuilder target)
    {
        float ox = (matrix[0] * dx) + (matrix[2] * dy);
        float oy = (matrix[1] * dx) + (matrix[3] * dy);
        if (ox == 0f && oy == 0f)
        {
            return;
        }

        IPath built = source.Build();
        for (int i = 0; i < built.SegmentCount; i++)
        {
            PathSegment segment = built.GetSegment(i);
            switch (segment.Type)
            {
                case PathSegmentType.MoveTo:
                    target.MoveTo(segment.X1 + ox, segment.Y1 + oy);
                    break;

                case PathSegmentType.LineTo:
                    target.LineTo(segment.X1 + ox, segment.Y1 + oy);
                    break;

                case PathSegmentType.CubicTo:
                    target.CubicTo(
                        segment.X1 + ox, segment.Y1 + oy,
                        segment.X2 + ox, segment.Y2 + oy,
                        segment.X3 + ox, segment.Y3 + oy);
                    break;

                case PathSegmentType.Close:
                    target.Close();
                    break;
            }
        }
    }

    /// <summary>
    /// Recognises the five-operand <c>endchar</c> form: <c>asb adx ady bchar achar endchar</c>. Walks the
    /// charstring with the Type 2 grammar so a hintmask's bytes are stepped over rather than read.
    /// </summary>
    private bool TryReadSeac(byte[] charString, out ushort baseGlyph, out ushort accentGlyph, out int accentX, out int accentY)
    {
        baseGlyph = 0;
        accentGlyph = 0;
        accentX = 0;
        accentY = 0;

        var stack = new List<float>(8);
        int hints = 0;
        int[]? operands = null;

        for (int i = 0; i < charString.Length;)
        {
            byte b = charString[i];
            if (b >= 32 || b == 28)
            {
                i++;
                if (b == 28)
                {
                    if (i + 2 > charString.Length)
                    {
                        return false;
                    }

                    stack.Add((short)((charString[i] << 8) | charString[i + 1]));
                    i += 2;
                }
                else if (b <= 246)
                {
                    stack.Add(b - 139);
                }
                else if (b <= 250 && i < charString.Length)
                {
                    stack.Add(((b - 247) * 256) + charString[i++] + 108);
                }
                else if (b <= 254 && i < charString.Length)
                {
                    stack.Add(-((b - 251) * 256) - charString[i++] - 108);
                }
                else if (i + 4 <= charString.Length)
                {
                    stack.Add(((charString[i] << 24) | (charString[i + 1] << 16) | (charString[i + 2] << 8) | charString[i + 3]) / 65536f);
                    i += 4;
                }
                else
                {
                    return false;
                }

                continue;
            }

            i++;
            if (b == 12)
            {
                i++;
            }
            else if (b is 1 or 3 or 18 or 23)
            {
                hints += stack.Count / 2;
            }
            else if (b is 19 or 20)
            {
                hints += stack.Count / 2;
                i += (hints + 7) / 8;
            }
            else if (b == 14)
            {
                operands = new int[stack.Count];
                for (int k = 0; k < stack.Count; k++)
                {
                    operands[k] = (int)MathF.Round(stack[k]);
                }
            }

            stack.Clear();
        }

        if (operands is not { Length: 5 })
        {
            return false;
        }

        // bchar and achar are standard-encoding codes, resolved through the charset.
        if (!TryGetGlyphIdForStandardCode(operands[3], out baseGlyph) ||
            !TryGetGlyphIdForStandardCode(operands[4], out accentGlyph))
        {
            return false;
        }

        accentX = operands[1];
        accentY = operands[2];
        return true;
    }

    private bool TryGetGlyphIdForStandardCode(int code, out ushort glyphId)
    {
        glyphId = 0;
        string? name = code >= 0 && code < PdfEncodings.Standard.Length ? PdfEncodings.Standard[code] : null;
        return name is not null && TryGetGlyphIdForName(name, out glyphId);
    }

    private static float[] Expand(float[] matrix) => matrix.Length >= 6
        ? [matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]]
        : [matrix[0], matrix[1], matrix[2], matrix[3], 0f, 0f];
}
