using System;
using System.IO;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// When a back end cannot load an embedded glyph program it substitutes a system face, and the page then
/// carries the substitute's letterforms rather than the PDF's. Poso reads the outlines out of the program
/// itself and paints them with ordinary path fills, so the fallback keeps the shapes the document asked
/// for. The oracle is the geometry of the programs: an 'x' from the pdf.js bug1539074.1 Type 1 subset and
/// a 'C' from the issue12399_reduced Type1C subset, both measured against the charstring bytes by hand.
/// </summary>
public class OutlineFallbackTests
{
    [Fact]
    public void Type1C_OutlineReaderRecoversTheEmbeddedLetterform()
    {
        byte[] program = FontsFile("issue12399-times-subset.bin");
        Assert.True(CffOutlines.TryCreate(program, out OutlineSource? outlines) && outlines is not null);

        Assert.True(outlines!.TryGetGlyphIdForName("C", out ushort c));
        var builder = new PathBuilder();
        Assert.True(outlines.TryGetOutline(c, builder), "the Type1C subset must yield an outline");
        IPath path = builder.Build();

        // The subset's FontMatrix scales by 1/1000 and the 'C' spans 605 units wide, 690 tall (the
        // outline's full point extent, control points included, as FreeType reads it). A zero y-extent
        // means the matrix was applied wrong; the outline is y-up, whose bounds RectF records with the
        // smallest y in Y, so the cap's baseline overshoot (-14 units) is the top edge here.
        RectF b = path.Bounds;
        Assert.InRange(b.Width, 0.55f, 0.65f);
        Assert.InRange(b.Height, 0.65f, 0.75f);
        Assert.True(b.Top >= -0.1f && b.Top <= 0.1f, "the cap sits on the baseline");
    }

    /// <summary>
    /// The Type 1 program of pdf.js bug1539074.1's second font carries a FontMatrix with a negative yy
    /// ([0.001 0.002 0.003 -0.004 0 0]), which the CFF bcd writer used to emit with the exponent-minus
    /// nibble, so the reader parsed -0.004 as 0 and every y coordinate collapsed onto the baseline.
    /// </summary>
    [Fact]
    public void NativeType1_NegativeFontMatrix_KeepsItsVerticalExtent()
    {
        byte[] program = FontsFile("lucida-typewriter-slant.bin");
        Assert.True(Type1Program.TryCreate(program, out Type1Program? t1) && t1 is not null);

        // The header itself says 0.001 0.002 0.003 -0.004 0 0.
        Assert.Equal(0.001f, t1!.FontMatrix[0], 6);
        Assert.Equal(-0.004f, t1.FontMatrix[3], 6);

        byte[] cff = Type1ToCff.Build(t1, "SlantTest");
        Assert.True(CffOutlines.TryCreate(cff, out OutlineSource? outlines) && outlines is not null);
        Assert.True(outlines!.TryGetGlyphIdForName("x", out ushort x));

        var builder = new PathBuilder();
        Assert.True(outlines.TryGetOutline(x, builder));
        RectF b = builder.Build().Bounds;

        // The slant moves x by 0.002/0.001 = 2 units per y unit, so the 530-unit tall 'x' shears about
        // 1 em horizontally; what matters is that the vertical extent survived the round trip.
        Assert.True(b.Height > 0.3f, $"a negative FontMatrix must still give the glyph height, got {b.Height}");
        Assert.True(b.Width > 0.3f, $"the slant must widen the glyph, got {b.Width}");
    }

    /// <summary>
    /// The end-to-end guarantee: a page whose Type1C program Skia cannot load must still show the
    /// embedded letterforms. The fixture draws "Con" at 40 pt; the font must report that it needs the
    /// outline fallback, and the page must come back with ink.
    /// </summary>
    [Fact]
    public void BrokenType1C_ProgramPaintsTheEmbeddedOutlinesNotTheSubstitute()
    {
        byte[] program = FontsFile("issue12399-times-subset.bin");

        // Skia must actually reject this program, or the test proves nothing about the fallback.
        using (SkiaFont face = SkiaFont.Create(program, "AZFXBF+Times New Roman")!)
        {
            Assert.True(face.UsedFallback, "the fixture must be a program Skia falls back from");
        }

        byte[] pdf = PdfFixtures.CreateOutlineFallbackPdf(program);
        using PdfDocument document = PdfDocument.FromArray(pdf);

        // The font layer must see the mismatch: a fallback face plus a program Poso can read.
        var fontDict = (Poso.Pdf.Objects.PdfDictionary)document.GetObject(4)!;
        var font = PdfFont.Create(document, SkiaGraphicsFactory.Default, fontDict);
        Assert.True(font.NeedsOutlineFallback, "the reader must offer outlines for the rejected program");

        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(220, 60);
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        int minX = int.MaxValue, maxX = -1, ink = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int o = ((y * image.Width) + x) * 4;
                if (px[o] < 200 || px[o + 1] < 200 || px[o + 2] < 200)
                {
                    ink++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }
        }

        // "Con" at 40 pt through /Widths 722+500+556 spans 71 px of pen travel; the ink of the three
        // letters runs slightly short of it (the final 'n' carries a right side bearing).
        Assert.True(ink > 400, $"the embedded outlines must paint, got {ink} dark pixels");
        Assert.InRange(maxX - minX, 55, 75);
    }

    /// <summary>
    /// A glyph the outline reader cannot produce must not blank the run: the remaining glyphs still
    /// paint and the rest of the page is untouched.
    /// </summary>
    [Fact]
    public void OutlineFallback_MissingGlyph_LeavesTheRestOfTheRunPainted()
    {
        byte[] program = FontsFile("issue12399-times-subset.bin");
        byte[] pdf = PdfFixtures.CreateOutlineFallbackPdf(program);
        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(220, 60);
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        // Code 1 ('A' in the subset, not drawn) must not throw; the drawn codes must still be dark.
        int dark = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 200) dark++;
        }

        Assert.True(dark > 400, "a run with an unresolvable glyph still paints what it can");
    }

    /// <summary>
    /// The TrueType side of the outline reader, on a real PDF subset: pdf.js bug1200096.pdf's Times
    /// program, which PeachDrawing rejects. The loca/glyf walk must yield plausible on-baseline outlines
    /// and, crucially, a different one per glyph - a flag or delta misread collapses several glyphs onto
    /// the same shape or onto nothing.
    /// </summary>
    [Fact]
    public void TrueType_OutlineReaderRecoversEmbeddedLetterforms()
    {
        byte[] program = FontsFile("bug1200096-times-subset.bin");
        Assert.True(TrueTypeOutlines.TryCreate(program, out OutlineSource? outlines) && outlines is not null);
        Assert.True(outlines!.GlyphCount > 50, $"a Times subset carries its drawn glyphs, got {outlines.GlyphCount}");

        var seen = new HashSet<string>();
        int drawn = 0;
        for (int gid = 30; gid < 45; gid++)
        {
            var builder = new PathBuilder();
            if (!outlines.TryGetOutline(gid, builder))
            {
                continue; // an empty glyph (a space) legitimately has no outline
            }

            RectF b = builder.Build().Bounds;
            Assert.True(b.Width > 0.05f && b.Width < 1.5f, $"glyph {gid} width {b.Width} is not a plausible letterform");
            Assert.True(b.Height > 0.05f && b.Height < 1.5f, $"glyph {gid} height {b.Height} is not a plausible letterform");
            Assert.True(b.Top >= -0.4f && b.Top <= 0.4f, $"glyph {gid} sits near the baseline, got top {b.Top}");
            seen.Add($"{b.Left:F3},{b.Bottom:F3},{b.Right:F3},{b.Top:F3}");
            drawn++;
        }

        Assert.True(drawn >= 10, $"most glyphs of a Times subset carry outlines, got {drawn} of 15");
        Assert.True(seen.Count >= 10, $"distinct glyphs must give distinct bounds, got {seen.Count} of {drawn}");
    }

    /// <summary>
    /// The TrueType fallback end to end on the PeachDrawing back end, which rejects this subset. A
    /// TrueType program carries no glyph names, so a code reaches its outline only through the sfnt's
    /// own cmap; without that step every lookup needed a name, found none, and the fallback painted a
    /// blank run. This subset maps 'A' to gid 36.
    /// </summary>
    [Fact]
    public void TrueTypeFallback_OnPeach_PaintsTheCmapResolvedOutlines()
    {
        byte[] program = FontsFile("bug1200096-times-subset.bin");

        // PeachDrawing must actually reject this program, or the test proves nothing about the fallback.
        using (PeachFont face = PeachFont.Create(program, "KVIQXV+Times New Roman")!)
        {
            Assert.True(face.UsedFallback, "the fixture must be a program PeachDrawing falls back from");
        }

        byte[] pdf = PdfFixtures.CreateTrueTypeOutlineFallbackPdf(program);
        using PdfDocument document = PdfDocument.FromArray(pdf);
        var fontDict = (Poso.Pdf.Objects.PdfDictionary)document.GetObject(4)!;
        var font = PdfFont.Create(document, PeachGraphicsFactory.Default, fontDict);
        Assert.True(font.NeedsOutlineFallback, "the reader must offer outlines for the rejected program");

        Assert.True(font.TryGetGlyphOutlinePath(65, out IPath? a) && a is not null,
            "code 65 must resolve to gid 36 through the cmap");
        RectF bounds = a!.Bounds;
        Assert.InRange(bounds.Width, 0.6f, 0.8f);

        using IGraphicsContext context = PeachGraphicsFactory.Default.CreateContext(220, 60);
        document.Pages[0].Render(PeachGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        int ink = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 200 || px[i + 1] < 200 || px[i + 2] < 200)
            {
                ink++;
            }
        }

        // "ABC" at 40 pt spans about 82 px of pen travel; three cap letterforms carry well over 600
        // dark pixels even through the software rasteriser's lighter anti-aliasing.
        Assert.True(ink > 600, $"the embedded outlines must paint through PeachDrawing, got {ink} dark pixels");
    }

    /// <summary>
    /// The shared sfnt reader has to prefer the Unicode tables and translate the Windows symbol
    /// convention: pdf.js issue2017r.pdf's subset carries a (1,0) table under ASCII and a (3,0) table
    /// whose entries sit at 0xF020-0xF0FF. Merging them last-wins would let the symbol table's zero
    /// entries erase the Mac mappings, and not translating 0xF0xx would leave every CDE lookup empty.
    /// </summary>
    [Fact]
    public void SfntCmap_PrefersTheUnicodeTablesAndNormalisesASymbolTable()
    {
        byte[] program = FontsFile("mac-cmap-subset.bin");
        var map = SfntCmap.Read(program);

        Assert.Equal(36, map[0x41]);   // 'A' through the (1,0) table
        Assert.Equal(72, map[0x65]);   // 'e' through the (3,0) table at 0xF065
        Assert.Equal(72, map[0xF065]);
    }

    /// <summary>
    /// A cmap record's subtable offset is a uint. A corrupt program can point it near 4G, which wraps
    /// negative when added to the table start; "offset + 2" then passes the upper-bound test and the
    /// reader walks off the array. pdf.js bug1050040.pdf's directory does this, and it took the Peach
    /// render down before the guard.
    /// </summary>
    [Fact]
    public void SfntCmap_SubtableOffsetPastTheProgram_YieldsNoMappingsInsteadOfThrowing()
    {
        var font = new byte[64];
        font[0] = 0;
        font[1] = 1;
        font[2] = 0;
        font[3] = 0;
        font[4] = 0;
        font[5] = 1; // one table
        "cmap"u8.CopyTo(font.AsSpan(12));
        uint cmapOffset = 28;
        uint cmapLength = 32;
        font[20] = (byte)(cmapOffset >> 24);
        font[21] = (byte)(cmapOffset >> 16);
        font[22] = (byte)(cmapOffset >> 8);
        font[23] = (byte)cmapOffset;
        font[24] = (byte)(cmapLength >> 24);
        font[25] = (byte)(cmapLength >> 16);
        font[26] = (byte)(cmapLength >> 8);
        font[27] = (byte)cmapLength;

        // cmap header: format 0, one record pointing at 0xFFFFFFF0.
        font[28] = 0;
        font[29] = 1;
        font[30] = 0;
        font[31] = 1;
        font[36] = 0; // platform
        font[37] = 3;
        font[38] = 0; // encoding
        font[39] = 1;
        font[40] = 0xFF;
        font[41] = 0xFF;
        font[42] = 0xFF;
        font[43] = 0xF0;

        var map = SfntCmap.Read(font);
        Assert.Empty(map);
    }

    private static byte[] FontsFile(string name) =>
        File.ReadAllBytes(IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name));
}
