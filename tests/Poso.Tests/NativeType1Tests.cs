using System;
using System.IO;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// Regression for native binary Type 1 fonts (PDF <c>FontFile</c>, eexec-encrypted). Such a program is
/// neither an OpenType container nor a CFF, so Skia cannot load it directly; Poso converts its charstrings
/// to Type 2 and rebuilds them as a CFF, then renders through the shared CFF pipeline. The oracle is MuPDF
/// (FreeType) rendering the *same* synthetic PDF, measured offline: a single 'x' at /F1 60 Tf, 30 30 Td on a
/// 200x100 page yields ink=554 with device bbox x[32..70] y[38..69].
/// </summary>
public class NativeType1Tests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void NativeType1_EmbeddedGlyph_MatchesMuPdfOutline()
    {
        byte[] program = File.ReadAllBytes(FontsFile("lucida-typewriter-x.bin"));

        // The program converts and its PostScript name 'x' resolves through the rebuilt CFF charset.
        Assert.True(Poso.Pdf.Fonts.Type1Program.TryCreate(program, out Poso.Pdf.Fonts.Type1Program? t1) && t1 is not null);
        byte[] cff = Poso.Pdf.Fonts.Type1ToCff.Build(t1!, "TestLucidaX");
        Assert.True(Poso.Pdf.Fonts.CffProgram.TryCreate(cff, out Poso.Pdf.Fonts.CffProgram? cffProg) && cffProg is not null);
        Assert.True(cffProg!.TryGetGlyphId("x", out ushort _));

        byte[] pdf = PdfFixtures.CreateNativeType1Pdf(program);
        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, ink = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (Dark(px, image.Width, x, y))
                {
                    ink++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        // Before the fix Skia rejected the raw Type1 program, so this font produced no ink at all.
        Assert.True(ink > 400 && ink < 700, $"'x' ink {ink} should be near the MuPDF oracle (554).");
        Assert.InRange(minX, 29, 35);
        Assert.InRange(maxX, 67, 74);
        Assert.InRange(minY, 35, 41);
        Assert.InRange(maxY, 66, 72);

        // Shape check: a genuine 'x' crosses at its centre and is empty at the middle of its left and
        // top edges - unlike a filled box or blob, which would ink those points too.
        int cx = (minX + maxX) / 2;
        int cy = (minY + maxY) / 2;
        Assert.True(Dark(px, image.Width, cx, cy), "the 'x' diagonals must cross at the centre");
        Assert.False(Dark(px, image.Width, minX + 2, cy), "mid-left of an 'x' is empty (no horizontal bar)");
        Assert.False(Dark(px, image.Width, cx, minY + 2), "mid-top of an 'x' is empty (no vertical bar)");
    }

    private static bool Dark(byte[] px, int width, int x, int y)
    {
        int o = ((y * width) + x) * 4;
        return px[o] < 200 || px[o + 1] < 200 || px[o + 2] < 200;
    }

    /// <summary>
    /// A symbolic (/Flags 4) Type 1 font whose /Encoding is a dictionary with /Differences that omit
    /// the drawn code must still resolve that code through the StandardEncoding base (PDF 32000-1
    /// Table 122). Guards the bug where a symbolic font with an /Encoding dictionary left such codes
    /// unmapped - which is how a real Open Sans document lost its lone page-number digit.
    /// MuPDF renders the same synthetic PDF's 'x' at ink 554, device bbox x[32..70] y[38..69].
    /// </summary>
    [Fact]
    public void SymbolicEncoding_DifferencesOmittedCode_FallsBackToStandardEncoding()
    {
        byte[] program = File.ReadAllBytes(FontsFile("lucida-typewriter-x.bin"));
        byte[] pdf = PdfFixtures.CreateSymbolicType1EncodingFallbackPdf(program);

        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, ink = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (Dark(px, image.Width, x, y))
                {
                    ink++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        // Before the fix the symbolic font's code 120 resolved to nothing, so no ink was produced.
        Assert.True(ink > 400 && ink < 700, $"symbolic 'x' ink {ink} should be near the MuPDF oracle (554).");
        Assert.InRange(minX, 29, 35);
        Assert.InRange(maxX, 67, 74);
        Assert.InRange(minY, 35, 41);
        Assert.InRange(maxY, 66, 72);
    }

    /// <summary>
    /// A real Open Sans native-Type 1 program uses a FontMatrix of 1/2048 (0.000488281), a CFF bcd real
    /// with an even nibble count. When re-serialised to CFF such a real must still carry its 0xF
    /// end-of-number terminator, or every following operand is misread and the font is rejected outright.
    /// This asserts the converted program parses and names resolve, and that the design-space scale
    /// survives the round trip.
    /// </summary>
    [Fact]
    public void LargeDesignSpaceType1_ConvertsToParsableCff()
    {
        byte[] program = File.ReadAllBytes(FontsFile("opensans-one.bin"));
        Assert.True(Poso.Pdf.Fonts.Type1Program.TryCreate(program, out Poso.Pdf.Fonts.Type1Program? t1) && t1 is not null);
        Assert.Equal(84, t1!.Glyphs.Count);
        Assert.Equal(1.0 / 2048, t1.FontMatrix[0], 5);

        byte[] cff = Poso.Pdf.Fonts.Type1ToCff.Build(t1, "OpenSansTest");
        Assert.True(Poso.Pdf.Fonts.CffProgram.TryCreate(cff, out Poso.Pdf.Fonts.CffProgram? cffProg) && cffProg is not null);
        Assert.True(cffProg!.TryGetGlyphId("one", out ushort _));
        // The top-dict FontMatrix real must survive parsing (1/2048, not a corrupted bleed of later bytes).
        Assert.Equal(1.0 / 2048, cffProg.FontMatrix[0], 5);
    }


    private static string FontsFile(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name);
        if (File.Exists(probe))
        {
            return probe;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = IOPath.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Fonts", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Type 1 font sample '{name}' not found.", name);
    }
}
