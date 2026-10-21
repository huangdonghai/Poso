namespace Poso.Pdf.Fonts;

/// <summary>
/// Reads an sfnt's <c>cmap</c> into code point -&gt; glyph id. A PDF can address a simple TrueType font
/// by code alone, so the program's own character map is the only bridge from a code to its glyph;
/// back ends that cannot load the program, or that only consult one subtable, still need this mapping.
/// </summary>
/// <remarks>
/// Subtables are merged by preference: full Unicode first, then the BMP table, then other Unicode
/// platforms, then the Windows symbol table, then the Macintosh one. Earlier sources win; zero glyph
/// ids are never recorded, so an empty table cannot shadow a usable one. A (3,0) symbol table normally
/// places its characters at 0xF020-0xF0FF, so each of those is also registered at its low-byte code -
/// the code a PDF actually writes.
/// </remarks>
internal static class SfntCmap
{
    public static Dictionary<uint, ushort> Read(byte[] data)
    {
        var map = new Dictionary<uint, ushort>();
        if (data is null || data.Length < 12 || !TrueTypeOutlines.TryFindTable(data, "cmap", out int cmap, out _))
        {
            return map;
        }

        int count = TrueTypeOutlines.ReadU16(data, cmap + 2);
        var records = new List<(int Rank, int Offset)>(count);
        for (int i = 0; i < count; i++)
        {
            int record = cmap + 4 + (i * 8);
            if (record + 8 > data.Length)
            {
                break;
            }

            int platform = TrueTypeOutlines.ReadU16(data, record);
            int encoding = TrueTypeOutlines.ReadU16(data, record + 2);
            int sub = cmap + (int)TrueTypeOutlines.ReadU32(data, record + 4);
            if (sub + 2 > data.Length)
            {
                continue;
            }

            records.Add((Rank(platform, encoding), sub));
        }

        records.Sort((left, right) => left.Rank.CompareTo(right.Rank));
        foreach ((int rank, int sub) in records)
        {
            var part = new Dictionary<uint, ushort>();
            switch (TrueTypeOutlines.ReadU16(data, sub))
            {
                case 0:
                    ReadFormat0(data, sub, part);
                    break;

                case 2:
                    ReadFormat2(data, sub, part);
                    break;

                case 4:
                    ReadFormat4(data, sub, part);
                    break;

                case 6:
                    ReadFormat6(data, sub, part);
                    break;

                case 12:
                    ReadFormat12(data, sub, part);
                    break;
            }

            // A Windows symbol table keys its characters at 0xF000 + the character code; a PDF code is
            // the low byte, so register that spelling too.
            if (rank == SymbolRank)
            {
                foreach (KeyValuePair<uint, ushort> pair in part.ToArray())
                {
                    if (pair.Key >= 0xF000 && pair.Key <= 0xF0FF)
                    {
                        part.TryAdd(pair.Key - 0xF000, pair.Value);
                    }
                }
            }

            foreach (KeyValuePair<uint, ushort> pair in part)
            {
                map.TryAdd(pair.Key, pair.Value);
            }
        }

        return map;
    }

    private const int SymbolRank = 3;

    private static int Rank(int platform, int encoding) => (platform, encoding) switch
    {
        (3, 10) => 0,
        (3, 1) => 1,
        (0, _) => 2,
        (3, 0) => SymbolRank,
        (1, 0) => 4,
        _ => 5,
    };

    private static void ReadFormat0(byte[] data, int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 6 + 256 > data.Length)
        {
            return;
        }

        for (int code = 0; code < 256; code++)
        {
            ushort glyph = data[sub + 6 + code];
            if (glyph != 0)
            {
                map.TryAdd((uint)code, glyph);
            }
        }
    }

    /// <summary>
    /// The mixed 8/16-bit table Ghostscript and many Windows subsets write. The two-byte code is
    /// (high &lt;&lt; 8) | low; a high byte whose key points at an empty sub-header falls through to the
    /// one-byte codes below 256, which the PDF /Encoding addresses directly.
    /// </summary>
    private static void ReadFormat2(byte[] data, int sub, Dictionary<uint, ushort> map)
    {
        int keys = sub + 6;
        int subHeaders = keys + 512;
        if (subHeaders + 8 > data.Length)
        {
            return;
        }

        for (int high = 0; high < 256; high++)
        {
            int key = TrueTypeOutlines.ReadU16(data, keys + (high * 2)) / 8;
            int header = subHeaders + (key * 8);
            if (header + 8 > data.Length)
            {
                continue;
            }

            int firstCode = TrueTypeOutlines.ReadU16(data, header);
            int entryCount = TrueTypeOutlines.ReadU16(data, header + 2);
            short delta = (short)TrueTypeOutlines.ReadU16(data, header + 4);
            int rangeOffset = TrueTypeOutlines.ReadU16(data, header + 6);
            for (int low = firstCode; low < firstCode + entryCount && low < 256; low++)
            {
                int address = header + 6 + rangeOffset + ((low - firstCode) * 2);
                if (address + 2 > data.Length)
                {
                    continue;
                }

                int glyph = TrueTypeOutlines.ReadU16(data, address);
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

    private static void ReadFormat4(byte[] data, int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 14 > data.Length)
        {
            return;
        }

        int segX2 = TrueTypeOutlines.ReadU16(data, sub + 6);
        int segments = segX2 / 2;
        int ends = sub + 14;
        int starts = ends + segX2 + 2;
        int deltas = starts + segX2;
        int ranges = deltas + segX2;
        for (int s = 0; s < segments; s++)
        {
            if (ends + (s * 2) + 2 > data.Length || starts + (s * 2) + 2 > data.Length)
            {
                break;
            }

            int last = TrueTypeOutlines.ReadU16(data, ends + (s * 2));
            int first = TrueTypeOutlines.ReadU16(data, starts + (s * 2));
            short delta = (short)TrueTypeOutlines.ReadU16(data, deltas + (s * 2));
            int rangeOffset = TrueTypeOutlines.ReadU16(data, ranges + (s * 2));
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
                    if (address + 2 > data.Length)
                    {
                        break;
                    }

                    int raw = TrueTypeOutlines.ReadU16(data, address);
                    glyph = raw == 0 ? (ushort)0 : (ushort)((raw + delta) & 0xffff);
                }

                if (glyph != 0)
                {
                    map.TryAdd((uint)code, glyph);
                }
            }
        }
    }

    private static void ReadFormat6(byte[] data, int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 10 > data.Length)
        {
            return;
        }

        int first = TrueTypeOutlines.ReadU16(data, sub + 6);
        int count = TrueTypeOutlines.ReadU16(data, sub + 8);
        for (int i = 0; i < count; i++)
        {
            int address = sub + 10 + (i * 2);
            if (address + 2 > data.Length)
            {
                break;
            }

            ushort glyph = (ushort)TrueTypeOutlines.ReadU16(data, address);
            if (glyph != 0)
            {
                map.TryAdd((uint)(first + i), glyph);
            }
        }
    }

    private static void ReadFormat12(byte[] data, int sub, Dictionary<uint, ushort> map)
    {
        if (sub + 16 > data.Length)
        {
            return;
        }

        int groups = (int)TrueTypeOutlines.ReadU32(data, sub + 12);
        for (int g = 0; g < groups; g++)
        {
            int record = sub + 16 + (g * 12);
            if (record + 12 > data.Length)
            {
                break;
            }

            uint start = TrueTypeOutlines.ReadU32(data, record);
            uint end = TrueTypeOutlines.ReadU32(data, record + 4);
            uint glyphStart = TrueTypeOutlines.ReadU32(data, record + 8);
            for (uint code = start; code <= end && code - start < 65536; code++)
            {
                if (glyphStart + (code - start) <= ushort.MaxValue)
                {
                    map.TryAdd(code, (ushort)(glyphStart + (code - start)));
                }
            }
        }
    }
}
