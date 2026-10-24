using System.IO;
using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// Character-level selection on <see cref="PdfTextPage"/>: caret hit-testing in reading order,
/// text/box ranges across lines, word and line ranges, and vertical text.
/// </summary>
public class TextSelectionTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Caret_ResolvesPointsToReadingOrderPositions()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        PdfTextPage text = document.Pages[0].GetText(Factory);

        Assert.Equal("Hello Poso", text.Text);
        Assert.Equal(10, text.Length);

        // Left of the first glyph, right of the last one, and the middle of a glyph
        Assert.Equal(0, text.GetCaretIndex(new Vector2(18f, 53f)));
        Assert.Equal(10, text.GetCaretIndex(new Vector2(200f, 53f)));
        Assert.Equal(2, text.GetCaretIndex(new Vector2(30f, 53f)));

        // Far below the line still maps into the line (nearest glyph)
        int below = text.GetCaretIndex(new Vector2(30f, -50f));
        Assert.InRange(below, 0, text.Length);
    }

    [Fact]
    public void Selection_ReturnsTextAndPerLineBoxes()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        PdfTextPage text = document.Pages[0].GetText(Factory);

        PdfTextSelection hello = text.GetSelection(0, 5);
        Assert.Equal("Hello", hello.Text);
        RectF box = Assert.Single(hello.Bounds);
        Assert.InRange(box.Left, 19f, 21f);
        Assert.InRange(box.Right, 49f, 51f);
        Assert.InRange(box.Top, 46f, 49f);   // descender edge (y-up)
        Assert.InRange(box.Bottom, 58f, 61f); // ascender edge

        PdfTextSelection poso = text.GetSelection(6, 10);
        Assert.Equal("Poso", poso.Text);
        Assert.Single(poso.Bounds);

        // start/end may be given in either order
        PdfTextSelection reversed = text.GetSelection(10, 0);
        Assert.Equal("Hello Poso", reversed.Text);
        Assert.Equal(text.GetSelection(0, 10).Bounds, reversed.Bounds);

        Assert.True(text.GetSelection(3, 3).IsEmpty);
        Assert.Empty(text.GetSelection(3, 3).Bounds);
        Assert.Equal("ell", text.GetText(1, 3));
        Assert.Equal("Poso", text.GetText(6, 100));
    }

    [Fact]
    public void Selection_AcrossLines_KeepsOneBoxPerLine()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextLeadingPdf());
        PdfTextPage text = document.Pages[0].GetText(Factory);

        Assert.Equal("L1\nL2\nL3", text.Text);
        Assert.Equal(8, text.Length);

        PdfTextSelection all = text.GetSelection(0, text.Length);
        Assert.Equal("L1\nL2\nL3", all.Text);
        Assert.Equal(3, all.Bounds.Count);
        // y-up: the boxes descend from line 1 to line 3
        Assert.True(all.Bounds[0].Top > all.Bounds[1].Top && all.Bounds[1].Top > all.Bounds[2].Top);

        // A partial selection from the middle of line 1 into line 2
        PdfTextSelection partial = text.GetSelection(1, 4);
        Assert.Equal("1\nL", partial.Text);
        Assert.Equal(2, partial.Bounds.Count);
    }

    [Fact]
    public void WordAndLine_RangesFollowTheCaret()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextLeadingPdf());
        PdfTextPage text = document.Pages[0].GetText(Factory);

        Assert.Equal((0, 2), text.GetWordRange(0));
        Assert.Equal((0, 2), text.GetWordRange(2)); // on the line break: the word before it
        Assert.Equal((3, 5), text.GetWordRange(4));
        Assert.Equal((6, 8), text.GetWordRange(7));
        Assert.Equal((6, 8), text.GetWordRange(8)); // caret past the end

        Assert.Equal((0, 2), text.GetLineRange(0));
        Assert.Equal((3, 5), text.GetLineRange(4));
        Assert.Equal((3, 5), text.GetLineRange(5)); // on the line break itself
        Assert.Equal((6, 8), text.GetLineRange(8));

        using PdfDocument words = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        PdfTextPage line = words.Pages[0].GetText(Factory);
        Assert.Equal((0, 5), line.GetWordRange(4));
        Assert.Equal((0, 5), line.GetWordRange(5)); // on the space after "Hello"
        Assert.Equal((6, 10), line.GetWordRange(6));
        Assert.Equal((0, 10), line.GetLineRange(7));
    }

    [Fact]
    public void IsOverText_IsTrueOnlyInsideAGlyphBox()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        PdfTextPage text = document.Pages[0].GetText(Factory);

        // "Hello Poso" runs from x=20 to x=80 on a band from y=47.6 to y=59.6
        Assert.True(text.IsOverText(new Vector2(23f, 53f)));
        Assert.True(text.IsOverText(new Vector2(53f, 53f)));  // the inter-word space is still the line
        Assert.False(text.IsOverText(new Vector2(18f, 53f))); // before the first glyph
        Assert.False(text.IsOverText(new Vector2(150f, 53f))); // past the end of the line
        Assert.False(text.IsOverText(new Vector2(23f, 90f)));  // above the line
        Assert.False(text.IsOverText(new Vector2(23f, 5f)));   // below the line
    }

    [Fact]
    public void VerticalText_CaretAndSelectionFollowTheColumn()
    {
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(PdfSample("vertical.pdf")));
        PdfTextPage text = document.Pages[0].GetText(Factory);

        Assert.Equal(2, text.Lines.Count);
        TextLine first = text.Lines[0];
        int firstLength = first.Text.Length;

        // Reading order starts at index 0, so the first column is the range [0, length).
        PdfTextSelection column = text.GetSelection(0, firstLength);
        Assert.Equal(first.Text, column.Text);
        RectF box = Assert.Single(column.Bounds);
        Assert.True(box.Height > box.Width, $"a vertical column box should be taller than wide, got {box}");

        float centerX = box.X + (box.Width * 0.5f);
        // y-up: the column starts at its top edge (largest Y) and advances downward
        Assert.Equal(0, text.GetCaretIndex(new Vector2(centerX, box.Bottom + 10f)));
        Assert.Equal(firstLength, text.GetCaretIndex(new Vector2(centerX, box.Top - 10f)));

        // The column is text where it stands and paper beside it. Columns read right to left, so the
        // blank side is to the right of the first column.
        Assert.True(text.IsOverText(new Vector2(centerX, box.Y + (box.Height * 0.5f))));
        Assert.False(text.IsOverText(new Vector2(box.Right + 40f, box.Y + (box.Height * 0.5f))));
    }

    private static string PdfSample(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Pdf", name);
        if (File.Exists(probe))
        {
            return probe;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = IOPath.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Pdf", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"PDF sample '{name}' not found.", name);
    }
}
