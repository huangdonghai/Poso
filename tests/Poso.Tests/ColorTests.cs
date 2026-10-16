using System;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Colors;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>M6: CIELAB color space support - conversion correctness and rendering integration.</summary>
public class ColorTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;
    private static readonly float[] D65 = { 0.9505f, 1f, 1.089f };

    /// <summary>
    /// A PostScript tint transform that reorders the stack with <c>roll</c> must paint the rolled
    /// colour. DeviceN tint transforms are written almost entirely with roll, so an evaluator that
    /// gives up on the operator leaves the operands in the wrong slots and the fill comes out with its
    /// channels permuted.
    /// </summary>
    [Fact]
    public void TintTransform_RollsTheStack()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateRollTintTransformPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 200);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        int offset = (100 * image.Width + 100) * image.BytesPerPixel;
        byte b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];

        // The program leaves [0, 1, tint] on the stack: red 0, green 1, blue 0.5.
        Assert.True(r < 40, $"red should be the rolled 0 (got {r})");
        Assert.True(g > 215, $"green should be the rolled 1 (got {g})");
        Assert.InRange(b, 100, 160);
    }

    [Fact]
    public void LabEndpointsAndNeutralAreGray()
    {
        PosoColorF black = Cielab.ToRgb(0, 0, 0, D65);
        Assert.True(black.R < 0.02f && black.G < 0.02f && black.B < 0.02f, $"L=0 should be black ({black})");

        PosoColorF white = Cielab.ToRgb(100, 0, 0, D65);
        Assert.True(white.R > 0.98f && white.G > 0.98f && white.B > 0.98f, $"L=100 should be white ({white})");

        // Neutral (a = b = 0) must give R == G == B, with brightness monotonically increasing in L.
        float previous = -1f;
        foreach (int l in new[] { 10, 30, 50, 70, 90 })
        {
            PosoColorF c = Cielab.ToRgb(l, 0, 0, D65);
            Assert.True(Math.Abs(c.R - c.G) < 0.01f && Math.Abs(c.G - c.B) < 0.01f, $"neutral L={l} not gray ({c})");
            Assert.True(c.R > previous, $"brightness must increase with L ({l}: {c.R} vs {previous})");
            previous = c.R;
        }
    }

    [Fact]
    public void CalGrayRoundTripsMidLuminance()
    {
        // A D65 CalGray with gamma 2.2 maps value v to sRGB ~v (decode then sRGB-encode round-trips).
        var cal = new CalColor { WhitePoint = new[] { 0.9505f, 1f, 1.089f }, Gamma = new[] { 2.2f, 2.2f, 2.2f } };
        PosoColorF gray = cal.ToRgb(new float[] { 0.5f });
        Assert.True(Math.Abs(gray.R - gray.G) < 0.02f && Math.Abs(gray.G - gray.B) < 0.02f, $"should be neutral ({gray})");
        Assert.True(Math.Abs(gray.R - 0.5f) < 0.1f, $"v=0.5 should round-trip near 0.5 ({gray})");
    }

    [Fact]
    public void CalRGBWithSrgbParametersProducesExpectedColor()
    {
        // CalRGB using the sRGB primaries matrix + gamma 2.2 + D65 white point: a warm input must
        // decode to a red-dominant color (validates the gamma -> matrix -> sRGB-encode chain).
        var cal = new CalColor
        {
            WhitePoint = new[] { 0.9505f, 1f, 1.089f },
            Gamma = new[] { 2.2f, 2.2f, 2.2f },
            Matrix = new[] { 0.4124f, 0.3576f, 0.1805f, 0.2126f, 0.7152f, 0.0722f, 0.0193f, 0.1192f, 0.9505f },
        };
        PosoColorF c = cal.ToRgb(new float[] { 1f, 0.3f, 0.2f });
        Assert.True(c.R > 0.9f, $"red should stay high ({c})");
        Assert.True(c.G > 0.12f && c.G < 0.4f, $"green should be low-mid ({c})");
        Assert.True(c.B > 0.08f && c.B < 0.32f, $"blue should be low ({c})");
        Assert.True(c.R > c.G && c.G > c.B, $"should be red-dominant warm color ({c})");
    }

    [Fact]
    public void LabColorSpaceRendersChromaticNotBlack()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateLabPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.Black));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // device (col 40, row 60) -> user (40,40) inside the Lab-filled rect.
        int offset = ((60 * image.Width) + 40) * 4;
        (byte b, byte g, byte r) = (px[offset], px[offset + 1], px[offset + 2]);

        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        // Not black (previous behavior) and genuinely chromatic (a=60,b=-60 -> reddish-blue, green low).
        Assert.True(max > 60, $"Lab fill should not be black (r={r} g={g} b={b})");
        Assert.True(max - min > 40, $"Lab fill should be chromatic (r={r} g={g} b={b})");
        Assert.True(g < r && g < b, $"a>0,b<0 should give low green (r={r} g={g} b={b})");
    }
}
