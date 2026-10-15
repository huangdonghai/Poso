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
        byte[]? repaired = WithEmptyCmap(data);
        if (repaired is not null)
        {
            data = repaired;
        }

        string familyName = "poso-embedded-" + Guid.NewGuid().ToString("n");
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
    /// PeachDrawing.Text reads the cmap while building a typeface's metrics and throws for a TrueType
    /// program that has none, which loses the font and substitutes a system family. Subsets addressed
    /// purely by glyph ID are exactly that shape - every CID-keyed PDF font with an Identity
    /// CIDToGIDMap, which is how CJK body text arrives - so give those programs an empty cmap. Nothing
    /// becomes reachable by character that was not already, and the renderer only ever asks for glyph
    /// IDs. Returns null when the program needs no repair.
    /// </summary>
    internal static byte[]? WithEmptyCmap(byte[] data)
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

        var tables = new List<(string Tag, uint CheckSum, int Offset, int Length)>(tableCount + 1);
        for (int i = 0; i < tableCount; i++)
        {
            int entry = 12 + (i * 16);
            string tag = Encoding.ASCII.GetString(data, entry, 4);
            if (tag == "cmap")
            {
                return null;
            }

            tables.Add((
                tag,
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 12))));
        }

        byte[] cmap = BuildEmptyCmap();
        tables.Add(("cmap", CheckSum(cmap), 0, cmap.Length));
        tables.Sort((left, right) => string.CompareOrdinal(left.Tag, right.Tag));

        int count = tables.Count;
        int selector = 0;
        while ((1 << (selector + 1)) <= count)
        {
            selector++;
        }

        int[] lengths = tables.Select(t => t.Tag == "cmap" ? t.Length : Math.Min(t.Length, data.Length - t.Offset)).ToArray();
        int dataStart = 12 + (count * 16);
        int total = dataStart;
        foreach (int length in lengths)
        {
            total += length + (Pad(length));
        }

        var output = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)(16 << selector));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)((count * 16) - (16 << selector)));

        int offset = dataStart;
        for (int i = 0; i < count; i++)
        {
            (string tag, uint checkSum, int sourceOffset, int _) = tables[i];
            int length = lengths[i];
            int directory = 12 + (i * 16);
            Encoding.ASCII.GetBytes(tag).CopyTo(output, directory);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 4), tag == "cmap" ? checkSum : CheckSum(data.AsSpan(sourceOffset, length)));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(directory + 12), (uint)length);

            if (tag == "cmap")
            {
                cmap.CopyTo(output, offset);
            }
            else
            {
                Array.Copy(data, sourceOffset, output, offset, length);
            }

            offset += length + Pad(length);
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

    /// <summary>An empty format 4 cmap (one 0xFFFF segment) for platform 3 / encoding 1.</summary>
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
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(26), 0xFFFF); // endCode
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(30), 0xFFFF); // startCode
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(32), 1); // idDelta
        return cmap;
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
