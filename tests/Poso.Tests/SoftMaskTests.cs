using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M3: image /SMask (soft mask -> per-pixel alpha). The fixture paints a 2x2 RGB image
/// (red/green/blue/white) scaled across a 100x100 page, with a matching 2x2 DeviceGray alpha
/// (0,255,128,0). Over a white background that means: alpha 0 shows the background, alpha 255
/// stays opaque, and alpha 128 becomes a translucent blend. The assertions are orientation
/// agnostic (they classify the four quadrant centres) so they lock in the compositing behaviour
/// regardless of the exact image y-flip.
/// </summary>
public class SoftMaskTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void ImageSoftMask_CompositesAlphaAcrossQuadrants()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateImageSoftMaskPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // Quadrant centres; a 2x2 source stretched to 100x100 keeps these well inside each block.
        (byte B, byte G, byte R)[] quadrants =
        {
            Sample(px, image.Width, 25, 25),
            Sample(px, image.Width, 25, 75),
            Sample(px, image.Width, 75, 25),
            Sample(px, image.Width, 75, 75),
        };

        int green = 0;
        int translucentBlue = 0;
        int white = 0;
        int opaqueBlue = 0;
        int red = 0;
        foreach ((byte B, byte G, byte R) c in quadrants)
        {
            if (c.R > 150 && c.G < 100 && c.B < 100)
            {
                red++; // would only appear if the alpha-0 red pixel were NOT made transparent
            }

            if (c.G > 150 && c.R < 100 && c.B < 100)
            {
                green++; // opaque green (alpha 255)
            }
            else if (c.B > 150 && c.R < 90 && c.G < 90)
            {
                opaqueBlue++; // would only appear if the alpha-128 blue pixel were opaque
            }
            else if (c.B > 150 && c.R >= 90 && c.R < 200 && c.G >= 90 && c.G < 200)
            {
                translucentBlue++; // blue blended toward the white background (alpha 128)
            }

            if (c.R > 150 && c.G > 150 && c.B > 150)
            {
                white++; // background shows through the alpha-0 pixels
            }
        }

        Assert.Equal(1, green);
        Assert.Equal(1, translucentBlue);
        Assert.Equal(2, white);
        Assert.Equal(0, opaqueBlue);
        Assert.Equal(0, red);
    }

    private static (byte B, byte G, byte R) Sample(byte[] px, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (px[offset], px[offset + 1], px[offset + 2]);
    }
}
