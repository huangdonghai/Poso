using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M0: unsupported features must never abort a page in lenient mode but must surface (via the
/// callback, or by throwing in strict mode); text rendering modes that set a clip must persist.
/// </summary>
public class UnsupportedAndClipTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Shading_Lenient_SkipsButStillRenders()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);
        Assert.True(IsBlue(pixels, image.Width, 80, 20), "Rectangle after sh should still render");
        Assert.Single(features);
        Assert.Equal(PdfUnsupportedKind.Shading, features[0].Kind);
    }

    [Fact]
    public void Shading_Strict_Throws()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        Assert.Throws<PdfUnsupportedException>(() =>
            document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White, Strict: true)));
    }

    [Fact]
    public void PatternColor_Lenient_SkipsButStillRenders()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreatePatternColorPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        Assert.True(IsBlue(ReadPixels(image), image.Width, 80, 20), "Rectangle after pattern color should still render");
        Assert.Contains(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);
    }

    [Fact]
    public void JpxImage_Lenient_SkipsImageButStillRenders()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateJpxImagePdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);
        // The red rect (user 5..25 x/y -> device col 5..25, row 75..95) renders despite the skipped image.
        Assert.True(IsRed(pixels, image.Width, 85, 15), "Rectangle after skipped image should still render");
        Assert.Contains(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);
    }

    [Fact]
    public void JpxImage_Strict_Throws()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateJpxImagePdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        Assert.Throws<PdfUnsupportedException>(() =>
            document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White, Strict: true)));
    }

    [Fact]
    public void UnknownStreamFilter_Lenient_ReturnsRawAndRenders()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateUnknownFilterPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        // The unknown filter is skipped; the raw content was plain ASCII so the blue rect still shows.
        Assert.True(IsBlue(ReadPixels(image), image.Width, 80, 20), "Rectangle with skipped filter should still render");
        Assert.Contains(features, f => f.Kind == PdfUnsupportedKind.StreamFilter);
    }

    [Fact]
    public void TextClipMode_PersistsAsClip()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextClipPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        // Far top-right corner must stay white: the red page fill was clipped to the 'b' outline.
        (byte b, byte g, byte r, _) = PixelAt(pixels, image.Width, 3, 196);
        Assert.True(r > 200 && g > 200 && b > 200, $"Corner must stay white when the text clip persists (r={r} g={g} b={b})");

        int redCount = CountRed(pixels, image.Width, image.Height);
        Assert.True(redCount > 0, "Clipped glyph must paint some red");
        Assert.True(redCount < 6000, $"Red must be confined to the glyph, not the whole page ({redCount})");
    }

    private static byte[] ReadPixels(IImage image)
    {
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        return pixels;
    }

    /// <summary>
    /// Two show-text runs in one text object, both in a clipping mode, must accumulate into a single
    /// clip applied at <c>ET</c>. Clipping each run where it is drawn instead intersects them, and the
    /// rectangle after <c>ET</c> is painted nowhere.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShadingTests.GraphicsFactories), MemberType = typeof(ShadingTests))]
    public void TextClipAccumulatesAcrossTheTextObject(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAccumulatedTextClipPdf());
        using IGraphicsContext context = factory.CreateContext(200, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = ReadPixels(image);

        Assert.True(IsBlue(pixels, image.Width, 60, 12), "the first glyph must clip the rectangle to itself");
        Assert.True(IsBlue(pixels, image.Width, 60, 114), "the second glyph must clip it to itself too");
        Assert.True(!IsBlue(pixels, image.Width, 5, 65) && !IsBlue(pixels, image.Width, 5, 165),
            "between the two glyphs the clip must be empty, not the union of their overlap");
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static bool IsRed(byte[] pixels, int width, int y, int x)
    {
        (byte b, byte g, byte r, _) = PixelAt(pixels, width, y, x);
        return r > 170 && g < 110 && b < 110;
    }

    private static bool IsBlue(byte[] pixels, int width, int y, int x)
    {
        (byte b, byte g, byte r, _) = PixelAt(pixels, width, y, x);
        return b > 170 && r < 110 && g < 170;
    }

    private static int CountRed(byte[] pixels, int width, int height)
    {
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (IsRed(pixels, width, y, x))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
