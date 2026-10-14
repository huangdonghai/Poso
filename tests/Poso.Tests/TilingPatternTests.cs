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

    private static (byte B, byte G, byte R) Sample(byte[] px, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (px[offset], px[offset + 1], px[offset + 2]);
    }

    private static void AssertRed((byte B, byte G, byte R) c, string where) =>
        Assert.True(c.R > 170 && c.G < 90 && c.B < 90, $"{where} expected red, got R={c.R} G={c.G} B={c.B}");

    private static void AssertWhite((byte B, byte G, byte R) c, string where) =>
        Assert.True(c.R > 200 && c.G > 200 && c.B > 200, $"{where} expected white, got R={c.R} G={c.G} B={c.B}");
}
