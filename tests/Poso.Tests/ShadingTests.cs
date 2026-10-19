using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7: axial (Type 2) shading rendering. A black-to-white horizontal gradient must darken the left
/// edge and lighten the right edge, with a mid-tone in the centre.
/// </summary>
public class ShadingTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void AxialShading_ProducesHorizontalGradient()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAxialShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        byte left = GrayAt(pixels, image.Width, 50, 25);
        byte middle = GrayAt(pixels, image.Width, 50, 100);
        byte right = GrayAt(pixels, image.Width, 50, 175);

        Assert.True(left < 90, $"Left edge should be dark (got {left})");
        Assert.True(right > 170, $"Right edge should be light (got {right})");
        Assert.True(middle > left && middle < right, $"Centre should be between the ends (left={left} mid={middle} right={right})");
    }

    [Fact]
    public void RadialShading_DarkensTowardCentre()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateRadialShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        byte centre = GrayAt(pixels, image.Width, 50, 100);   // user (100,50): t = 0 -> black
        byte corner = GrayAt(pixels, image.Width, 90, 10);     // user (10,10): ~0.49 radius -> mid-gray

        Assert.True(centre < 40, $"Radial centre should be dark (got {centre})");
        Assert.True(corner > centre, $"Radial corner should be lighter than centre (centre={centre} corner={corner})");
    }

    /// <summary>
    /// A colour space written as <c>[/Pattern]</c> with no base space still selects a pattern, so the
    /// shading pattern painted through it must show. Reading the array as malformed instead leaves the
    /// fill at whatever solid colour the graphics state happened to hold.
    /// </summary>
    [Fact]
    public void BarePatternSpace_SelectsTheShadingPattern()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateBarePatternSpaceShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte R, byte G, byte B) At(int x, int y)
        {
            int offset = ((y * image.Width) + x) * image.BytesPerPixel;
            return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }

        (byte R, byte G, byte B) left = At(20, 50);
        (byte R, byte G, byte B) right = At(80, 50);
        Assert.True(left.B > 170 && left.G < 90, $"the blue end should show at x=20 (got {left})");
        Assert.True(right.G > 170 && right.B < 90, $"the green end should show at x=80 (got {right})");
    }

    /// <summary>
    /// A shading pattern whose colour map is a stitching function with every operand written as an
    /// indirect reference must still paint (pdf.js issue7821.pdf). Resolving <c>/Functions</c> is what
    /// keeps the whole shading from vanishing; the ramp must match the inline-operand form.
    /// </summary>
    [Fact]
    public void StitchingIndirectOperands_ResolveAndPaint()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateStitchingIndirectShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.PatternColorSpace);
        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte R, byte G, byte B) At(int x, int y)
        {
            int offset = ((y * image.Width) + x) * image.BytesPerPixel;
            return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }

        (byte R, byte G, byte B) left = At(20, 50);
        (byte R, byte G, byte B) right = At(80, 50);
        Assert.True(left.B > 170 && left.G < 90, $"the blue end should show at x=20 (got {left})");
        Assert.True(right.G > 170 && right.B < 90, $"the green end should show at x=80 (got {right})");
    }

    /// <summary>
    /// A function-based (Type 1) shading must paint: its function takes the shading-space point itself,
    /// and <c>/Matrix</c> is what places that space. Nine of these sit on one page of the pdf.js suite and
    /// all of them came out as the background.
    /// </summary>
    [Fact]
    public void FunctionShading_MapsItsMatrixAndEvaluatesTwoInputs()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateFunctionShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte R, byte G, byte B) At(int x, int y)
        {
            int offset = ((y * image.Width) + x) * image.BytesPerPixel;
            return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }

        (byte R, byte G, byte B) left = At(10, 50);
        (byte R, byte G, byte B) right = At(90, 50);
        Assert.True(left.R > 10 && left.R < 50, $"x=10 of a 0..1 ramp should be a low red (got {left})");
        Assert.True(right.R > 200, $"x=90 of a 0..1 ramp should be a strong red (got {right})");
        Assert.Equal(0, left.G);
        Assert.Equal(0, left.B);
    }

    /// <summary>
    /// A shading defined in a Separation space paints the tint transform's colour, not the raw tint.
    /// Regression: the shading reader only understood device spaces, so a Separation or DeviceN array
    /// fell through to its base and the single tint was painted as if it were a grey/red component -
    /// which turned a full-page green wash on an Illustrator cover near black.
    /// </summary>
    [Fact]
    public void SeparationShading_PaintsTheTransformedTint()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSeparationShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte B, byte G, byte R) left = At(pixels, image.Width, 50, 10);
        (byte B, byte G, byte R) right = At(pixels, image.Width, 50, 190);

        Assert.True(left.R > 150 && left.G < 80, $"tint 0 should be red (got {left})");
        Assert.True(right.G > 150 && right.R < 80, $"tint 1 should be green (got {right})");
    }

    /// <summary>
    /// A CMYK shading must reach the same RGB as the same CMYK painted as a plain fill. The shading
    /// rasteriser had its own textbook <c>1 - (c + k)</c> conversion, which sends a cyan-and-yellow wash
    /// to full green - a cover went from matching MuPDF to differing on 55% of its pixels over nothing
    /// but that formula.
    /// </summary>
    [Theory]
    [MemberData(nameof(GraphicsFactories))]
    public void CmykShading_MatchesTheSharedCmykConversion(IGraphicsFactory factory)
    {
        PosoColor expected = PosoColor.FromCmyk(0.46f, 0f, 0.4775f, 0f);
        Assert.True(expected.G < 240, $"the reference conversion itself should not pin green at full (got {expected.G})");

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateCmykShadingPdf());
        using IGraphicsContext context = factory.CreateContext(200, 200);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        (byte B, byte G, byte R) painted = At(pixels, image.Width, 100, 100);

        Assert.Equal(expected.R, painted.R);
        Assert.Equal(expected.G, painted.G);
        Assert.Equal(expected.B, painted.B);
    }

    /// <summary>
    /// <c>/Extend [true true]</c> must paint the end colour past the end circle. The entries are PDF
    /// booleans, so a reader that asks for a number sees false and clips the gradient: a full-page wash
    /// then stops dead in a hard circle in the middle of the page.
    /// </summary>
    [Fact]
    public void ExtendedRadialShading_PaintsPastTheEndCircle()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateExtendedRadialShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 200);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        foreach ((int x, int y) in new[] { (5, 5), (195, 5), (5, 195), (195, 195) })
        {
            byte gray = GrayAt(pixels, image.Width, y, x);
            Assert.True(gray < 40, $"page corner ({x},{y}) should carry the extended colour (got {gray})");
        }
    }

    /// <summary>
    /// A shading painted inside a masked layer whose surface origin is not (0,0) has to land where the
    /// clip put it. A back-end that measures the clip in the layer's own surface pixels but draws the
    /// raster in the parent's device pixels offsets it by the layer origin, and the shading is lost -
    /// two radial washes on the pdf.js issue13520 page came out as bare background that way.
    /// </summary>
    [Theory]
    [MemberData(nameof(GraphicsFactories))]
    public void ShadingOnAnOffsetMaskedLayer_LandsWhereTheClipPutIt(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateOffsetMaskedShadingFillPdf());
        using IGraphicsContext context = factory.CreateContext(200, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // The window is user x 100..190, y 40..60 and the ramp runs 0..1 across x 0..200.
        byte left = GrayAt(pixels, image.Width, 50, 105);
        byte right = GrayAt(pixels, image.Width, 50, 185);
        Assert.True(left < 170, $"the window's dark end should carry the ramp (got {left})");
        Assert.True(right > 200, $"the window's light end should carry the ramp (got {right})");
        Assert.True(right > left + 40, $"the window should carry the ramp (left={left}, right={right})");

        Assert.True(GrayAt(pixels, image.Width, 50, 95) > 240, "left of the window should stay white");
        Assert.True(GrayAt(pixels, image.Width, 20, 150) > 240, "above the window should stay white");
        Assert.True(GrayAt(pixels, image.Width, 80, 150) > 240, "below the window should stay white");
    }

    /// <summary>
    /// A DeviceN shading whose tint transform takes more colourants than the base space (five in, CMYK out)
    /// must feed the tint every colourant - the intermediate colour buffer cannot be capped to the base's
    /// four slots or the tint reads short, its stack underflows, and the whole wash paints white. This is
    /// pdf.js postscript_type4_many_outputs.pdf: a cyan-to-white axial ramp that came out entirely white.
    /// </summary>
    [Fact]
    public void DeviceNShading_MoreColorantsThanBase_PaintsTheTint()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateDeviceN5ShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.Shading);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte B, byte G, byte R) strong = At(pixels, image.Width, 50, 90); // cyan-heavy end
        Assert.True(strong.R < 120 && strong.G > 150 && strong.B > 180, $"the cyan end should paint cyan, not white (got R{strong.R} G{strong.G} B{strong.B})");
    }

    /// <summary>
    /// A shading painted inside an optional-content group that /OCProperties /D /OFF disables must not draw.
    /// Paths honoured the suppressed state but the sh operator did not, so an OFF layer's shading painted
    /// anyway - and, because the clip was also skipped, it covered the whole page.
    /// </summary>
    [Fact]
    public void ShadingInsideHiddenOptionalContent_DoesNotPaint()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateHiddenOcgShadingPdf());
        using IGraphicsContext context = Factory.CreateContext(100, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (byte B, byte G, byte R) centre = At(pixels, image.Width, 50, 50);
        Assert.True(centre.R > 250 && centre.G > 250 && centre.B > 250,
            $"the hidden layer's shading must not paint (got R{centre.R} G{centre.G} B{centre.B})");
    }

    public static IEnumerable<object[]> GraphicsFactories()
    {
        yield return [SkiaGraphicsFactory.Default];
        yield return [Poso.PeachDrawing.PeachGraphicsFactory.Default];
    }

    private static (byte, byte, byte) At(byte[] pixels, int width, int y, int x)    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static byte GrayAt(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return pixels[offset]; // BGRA gray channel (R==G==B)
    }
}
