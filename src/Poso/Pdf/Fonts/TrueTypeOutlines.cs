using Poso.Graphics;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Reads glyph outlines out of a TrueType / OpenType <c>glyf</c> table: the <c>loca</c> index, simple
/// glyphs with quadratic curves, and composite glyphs that place other glyphs by offset.
/// </summary>
/// <remarks>
/// Curves are elevated to cubics here rather than being handed down as quadratics: the path model has one
/// curve type, and the elevation is exact for a quadratic (P0, P0 + 2/3(P1 - P0), P2 + 2/3(P1 - P2), P2).
/// </remarks>
internal sealed class TrueTypeOutlines : OutlineSource
{
    private readonly byte[] _font;
    private readonly int[] _offsets;
    private readonly int _glyphCount;
    private readonly int _unitsPerEm;

    private TrueTypeOutlines(byte[] font, int[] offsets, int glyphCount, int unitsPerEm)
    {
        _font = font;
        _offsets = offsets;
        _glyphCount = glyphCount;
        _unitsPerEm = unitsPerEm;
    }

    public override int GlyphCount => _glyphCount;

    /// <summary>Parses a single sfnt program (not a collection).</summary>
    public static bool TryCreate(byte[] data, out OutlineSource? source)
    {
        source = null;
        if (data is null || data.Length < 12)
        {
            return false;
        }

        // 0x00010000 (TrueType) and 'true' both describe an sfnt; 'OTTO' is CFF-based and handled elsewhere.
        bool truetype = (data[0] == 0 && data[1] == 1 && data[2] == 0 && data[3] == 0) ||
                        (data[0] == 't' && data[1] == 'r' && data[2] == 'u' && data[3] == 'e');
        if (!truetype)
        {
            return false;
        }

        if (!TryFindTable(data, "loca", out int locaOffset, out int locaLength) ||
            !TryFindTable(data, "glyf", out _, out _) ||
            !TryFindTable(data, "head", out int headOffset, out int headLength) ||
            !TryFindTable(data, "maxp", out int maxpOffset, out int maxpLength))
        {
            return false;
        }

        if (headLength < 54 || maxpLength < 6)
        {
            return false;
        }

        int indexToLocFormat = ReadU16(data, headOffset + 50);
        int unitsPerEm = ReadU16(data, headOffset + 18);
        int glyphCount = ReadU16(data, maxpOffset + 4);
        if (unitsPerEm <= 0 || glyphCount <= 0)
        {
            return false;
        }

        int[] offsets = new int[glyphCount + 1];
        if (indexToLocFormat == 0)
        {
            if (locaLength < (glyphCount + 1) * 2)
            {
                return false;
            }

            for (int i = 0; i <= glyphCount; i++)
            {
                offsets[i] = ReadU16(data, locaOffset + (i * 2)) * 2;
            }
        }
        else
        {
            if (locaLength < (glyphCount + 1) * 4)
            {
                return false;
            }

            for (int i = 0; i <= glyphCount; i++)
            {
                offsets[i] = (int)ReadU32(data, locaOffset + (i * 4));
            }
        }

        // Every offset is relative to the glyf table, so the table's own start has to be added back.
        TryFindTable(data, "glyf", out int glyfOffset, out _);
        for (int i = 0; i <= glyphCount; i++)
        {
            offsets[i] += glyfOffset;
        }

        source = new TrueTypeOutlines(data, offsets, glyphCount, unitsPerEm);
        return true;
    }

    private static bool TryFindTable(byte[] data, string tag, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        int count = ReadU16(data, 4);
        if (count < 0 || data.Length < 12 + (count * 16))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (data[record] == tag[0] && data[record + 1] == tag[1] &&
                data[record + 2] == tag[2] && data[record + 3] == tag[3])
            {
                offset = (int)ReadU32(data, record + 8);
                length = (int)ReadU32(data, record + 12);
                return offset > 0 && offset <= data.Length;
            }
        }

        return false;
    }

    public override bool TryGetGlyphIdForName(string name, out ushort glyphId)
    {
        glyphId = 0;
        return false;
    }

    private Dictionary<uint, ushort>? _codeToGlyph;

    /// <summary>
    /// Maps a code point through the program's own <c>cmap</c>. A simple TrueType font whose program the
    /// back end rejected has no other route from a PDF code to an outline: the back end's substitute
    /// face does not know the subset's glyph order, and the PDF rarely ships /Widths-by-name.
    /// </summary>
    public override bool TryGetGlyphIdForCodePoint(uint codePoint, out ushort glyphId)
    {
        _codeToGlyph ??= ReadCmap();
        return _codeToGlyph.TryGetValue(codePoint, out glyphId);
    }

    private Dictionary<uint, ushort> ReadCmap()
    {
        var map = new Dictionary<uint, ushort>();
        if (!TryFindTable(_font, "cmap", out int cmap, out _))
        {
            return map;
        }

        int subTables = ReadU16(_font, cmap + 2);
        for (int i = 0; i < subTables; i++)
        {
            int record = cmap + 4 + (i * 8);
            if (record + 8 > _font.Length)
            {
                break;
            }

            int sub = cmap + (int)ReadU32(_font, record + 4);
            if (sub + 2 > _font.Length)
            {
                continue;
            }

            switch (ReadU16(_font, sub))
            {
                case 0:
                    ReadCmapFormat0(sub, map);
                    break;

                case 2:
                    ReadCmapFormat2(sub, map);
                    break;

                case 4:
                    ReadCmapFormat4(sub, map);
                    break;

                case 6:
                    ReadCmapFormat6(sub, map);
                    break;

                case 12:
                    ReadCmapFormat12(sub, map);
                    break;
            }
        }

        return map;
    }

    private void ReadCmapFormat0(int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 6 + 256 > _font.Length)
        {
            return;
        }

        for (int code = 0; code < 256; code++)
        {
            ushort glyph = _font[sub + 6 + code];
            if (glyph != 0)
            {
                map.TryAdd((uint)code, glyph);
            }
        }
    }

    /// <summary>
    /// The mixed 8/16-bit table Ghostscript and many Windows subsets actually write. The two-byte code
    /// is (high &lt;&lt; 8) | low; a high byte whose key points at an empty sub-header falls through to
    /// the one-byte codes below 256, which the PDF /Encoding addresses directly.
    /// </summary>
    private void ReadCmapFormat2(int sub, Dictionary<uint, ushort> map)
    {
        int keys = sub + 6;
        int subHeaders = keys + 512;
        if (subHeaders + 8 > _font.Length)
        {
            return;
        }

        for (int high = 0; high < 256; high++)
        {
            int key = ReadU16(_font, keys + (high * 2)) / 8;
            int header = subHeaders + (key * 8);
            if (header + 8 > _font.Length)
            {
                continue;
            }

            int firstCode = ReadU16(_font, header);
            int entryCount = ReadU16(_font, header + 2);
            short delta = (short)ReadU16(_font, header + 4);
            int rangeOffset = ReadU16(_font, header + 6);
            for (int low = firstCode; low < firstCode + entryCount && low < 256; low++)
            {
                int address = header + 6 + rangeOffset + ((low - firstCode) * 2);
                if (address + 2 > _font.Length)
                {
                    continue;
                }

                int glyph = ReadU16(_font, address);
                if (glyph == 0)
                {
                    continue;
                }

                glyph = (glyph + delta) & 0xffff;
                if (glyph != 0)
                {
                    map.TryAdd((uint)((high << 8) | low), (ushort)glyph);
                }
            }
        }
    }

    private void ReadCmapFormat4(int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 14 > _font.Length)
        {
            return;
        }

        int segX2 = ReadU16(_font, sub + 6);
        int segments = segX2 / 2;
        int ends = sub + 14;
        int starts = ends + segX2 + 2;
        int deltas = starts + segX2;
        int ranges = deltas + segX2;
        for (int s = 0; s < segments; s++)
        {
            if (ends + s * 2 + 2 > _font.Length || starts + s * 2 + 2 > _font.Length)
            {
                break;
            }

            int last = ReadU16(_font, ends + (s * 2));
            int first = ReadU16(_font, starts + (s * 2));
            short delta = (short)ReadU16(_font, deltas + (s * 2));
            int rangeOffset = ReadU16(_font, ranges + (s * 2));
            for (int code = first; code <= last && code != 0xffff; code++)
            {
                ushort glyph;
                if (rangeOffset == 0)
                {
                    glyph = (ushort)((code + delta) & 0xffff);
                }
                else
                {
                    int address = ranges + (s * 2) + rangeOffset + ((code - first) * 2);
                    if (address + 2 > _font.Length)
                    {
                        break;
                    }

                    int raw = ReadU16(_font, address);
                    glyph = raw == 0 ? (ushort)0 : (ushort)((raw + delta) & 0xffff);
                }

                if (glyph != 0)
                {
                    map.TryAdd((uint)code, glyph);
                }
            }
        }
    }

    private void ReadCmapFormat6(int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 10 > _font.Length)
        {
            return;
        }

        int first = ReadU16(_font, sub + 6);
        int count = ReadU16(_font, sub + 8);
        for (int i = 0; i < count; i++)
        {
            int address = sub + 10 + (i * 2);
            if (address + 2 > _font.Length)
            {
                break;
            }

            ushort glyph = (ushort)ReadU16(_font, address);
            if (glyph != 0)
            {
                map.TryAdd((uint)(first + i), glyph);
            }
        }
    }

    private void ReadCmapFormat12(int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 16 > _font.Length)
        {
            return;
        }

        int groups = (int)ReadU32(_font, sub + 12);
        for (int g = 0; g < groups; g++)
        {
            int record = sub + 16 + (g * 12);
            if (record + 12 > _font.Length)
            {
                break;
            }

            uint start = ReadU32(_font, record);
            uint end = ReadU32(_font, record + 4);
            uint glyphStart = ReadU32(_font, record + 8);
            for (uint code = start; code <= end && code - start < 65536; code++)
            {
                if (glyphStart + (code - start) <= ushort.MaxValue)
                {
                    map.TryAdd(code, (ushort)(glyphStart + (code - start)));
                }
            }
        }
    }

    public override bool TryGetOutline(int glyphId, IPathBuilder path) =>
        AppendGlyph(glyphId, 0f, 0f, path, depth: 0);

    /// <summary>
    /// Appends one glyph's contours. Composite glyphs are walked recursively: each component names a glyph
    /// id and an offset, and the transform that would rotate or scale it (rare, and only in a WE_HAVE_A_TWO_BY_TWO
    /// record) is applied to the component's coordinates as they are emitted.
    /// </summary>
    private bool AppendGlyph(int glyphId, float dx, float dy, IPathBuilder path, int depth)
    {
        if (glyphId < 0 || glyphId >= _glyphCount || depth > 8)
        {
            return false;
        }

        int start = _offsets[glyphId];
        int end = _offsets[glyphId + 1];
        if (start < 0 || end <= start || end > _font.Length || end - start < 10)
        {
            return false;
        }

        int contours = (short)ReadU16(_font, start);
        if (contours >= 0)
        {
            return AppendSimpleGlyph(start, contours, dx, dy, path);
        }

        return AppendCompositeGlyph(start, dx, dy, path, depth);
    }

    private bool AppendSimpleGlyph(int start, int contours, float dx, float dy, IPathBuilder path)
    {
        int position = start + 10;
        int pointCount = 0;
        int[] endPoints = new int[contours];
        for (int i = 0; i < contours; i++)
        {
            if (position + 2 > _font.Length)
            {
                return false;
            }

            endPoints[i] = ReadU16(_font, position);
            position += 2;
        }

        if (contours > 0)
        {
            pointCount = endPoints[^1] + 1;
        }

        if (position + 2 > _font.Length)
        {
            return false;
        }

        int instructionLength = ReadU16(_font, position);
        position += 2 + instructionLength;
        if (position > _font.Length || pointCount <= 0)
        {
            return false;
        }

        // Flags, then the x deltas, then the y deltas - each run-length encoded.
        byte[] flags = new byte[pointCount];
        for (int i = 0; i < pointCount && position < _font.Length;)
        {
            byte flag = _font[position++];
            flags[i++] = flag;
            if ((flag & 0x08) != 0 && position < _font.Length)
            {
                int repeat = _font[position++];
                for (int k = 0; k < repeat && i < pointCount; k++)
                {
                    flags[i++] = flag;
                }
            }
        }

        int[] xs = new int[pointCount];
        int x = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & 0x02) != 0)
            {
                if (position >= _font.Length)
                {
                    return false;
                }

                int delta = _font[position++];
                x += (flag & 0x10) != 0 ? delta : -delta;
            }
            else if ((flag & 0x10) == 0)
            {
                if (position + 2 > _font.Length)
                {
                    return false;
                }

                x += (short)ReadU16(_font, position);
                position += 2;
            }

            xs[i] = x;
        }

        int[] ys = new int[pointCount];
        int y = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & 0x04) != 0)
            {
                if (position >= _font.Length)
                {
                    return false;
                }

                int delta = _font[position++];
                y += (flag & 0x20) != 0 ? delta : -delta;
            }
            else if ((flag & 0x20) == 0)
            {
                if (position + 2 > _font.Length)
                {
                    return false;
                }

                y += (short)ReadU16(_font, position);
                position += 2;
            }

            ys[i] = y;
        }

        float scale = 1f / _unitsPerEm;
        int first = 0;
        for (int c = 0; c < contours; c++)
        {
            int last = endPoints[c];
            if (last < first || last >= pointCount)
            {
                return false;
            }

            EmitContour(flags, xs, ys, first, last, dx, dy, scale, path);
            first = last + 1;
        }

        return contours > 0;
    }

    /// <summary>
    /// Emits one contour. A TrueType contour may begin on an off-curve point, and consecutive off-curve
    /// points imply an on-curve point midway between them; both are resolved here so the path only ever
    /// carries explicit on-curve endpoints.
    /// </summary>
    private static void EmitContour(
        byte[] flags, int[] xs, int[] ys, int first, int last, float dx, float dy, float scale, IPathBuilder path)
    {
        int count = last - first + 1;
        if (count < 2)
        {
            return;
        }

        bool OnCurve(int index) => (flags[first + (index % count)] & 0x01) != 0;
        float Px(int index) => (xs[first + (index % count)] * scale) + dx;
        float Py(int index) => (ys[first + (index % count)] * scale) + dy;

        int startIndex = 0;
        while (startIndex < count && !OnCurve(startIndex))
        {
            startIndex++;
        }

        if (startIndex == count)
        {
            // An all-off-curve contour is degenerate; treat its first point as the start.
            path.MoveTo(Px(0), Py(0));
            path.Close();
            return;
        }

        path.MoveTo(Px(startIndex), Py(startIndex));

        int index = startIndex + 1;
        int steps = 0;
        while (steps < count)
        {
            int current = index % count;
            if (OnCurve(current))
            {
                path.LineTo(Px(current), Py(current));
                index++;
                steps++;
                continue;
            }

            int next = (index + 1) % count;
            if (OnCurve(next))
            {
                path.CubicTo(
                    Px(current), Py(current),
                    Px(current), Py(current),
                    Px(next), Py(next));
                index += 2;
                steps += 2;
                continue;
            }

            // Two off-curve points in a row: the implied on-curve point sits halfway between them.
            float midX = (Px(current) + Px(next)) * 0.5f;
            float midY = (Py(current) + Py(next)) * 0.5f;
            path.CubicTo(
                Px(current), Py(current),
                Px(current), Py(current),
                midX, midY);
            index++;
            steps++;
        }

        path.Close();
    }

    private bool AppendCompositeGlyph(int start, float dx, float dy, IPathBuilder path, int depth)
    {
        int position = start + 10;
        bool drew = false;

        for (int guard = 0; guard < 64; guard++)
        {
            if (position + 4 > _font.Length)
            {
                return drew;
            }

            int flags = ReadU16(_font, position);
            int glyphId = ReadU16(_font, position + 2);
            position += 4;

            float arg1;
            float arg2;
            if ((flags & 0x0001) != 0)
            {
                // ARG_1_AND_2_ARE_WORDS: both are signed 16-bit values.
                if (position + 4 > _font.Length)
                {
                    return drew;
                }

                arg1 = (short)ReadU16(_font, position);
                arg2 = (short)ReadU16(_font, position + 2);
                position += 4;
            }
            else
            {
                if (position + 2 > _font.Length)
                {
                    return drew;
                }

                arg1 = (sbyte)_font[position];
                arg2 = (sbyte)_font[position + 1];
                position += 2;
            }

            // ARGS_ARE_XY_VALUES: the arguments are an offset. Otherwise they are point numbers to match
            // up between the component already placed and this one, which only the hinting stage uses;
            // treating them as an offset would move the component somewhere it does not belong.
            bool xyOffset = (flags & 0x0002) != 0;
            float offsetX = xyOffset ? arg1 / _unitsPerEm : 0f;
            float offsetY = xyOffset ? arg2 / _unitsPerEm : 0f;

            if ((flags & 0x0008) != 0)
            {
                // WE_HAVE_A_SCALE: one F2Dot14 factor for both axes.
                if (position + 2 > _font.Length)
                {
                    return drew;
                }

                float scale = (short)ReadU16(_font, position) / 16384f;
                position += 2;
                var scaled = new PathBuilder();
                if (AppendGlyph(glyphId, 0f, 0f, scaled, depth + 1))
                {
                    AppendTransformed(scaled, scale, 0f, 0f, scale, dx + offsetX, dy + offsetY, path);
                    drew = true;
                }
            }
            else if ((flags & 0x0040) != 0)
            {
                // WE_HAVE_AN_X_AND_Y_SCALE
                if (position + 4 > _font.Length)
                {
                    return drew;
                }

                float sx = (short)ReadU16(_font, position) / 16384f;
                float sy = (short)ReadU16(_font, position + 2) / 16384f;
                position += 4;
                var scaled = new PathBuilder();
                if (AppendGlyph(glyphId, 0f, 0f, scaled, depth + 1))
                {
                    AppendTransformed(scaled, sx, 0f, 0f, sy, dx + offsetX, dy + offsetY, path);
                    drew = true;
                }
            }
            else if ((flags & 0x0080) != 0)
            {
                // WE_HAVE_A_TWO_BY_TWO
                if (position + 8 > _font.Length)
                {
                    return drew;
                }

                float a = (short)ReadU16(_font, position) / 16384f;
                float b = (short)ReadU16(_font, position + 2) / 16384f;
                float c = (short)ReadU16(_font, position + 4) / 16384f;
                float d = (short)ReadU16(_font, position + 6) / 16384f;
                position += 8;
                var scaled = new PathBuilder();
                if (AppendGlyph(glyphId, 0f, 0f, scaled, depth + 1))
                {
                    AppendTransformed(scaled, a, b, c, d, dx + offsetX, dy + offsetY, path);
                    drew = true;
                }
            }
            else
            {
                var plain = new PathBuilder();
                if (AppendGlyph(glyphId, 0f, 0f, plain, depth + 1))
                {
                    AppendTransformed(plain, 1f, 0f, 0f, 1f, dx + offsetX, dy + offsetY, path);
                    drew = true;
                }
            }

            if ((flags & 0x0020) == 0)
            {
                break;
            }

            // MORE_COMPONENTS
            if ((flags & 0x0100) != 0)
            {
                position += 2; // WE_HAVE_INSTRUCTIONS: the length word follows the last component
            }
        }

        return drew;
    }

    private static void AppendTransformed(
        IPathBuilder source, float a, float b, float c, float d, float dx, float dy, IPathBuilder target)
    {
        IPath built = source.Build();
        for (int i = 0; i < built.SegmentCount; i++)
        {
            PathSegment segment = built.GetSegment(i);
            float X(float x, float y) => (a * x) + (c * y) + dx;
            float Y(float x, float y) => (b * x) + (d * y) + dy;

            switch (segment.Type)
            {
                case PathSegmentType.MoveTo:
                    target.MoveTo(X(segment.X1, segment.Y1), Y(segment.X1, segment.Y1));
                    break;

                case PathSegmentType.LineTo:
                    target.LineTo(X(segment.X1, segment.Y1), Y(segment.X1, segment.Y1));
                    break;

                case PathSegmentType.CubicTo:
                    target.CubicTo(
                        X(segment.X1, segment.Y1), Y(segment.X1, segment.Y1),
                        X(segment.X2, segment.Y2), Y(segment.X2, segment.Y2),
                        X(segment.X3, segment.Y3), Y(segment.X3, segment.Y3));
                    break;

                case PathSegmentType.Close:
                    target.Close();
                    break;
            }
        }
    }

    private static int ReadU16(byte[] data, int offset) =>
        (data[offset] << 8) | data[offset + 1];

    private static uint ReadU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
