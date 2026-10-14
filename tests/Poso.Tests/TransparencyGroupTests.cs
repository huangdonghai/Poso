using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M3: ExtGState / form transparency-group compositing. Ground truth comes from MuPDF rendering the
/// same PDF (scale 1, 100x100). A form painted under /ca 0.5 fills two opaque overlapping rectangles
/// (red, then blue). With a /Group /S /Transparency the group is composited as a unit, so the overlap is
/// pure blue @50% (RGB 128,128,255). Without a group each rectangle is blended individually, so the
/// overlap becomes a red+blue mix (RGB ~128,64,191). Comparing the two proves the isolation wiring is
/// both active and behaviour-changing.
/// </summary>
public class TransparencyGroupTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void TransparencyGroup_CompositesContentAsSingleUnit()
    {
        (int R, int G, int B) overlap = Render(PdfFixtures.CreateTransparencyGroupPdf(withGroup: true), 60, 70);
        (int R, int G, int B) redOnly = Render(PdfFixtures.CreateTransparencyGroupPdf(withGroup: true), 20, 70);
        (int R, int G, int B) background = Render(PdfFixtures.CreateTransparencyGroupPdf(withGroup: true), 50, 10);

        // Red-only region: opaque red composited once at 50% over white.
        AssertInBand(redOnly.R, 245, 255, "red-only R");
        AssertInBand(redOnly.G, 110, 145, "red-only G");
        AssertInBand(redOnly.B, 110, 145, "red-only B");

        // Overlap: within the group blue fully covers red, then the whole group fades to 50% -> blue.
        // The decisive signal is G ~ 128 (isolated) versus G ~ 64 (layered, see contrast test).
        AssertInBand(overlap.R, 110, 145, "overlap R");
        AssertInBand(overlap.G, 110, 145, "overlap G");
        AssertInBand(overlap.B, 245, 255, "overlap B");

        AssertInBand(background.R, 250, 255, "background R");
        AssertInBand(background.G, 250, 255, "background G");
        AssertInBand(background.B, 250, 255, "background B");
    }

    [Fact]
    public void WithoutGroup_EachElementBlendsIndependently()
    {
        (int R, int G, int B) overlap = Render(PdfFixtures.CreateTransparencyGroupPdf(withGroup: false), 60, 70);

        // Non-grouped: blue @50% over (red @50% over white) -> purple-ish with a LOW green channel.
        AssertInBand(overlap.R, 110, 145, "nogroup overlap R");
        Assert.True(overlap.G < 90, $"nogroup overlap G expected < 90 (layered blend), got {overlap.G}");
        AssertInBand(overlap.B, 170, 215, "nogroup overlap B");
    }

    private static (int R, int G, int B) Render(byte[] pdf, int x, int y)
    {
        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        int offset = ((y * image.Width) + x) * 4; // BGRA
        return (px[offset + 2], px[offset + 1], px[offset]);
    }

    private static void AssertInBand(int value, int low, int high, string where) =>
        Assert.True(value >= low && value <= high, $"{where} expected [{low},{high}], got {value}");
}
