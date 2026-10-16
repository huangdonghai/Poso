using Poso.Graphics;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

public class GraphicsRenderTests
{
    [Fact]
    public void SkiaContext_FillsPathAndDrawsText_ProducesNonBlankPixels()
    {
        var factory = SkiaGraphicsFactory.Default;

        using IGraphicsContext context = factory.CreateContext(120, 120);
        context.Clear(PosoColor.White);

        // Path fill (red rectangle)
        var builder = new PathBuilder();
        builder.MoveTo(10, 10);
        builder.LineTo(110, 10);
        builder.LineTo(110, 110);
        builder.LineTo(10, 110);
        builder.Close();
        context.FillPath(builder.Build(), PosoColor.Red, evenOdd: false);

        // Text (blue "Hi")
        IFont? font = factory.CreateFont(default, "Helvetica");
        Assert.NotNull(font);
        using (font)
        {
            ushort h = font.GetGlyphId((uint)'H');
            ushort i = font.GetGlyphId((uint)'i');
            Assert.True(h > 0);
            Assert.True(i > 0);
            Assert.True(font.GetAdvanceWidth(h) > 0f);

            float advanceH = font.GetAdvanceWidth(h) * 24f;
            Span<GlyphPosition> glyphs = stackalloc GlyphPosition[2]
            {
                new GlyphPosition(h, 30, 60),
                new GlyphPosition(i, 30 + advanceH, 60),
            };
            context.DrawGlyphs(font, 24f, glyphs, PosoColor.Blue, PosoColor.Blue, TextRenderMode.Fill, StrokeStyle.Default);
        }

        using IImage image = context.Snapshot();
        Assert.Equal(120, image.Width);
        Assert.Equal(120, image.Height);
        Assert.Equal(PixelFormat.Bgra8888Premul, image.Format);

        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        int nonWhite = 0;
        int redish = 0;
        for (int p = 0; p + 3 < pixels.Length; p += 4)
        {
            byte b = pixels[p];
            byte g = pixels[p + 1];
            byte r = pixels[p + 2];
            if (r < 250 || g < 250 || b < 250)
            {
                nonWhite++;
            }

            if (r > 150 && g < 100 && b < 100)
            {
                redish++;
            }
        }

        Assert.True(nonWhite > 5000, $"Too few non-white pixels: {nonWhite}");
        Assert.True(redish > 5000, $"Too few red fill pixels: {redish}");
    }

    [Fact]
    public void SkiaFont_TryGetGlyphOutline_ReturnsPath()
    {
        var factory = SkiaGraphicsFactory.Default;
        using IFont font = factory.CreateFont(default, "Helvetica-Bold")!;

        var path = new PathBuilder();
        bool ok = font.TryGetGlyphOutline(font.GetGlyphId((uint)'A'), path);

        Assert.True(ok);
        Assert.False(path.IsEmpty);
    }

    [Fact]
    public void SkiaContext_ClipAndStroke_Work()
    {
        var factory = SkiaGraphicsFactory.Default;

        using IGraphicsContext context = factory.CreateContext(60, 60);
        context.Clear(PosoColor.White);

        context.ClipRect(new Poso.Geometry.RectF(10, 10, 40, 40));

        var builder = new PathBuilder();
        builder.MoveTo(0, 0);
        builder.LineTo(60, 0);
        builder.LineTo(60, 60);
        builder.LineTo(0, 60);
        builder.Close();
        context.FillPath(builder.Build(), PosoColor.Green, evenOdd: false);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // The corner (5,5) should still be white (clipped); the center (30,30) should be green
        int corner = ((5 * 60) + 5) * 4;
        int center = ((30 * 60) + 30) * 4;
        Assert.True(pixels[corner] > 200 && pixels[corner + 1] > 200 && pixels[corner + 2] > 200, "Clip failed: the corner is not white");
        Assert.True(pixels[center + 1] > 100 && pixels[center] < 100, "The center is not green");
    }
}
