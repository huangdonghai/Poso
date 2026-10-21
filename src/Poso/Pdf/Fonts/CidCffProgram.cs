namespace Poso.Pdf.Fonts;

/// <summary>
/// Reader for a CID-keyed CFF program (PDF <c>FontFile3</c> with /Subtype /CIDFontType0C, the form
/// used by embedded CJK/Adobe-*. fonts). Unlike a name-keyed CFF it carries a ROS (registry/ordering/
/// supplement) in the top DICT, splits glyphs across an FDArray/FDSelect, and its charset maps each
/// glyph ID to a CID rather than a string ID.
/// </summary>
/// <remarks>
/// The renderer (Skia/FreeType) parses the FDArray/FDSelect and outline data itself once the raw CFF
/// is wrapped into an OpenType container, so this reader does not re-serialize anything: it only
/// extracts the glyph count and design-space metrics the wrapper needs, plus the CID to glyph-ID map
/// derived from the charset. A Type 0 / Identity font addresses glyphs by CID, but the CFF numbers them
/// by GID in CharStrings order, and the two only coincide when the charset is the identity mapping -
/// which embedded subsets (like this book's fonts) almost never are.
/// </remarks>
internal sealed class CidCffProgram : ICffMetrics
{
    private static readonly float[] DefaultFontMatrix = [0.001f, 0f, 0f, 0f, 0.001f, 0f];

    private CidCffProgram(int glyphCount, float[] fontMatrix, float[] fontBBox, Dictionary<int, ushort> cidToGid)
    {
        GlyphCount = glyphCount;
        FontMatrix = fontMatrix;
        FontBBox = fontBBox;
        CidToGid = cidToGid;
    }

    public int GlyphCount { get; }

    public float[] FontMatrix { get; }

    public float[] FontBBox { get; }

    /// <summary>Maps a CID (from the Type 0 / Identity codes) to the glyph ID to draw, taken from the charset.</summary>
    public Dictionary<int, ushort> CidToGid { get; }

    /// <summary>Parses a CID-keyed CFF; returns false for a name-keyed, encrypted or malformed program.</summary>
    public static bool TryCreate(byte[] data, out CidCffProgram? program)
    {
        program = null;
        if (data is null || data.Length < 22 || data[0] != 1 || data[1] != 0)
        {
            return false;
        }

        // The header's third byte is its size; a CID-keyed subset can carry a longer header than the
        // usual 4 (pdf.js issue13147.pdf writes 12), and a fixed 4 reads the INDEX tables as garbage.
        int position = data[2];
        if (position < 4 || position >= data.Length)
        {
            return false;
        }

        if (!CffIndex.TryRead(data, ref position, out _) ||           // Name
            !CffIndex.TryRead(data, ref position, out CffIndex topIndex) || // Top DICT
            !CffIndex.TryRead(data, ref position, out _) ||           // String
            !CffIndex.TryRead(data, ref position, out _))             // Global subroutines
        {
            return false;
        }

        if (topIndex.Count < 1 || !CffDict.TryParse(topIndex.ReadItem(data, 0), out CffDict? dict) || dict is null)
        {
            return false;
        }

        // Only a CID-keyed program (has ROS, operator 12 30) belongs here; name-keyed/encrypted are rejected.
        if (!dict.Contains((12 << 8) | 30) || dict.Contains(30))
        {
            return false;
        }

        int charStringsOffset = (int)dict.Get(17);
        if (charStringsOffset <= 0 || charStringsOffset >= data.Length ||
            !CffIndex.TryRead(data, ref charStringsOffset, out CffIndex charStrings) ||
            charStrings.Count < 1 || charStrings.Count > ushort.MaxValue)
        {
            return false;
        }

        float[]? matrix = dict.GetArray(7);
        float[]? bbox = dict.GetArray(5);
        Dictionary<int, ushort> cidToGid = ReadCidToGid(data, (int)dict.Get(15), charStrings.Count);

        program = new CidCffProgram(
            charStrings.Count,
            matrix is { Length: >= 4 } ? ExpandMatrix(matrix) : DefaultFontMatrix,
            bbox is { Length: 4 } ? bbox : [0f, 0f, 0f, 0f],
            cidToGid);
        return true;
    }

    private static float[] ExpandMatrix(float[] matrix) => matrix.Length >= 6
        ? [matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]]
        : [matrix[0], matrix[1], 0f, matrix[2], 0f, matrix[3]];

    /// <summary>
    /// Reads the charset (which for a CID font lists a CID per glyph, GID 1..n) and inverts it into a
    /// CID to glyph-ID map. Format 0 is one CID per glyph; formats 1 and 2 are runs of consecutive CIDs.
    /// A missing charset (offset 0) means the predefined charset, i.e. the identity mapping CID == GID.
    /// </summary>
    private static Dictionary<int, ushort> ReadCidToGid(byte[] data, int offset, int glyphCount)
    {
        var map = new Dictionary<int, ushort>(glyphCount);

        if (offset <= 0 || offset >= data.Length)
        {
            for (int g = 1; g < glyphCount && g <= ushort.MaxValue; g++)
            {
                map[g] = (ushort)g;
            }

            return map;
        }

        byte format = data[offset];
        int position = offset + 1;
        int gid = 1;

        while (gid < glyphCount)
        {
            if (position + 1 >= data.Length)
            {
                break;
            }

            int first = (data[position] << 8) | data[position + 1];
            position += 2;

            int runLength;
            if (format == 0)
            {
                map.TryAdd(first, (ushort)gid);
                gid++;
                continue;
            }

            if (format == 1)
            {
                if (position >= data.Length)
                {
                    break;
                }

                runLength = data[position++];
            }
            else
            {
                if (position + 1 >= data.Length)
                {
                    break;
                }

                runLength = (data[position] << 8) | data[position + 1];
                position += 2;
            }

            for (int k = 0; k <= runLength && gid < glyphCount; k++)
            {
                map.TryAdd(first + k, (ushort)gid);
                gid++;
            }
        }

        return map;
    }
}
