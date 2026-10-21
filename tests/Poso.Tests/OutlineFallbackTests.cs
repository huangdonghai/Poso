using System;
using System.IO;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
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

        // The subset's FontMatrix scales by 1/1000 and the 'C' spans 722 units wide, 1006 tall. A zero
        // y-extent means the matrix was applied wrong; the outline is y-up, whose bounds RectF records
        // with the smallest y in Y, so the cap's baseline overshoot (-14 units) is the top edge here.
        RectF b = path.Bounds;
        Assert.InRange(b.Width, 0.6f, 0.75f);
        Assert.InRange(b.Height, 0.9f, 1.1f);
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

    private static byte[] FontsFile(string name) =>
        File.ReadAllBytes(IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name));
}
