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

    /// <summary>
    /// ExtGState <c>/SMask</c>: a form drawn while a luminosity mask is in effect must be masked by the
    /// mask group's own rendering. The mask is black on the left and white on the right, so the red
    /// square may only appear on the left. This never worked: /SMask is a Mask <b>dictionary</b>, and the
    /// graphics state stored it as a stream, so the field was always null and nothing read it.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ExtGStateLuminosityMask_KeepsOnlyTheBlackHalf(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskedFormPdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) left = Sample(px, image.Width, 100, 50);
        (byte B, byte G, byte R) right = Sample(px, image.Width, 100, 150);

        Assert.True(left.R > 150 && left.G < 90 && left.B < 90, $"masked-left should stay red (got {left})");
        Assert.True(right.R > 200 && right.G > 200 && right.B > 200, $"white mask must erase the element (got {right})");
    }

    // Both back ends composite their groups on an off-surface they own, so the mask can be cut in
    // before the layer is drawn back.
    public static IEnumerable<object[]> MaskCapableFactories()
    {
        yield return [Poso.PeachDrawing.PeachGraphicsFactory.Default];
        yield return [SkiaGraphicsFactory.Default];
    }

    /// <summary>
    /// A form whose content leaves a <c>q</c> open must not cost the page its transparency group. The
    /// group's <c>Restore</c> pops whatever level is on top, so one stray save turns the whole page
    /// white; the interpreter has to hand the back end back the save depth it was given.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void LeakedSaveInAForm_StillCompositesThePage(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateLeakedSaveFormPdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) red = Sample(px, image.Width, 100, 50);
        (byte B, byte G, byte R) green = Sample(px, image.Width, 100, 150);

        Assert.True(red.R > 170 && red.G < 90 && red.B < 90, $"left half should be red (got {red})");
        Assert.True(green.G > 170 && green.R < 90 && green.B < 90, $"right half should stay green (got {green})");
    }

    private static (byte B, byte G, byte R) Sample(byte[] px, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (px[offset], px[offset + 1], px[offset + 2]);
    }
}
