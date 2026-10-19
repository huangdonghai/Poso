using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

public class ContentRenderTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Render_FilledRect_LandsAtExpectedDevicePixels()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Rect: PDF x in [10,90], y in [10,50] (y-up) -> device y in [50,90]
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 50, 30);
        Assert.True(b > 180 && g is > 60 and < 160 && r < 120, $"Rectangle color wrong at (50,30): r={r} g={g} b={b}");

        // Outside the rect stays white
        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 5, 5);
        Assert.True(or > 200 && og > 200 && ob > 200, $"Background should stay white at (5,5): r={or} g={og} b={ob}");
    }

    [Fact]
    public void Render_StrokedLineAndText_ProduceInk()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Red stroked line at PDF y=70 -> device y=30, x from 20 to 180
        int redOnLine = CountColor(pixels, image.Width, 30, 20, 180, r: 200, g: 60, b: 60);
        Assert.True(redOnLine > 50, $"Stroked line pixels too few: {redOnLine}");

        // Text baseline at PDF y=50 -> device y=50; glyph ink should appear around x=20..70
        int inkNearText = CountInk(pixels, image.Width, 40, 70, 20, 90);
        Assert.True(inkNearText > 20, $"Text ink pixels too few: {inkNearText}");
    }

    [Fact]
    public void Render_Rotated90_TransposesContent()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        // Page 200x100 rotated 90 degrees -> device 100x200
        using IGraphicsContext context = Factory.CreateContext(100, 200);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 90f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The whole page is transposed: (x, y) -> (y, x).
        // Main rect: user x in [10,90], y in [10,50] -> device x in [10,50], y in [10,90]
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 50, 30);
        Assert.True(b > 180 && r < 120, $"Rotated rectangle color wrong at (x=30,y=50): r={r} g={g} b={b}");

        // The small blue rect (user (10,10)-(20,20) after the 5,5 translate) -> device (10,10)-(20,20)
        (byte sb, byte sg, byte sr, _) = PixelAt(pixels, image.Width, 15, 15);
        Assert.True(sb > 180 && sr < 100, $"Small rect should be blue at (x=15,y=15): r={sr} g={sg} b={sb}");
    }

    [Fact]
    public void Render_ImageXObject_DrawsScaledBitmap()
    {
        byte[] data = PdfFixtures.CreateImagePdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Image occupies device x in [0,100], y in [50,100] (PDF y-up origin at bottom)
        // Top-left of the image is red
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 60, 10);
        Assert.True(r > 180 && g < 100 && b < 100, $"Image top-left should be red at (10,60): r={r} g={g} b={b}");

        // Bottom-right of the image is yellow
        (byte yb, byte yg, byte yr, _) = PixelAt(pixels, image.Width, 90, 90);
        Assert.True(yr > 180 && yg > 180 && yb < 100, $"Image bottom-right should be yellow at (90,90): r={yr} g={yg} b={yb}");    }

    [Fact]
    public void Render_Zoomed_DoublesInkCoverage()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        using (IGraphicsContext context = Factory.CreateContext(200, 100))
        {
            document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
            using IImage image = context.Snapshot();
            int at1x = CountInk(ReadPixels(image), image.Width, 0, 100, 0, 200);
            Assert.True(at1x > 100);
        }

        using (IGraphicsContext context = Factory.CreateContext(400, 200))
        {
            document.Pages[0].Render(Factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
            using IImage image = context.Snapshot();
            int at2x = CountInk(ReadPixels(image), image.Width, 0, 200, 0, 400);
            Assert.True(at2x > 100);
        }
    }

    [Fact]
    public void Render_TextGlyph_IsUprightAndNotMirrored()
    {
        byte[] data = PdfFixtures.CreateGlyphPdf('b');

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Baseline: PDF y=20 (y-up) -> device row 80. A lowercase 'b' has no descender, so nearly
        // all of its ink must sit above the baseline row; a vertically mirrored outline flips that.
        int above = CountInk(pixels, image.Width, 0, 79, 0, image.Width);
        int below = CountInk(pixels, image.Width, 81, image.Height, 0, image.Width);
        Assert.True(above > below * 10, $"'b' must be drawn above its baseline: above={above}, below={below}");

        // The stem is the leftmost inked column and spans the whole ascender, while the bowl reaches
        // the right edge only over the x-height; a horizontally mirrored outline swaps the two.
        (int leftSpan, int rightSpan) = EdgeColumnSpans(pixels, image.Width);
        Assert.True(leftSpan > rightSpan, $"'b' stem must be on the left: leftSpan={leftSpan}, rightSpan={rightSpan}");
    }

    /// <summary>
    /// A resource dictionary reached through an indirect reference must resolve like an inline one: the
    /// page otherwise draws nothing at all and reports nothing, because the name simply never resolves.
    /// </summary>
    [Fact]
    public void Render_XObjectResourcesReachedThroughIndirectReference()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectXObjectPdf());
        using IGraphicsContext context = Factory.CreateContext(4, 4);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.Empty(features);
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);
        for (int i = 0; i < 16; i++)
        {
            (byte b, byte g, byte r, byte a) = PixelAt(pixels, image.Width, i / image.Width, i % image.Width);
            Assert.True(r > 200 && g < 60 && b < 60, $"pixel {i} should be the image's red, got r={r} g={g} b={b}");
        }
    }

    [Fact]
    public void Render_StrokedText_KeepsThinOutlineNotBlob()
    {
        // Regression: the text line width was scaled by the em-to-user text matrix (font size), so a
        // thin 0.5pt stroke at 40pt became a ~20px blob that filled the letter's counter.
        byte[] data = PdfFixtures.CreateStrokedTextPdf();
        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(120, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Bounding box of the stroked 'O'.
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, y, x);
                if (r < 200 || g < 200 || b < 200)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        Assert.True(maxX > minX && maxY > minY, "stroked 'O' produced no ink");

        // The stroke must not enlarge the glyph's own box: a back end that clamps the line width to a
        // fraction of the font size re-scales it by the text matrix again and inflates this 'O' from
        // 28x30 to 59x61.
        Assert.True(maxX - minX <= 34 && maxY - minY <= 36,
            $"stroked 'O' must stay inside its glyph box, got {maxX - minX}x{maxY - minY}");
        int ink = 0;
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            int o = i * 4;
            if (pixels[o] < 200 || pixels[o + 1] < 200 || pixels[o + 2] < 200) { ink++; }
        }

        Assert.True(ink is > 0 and < 600, $"a 0.5pt stroke at 40pt should ink a few hundred pixels, got {ink}");

        // A correct thin stroke leaves the counter (letter center) white; the blob bug fills it.
        int cx = (minX + maxX) / 2;
        int cy = (minY + maxY) / 2;
        (byte cb, byte cg, byte cr, _) = PixelAt(pixels, image.Width, cy, cx);
        Assert.True(cr > 200 && cg > 200 && cb > 200,
            $"stroked 'O' counter must stay white (thin outline); center ({cx},{cy}) was r={cr} g={cg} b={cb}");
    }

    /// <summary>
    /// A form whose content opens a q but never closes it must not strand the graphics state it was
    /// drawn under (here ca .5). If the stray q is left on the state stack, the page-level Q restores the
    /// form's 0.5 alpha and the following fill paints translucent - pdf.js issue1905_reduced.pdf washed
    /// every image after such a form to half opacity.
    /// </summary>
    [Fact]
    public void FormWithUnclosedQ_DoesNotLeakItsAlphaToLaterContent()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateFormWithUnclosedStatePdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The green square is painted after the form, at full opacity; a leaked ca .5 would wash it.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 50, 50);
        Assert.True(r < 60 && g > 200 && b < 60, $"green must stay opaque after an unclosed-q form (r={r} g={g} b={b})");
    }

    /// <summary>
    /// W before the path is built still clips to that path: MuPDF and PDFium apply the clip when the path
    /// is consumed. pdf.js clippath.pdf opens with a lone W and expects the rectangle it then builds to
    /// confine the following fill, not the whole page.
    /// </summary>
    [Fact]
    public void ClipBeforePath_ClipsToTheFollowingPath()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateDeferredClipPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        (byte ib, byte ig, byte ir, _) = PixelAt(pixels, image.Width, 50, 100);
        Assert.True(ir < 20 && ig < 20 && ib < 20, $"inside the deferred clip must be black (r={ir} g={ig} b={ib})");

        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 10, 20);
        Assert.True(or > 240 && og > 240 && ob > 240, $"outside the deferred clip must stay white (r={or} g={og} b={ob})");
    }

    private static byte[] ReadPixels(IImage image)
    {
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        return pixels;
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    /// <summary>Vertical ink span of the first and of the last column that carries ink.</summary>
    private static (int LeftSpan, int RightSpan) EdgeColumnSpans(byte[] pixels, int width)
    {
        int height = pixels.Length / (width * 4);
        int left = -1;
        int right = -1;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                (byte b, byte g, byte r, _) = PixelAt(pixels, width, y, x);
                if (r < 200 || g < 200 || b < 200)
                {
                    if (left < 0)
                    {
                        left = x;
                    }

                    right = x;
                    break;
                }
            }
        }

        Assert.True(left >= 0 && right > left, "No glyph ink found in the rendered page");
        return (ColumnSpan(pixels, width, height, left), ColumnSpan(pixels, width, height, right));
    }

    private static int ColumnSpan(byte[] pixels, int width, int height, int x)
    {
        int first = -1;
        int last = -1;

        for (int y = 0; y < height; y++)
        {
            (byte b, byte g, byte r, _) = PixelAt(pixels, width, y, x);
            if (r < 200 || g < 200 || b < 200)
            {
                if (first < 0)
                {
                    first = y;
                }

                last = y;
            }
        }

        return last - first + 1;
    }

    private static int CountInk(byte[] pixels, int width, int yFrom, int yTo, int xFrom, int xTo)
    {
        int count = 0;
        for (int y = yFrom; y < yTo; y++)
        {
            for (int x = xFrom; x < xTo; x++)
            {
                (byte b, byte g, byte r, _) = PixelAt(pixels, width, y, x);
                if (r < 200 || g < 200 || b < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountColor(byte[] pixels, int width, int y, int xFrom, int xTo, byte r, byte g, byte b)
    {
        int count = 0;
        for (int x = xFrom; x < xTo; x++)
        {
            (byte pb, byte pg, byte pr, _) = PixelAt(pixels, width, y, x);
            if (Math.Abs(pr - r) < 60 && Math.Abs(pg - g) < 60 && Math.Abs(pb - b) < 60)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// A dash array has to come in pairs. Given three entries, Skia's dash path effect throws and the
    /// whole page is lost; the pattern is dropped and the line strokes solid instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShadingTests.GraphicsFactories), MemberType = typeof(ShadingTests))]
    public void OddLengthDashArray_StillStrokesTheLine(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateOddDashPdf());
        using IGraphicsContext context = factory.CreateContext(100, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The line is antialiased across a row or two, so count the darkest band rather than one row.
        int longest = 0;
        for (int y = 45; y <= 55; y++)
        {
            int run = 0;
            for (int x = 10; x < 91; x++)
            {
                if (PixelAt(pixels, image.Width, y, x).Item2 < 200)
                {
                    run++;
                }
            }

            longest = Math.Max(longest, run);
        }

        Assert.True(longest >= 75, $"the dash pattern should have been dropped and the line stroked solid (longest run {longest})");
    }
}
