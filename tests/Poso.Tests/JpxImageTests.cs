using System;
using System.Collections.Generic;
using System.IO;
using Poso.Graphics;
using Poso.Pdf;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// JPXDecode (JPEG 2000). Neither graphics back end can decode it - SkiaSharp's native build omits
/// OpenJPEG and PeachDrawing has no codec - so before the managed CoreJ2K decoder was wired in, a page
/// whose only content is a JPX image rendered blank: the cover of a CJK book, plus six more images in
/// that file and three across two others. The fixtures are flat-colour blocks, so the expected pixels
/// are known exactly rather than sampled from a reference render.
/// </summary>
public class JpxImageTests
{
    private static readonly IGraphicsFactory Skia = SkiaGraphicsFactory.Default;
    private static readonly IGraphicsFactory Peach = PeachGraphicsFactory.Default;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JpxRgbImage_RendersItsColours(bool peach)
    {
        byte[] jpx = Sample("blocks.jp2");
        byte[] pixels = Render(PdfFixtures.CreateJpxImagePdf(jpx, 16, 16), peach, 16, 16, out List<PdfUnsupportedFeature> features);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        // Four 8x8 quadrants: red top-left, green top-right, blue bottom-left, yellow bottom-right.
        AssertColor(pixels, 16, 2, 2, r: 255, g: 0, b: 0, "top-left");
        AssertColor(pixels, 16, 12, 2, r: 0, g: 255, b: 0, "top-right");
        AssertColor(pixels, 16, 2, 12, r: 0, g: 0, b: 255, "bottom-left");
        AssertColor(pixels, 16, 12, 12, r: 255, g: 255, b: 0, "bottom-right");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JpxGrayImage_RendersBothHalves(bool peach)
    {
        byte[] jpx = Sample("gray_split.jp2");
        byte[] pixels = Render(PdfFixtures.CreateJpxImagePdf(jpx, 8, 8), peach, 8, 8, out _);

        Assert.True(Luma(pixels, 8, 1, 4) < 24, "left half should be black");
        Assert.True(Luma(pixels, 8, 6, 4) > 231, "right half should be white");
    }

    [Fact]
    public void CorruptJpx_ReportsUnsupportedInsteadOfThrowing()
    {
        byte[] junk = new byte[64];
        new Random(7).NextBytes(junk);

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateJpxImagePdf(junk, 16, 16));
        using IGraphicsContext context = Skia.CreateContext(16, 16);
        var features = new List<PdfUnsupportedFeature>();

        // Must not throw: a damaged codestream costs its image, not the page.
        document.Pages[0].Render(Skia, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.Contains(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);
    }

    private static byte[] Render(byte[] pdf, bool peach, int width, int height, out List<PdfUnsupportedFeature> features)
    {
        IGraphicsFactory factory = peach ? Peach : Skia;
        features = new List<PdfUnsupportedFeature>();

        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = factory.CreateContext(width, height);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        return pixels;
    }

    private static void AssertColor(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, string where)
    {
        int offset = ((y * width) + x) * 4;
        Assert.True(
            pixels[offset + 2] == r && pixels[offset + 1] == g && pixels[offset] == b,
            $"{where}: expected RGB({r},{g},{b}), got BGRA({pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]})");
    }

    private static int Luma(byte[] pixels, int width, int x, int y)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3;
    }

    private static byte[] Sample(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Jpx", name);
        if (File.Exists(probe))
        {
            return File.ReadAllBytes(probe);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = IOPath.Combine(directory.FullName, "tests", "Poso.Tests", "Samples", "Jpx", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"JPX sample '{name}' not found.", name);
    }
}
