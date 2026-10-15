using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Poso.PeachDrawing;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// CID-keyed PDF fonts are addressed purely by glyph ID, so subsetting strips their cmap. PeachDrawing.Text
/// dereferences that missing table while building metrics and throws, which used to drop the embedded
/// program and draw whatever sat at those glyph IDs in a system substitute font - page 11 of a CJK book
/// came out as Greek and Coptic characters. Poso hands the back end an empty cmap instead; these cover the
/// sfnt surgery that makes that safe (sorted directory, preserved tables, recomputed head checksum).
/// </summary>
public class CmapLessTrueTypeTests
{
    [Fact]
    public void WithEmptyCmap_InsertsCmapAndPreservesEveryTable()
    {
        (string Tag, byte[] Data)[] source = [
            ("glyf", [0x00, 0x01, 0x02, 0x03, 0x04]),
            ("head", new byte[54]),
            ("maxp", [0x00, 0x01, 0x00, 0x00, 0x00, 0x02]),
            ("OS/2", new byte[68]),
        ];

        byte[]? repaired = PeachFont.WithEmptyCmap(StubSfnt(source));

        Assert.NotNull(repaired);
        List<(string Tag, int Offset, int Length)> directory = ReadDirectory(repaired!);
        Assert.Equal(["OS/2", "cmap", "glyf", "head", "maxp"], directory.Select(e => e.Tag));

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
    public void WithEmptyCmap_LeavesFontsThatAlreadyHaveOneAlone()
    {
        byte[] sfnt = StubSfnt([("cmap", new byte[4]), ("head", new byte[54])]);
        Assert.Null(PeachFont.WithEmptyCmap(sfnt));
    }

    [Fact]
    public void WithEmptyCmap_LeavesNonGlyfProgramsAlone()
    {
        // 'OTTO' (CFF), a TrueType collection ('ttcf') and a truncated header are all not sfnt/glyf fonts.
        Assert.Null(PeachFont.WithEmptyCmap([0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(PeachFont.WithEmptyCmap([0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 0]));
        Assert.Null(PeachFont.WithEmptyCmap([0x00, 0x01, 0x00, 0x00]));
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
