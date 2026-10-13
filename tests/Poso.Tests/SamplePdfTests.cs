using System.IO;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

public class SamplePdfTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void SamplePdf_ParsesEndToEnd()
    {
        byte[] data = SamplePdfGenerator.Build();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"poso-sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, data);

        try
        {
            using PdfDocument document = PdfDocument.Open(path);

            Assert.Equal(3, document.PageCount);
            Assert.Equal("1.7", document.Version);

            // Page geometry
            foreach (PdfPage page in document.Pages)
            {
                Assert.Equal(612f, page.MediaBox.Width);
                Assert.Equal(792f, page.MediaBox.Height);
            }

            // Outline: two chapters, the second with one section
            PdfOutline? outline = document.Outline;
            Assert.NotNull(outline);
            Assert.Equal(2, outline!.Children.Count);
            Assert.Equal("Chapter 1 - Introduction", outline.Children[0].Title);
            Assert.Equal(0, outline.Children[0].Destination?.PageIndex);
            Assert.Single(outline.Children[1].Children);
            Assert.Equal("Section 2.1 - Typography", outline.Children[1].Children[0].Title);

            // Links: one internal jump and one URI
            IReadOnlyList<PdfLink> links = document.Pages[0].Links;
            Assert.Equal(2, links.Count);
            Assert.Contains(links, l => l.Kind == PdfActionKind.GoTo && l.Destination?.PageIndex == 1);
            Assert.Contains(links, l => l.Kind == PdfActionKind.Uri && l.Uri == "https://example.com/poso");

            // Text extraction
            PdfTextPage page1Text = document.Pages[0].GetText(Factory);
            Assert.Contains("Poso PDF Viewer", page1Text.Text);
            Assert.Contains("from-scratch PDF pipeline", page1Text.Text);

            PdfTextPage page2Text = document.Pages[1].GetText(Factory);
            Assert.Contains("Text Rendering", page2Text.Text);
            Assert.Contains("horizontal scaling", page2Text.Text);

            PdfTextPage page3Text = document.Pages[2].GetText(Factory);
            Assert.Contains("Images and Forms", page3Text.Text);

            // Search finds text and reports a usable hit rectangle
            IReadOnlyList<TextHit> hits = page1Text.Search("Viewer");
            Assert.Single(hits);
            Assert.False(hits[0].Bounds.IsEmpty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SamplePdf_RendersNonBlankPages()
    {
        byte[] data = SamplePdfGenerator.Build();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"poso-sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, data);

        try
        {
            using PdfDocument document = PdfDocument.Open(path);

            for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                PdfPage page = document.Pages[pageIndex];
                using IGraphicsContext context = Factory.CreateContext(306, 396); // 50% zoom
                page.Render(Factory, context, new PdfRenderOptions(0.5f));

                using IImage image = context.Snapshot();
                byte[] pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixels(pixels);

                int ink = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240)
                    {
                        ink++;
                    }
                }

                Assert.True(ink > 500, $"Page {pageIndex} rendered too little ink: {ink}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SamplePdf_ShippedAsset_MatchesGenerator()
    {
        string? asset = FindAsset();
        Assert.True(asset is not null,
            "samples/Sample.Avalonia/Assets/sample.pdf is missing; regenerate it from SamplePdfGenerator.Build()");

        byte[] expected = SamplePdfGenerator.Build();
        byte[] actual = File.ReadAllBytes(asset!);
        Assert.Equal(expected, actual);
    }

    private static string? FindAsset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(directory.FullName, "samples", "Sample.Avalonia", "Assets", "sample.pdf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
