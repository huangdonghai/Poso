using System;
using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.PeachDrawing;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// A combined fill-and-stroke (<c>B</c>, <c>b</c>, <c>B*</c>) is a single graphics object: the stroke
/// replaces the fill where it covers instead of compositing over it (ISO 32000-1 11.7.4.4). MuPDF
/// paints the pair inside a knockout group whenever the stroke is visible and either translucent or
/// blended (pdf_show_path), and the expected pixels below were measured from a pymupdf render of the
/// same fixture. Without the group a translucent stroke reads the fill through its inner half, and a
/// blended stroke applies its blend mode twice.
/// </summary>
public class AtomicFillStrokeTests
{
    private static readonly IGraphicsFactory[] Backends =
    {
        SkiaGraphicsFactory.Default,
        PeachGraphicsFactory.Default,
    };

    [Fact]
    public void TranslucentFillAndStroke_StrokeBandShowsTheStrokeOverThePage()
    {
        foreach (IGraphicsFactory factory in Backends)
        {
            string tag = Tag(factory);
            byte[] pixels = Render(PdfFixtures.CreateAtomicFillStrokePdf(), factory, out int width);

            // The 2-unit stroke is centred on user x=20, so device column 20 lies wholly inside the
            // band while column 100 is fill-only; user y=50 is device row 50.
            AssertRgb(pixels, width, 50, 20, 128, 255, 128, 10, $"{tag}: stroke over the fill's edge");
            AssertRgb(pixels, width, 50, 100, 255, 128, 128, 10, $"{tag}: fill interior");
            AssertRgb(pixels, width, 5, 5, 255, 255, 255, 2, $"{tag}: page outside the rectangle");
        }
    }

    [Fact]
    public void MultiplyFillAndStroke_CompositesThePairOnce()
    {
        foreach (IGraphicsFactory factory in Backends)
        {
            string tag = Tag(factory);
            byte[] pixels = Render(PdfFixtures.CreateAtomicFillStrokePdf(multiply: true), factory, out int width);

            // Painting "re f" then "re S" under /BM /Multiply would multiply the stroke into the
            // fill's multiplied result and give a (0,0,0) band; MuPDF's group gives pure green.
            AssertRgb(pixels, width, 50, 20, 0, 255, 0, 10, $"{tag}: stroke over the fill's edge");
            AssertRgb(pixels, width, 50, 100, 255, 0, 0, 10, $"{tag}: fill interior");
            AssertRgb(pixels, width, 5, 5, 255, 255, 255, 2, $"{tag}: page outside the rectangle");
        }
    }

    private static string Tag(IGraphicsFactory factory) =>
        factory is PeachGraphicsFactory ? "peach" : "skia";

    private static byte[] Render(byte[] data, IGraphicsFactory factory, out int width)
    {
        using PdfDocument document = PdfDocument.FromArray(data);
        using IGraphicsContext context = factory.CreateContext(200, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), _ => { });

        using IImage image = context.Snapshot();
        width = image.Width;
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        return pixels;
    }

    private static void AssertRgb(
        byte[] pixels, int width, int y, int x, int r, int g, int b, int tolerance, string where)
    {
        int offset = ((y * width) + x) * 4;
        int gotB = pixels[offset];
        int gotG = pixels[offset + 1];
        int gotR = pixels[offset + 2];
        Assert.True(
            Math.Abs(gotR - r) <= tolerance && Math.Abs(gotG - g) <= tolerance && Math.Abs(gotB - b) <= tolerance,
            $"{where}: expected ~({r},{g},{b}), got ({gotR},{gotG},{gotB})");
    }
}
