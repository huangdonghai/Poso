using System.IO;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Parsing;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

public class PdfParsingTests
{
    /// <summary>
    /// A stray ')' has no token case of its own, and the default branch's scan stops immediately on any
    /// delimiter - so the old lexer returned a zero-length keyword without advancing. Every token loop then
    /// spun forever; pdf.js issue8061.pdf's binary ToUnicode stream contains ')', and the render hung.
    /// </summary>
    [Fact]
    public void NextToken_StrayCloseParen_AlwaysAdvances()
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(")))x"));
        int guard = 0;
        while (!lexer.AtEnd && guard++ < 16)
        {
            lexer.NextToken();
        }

        Assert.True(lexer.AtEnd, "a stray ')' must be consumed, not returned as a zero-length token");
    }

    /// <summary>
    /// pdf.js PDFBOX-4352-0.pdf is a fuzzed file whose /Encrypt dictionary opens with <c>E&lt;</c> where
    /// <c>&lt;&lt;</c> belongs. The dictionary cannot be read, so nothing can be decrypted; PDFium opens the
    /// document anyway and shows a blank page. Reading it must not turn the whole document into an
    /// exception - the page renders what is recoverable from its streams, which here is nothing.
    /// </summary>
    [Fact]
    public void Open_UnreadableEncryptDictionary_RendersThePageInsteadOfThrowing()
    {
        string path = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Pdf", "corrupt-encrypt.pdf");
        using PdfDocument document = PdfDocument.FromArray(File.ReadAllBytes(path));

        Assert.Equal(1, document.PageCount);
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(400, 100);
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(2f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        Assert.Equal(400, image.Width);
        Assert.Equal(100, image.Height);

        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.True(px[i] > 200 && px[i + 1] > 200 && px[i + 2] > 200, $"pixel {i / 4} is not white");
        }
    }

    /// <summary>
    /// A dictionary ends at <c>&gt;&gt;</c> or at a lone <c>&gt;</c>. Matching only the doubled form loses
    /// the object - and the page hanging off it - on files from the PDF Association's difference corpus.
    /// </summary>
    /// <summary>
    /// An array element that cannot start an object is damage; the array around it is still readable and
    /// dropping the token keeps it. pdf.js bug1953099.pdf's TJ array carries a lone '-' between two
    /// strings, and throwing on it lost the whole line.
    /// </summary>
    [Fact]
    public void Parse_ArrayWithAStrayToken_KeepsTheElementsAroundIt()
    {
        var parser = new PdfParser(Encoding.ASCII.GetBytes("[ (a) - (b) 12 ]"));
        var array = Assert.IsType<Poso.Pdf.Objects.PdfArray>(parser.ParseObject());

        Assert.Equal(3, array.Count);
        Assert.IsType<Poso.Pdf.Objects.PdfString>(array[0]);
        Assert.IsType<Poso.Pdf.Objects.PdfString>(array[1]);
        Assert.Equal(12, Assert.IsType<Poso.Pdf.Objects.PdfNumber>(array[2]).AsInt());
    }

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
    /// A box whose elements are indirect is resolved element by element. Without that the array does not
    /// read as four numbers at all and the page quietly falls back to Letter, which is what pdf.js
    /// issue7872.pdf hit: its 250x50 page came out 612x792 and every pixel landed in the wrong place.
    /// </summary>
    [Fact]
    public void Open_PageWithIndirectMediaBoxElements_UsesTheBox()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndirectMediaBoxPdf());
        PdfPage page = document.Pages[0];

        Assert.Equal(250f, page.MediaBox.Width);
        Assert.Equal(50f, page.MediaBox.Height);
        Assert.Equal(250f, page.Width);
        Assert.Equal(50f, page.Height);
    }

    /// <summary>
    /// An entry that names the wrong object is not trusted. The page it describes parses - as a different
    /// object - so the reader cannot tell from the entry alone; the object names itself, and the table is
    /// rebuilt from the file when they disagree. pdf.js issue7229.pdf loses a whole page without that:
    /// its first subsection starts at object 1 where the file's objects start at 0.
    /// </summary>
    [Fact]
    public void Open_ShiftedXrefEntry_RebuildsTheTableAndKeepsThePage()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateShiftedXrefPdf());

        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(200f, document.Pages[0].Width);
        Assert.Equal(100f, document.Pages[0].Height);
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

    /// <summary>
    /// A document whose /xref section is an empty placeholder and whose catalog sits inside an object stream
    /// still has to open: the object table is rebuilt by scanning and the object streams that scan finds are
    /// expanded, so the compressed /Root resolves (pdf.js empty_protected.pdf and its family).
    /// </summary>
    [Fact]
    public void CompressedCatalogIsRecoveredAfterRebuildingTheXref()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateCompressedCatalogAfterRebuildPdf());

        Assert.Equal(1, document.PageCount);
        PdfPage page = document.Pages[0];
        Assert.Equal(100f, page.Width);
        Assert.Equal(100f, page.Height);
    }
}
