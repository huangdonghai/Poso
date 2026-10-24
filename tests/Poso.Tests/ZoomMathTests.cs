using System;
using System.Numerics;
using Avalonia;
using Poso.Avalonia.Rendering;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Viewer geometry: the page-to-view matrix must reproduce the renderer's own base transform for
/// rotated pages, crop boxes that do not start at the origin, and /UserUnit scaling. The oracle is
/// the pixel bounding box of a marker rectangle actually rendered by the content interpreter.
/// </summary>
public class ZoomMathTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    // The marker drawn by PdfFixtures.CreateRotatedUserUnitPdf, in page user space (y-up)
    private static readonly Poso.Geometry.RectF Marker =
        Poso.Geometry.RectF.FromLbrb(20f, 30f, 60f, 50f);

    [Fact]
    public void PageSize_AppliesRotationAndUserUnit()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateRotatedUserUnitPdf());
        PdfPage page = document.Pages[0];

        // Crop box 200x100, /UserUnit 2, /Rotate 90
        Assert.Equal(new Size(200, 400), ZoomMath.PageSize(page, 0));
        Assert.Equal(new Size(400, 200), ZoomMath.PageSize(page, 90));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PageToViewMatrix_LinesUpWithTheRenderedInk(int viewRotation)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateRotatedUserUnitPdf());
        PdfPage page = document.Pages[0];

        const double scale = 2.0;
        Size size = ZoomMath.PageSize(page, viewRotation);
        int width = (int)Math.Ceiling(size.Width * scale);
        int height = (int)Math.Ceiling(size.Height * scale);

        using IGraphicsContext context = Factory.CreateContext(width, height);
        page.Render(Factory, context, new PdfRenderOptions((float)scale, viewRotation, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixels(pixels);

        (int minX, int minY, int maxX, int maxY) = InkBounds(pixels, image.Width, image.Height);
        Assert.True(minX <= maxX, "the marker rectangle must render");

        Matrix3x2 pageToView = ZoomMath.PageToViewMatrix(page, viewRotation, scale);
        Rect expected = ZoomMath.ToViewRect(Marker, pageToView);

        // Ink bounds are inclusive pixel indices; the mapped rectangle's right/bottom edges are exclusive
        Assert.InRange(expected.X, minX - 2.0, minX + 2.0);
        Assert.InRange(expected.Y, minY - 2.0, minY + 2.0);
        Assert.InRange(expected.Right, maxX - 1.0, maxX + 3.0);
        Assert.InRange(expected.Bottom, maxY - 1.0, maxY + 3.0);

        // Inverse mapping: the centre of the ink lands inside the marker in page user space
        var inkCenter = new Point((minX + maxX + 1) / 2.0, (minY + maxY + 1) / 2.0);
        Vector2 pagePoint = ZoomMath.ToPagePoint(inkCenter, pageToView);
        Assert.InRange(pagePoint.X, Marker.Left - 2f, Marker.Right + 2f);
        Assert.InRange(pagePoint.Y, Marker.Top - 2f, Marker.Bottom + 2f);
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) InkBounds(byte[] pixels, int width, int height)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                if (pixels[offset] < 200 || pixels[offset + 1] < 200 || pixels[offset + 2] < 200)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return (minX, minY, maxX, maxY);
    }
}
