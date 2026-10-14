using Poso.Geometry;
using Poso.Pdf;
using Xunit;

namespace Poso.Tests;

public class OutlineLinkTests
{
    [Fact]
    public void Outline_LoadsTreeWithDestinations()
    {
        byte[] data = PdfFixtures.CreateOutlinePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        PdfOutline? outline = document.Outline;
        Assert.NotNull(outline);
        Assert.Equal(2, outline!.Children.Count);

        PdfOutline chapter1 = outline.Children[0];
        Assert.Equal("Chapter 1", chapter1.Title);
        Assert.NotNull(chapter1.Destination);
        Assert.Equal(0, chapter1.Destination!.PageIndex);
        Assert.Equal(PdfDestinationType.Fit, chapter1.Destination.Type);
        Assert.Empty(chapter1.Children);

        PdfOutline chapter2 = outline.Children[1];
        Assert.Equal("Chapter 2", chapter2.Title);
        Assert.Single(chapter2.Children);

        // Named destination resolved through /Dests
        Assert.NotNull(chapter2.Destination);
        Assert.Equal(1, chapter2.Destination!.PageIndex);
        Assert.Equal(PdfDestinationType.Xyz, chapter2.Destination.Type);

        PdfOutline section = chapter2.Children[0];
        Assert.Equal("Section 2.1", section.Title);
        Assert.NotNull(section.Destination);
        Assert.Equal(1, section.Destination!.PageIndex);
        Assert.Equal(1f, section.Destination.GetParameter(2), 3);
    }

    [Fact]
    public void Links_LoadsUriAndGoTo()
    {
        byte[] data = PdfFixtures.CreateOutlinePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        IReadOnlyList<PdfLink> links = document.Pages[0].Links;
        Assert.Equal(2, links.Count);

        PdfLink uri = links[0];
        Assert.Equal(PdfActionKind.Uri, uri.Kind);
        Assert.Equal("https://example.com", uri.Uri);
        Assert.Equal(new RectF(10, 10, 90, 20), uri.Rect);

        PdfLink goTo = links[1];
        Assert.Equal(PdfActionKind.GoTo, goTo.Kind);
        Assert.NotNull(goTo.Destination);
        Assert.Equal(1, goTo.Destination!.PageIndex);

        // Page 2 has no annotations
        Assert.Empty(document.Pages[1].Links);
    }

    [Fact]
    public void Outline_ResolvesNamedDestinationThroughIndirectNameTree()
    {
        byte[] data = PdfFixtures.CreateOutlineNamedTreePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        PdfOutline? outline = document.Outline;
        Assert.NotNull(outline);
        PdfOutline chapter = Assert.Single(outline!.Children);
        Assert.Equal("Chapter 2", chapter.Title);

        // The /A GoTo action names a destination resolved through an indirect /Names /Dests tree;
        // before the fix this was null (the tree root was skipped) so the bookmark did nothing.
        Assert.NotNull(chapter.Destination);
        Assert.Equal(1, chapter.Destination!.PageIndex);
        Assert.Equal(PdfDestinationType.Xyz, chapter.Destination.Type);
    }

    [Fact]
    public void Outline_IsNullWhenAbsent()
    {
        byte[] data = PdfFixtures.CreateSimplePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        Assert.Null(document.Outline);
    }
}
