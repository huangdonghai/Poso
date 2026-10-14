using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

public class TextSearchTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void GetText_ExtractsWordsAndPositions()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        PdfTextPage text = document.Pages[0].GetText(Factory);

        Assert.Contains("Hello", text.Text);
        Assert.Contains("Poso", text.Text);
        Assert.NotEmpty(text.Lines);

        // The text baseline sits at PDF y=50; the extracted span must be near it (y-up)
        TextLine line = text.Lines[0];
        Assert.True(line.Bounds.Top < 60f && line.Bounds.Bottom > 40f,
            $"Text line should straddle y=50, got bounds {line.Bounds}");
        Assert.True(line.Bounds.Left >= 15f && line.Bounds.Left <= 25f,
            $"Text should start near x=20, got {line.Bounds.Left}");
    }

    [Fact]
    public void Search_FindsHitsWithRects()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        PdfTextPage text = document.Pages[0].GetText(Factory);

        IReadOnlyList<TextHit> hits = text.Search("Poso");
        Assert.Single(hits);

        TextHit hit = hits[0];
        Assert.False(hit.Bounds.IsEmpty);
        Assert.True(hit.Bounds.Left > 20f, $"Hit should be to the right of the text start: {hit.Bounds}");
        Assert.True(hit.Bounds.Top < 60f && hit.Bounds.Bottom > 40f, $"Hit should be on the text line: {hit.Bounds}");

        // Case-insensitive search finds the same hit
        Assert.Single(text.Search("POSO"));
        Assert.Single(text.Search("hello"));

        // No match
        Assert.Empty(text.Search("Zebra"));
    }

    [Fact]
    public void GetText_IsCached()
    {
        byte[] data = PdfFixtures.CreateGraphicsPdf();

        using PdfDocument document = PdfDocument.FromArray(data);
        PdfTextPage first = document.Pages[0].GetText(Factory);
        PdfTextPage second = document.Pages[0].GetText(Factory);

        Assert.Same(first, second);
    }

    [Fact]
    public void TdThenTStar_StepDownByLeading()
    {
        // Regression: TD sets the leading (TL = -ty); a following T* must step by that leading in the
        // same direction as TD. A sign bug stacked line 3 back onto line 1 (visible as overlapping text
        // on the "Advanced Flutter" cover subtitle).
        byte[] data = PdfFixtures.CreateTextLeadingPdf();
        using PdfDocument document = PdfDocument.FromArray(data);
        PdfTextPage text = document.Pages[0].GetText(Factory);

        float Top(string label) => Assert.Single(text.Lines, l => l.Text == label).Bounds.Top;

        float l1 = Top("L1"), l2 = Top("L2"), l3 = Top("L3");
        // y-up: each successive line sits ~20pt lower; L3 must be ~40pt below L1, not on top of it.
        Assert.True(l1 > l2 && l2 > l3, $"lines must descend (y-up): L1={l1} L2={l2} L3={l3}");
        Assert.True(l1 - l3 > 30f, $"T* should step down by the leading, got L1-L3={l1 - l3}");
    }

    [Fact]
    public void GlyphSpacing_ScalesLinearlyWithFontSize()
    {
        // Regression: glyph pen positions were fed the user-space advance (widthEm * fontSize) and then
        // scaled by the text-space matrix a second time, so a Tf size of 40 sprawled the line roughly
        // fontSize times too wide (only visible when Tf size != 1).
        byte[] data = PdfFixtures.CreateScaledTextPdf();
        using PdfDocument document = PdfDocument.FromArray(data);
        PdfTextPage text = document.Pages[0].GetText(Factory);

        TextLine line = Assert.Single(text.Lines, l => l.Text == "MMMMM");
        // 5 glyphs, each 0.5 em at 40pt = 20pt advance, starting at x=20: the run ends near x=120.
        // The double-scaling bug would push the end into the thousands.
        Assert.True(MathF.Abs(line.Bounds.Right - 120f) < 12f,
            $"line should end near x=120 (advance linear in font size), got {line.Bounds.Right}");
    }
}
