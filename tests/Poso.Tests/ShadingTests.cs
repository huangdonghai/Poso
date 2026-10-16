using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7: axial (Type 2) shading rendering. A black-to-white horizontal gradient must darken the left
/// edge and lighten the right edge, with a mid-tone in the centre.
/// </summary>
public class ShadingTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void AxialShading_ProducesHorizontalGradient()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAxialShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        byte left = GrayAt(pixels, image.Width, 50, 25);
        byte middle = GrayAt(pixels, image.Width, 50, 100);
        byte right = GrayAt(pixels, image.Width, 50, 175);

        Assert.True(left < 90, $"Left edge should be dark (got {left})");
        Assert.True(right > 170, $"Right edge should be light (got {right})");
        Assert.True(middle > left && middle < right, $"Centre should be between the ends (left={left} mid={middle} right={right})");
    }

    [Fact]
    public void RadialShading_DarkensTowardCentre()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateRadialShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        byte centre = GrayAt(pixels, image.Width, 50, 100);   // user (100,50): t = 0 -> black
        byte corner = GrayAt(pixels, image.Width, 90, 10);     // user (10,10): ~0.49 radius -> mid-gray

        Assert.True(centre < 40, $"Radial centre should be dark (got {centre})");
        Assert.True(corner > centre, $"Radial corner should be lighter than centre (centre={centre} corner={corner})");
    }

    /// <summary>
    /// A shading defined in a Separation space paints the tint transform's colour, not the raw tint.
    /// Regression: the shading reader only understood device spaces, so a Separation or DeviceN array
    /// fell through to its base and the single tint was painted as if it were a grey/red component -
    /// which turned a full-page green wash on an Illustrator cover near black.
    /// </summary>
    [Fact]
    public void SeparationShading_PaintsTheTransformedTint()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSeparationShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte B, byte G, byte R) left = At(pixels, image.Width, 50, 10);
        (byte B, byte G, byte R) right = At(pixels, image.Width, 50, 190);

        Assert.True(left.R > 150 && left.G < 80, $"tint 0 should be red (got {left})");
        Assert.True(right.G > 150 && right.R < 80, $"tint 1 should be green (got {right})");
    }

    /// <summary>
    /// <c>/Extend [true true]</c> must paint the end colour past the end circle. The entries are PDF
    /// booleans, so a reader that asks for a number sees false and clips the gradient: a full-page wash
    /// then stops dead in a hard circle in the middle of the page.
    /// </summary>
    [Fact]
    public void ExtendedRadialShading_PaintsPastTheEndCircle()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateExtendedRadialShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 200);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        foreach ((int x, int y) in new[] { (5, 5), (195, 5), (5, 195), (195, 195) })
        {
            byte gray = GrayAt(pixels, image.Width, y, x);
            Assert.True(gray < 40, $"page corner ({x},{y}) should carry the extended colour (got {gray})");
        }
    }

    private static (byte, byte, byte) At(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static byte GrayAt(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return pixels[offset]; // BGRA gray channel (R==G==B)
    }
}
