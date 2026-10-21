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
    // System-family lookups go through this set, which only ever reads the process-wide font database.
    // Embedded programs do NOT: PeachDrawing's FontSet has no removal API, and AddData keeps the
    // program's bytes, family and descriptor alive for as long as the set lives, so registering every
    // document's fonts here pinned them for the life of the process. Each embedded program now loads
    // through a private FontSet (see LoadEmbedded); the lock still serialises all PeachDrawing font
    // access because its internal caches are not documented as thread-safe and the render pipeline
    // runs several pages concurrently.
    internal static readonly PeachText.FontSet SharedFontSet = new();
    internal static readonly object FontSetLock = new();

    private readonly PeachText.Typeface _typeface;
    private PeachText.FontSet? _embeddedSet;
    private readonly int _unitsPerEm;
    private readonly Poso.Pdf.Fonts.CffProgram? _cff;

    private PeachFont(PeachText.Typeface typeface, int unitsPerEm, bool usedFallback, Poso.Pdf.Fonts.CffProgram? cff = null, PeachText.FontSet? embeddedSet = null)
    {
        _typeface = typeface;
        _unitsPerEm = unitsPerEm;
        UsedFallback = usedFallback;
        _cff = cff;
        _embeddedSet = embeddedSet;
    }

    public bool UsedFallback { get; }

    /// <summary>
    /// Probes the lazily-initialized metrics and wraps the typeface. PeachDrawing loads font tables on
    /// first access and some malformed embedded programs throw (NRE inside its OpenType descriptor);
    /// such a font must fail here, at load, so the caller can fall back instead of crashing mid-render.
    /// </summary>
    private static PeachFont? From(PeachText.Typeface typeface, bool usedFallback, Poso.Pdf.Fonts.CffProgram? cff = null, PeachText.FontSet? embeddedSet = null)
    {
        try
        {
            int unitsPerEm = typeface.Metrics.UnitsPerEm;
            if (unitsPerEm <= 0)
            {
                return null;
            }

            // A failed probe must still let the private set go; on success it rides along on the font.
            return new PeachFont(typeface, unitsPerEm, usedFallback, cff, embeddedSet);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The CFF program inside an sfnt container, when there is one. PeachDrawing reads outlines from the
    /// container but does not expose the charstrings, and a seac composite can only be recognised - and
    /// its two parts named - from the charstring itself.
    /// </summary>
    private static Poso.Pdf.Fonts.CffProgram? CffFrom(byte[] sfnt)
    {
        if (sfnt.Length < 12 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sfnt) != 0x4F54544F)
        {
            return null;
        }

        int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        if (sfnt.Length < 12 + (count * 16))
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            if (sfnt[entry] != 'C' || sfnt[entry + 1] != 'F' || sfnt[entry + 2] != 'F' || sfnt[entry + 3] != ' ')
            {
                continue;
            }

            int offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(entry + 8));
            int length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(entry + 12));
            if (offset <= 0 || length <= 0 || sfnt.Length < offset + length)
            {
                return null;
            }

            return Poso.Pdf.Fonts.CffProgram.TryCreate(sfnt[offset..(offset + length)], out Poso.Pdf.Fonts.CffProgram? program)
                ? program
                : null;
        }

        return null;
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

            return CreateFallback(baseFontName, usedFallback: true);
        }

        return CreateFallback(baseFontName, usedFallback: false);
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
                // A set of one's own: the program is registered here and nowhere else, so releasing the
                // font releases the program. A fresh FontSet still sees the process-wide system families
                // (the lookup below is a read), and building one costs nothing measurable.
                var set = new PeachText.FontSet();
                set.AddData(
                    data,
                    new PeachText.AddOptions { FamilyName = familyName });
                if (!set.TryFindFamily(familyName, out PeachText.TypefaceFamily? family) || family is null)
                {
                    return null;
                }

                if (!family.TryMatch(default, out PeachText.TypefaceMatch match))
                {
                    return null;
                }

                return From(match.Typeface, usedFallback: false, CffFrom(data), set);
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
    /// <item>It resolves a face by the family record of the name table and throws when that record is
    /// missing - subsetting tools routinely leave only the PostScript name, or a single empty record -
    /// which loses the font the same way. Give those a name table built from the unique family name the
    /// caller generated.</item>
    /// </list>
    ///
    /// Returns null when the program needs neither repair.
    /// </summary>
    internal static byte[]? RepairSubset(byte[] data, string identity)
    {
        // 0x00010000 is the TrueType (glyf) sfnt version and 'true' (0x74727565) is Apple's spelling of the
        // same layout; 'OTTO' CFF and collection headers are left alone. The back end reads only the first,
        // so a 'true' program is normalised to it (the sfnt header is covered by head.checkSumAdjustment,
        // which this method recomputes whenever it rewrites the file).
        if (data.Length < 12)
        {
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (version is not (0x00010000 or 0x74727565))
        {
            return null;
        }

        bool normaliseVersion = version != 0x00010000;

        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        if (data.Length < 12 + (tableCount * 16))
        {
            return null;
        }

        bool hasUsableCmap = false;
        bool hasIdentity = false;
        bool hasPost = false;
        bool hasOs2 = false;
        int hheaOffset = -1;
        int cmapOffset = -1;
        int cmapLength = 0;
        var tables = new List<(string Tag, int Offset, int Length)>(tableCount + 1);
        for (int i = 0; i < tableCount; i++)
        {
            int entry = 12 + (i * 16);
            string tag = Encoding.ASCII.GetString(data, entry, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 12));

            // A fuzzed header can claim more tables than the directory holds: pdf.js issue3405r.pdf says 9
            // where the ninth "entry" is head's first bytes, with an offset and a length that both run past
            // the end of the program. That entry is not a table - it is dropped instead of being clamped,
            // because clamping its length to the bytes left subtracts more than the file has and the
            // negative total overflows the output allocation.
            if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
            {
                continue;
            }

            switch (tag)
            {
                case "cmap":
                    hasUsableCmap = HasUsableCmapSubtable(data, offset, length);
                    cmapOffset = offset;
                    cmapLength = length;
                    break;
                case "name":
                    hasIdentity = HasIdentity(data, offset, length);
                    break;
                case "post":
                    hasPost = true;
                    break;
                case "OS/2":
                    hasOs2 = true;
                    break;
                case "hhea":
                    hheaOffset = offset;
                    break;
            }

            tables.Add((tag, offset, length));
        }

        if (hasUsableCmap && hasIdentity && hasPost && hasOs2 && !normaliseVersion)
        {
            return null;
        }

        var synthetic = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!hasUsableCmap)
        {
            synthetic["cmap"] = cmapOffset < 0
                ? BuildEmptyCmap()
                : WithUnicodeBmpSubtable(data, cmapOffset, cmapLength);
            tables.RemoveAll(t => t.Tag == "cmap");
            tables.Add(("cmap", 0, synthetic["cmap"].Length));
        }

        if (!hasPost)
        {
            synthetic["post"] = BuildEmptyPost();
            tables.Add(("post", 0, synthetic["post"].Length));
        }

        if (!hasOs2)
        {
            synthetic["OS/2"] = BuildOs2(data, hheaOffset);
            tables.Add(("OS/2", 0, synthetic["OS/2"].Length));
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

        // A fuzzed header can claim more tables than the directory holds: pdf.js issue3405r.pdf says 9
        // where the ninth "entry" is head's first bytes, with an offset and a length that both run past
        // the end of the program. That entry is not a table, so it is dropped instead of being clamped:
        // clamping its length to the bytes left subtracts more than the file has, and the negative total
        // then overflows the output allocation.
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
    /// Whether a name table carries a non-empty family record (ID 1). PeachDrawing resolves a face by that
    /// name and throws ArgumentNullException on it when it is missing; subsets that keep only the PostScript
    /// name (ID 6) are common enough.
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
            if (nameId != 1 || length == 0 || tableOffset + strings + offset + length > data.Length)
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
    /// True when the cmap already holds the record PeachDrawing.Text is known to read: Windows platform,
    /// Unicode BMP encoding. Measured against the back end, a Unicode record under platform 0 is refused
    /// ("no usable platform or encoding ID") unless its encoding is 3, and a Macintosh record onto a
    /// format 6 subtable is refused whatever its encoding, so the record ID alone does not decide - any
    /// cmap other than (3,1) gets the empty record <see cref="WithUnicodeBmpSubtable"/> appends.
    /// </summary>
    private static bool HasUsableCmapSubtable(byte[] data, int tableOffset, int tableLength)
    {
        if (tableLength < 4 || data.Length < tableOffset + tableLength)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(tableOffset + 2));
        if (tableLength < 4 + (count * 8))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            int record = tableOffset + 4 + (i * 8);
            if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record)) == 3
                && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 2)) == 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends an empty platform 3 / encoding 1 subtable to an existing cmap, keeping the records it
    /// already had: the font may map characters through them, and the renderer only asks for glyph IDs.
    /// </summary>
    private static byte[] WithUnicodeBmpSubtable(byte[] data, int tableOffset, int tableLength)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(tableOffset + 2));
        int oldHeader = 4 + (count * 8);
        if (tableLength < oldHeader)
        {
            return BuildEmptyCmap();
        }

        int oldSubtables = tableLength - oldHeader;
        int newHeader = 4 + ((count + 1) * 8);
        int appended = newHeader + oldSubtables + Pad(oldSubtables);
        byte[] subtable = BuildEmptyCmapSubtable();
        var cmap = new byte[appended + subtable.Length];

        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), (ushort)(count + 1));
        for (int i = 0; i < count; i++)
        {
            int source = tableOffset + 4 + (i * 8);
            int target = 4 + (i * 8);
            BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(target), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(source)));
            BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(target + 2), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(source + 2)));
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(target + 4),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(source + 4)) + (uint)(newHeader - oldHeader));
        }

        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4 + (count * 8)), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4 + (count * 8) + 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(4 + (count * 8) + 4), (uint)appended);

        Array.Copy(data, tableOffset + oldHeader, cmap, newHeader, oldSubtables);
        subtable.CopyTo(cmap, appended);
        return cmap;
    }

    /// <summary>
    /// An empty format 4 cmap for platform 3 / encoding 1. Its only segment is the required terminator
    /// (0xFFFF..0xFFFF, idDelta 1, idRangeOffset 0), so it maps nothing; the arrays follow the header
    /// back to back at subtable offsets 14, 16, 18 and 20, and the reserved pad ends the table at 24.
    /// </summary>
    private static byte[] BuildEmptyCmap()
    {
        var cmap = new byte[12 + BuildEmptyCmapSubtable().Length];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1); // one subtable
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3); // platform: Windows
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 1); // encoding: Unicode BMP
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12); // subtable offset
        BuildEmptyCmapSubtable().CopyTo(cmap, 12);
        return cmap;
    }

    private static byte[] BuildEmptyCmapSubtable()
    {
        var cmap = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(0), 4); // format
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 24); // length
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 2); // segCountX2
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(8), 2); // searchRange
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14), 0xFFFF); // endCode[0]
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(16), 0xFFFF); // startCode[0]
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(18), 1); // idDelta[0]
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

    /// <summary>
    /// A version 4 OS/2 table for a program that has none. The descriptor reads it for the weight, the
    /// selection flags and the vertical metrics, and a missing one aborts the parse; the vertical metrics
    /// are copied from hhea so the line metrics stay consistent with what the PDF already declares.
    /// </summary>
    private static byte[] BuildOs2(byte[] data, int hheaOffset)
    {
        var os2 = new byte[96];
        short ascender = 800;
        short descender = -200;
        short lineGap = 0;
        if (hheaOffset >= 0 && data.Length >= hheaOffset + 10)
        {
            ascender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hheaOffset + 4));
            descender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hheaOffset + 6));
            lineGap = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hheaOffset + 8));
        }

        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(0), 4); // version
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), 400); // usWeightClass: normal
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62), 0x40); // fsSelection: regular
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(68), ascender); // sTypoAscender
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(70), descender); // sTypoDescender
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(72), lineGap); // sTypoLineGap
        return os2;
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

    private static PeachFont? CreateFallback(string baseFontName, bool usedFallback)
    {
        string family = PeachStandardFonts.MapFamily(baseFontName);
        PeachText.TypefaceQuery query = PeachStandardFonts.MapQuery(baseFontName);

        lock (FontSetLock)
        {
            if (TryMatchFamily(family, query, usedFallback) is { } matched)
            {
                return matched;
            }

            // Broaden progressively: default style, then a generic sans, then anything the set can name.
            if (TryMatchFamily(family, default, usedFallback) is { } plain)
            {
                return plain;
            }

            if (TryMatchFamily("Arial", default, usedFallback) is { } arial)
            {
                return arial;
            }

            foreach (string name in PeachText.FontSet.InstalledFamilyNames)
            {
                if (TryMatchFamily(name, default, usedFallback) is { } first)
                {
                    return first;
                }
            }
        }

        return null;
    }

    private static PeachFont? TryMatchFamily(
        string family,
        in PeachText.TypefaceQuery query,
        bool usedFallback)
    {
        if (SharedFontSet.TryFindFamily(family, out PeachText.TypefaceFamily? found) && found is not null
            && found.TryMatch(query, out PeachText.TypefaceMatch match))
        {
            return From(match.Typeface, usedFallback);
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
    /// The parts of a glyph the Type 1 seac form builds, in design units: the base outline and the accent
    /// outline with the offset the charstring gives it. A seac glyph has no outline of its own - its
    /// charstring is one five-operand endchar - and PeachDrawing does not expand those, so pdf.js
    /// endchar.pdf's E-acute (a composite over "E") came back empty and the text read "...PLAN  TAIRE".
    /// </summary>
    internal bool TryGetSeacParts(ushort glyphId, out PeachOutlines.GlyphOutline? baseOutline,
        out PeachOutlines.GlyphOutline? accentOutline, out float accentX, out float accentY)
    {
        baseOutline = null;
        accentOutline = null;
        accentX = 0f;
        accentY = 0f;
        if (_cff is null || !_cff.TryGetSeacGlyph(glyphId, out Poso.Pdf.Fonts.CffProgram.SeacGlyph seac))
        {
            return false;
        }

        if (!TryGetOutline(seac.BaseGlyph, out baseOutline) || baseOutline is null || baseOutline.IsEmpty)
        {
            return false;
        }

        TryGetOutline(seac.AccentGlyph, out accentOutline);
        accentX = seac.AccentX;
        accentY = seac.AccentY;
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
        // The typeface itself is not disposable in PeachDrawing, but an embedded face carries the one
        // FontSet that registered its program; dropping that reference lets the program's bytes and
        // descriptor go with the font, which is the point of the private set.
        _embeddedSet = null;
    }
}
