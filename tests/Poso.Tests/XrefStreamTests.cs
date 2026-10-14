using System;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Regression for PDFs whose cross-reference is an XRef <b>stream</b> (<c>/Type/XRef</c>, PDF-1.5) with no
/// classic trailer. MuPDF always writes classic xref+trailer, so the suite never exercised this path and a
/// case-sensitivity bug (comparing against "Xref" instead of "XRef") silently rejected every real xref stream,
/// forcing scan-recovery which - finding no trailer - threw "Unable to locate ... catalog". This surfaced on a
/// real 732-page document that MuPDF opened fine but Poso could not.
/// </summary>
public class XrefStreamTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void XrefStreamOnlyPdf_LoadsRendersWithoutTrailer()
    {
        byte[] pdf = PdfFixtures.CreateXrefStreamPdf();

        // Sanity: the fixture really is an xref-stream file with no trailer keyword.
        string asLatin = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(pdf);
        Assert.Contains("/Type /XRef", asLatin);
        Assert.DoesNotContain("trailer", asLatin);

        using PdfDocument document = PdfDocument.FromArray(pdf); // threw before the fix
        Assert.Single(document.Pages);

        PdfPage page = document.Pages[0];
        Assert.Equal(200, (int)page.Width);
        Assert.Equal(100, (int)page.Height);

        using IGraphicsContext context = Factory.CreateContext(200, 100);
        page.Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        int ink = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 200 || px[i + 1] < 200 || px[i + 2] < 200)
            {
                ink++;
            }
        }

        // The catalog/pages/page tree resolved through the xref stream, so the content's stroked path rendered.
        Assert.True(ink > 0, $"expected the stroked path to render, got ink {ink}");
    }
}
