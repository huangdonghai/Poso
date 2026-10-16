using System.Buffers.Binary;
using System.Text;
using Poso.Graphics;

namespace Poso.PeachDrawing;

/// <summary>
/// PeachDrawing implementation of <see cref="IFont"/>: embedded font programs load through
/// <c>FontSet.AddData</c>; the standard-14 names fall back to system families. Glyph outlines come from
/// <c>Typeface.TryGetOutline</c> in design units (y-up, baseline origin - already the <see cref="IFont"/>
/// orientation) and are normalised to em units by dividing by units-per-em.
/// </summary>
public sealed class PeachFont : IFont
{
    // One FontSet serves the whole process; PeachDrawing's font database is not documented as
    // thread-safe, and the render pipeline runs several pages concurrently, so every access is serialised.
    internal static readonly PeachText.FontSet SharedFontSet = new();
    internal static readonly object FontSetLock = new();

    private readonly PeachText.Typeface _typeface;
    private readonly int _unitsPerEm;

    private PeachFont(PeachText.Typeface typeface, int unitsPerEm)
    {
        _typeface = typeface;
        _unitsPerEm = unitsPerEm;
    }

    /// <summary>
    /// Probes the lazily-initialized metrics and wraps the typeface. PeachDrawing loads font tables on
    /// first access and some malformed embedded programs throw (NRE inside its OpenType descriptor);
    /// such a font must fail here, at load, so the caller can fall back instead of crashing mid-render.
    /// </summary>
    private static PeachFont? From(PeachText.Typeface typeface)
    {
        try
        {
            int unitsPerEm = typeface.Metrics.UnitsPerEm;
            return unitsPerEm > 0 ? new PeachFont(typeface, unitsPerEm) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    public static PeachFont? Create(ReadOnlySpan<byte> fontData, string baseFontName)
    {
        if (!fontData.IsEmpty)
        {
            PeachFont? embedded = LoadEmbedded(fontData);
            if (embedded is not null)
            {
                return embedded;
            }
        }

        return CreateFallback(baseFontName);
    }

    private static PeachFont? LoadEmbedded(ReadOnlySpan<byte> fontData)
    {
        // Copy: FontSet keeps the memory alive and the caller's span may be a temporary.
        byte[] data = fontData.ToArray();
        string familyName = "poso-embedded-" + Guid.NewGuid().ToString("n");
        byte[]? repaired = RepairSubset(data, familyName);
        if (repaired is not null)
        {
            data = repaired;
        }

        try
        {
            lock (FontSetLock)
            {
                SharedFontSet.AddData(
                    data,
                    new PeachText.AddOptions { FamilyName = familyName });
                if (!SharedFontSet.TryFindFamily(familyName, out PeachText.TypefaceFamily? family) || family is null)
                {
                    return null;
                }

                if (!family.TryMatch(default, out PeachText.TypefaceMatch match))
                {
                    return null;
                }

                return From(match.Typeface);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A malformed embedded program (or one PeachDrawing cannot build a descriptor for):
            // the caller falls back to the name-based system lookup rather than losing the page.
            return null;
        }
    }

    /// <summary>
    /// Prepares a CID-keyed TrueType subset for PeachDrawing.Text, which needs two things a PDF subset
    /// routinely lacks:
    ///
    /// <list type="bullet">
    /// <item>It reads the cmap while building a typeface's metrics and throws when there is none, which
    /// loses the font and substitutes a system family. Subsets addressed purely by glyph ID are exactly
    /// that shape - every CID-keyed PDF font with an Identity CIDToGIDMap, which is how CJK body text
    /// arrives - so give those an empty cmap: nothing becomes reachable by character that was not
    /// already, and the renderer only ever asks for glyph IDs.</item>
    /// <item>It interns typefaces by the identity strings in the name table. Subsetting tools often leave
    /// that table with a single empty uniqueID record, so every such font in a document collapses onto the
    /// first one registered and the rest of the text renders as .notdef boxes. Give those an identity
    /// derived from the unique family name the caller generated.</item>
    /// </list>
    ///
    /// Returns null when the program needs neither repair.
    /// </summary>
    internal static byte[]? RepairSubset(byte[] data, string identity)
    {
        // 0x00010000 is the TrueType (glyf) sfnt version; 'OTTO' CFF and collection headers are left alone.
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != 0x00010000)
        {
            return null;
        }

        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        if (data.Length < 12 + (tableCount * 16))
        {
            return null;
        }

        bool hasCmap = false;
        bool hasIdentity = false;
        bool hasPost = false;
        var tables = new List<(string Tag, int Offset, int Length)>(tableCount + 1);
        for (int i = 0; i < tableCount; i++)
        {
            int entry = 12 + (i * 16);
            string tag = Encoding.ASCII.GetString(data, entry, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 12));
            switch (tag)
            {
                case "cmap":
                    hasCmap = true;
                    break;
                case "name":
                    hasIdentity = HasIdentity(data, offset, length);
                    break;
                case "post":
                    hasPost = true;
                    break;
            }

            tables.Add((tag, offset, length));
        }

        if (hasCmap && hasIdentity && hasPost)
        {
            return null;
        }

        var synthetic = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!hasCmap)
        {
            synthetic["cmap"] = BuildEmptyCmap();
            tables.Add(("cmap", 0, synthetic["cmap"].Length));
        }

        if (!hasPost)
        {
            synthetic["post"] = BuildEmptyPost();
            tables.Add(("post", 0, synthetic["post"].Length));
        }

        if (!hasIdentity)
        {
            byte[] name = BuildNameTable(identity);
            if (tables.Exists(t => t.Tag == "name"))
            {
                tables.RemoveAll(t => t.Tag == "name");
            }

            synthetic["name"] = name;
            tables.Add(("name", 0, name.Length));
        }

        tables.Sort((left, right) => string.CompareOrdinal(left.Tag, right.Tag));

        int count = tables.Count;
        int selector = 0;
        while ((1 << (selector + 1)) <= count)
        {
            selector++;
        }

        int[] lengths = tables.Select(t => synthetic.TryGetValue(t.Tag, out byte[]? bytes)
                ? bytes.Length
                : Math.Min(t.Length, data.Length - t.Offset))
            .ToArray();
        int dataStart = 12 + (count * 16);
        int total = dataStart;
        foreach (int length in lengths)
        {
            total += length + Pad(length);
        }

        var output = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)(16 << selector));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)((count * 16) - (16 << selector)));

        int writeOffset = dataStart;
        for (int i = 0; i < count; i++)
        {
            (string tag, int sourceOffset, int _) = tables[i];
            int length = lengths[i];
            int directory = 12 + (i * 16);
            Encoding.ASCII.GetBytes(tag).CopyTo(output, directory);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 8), (uint)writeOffset);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 12), (uint)length);

            if (synthetic.TryGetValue(tag, out byte[]? bytes))
            {
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 4), CheckSum(bytes));
                bytes.CopyTo(output, writeOffset);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 4), CheckSum(data.AsSpan(sourceOffset, length)));
                Array.Copy(data, sourceOffset, output, writeOffset, length);
            }

            writeOffset += length + Pad(length);
        }

        // head.checkSumAdjustment must be recomputed for any sfnt whose layout changed: zero it, sum the
        // whole file and store the difference from the magic constant.
        int headIndex = tables.FindIndex(t => t.Tag == "head");
        if (headIndex >= 0)
        {
            int headOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(12 + (headIndex * 16) + 8));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headOffset + 8), 0u);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headOffset + 8), 0xB105u - CheckSum(output));
        }

        return output;
    }

    /// <summary>
    /// Whether a name table carries a non-empty family (ID 1) or unique ID (ID 3) record - the strings
    /// PeachDrawing internates typefaces by.
    /// </summary>
    private static bool HasIdentity(byte[] data, int tableOffset, int tableLength)
    {
        if (tableLength < 6 || data.Length < tableOffset + 6)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(tableOffset + 2));
        int strings = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(tableOffset + 4));
        for (int i = 0; i < count; i++)
        {
            int record = tableOffset + 6 + (i * 12);
            if (record + 12 > data.Length)
            {
                return false;
            }

            int nameId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 6));
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 8));
            int offset = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 10));
            if (nameId is not (1 or 3) || length == 0 || tableOffset + strings + offset + length > data.Length)
            {
                continue;
            }

            for (int b = 0; b < length; b++)
            {
                if (data[tableOffset + strings + offset + b] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A minimal Windows-platform name table (format 0) naming the font after <paramref name="identity"/>:
    /// family, subfamily, unique ID, full name and PostScript name.
    /// </summary>
    private static byte[] BuildNameTable(string identity)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(identity);
        ReadOnlySpan<(ushort NameId, byte[] Value)> records = new (ushort, byte[])[]
        {
            (1, utf16.ToArray()),
            (2, Encoding.BigEndianUnicode.GetBytes("Regular")),
            (3, utf16.ToArray()),
            (4, utf16.ToArray()),
            (6, utf16.ToArray()),
        };

        int header = 6 + (records.Length * 12);
        int storage = 0;
        foreach ((_, byte[] value) in records)
        {
            storage += value.Length;
        }

        var name = new byte[header + storage];
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(2), (ushort)records.Length);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(4), (ushort)header);

        int write = header;
        for (int i = 0; i < records.Length; i++)
        {
            int entry = 6 + (i * 12);
            BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(entry), 3); // platform: Windows
            BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(entry + 2), 1); // encoding: Unicode BMP
            BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(entry + 4), 0x409); // language: English (US)
            BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(entry + 6), records[i].NameId);
            BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(entry + 8), (ushort)records[i].Value.Length);
            BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(entry + 10), (ushort)(write - header));
            records[i].Value.CopyTo(name, write);
            write += records[i].Value.Length;
        }

        return name;
    }

    /// <summary>
    /// An empty format 4 cmap for platform 3 / encoding 1. Its only segment is the required terminator
    /// (0xFFFF..0xFFFF, idDelta 1, idRangeOffset 0), so it maps nothing; the arrays follow the header
    /// back to back at subtable offsets 14, 16, 18 and 20, and the reserved pad ends the table at 24.
    /// </summary>
    private static byte[] BuildEmptyCmap()
    {
        var cmap = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1); // one subtable
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3); // platform: Windows
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 1); // encoding: Unicode BMP
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12); // subtable offset
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 4); // format
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14), 24); // length
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(18), 2); // segCountX2
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(20), 2); // searchRange
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(26), 0xFFFF); // endCode[0]
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(28), 0xFFFF); // startCode[0]
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(30), 1); // idDelta[0]
        return cmap;
    }

    /// <summary>
    /// A format 3.0 post table with no glyph names: 32 bytes of the fields the descriptor reads. The
    /// descriptor dereferences this table while initializing, so a subset without one loses the font.
    /// </summary>
    private static byte[] BuildEmptyPost()
    {
        var post = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(0), 0x00030000); // version 3.0
        BinaryPrimitives.WriteInt16BigEndian(post.AsSpan(8), -100); // underlinePosition
        BinaryPrimitives.WriteInt16BigEndian(post.AsSpan(10), 50); // underlineThickness
        return post;
    }

    private static int Pad(int length) => (4 - (length % 4)) % 4;

    private static uint CheckSum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int b = 0; b < 4; b++)
            {
                word = (word << 8) | (uint)(i + b < data.Length ? data[i + b] : 0);
            }

            sum += word;
        }

        return sum;
    }

    private static PeachFont? CreateFallback(string baseFontName)
    {
        string family = PeachStandardFonts.MapFamily(baseFontName);
        PeachText.TypefaceQuery query = PeachStandardFonts.MapQuery(baseFontName);

        lock (FontSetLock)
        {
            if (TryMatchFamily(family, query) is { } matched)
            {
                return matched;
            }

            // Broaden progressively: default style, then a generic sans, then anything the set can name.
            if (TryMatchFamily(family, default) is { } plain)
            {
                return plain;
            }

            if (TryMatchFamily("Arial", default) is { } arial)
            {
                return arial;
            }

            foreach (string name in PeachText.FontSet.InstalledFamilyNames)
            {
                if (TryMatchFamily(name, default) is { } first)
                {
                    return first;
                }
            }
        }

        return null;
    }

    private static PeachFont? TryMatchFamily(
        string family,
        in PeachText.TypefaceQuery query)
    {
        if (SharedFontSet.TryFindFamily(family, out PeachText.TypefaceFamily? found) && found is not null
            && found.TryMatch(query, out PeachText.TypefaceMatch match))
        {
            return From(match.Typeface);
        }

        return null;
    }

    public string Name
    {
        get
        {
            try
            {
                return _typeface.FamilyName;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                return "Unknown";
            }
        }
    }

    /// <summary>The PeachDrawing typeface, for the drawing context's glyph runs.</summary>
    internal PeachText.Typeface Typeface => _typeface;

    /// <summary>Units per em, probed at load; always positive.</summary>
    internal int UnitsPerEm => _unitsPerEm;

    public ushort GetGlyphId(uint codePoint)
    {
        if (!Rune.IsValid(codePoint))
        {
            return 0;
        }

        try
        {
            return _typeface.TryMapRune(new Rune((int)codePoint), out ushort glyph) ? glyph : (ushort)0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return 0;
        }
    }

    public float GetAdvanceWidth(ushort glyphId)
    {
        try
        {
            return _typeface.GetAdvance(glyphId) / (float)_unitsPerEm;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return 0f;
        }
    }

    public bool TryGetGlyphOutline(ushort glyphId, IPathBuilder path)
    {
        if (!TryGetOutline(glyphId, out PeachOutlines.GlyphOutline? outline) || outline is null || outline.IsEmpty)
        {
            return false;
        }

        // Outlines arrive in design units, y-up with the baseline at the origin: the IFont orientation
        // already - only the units-per-em normalisation is applied.
        float scale = 1f / _unitsPerEm;
        foreach (PeachOutlines.OutlineContour contour in outline.Contours)
        {
            path.MoveTo((float)contour.Start.X * scale, (float)contour.Start.Y * scale);
            foreach (PeachOutlines.OutlineSegment segment in contour.Segments)
            {
                if (segment.IsCubic)
                {
                    path.CubicTo(
                        (float)segment.Control1.X * scale, (float)segment.Control1.Y * scale,
                        (float)segment.Control2.X * scale, (float)segment.Control2.Y * scale,
                        (float)segment.End.X * scale, (float)segment.End.Y * scale);
                }
                else
                {
                    path.LineTo((float)segment.End.X * scale, (float)segment.End.Y * scale);
                }
            }

            path.Close();
        }

        return true;
    }

    /// <summary>
    /// Outline lookup with a hard guard: a font that passes the load-time metrics probe can still fail
    /// lazily on individual malformed glyphs, and a mid-page throw must degrade to "no ink for this
    /// glyph" rather than lose the whole page.
    /// </summary>
    internal bool TryGetOutline(ushort glyphId, out PeachOutlines.GlyphOutline? outline)
    {
        try
        {
            return _typeface.TryGetOutline(glyphId, out outline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            outline = null;
            return false;
        }
    }

    public void Dispose()
    {
        // Typeface instances live in the shared FontSet for the process lifetime (PDF documents reuse
        // fonts across pages and zoom levels); there is nothing per-instance to release.
    }
}
