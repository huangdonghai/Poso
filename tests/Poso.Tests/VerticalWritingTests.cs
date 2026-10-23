using System;
using System.IO;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// Vertical-writing (WMode 2) support. Rendering a page whose font is a vertical CID font (/Identity-V or an
/// embedded CMap with /WMode 2) must lay the upright glyphs down a vertical column, not left-to-right.
/// Oracle: MuPDF rendering of the pdf.js sample <c>vertical.pdf</c> page 0 (one vertical column of
/// 'あいうえお日本語' with a second column to its left) gives ink 230, device bbox (214,22)-(237,66) at scale 1.
/// Poso matches that bbox to ~1px by applying the PDF vertical position vector (drop the baseline one VY;
/// centre each glyph horizontally by half its advance). Poso's ink is lower (~171) because text now
/// renders through Skia's glyph atlas (faster), whose coverage-AA inks small CJK glyphs lighter than the
/// analytic path fill; the geometry, not the ink density, is the fidelity guard.
/// </summary>
public class VerticalWritingTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void CMap_ParsesWModeVertical()
    {
        byte[] vertical = System.Text.Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapType 2 def /WMode 2 def\n" +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 begincidrange <0000> <00FF> 0 endcidrange endcmap");
        byte[] horizontal = System.Text.Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapType 2 def /WMode 0 def\n" +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 begincidrange <0000> <00FF> 0 endcidrange endcmap");

        Assert.True(CMap.Parse(vertical)?.IsVertical);
        Assert.False(CMap.Parse(horizontal)?.IsVertical);
        Assert.False(CMap.Identity.IsVertical);
    }

    /// <summary>
    /// An encoding CMap may carry its mapping as beginbfchar/beginbfrange instead of cidchar/cidrange.
    /// The operators are ToUnicode syntax, but pdf.js reads the destination string's bytes as a CID
    /// (convertCidString) and so renders pdf.js bug920426.pdf, whose /Encoding is exactly this - the
    /// file's text comes out instead of .notdef boxes.
    /// </summary>
    [Fact]
    public void CMap_ParsesBfCharAndBfRangeAsCids()
    {
        byte[] cmap = System.Text.Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "2 beginbfchar\n<0043> <0026>\n<0068> <004B>\nendbfchar\n" +
            "1 beginbfrange\n<0020> <0021> <0003>\nendbfrange\n" +
            "1 beginbfrange\n<0065> <0067> [<0048> <0049> <004A>]\nendbfrange\n" +
            "endcmap end end");

        CMap? parsed = CMap.Parse(cmap);
        Assert.NotNull(parsed);

        // bfchar: the two-byte destination is the CID, big-endian.
        Assert.True(parsed!.TryLookup(new byte[] { 0x00, 0x43 }, out int cid));
        Assert.Equal(0x26, cid);
        Assert.True(parsed.TryLookup(new byte[] { 0x00, 0x68 }, out cid));
        Assert.Equal(0x4B, cid);

        // bfrange: consecutive CIDs from the first destination.
        Assert.True(parsed.TryLookup(new byte[] { 0x00, 0x20 }, out cid));
        Assert.Equal(0x03, cid);
        Assert.True(parsed.TryLookup(new byte[] { 0x00, 0x21 }, out cid));
        Assert.Equal(0x04, cid);

        // bfrange with an array destination: one CID per code, in order.
        Assert.True(parsed.TryLookup(new byte[] { 0x00, 0x65 }, out cid));
        Assert.Equal(0x48, cid);
        Assert.True(parsed.TryLookup(new byte[] { 0x00, 0x67 }, out cid));
        Assert.Equal(0x4A, cid);
    }

    [Fact]
    public void VerticalIdentityV_RendersTallColumn_MatchesMuPdf()
    {
        byte[] pdf = File.ReadAllBytes(PdfSample("vertical.pdf"));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        PdfPage page = document.Pages[0];

        using IGraphicsContext context = Factory.CreateContext((int)System.Math.Ceiling(page.Width), (int)System.Math.Ceiling(page.Height));
        page.Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        int w = image.Width, h = image.Height;
        byte[] px = new byte[w * h * 4];
        image.CopyPixels(px);

        int ink = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Dark(px, w, x, y))
                {
                    ink++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        Assert.True(ink > 0, "vertical column must render glyphs");
        // The fix: the run is tall (height) rather than wide (width) - a vertical column.
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        Assert.True(height > width, $"expected a vertical column, got width {width} > height {height}");

        // Geometry (bbox) matches the MuPDF oracle (214,22)-(237,66) to ~1px: the vertical position
        // vector (drop by one VY, shift left by half the horizontal advance) aligns both axes. The
        // tight bbox bounds catch a revert of the half-width shift (moves the column ~5px right).
        // Ink is a looser band: text renders via Skia's glyph atlas (faster), whose coverage-AA inks
        // small CJK glyphs lighter than the analytic path fill (atlas ~171 vs path/MuPDF ~230); the
        // band accepts either so it guards "renders, not blank/over-heavy", not exact density.
        Assert.InRange(ink, 140, 270);
        Assert.InRange(width, 20, 28);
        Assert.InRange(height, 42, 50);
        Assert.InRange(minX, 210, 218);
        Assert.InRange(maxX, 233, 241);
        Assert.InRange(minY, 19, 25);
        Assert.InRange(maxY, 63, 71);
    }

    /// <summary>
    /// Vertical text extraction groups glyphs into columns (a line break is a horizontal jump to the next
    /// column), not into horizontal rows. MuPDF sees 2 columns in vertical.pdf page 0: the right column
    /// 'あいうえお' (5 glyphs) then the left column '日本語' (3 glyphs) ~15.6 user units to its left.
    /// (The characters extract as fallback glyphs because Poso has no Adobe-Japan1 CID->Unicode reverse map -
    /// a separate deferred feature - so this asserts structure/geometry, not the character values.)
    /// </summary>
    [Fact]
    public void VerticalText_ExtractionGroupsByColumn()
    {
        byte[] pdf = File.ReadAllBytes(PdfSample("vertical.pdf"));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        PdfTextPage text = document.Pages[0].GetText(Factory);

        Assert.Equal(2, text.Lines.Count);
        Assert.Equal(5, text.Lines[0].Text.Length); // first (right) column: 5 glyphs
        Assert.Equal(3, text.Lines[1].Text.Length); // second (left) column: 3 glyphs

        RectF col0 = text.Lines[0].Bounds;
        RectF col1 = text.Lines[1].Bounds;
        float w0 = System.Math.Abs(col0.Right - col0.Left);
        float h0 = System.Math.Abs(col0.Top - col0.Bottom);
        float h1 = System.Math.Abs(col1.Top - col1.Bottom);

        // Each column is tall and narrow (a vertical run), not a horizontal row.
        Assert.True(h0 > w0, $"first column should be tall: w{w0} h{h0}");
        Assert.True(h1 > w1(col1), "second column should be tall");
        Assert.InRange(w0, 5, 15);
        // The right column comes first and is offset left by the next column (~15.6 user units).
        Assert.True(col0.Left > col1.Left, "reading order: right column before left column");
        Assert.InRange(col0.Left - col1.Left, 10, 22);
        // First column (5 glyphs) is taller than the second (3 glyphs).
        Assert.True(h0 > h1);

        static float w1(RectF r) => System.Math.Abs(r.Right - r.Left);
    }

    private static bool Dark(byte[] px, int width, int x, int y)
    {
        int o = ((y * width) + x) * 4;
        return px[o] < 200 || px[o + 1] < 200 || px[o + 2] < 200;
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
