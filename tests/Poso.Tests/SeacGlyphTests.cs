using System;
using System.IO;
using IOPath = System.IO.Path;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Skia;
using Poso.PeachDrawing;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Type 1 <c>seac</c> composites inside a CFF program: a glyph whose charstring is a single five-operand
/// <c>endchar</c> names a base glyph and an accent glyph and draws no outline of its own. Skia expands
/// them; PeachDrawing does not, and pdf.js endchar.pdf - a crop of the word "PLANÉTAIRE" - came out as
/// "PLAN  TAIRE" on that back end with the whole page 32.6% away from PDFium.
/// The sample program is that document's embedded StoneSans (CFF, 229 glyphs, 56 of them composites).
/// </summary>
public class SeacGlyphTests
{
    private const ushort EAcute = 178;

    [Fact]
    public void CffProgram_NamesTheBaseAndAccentOfAComposite()
    {
        byte[] program = Sample("stonesans-seac.bin");
        Assert.True(CffProgram.TryCreate(program, out CffProgram? cff));
        Assert.NotNull(cff);

        Assert.True(cff!.TryGetSeacGlyph(EAcute, out CffProgram.SeacGlyph seac), "E-acute must read as a seac composite");
        // asb adx ady bchar achar endchar: the charstring is fb ef f7 48 f7 56 0e, i.e. -17, 73, 180, 69, 194.
        Assert.Equal(73, seac.AccentX);
        Assert.Equal(180, seac.AccentY); // the accent sits above the baseline
        Assert.NotEqual(0, seac.BaseGlyph);
        Assert.NotEqual(0, seac.AccentGlyph);

        // The base really is "E": both spellings of the name resolve to the same glyph.
        Assert.True(cff.TryGetGlyphId("E", out ushort e));
        Assert.Equal(e, seac.BaseGlyph);
        Assert.True(cff.TryGetGlyphId("acute", out ushort acute));
        Assert.Equal(acute, seac.AccentGlyph);
    }

    [Fact]
    public void CffProgram_LeavesAnOrdinaryGlyphAlone()
    {
        byte[] program = Sample("stonesans-seac.bin");
        Assert.True(CffProgram.TryCreate(program, out CffProgram? cff));
        Assert.True(cff!.TryGetGlyphId("E", out ushort e));
        Assert.False(cff.TryGetSeacGlyph(e, out _), "a plain letter is not a composite");
    }

    /// <summary>
    /// The composite has to reach the page as ink on the back end that cannot expand it itself. The page
    /// shows "E" at x=10 and E-acute at x=100, so the composite's own region is the right half: a back end
    /// without seac support paints the letter and nothing for the accent.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeacComposite_PaintsTheAccentOnBothBackends(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : SkiaGraphicsFactory.Default;

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSeacCffPdf(Sample("stonesans-seac.bin")));
        using IGraphicsContext context = factory.CreateContext(400, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        int left = InkIn(pixels, image.Width, 0, 100);
        int right = InkIn(pixels, image.Width, 100, image.Width);

        // Skia expands the composite from the charstring; PeachDrawing does not, and reaches the same
        // picture through the same seac reader plus its own two glyph outlines.
        Assert.True(left > 0, $"{factory.GetType().Name} did not paint the plain letter (left={left} right={right})");
        Assert.True(right > 0, $"{factory.GetType().Name} painted nothing for the seac composite (left={left} right={right})");
    }

    private static int InkIn(byte[] pixels, int width, int fromX, int toX)
    {
        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int x = (i / 4) % width;
            if (x < fromX || x >= toX)
            {
                continue;
            }

            if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200)
            {
                ink++;
            }
        }

        return ink;
    }

    private static byte[] Sample(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name);
        if (File.Exists(probe))
        {
            return File.ReadAllBytes(probe);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = IOPath.Combine(directory.FullName, "tests", "Poso.Tests", "Samples", "Fonts", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"font sample '{name}' not found", name);
    }
}
