using System;
using System.Collections.Generic;
using System.Linq;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Pdf.Objects;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Font dictionary values that are indirect references rather than inline objects. Any PDF value may be
/// written as <c>n 0 R</c>, and repaired or subsetted producers do exactly that for /DescendantFonts and
/// /W; a reader that casts the raw value to PdfArray gets null and silently loses the descendant font -
/// the embedded program, the widths and the CID->GID map with it - so the page draws random glyphs out
/// of a system substitute font. Surfaced on page 11 of a CJK book whose Type 0 body fonts all rendered
/// as Greek/Coptic characters while MuPDF showed the correct Han text.
/// </summary>
public class IndirectFontReferenceTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Type0Font_ReadsDescendantThroughIndirectReference()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectReferenceType0Pdf());
        PdfFont font = CreateFont(document);

        // Without the descendant there is no FontDescriptor, so the program never loads and the
        // backend substitutes a system family; the embedded name proves the whole chain resolved.
        Assert.NotNull(font.GraphicsFont);
        Assert.Equal("CidTest", font.GraphicsFont!.Name);
    }

    [Fact]
    public void Type0Font_ReadsWidthsThroughIndirectReference()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectReferenceType0Pdf());
        PdfFont font = CreateFont(document);

        // /W is 1 0 R -> [ 1 [600] 2 [600] ]; an unresolved /W leaves every code at DW (1.0 em).
        Assert.Equal(0.6f, font.GetWidthEm(1));
        Assert.Equal(0.6f, font.GetWidthEm(2));
        Assert.Equal(1f, font.GetWidthEm(3));
    }

    [Fact]
    public void Type0Font_DecodesHexToUnicodeDestinations()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectReferenceType0Pdf());

        // The destinations are <0041> and <4E00>: read as raw bytes they are the ASCII digits that
        // spell them, which is what made extracted text come out as mojibake.
        Assert.Equal("A一", document.Pages[0].GetText(Factory).Text);
    }

    [Fact]
    public void Type0Font_RendersEmbeddedProgramNotASubstitute()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectReferenceType0Pdf());
        PdfPage page = document.Pages[0];

        using IGraphicsContext context = Factory.CreateContext((int)page.Width, (int)page.Height);
        page.Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128 || pixels[i + 1] < 128 || pixels[i + 2] < 128)
            {
                ink++;
            }
        }

        // Both CID glyphs are 500x400-unit filled boxes at 100pt: the substitute path draws nothing
        // here (the codes are unmapped in Arial), so any real ink means the embedded program rendered.
        Assert.True(ink > 1000, $"expected the embedded rectangles to render, got {ink} dark pixels");
    }

    /// <summary>
    /// Substituting a system face is the right thing to draw with, but it is silent by nature: widths stay
    /// and glyphs change, which is how a page of CJK ends up as .notdef boxes with nothing reported. An
    /// unreadable program must therefore announce itself - once per font, not once per glyph run.
    /// </summary>
    [Fact]
    public void UnreadableEmbeddedProgram_ReportsTheFallbackOnce()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectReferenceType0Pdf(new byte[64]));
        PdfPage page = document.Pages[0];
        using IGraphicsContext context = Factory.CreateContext((int)page.Width, (int)page.Height);
        var features = new List<PdfUnsupportedFeature>();
        page.Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.Equal(1, features.Count(f => f.Kind == PdfUnsupportedKind.FontFallback));
    }

    [Fact]
    public void ReadableEmbeddedProgram_ReportsNoFallback()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectReferenceType0Pdf());
        PdfPage page = document.Pages[0];
        using IGraphicsContext context = Factory.CreateContext((int)page.Width, (int)page.Height);
        var features = new List<PdfUnsupportedFeature>();
        page.Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.FontFallback);
    }

    private static PdfFont CreateFont(PdfDocument document)
    {
        PdfDictionary? resources = document.Resolve(document.Pages[0].Dictionary.Get("Resources")) as PdfDictionary;
        PdfDictionary? fonts = document.Resolve(resources?.Get("Font")) as PdfDictionary;
        PdfDictionary fontDictionary = (PdfDictionary)document.Resolve(fonts!["F1"])!;
        return PdfFont.Create(document, Factory, fontDictionary);
    }
}
