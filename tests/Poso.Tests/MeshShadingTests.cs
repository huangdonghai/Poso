using System;
using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7: mesh shading. A <c>ShadingType 7</c> patch is a tensor-product surface: 16 control points and four
/// corner colours, not a triangle with a colour at each vertex.
/// </summary>
public class MeshShadingTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    /// <summary>
    /// The ramp runs left to right and does not vary with y, which is what pins down both the point order
    /// (boundary then interior) and which of the sixteen control points carry the four colours. MuPDF,
    /// rendered from the same fixture: 13 at x=5, 126 at x=50, 241 at x=95, and 126 at every y probed.
    /// </summary>
    [Fact]
    public void Type7Patch_InterpolatesItsCornerColoursAcrossTheSurface()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType7PatchPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, userX: 5, userY: 50);
        Assert.True(r < 40 && g < 40 && b < 40, $"the black corner should hold to its edge (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 50, 50);
        Assert.True(r is > 100 and < 160 && Math.Abs(r - g) < 6 && Math.Abs(g - b) < 6, $"mid-ramp grey (~126), got {r},{g},{b}");

        (r, g, b) = Luma(px, image.Width, 95, 50);
        Assert.True(r > 200 && g > 200 && b > 200, $"the white corner should hold to its edge (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 50, 10);
        Assert.True(r is > 100 and < 160, $"the ramp should not vary with y (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 50, 90);
        Assert.True(r is > 100 and < 160, $"nor at the other end of y (got {r},{g},{b})");
    }

    /// <summary>
    /// A mesh shading that omits <c>/Function</c> must still paint, mapping each sampled component
    /// straight through as the colourspace operand (the identity PDFium and MuPDF both apply). Dropping
    /// the shading instead left whole pages of the pdf.js suite blank; the ramp here must match
    /// <see cref="Type7Patch_InterpolatesItsCornerColoursAcrossTheSurface"/>, whose function is already identity.
    /// </summary>
    [Fact]
    public void Type7Patch_WithoutFunction_RendersAsIdentity()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType7PatchNoFunctionPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, userX: 5, userY: 50);
        Assert.True(r < 40 && g < 40 && b < 40, $"the black corner should hold to its edge (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 95, 50);
        Assert.True(r > 200 && g > 200 && b > 200, $"the white corner should hold to its edge (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 50, 50);
        Assert.True(r is > 100 and < 160 && Math.Abs(r - g) < 6 && Math.Abs(g - b) < 6, $"mid-ramp grey (~126), got {r},{g},{b}");
    }

    /// <summary>
    /// A <c>ShadingType 5</c> lattice-form Gouraud triangle mesh is rows of vertices, not flagged patches.
    /// MuPDF reads a full /VerticesPerRow per row and stitches quads between adjacent rows; a two-by-two
    /// lattice is one quad. Type 5 was previously dropped (only 4/6/7 parsed), so the whole mesh painted
    /// as background - this locks the row-major reader and the Gouraud colour through the ramp.
    /// </summary>
    [Fact]
    public void Type5Lattice_StitchesRowsIntoAGouraudRamp()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType5LatticePdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, userX: 5, userY: 50);
        Assert.True(r < 40 && g < 40 && b < 40, $"left edge should be black (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 95, 50);
        Assert.True(r > 200 && g > 200 && b > 200, $"right edge should be white (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 50, 50);
        Assert.True(r is > 90 and < 165 && Math.Abs(r - g) < 8 && Math.Abs(g - b) < 8, $"mid-ramp grey, got {r},{g},{b}");
    }

    private static (byte R, byte G, byte B) Luma(byte[] px, int width, int userX, int userY)
    {
        int deviceRow = 100 - userY;
        int offset = ((deviceRow * width) + userX) * 4;
        return (px[offset + 2], px[offset + 1], px[offset]); // BGRA -> R,G,B
    }
}
