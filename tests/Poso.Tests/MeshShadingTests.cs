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
    /// A <c>ShadingType 4</c> free-form Gouraud triangle mesh is flag-per-vertex triangles, not a 16-point
    /// tensor patch. Two flag-0 triangles tile the square black (left) to white (right); this locks the
    /// triangle-strip reader that previously dropped type 4 entirely (issue2948 painted background).
    /// </summary>
    [Fact]
    public void Type4FreeFormTriangleMesh_PaintsGouraudTriangles()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType4TrianglePdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, userX: 6, userY: 50);
        Assert.True(r < 40 && g < 40 && b < 40, $"left edge should be black (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 94, 50);
        Assert.True(r > 215 && g > 215 && b > 215, $"right edge should be white (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 50, 50);
        Assert.True(r is > 90 and < 165 && Math.Abs(r - g) < 8 && Math.Abs(g - b) < 8, $"mid-ramp grey, got {r},{g},{b}");
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

    /// <summary>
    /// A <c>ShadingType 6</c> constant-colour tensor patch supplies 12 boundary points plus four corner
    /// colours; the four interior poles are computed. A single flat-square patch painted all-black must cover
    /// the page - the earlier Coons approximation scrambled real type 6 meshes (coons-allflags-withfunction).
    /// </summary>
    [Fact]
    public void Type6ConstantColorPatch_PaintsItsComputedInteriorSurface()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType6PatchPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, 50, 50);
        Assert.True(r < 40 && g < 40 && b < 40, $"the black surface should cover the centre (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 15, 85);
        Assert.True(r < 40 && g < 40 && b < 40, $"and the upper-left of the patch (got {r},{g},{b})");
    }

    /// <summary>
    /// A mesh may declare 32 bits per coordinate (pdf.js personwithdog.pdf). Reading the 32nd bit through
    /// the int-returning bit reader overflowed to a negative value, which pushed every point off-page and
    /// left the whole page blank; the lattice must paint.
    /// </summary>
    [Fact]
    public void Type5Mesh_With32BitCoordinates_Paints()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateType5Mesh32BitPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        int painted = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int off = ((y * image.Width) + x) * 4;
                if (px[off] < 250 || px[off + 1] < 250 || px[off + 2] < 250)
                {
                    painted++;
                }
            }
        }

        Assert.True(painted > 0, "32-bit mesh coordinates must paint ink somewhere on the page");
    }

    /// <summary>
    /// A function-based shading's /BBox limits the region that gets painted for axial and radial shadings
    /// too, not only for the function type - a shading pattern filling a bigger area must leave the area
    /// outside its box untouched (pdf.js issue8092.pdf fills a page with a boxed shading).
    /// </summary>
    [Fact]
    public void AxialShading_StopsAtItsBBox()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAxialShadingBBoxPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, 50, 50);
        Assert.True(r < 240 || b < 240, $"inside the /BBox the gradient must paint (got {r},{g},{b})");

        (r, g, b) = Luma(px, image.Width, 5, 50);
        Assert.True(r > 250 && g > 250 && b > 250, $"outside the /BBox the page must stay white (got {r},{g},{b})");
    }

    /// <summary>
    /// A shading in /CalRGB with /Gamma [1 1 1] carries LINEAR samples: they must go through the white point,
    /// matrix and the sRGB transfer, so a flat 0.5 paints near 187 rather than the 128 an identity reading
    /// would give (ISO 32000-1 8.6.5.4).
    /// </summary>
    [Fact]
    public void CalRgbShading_AppliesTransfer()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateCalRgbShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte r, byte g, byte b) = Luma(px, image.Width, 50, 50);
        Assert.True(r >= 165 && r <= 210, $"a linear 0.5 must be sRGB-encoded to about 187, got {r}");
        Assert.True(Math.Abs(r - g) < 12 && Math.Abs(r - b) < 12, $"a neutral grey must stay neutral (r={r} g={g} b={b})");
    }

    /// <summary>
    /// A function's width is set by its own /Domain and /Range, not by the caller's buffer. A PostScript
    /// calculator that leaves its inputs on the stack - how DeviceN tint transforms are written - handed the
    /// extra leftover to a wider buffer and shifted every channel after it (pdf.js issue17065.pdf).
    /// </summary>
    [Fact]
    public void Type4Function_OutputWidthComesFromRangeNotTheBuffer()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateLeftoverStackFunctionPdf());
        Poso.Pdf.Functions.PdfFunction? function =
            Poso.Pdf.Functions.PdfFunction.Create(document, document.Resolve(new Poso.Pdf.Objects.PdfIndirectRef(5, 0)));
        Assert.NotNull(function);

        float[] input = [0.2f, 0.4f, 0.6f];
        var three = new float[3];
        var four = new float[4];
        function!.Evaluate(input, three);
        function.Evaluate(input, four);

        Assert.Equal(three[0], four[0], 3);
        Assert.Equal(three[1], four[1], 3);
        Assert.Equal(three[2], four[2], 3);
    }

    private static (byte R, byte G, byte B) Luma(byte[] px, int width, int userX, int userY)
    {
        int deviceRow = 100 - userY;
        int offset = ((deviceRow * width) + userX) * 4;
        return (px[offset + 2], px[offset + 1], px[offset]); // BGRA -> R,G,B
    }
}
