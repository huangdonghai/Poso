using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Poso.Pdf;
using Poso.PeachDrawing;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// CID-keyed PDF fonts are addressed purely by glyph ID, so subsetting strips their cmap and often leaves
/// the name table with nothing but an empty uniqueID record. PeachDrawing.Text dereferences the missing
/// cmap while building metrics and throws - which used to drop the embedded program and draw whatever sat
/// at those glyph IDs in a system substitute font, so page 11 of a CJK book came out as Greek and Coptic
/// characters - and it interns typefaces by the name strings, so an empty identity collapses every such
/// font in a document onto the first one registered and the rest render as .notdef boxes. Poso repairs
/// both; these cover the sfnt surgery that makes that safe (sorted directory, preserved tables, recomputed
/// head checksum).
/// </summary>
public class CmapLessTrueTypeTests
{
    [Fact]
    public void RepairSubset_AddsCmapAndIdentityAndPreservesEveryTable()
    {
        (string Tag, byte[] Data)[] source = [
            ("glyf", [0x00, 0x01, 0x02, 0x03, 0x04]),
            ("head", new byte[54]),
            ("maxp", [0x00, 0x01, 0x00, 0x00, 0x00, 0x02]),
            ("OS/2", new byte[68]),
        ];

        byte[]? repaired = PeachFont.RepairSubset(StubSfnt(source), "poso-embedded-abc");

        Assert.NotNull(repaired);
        List<(string Tag, int Offset, int Length)> directory = ReadDirectory(repaired!);
        Assert.Equal(["OS/2", "cmap", "glyf", "head", "maxp", "name", "post"], directory.Select(e => e.Tag));

        (string _, int offset, int length) nameEntry = directory.First(e => e.Tag == "name");
        Assert.Contains("poso-embedded-abc", ReadNameStrings(repaired!, nameEntry.offset, nameEntry.length));

        foreach ((string tag, byte[] data) in source)
        {
            (string _, int offset, int length) entry = directory.First(e => e.Tag == tag);
            Assert.Equal(data.Length, entry.length);

            // head is the one table the repair rewrites: its checkSumAdjustment covers the new layout.
            byte[] written = repaired!.AsSpan(entry.offset, data.Length).ToArray();
            if (tag == "head")
            {
                written[8] = written[9] = written[10] = written[11] = 0;
            }

            Assert.Equal(data, written);
        }

        // An sfnt whose head.checkSumAdjustment was recomputed sums to the magic constant over the file.
        Assert.Equal(0xB105u, CheckSum(repaired!));
    }

    [Fact]
    public void RepairSubset_LeavesCompleteFontsAlone()
    {
        byte[] sfnt = StubSfnt([
            ("OS/2", new byte[96]),
            ("cmap", StubCmapWithWindowsRecord()),
            ("head", new byte[54]),
            ("name", StubNameTable("Real Font")),
            ("post", new byte[32]),
        ]);
        Assert.Null(PeachFont.RepairSubset(sfnt, "poso-embedded-abc"));
    }

    [Fact]
    public void RepairSubset_RenamesACompleteFontOnlyWhenAsked()
    {
        // A program that needs none of the descriptor repairs: without renameSelf it is left alone.
        byte[] sfnt = StubSfnt([
            ("OS/2", new byte[96]),
            ("cmap", StubCmapWithWindowsRecord()),
            ("head", new byte[54]),
            ("name", StubNameTable("Real Font")),
            ("post", new byte[32]),
        ]);
        Assert.Null(PeachFont.RepairSubset(sfnt, "poso-embedded-abc"));

        byte[]? renamed = PeachFont.RepairSubset(sfnt, "poso-embedded-abc", renameSelf: true);

        Assert.NotNull(renamed);
        Assert.Equal(0x00010000u, Read(renamed!, 0));
        Assert.Equal(0xB105u, CheckSum(renamed!));
        List<(string Tag, int Offset, int Length)> directory = ReadDirectory(renamed!);
        Assert.Equal(["OS/2", "cmap", "head", "name", "post"], directory.Select(e => e.Tag));

        // The name table is the one table rewritten: family, subfamily, unique ID, full name and
        // PostScript name now all say the identity, and the declared family is gone.
        (string _, int offset, int length) = directory.First(e => e.Tag == "name");
        string names = ReadNameStrings(renamed!, offset, length);
        Assert.Contains("poso-embedded-abc", names);
        Assert.DoesNotContain("Real Font", names);
    }

    [Fact]
    public void RepairSubset_RenamesACffProgramWithoutAddingTrueTypeTables()
    {
        // 'OTTO' needs no cmap/post/OS/2 repairs - those are the TrueType reader's requirements - but it
        // does carry a name table, and a CFF subset named after an installed family pollutes the same
        // process-wide cache.
        byte[] sfnt = StubSfnt([
            ("CFF ", [0x01, 0x00, 0x04, 0x02]),
            ("head", new byte[54]),
            ("name", StubNameTable("Real Font")),
        ]);
        Encoding.ASCII.GetBytes("OTTO").CopyTo(sfnt, 0);
        Assert.Null(PeachFont.RepairSubset(sfnt, "poso-embedded-cff"));

        byte[]? renamed = PeachFont.RepairSubset(sfnt, "poso-embedded-cff", renameSelf: true);

        Assert.NotNull(renamed);
        Assert.Equal(0x4F54544Fu, Read(renamed!, 0));
        List<(string Tag, int Offset, int Length)> directory = ReadDirectory(renamed!);
        Assert.Equal(["CFF ", "head", "name"], directory.Select(e => e.Tag));
        Assert.Contains("poso-embedded-cff", ReadNameStrings(renamed!, directory[2].Offset, directory[2].Length));
    }

    [Fact]
    public void RepairSubset_ReplacesAnIdentityFreeNameTable()
    {
        // What a subsetting tool leaves behind: one record, the unique ID, empty.
        byte[] empty = StubNameTable("");
        byte[]? repaired = PeachFont.RepairSubset(StubSfnt([("head", new byte[54]), ("name", empty)]), "poso-embedded-xyz");

        Assert.NotNull(repaired);
        (string _, int offset, int length) entry = ReadDirectory(repaired!).First(e => e.Tag == "name");
        Assert.Contains("poso-embedded-xyz", ReadNameStrings(repaired!, entry.offset, entry.length));
    }

    [Fact]
    public void RepairSubset_LeavesNonGlyfProgramsAlone()
    {
        // 'OTTO' (CFF), a TrueType collection ('ttcf') and a truncated header are all not sfnt/glyf fonts.
        Assert.Null(PeachFont.RepairSubset([0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0, 0, 0, 0, 0], "id"));
        Assert.Null(PeachFont.RepairSubset([0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 0], "id"));
        Assert.Null(PeachFont.RepairSubset([0x00, 0x01, 0x00, 0x00], "id"));
    }

    /// <summary>
    /// The end-to-end defect. PeachDrawing.Text dereferences the cmap and the post table while it builds a
    /// typeface descriptor, and CID-keyed PDF subsets drop both. When either is missing and unrepaired,
    /// <see cref="PeachFont.Create"/> silently falls back to a system face: Latin text still looks right
    /// while CJK renders as .notdef boxes, and no unsupported feature is reported because the fallback
    /// "succeeded". These load a real TrueType program with the tables removed and require the embedded
    /// face - its name and its metrics - to survive.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("cmap")]
    [InlineData("post")]
    [InlineData("cmap,post")]
    public void PeachFont_KeepsTheEmbeddedFaceWithoutDescriptorTables(string dropped)
    {
        string[] drop = dropped.Length == 0 ? [] : dropped.Split(',');
        byte[] program = File.ReadAllBytes(FontFile("liberation-sans-regular.ttf"));
        byte[] subset = drop.Length == 0 ? program : WithoutTables(program, drop);
        foreach (string tag in drop)
        {
            Assert.DoesNotContain(tag, ReadDirectory(subset).Select(e => e.Tag));
        }

        using PeachFont? font = PeachFont.Create(subset, "probe");
        Assert.NotNull(font);

        // The face keeps its own program but not the name it declares: every embedded program is renamed
        // to the generated identity so it cannot take an installed family's slot in PeachDrawing's
        // process-wide font-source cache (see RepairSubset's renameSelf). The metrics below are what
        // prove the embedded face, not a substitute, is loaded.
        Assert.StartsWith("poso-embedded-", font!.Name);

        using PeachFont reference = PeachFont.Create(program, "probe")!;
        List<(ushort Gid, float Width)> metrics = [
            .. Enumerable.Range(1, 99).Select(gid => ((ushort)gid, font.GetAdvanceWidth((ushort)gid)))
        ];

        Assert.Contains(metrics, m => m.Width > 0f);
        foreach ((ushort gid, float width) in metrics)
        {
            Assert.Equal(reference.GetAdvanceWidth(gid), width);
        }
    }

    /// <summary>
    /// The other side of a missing /cmap: a SIMPLE font is addressed by character, so without a character-map
    /// the back end loads the face, hands back ids it cannot rasterise, and the page paints nothing - the
    /// defect in pdf.js TrueType_without_cmap.pdf. The core substitutes a face by the base font name, so text
    /// stays visible, while the CID case above keeps its embedded program.
    /// </summary>
    [Fact]
    public void SimpleFontWithoutCmap_PaintsWithASubstituteFace()
    {
        byte[] program = File.ReadAllBytes(FontFile("liberation-sans-regular.ttf"));
        byte[] cmapLess = WithoutTables(program, ["cmap"]);
        Assert.DoesNotContain("cmap", ReadDirectory(cmapLess).Select(e => e.Tag));

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextWithFontProgramPdf(cmapLess));

        int ink = PaintedPixels(document);
        Assert.True(ink > 0, "a simple font whose program has no cmap must still paint, through a substitute face");
    }

    private static int PaintedPixels(PdfDocument document)
    {
        Poso.Graphics.IGraphicsFactory factory = Poso.Skia.SkiaGraphicsFactory.Default;
        using Poso.Graphics.IGraphicsContext context = factory.CreateContext(200, 50);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, Poso.Graphics.PosoColor.White));
        using Poso.Graphics.IImage image = context.Snapshot();

        var pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
            {
                ink++;
            }
        }

        return ink;
    }

    [Fact]
    public void RepairSubset_NormalisesAppleVersionExtendsCmapAndAddsOs2()
    {
        // The shape a subsetting tool on macOS leaves behind: 'true' sfnt version, a Macintosh-only cmap,
        // a name table with no family record, and no OS/2 at all.
        byte[] sfnt = StubSfnt([
            ("cmap", StubCmapWithRecord(platform: 1, encoding: 0)),
            ("head", new byte[54]),
            ("name", StubNameTable("")),
            ("post", new byte[32]),
        ]);
        Encoding.ASCII.GetBytes("true").CopyTo(sfnt, 0);

        byte[]? repaired = PeachFont.RepairSubset(sfnt, "poso-embedded-mac");

        Assert.NotNull(repaired);
        Assert.Equal(0x00010000u, Read(repaired!, 0));
        Assert.Equal(0xB105u, CheckSum(repaired!));

        List<(string Tag, int Offset, int Length)> directory = ReadDirectory(repaired!);
        Assert.Contains(("OS/2"), directory.Select(e => e.Tag));
        (string _, int cmapOffset, int cmapLength) = directory.First(e => e.Tag == "cmap");

        Assert.Equal(2, (repaired![cmapOffset + 2] << 8) | repaired[cmapOffset + 3]);
        Assert.Equal(1, (repaired[cmapOffset + 4] << 8) | repaired[cmapOffset + 5]); // the Mac record first
        Assert.Equal(3, (repaired[cmapOffset + 12] << 8) | repaired[cmapOffset + 13]);
        Assert.Equal(1, (repaired[cmapOffset + 14] << 8) | repaired[cmapOffset + 15]);
        Assert.True(cmapLength >= 4 + (2 * 8));
    }

    /// <summary>
    /// Only a Windows / Unicode BMP cmap record is trusted; every other shape gets the empty readable one
    /// appended, because PeachDrawing.Text refuses to open a font without it. Measured against the back
    /// end: a Unicode record under platform 0 throws "Font has no usable platform or encoding ID" however
    /// it is spelled except encoding 3, and a Macintosh record onto a format 6 subtable throws too. The
    /// corpus fonts that were lost this way were exactly the platform 0 / encoding 1 shape a macOS
    /// subsetter writes, which the old check accepted as usable.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    public void RepairSubset_AppendsAReadableCmapRecordToAnythingElse(int platform, int encoding)
    {
        byte[] sfnt = StubSfnt([
            ("cmap", StubCmapWithRecord((ushort)platform, (ushort)encoding)),
            ("head", new byte[54]),
            ("maxp", [0x00, 0x01, 0x00, 0x00, 0x00, 0x02]),
            ("name", StubNameTable("Probe Family")),
            ("OS/2", new byte[96]),
            ("post", new byte[32]),
        ]);

        byte[]? repaired = PeachFont.RepairSubset(sfnt, "Probe Family");

        Assert.NotNull(repaired);

        // The original record stays and the empty Windows Unicode BMP one is appended after it.
        (string _, int offset, int _) = ReadDirectory(repaired!).First(e => e.Tag == "cmap");
        Assert.Equal(2, (repaired![offset + 2] << 8) | repaired[offset + 3]);
        Assert.Equal(platform, (repaired[offset + 4] << 8) | repaired[offset + 5]);
        Assert.Equal(3, (repaired[offset + 12] << 8) | repaired[offset + 13]);
        Assert.Equal(1, (repaired[offset + 14] << 8) | repaired[offset + 15]);
    }

    [Fact]
    public void RepairSubset_LeavesAFontWithAReadableCmapRecordAlone()
    {
        byte[] sfnt = StubSfnt([
            ("cmap", StubCmapWithRecord(3, 1)),
            ("head", new byte[54]),
            ("maxp", [0x00, 0x01, 0x00, 0x00, 0x00, 0x02]),
            ("name", StubNameTable("Probe Family")),
            ("OS/2", new byte[96]),
            ("post", new byte[32]),
        ]);

        Assert.Null(PeachFont.RepairSubset(sfnt, "Probe Family"));
    }

    /// <summary>A cmap directory with one record for the given platform and encoding.</summary>
    private static byte[] StubCmapWithRecord(ushort platform, ushort encoding)
    {
        var cmap = new byte[12];
        Write16(cmap, 2, 1);
        Write16(cmap, 4, platform);
        Write16(cmap, 6, encoding);
        Write(cmap, 8, 20);
        return cmap;
    }

    /// <summary>Rebuilds an sfnt without the named tables, recomputing head.checkSumAdjustment.</summary>
    private static byte[] WithoutTables(byte[] sfnt, params string[] drop)
    {
        (string Tag, int Offset, int Length)[] kept = ReadDirectory(sfnt).Where(e => !drop.Contains(e.Tag)).ToArray();
        int count = kept.Length;
        int dataStart = 12 + (count * 16);
        int total = dataStart;
        foreach ((_, int _, int length) in kept)
        {
            total += length + Pad(length);
        }

        var output = new byte[total];
        Write(output, 0, 0x00010000);
        Write16(output, 4, count);
        int selector = 0;
        while ((1 << (selector + 1)) <= count)
        {
            selector++;
        }

        Write16(output, 6, 16 << selector);
        Write16(output, 8, selector);
        Write16(output, 10, (count * 16) - (16 << selector));

        int write = dataStart;
        int headAt = -1;
        for (int i = 0; i < count; i++)
        {
            (string tag, int offset, int length) = kept[i];
            int directory = 12 + (i * 16);
            Encoding.ASCII.GetBytes(tag).CopyTo(output, directory);
            Write(output, directory + 4, CheckSum(sfnt.AsSpan(offset, length).ToArray()));
            Write(output, directory + 8, (uint)write);
            Write(output, directory + 12, (uint)length);
            Array.Copy(sfnt, offset, output, write, length);
            if (tag == "head")
            {
                headAt = write;
            }

            write += length + Pad(length);
        }

        if (headAt >= 0)
        {
            Write(output, headAt + 8, 0u);
            Write(output, headAt + 8, 0xB105u - CheckSum(output));
        }

        return output;
    }

    private static string FontFile(string name)
    {
        string probe = Path.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name);
        if (File.Exists(probe))
        {
            return probe;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Fonts", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(name);
    }

    /// <summary>Every string a name table holds, decoded as UTF-16.</summary>
    private static string ReadNameStrings(byte[] sfnt, int tableOffset, int tableLength)
    {
        int count = (sfnt[tableOffset + 2] << 8) | sfnt[tableOffset + 3];
        int strings = (sfnt[tableOffset + 4] << 8) | sfnt[tableOffset + 5];
        var text = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            int record = tableOffset + 6 + (i * 12);
            int length = (sfnt[record + 8] << 8) | sfnt[record + 9];
            int offset = (sfnt[record + 10] << 8) | sfnt[record + 11];
            text.Append(Encoding.BigEndianUnicode.GetString(sfnt, tableOffset + strings + offset, length)).Append('|');
        }

        return text.ToString();
    }

    /// <summary>A cmap directory with a single Windows / Unicode BMP (3,1) record.</summary>
    private static byte[] StubCmapWithWindowsRecord()
    {
        var cmap = new byte[12];
        Write16(cmap, 2, 1);
        Write16(cmap, 4, 3);
        Write16(cmap, 6, 1);
        Write(cmap, 8, 20);
        return cmap;
    }

    /// <summary>A one-record Windows name table (format 0) carrying the given family string.</summary>
    private static byte[] StubNameTable(string family)
    {
        byte[] value = Encoding.BigEndianUnicode.GetBytes(family);
        var name = new byte[6 + 12 + value.Length];
        Write16(name, 2, 1);
        Write16(name, 4, 6 + 12);
        Write16(name, 6, 3); // platform: Windows
        Write16(name, 8, 1); // encoding: Unicode BMP
        Write16(name, 10, 0x409);
        Write16(name, 12, 1); // name ID: family
        Write16(name, 14, value.Length);
        Write16(name, 16, 0);
        value.CopyTo(name, 18);
        return name;
    }

    /// <summary>Writes tables into a sorted, padded sfnt directory with no cmap.</summary>
    private static byte[] StubSfnt((string Tag, byte[] Data)[] tables)
    {
        var sorted = tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToArray();
        int count = sorted.Length;
        int dataStart = 12 + (count * 16);
        int offset = dataStart;
        foreach ((_, byte[] data) in sorted)
        {
            offset += data.Length + Pad(data.Length);
        }

        var output = new byte[offset];
        Write(output, 0, 0x00010000);
        Write16(output, 4, count);
        Write16(output, 6, 16); // searchRange is unused by the repair
        for (int i = 0; i < count; i++)
        {
            (string tag, byte[] data) = sorted[i];
            Encoding.ASCII.GetBytes(tag).CopyTo(output, 12 + (i * 16));
            Write(output, 12 + (i * 16) + 8, (uint)dataStart);
            Write(output, 12 + (i * 16) + 12, (uint)data.Length);
            data.CopyTo(output, dataStart);
            dataStart += data.Length + Pad(data.Length);
        }

        return output;
    }

    private static List<(string Tag, int Offset, int Length)> ReadDirectory(byte[] sfnt)
    {
        int count = (sfnt[4] << 8) | sfnt[5];
        var entries = new List<(string, int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            entries.Add((
                Encoding.ASCII.GetString(sfnt, entry, 4),
                (int)Read(sfnt, entry + 8),
                (int)Read(sfnt, entry + 12)));
        }

        return entries;
    }

    private static uint Read(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    private static void Write(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }

    private static void Write16(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 8);
        data[at + 1] = (byte)value;
    }

    private static int Pad(int length) => (4 - (length % 4)) % 4;

    private static uint CheckSum(byte[] data)
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
}
