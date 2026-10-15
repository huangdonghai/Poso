using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.PeachDrawing;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Runs the core drawing assertions of the Skia back-end tests against the pure-managed
/// PeachDrawing raster back-end. Geometry assertions stay strict; ink-coverage assertions are
/// relaxed (the software rasterizer's anti-aliasing differs from Skia's at the pixel level).
/// </summary>
public class PeachBackendTests
{
    private static readonly IGraphicsFactory Factory = PeachGraphicsFactory.Default;

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
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 30, 70);
        Assert.True(b > 180 && g is > 60 and < 160 && r < 120, $"Rectangle color wrong at (30,70): r={r} g={g} b={b}");

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
        int redOnLine = CountColor(pixels, image.Width, 30, 20, 180, r: 230, g: 26, b: 26);
        Assert.True(redOnLine > 50, $"Stroked line pixels too few: {redOnLine}");

        // Text baseline at PDF y=50 -> device y=50; glyph ink should appear around x=20..90.
        // (Helvetica here is a system fallback font: assert ink presence, not exact shapes.)
        int inkNearText = CountInk(pixels, image.Width, 38, 52, 18, 95);
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

        // The whole page is transposed: the main rect lands at device x in [10,50], y in [10,90].
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 30, 50);
        Assert.True(b > 180 && r < 120, $"Rotated rectangle color wrong at (x=50,y=30): r={r} g={g} b={b}");

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

        // Image occupies device x in [0,100], y in [50,100] (PDF y-up origin at bottom).
        // Top-left of the image is red
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 10, 60);
        Assert.True(r > 180 && g < 100 && b < 100, $"Image top-left should be red at (10,60): r={r} g={g} b={b}");

        // Bottom-right of the image is yellow
        (byte yb, byte yg, byte yr, _) = PixelAt(pixels, image.Width, 90, 90);
        Assert.True(yr > 180 && yg > 180 && yb < 100, $"Image bottom-right should be yellow at (90,90): r={yr} g={yg} b={yb}");
    }

    /// <summary>
    /// Outline-orientation guard (S0 spike 2 promoted to a regression test): the PeachDrawing glyph
    /// outlines are design-unit y-up, and the back-end must place them right-side up under the page's
    /// flipped CTM exactly like the Skia path mode does.
    /// </summary>
    [Fact]
    public void Render_TextGlyph_IsUprightAndNotMirrored()
    {
        byte[] data = PdfFixtures.CreateGlyphPdf('b');

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Baseline at device row 80; 'b' has no descender, so ink must sit above the baseline.
        int above = CountInk(pixels, image.Width, 0, 79, 0, image.Width);
        int below = CountInk(pixels, image.Width, 81, image.Height, 0, image.Width);
        Assert.True(above > below * 10, $"'b' must be drawn above its baseline: above={above}, below={below}");

        // The stem is the leftmost inked column and spans the whole ascender (a mirrored outline swaps that).
        (int leftSpan, int rightSpan) = EdgeColumnSpans(pixels, image.Width);
        Assert.True(leftSpan > rightSpan, $"'b' stem must be on the left: leftSpan={leftSpan}, rightSpan={rightSpan}");
    }

    /// <summary>Stroke-text width must not be re-scaled by the text matrix (same trap as the Skia back-end).</summary>
    [Fact]
    public void Render_StrokedText_KeepsThinOutlineNotBlob()
    {
        byte[] data = PdfFixtures.CreateStrokedTextPdf();
        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(120, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, x, y);
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

        int cx = (minX + maxX) / 2;
        int cy = (minY + maxY) / 2;
        (byte cb, byte cg, byte cr, _) = PixelAt(pixels, image.Width, cy, cx);
        Assert.True(cr > 200 && cg > 200 && cb > 200,
            $"stroked 'O' counter must stay white (thin outline); center ({cx},{cy}) was r={cr} g={cg} b={cb}");
    }

    /// <summary>
    /// Clip-under-transform guard (S0 spike 1 promoted): a clip pushed under the current CTM must
    /// restrict painting at its transformed device position, and survive the transform pop.
    /// </summary>
    [Fact]
    public void Render_ClipRect_AndPath_HoldUnderTransforms()
    {
        // "q 100 0 0 100 0 0 cm q 10 10 20 20 re W n 0 0 50 50 re f Q Q" style page built inline:
        // a translated/scaled user space clips a big fill to a small device square.
        string content =
            "1 0 0 rg\n" +
            "q 0.5 0 0 0.5 40 40 cm q 10 10 20 20 re W n 0 0 100 100 re f Q Q\n";
        byte[] data = PdfFixtures.Build(1,
            new PdfObjectDefinition(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new PdfObjectDefinition(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new PdfObjectDefinition(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R >>"),
            PdfFixtures.Stream(4, string.Empty, System.Text.Encoding.ASCII.GetBytes(content)));

        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Clip user [10,30]x[10,30] under scale 0.5 translate(40,40): device x [45,55],
        // PDF y [45,55] -> device y [45,55]. Interior pixel red, outside white.
        (byte ib, byte ig, byte ir, _) = PixelAt(pixels, image.Width, 50, 50);
        Assert.True(ir > 180 && ig < 80 && ib < 80, $"clipped fill must land at (50,50): r={ir} g={ig} b={ib}");

        (byte ob, byte og, byte or, _) = PixelAt(pixels, image.Width, 40, 40);
        Assert.True(or > 200 && og > 200 && ob > 200, $"outside the clip must stay white at (40,40): r={or} g={og} b={ob}");
    }

    [Fact]
    public void Context_SmokeOps_DoNotThrow()
    {
        using IGraphicsContext context = Factory.CreateContext(10, 10);
        context.Clear(PosoColor.White);
        context.Save();
        context.SetMatrix(Matrix3x2.CreateScale(2f));
        var builder = new PathBuilder();
        builder.MoveTo(0, 0);
        builder.LineTo(4, 0);
        builder.LineTo(4, 4);
        builder.Close();
        IPath path = builder.Build();
        context.ClipPath(path, evenOdd: false);
        context.FillPath(path, PosoColor.Red, evenOdd: false);
        context.StrokePath(path, new StrokeStyle(0.5f), PosoColor.Black);
        context.SetBlendMode(BlendMode.Multiply);
        context.FillPath(path, PosoColor.Blue, evenOdd: true);
        context.Restore();

        RectF clipBounds = context.ClipBounds;
        Assert.False(clipBounds.IsEmpty);

        using IImage snapshot = context.Snapshot();
        Assert.Equal(PixelFormat.Bgra8888Premul, snapshot.Format);
        Assert.Equal(10 * 10 * 4, snapshot.Width * snapshot.Height * snapshot.BytesPerPixel);
    }

    /// <summary>
    /// The Peach back-end must reproduce the MuPDF oracle signatures the Skia back-end was calibrated
    /// against (ReferenceRenderTests baseline): shapes and the axial shading, as coarse 4x2 luminance
    /// cells - independent of Skia, locking the new back-end against the same oracle.
    /// </summary>
    [Theory]
    [InlineData(ReferenceRenderTests.ShapesPdf, 0.800000, 160, 80, "Shapes")]
    [InlineData(ReferenceRenderTests.ShadingPdf, 0.800000, 160, 80, "Shading")]
    public void MatchesMuPdfReferenceSignature(string base64, double scale, int contextWidth, int contextHeight, string kind)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using PdfDocument document = PdfDocument.FromArray(bytes);
        using IGraphicsContext context = Factory.CreateContext(contextWidth, contextHeight);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions((float)scale, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = ReadPixels(image);
        int[] actual = ReferenceRenderTests.Signature(px, image.Width, image.Height);
        int[] expected = kind == "Shapes" ? ReferenceRenderTests.ShapesExpected : ReferenceRenderTests.ShadingExpected;

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(actual[i] - expected[i]) <= ReferenceRenderTests.Tolerance,
                $"Peach {kind} cell {i}: actual={actual[i]} MuPDF={expected[i]}");
        }
    }

    private static byte[] ReadPixels(IImage image)
    {
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        return pixels;
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int width, int x, int y)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static (int LeftSpan, int RightSpan) EdgeColumnSpans(byte[] pixels, int width)
    {
        int height = pixels.Length / (width * 4);
        int left = -1;
        int right = -1;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                (byte b, byte g, byte r, _) = PixelAt(pixels, width, x, y);
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
            (byte b, byte g, byte r, _) = PixelAt(pixels, width, x, y);
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
                (byte b, byte g, byte r, _) = PixelAt(pixels, width, x, y);
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
            (byte pb, byte pg, byte pr, _) = PixelAt(pixels, width, x, y);
            if (Math.Abs(pr - r) < 60 && Math.Abs(pg - g) < 60 && Math.Abs(pb - b) < 60)
            {
                count++;
            }
        }

        return count;
    }
}
