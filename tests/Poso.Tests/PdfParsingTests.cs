using System.Text;
using Poso.Pdf;
using Xunit;

namespace Poso.Tests;

public class PdfParsingTests
{
    /// <summary>
    /// A dictionary ends at <c>&gt;&gt;</c> or at a lone <c>&gt;</c>. Matching only the doubled form loses
    /// the object - and the page hanging off it - on files from the PDF Association's difference corpus.
    /// </summary>
    [Fact]
    public void Open_DictionaryClosedByASingleAngleBracket_ReadsThePage()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateLoneAngleBracketDictionaryPdf());

        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(200f, document.Pages[0].Width);
        Assert.Equal(100f, document.Pages[0].Height);
    }

    /// <summary>
    /// <c>/UserUnit</c> rescales the box coordinates into default user space units. Ignoring it renders
    /// the page a tenth of its size and puts every placement outside the crop.
    /// </summary>
    [Fact]
    public void Open_PageWithUserUnit_ScalesTheBoxesIntoRealUnits()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateUserUnitPdf());
        PdfPage page = document.Pages[0];

        Assert.Equal(10f, page.UserUnit);
        Assert.Equal(2f, page.MediaBox.Width);
        Assert.Equal(20f, page.Width);
        Assert.Equal(20f, page.Height);
    }

    /// <summary>
    /// A /Kids chain that closes on itself costs the branch that closes it and nothing else: MuPDF's
    /// reader shows every page it reached before the cycle, and walking the cycle regardless cannot even
    /// be reported - a stack overflow ends the process.
    /// </summary>
    [Fact]
    public void Open_CyclicPageTree_KeepsThePagesBeforeTheCycle()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateCyclicPageTreePdf());

        Assert.Single(document.Pages);
        Assert.Equal(200f, document.Pages[0].Width);
    }

    /// <summary>
    /// A page box written as an indirect reference is still the page's box.
    /// </summary>
    [Fact]
    public void Open_IndirectMediaBox_ReadsTheReferencedArray()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectPageBoxPdf());

        Assert.Equal(300f, document.Pages[0].Width);
        Assert.Equal(200f, document.Pages[0].Height);
    }

    /// <summary>
    /// A page whose declared boxes describe nothing is rendered from the default box, which is what
    /// MuPDF does: an empty /MediaBox, an empty /CropBox, or a /CropBox lying wholly outside the media
    /// box each used to give a zero-sized page that no back end can even allocate a surface for.
    /// </summary>
    [Theory]
    [InlineData(0, 612f, 792f)]
    [InlineData(1, 612f, 792f)]
    [InlineData(2, 612f, 792f)]
    [InlineData(3, 540f, 720f)]
    public void Open_DegeneratePageBoxes_FallBackToTheDefaultBox(int index, float width, float height)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreatePageBoxVariantsPdf());

        Assert.Equal(4, document.Pages.Count);
        Assert.Equal(width, document.Pages[index].Width);
        Assert.Equal(height, document.Pages[index].Height);
    }

    [Fact]
    public void Open_SimplePdf_ReadsPagesBoxesAndContent()
    {
        byte[] data = PdfFixtures.CreateSimplePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        Assert.Equal("1.4", document.Version);
        Assert.False(document.WasRebuilt);
        Assert.Equal(2, document.PageCount);

        PdfPage page0 = document.Pages[0];
        Assert.Equal(0, page0.Index);
        Assert.Equal(200f, page0.Width);
        Assert.Equal(100f, page0.Height);
        Assert.Equal(0, page0.Rotation);
        Assert.Contains("Hello Poso", Encoding.ASCII.GetString(page0.GetContent()));

        PdfPage page1 = document.Pages[1];
        Assert.Equal(90, page1.Rotation);
        Assert.Equal(150f, page1.Width);
        Assert.Equal(300f, page1.Height);
    }

    [Fact]
    public void Open_CatalogResolves()
    {
        byte[] data = PdfFixtures.CreateSimplePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        Assert.Equal("Catalog", document.Catalog?.GetNameValue("Type"));
        Assert.Null(document.CatalogInfo.Outlines);
        Assert.Equal(2, document.CatalogInfo.PageTreeRoot?.GetIntegerValue("Count"));
    }

    [Fact]
    public void Open_FlateContent_DecodesStream()
    {
        byte[] data = PdfFixtures.CreateFlatePdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        Assert.Equal(1, document.PageCount);
        Assert.Contains("Compressed Content", Encoding.ASCII.GetString(document.Pages[0].GetContent()));
    }

    [Fact]
    public void Open_ObjectStreamAndXrefStream_ResolvesObjects()
    {
        byte[] data = PdfFixtures.CreateObjectStreamPdf();

        using PdfDocument document = PdfDocument.FromArray(data);

        Assert.False(document.WasRebuilt);
        Assert.Equal(1, document.PageCount);

        PdfPage page = document.Pages[0];
        Assert.Equal(120f, page.Width);
        Assert.Equal(60f, page.Height);
        Assert.Contains("InObjStm", Encoding.ASCII.GetString(page.GetContent()));
    }

    [Fact]
    public void Open_CorruptStartXref_RebuildsByScanning()
    {
        byte[] data = PdfFixtures.CreateSimplePdf();
        int index = LastIndexOf(data, "startxref"u8);
        Assert.True(index >= 0);

        // Overwrite the real startxref offset with an out-of-range value to trigger the rebuild
        byte[] garbage = Encoding.ASCII.GetBytes("999999");
        Array.Copy(garbage, 0, data, index + "startxref".Length, garbage.Length);

        using PdfDocument document = PdfDocument.FromArray(data);

        Assert.True(document.WasRebuilt);
        Assert.Equal(2, document.PageCount);
        Assert.Contains("Hello Poso", Encoding.ASCII.GetString(document.Pages[0].GetContent()));
    }

    [Fact]
    public void Open_MissingFile_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfDocument.Open((string)null!));
    }

    [Fact]
    public void Open_NotAPdf_ThrowsFormat()
    {
        byte[] data = Encoding.ASCII.GetBytes("this is definitely not a pdf file at all");
        Assert.Throws<Poso.Pdf.PdfFormatException>(() => PdfDocument.FromArray(data));
    }

    private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> keyword)
    {
        for (int i = data.Length - keyword.Length; i >= 0; i--)
        {
            if (data.AsSpan(i, keyword.Length).SequenceEqual(keyword))
            {
                return i;
            }
        }

        return -1;
    }
}
