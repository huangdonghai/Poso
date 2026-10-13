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
}
