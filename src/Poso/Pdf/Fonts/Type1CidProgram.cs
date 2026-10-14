using System.Collections.Generic;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Reader for a CID-keyed binary Type 1 font program (Adobe Technical Note 5014, the program behind a
/// PDF <c>CIDFontType0</c> whose <c>FontFile</c> is a raw Type 1 program rather than a CFF). It parses the
/// cleartext header (CIDCount / CIDMapOffset / FDBytes / GDBytes / SubrMap / lenIV), reads the binary block
/// that follows the <c>StartData</c> marker (CIDMap offsets, optional SubrMap and the charstring data), and
/// converts every charstring to a Type 2 (CFF) charstring - reusing <see cref="Type1Program.Type1CharString"/>.
/// Glyphs are named <c>cidN</c> in CID order so the resulting CFF maps GID == CID under the Identity mapping.
/// The algorithm mirrors pdf.js's <c>type1_parser.js#extractCidKeyedFontProgram</c>.
/// </summary>
/// <remarks>
/// Only single-FDArray programs are supported: any CID selecting a non-zero FD index is rejected (each FD can
/// carry different private data and subroutines), exactly as pdf.js does. CID-keyed programs whose Private /
/// FDArray live inside an <c>eexec</c> block are not handled here and degrade to no embedded font.
/// </remarks>
internal sealed class Type1CidProgram : IType1Program
{
    public float[] FontMatrix { get; }
    public float[] FontBBox { get; }
    public IReadOnlyList<Type1Glyph> Glyphs { get; }

    private Type1CidProgram(float[] fontMatrix, float[] fontBBox, List<Type1Glyph> glyphs)
    {
        FontMatrix = fontMatrix;
        FontBBox = fontBBox;
        Glyphs = glyphs;
    }

    /// <summary>True when a Type 1 program's cleartext header marks it as CID-keyed (Adobe TN 5014).</summary>
    public static bool IsCidKeyedProgram(byte[] data)
    {
        int limit = System.Math.Min(2048, data.Length);
        return ContainsAscii(data, limit, "Resource-CIDFont") || ContainsAscii(data, limit, "/CIDFontType 0");
    }

    private static bool ContainsAscii(byte[] data, int limit, string text)
    {
        int n = text.Length;
        for (int i = 0; i + n <= limit; i++)
        {
            bool hit = true;
            for (int j = 0; j < n; j++)
            {
                if (data[i + j] != (byte)text[j])
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a CID-keyed Type 1 program; false when it is not CID-keyed, malformed or multi-FD.</summary>
    public static bool TryCreate(byte[] data, out Type1CidProgram? program)
    {
        program = null;
        if (data is not { Length: > 32 })
        {
            return false;
        }

        var stream = new Type1Program.PsStream(data);
        float[] fontMatrix = [0.001f, 0f, 0f, 0.001f, 0f, 0f];
        float[] fontBBox = [0f, 0f, 0f, 0f];

        int cidCount = 0;
        int cidMapOffset = -1;
        int fdBytes = 1;
        int gdBytes = 0;
        int subrMapOffset = -1;
        int sdBytes = 0;
        int subrCount = 0;
        int lenIV = 4;
        int startDataLength = 0;
        bool startDataIsHex = false;
        bool foundStartData = false;

        var previous = new List<string>();
        void Remember(string? value)
        {
            if (value is not null)
            {
                previous.Add(value);
                if (previous.Count > 4)
                {
                    previous.RemoveAt(0);
                }
            }
        }

        string? token;
        while ((token = stream.GetToken()) is not null)
        {
            if (token == "StartData")
            {
                if (previous.Count < 4
                    || previous[^4] != "("
                    || previous[^2] != ")"
                    || (previous[^3] != "Binary" && previous[^3] != "Hex")
                    || !IsAllDigits(previous[^1]))
                {
                    return false;
                }

                startDataLength = int.Parse(previous[^1], System.Globalization.CultureInfo.InvariantCulture);
                if (startDataLength <= 0)
                {
                    return false;
                }

                startDataIsHex = previous[^3] == "Hex";
                foundStartData = true;
                break;
            }

            Remember(token);
            if (token != "/")
            {
                continue;
            }

            token = stream.GetToken();
            Remember(token);
            switch (token)
            {
                case "FontMatrix":
                    ReadInto(stream.ReadNumberArray(), fontMatrix);
                    break;
                case "FontBBox":
                    ReadInto(stream.ReadNumberArray(), fontBBox);
                    break;
                case "CIDCount":
                    cidCount = stream.ReadInt();
                    break;
                case "CIDMapOffset":
                    cidMapOffset = stream.ReadInt();
                    break;
                case "FDBytes":
                    fdBytes = stream.ReadInt();
                    break;
                case "GDBytes":
                    gdBytes = stream.ReadInt();
                    break;
                case "SubrMapOffset":
                    subrMapOffset = stream.ReadInt();
                    break;
                case "SDBytes":
                    sdBytes = stream.ReadInt();
                    break;
                case "SubrCount":
                    subrCount = stream.ReadInt();
                    break;
                case "BlueValues" or "OtherBlues" or "FamilyBlues" or "FamilyOtherBlues"
                    or "StemSnapH" or "StemSnapV" or "StdHW" or "StdVW":
                    _ = stream.ReadNumberArray();
                    break;
                case "BlueShift" or "lenIV" or "BlueFuzz" or "BlueScale" or "LanguageGroup" or "ExpansionFactor":
                    if (token == "lenIV")
                    {
                        lenIV = (int)stream.ReadNumber();
                    }
                    else
                    {
                        _ = stream.ReadNumber();
                    }

                    break;
                case "ForceBold":
                    _ = stream.GetToken();
                    break;
            }
        }

        if (!foundStartData || cidCount <= 0 || cidMapOffset < 0 || fdBytes is < 0 or > 4 || gdBytes is < 1 or > 4)
        {
            return false;
        }

        // "startDataLength" is only an upper bound: some generators write a wrong value and the buffer is
        // untrusted input, so cap it to what remains before allocating.
        int maxLength = stream.Remaining;
        if (startDataLength > maxLength)
        {
            if (!startDataIsHex)
            {
                startDataLength = maxLength;
            }
            else if (startDataLength > 2 * maxLength)
            {
                return false;
            }
        }

        byte[] block = stream.GetBytes(startDataIsHex ? maxLength : startDataLength);
        if (startDataIsHex)
        {
            byte[]? decoded = DecodeHex(block, startDataLength);
            if (decoded is null)
            {
                return false;
            }

            block = decoded;
        }

        byte[] binary = block;
        int cidEntrySize = fdBytes + gdBytes;
        if (cidMapOffset + (cidCount + 1) * cidEntrySize > binary.Length
            || (subrCount > 0 && (subrMapOffset < 0 || sdBytes is < 1 or > 4 || subrMapOffset + (subrCount + 1) * sdBytes > binary.Length)))
        {
            return false;
        }

        if (fdBytes > 0)
        {
            for (int cid = 0; cid < cidCount; cid++)
            {
                if (ReadUInt(binary, cidMapOffset + (cid * cidEntrySize), fdBytes) != 0)
                {
                    return false; // multi-FD font: unsupported
                }
            }
        }

        var subrs = new List<byte[]>();
        if (subrCount > 0)
        {
            var offsets = new uint[subrCount + 1];
            for (int i = 0; i <= subrCount; i++)
            {
                offsets[i] = ReadUInt(binary, subrMapOffset + (i * sdBytes), sdBytes);
            }

            for (int i = 0; i < subrCount; i++)
            {
                int start = (int)offsets[i];
                int end = (int)offsets[i + 1];
                if (end > binary.Length || end < start)
                {
                    subrs.Add(System.Array.Empty<byte>());
                    continue;
                }

                subrs.Add(ReadCharString(Slice(binary, start, end - start), lenIV));
            }
        }

        var glyphs = new List<Type1Glyph>(cidCount);
        uint prevOffset = ReadUInt(binary, cidMapOffset + fdBytes, gdBytes);
        for (int cid = 0; cid < cidCount; cid++)
        {
            uint nextOffset = ReadUInt(binary, cidMapOffset + ((cid + 1) * cidEntrySize) + fdBytes, gdBytes);
            string glyphName = cid == 0 ? ".notdef" : $"cid{cid}";
            if (nextOffset > prevOffset && nextOffset <= binary.Length)
            {
                byte[] encoded = ReadCharString(Slice(binary, (int)prevOffset, (int)(nextOffset - prevOffset)), lenIV);
                var cs = new Type1Program.Type1CharString();
                bool error = cs.Convert(encoded, subrs);
                glyphs.Add(new Type1Glyph
                {
                    Name = glyphName,
                    Charstring = error ? [14] : cs.Output.ToArray(),
                    Width = (int)cs.Width,
                });
            }
            else
            {
                // Empty intervals reuse CID 0's notdef glyph while keeping the CID -> GID slots aligned.
                Type1Glyph notDef = glyphs.Count > 0 ? glyphs[0] : new Type1Glyph { Name = ".notdef", Charstring = [0x8b, 0x0e], Width = 0 };
                glyphs.Add(new Type1Glyph
                {
                    Name = glyphName,
                    Charstring = (byte[])notDef.Charstring.Clone(),
                    Width = notDef.Width,
                });
            }

            prevOffset = nextOffset;
        }

        if (glyphs.Count == 0)
        {
            return false;
        }

        program = new Type1CidProgram(fontMatrix, fontBBox, glyphs);
        return true;
    }

    // ---- byte helpers ----

    private static byte[] ReadCharString(byte[] bytes, int lenIV) =>
        lenIV < 0 ? bytes : Type1Program.Decrypt(bytes, Type1Program.CharStringsKey, lenIV);

    private static byte[] Slice(byte[] data, int start, int length)
    {
        length = System.Math.Min(length, data.Length - start);
        if (start < 0 || length <= 0)
        {
            return System.Array.Empty<byte>();
        }

        var slice = new byte[length];
        System.Array.Copy(data, start, slice, 0, length);
        return slice;
    }

    private static uint ReadUInt(byte[] data, int offset, int byteCount)
    {
        uint value = 0;
        for (int i = 0; i < byteCount; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }

    private static byte[]? DecodeHex(byte[] data, int expectedLength)
    {
        var decoded = new byte[expectedLength];
        int j = 0;
        int high = -1;
        for (int i = 0; i < data.Length && j < expectedLength; i++)
        {
            int digit = HexValue(data[i]);
            if (digit < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = digit;
                continue;
            }

            decoded[j++] = (byte)((high << 4) | digit);
            high = -1;
        }

        return j == expectedLength ? decoded : null;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };

    private static void ReadInto(float[] source, float[] target)
    {
        for (int i = 0; i < target.Length && i < source.Length; i++)
        {
            target[i] = source[i];
        }
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (c is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }
}
