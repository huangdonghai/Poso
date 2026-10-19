using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Filters.Codecs;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Validates the CCITT Group 4 (T.6) decoder against MuPDF-generated reference bitmaps
/// (raw codestream + expected bilevel matrix, 1 = black).
/// </summary>
public class CcittTests
{
    private const string SmallRawHex = "23828450fb21897ffffc8a3ec8e0f64704b23870010010";

    private static readonly string[] SmallExpected =
    {
        "100000000000000000000000",
        "010000000000000000000000",
        "001111111111111000000000",
        "000111111111111000000000",
        "000111111111111000000000",
        "000111111111111000000000",
        "000111111111111000000000",
        "000111111111111000000000",
        "000000001000000000000000",
        "000000000100000000000000",
        "000000000010000000000000",
        "000000000001000000000000",
    };

    [Fact]
    public void Group4_SmallPattern_MatchesMuPdf()
    {
        AssertBits(Convert.FromHexString(SmallRawHex), 24, 12, SmallExpected);
    }

    [Fact]
    public void Group4_RandomNoise_MatchesMuPdf()
    {
        byte[] raw = Convert.FromHexString(RandomRawHex);
        AssertBits(raw, 40, 20, RandomExpected);
    }

    [Fact]
    public void Group4_ImageMask_PaintsTheDecodedSetBits()
    {
        byte[] raw = Convert.FromHexString(SmallRawHex);
        byte[] decoded = CcittFaxDecoder.Decode(raw, 24, 12, k: -1, encodedByteAlign: false, blackIs1: false, endOfBlock: true, endOfLine: false);
        int setBits = 0;
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                setBits += (decoded[(y * 3) + (x / 8)] >> (7 - (x % 8))) & 1;
            }
        }

        Assert.InRange(setBits, 1, 287);

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateCcittMaskPdf(raw));
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(24, 12);
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White), _ => { });

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int painted = 0;
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            if (px[i * 4] < 128) { painted++; }
        }

        // MuPDF paints the set bits of a fax stencil mask whichever way /BlackIs1 reads; the unset
        // bits leave the backdrop through. The old path ignored /ImageMask and painted the complement.
        Assert.Equal(setBits, painted);
    }

    [Fact]
    public void CcittImage_RendersNonBlank()
    {
        byte[] pdf = PdfFixtures.CreateCcittImagePdf(Convert.FromHexString(SmallRawHex));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        // The image must now be supported (no codec-unsupported report) and paint dark bars.
        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int dark = 0;
        int light = 0;
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            byte b = px[i * 4];
            if (b < 128) { dark++; } else if (b > 200) { light++; }
        }

        Assert.True(dark > 500, $"CCITT bars should paint dark pixels (dark={dark})");
        Assert.True(light > 500, $"CCITT background should stay light (light={light})");
    }

    /// <summary>
    /// /BlackIs1 chooses which decoded bit is black, so the same codestream drawn with the default (false)
    /// flag must be the exact pixel complement of the true render. Re-applying the flag on top of the
    /// decoder's already-folded output made false equal true instead - which in practice inverted every
    /// default-flag grayscale scan (pdf.js issue5747.pdf painted its whole page as a negative).
    /// </summary>
    [Fact]
    public void CcittImage_BlackIs1False_IsTheComplementOfTrue()
    {
        byte[] raw = Convert.FromHexString(SmallRawHex);
        byte[] RenderPdf(bool blackIs1)
        {
            using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateCcittImagePdf(raw, blackIs1));
            using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(200, 100);
            document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White), _ => { });
            using IImage image = context.Snapshot();
            byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
            image.CopyPixels(px);
            return px;
        }

        byte[] on = RenderPdf(true);
        byte[] off = RenderPdf(false);
        int bad = 0;
        for (int i = 0; i < on.Length; i += 4)
        {
            if (Math.Abs(on[i] + off[i] - 255) > 4) { bad++; }
        }

        Assert.True(bad <= (on.Length / 4) / 100, $"false should invert true exactly; {bad} pixels disagreed");
    }

    private static void AssertBits(byte[] raw, int columns, int rows, string[] expected)
    {
        byte[] decoded = CcittFaxDecoder.Decode(raw, columns, rows, k: -1, encodedByteAlign: false, blackIs1: true, endOfBlock: true, endOfLine: false);
        int bytesPerRow = (columns + 7) / 8;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int actual = (decoded[(y * bytesPerRow) + (x / 8)] >> (7 - (x % 8))) & 1;
                char want = expected[y][x];
                Assert.True((actual == 1 ? '1' : '0') == want, $"pixel ({x},{y}) expected {want} got {actual}");
            }
        }
    }

    private const string RandomRawHex = "26a8bb23a2e88f9bc8f11d11e2f020bc102498b840a8311410278ec8e8121650e71cae06ac2085a4621a488ed050ace3840a0ce38408121150813840abb8c6088e820ae95bb23a08223b6fe0b8a653b23e119d716aba11082859438409d608bcca729ca8d82484524ad20447eda040a20821104087a8b0c10f6c329c41250ca8104092408d820820ac2099439c7082bb13e7c690415d1c728708ba0471c1026aca1d2b38fa184931de3682418e18a09143b450e820a211741b08223e9b2870a31609c186104c228771d0223aa457304f6b80080080";

    private static readonly string[] RandomExpected =
    {
        "0110000101101001111001000101000110001011",
        "0110101000111101101011110000001001111100",
        "0000100101100011111101001011111100001000",
        "1010000011000111000111010100001000111011",
        "1100101011111001011010000000011011000001",
        "0101101001111000011110000010101001000010",
        "1000100101100001000011000001101001011110",
        "1000001110110000100100011011101000001100",
        "0000001110100000000001100010001010100101",
        "0001100011011011011101100001010000001010",
        "1011010011001100101100001100000001000010",
        "0011111000100000101110000111011000011101",
        "1101110000000101010001111100000011110100",
        "0110100100010110011000000000111110000100",
        "0111101001011000010010100011010010100010",
        "1010010001100100010111101110111000100001",
        "1000011010001111000111100011110011010000",
        "0100101000110111110010001001100001101011",
        "1100000110010000111100100110100000100011",
        "1000101110101111110000010010110001100011",
    };
}
