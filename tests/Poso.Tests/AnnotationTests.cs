using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M4: annotation rendering. A /Square appearance stream paints into its Rect; a /Highlight markup
/// tints its QuadPoints; untouched background stays neutral.
/// </summary>
public class AnnotationTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void AppearanceStreamAndHighlightBothRender()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAnnotationPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // /Square appearance: user (40,40) -> device (col 40, row 60): pure red.
        (byte r, byte g, byte b) = Sample(pixels, image.Width, 60, 40);
        Assert.True(r > 170 && g < 90 && b < 90, $"/Square appearance should paint red (r={r} g={g} b={b})");

        // /Highlight: user (115,50) -> device (col 115, row 50): yellow tint (blue drops, red stays high).
        (byte hr, byte hg, byte hb) = Sample(pixels, image.Width, 50, 115);
        Assert.True(hr > 200 && hg > 200 && hb < 200, $"Highlight should tint yellow-ish (r={hr} g={hg} b={hb})");

        // Untouched background: user (180,90) -> device (col 180, row 10): stays light gray.
        (byte br, byte bg, byte bb) = Sample(pixels, image.Width, 10, 180);
        Assert.True(br is > 200 and < 245 && bg is > 200 and < 245 && bb is > 200 and < 245, $"Background should be light gray (r={br} g={bg} b={bb})");
    }

    /// <summary>
    /// A form field's widget carries no appearance stream here, which is the common case for a file that
    /// leaves them to the viewer (/NeedAppearances, or no /AP at all). MuPDF synthesises one from /MK,
    /// /DA and the field value; a viewer that does not must leave the rectangle as the content drew it
    /// rather than paint a mark of its own over it.
    /// </summary>
    [Fact]
    public void WidgetWithoutAppearanceStreamIsNotPainted()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAnnotationPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // The widget's Rect is user (20,70)-(60,90) -> device cols 20..60, rows 10..30.
        (byte r, byte g, byte b) = Sample(pixels, image.Width, 20, 40);
        Assert.True(r is > 200 and < 245 && g is > 200 and < 245 && b is > 200 and < 245,
            $"an appearance-less widget must leave its rect as background (r={r} g={g} b={b})");
    }

    /// <summary>
    /// A check box keeps one appearance stream per state and names the current one with /AS; the
    /// dictionary it lives in is not a stream, so reading /AP /N as one loses the widget entirely.
    /// </summary>
    [Fact]
    public void WidgetWithStateDictionaryPaintsTheStateNamedByAS()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAnnotationPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // Its Rect is user (80,70)-(120,90) -> device cols 80..120, rows 10..30, and /AS is /Off, whose
        // stream paints green; the /Yes state would paint blue.
        (byte r, byte g, byte b) = Sample(pixels, image.Width, 20, 100);
        Assert.True(g > 200 && r < 90 && b < 90, $"the /Off appearance should paint green (r={r} g={g} b={b})");
    }

    private static (byte R, byte G, byte B) Sample(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
