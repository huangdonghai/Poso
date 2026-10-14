using System;
using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7: mesh shading (Type 7 lattice triangle) with Gouraud colour interpolation. The triangle has a
/// black vertex at (0,0), white at (100,0) and mid-grey at (0,100); device rows are 100 - userY.
/// </summary>
public class MeshShadingTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Type7MeshTriangle_InterpolatesGouraud()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateMeshTrianglePdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, userX: 33, userY: 33);
        Assert.True(Math.Abs(r - g) < 6 && Math.Abs(g - b) < 6, $"centroid should be neutral grey ({r},{g},{b})");
        Assert.True(r is > 90 and < 165, $"centroid t~0.5 grey (~128), got {r}");

        (r, g, b) = Luma(px, image.Width, 6, 6);
        Assert.True(r < 70, $"near black vertex should be dark, got {r}");

        (r, g, b) = Luma(px, image.Width, 88, 6);
        Assert.True(r > 170, $"near white vertex should be bright, got {r}");

        // (90,90) lies outside the triangle (x+y>100) -> untouched, shows the white background.
        (r, g, b) = Luma(px, image.Width, 90, 90);
        Assert.True(r > 200 && g > 200 && b > 200, $"outside triangle should be white background ({r},{g},{b})");
    }

    private static (byte R, byte G, byte B) Luma(byte[] px, int width, int userX, int userY)
    {
        int deviceRow = 100 - userY;
        int offset = ((deviceRow * width) + userX) * 4;
        return (px[offset + 2], px[offset + 1], px[offset]); // BGRA -> R,G,B
    }
}
