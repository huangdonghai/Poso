using System.Collections.Generic;
using System.IO;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Pdf.Objects;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Font programs embedded as an <b>'OTTO' container that holds nothing but a CFF table</b>. Some
/// generators write a FontFile3 as a two-entry sfnt - CFF and cmap, no head, maxp or name - which no
/// font back end opens; Skia and PeachDrawing both substitute a system face, so a book set in
/// AGaramondPro and MyriadPro comes out in Times New Roman and Arial. The CFF inside is a perfectly
/// good bare program, so the reader takes it out of the container and packages it itself. Surfaced on a
/// 1000+ page competition textbook where 94 of the body fonts substituted.
/// </summary>
public class CffOnlyContainerTests
{
    private static readonly IGraphicsFactory[] Factories = [SkiaGraphicsFactory.Default, PeachGraphicsFactory.Default];

    [Fact]
    public void TryExtractCff_ReturnsTheCffTableOfACffOnlyContainer()
    {
        byte[] cff = CffFixture.Build();

        Assert.True(CffOpenType.TryExtractCff(CffOnlyContainer(cff), out byte[]? extracted));
        Assert.Equal(cff, extracted);
    }

    [Fact]
    public void TryExtractCff_LeavesFontsThatCarryTheirOwnTablesAlone()
    {
        byte[] cff = CffFixture.Build();

        // A complete OpenType CFF font has a head table, and a TrueType or bare CFF program is not an
        // OTTO container at all. Rewriting any of those would break a font that was already loading.
        Assert.False(CffOpenType.TryExtractCff(CompleteContainer(cff), out _));
        Assert.False(CffOpenType.TryExtractCff(cff, out _));
        Assert.False(CffOpenType.TryExtractCff(new byte[4], out _));
        Assert.False(CffOpenType.TryExtractCff([0x4F, 0x54, 0x54, 0x4F, 0, 8, 0, 0, 0, 0, 0, 0], out _));
    }

    [Theory]
    [MemberData(nameof(GraphicsFactories))]
    public void CffOnlyProgram_LoadsTheEmbeddedFaceNotASubstitute(IGraphicsFactory factory)
    {
        byte[] pdf = PdfFixtures.CreateType1CffPdf(CffOnlyContainer(CffFixture.Build()));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        PdfDictionary fontDictionary = FontDictionary(document);

        IFont font = PdfFont.Create(document, factory, fontDictionary).GraphicsFont!;

        Assert.False(font.UsedFallback);
        Assert.Equal("CffOnlyTest", font.Name);
    }

    private static PdfDictionary FontDictionary(PdfDocument document)
    {
        PdfDictionary? resources = document.Resolve(document.Pages[0].Dictionary.Get("Resources")) as PdfDictionary;
        PdfDictionary? fonts = document.Resolve(resources?.Get("Font")) as PdfDictionary;
        return (PdfDictionary)document.Resolve(fonts!["F1"])!;
    }

    public static IEnumerable<object[]> GraphicsFactories()
    {
        foreach (IGraphicsFactory factory in Factories)
        {
            yield return [factory];
        }
    }

    /// <summary>Packages a CFF the way the back ends expect, so the test starts from a real font.</summary>
    private static byte[] CompleteContainer(byte[] cff)
    {
        Assert.True(CffProgram.TryCreate(cff, out CffProgram? program));
        return CffOpenType.Build(cff, program!, 750f, -250f, "CffOnlyTest");
    }

    /// <summary>Re-emits that container keeping only the CFF and cmap tables, the broken shape.</summary>
    private static byte[] CffOnlyContainer(byte[] cff)
    {
        byte[] sfnt = CompleteContainer(cff);
        int count = ReadU16(sfnt, 4);
        var kept = new List<(string Tag, byte[] Data)>();
        for (int i = 0; i < count; i++)
        {
            int directoryEntry = 12 + (i * 16);
            string tag = Encoding.ASCII.GetString(sfnt, directoryEntry, 4);
            if (tag is "CFF " or "cmap")
            {
                int tableOffset = (int)ReadU32(sfnt, directoryEntry + 8);
                kept.Add((tag, sfnt[tableOffset..(tableOffset + (int)ReadU32(sfnt, directoryEntry + 12))]));
            }
        }

        Assert.Equal(2, kept.Count);

        using var stream = new MemoryStream();
        Write(stream, [0x4F, 0x54, 0x54, 0x4F]); // 'OTTO'
        WriteUInt16(stream, (ushort)kept.Count);
        WriteUInt16(stream, 16); // searchRange
        WriteUInt16(stream, 1); // entrySelector
        WriteUInt16(stream, 16); // rangeShift

        int offset = 12 + (kept.Count * 16);
        foreach ((string tag, byte[] data) in kept)
        {
            Write(stream, Encoding.ASCII.GetBytes(tag));
            WriteUInt32(stream, CheckSum(data));
            WriteUInt32(stream, (uint)offset);
            WriteUInt32(stream, (uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }

        foreach ((_, byte[] data) in kept)
        {
            Write(stream, data);
            for (int padding = ((data.Length + 3) & ~3) - data.Length; padding > 0; padding--)
            {
                stream.WriteByte(0);
            }
        }

        return stream.ToArray();
    }

    private static void Write(MemoryStream stream, byte[] data) => stream.Write(data, 0, data.Length);

    private static void WriteUInt16(MemoryStream stream, ushort value) =>
        Write(stream, [(byte)(value >> 8), (byte)value]);

    private static void WriteUInt32(MemoryStream stream, uint value) =>
        Write(stream, [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static uint CheckSum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++)
            {
                word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            }

            sum += word;
        }

        return sum;
    }

    private static ushort ReadU16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
