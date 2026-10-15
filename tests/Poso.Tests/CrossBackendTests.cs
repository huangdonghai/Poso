using Poso.Graphics;
using Poso.Pdf;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Cross-back-end agreement on the core fixture pages. Solid-color pages (fills, images) are compared
/// per channel within anti-aliasing noise; text pages go through geometry assertions (ink bounding box
/// and mask IoU) because the two independent rasterizers halo AA edges differently - the project's
/// established rule of relaxing ink metrics while keeping geometry strict.
/// </summary>
public class CrossBackendTests
{
    private static readonly IGraphicsFactory Skia = SkiaGraphicsFactory.Default;
    private static readonly IGraphicsFactory Peach = PeachGraphicsFactory.Default;

    [Theory]
    [InlineData("GraphicsPage")]
    [InlineData("ImagePage")]
    public void ColorPages_AgreeWithinTolerance(string page)
    {
        byte[] data = page == "GraphicsPage" ? PdfFixtures.CreateGraphicsPdf() : PdfFixtures.CreateImagePdf();
        (int width, int height) = (200, 100);

        byte[] skiaPixels = Render(data, Skia, width, height);
        byte[] peachPixels = Render(data, Peach, width, height);

        int pixels = width * height;
        int mismatches = 0;
        int worst = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            // Alpha-blended BGRA: compare unpremultiplied-free straight channels; surfaces here are opaque.
            int db = Math.Abs(skiaPixels[o] - peachPixels[o]);
            int dg = Math.Abs(skiaPixels[o + 1] - peachPixels[o + 1]);
            int dr = Math.Abs(skiaPixels[o + 2] - peachPixels[o + 2]);
            int delta = Math.Max(db, Math.Max(dg, dr));
            worst = Math.Max(worst, delta);
            if (delta > 32)
            {
                mismatches++;
            }
        }

        double mismatchRatio = (double)mismatches / pixels;
        Assert.True(mismatchRatio <= 0.05d,
            $"{page}: {mismatches}/{pixels} pixels differ by more than 32/255 (worst {worst}); ratio {mismatchRatio:P2}");
    }

    [Theory]
    [InlineData("GlyphPage")]
    [InlineData("StrokedTextPage")]
    public void TextPages_AgreeGeometrically(string page)
    {
        byte[] data = page == "GlyphPage" ? PdfFixtures.CreateGlyphPdf('b') : PdfFixtures.CreateStrokedTextPdf();
        (int width, int height) = page == "StrokedTextPage" ? (120, 100) : (200, 100);

        byte[] skiaPixels = Render(data, Skia, width, height);
        byte[] peachPixels = Render(data, Peach, width, height);

        // Ink mask geometry: bounding boxes must agree within 2 device pixels (the two back-ends share
        // the same font sources; only their AA coverage differs), and the masks must overlap strongly.
        (int minX, int minY, int maxX, int maxY) = InkBounds(skiaPixels, width, height);
        (int pMinX, int pMinY, int pMaxX, int pMaxY) = InkBounds(peachPixels, width, height);
        Assert.True(maxX > minX, "Skia produced no ink");
        Assert.True(pMaxX > pMinX, "Peach produced no ink");
        Assert.True(Math.Abs(minX - pMinX) <= 2 && Math.Abs(maxX - pMaxX) <= 2
            && Math.Abs(minY - pMinY) <= 2 && Math.Abs(maxY - pMaxY) <= 2,
            $"{page}: ink bounds differ: skia=({minX},{minY})-({maxX},{maxY}) peach=({pMinX},{pMinY})-({pMaxX},{pMaxY})");

        int both = 0;
        int either = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool s = IsInk(skiaPixels, x, y, width);
                bool p = IsInk(peachPixels, x, y, width);
                if (s || p)
                {
                    either++;
                    if (s && p)
                    {
                        both++;
                    }
                }
            }
        }

        double iou = (double)both / either;
        Assert.True(iou >= 0.85d, $"{page}: ink mask IoU {iou:F3} (skia/peach disagree beyond AA noise)");
    }

    /// <summary>
    /// The multi-page sample document (SamplePdfTests' generator: text-heavy pages, images, shapes,
    /// transparency) must render on Peach without dropping content, and its ink mask must agree with
    /// the Skia render's page by page (geometry, not AA-exact pixels).
    /// </summary>
    [Fact]
    public void SamplePdf_AllPages_RenderAndAgreeWithSkia()
    {
        byte[] data = SamplePdfGenerator.Build();
        using var document = PdfDocument.FromArray(data);
        Assert.Equal(3, document.PageCount);

        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            byte[] skiaPixels = RenderSample(data, Skia, 306, 396, pageIndex);
            byte[] peachPixels = RenderSample(data, Peach, 306, 396, pageIndex);

            int inkS = CountInk(skiaPixels), inkP = CountInk(peachPixels);
            Assert.True(inkS > 500, $"page {pageIndex}: skia ink {inkS}");
            Assert.True(inkP > 500, $"page {pageIndex}: peach ink {inkP}");

            int both = 0, either = 0;
            for (int i = 0; i < skiaPixels.Length; i += 4)
            {
                bool s = IsInkAt(skiaPixels, i);
                bool p = IsInkAt(peachPixels, i);
                if (s || p)
                {
                    either++;
                    if (s && p)
                    {
                        both++;
                    }
                }
            }

            double iou = (double)both / either;
            Assert.True(iou >= 0.75d, $"page {pageIndex}: sample ink mask IoU {iou:F3} (skia ink {inkS}, peach ink {inkP})");
        }
    }

    /// <summary>
    /// Regression: the document-level font cache must not hand a back-end's cached typeface to a
    /// different back-end mid-document (a live back-end toggle rendered text-less blank pages because
    /// the new context silently skipped foreign IFont/IImage types). Render the same document with
    /// both factories in either order; both must produce text ink.
    /// </summary>
    [Fact]
    public void SwitchingBackendsMidDocument_KeepsTextInk()
    {
        byte[] data = PdfFixtures.CreateGlyphPdf('b');

        using (var document = PdfDocument.FromArray(data))
        {
            Assert.True(RenderInk(document, Skia) > 20, "skia-first: Skia should ink");
            int peachInk = RenderInk(document, Peach);
            Assert.True(peachInk > 20, $"skia-first: Peach rendered {peachInk} ink pixels after the switch (stale SkiaFont?)");
        }

        using (var document = PdfDocument.FromArray(data))
        {
            Assert.True(RenderInk(document, Peach) > 20, "peach-first: Peach should ink");
            int skiaInk = RenderInk(document, Skia);
            Assert.True(skiaInk > 20, $"peach-first: Skia rendered {skiaInk} ink pixels after the switch");
        }
    }

    private static int RenderInk(PdfDocument document, IGraphicsFactory factory)
    {
        using IGraphicsContext context = factory.CreateContext(200, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixels(pixels);
        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200)
            {
                ink++;
            }
        }

        return ink;
    }

    private static byte[] Render(byte[] data, IGraphicsFactory factory, int width, int height)
        => Render(data, factory, width, height, 0, 1f);

    private static byte[] RenderSample(byte[] data, IGraphicsFactory factory, int width, int height, int pageIndex)
        => Render(data, factory, width, height, pageIndex, 0.5f);

    private static byte[] Render(byte[] data, IGraphicsFactory factory, int width, int height, int pageIndex, float scale)
    {
        using var document = PdfDocument.FromArray(data);
        using IGraphicsContext context = factory.CreateContext(width, height);
        document.Pages[pageIndex].Render(factory, context, new PdfRenderOptions(scale, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixels(pixels);
        return pixels;
    }

    private static int CountInk(byte[] pixels)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (IsInkAt(pixels, i))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsInkAt(byte[] pixels, int offset) =>
        pixels[offset] < 240 || pixels[offset + 1] < 240 || pixels[offset + 2] < 240;

    private static bool IsInk(byte[] pixels, int x, int y, int width)
    {
        int o = ((y * width) + x) * 4;
        return pixels[o] < 200 || pixels[o + 1] < 200 || pixels[o + 2] < 200;
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) InkBounds(byte[] pixels, int width, int height)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (IsInk(pixels, x, y, width))
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return (minX, minY, maxX, maxY);
    }
}
