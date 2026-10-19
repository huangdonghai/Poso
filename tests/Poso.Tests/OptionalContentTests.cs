using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7/C: optional content (layers). The "Red" layer is ON and the "Blue" layer OFF by default;
/// toggling flips which rectangle renders.
/// </summary>
public class OptionalContentTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void DefaultVisibility_ShowsOnHidesOffLayer()
    {
        (bool red, bool blue) = Render(PdfFixtures.CreateOcgPdf(), toggle: null);
        Assert.True(red, "the ON layer should render");
        Assert.False(blue, "the OFF layer should be hidden");
    }

    [Fact]
    public void TogglingLayersFlipsVisibility()
    {
        (bool red, bool blue) = Render(PdfFixtures.CreateOcgPdf(), toggle: document =>
        {
            document.SetOptionalContentGroupVisible(6, false); // hide Red
            document.SetOptionalContentGroupVisible(7, true);  // show Blue
        });
        Assert.False(red, "hidden Red layer should not render");
        Assert.True(blue, "shown Blue layer should render");
    }

    /// <summary>
    /// A form XObject's own /OC (an OCMD here) that resolves to an off OCG must keep the form from
    /// painting. Ignoring the XObject-level /OC buried pdf.js issue12007_reduced.pdf under the two image
    /// forms whose layers its /OCProperties turns off.
    /// </summary>
    [Fact]
    public void FormWithHiddenOptionalContent_DoesNotPaint()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateFormWithHiddenOptionalContentPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int offset = ((50 * image.Width) + 50) * image.BytesPerPixel;
        (byte b, byte g, byte r) = (px[offset], px[offset + 1], px[offset + 2]);

        Assert.True(r > 250 && g > 250 && b > 250,
            $"the form in an off optional-content group must not paint (got r={r} g={g} b={b})");
    }

    // Renders and reports whether the Red (user ~40,40) and Blue (user ~150,40) samples are painted.
    private static (bool Red, bool Blue) Render(byte[] pdf, System.Action<PdfDocument>? toggle)
    {
        using PdfDocument document = PdfDocument.FromArray(pdf);
        toggle?.Invoke(document);

        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // device (col 40, row 60) -> user (40,40) inside Red rect; (col 150, row 60) -> user (150,40) inside Blue.
        (byte rb, byte rg, byte rr) = Sample(px, image.Width, 60, 40);
        (byte bb, byte bg, byte br) = Sample(px, image.Width, 60, 150);

        bool redPainted = rr > 170 && rg < 90 && rb < 90;
        bool bluePainted = bb > 170 && br < 90 && bg < 90;
        return (redPainted, bluePainted);
    }

    private static (byte B, byte G, byte R) Sample(byte[] px, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (px[offset], px[offset + 1], px[offset + 2]);
    }
}
