namespace Poso.Pdf.Fonts;

/// <summary>
/// Wraps a bare CFF program into a minimal OpenType (sfnt) container.
/// </summary>
/// <remarks>
/// Font back ends such as Skia load sfnt containers only, so a <c>FontFile3</c> with
/// /Subtype /Type1C - a bare CFF - has to be packaged before it can be rendered. The wrapper carries
/// the original CFF unchanged plus the handful of sfnt tables a parser insists on (head, hhea, maxp,
/// OS/2, name, hmtx and an empty cmap); glyph selection goes through <see cref="CffProgram"/> and the
/// advance widths through the PDF /Widths array, so the placeholder values here never reach the page.
/// Table lengths follow the OpenType specification exactly: a parser rejects a font whose directory
/// or tables are inconsistent.
/// </remarks>
internal static class CffOpenType
{
    /// <summary>Units per em declared by PDF font metrics (Type1C programs are always 1/1000 em).</summary>
    private const int PdfTextSpaceUnits = 1000;

    public static byte[] Build(byte[] cff, ICffMetrics program, float ascent, float descent, string postScriptName)
    {
        int upem = UnitsPerEm(program.FontMatrix);
        float toUnits = upem / (float)PdfTextSpaceUnits;

        byte[][] tables =
        [
            Table("CFF ", cff),
            Table("OS/2", Metrics(upem, ascent * toUnits, descent * toUnits, postScriptName)),
            Table("cmap", Cmap(program)),
            Table("head", Head(program, upem)),
            Table("hhea", HorizontalHeader(ascent * toUnits, descent * toUnits, program, toUnits, program.GlyphCount)),
            Table("hmtx", HorizontalMetrics(program.GlyphCount, upem)),
            Table("maxp", Maxp(program.GlyphCount)),
            Table("name", Names(postScriptName)),
            Table("post", Post()),
        ];

        Array.Sort(tables, static (left, right) => string.CompareOrdinal(Tag(left), Tag(right)));
        return Container(tables);
    }

    private static int UnitsPerEm(float[] matrix)
    {
        float scale = matrix.Length > 0 ? matrix[0] : 0.001f;
        if (scale <= 0f || !float.IsFinite(scale))
        {
            scale = 0.001f;
        }

        return Math.Clamp((int)MathF.Round(1f / scale), 16, 16384);
    }

    private static string Tag(byte[] table) => System.Text.Encoding.ASCII.GetString(table, 0, 4);

    /// <summary>
    /// Unwraps the CFF table of an sfnt container that carries nothing a parser needs besides it. Some
    /// generators emit a FontFile3 program as 'OTTO' with a two-entry directory (CFF and cmap) and no
    /// head, maxp or name table, which no font back end will open; the CFF inside is a perfectly good
    /// bare program. Returns false for a real sfnt font, which is one that has a head table.
    /// </summary>
    public static bool TryExtractCff(byte[] program, out byte[]? cff)
    {
        cff = null;
        if (program.Length < 12 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program) != 0x4F54544F)
        {
            return false;
        }

        int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
        if (program.Length < 12 + (count * 16))
        {
            return false;
        }

        int cffOffset = -1;
        int cffLength = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            string tag = System.Text.Encoding.ASCII.GetString(program, entry, 4);
            if (tag == "head")
            {
                return false;
            }

            if (tag == "CFF ")
            {
                cffOffset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(entry + 8));
                cffLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(entry + 12));
            }
        }

        if (cffOffset < 0 || cffLength <= 0 || program.Length < cffOffset + cffLength)
        {
            return false;
        }

        cff = program[cffOffset..(cffOffset + cffLength)];
        return true;
    }

    private static byte[] Table(string tag, byte[] body)
    {
        byte[] table = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(table, 0);
        body.CopyTo(table, 4);
        return table;
    }

    private static byte[] Container(byte[][] tables)
    {
        int count = tables.Length;
        int entrySelector = (int)MathF.Floor(MathF.Log2(count));
        int searchRange = (1 << entrySelector) * 16;

        using var stream = new MemoryStream();
        BigEndian writer = new(stream);
        writer.UInt32(0x4F54544F); // 'OTTO': CFF flavored sfnt
        writer.UInt16((ushort)count);
        writer.UInt16((ushort)searchRange);
        writer.UInt16((ushort)entrySelector);
        writer.UInt16((ushort)((count * 16) - searchRange));

        int offset = 12 + (count * 16);
        foreach (byte[] table in tables)
        {
            int length = table.Length - 4;
            writer.Bytes(table, 0, 4);
            writer.UInt32(CheckSum(table, 4, length));
            writer.UInt32((uint)offset);
            writer.UInt32((uint)length);
            offset += Aligned(length);
        }

        foreach (byte[] table in tables)
        {
            int length = table.Length - 4;
            writer.Bytes(table, 4, length);
            for (int padding = Aligned(length) - length; padding > 0; padding--)
            {
                writer.Byte(0);
            }
        }

        return stream.ToArray();
    }

    private static int Aligned(int length) => (length + 3) & ~3;

    private static uint CheckSum(byte[] data, int start, int length)
    {
        uint sum = 0;
        for (int i = 0; i < Aligned(length); i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++)
            {
                int position = start + i + k;
                word = (word << 8) | (position < start + length ? data[position] : (byte)0);
            }

            sum += word;
        }

        return sum;
    }

    private static byte[] Head(ICffMetrics program, int upem)
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);
        float[] box = program.FontBBox;

        writer.UInt32(0x00010000); // version
        writer.UInt32(0x00010000); // fontRevision
        writer.UInt32(0); // checkSumAdjustment
        writer.UInt32(0x5F0F3CF5); // magicNumber
        writer.UInt16(0x0003); // flags: baseline y is up, no composite glyphs
        writer.UInt16((ushort)upem);
        writer.UInt32(0); // created
        writer.UInt32(0);
        writer.UInt32(0); // modified
        writer.UInt32(0);
        writer.Int16((short)box[0]);
        writer.Int16((short)box[1]);
        writer.Int16((short)box[2]);
        writer.Int16((short)box[3]);
        writer.UInt16(0); // macStyle
        writer.UInt16(8); // lowestRecPPEM
        writer.Int16(2); // fontDirectionHint
        writer.Int16(0); // indexToLocFormat
        writer.Int16(0); // glyphDataFormat
        return stream.ToArray();
    }

    private static byte[] HorizontalHeader(float ascent, float descent, ICffMetrics program, float toUnits, int glyphCount)
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt32(0x00010000); // version
        writer.Int16(Round(ascent));
        writer.Int16(Round(descent));
        writer.Int16(0); // lineGap
        writer.UInt16((ushort)Math.Clamp(Round(program.FontBBox[2] * toUnits + 1000), 0, ushort.MaxValue)); // advanceWidthMax
        writer.Int16(Round(program.FontBBox[0] * toUnits)); // minLeftSideBearing
        writer.Int16(Round(program.FontBBox[0] * toUnits)); // minRightSideBearing
        writer.Int16(Round(program.FontBBox[2] * toUnits)); // xMaxExtent
        writer.Int16(0); // caretSlopeRise
        writer.Int16(1); // caretSlopeRun
        writer.Int16(0); // caretOffset
        writer.Int16(0); // reserved
        writer.Int16(0);
        writer.Int16(0);
        writer.Int16(0);
        writer.Int16(0); // metricDataFormat
        writer.UInt16((ushort)Math.Min(glyphCount, ushort.MaxValue)); // numberOfHMetrics
        return stream.ToArray();
    }

    private static byte[] HorizontalMetrics(int glyphCount, int upem)
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);
        ushort advance = (ushort)(upem / 2);

        for (int i = 0; i < glyphCount; i++)
        {
            writer.UInt16(advance);
            writer.Int16(0);
        }

        return stream.ToArray();
    }

    private static byte[] Maxp(int glyphCount)
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt32(0x00010000); // version 1.0
        writer.UInt16((ushort)Math.Min(glyphCount, ushort.MaxValue));
        for (int i = 0; i < 13; i++)
        {
            writer.UInt16(0);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// A post format 3.0 table. Some back ends (notably PeachDrawing) read the post table
    /// unconditionally when computing metrics and throw on its absence; the PDF FontDescriptor's
    /// /ItalicAngle is not threaded here, and a real italic CFF already has its slant baked into the
    /// charstrings, so italicAngle is left 0 (no synthetic skew) with conventional underline metrics.
    /// </summary>
    private static byte[] Post()
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt32(0x00030000); // version 3.0 (no glyph names)
        writer.Int16(0); // italicAngle (16.16 fixed) high word
        writer.Int16(0); // italicAngle low word
        writer.Int16(-100); // underlinePosition
        writer.Int16(50); // underlineThickness
        writer.UInt32(0); // isFixedPitch
        writer.UInt32(0); // minMemType42
        writer.UInt32(0); // maxMemType42
        writer.UInt32(0); // minMemType1
        writer.UInt32(0); // maxMemType1
        return stream.ToArray();
    }

    private static byte[] Metrics(int upem, float ascent, float descent, string name)
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt16(4); // version
        writer.Int16((short)(upem * 6 / 10)); // xAvgCharWidth
        writer.UInt16(400); // usWeightClass
        writer.UInt16(5); // usWidthClass
        writer.UInt16(0); // fsType
        writer.Int16((short)(upem * 3 / 10)); // subscriptXSize
        writer.Int16((short)(upem * 2 / 10)); // subscriptYSize
        writer.Int16((short)(upem * 3 / 10)); // superscriptXSize
        writer.Int16((short)(upem * 2 / 10)); // superscriptYSize
        writer.Int16(0); // subscriptXOffset
        writer.Int16(0); // subscriptYOffset
        writer.Int16(0); // superscriptXOffset
        writer.Int16(0); // superscriptYOffset
        writer.Int16(0); // strikeoutSize
        writer.Int16(0); // strikeoutPosition
        writer.Int16(0); // sFamilyClass
        for (int i = 0; i < 10; i++)
        {
            writer.Byte(0); // panose
        }

        writer.UInt32(0x00000001); // ulUnicodeRange1: Basic Latin
        writer.UInt32(0); // ulUnicodeRange2
        writer.UInt32(0); // ulUnicodeRange3
        writer.UInt32(0); // ulUnicodeRange4
        writer.Ascii("Poso"); // achVendID
        writer.UInt16(0x0040); // fsSelection: regular
        writer.UInt16(0x0020); // usFirstCharIndex
        writer.UInt16(0x007E); // usLastCharIndex
        writer.Int16(Round(ascent)); // sTypoAscender
        writer.Int16(Round(descent)); // sTypoDescender
        writer.Int16(0); // sTypoLineGap
        writer.UInt16((ushort)Math.Max(0, (int)Round(ascent))); // usWinAscent
        writer.UInt16((ushort)Math.Abs((int)Round(descent))); // usWinDescent
        writer.UInt32(0); // ulCodePageRange1
        writer.UInt32(0); // ulCodePageRange2
        writer.Int16(0); // sxHeight
        writer.Int16(0); // sCapHeight
        writer.UInt16(0); // usDefaultChar
        writer.UInt16(0x20); // usBreakChar
        writer.UInt16(8); // usMaxContext
        return stream.ToArray();
    }

    /// <summary>
    /// The container's character map, built from the program's charset: every glyph whose name the
    /// Adobe Glyph List knows is mapped from its character to the glyph.
    ///
    /// A back end that opens a font looks the character up in the cmap, and the glyph id it gets back
    /// is the one it will use to draw. With no cmap at all, a CFF program leaves the back end to guess
    /// the ids from the charset, and the guess does not match the program's own numbering: pdf.js
    /// issue6132.pdf's cmbx12 drew every letter as the wrong one - the page came out as evenly spaced
    /// rubbish. The ids the back end hands out are now its own, and they address the right outlines.
    /// </summary>
    private static byte[] Cmap(ICffMetrics program)
    {
        var mappings = new SortedDictionary<int, ushort>();
        for (int gid = 0; gid < program.GlyphCount; gid++)
        {
            string? name = program.GetNameForGlyph(gid);
            if (name is null)
            {
                continue;
            }

            uint codePoint = AdobeGlyphList.GetUnicode(name);
            if (codePoint == 0 || codePoint > 0xFFFF)
            {
                continue;
            }

            mappings.TryAdd((int)codePoint, (ushort)gid);
        }

        if (mappings.Count == 0)
        {
            return EmptyCmap();
        }

        // A format 4 subtable addresses a range of characters with a constant offset from the
        // character code to the glyph id, so the characters are grouped by (glyph id - code point).
        var segments = new SortedDictionary<int, List<int>>();
        foreach ((int codePoint, ushort gid) in mappings)
        {
            int delta = gid - codePoint;
            if (!segments.TryGetValue(delta, out List<int>? list))
            {
                list = [];
                segments[delta] = list;
            }

            list.Add(codePoint);
        }

        var entries = new List<(int Start, int End, int Delta)>();
        foreach ((int delta, List<int> codePoints) in segments)
        {
            codePoints.Sort();
            int start = codePoints[0];
            int previous = start;
            foreach (int codePoint in codePoints)
            {
                if (codePoint != previous + 1)
                {
                    entries.Add((start, previous, delta));
                    start = codePoint;
                }

                previous = codePoint;
            }

            entries.Add((start, previous, delta));
        }

        entries.Sort((left, right) => left.Start.CompareTo(right.Start));
        entries.Add((0xFFFF, 0xFFFF, 1));

        int segCount = entries.Count;
        int length = 16 + (segCount * 8);

        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt16(0); // version
        writer.UInt16(1); // numTables
        writer.UInt16(3); // platformID: Windows
        writer.UInt16(1); // encodingID: Unicode BMP
        writer.UInt32(12); // subtable offset

        writer.UInt16(4); // format 4
        writer.UInt16((ushort)length);
        writer.UInt16(0); // language
        writer.UInt16((ushort)(segCount * 2)); // segCountX2
        int searchRange = 2;
        int entrySelector = 0;
        while (searchRange * 2 <= segCount * 2)
        {
            searchRange *= 2;
            entrySelector++;
        }

        writer.UInt16((ushort)searchRange);
        writer.UInt16((ushort)entrySelector);
        writer.UInt16((ushort)((segCount * 2) - searchRange));
        foreach ((int start, int end, _) in entries)
        {
            writer.UInt16((ushort)end);
        }

        writer.UInt16(0); // reservedPad
        foreach ((int start, _, _) in entries)
        {
            writer.UInt16((ushort)start);
        }

        foreach ((_, _, int delta) in entries)
        {
            writer.UInt16((ushort)(delta & 0xFFFF));
        }

        foreach ((_, _, _) in entries)
        {
            writer.UInt16(0); // idRangeOffset
        }

        return stream.ToArray();
    }

    /// <summary>A character map with no mappings: the subtable a font with no named glyphs gets.</summary>
    private static byte[] EmptyCmap()
    {
        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt16(0); // version
        writer.UInt16(1); // numTables
        writer.UInt16(3); // platformID: Windows
        writer.UInt16(1); // encodingID: Unicode BMP
        writer.UInt32(12); // subtable offset

        writer.UInt16(4); // format 4
        writer.UInt16(32); // length
        writer.UInt16(0); // language
        writer.UInt16(2); // segCountX2
        writer.UInt16(2); // searchRange
        writer.UInt16(1); // entrySelector
        writer.UInt16(2); // rangeShift
        writer.UInt16(0xFFFF); // endCode[0]
        writer.UInt16(0); // endCode[1]
        writer.UInt16(0); // reservedPad
        writer.UInt16(0xFFFF); // startCode[0]
        writer.UInt16(0); // startCode[1]
        writer.UInt16(1); // idDelta[0]
        writer.UInt16(1); // idDelta[1]
        writer.UInt16(0); // idRangeOffset[0]
        writer.UInt16(0); // idRangeOffset[1]
        return stream.ToArray();
    }

    private static byte[] Names(string postScriptName)
    {
        // nameIDs 1, 2, 3, 4 and 6: family, subfamily, unique, full and PostScript name
        string[] values = [postScriptName, "Regular", $"{postScriptName};Poso", postScriptName, postScriptName];
        ushort[] nameIds = [1, 2, 3, 4, 6];

        using var stream = new MemoryStream();
        BigEndian writer = new(stream);

        writer.UInt16(0); // format
        writer.UInt16((ushort)values.Length);
        writer.UInt16((ushort)(6 + (values.Length * 12))); // string offset

        ushort offset = 0;
        for (int i = 0; i < values.Length; i++)
        {
            writer.UInt16(3); // platformID: Windows
            writer.UInt16(1); // encodingID: Unicode
            writer.UInt16(0x0409); // languageID: English
            writer.UInt16(nameIds[i]);
            writer.UInt16((ushort)(values[i].Length * 2));
            writer.UInt16(offset);
            offset += (ushort)(values[i].Length * 2);
        }

        foreach (string value in values)
        {
            foreach (char c in value)
            {
                writer.UInt16((ushort)c);
            }
        }

        return stream.ToArray();
    }

    private static short Round(float value) => float.IsFinite(value)
        ? (short)Math.Clamp((int)MathF.Round(value), short.MinValue, short.MaxValue)
        : (short)0;

    /// <summary>Writes big endian values, the byte order of both sfnt and CFF.</summary>
    private readonly struct BigEndian(MemoryStream stream)
    {
        public void Byte(int value) => stream.WriteByte((byte)value);

        public void Int16(short value) => UInt16((ushort)value);

        public void UInt16(ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        public void UInt32(uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        public void Bytes(byte[] data, int start, int length) => stream.Write(data, start, length);

        public void Ascii(string text)
        {
            foreach (byte value in System.Text.Encoding.ASCII.GetBytes(text))
            {
                stream.WriteByte(value);
            }
        }
    }
}
