using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// A pattern selected while stroking (<c>SCN</c> under a pattern colour space) paints through the
/// stroke's own outline, on both back-ends - including when the mark lands on a masked layer whose
/// surface origin is not (0,0), where a raster sized from the clip must be placed in the device space
/// the layer's matrix works in.
/// </summary>
public class PatternStrokeTests
{
    private static readonly IGraphicsFactory[] Backends =
    {
        SkiaGraphicsFactory.Default,
        PeachGraphicsFactory.Default,
    };

    [Fact]
    public void ShadingPatternStroke_PaintsTheRampAlongTheBand()
    {
        foreach (IGraphicsFactory factory in Backends)
        {
            string tag = Tag(factory);
            byte[] pixels = Render(PdfFixtures.CreateShadingStrokePdf(), factory, out int width, out List<PdfUnsupportedFeature> features);

            Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);
            Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

            // The page is 200x100 user units at scale 1, so device x is user x and the band sits on
            // user y 40..60, i.e. device rows 40..60.
            byte left = Gray(pixels, width, 50, 60);
            byte right = Gray(pixels, width, 50, 175);
            Assert.True(left < 90, $"{tag}: the band's x=60 should carry the dark ramp end (got {left})");
            Assert.True(right > 170, $"{tag}: the band's x=175 should carry the light ramp end (got {right})");
            Assert.True(right > left + 60, $"{tag}: the band should ramp along its length (left={left}, right={right})");

            // The pattern is painted through the outline, not the path's bounding box.
            AssertWhite(pixels, width, 20, 100, $"{tag}: above the band");
            AssertWhite(pixels, width, 80, 100, $"{tag}: below the band");
            // The band stops at the line's endpoints (user x 10..190).
            AssertWhite(pixels, width, 50, 5, $"{tag}: before the line's start");
            AssertWhite(pixels, width, 50, 195, $"{tag}: after the line's end");
        }
    }

    [Fact]
    public void TilingPatternStroke_RepeatsTheCellAlongTheBand()
    {
        foreach (IGraphicsFactory factory in Backends)
        {
            string tag = Tag(factory);
            byte[] pixels = Render(PdfFixtures.CreateTilingStrokePdf(), factory, out int width, out List<PdfUnsupportedFeature> features);

            Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);

            // Cells align on user x multiples of 20 from the pattern's origin, so each cell's left half
            // (x mod 20 < 10) is red and its right half white - along the band only.
            AssertWhite(pixels, width, 50, 15, $"{tag}: cell 0 right half");
            AssertRed(pixels, width, 50, 25, $"{tag}: cell 1 left half");
            AssertWhite(pixels, width, 50, 35, $"{tag}: cell 1 right half");
            AssertRed(pixels, width, 50, 45, $"{tag}: cell 2 left half, proving repetition");

            AssertWhite(pixels, width, 20, 25, $"{tag}: above the band stays clear");
        }
    }

    [Fact]
    public void ShadingPatternStroke_OnAnOffsetMaskedLayer_LandsWhereItWasClipped()
    {
        foreach (IGraphicsFactory factory in Backends)
        {
            string tag = Tag(factory);
            byte[] pixels = Render(PdfFixtures.CreateOffsetMaskedShadingStrokePdf(), factory, out int width, out _);

            // The window is user x 100..190, y 40..60 and the band's stroke covers x 110..190, so the
            // ramp is visible across the window and the opaque mask leaves it alone.
            byte near = Gray(pixels, width, 50, 120);
            byte far = Gray(pixels, width, 50, 185);
            Assert.True(near < 170, $"{tag}: the masked window's dark end should show the pattern (got {near})");
            Assert.True(far > 200, $"{tag}: the masked window's light end should show the pattern (got {far})");
            Assert.True(far > near + 40, $"{tag}: the masked window should carry the ramp (near={near}, far={far})");

            // Outside the window the layer paints nothing.
            AssertWhite(pixels, width, 50, 95, $"{tag}: left of the masked window");
            AssertWhite(pixels, width, 50, 195, $"{tag}: right of the masked window");
            AssertWhite(pixels, width, 20, 150, $"{tag}: above the masked window");
        }
    }

    private static string Tag(IGraphicsFactory factory) =>
        factory is PeachGraphicsFactory ? "peach" : "skia";

    private static byte[] Render(
        byte[] data, IGraphicsFactory factory, out int width, out List<PdfUnsupportedFeature> features)
    {
        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = factory.CreateContext(200, 100);
        features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        width = image.Width;
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        return pixels;
    }

    private static (byte B, byte G, byte R) Pixel(byte[] px, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (px[offset], px[offset + 1], px[offset + 2]);
    }

    private static byte Gray(byte[] px, int width, int y, int x)
    {
        (byte b, byte g, byte r) = Pixel(px, width, y, x);
        return (byte)((b + g + r) / 3);
    }

    private static void AssertWhite(byte[] px, int width, int y, int x, string where)
    {
        (byte b, byte g, byte r) = Pixel(px, width, y, x);
        Assert.True(r > 240 && g > 240 && b > 240, $"{where}: expected white, got R={r} G={g} B={b}");
    }

    private static void AssertRed(byte[] px, int width, int y, int x, string where)
    {
        (byte b, byte g, byte r) = Pixel(px, width, y, x);
        Assert.True(r > 170 && g < 90 && b < 90, $"{where}: expected red, got R={r} G={g} B={b}");
    }
}
