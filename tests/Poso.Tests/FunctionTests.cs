using System.Collections.Generic;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Functions;
using Poso.Pdf.Objects;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M6: PDF function evaluation (types 0/2/3/4) and its use by Separation tint transforms.
/// </summary>
public class FunctionTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Exponential_Type2_WithExponent()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());
        PdfFunction function = PdfFunction.Create(document, Exponential(0, 1, 2))!;

        Assert.Equal(1, function.InputCount);
        Assert.Equal(1, function.OutputCount);
        Assert.Equal(0.25, function.Evaluate(new float[] { 0.5f })[0], 4);
    }

    [Fact]
    public void Exponential_Type2_ClampsToRange()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());
        PdfDictionary dictionary = Exponential(0, 1, 1);
        dictionary["Range"] = Arr(0, 0.5);
        PdfFunction function = PdfFunction.Create(document, dictionary)!;

        // Linear ramp would give 0.8, but the /Range [0 0.5] clamps it.
        Assert.Equal(0.5, function.Evaluate(new float[] { 0.8f })[0], 4);
    }

    [Fact]
    public void PostScript_Type4_Arithmetic()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());
        PdfFunction function = PostScript(document, "2 mul", new double[] { 0, 1 }, new double[] { 0, 1 })!;

        Assert.Equal(1.0, function.Evaluate(new float[] { 0.5f })[0], 4);
    }

    [Fact]
    public void PostScript_Type4_IfElseThreshold()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());
        PdfFunction function = PostScript(document, "0.5 lt {1} {0} ifelse", new double[] { 0, 1 }, new double[] { 0, 1 })!;

        Assert.Equal(1.0, function.Evaluate(new float[] { 0.3f })[0], 4);
        Assert.Equal(0.0, function.Evaluate(new float[] { 0.7f })[0], 4);
    }

    /// <summary>
    /// The specification's trig operators take degrees, its <c>atan</c> takes two operands and answers in
    /// degrees folded into 0..360, <c>exp</c> is the binary power, and <c>copy</c> repeats the top of the
    /// stack. Shading functions use all of them: one paints a moiré ramp with <c>sin(angle*1440)</c> and
    /// another builds a colour wheel out of <c>sin cos atan</c>.
    /// </summary>
    [Fact]
    public void PostScript_Type4_TrigonometryIsInDegrees()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());

        PdfFunction sine = PostScript(document, "sin", new double[] { 0, 360 }, new double[] { -1, 1 })!;
        Assert.Equal(1.0, sine.Evaluate(new float[] { 90f })[0], 4);
        Assert.Equal(-1.0, sine.Evaluate(new float[] { 270f })[0], 4);

        PdfFunction cosine = PostScript(document, "cos", new double[] { 0, 360 }, new double[] { -1, 1 })!;
        Assert.Equal(-1.0, cosine.Evaluate(new float[] { 180f })[0], 4);

        PdfFunction angle = PostScript(document, "atan", new double[] { -1, 1, -1, 1 }, new double[] { 0, 360 })!;
        Assert.Equal(45.0, angle.Evaluate(new float[] { 1f, 1f })[0], 3);
        Assert.Equal(270.0, angle.Evaluate(new float[] { -1f, 0f })[0], 3);

        PdfFunction power = PostScript(document, "exp", new double[] { 0, 4, 0, 4 }, new double[] { 0, 100 })!;
        Assert.Equal(8.0, power.Evaluate(new float[] { 2f, 3f })[0], 4);

        PdfFunction copied = PostScript(
            document, "2 copy mul", new double[] { 0, 1, 0, 1 }, new double[] { 0, 1, 0, 1, 0, 1 })!;
        float[] three = copied.Evaluate(new float[] { 0.3f, 0.5f });
        Assert.Equal(0.3, three[0], 3);
        Assert.Equal(0.5, three[1], 3);
        Assert.Equal(0.15, three[2], 3);
    }

    [Fact]
    public void Sampled_Type0_LinearRamp()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());
        var dictionary = new PdfDictionary();
        dictionary["FunctionType"] = new PdfNumber(0, true);
        dictionary["Domain"] = Arr(0, 1);
        dictionary["Range"] = Arr(0, 1);
        dictionary["Size"] = Arr(2);
        dictionary["BitsPerSample"] = new PdfNumber(8, true);
        dictionary["Order"] = new PdfNumber(1, true);
        var stream = new PdfStream(dictionary, new byte[] { 0x00, 0xFF });

        PdfFunction function = PdfFunction.Create(document, stream)!;
        Assert.Equal(0.0, function.Evaluate(new float[] { 0f })[0], 3);
        Assert.Equal(1.0, function.Evaluate(new float[] { 1f })[0], 3);
        Assert.Equal(0.5, function.Evaluate(new float[] { 0.5f })[0], 3);
    }

    [Fact]
    public void Stitching_Type3_SelectsSubFunction()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSimplePdf());
        var dictionary = new PdfDictionary();
        dictionary["FunctionType"] = new PdfNumber(3, true);
        dictionary["Domain"] = Arr(0, 1);
        dictionary["Range"] = Arr(0, 1);
        dictionary["Encode"] = Arr(0, 1, 0, 1);
        dictionary["Functions"] = new PdfArray(new PdfObject[]
        {
            Exponential(0.2, 0.2, 1),
            Exponential(0.8, 0.8, 1),
        });

        PdfFunction function = PdfFunction.Create(document, dictionary)!;
        Assert.Equal(0.2, function.Evaluate(new float[] { 0.25f })[0], 4);
        Assert.Equal(0.8, function.Evaluate(new float[] { 0.75f })[0], 4);
    }

    [Fact]
    public void Separation_TintTransform_EvaluatesFunction()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateSeparationPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // Separation fill: tint 0.5 through out = t^2 gives gray 0.25 -> ~64 (identity would give ~128).
        (byte r, byte g, byte b) = Sample(pixels, image.Width, 65, 35);
        Assert.True(
            r is >= 50 and <= 80 && g is >= 50 and <= 80 && b is >= 50 and <= 80,
            $"Separation tint transform should yield ~64 gray (r={r} g={g} b={b})");

        // DeviceRGB control rectangle stays red.
        (byte rr, byte rg, byte rb) = Sample(pixels, image.Width, 65, 145);
        Assert.True(rr > 170 && rg < 90 && rb < 90, $"Control rect should be red (r={rr} g={rg} b={rb})");
    }

    private static PdfDictionary Exponential(double c0, double c1, double exponent)
    {
        var dictionary = new PdfDictionary();
        dictionary["FunctionType"] = new PdfNumber(2, true);
        dictionary["Domain"] = Arr(0, 1);
        dictionary["C0"] = Arr(c0);
        dictionary["C1"] = Arr(c1);
        dictionary["E"] = new PdfNumber(exponent, false);
        return dictionary;
    }

    private static PdfFunction? PostScript(PdfDocument document, string program, double[] domain, double[] range) =>
        PdfFunction.Create(document, PostScriptStream(program, domain, range));

    private static PdfStream PostScriptStream(string program, double[] domain, double[] range)
    {
        var dictionary = new PdfDictionary();
        dictionary["FunctionType"] = new PdfNumber(4, true);
        dictionary["Domain"] = Arr(domain);
        dictionary["Range"] = Arr(range);
        return new PdfStream(dictionary, Encoding.ASCII.GetBytes(program));
    }

    private static PdfArray Arr(params double[] values)
    {
        var items = new List<PdfObject>(values.Length);
        foreach (double value in values)
        {
            items.Add(new PdfNumber(value, false));
        }

        return new PdfArray(items);
    }

    private static (byte R, byte G, byte B) Sample(byte[] pixels, int width, int y, int x)
    {
        int offset = ((y * width) + x) * 4;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
