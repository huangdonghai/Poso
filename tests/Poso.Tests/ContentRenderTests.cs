using System.IO;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

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

    /// <summary>
    /// A Brotli-compressed content stream must decode before parsing. Brotli is not in ISO 32000, but pdf.js
    /// accepts it and some generators use it for every stream - object and xref streams included - so a
    /// missing filter leaves the whole file unreadable (pdf.js Brotli-Prototype-FileA.pdf).
    /// </summary>
    [Fact]
    public void BrotliContent_DecodesAndRenders()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateBrotliContentPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 50, 50);
        Assert.True(r > 200 && g < 60 && b < 60, $"the Brotli content must paint a red square (r={r} g={g} b={b})");
    }

    /// <summary>
    /// When an embedded TrueType program cannot be loaded the text must still paint through a system
    /// fallback. pdf.js bug1200096.pdf's subset program does not load and its Skia render was blank while
    /// PeachDrawing, which also falls back, painted it.
    /// </summary>
    [Fact]
    public void TextWithBrokenEmbeddedFont_FallsBackAndPaints()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextWithBrokenEmbeddedFontPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 50);

        using (SkiaFont face = SkiaFont.Create(new byte[] { 1, 2, 3 }, "KVIQXV+Times New Roman")!)
        {
            Assert.True(face.UsedFallback, "a broken program must fall back");
            Assert.NotEqual(0, face.GetGlyphId('A'));
            Assert.True(face.GetAdvanceWidth(face.GetGlyphId('A')) > 0f, "fallback glyph must have an advance");
        }

        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 50, $"the fallback face must paint the text, got {ink} dark pixels");
    }

    /// <summary>
    /// Square and Circle annotations without an /AP stream must still be drawn from their /Rect, /C, /IC
    /// and border width - the viewer generates the appearance (ISO 32000-1 12.5.5.2/12.5.5.3), as pdf.js
    /// annotation-square-circle-without-appearance.pdf relies on.
    /// </summary>
    [Fact]
    public void SquareAndCircleAnnotations_WithoutAppearance_AreDrawn()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSquareCircleAnnotationPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Square top edge: user (25,37) -> device row 63; the red stroke must be there.
        (byte sb, byte sg, byte sr, _) = PixelAt(pixels, image.Width, 63, 25);
        Assert.True(sr > 180 && sg < 90 && sb < 90, $"the Square must be stroked in red (r={sr} g={sg} b={sb})");

        // Circle centre: user (75,75) -> device row 25; the green interior must fill it.
        (byte cb, byte cg, byte cr, _) = PixelAt(pixels, image.Width, 25, 75);
        Assert.True(cg > 180 && cr < 120 && cb < 120, $"the Circle must be filled with its interior colour (r={cr} g={cg} b={cb})");

        // Circle bounding-box corner: user (62,62) -> device row 38; outside the ellipse, so still white.
        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 38, 62);
        Assert.True(or > 240 && og > 240 && ob > 240, $"outside the ellipse must stay clear (r={or} g={og} b={ob})");
    }

    /// <summary>
    /// A Type3 glyph procedure that begins with d1 is a stencil: it is painted with the text's colour and
    /// its own colour operators are ignored (MuPDF sets FZ_DEVFLAG_MASK for d1, FZ_DEVFLAG_COLOR for d0).
    /// pdf.js issue12705.pdf's glyphs fill themselves white, which left the page empty.
    /// </summary>
    [Fact]
    public void Type3StencilGlyph_PaintsInTextColor()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType3StencilGlyphPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The glyph box spans user x 10..58, y 40..88; sample its middle (device row 40, column 30).
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 40, 30);
        Assert.True(r < 80 && g < 80 && b < 80, $"a d1 glyph must paint in the text colour, not its own white (r={r} g={g} b={b})");

        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 5, 5);
        Assert.True(or > 240 && og > 240 && ob > 240, $"outside the glyph must stay white (r={or} g={og} b={ob})");
    }

    /// <summary>
    /// A Type3 glyph procedure runs as if surrounded by q and Q (ISO 32000-1 9.5.5), so the graphics
    /// state it sets must not outlive it. pdf.js issue19634.pdf's charprocs set a Luminosity /SMask;
    /// the mask leaked into the next text run and painted it under a white layer, so the whole line
    /// vanished. Here the glyph fills itself red and the page then paints a rectangle with no colour
    /// of its own: the rectangle must be the default black, not the glyph's red.
    /// </summary>
    [Fact]
    public void Type3Glyph_GraphicsStateDoesNotLeakOut()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType3StateLeakPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The rectangle spans user x 60..90, y 10..40; device row 75, column 75 sits inside it.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 75, 75);
        Assert.True(r < 80 && g < 80 && b < 80, $"the rectangle must paint in the default black, not the glyph's red (r={r} g={g} b={b})");

        // The glyph itself is red: user x 10..58, y 40..88; device row 40, column 30.
        (byte gb, byte gg, byte gr, _) = PixelAt(pixels, image.Width, 40, 30);
        Assert.True(gr > 180 && gg < 90 && gb < 90, $"the d0 glyph must keep its own red (r={gr} g={gg} b={gb})");
    }

    /// <summary>
    /// A Type3 glyph procedure with no /Resources on the font inherits the resources in force where the
    /// text was shown - pdf.js pr4922.pdf's glyph procedures show text with the page's own fonts, and
    /// resolving only against the font's (absent) resources left the page empty.
    /// </summary>
    [Fact]
    public void Type3Glyph_InheritsTheCallersResources()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType3InheritedResourcesPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 50, $"the glyph procedure must resolve /F1 through the caller's resources, got {ink} dark pixels");
    }

    /// <summary>
    /// A tiling pattern whose /Matrix shears the lattice must place each cell as the parallelogram it is,
    /// not as that parallelogram's bounding box: with [1 .5 0 1 0 0] and 10x10 cells 20 apart, the point
    /// (5,18) lies inside a cell's box but outside the sheared cell, so it has to stay white.
    /// </summary>
    [Fact]
    public void ShearedTilingPattern_KeepsTheShear()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShearedTilingPatternPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Inside the sheared cell: user (5,7) -> device row 93.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 93, 5);
        Assert.True(r < 60 && g < 60 && b < 60, $"the sheared cell must paint its interior black (r={r} g={g} b={b})");

        // Inside the cell's bounding box but outside the parallelogram: user (5,18) -> device row 82.
        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 82, 5);
        Assert.True(or > 240 && og > 240 && ob > 240, $"the gap beside the sheared cell must stay white (r={or} g={og} b={ob})");
    }

    /// <summary>
    /// An ImageMask XObject drawn while the fill is a shading pattern must let the pattern show through the
    /// stencil only where the mask is set (ISO 32000-1 8.9.6.3); folding the pattern into a flat stencil
    /// colour painted the whole block, as pdf.js issue13372.pdf showed.
    /// </summary>
    [Fact]
    public void ImageMaskXObject_PaintsPatternThroughTheStencil()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateImageMaskWithShadingPatternPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Inside the stencil (user 25,75 -> device row 25): the gradient there is red-heavy.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 25, 25);
        Assert.True(r > 150 && r > b, $"the pattern must paint through the set mask (r={r} g={g} b={b})");

        // Below the stencil (user 25,25 -> device row 75) and beside it (user 75,75 -> device col 75).
        (byte lb, byte lg, byte lr, _) = PixelAt(pixels, image.Width, 75, 25);
        Assert.True(lr > 240 && lg > 240 && lb > 240, $"the clear mask rows must stay white (r={lr} g={lg} b={lb})");

        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 25, 75);
        Assert.True(or > 240 && og > 240 && ob > 240, $"the clear mask columns must stay white (r={or} g={og} b={ob})");
    }

    /// <summary>
    /// A transparency group without /I is non-isolated (the spec default): its elements blend against the
    /// page beneath, so a Multiply inside the group must mix with the yellow page and come out green rather
    /// than landing on an empty buffer (pdf.js knockout_blend_multiply.pdf).
    /// </summary>
    [Fact]
    public void NonIsolatedGroup_BlendsWithThePage()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateNonIsolatedBlendGroupPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Inside the multiplied block: cyan over yellow under Multiply is green.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 50, 50);
        Assert.True(g > 200 && r < 60 && b < 60, $"the group must blend with the page (r={r} g={g} b={b})");

        // Outside it the page stays yellow.
        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 95, 5);
        Assert.True(or > 240 && og > 240 && ob < 60, $"the page around the group must stay yellow (r={or} g={og} b={ob})");
    }

    /// <summary>
    /// A knockout group (/K true) clears its own earlier contents from beneath each element it receives, so
    /// a nested group has an emptied backdrop to blend with - not the blue that was already painted. Poso
    /// rendered a nested non-isolated group inline whenever it was fully opaque, which handed the nested
    /// Multiply straight to the blue and printed black wherever pdf.js knockout_inner_backdrop.pdf wants red
    /// or green.
    /// </summary>
    [Fact]
    public void KnockoutGroup_NestedBlendDoesNotSeeTheClearedBackdrop()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateKnockoutGroupWithNestedBlendPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 200);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Red only (Multiply against nothing keeps the source), green only, and their overlap.
        (byte rb, byte rg, byte rr, _) = PixelAt(pixels, image.Width, 170, 30);
        Assert.True(rr > 200 && rg < 60 && rb < 60, $"the red rect must stay red (r={rr} g={rg} b={rb})");

        (byte gb, byte gg, byte gr, _) = PixelAt(pixels, image.Width, 35, 165);
        Assert.True(gg > 200 && gr < 60 && gb < 60, $"the green rect must stay green (r={gr} g={gg} b={gb})");

        (byte ib, byte ig, byte ir, _) = PixelAt(pixels, image.Width, 100, 100);
        Assert.True(ir < 60 && ig < 60 && ib < 60, $"red multiplied by green must be black (r={ir} g={ig} b={ib})");

        // The blue the knockout group painted first still stands where the nested group never reached.
        (byte bb, byte bg, byte br, _) = PixelAt(pixels, image.Width, 195, 5);
        Assert.True(bb > 200 && br < 60 && bg < 60, $"the knockout group's own fill must survive (r={br} g={bg} b={bb})");
    }

    /// <summary>
    /// A text object in a clipping render mode replaces the clipping path with its glyph outlines - and when
    /// none of them carry an outline, that path is EMPTY, so nothing after it paints. Treating "no outline"
    /// as "no clip" let the following full-page fill through (pdf.js recursiveCompositGlyf.pdf renders blank
    /// in MuPDF and PDFium alike).
    /// </summary>
    [Fact]
    public void EmptyTextClip_PaintsNothing()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateEmptyTextClipPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
            {
                ink++;
            }
        }

        Assert.True(ink == 0, $"an empty text clip must leave the page white, got {ink} dark pixels");
    }

    /// <summary>
    /// A Type3 glyph paints by running its procedure, so a text rendering mode of 3 (invisible) or 7 has to
    /// stop that run - the advance still applies, nothing is drawn (ISO 32000-2 9.3.6). pdf.js
    /// issue5421.pdf's "3 Tr" line rendered because the procedure executed whatever the mode said.
    /// </summary>
    [Fact]
    public void Type3Glyph_RespectsInvisibleRenderMode()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType3InvisibleTextPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The Tr 0 copy at user (10,10) covers x 10..58, y 10..58; sample its middle (device row 70).
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 70, 30);
        Assert.True(r < 80 && g < 80 && b < 80, $"the Tr 0 glyph must paint (r={r} g={g} b={b})");

        // The Tr 3 copy at user (60,60) would cover x 60..100, y 60..100; sample it (device row 20, col 80).
        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 20, 80);
        Assert.True(or > 240 && og > 240 && ob > 240, $"the invisible glyph must not paint (r={or} g={og} b={ob})");
    }

    /// <summary>
    /// An ExtGState /SMask whose luminosity group paints a named shading with the {@code sh} operator must
    /// really fade the element - for a stroked path as much as a filled one. pdf.js issue18529.pdf strokes a
    /// square under exactly such a mask and comes out with no fade at all, uniformly full colour.
    /// </summary>
    [Fact]
    public void ShadingSoftMask_FadesTheElement()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShadingSoftMaskPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The top border sits at user y 95 -> device row 5. At x 10 the mask is still bright (the element
        // stays dark); at x 90 the mask has run out (Extend keeps it black) and the stroke must be gone.
        (byte lb, byte lg, byte lr, _) = PixelAt(pixels, image.Width, 5, 10);
        (byte ob, byte og, byte or_, _) = PixelAt(pixels, image.Width, 5, 90);

        Assert.True(lr < 80, $"under the bright end of the mask the stroke must stay dark (r={lr})");
        Assert.True(or_ > 200, $"past the mask's span the stroke must be erased (left r={lr}, right r={or_})");
    }

    /// <summary>
    /// Resource category dictionaries are allowed to sit behind indirect references, and operators that read
    /// them have to resolve them: here /ExtGState does, so the gs really installs the luminosity mask, and
    /// /Shading does, so the {@code sh} inside that mask's group really paints the fade.
    /// </summary>
    [Fact]
    public void IndirectResourceCategories_StillFeedTheOperators()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectResourceCategoriesPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The mask is the shading stretched over x 0..50, white at its left end and black at its right, so
        // along the top border (device row 5) the stroke has to be nearly black at x 10, half gone at x 25
        // and entirely gone by x 90.
        (byte lb, byte lg, byte lr, _) = PixelAt(pixels, image.Width, 5, 10);
        (byte mb, byte mg, byte mr, _) = PixelAt(pixels, image.Width, 5, 25);
        (byte ob, byte og, byte or_, _) = PixelAt(pixels, image.Width, 5, 90);

        Assert.True(lr < 80 && lg < 80 && lb < 80, $"the stroke must survive under the bright mask (r={lr} g={lg} b={lb})");
        Assert.True(mr > lr + 40 && or_ > mr + 40, $"the mask must fade the stroke across its span (x10 r={lr}, x25 r={mr}, x90 r={or_})");
        Assert.True(or_ > 200, $"past the dark end of the mask the stroke must be erased (right r={or_})");
    }

    /// <summary>
    /// The same shape as <see cref="ShadingSoftMask_FadesTheElement"/> with every number lifted out of
    /// pdf.js issue18529.pdf: a 65x50 page whose /CS is the array form, a stroke of width 5 over a rect at
    /// 10 10 45 30, and a mask form whose /Matrix [45 0 0 30 10 10] lands the shading exactly on the stroke
    /// while its /BBox reaches past the unit square on both axes.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void ShadingSoftMask_ReplicaOfIssue18529_FadesTheStroke(float scale)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShadingSoftMaskStrokeReplicaPdf());
        using IGraphicsContext context = Factory.CreateContext((int)(65 * scale), (int)(50 * scale));
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(scale, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The top border of the stroked rect sits at page y 37.5..42.5 -> the row at page y 40; the mask is
        // the shading stretched over x 10..55, white at its left end and black at its right. The stroke is
        // red, so the green channel says whether it survived: low where painted, high where erased.
        int row = (int)Math.Round((50 - 40f) * scale);
        (byte _, byte lg, byte _, _) = PixelAt(pixels, image.Width, row, (int)(12 * scale));
        (byte _, byte rg, byte _, _) = PixelAt(pixels, image.Width, row, (int)(52 * scale));

        Assert.True(lg < 80, $"near the bright end of the mask the red stroke must survive (scale={scale} g={lg})");
        Assert.True(rg > 200, $"near the dark end the stroke must be erased (scale={scale} left g={lg}, right g={rg})");
    }

    /// <summary>
    /// pdf.js issue8234.pdf's subset maps code 32 onto glyph 0, and its glyph 0 is a drawn letterform
    /// rather than an empty .notdef. A viewer that treats zero as "no glyph" paints nothing, while
    /// PDFium and MuPDF both draw it; the page must carry ink on both back ends.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_CodeMappedToGlyphZero_PaintsNotdef(bool peach)
    {
        byte[] data = PdfFixtures.CreateSymbolicNotdefPdf(FontsFile("symbolic-notdef.bin"));
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = factory.CreateContext(300, 250);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 1000, $"glyph zero must paint, got {ink} dark pixels");
    }

    /// <summary>
    /// The content names /F1 but the page's /Resources has no /Font entry at all (pdf.js issue5954.pdf:
    /// its resources carry only an empty /XObject). PDFium draws the run in a stock Helvetica instead of
    /// dropping it, and the page must not come out blank.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_FontMissingFromResources_DrawsInAStockFace(bool peach)
    {
        byte[] data = PdfFixtures.CreateMissingFontResourcePdf();
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = factory.CreateContext(220, 60);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 100, $"a missing font resource must still paint, got {ink} dark pixels");
    }

    /// <summary>
    /// The tiling lattice starts at the pattern's /BBox, not at the coordinate origin: this fixture's cell
    /// begins at (10, 30), and indexing the absolute pattern coordinates moves every cell up by three
    /// steps, leaving the top of the page blank.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_TilingPatternWithAnOffsetBBox_AnchorsAtTheBox(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateOffsetTilingPatternPdf());
        using IGraphicsContext context = factory.CreateContext(100, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The cell's painted quarter is (10..14, 30..34) in PDF space, i.e. device (10..14, 66..70).
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 68, 12);
        Assert.True(b < 100 && g < 100 && r < 100, $"the cell must paint at (12, 68), got r={r} g={g} b={b}");

        (byte gb, byte gg, byte gr, _) = PixelAt(pixels, image.Width, 64, 12);
        Assert.True(gb > 200 && gg > 200 && gr > 200, $"the rest of the cell stays white at (12, 64), got r={gr} g={gg} b={gb}");
    }

    /// <summary>
    /// A pattern /Matrix that mirrors pattern space must place the cell mirrored too. The cell paints its
    /// top half, so the black band lands at device y 4..8 of each cell; a plain device blit flips it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_MirroredTilingPattern_KeepsTheCellOrientation(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateMirroredTilingPatternPdf());
        using IGraphicsContext context = factory.CreateContext(100, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 6, 2);
        Assert.True(b < 100 && g < 100 && r < 100, $"the band must paint at (2, 6), got r={r} g={g} b={b}");

        (byte wb, byte wg, byte wr, _) = PixelAt(pixels, image.Width, 2, 2);
        Assert.True(wb > 200 && wg > 200 && wr > 200, $"the other half stays white at (2, 2), got r={wr} g={wg} b={wb}");
    }

    /// <summary>
    /// A Huffman symbol dictionary can encode a symbol as a refinement of another one (pdf.js
    /// bitmap-symbol-symhuffrefineone.pdf) or as an aggregate of several (symhuffrefineseveral.pdf).
    /// Both used to return null, so the page carried no image at all; the rendered page is 7.1% ink.
    /// </summary>
    [Theory]
    [InlineData(false, "jbig2-huffman-refine.pdf")]
    [InlineData(true, "jbig2-huffman-refine.pdf")]
    [InlineData(false, "jbig2-huffman-aggregate.pdf")]
    [InlineData(true, "jbig2-huffman-aggregate.pdf")]
    public void Render_HuffmanSymbolDictionaryWithRefinement_PaintsTheImage(bool peach, string sample)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(PdfSample(sample)));
        using IGraphicsContext context = factory.CreateContext(798, 800);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200)
            {
                ink++;
            }
        }

        double fraction = (double)ink / (image.Width * image.Height);
        Assert.InRange(fraction, 0.065, 0.075);
    }

    /// <summary>
    /// pdf.js annotation-ink-without-appearance.pdf carries only /InkList geometry: strokes drawn in /C
    /// with the /BS width. An Ink annotation was not in the markup set, so the strokes were dropped.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_InkAnnotationWithoutAppearance_StrokesItsInkList(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(PdfSample("ink-without-appearance.pdf")));
        using IGraphicsContext context = factory.CreateContext(1224, 1584);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200)
            {
                ink++;
            }
        }

        // The six freehand strokes cover about 0.47% of the page.
        Assert.True(ink > 4000, $"the ink strokes must paint, got {ink} dark pixels");
    }

    /// <summary>
    /// pdf.js bug1865341.pdf's FreeText annotation has /DA (/Helv 10 Tf 0 g) and a UTF-16 /Contents but
    /// no /AP. The appearance has to be built from those: a border box and the text inside it (PDFium
    /// renders "Załącznik" as "Zacznik" because its default font cannot encode the Polish letters).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_FreeTextWithoutAppearance_DrawsItsTextInsideTheBox(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(PdfSample("freetext-without-appearance.pdf")));
        using IGraphicsContext context = factory.CreateContext(459, 198);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The text band inside the box (device rows 33..60, columns 25..122) must carry the glyphs, not
        // just the border.
        int textInk = 0;
        for (int y = 33; y < 60; y++)
        {
            for (int x = 25; x < 122; x++)
            {
                int o = ((y * image.Width) + x) * 4;
                if (pixels[o] < 200)
                {
                    textInk++;
                }
            }
        }

        Assert.True(textInk > 150, $"the FreeText string must paint inside its box, got {textInk} dark pixels");
    }

    /// <summary>
    /// pdf.js issue13316_reduced.pdf's font names its glyphs /g5167 and friends. The Adobe Glyph List
    /// cannot resolve those, and a stripped subset has no post table; PDFium reads the number as the
    /// glyph index, and so does Poso now - before, the codes fell through to their own values and the
    /// Chinese invoice line came out as "A C E F".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Font_GlyphNameThatSpellsItsIndex_MapsToThatGlyph(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        byte[] pdf = PdfFixtures.CreateGlyphIndexNamePdf(FontsFile("code-keyed-truetype.bin"));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        var font = Poso.Pdf.Fonts.PdfFont.Create(
            document, factory, (Poso.Pdf.Objects.PdfDictionary)document.GetObject(4)!);

        Assert.Equal(53, font.GetGlyphId(1));
        Assert.Equal(82, font.GetGlyphId(2));
    }

    /// <summary>
    /// A zero-padded glyph name is a character code, not a glyph index: pdf.js bug1027533.pdf names its
    /// glyphs /g0024 and /g003a, and reading the number as an id picked the wrong glyphs. The code-keyed
    /// cmap is the fallback that stays right (code 1 -> glyph 53, code 2 -> glyph 82).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Font_ZeroPaddedGlyphName_IsNotReadAsAGlyphIndex(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        byte[] pdf = PdfFixtures.CreatePaddedGlyphNamePdf(FontsFile("code-keyed-truetype.bin"));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        var font = Poso.Pdf.Fonts.PdfFont.Create(
            document, factory, (Poso.Pdf.Objects.PdfDictionary)document.GetObject(4)!);

        Assert.Equal(53, font.GetGlyphId(1));
        Assert.Equal(82, font.GetGlyphId(2));
    }

    /// <summary>
    /// A rectangle with no height encloses no area, so a strict fill leaves it blank - but PDFium
    /// paints its edges, and pdf.js issue4260_reduced.pdf draws a whole table of grid lines that way.
    /// The line is one device pixel wide and sits on the pixel the contour falls in.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_DegenerateRectangle_PaintsItsEdges(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateDegenerateRectPdf());
        using IGraphicsContext context = factory.CreateContext(300, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The horizontal line runs along PDF y = 100, which is device row 100 on a 200-tall page.
        (byte hb, byte hg, byte hr, _) = PixelAt(pixels, image.Width, 100, 100);
        Assert.True(hr < 120 && hg < 120 && hb < 120, $"the horizontal line must paint at (100, 100), got r={hr} g={hg} b={hb}");

        // The vertical one is blue, along PDF x = 10, from y = 20 to 220.
        (byte vb, byte vg, byte vr, _) = PixelAt(pixels, image.Width, 100, 10);
        Assert.True(vb > 120 && vr < 120 && vg < 120, $"the vertical line must paint at (10, 100), got r={vr} g={vg} b={vb}");
    }

    /// <summary>
    /// A Type3 glyph procedure runs in the font's space, so the scale it applies to its own content
    /// is measured against the font matrix and the text space, not against the page. pdf.js
    /// issue19634.pdf's F4 glyph scales its content by 0.01 inside a 10600-unit cell: with the page's
    /// transform in the wrong place the scale lands on the page and the text comes out a hundred times
    /// too large, with only its top edge on the page.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_Type3GlyphScale_AppliesToTheGlyphNotThePage(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(PdfSample("type3-scaled-glyph.pdf")));
        using IGraphicsContext context = factory.CreateContext(918, 1188);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The green words are the F5 glyphs; the pink rectangles behind them are the F4 ones, which
        // PDFium paints as a soft grey shadow. With the scale on the page instead of on the glyph, the
        // F4 rectangles are mis-placed and trail every line as solid blocks.
        int green = 0, pink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // The pixels come as BGRA.
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (g > r + 20 && g > b + 20)
            {
                green++;
            }
            else if (r > 200 && g < 220 && b < 220 && r - b > 10)
            {
                pink++;
            }
        }

        Assert.True(green > 200, $"the green words must paint, got {green} pixels");

        // The F4 shadow is a mis-placed block when the glyph procedure runs against the page's
        // transform: the ink it adds is a solid rectangle in the text colour, and the page carries
        // far more of it than the letters do.
        Assert.True(pink < 3000, $"the shadow must not become solid blocks ({pink} pixels)");
    }

    /// <summary>
    /// pdf.js issue4379.pdf masks a 1000x800 indexed image with a CCITT-compressed stencil. The mask
    /// reached the decoder as raw compressed bytes because only DCT/JPX masks were routed through the
    /// image pipeline, so the page painted a band of noise instead of the text.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_CodecEncodedImageMask_ShowsTheImage(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(PdfSample("masked-indexed-image.pdf")));
        using IGraphicsContext context = factory.CreateContext(1190, 1684);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 80000, $"the masked image fills about 6% of the page, got {ink} dark pixels");
    }

    /// <summary>
    /// A form with no /Resources draws a form that only the page's resources define. Dropping the
    /// inheritance loses the whole nested drawing (pdf.js issue1055r.pdf).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_FormWithoutResources_InheritsTheCallers(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateFormWithoutResourcesPdf());
        using IGraphicsContext context = factory.CreateContext(200, 50);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The black square covers PDF (10..40, 10..40) minus a white centre; device y = 50 - y.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 38, 12);
        Assert.True(b < 100 && g < 100 && r < 100, $"the nested form must paint at (12, 38), got r={r} g={g} b={b}");
    }

    /// <summary>
    /// Each Type3 glyph procedure sets its own Tm before painting its square. The procedure runs in a
    /// closed q/Q scope, so the caller's text matrix must survive; if it does not, the second glyph is
    /// placed from the glyph's origin instead of the next pen position.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_Type3GlyphTextMatrix_DoesNotLeak(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType3TextMatrixPdf());
        using IGraphicsContext context = factory.CreateContext(200, 50);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Two 10x10 squares at PDF (5..15, 5..15) and (15..25, 5..15); device y 35..45.
        (byte b1, byte g1, byte r1, _) = PixelAt(pixels, image.Width, 38, 10);
        Assert.True(b1 < 100 && g1 < 100 && r1 < 100, $"the first glyph must paint at (10, 38), got r={r1} g={g1} b={b1}");

        (byte b2, byte g2, byte r2, _) = PixelAt(pixels, image.Width, 38, 20);
        Assert.True(b2 < 100 && g2 < 100 && r2 < 100, $"the second glyph must paint at (20, 38), got r={r2} g={g2} b={b2}");

        // With a leaked matrix the second square sits at PDF y 0..10, i.e. device y 40..50.
        (byte wb, byte wg, byte wr, _) = PixelAt(pixels, image.Width, 47, 20);
        Assert.True(wb > 200 && wg > 200 && wr > 200, $"nothing paints at (20, 47), got r={wr} g={wg} b={wb}");
    }

    /// <summary>
    /// The subset's only cmap is a Macintosh (1,0) table keyed by the PDF code, while /Differences names
    /// code 1 'R' - a character the table does not hold. The code itself has to reach the program's map
    /// (pdf.js issue8229.pdf).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_SymbolicTrueType_MapsItsCodesThroughTheProgramsCmap(bool peach)
    {
        IGraphicsFactory factory = peach ? PeachGraphicsFactory.Default : Factory;
        byte[] pdf = PdfFixtures.CreateCodeKeyedTrueTypePdf(FontsFile("code-keyed-truetype.bin"));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = factory.CreateContext(240, 80);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 300, $"the two mapped glyphs must paint, got {ink} dark pixels");
    }

    private static string PdfSample(string name) => IOPath.Combine(AppContext.BaseDirectory, "Samples", "Pdf", name);

    private static byte[] FontsFile(string name) =>
        File.ReadAllBytes(IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name));

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
    /// A form's /BBox may hold indirect numbers, and the clip it describes has to be the real one. Read
    /// as a type test the box collapses to zero height, and the two back ends then disagree about what an
    /// empty clip means: pdf.js franz_2.pdf is a gray rectangle drawn by a nested form, which Skia kept
    /// (it drops an empty clip) while Peach kept the zero-area clip and painted a white page.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShadingTests.GraphicsFactories), MemberType = typeof(ShadingTests))]
    public void IndirectFormBBox_StillClipsToTheForm(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectFormBBoxPdf());
        using IGraphicsContext context = factory.CreateContext(200, 50);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // The nested form's 0.5 gray rectangle covers the whole 200x50 page.
        foreach ((int x, int y) in new[] { (10, 10), (100, 25), (190, 40) })
        {
            (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, y, x);
            Assert.True(r is > 100 and < 170 && Math.Abs(r - g) < 8 && Math.Abs(g - b) < 8,
                $"the form's gray should cover ({x},{y}): r={r} g={g} b={b}");
        }
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
