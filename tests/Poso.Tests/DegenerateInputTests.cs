using System;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Inputs that make a reader give up on a page rather than on the document: a colour lookup with nothing
/// to index, and a cross-reference entry pointing outside the file. Both threw, which costs the caller the
/// whole page instead of one wrong colour or one missing object.
/// </summary>
public class DegenerateInputTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    /// <summary>
    /// An /Indexed lookup shorter than one entry has no index to clamp to. Math.Clamp throws when min > max,
    /// so pdf.js issue1985.pdf lost its page to a colour it could not resolve; black is the answer.
    /// </summary>
    [Fact]
    public void IndexedColourWithAnEmptyLookup_PaintsInsteadOfThrowing()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShortIndexedLookupPdf());
        using IGraphicsContext context = Factory.CreateContext(200, 100);

        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        Assert.True(image.Width > 0);
    }

    /// <summary>
    /// A lexer handed a start past the end of the buffer reports "nothing here" rather than indexing past
    /// its data. Defensive: the construction that reached it in practice - a cross-reference entry beyond
    /// the last byte, as in pdf.js operator_list_cycle.pdf - also has <c>End</c> at the file length, so
    /// this pins the clamp itself rather than reproducing that file's exact path.
    /// </summary>
    [Fact]
    public void LexerStartedPastTheEnd_ReportsNoMoreInput()
    {
        byte[] data = Encoding.ASCII.GetBytes("%PDF-1.7\n");

        var lexer = new Poso.Pdf.Parsing.PdfLexer(data, data.Length + 4096);
        lexer.SkipWhiteSpace();
        Assert.True(lexer.AtEnd);
        Assert.Equal(Poso.Pdf.Parsing.PdfTokenKind.Eof, lexer.NextToken().Kind);
    }
}
