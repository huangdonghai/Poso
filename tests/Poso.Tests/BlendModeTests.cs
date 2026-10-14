using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M3: ExtGState blend modes reach the back-end. A blue rectangle painted with /BM /Multiply over a
/// green one yields near-black in the overlap, whereas normal painting leaves it blue.
/// </summary>
public class BlendModeTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void MultiplyBlend_ProducesDarkerOverlapThanNormal()
    {
        (byte r, byte g, byte b) multiply = OverlapPixel(multiply: true);
        (byte r, byte g, byte b) normal = OverlapPixel(multiply: false);

        // Normal: the blue rect simply covers the green one in the overlap.
        Assert.True(normal.b > 170 && normal.r < 90 && normal.g < 90, $"Normal overlap should be blue (r={normal.r} g={normal.g} b={normal.b})");

        // Multiply: green * blue = black in the overlap.
        Assert.True(multiply.r < 90 && multiply.g < 90 && multiply.b < 90, $"Multiply overlap should be near-black (r={multiply.r} g={multiply.g} b={multiply.b})");

        Assert.True(
            multiply.r + multiply.g + multiply.b < normal.r + normal.g + normal.b,
            "Multiply overlap must be darker than the normal overlap");
    }

    private static (byte R, byte G, byte B) OverlapPixel(bool multiply)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateBlendModePdf(multiply));
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // Overlap center at user (60,60) -> device (col 60, row 40).
        int offset = ((40 * image.Width) + 60) * 4;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
