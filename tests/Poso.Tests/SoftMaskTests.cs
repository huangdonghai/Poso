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
    /// ExtGState <c>/SMask</c>: an element drawn while a luminosity mask is in effect is masked by the mask
    /// group's own rendering. The mask is black on the left and white on the right, so the red square may
    /// only appear on the right: MuPDF reads a luminosity mask's coverage as the group's luminance, which
    /// is what the rendered reference shows. /SMask is also a Mask <b>dictionary</b>, not a stream - the
    /// field used to be always null, so nothing read it at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ExtGStateLuminosityMask_KeepsOnlyTheWhiteHalf(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskedFormPdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) left = Sample(px, image.Width, 100, 50);
        (byte B, byte G, byte R) right = Sample(px, image.Width, 100, 150);

        Assert.True(right.R > 150 && right.G < 90 && right.B < 90, $"white-masked half should stay red (got {right})");
        Assert.True(left.R > 200 && left.G > 200 && left.B > 200, $"black mask must erase the element (got {left})");
    }

    /// <summary>
    /// ExtGState <c>/SMask</c> on a plain path fill, with a <c>/TR</c> that maps 0 to 0.5: the mask group
    /// covers only the page's left half, and the half it leaves transparent has to come out at half
    /// strength. An alpha mask's value is the transfer function applied to the group's alpha, and the
    /// group's alpha outside its box is 0 - not "no coverage at all". PDFium paints pdf.js
    /// smask_alpha_oob_transfer.pdf that way: (51,153,230) inside the group's box and (152,203,242), the
    /// element at 50%, everywhere outside it. Verified on this fixture: (0,0,255) at x=50 and
    /// (128,128,255) at x=150.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ExtGStateAlphaMaskOnAPath_LiftsTheUncoveredAreaToTheTransferFunction(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskedRectPdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) inside = Sample(px, image.Width, 100, 50);
        (byte B, byte G, byte R) outside = Sample(px, image.Width, 100, 150);

        Assert.True(inside.B > 150 && inside.R < 90 && inside.G < 90, $"inside the mask box should stay blue (got {inside})");
        Assert.True(outside.R is > 110 and < 150 && outside.G is > 110 and < 150 && outside.B > 230,
            $"outside the mask box should be the element at TR(0)=0.5, half blue (got {outside})");
    }

    /// <summary>
    /// The other side of the same rule: with no <c>/TR</c> the transfer function is the identity, so the
    /// area the group leaves transparent stays at 0 and the element is erased there. pdf.js
    /// smask_alpha_oob.pdf is this shape and both references paint it this way.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ExtGStateAlphaMaskWithoutTransfer_ErasesTheUncoveredArea(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskedRectPdf(transferFunction: false));
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) outside = Sample(px, image.Width, 100, 150);
        Assert.True(outside.R > 200 && outside.G > 200 && outside.B > 200, $"outside the mask box must be erased (got {outside})");
    }

    /// <summary>
    /// An image that carries its own <c>/SMask</c> is not also masked by the ExtGState group mask, which is
    /// what MuPDF does with it (measured on this fixture: (255,0,0) inside the square, (255,255,255)
    /// outside). Multiplying both instead erases the image, because the group here paints nothing and its
    /// all-black backdrop is zero coverage.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ImageWithItsOwnSoftMask_KeepsTheExtGStateMaskAtBay(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskedImagePdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) inside = Sample(px, image.Width, 100, 100);
        Assert.True(inside.R > 200 && inside.G < 60 && inside.B < 60, $"the image's own mask stands in for the group's (got {inside})");
    }

    /// <summary>
    /// A transparency group consumes the soft mask at its boundary: the mask in force at <c>Do</c> is cut
    /// into the group's own pixels, and the content inside starts unmasked - so a form that clears the
    /// mask with its own <c>/SMask /None</c> cannot undo it. MuPDF renders this fixture masked on the
    /// right half; a plain form with the same content comes out whole, which the next test pins.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ExtGStateMaskOnATransparencyGroup_SurvivesTheFormClearingIt(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskClearedInsideAFormPdf(transparencyGroup: true));
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) left = Sample(px, image.Width, 100, 50);
        (byte B, byte G, byte R) right = Sample(px, image.Width, 100, 150);

        Assert.True(right.R > 150 && right.G < 90 && right.B < 90, $"white-masked half should stay red (got {right})");
        Assert.True(left.R > 200 && left.G > 200 && left.B > 200, $"black mask must erase the group (got {left})");
    }

    /// <summary>
    /// The same content without a transparency group: there is no boundary for the mask to bind to, so it
    /// stays in the graphics state and the form's own <c>/SMask /None</c> clears it. The square comes out
    /// over both halves. This is the half of the rule that makes the test above meaningful.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void ExtGStateMaskOnAPlainForm_IsClearedByTheFormItself(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSoftMaskClearedInsideAFormPdf(transparencyGroup: false));
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) left = Sample(px, image.Width, 100, 50);
        (byte B, byte G, byte R) right = Sample(px, image.Width, 100, 150);

        Assert.True(left.R > 150 && left.G < 90 && left.B < 90, $"the cleared mask must leave the left half red (got {left})");
        Assert.True(right.R > 150 && right.G < 90 && right.B < 90, $"and the right half too (got {right})");
    }

    /// <summary>
    /// A masked transparency group whose surface origin is not the page origin. The back end puts the
    /// group on an off-surface that starts where its clip does, so half-strength blue has to land in the
    /// top-right quadrant rather than nowhere: shifting that surface's matrix in user space instead of
    /// device space displaces the content by the origin, scaled and flipped by the page matrix.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskCapableFactories))]
    public void MaskedGroupAwayFromTheOrigin_DrawsWhereItsBoxIs(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateOffsetTransparencyGroupPdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // User (150,150) -> device (col 150, row 50); 50% blue over white.
        (byte B, byte G, byte R) inside = Sample(px, image.Width, 50, 150);
        Assert.True(inside.B > 200 && inside.R is > 90 and < 200 && inside.G is > 90 and < 200,
            $"the masked group should paint half-strength blue where its box is (got {inside})");

        // Outside the group's box: untouched background.
        (byte B, byte G, byte R) outside = Sample(px, image.Width, 50, 50);
        Assert.True(outside.R > 200 && outside.G > 200 && outside.B > 200, $"outside the box stays white (got {outside})");
    }

    // Both back ends composite their groups on an off-surface they own, so the mask can be cut in
    // before the layer is drawn back.
    public static IEnumerable<object[]> MaskCapableFactories()    {
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
