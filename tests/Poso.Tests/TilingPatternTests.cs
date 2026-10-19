using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7: Type 1 tiling pattern fill. The pattern cell is white with a red left half, tiled every 20pt;
/// the fixture page is 100x100 at scale 1, so device columns map directly to pattern x.
/// </summary>
public class TilingPatternTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void FillWithTilingPattern_RepeatsCellAcrossPath()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTilingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // Within each 20px cell: x 0..10 red, x 10..20 white. Row 50 is inside every cell vertically.
        AssertRed(Sample(px, image.Width, 50, 5), "col 5 (cell0 left)");
        AssertWhite(Sample(px, image.Width, 50, 15), "col 15 (cell0 right)");
        AssertRed(Sample(px, image.Width, 50, 25), "col 25 (cell1 left, proves repetition)");
        AssertWhite(Sample(px, image.Width, 50, 35), "col 35 (cell1 right)");
    }

    [Fact]
    public void TilingPatternCell_WithText_RendersInk()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTilingTextPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int painted = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 250 || px[i + 1] < 250 || px[i + 2] < 250)
            {
                painted++;
            }
        }

        Assert.True(painted > 0, "text inside a tiling pattern must paint");
    }

    /// <summary>
    /// ZapfDingbats addresses its shapes through glyph names such as a109 (spade U+2660) in /Differences.
    /// The Adobe Glyph List must resolve the name and the family must map the Dingbats block; resolving to
    /// Wingdings (whose shapes sit in the private use area) left the cell, and pdf.js issue15716.pdf, blank.
    /// </summary>
    [Fact]
    public void TilingPatternCell_WithZapfDingbats_RendersGlyph()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTilingDingbatPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int painted = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 250 || px[i + 1] < 250 || px[i + 2] < 250)
            {
                painted++;
            }
        }

        Assert.True(painted > 0, "a ZapfDingbats glyph named a109 must paint the spade");
    }

    /// <summary>
    /// /XStep and /YStep (500) far larger than the 20x20 cell: exactly one cell paints at the origin
    /// and the rest of the fill is a gap (the white backdrop). Sizing the raster tile by the step - and
    /// clamping its huge device width to the buffer cap - drew nothing at all (pdf.js
    /// tiling-pattern-large-steps.pdf, 0% ink against MuPDF's 74%).
    /// </summary>
    [Fact]
    public void LargeSteps_PaintTheCellOnceAtTheOrigin()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTilingLargeStepsPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // The cell sits at pattern y 0..20, i.e. device rows 80..100 (y-down); sample its middle row.
        AssertRed(Sample(px, image.Width, 90, 5), "origin cell left half should be red");
        AssertWhite(Sample(px, image.Width, 90, 15), "origin cell right half should be white");
        AssertWhite(Sample(px, image.Width, 50, 50), "the gap between large steps should stay white");
    }

    /// <summary>
    /// The cell artwork comes from an image XObject reached through an indirect /Resources dictionary.
    /// Unresolved, the cell's /Im1 Do found nothing and the entire pattern fill stayed blank.
    /// </summary>
    [Fact]
    public void TilingCellWithIndirectResources_PaintsItsImage()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTilingWithIndirectResourcesPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        Assert.True(Dark(Sample(px, image.Width, 50, 5)), "col 5: the cell image's dark half");
        Assert.False(Dark(Sample(px, image.Width, 50, 15)), "col 15: the cell image's light half");
        Assert.True(Dark(Sample(px, image.Width, 50, 25)), "col 25: the next cell, proving repetition");
    }

    [Fact]
    public void DeviceColorAfterPatternSelection_ReplacesPatternForLaterFills()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTilingThenSolidFillPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // The blue rectangle covers user x 0..50, and it must be flat: every column of the tiled cell.
        foreach (int x in new[] { 5, 15, 25, 35, 45 })
        {
            AssertBlue(Sample(px, image.Width, 50, x), $"col {x} inside the solid fill");
        }

        // The unoverlapped right half still shows the tiling (cell phase: col 55 is 15 into a 20pt cell).
        AssertWhite(Sample(px, image.Width, 50, 55), "col 55 (cell right half)");
        AssertRed(Sample(px, image.Width, 50, 65), "col 65 (cell left half)");
    }

    private static bool Dark((byte B, byte G, byte R) c) => (c.B + c.G + c.R) / 3 < 128;

    private static (byte B, byte G, byte R) Sample(byte[] px, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (px[offset], px[offset + 1], px[offset + 2]);
    }

    private static void AssertBlue((byte B, byte G, byte R) c, string where) =>
        Assert.True(c.B > 170 && c.R < 90 && c.G < 90, $"{where} expected blue, got R={c.R} G={c.G} B={c.B}");

    private static void AssertRed((byte B, byte G, byte R) c, string where) =>
        Assert.True(c.R > 170 && c.G < 90 && c.B < 90, $"{where} expected red, got R={c.R} G={c.G} B={c.B}");

    private static void AssertWhite((byte B, byte G, byte R) c, string where) =>
        Assert.True(c.R > 200 && c.G > 200 && c.B > 200, $"{where} expected white, got R={c.R} G={c.G} B={c.B}");
}
