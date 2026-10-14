namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// JBIG2 (ITU-T T.88 Annex B) Huffman decoder: the MSB-first, big-endian 32-bit word bit-reader, the
/// canonical-table builder (B.3) and the 15 standard tables (B.1..B.15), ported faithfully from the
/// reference implementation <c>jbig2_huffman.c</c> / <c>jbig2_hufftab.c</c> (Artifex jbig2dec — the upstream
/// of MuPDF). Region decoders use it to code symbol/instance deltas; symbol bitmaps themselves are still
/// arithmetic/MMR coded. Also decodes a user-supplied code-table segment (type 53, §7.4.2.1.5 / B.2).
/// </summary>
internal static class Jbig2Huffman
{
    private const int LogTableSizeMax = 16;

    // Entry flags (self-consistent with Build; mirrors jbig2_huffman.h ISLOW/ISEXT/ISOOB semantics).
    private const byte FlagIsLow = 1;   // range applies downward (result -= HTOFFSET)
    private const byte FlagIsExt = 2;   // extension table (unused by jbig2dec's flat build)
    private const byte FlagIsOob = 4;   // this entry is the OOB code

    // Line matches C's { PREFLEN, RANGELEN, RANGELOW } field order used in the table literals below.
    internal readonly struct Line(int preflen, int rangelen, int rangelow)
    {
        internal readonly int Preflen = preflen;
        internal readonly int Rangelen = rangelen;
        internal readonly int Rangelow = rangelow;
    }

    internal sealed class Entry
    {
        internal int Preflen = -1;   // -1 == unpopulated
        internal int Rangelen;
        internal int Rangelow;
        internal byte Flags = 0xFF;  // 0xFF == unpopulated sentinel
    }

    /// <summary>A built Huffman lookup table (flat, indexed by the top LogTableSize bits).</summary>
    internal sealed class Table
    {
        internal Entry[] Entries = [];
        internal int LogTableSize;
    }

    // ---- Standard tables (jbig2_hufftab.c). Built lazily; see StandardLines() for the B.1..B.15 data. ----
    internal static Table Build(Line[] lines, bool hasOob)
    {
        int nLines = lines.Length;
        int lenMax = -1;
        int[] lenCount = new int[256];
        int logTableSize = 0;

        // B.3 step 1/2: histogram code lengths and pick the lookup depth.
        for (int i = 0; i < nLines; i++)
        {
            int preflen = lines[i].Preflen;
            if (preflen > lenMax)
            {
                for (int j = lenMax + 1; j < preflen + 1; j++)
                {
                    lenCount[j] = 0;
                }
                lenMax = preflen;
            }
            lenCount[preflen]++;

            int lts = preflen + lines[i].Rangelen;
            if (lts > LogTableSizeMax)
            {
                lts = preflen;
            }
            if (lts <= LogTableSizeMax && logTableSize < lts)
            {
                logTableSize = lts;
            }
        }

        uint maxJ = 1u << logTableSize;
        Table result = new() { LogTableSize = logTableSize, Entries = new Entry[maxJ] };
        for (int i = 0; i < maxJ; i++)
        {
            result.Entries[i] = new Entry();
        }

        lenCount[0] = 0;
        int firstCode = 0;
        for (int curLen = 1; curLen <= lenMax; curLen++)
        {
            int shift = logTableSize - curLen;
            firstCode = (firstCode + lenCount[curLen - 1]) << 1; // B.3 3(a)
            int curCode = firstCode;
            for (int curTemp = 0; curTemp < nLines; curTemp++) // B.3 3(b)
            {
                if (lines[curTemp].Preflen != curLen)
                {
                    continue;
                }

                int rangeLen = lines[curTemp].Rangelen;
                uint startJ = (uint)curCode << shift;
                uint endJ = (uint)(curCode + 1) << shift;
                byte eflags = 0;
                if (hasOob && curTemp == nLines - 1)
                {
                    eflags |= FlagIsOob;
                }
                if (curTemp == nLines - (hasOob ? 3 : 2))
                {
                    eflags |= FlagIsLow;
                }

                if (curLen + rangeLen > LogTableSizeMax)
                {
                    for (uint j = startJ; j < endJ; j++)
                    {
                        Entry e = result.Entries[j];
                        e.Rangelow = lines[curTemp].Rangelow;
                        e.Preflen = curLen;
                        e.Rangelen = rangeLen;
                        e.Flags = eflags;
                    }
                }
                else
                {
                    for (uint j = startJ; j < endJ; j++)
                    {
                        int htOffset = (int)((j >> (shift - rangeLen)) & ((1 << rangeLen) - 1));
                        Entry e = result.Entries[j];
                        e.Rangelow = (eflags & FlagIsLow) != 0
                            ? lines[curTemp].Rangelow - htOffset
                            : lines[curTemp].Rangelow + htOffset;
                        e.Preflen = curLen + rangeLen;
                        e.Rangelen = 0;
                        e.Flags = (byte)(eflags & ~FlagIsLow); // folded: no runtime range
                        if ((eflags & FlagIsOob) != 0)
                        {
                            e.Flags |= FlagIsOob;
                        }
                    }
                }
                curCode++;
            }
        }
        return result;
    }

    /// <summary>
    /// Decoding state over a byte slice: two overlapped 32-bit big-endian words advanced MSB-first. Ported
    /// from <c>Jbig2HuffmanState</c> + <c>jbig2_huffman_get/skip/advance/get_bits</c>.
    /// </summary>
    internal sealed class State
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _bitpos;   // absolute bit index into _data (start-relative at construction)

        internal State(byte[] data, int start, int end)
        {
            _data = data;
            _end = end;
            _bitpos = start * 8;
        }

        /// <summary>Peek <paramref name="count"/> (0..32) bits MSB-first at the cursor, zero-filled beyond the end.</summary>
        private uint Peek(int count)
        {
            uint val = 0;
            int pos = _bitpos;
            for (int i = 0; i < count; i++)
            {
                val <<= 1;
                int byteIndex = (pos + i) >> 3;
                if (byteIndex < _end)
                {
                    val |= (uint)((_data[byteIndex] >> (7 - ((pos + i) & 7))) & 1);
                }
            }
            return val;
        }

        /// <summary>Returns the current byte offset of the decode pointer (for reading raw MMR/uncompressed data).</summary>
        internal int Offset() => _bitpos >> 3;

        /// <summary>Decode one symbol from <paramref name="table"/>. code: 0 ok, 1 OOB, -1 error.</summary>
        internal int Get(Table table, out int code)
        {
            code = 0;
            int lts = table.LogTableSize;
            Entry entry = table.Entries[lts > 0 ? (int)Peek(lts) : 0];
            if (entry.Preflen < 0 || entry.Flags == 0xFF)
            {
                code = -1;
                return 0;
            }
            _bitpos += entry.Preflen;
            int result = entry.Rangelow;
            if (entry.Rangelen > 0)
            {
                int htOffset = (int)Peek(entry.Rangelen);
                _bitpos += entry.Rangelen;
                result = (entry.Flags & FlagIsLow) != 0 ? result - htOffset : result + htOffset;
            }
            if ((entry.Flags & FlagIsOob) != 0)
            {
                code = 1;
            }
            return result;
        }

        /// <summary>Read <paramref name="bits"/> raw bits MSB-first. code: 0 ok (always).</summary>
        internal int GetBits(int bits, out int code)
        {
            code = 0;
            if (bits == 0)
            {
                return 0;
            }
            int result = (int)Peek(bits);
            _bitpos += bits;
            return result;
        }

        /// <summary>Discard bits up to the next byte boundary (used before reading raw MMR data).</summary>
        internal void SkipToByteBoundary()
        {
            _bitpos = (_bitpos + 7) & ~7;
        }

        /// <summary>Advance the decode pointer by a whole number of bytes (byte-aligned) after raw reads.</summary>
        internal void Advance(int bytes)
        {
            _bitpos += bytes * 8;
        }
    }

    // ---- Standard table accessors (Annex B.1..B.15). Built lazily and cached. ----
    private static readonly Table?[] Cache = new Table?[16];

    /// <summary>Returns the standard table for Annex B figure number 1..15 (cached).</summary>
    internal static Table Standard(int bfigure)
    {
        Table? t = Cache[bfigure];
        if (t is not null)
        {
            return t;
        }
        t = Build(StandardLines(bfigure, out bool oob), oob);
        Cache[bfigure] = t;
        return t;
    }

    /// <summary>
    /// Decodes a user-supplied code-table segment (§7.4.2.1.5 / Annex B.2) and returns the built table.
    /// Reads HTLOW/HTHIGH bounds and the packed (HTPS-bit prefix, HTRS-bit range) line stream, then appends
    /// the low/high sentinel lines (RANGELEN=32) and, if HTOOB, an OOB line — mirroring <c>jbig2_table</c>.
    /// Returns null on malformed input.
    /// </summary>
    internal static Table? BuildCustom(byte[] data, int bodyStart, int bodyEnd)
    {
        if (bodyEnd - bodyStart < 10)
        {
            return null;
        }

        int flags = data[bodyStart];
        int htOob = flags & 0x01;
        int htps = ((flags >> 1) & 0x07) + 1;
        int htrs = ((flags >> 4) & 0x07) + 1;
        int htLow = (int)ReadUInt32(data, bodyStart + 1);
        int htHigh = (int)ReadUInt32(data, bodyStart + 5);
        if (htLow >= htHigh)
        {
            return null;
        }

        int dataLen = bodyEnd - bodyStart;
        long linesMax = (long)((dataLen * 8L - htps * (htOob != 0 ? 3 : 2)) / (htps + htrs)) + (htOob != 0 ? 3 : 2);
        if (linesMax <= 0 || linesMax > 1000000)
        {
            return null;
        }

        Line[] lines = new Line[linesMax];
        int bitOffset = 9 * 8; // lines_data begins at bodyStart + 9
        int bitLen = dataLen * 8;
        long curRangeLow = htLow;
        int nTemp = 0;

        while (curRangeLow < htHigh)
        {
            if (bitOffset + htps >= bitLen || nTemp >= linesMax)
            {
                return null;
            }
            int preflen = (int)ReadBits(data, bodyStart, bitLen, ref bitOffset, htps);
            if (bitOffset + htrs >= bitLen)
            {
                return null;
            }
            int rangeLen = (int)ReadBits(data, bodyStart, bitLen, ref bitOffset, htrs);
            lines[nTemp++] = new Line(preflen, rangeLen, (int)curRangeLow);
            curRangeLow += 1L << rangeLen;
        }

        // low sentinel line, high sentinel line, optional OOB line
        if (bitOffset + htps >= bitLen || nTemp >= linesMax)
        {
            return null;
        }
        int loLen = (int)ReadBits(data, bodyStart, bitLen, ref bitOffset, htps);
        lines[nTemp++] = new Line(loLen, 32, htLow - 1);
        if (bitOffset + htps >= bitLen || nTemp >= linesMax)
        {
            return null;
        }
        int hiLen = (int)ReadBits(data, bodyStart, bitLen, ref bitOffset, htps);
        lines[nTemp++] = new Line(hiLen, 32, htHigh);
        if (htOob != 0)
        {
            if (bitOffset + htps >= bitLen || nTemp >= linesMax)
            {
                return null;
            }
            int oobLen = (int)ReadBits(data, bodyStart, bitLen, ref bitOffset, htps);
            lines[nTemp++] = new Line(oobLen, 0, 0);
        }

        return Build(lines[..nTemp], htOob != 0);
    }

    private static uint ReadBits(byte[] data, int baseOffset, int bitLen, ref int bitOffset, int count)
    {
        uint val = 0;
        for (int i = 0; i < count; i++)
        {
            val <<= 1;
            int bi = bitOffset + i;
            if (bi < bitLen)
            {
                int byteIndex = baseOffset + (bi >> 3);
                val |= (uint)((data[byteIndex] >> (7 - (bi & 7))) & 1);
            }
        }
        bitOffset += count;
        return val;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static Line[] StandardLines(int b, out bool hasOob)
    {
        hasOob = false;
        switch (b)
        {
            case 1: return [new(1, 4, 0), new(2, 8, 16), new(3, 16, 272), new(0, -1, 32), new(3, 32, 65808)]; // A
            case 2: hasOob = true; return [new(1, 0, 0), new(2, 0, 1), new(3, 0, 2), new(4, 3, 3), new(5, 6, 11), new(0, 32, -1), new(6, 32, 75), new(6, 0, 0)]; // B
            case 3: hasOob = true; return [new(8, 8, -256), new(1, 0, 0), new(2, 0, 1), new(3, 0, 2), new(4, 3, 3), new(5, 6, 11), new(8, 32, -257), new(7, 32, 75), new(6, 0, 0)]; // C
            case 4: return [new(1, 0, 1), new(2, 0, 2), new(3, 0, 3), new(4, 3, 4), new(5, 6, 12), new(0, 32, -1), new(5, 32, 76)]; // D
            case 5: return [new(7, 8, -255), new(1, 0, 1), new(2, 0, 2), new(3, 0, 3), new(4, 3, 4), new(5, 6, 12), new(7, 32, -256), new(6, 32, 76)]; // E
            case 6: return [new(5, 10, -2048), new(4, 9, -1024), new(4, 8, -512), new(4, 7, -256), new(5, 6, -128), new(5, 5, -64), new(4, 5, -32), new(2, 7, 0), new(3, 7, 128), new(3, 8, 256), new(4, 9, 512), new(4, 10, 1024), new(6, 32, -2049), new(6, 32, 2048)]; // F
            case 7: return [new(4, 9, -1024), new(3, 8, -512), new(4, 7, -256), new(5, 6, -128), new(5, 5, -64), new(4, 5, -32), new(4, 5, 0), new(5, 5, 32), new(5, 6, 64), new(4, 7, 128), new(3, 8, 256), new(3, 9, 512), new(3, 10, 1024), new(5, 32, -1025), new(5, 32, 2048)]; // G
            case 8: hasOob = true; return [new(8, 3, -15), new(9, 1, -7), new(8, 1, -5), new(9, 0, -3), new(7, 0, -2), new(4, 0, -1), new(2, 1, 0), new(5, 0, 2), new(6, 0, 3), new(3, 4, 4), new(6, 1, 20), new(4, 4, 22), new(4, 5, 38), new(5, 6, 70), new(5, 7, 134), new(6, 7, 262), new(7, 8, 390), new(6, 10, 646), new(9, 32, -16), new(9, 32, 1670), new(2, 0, 0)]; // H
            case 9: hasOob = true; return [new(8, 4, -31), new(9, 2, -15), new(8, 2, -11), new(9, 1, -7), new(7, 1, -5), new(4, 1, -3), new(3, 1, -1), new(3, 1, 1), new(5, 1, 3), new(6, 1, 5), new(3, 5, 7), new(6, 2, 39), new(4, 5, 43), new(4, 6, 75), new(5, 7, 139), new(5, 8, 267), new(6, 8, 523), new(7, 9, 779), new(6, 11, 1291), new(9, 32, -32), new(9, 32, 3339), new(2, 0, 0)]; // I
            case 10: hasOob = true; return [new(7, 4, -21), new(8, 0, -5), new(7, 0, -4), new(5, 0, -3), new(2, 2, -2), new(5, 0, 2), new(6, 0, 3), new(7, 0, 4), new(8, 0, 5), new(2, 6, 6), new(5, 5, 70), new(6, 5, 102), new(6, 6, 134), new(6, 7, 198), new(6, 8, 326), new(6, 9, 582), new(6, 10, 1094), new(7, 11, 2118), new(8, 32, -22), new(8, 32, 4166), new(2, 0, 0)]; // J
            case 11: return [new(1, 0, 1), new(2, 1, 2), new(4, 0, 4), new(4, 1, 5), new(5, 1, 7), new(5, 2, 9), new(6, 2, 13), new(7, 2, 17), new(7, 3, 21), new(7, 4, 29), new(7, 5, 45), new(7, 6, 77), new(0, 32, -1), new(7, 32, 141)]; // K
            case 12: return [new(1, 0, 1), new(2, 0, 2), new(3, 1, 3), new(5, 0, 5), new(5, 1, 6), new(6, 1, 8), new(7, 0, 10), new(7, 1, 11), new(7, 2, 13), new(7, 3, 17), new(7, 4, 25), new(8, 5, 41), new(8, 32, 73), new(0, 32, -1), new(0, 32, 0)]; // L
            case 13: return [new(1, 0, 1), new(3, 0, 2), new(4, 0, 3), new(5, 0, 4), new(4, 1, 5), new(3, 3, 7), new(6, 1, 15), new(6, 2, 17), new(6, 3, 21), new(6, 4, 29), new(6, 5, 45), new(7, 6, 77), new(0, 32, -1), new(7, 32, 141)]; // M
            case 14: return [new(3, 0, -2), new(3, 0, -1), new(1, 0, 0), new(3, 0, 1), new(3, 0, 2), new(0, 32, -1), new(0, 32, 3)]; // N
            case 15: return [new(7, 4, -24), new(6, 2, -8), new(5, 1, -4), new(4, 0, -2), new(3, 0, -1), new(1, 0, 0), new(3, 0, 1), new(4, 0, 2), new(5, 1, 3), new(6, 2, 5), new(7, 4, 9), new(7, 32, -25), new(7, 32, 25)]; // O
            default: hasOob = false; return [];
        }
    }
}
