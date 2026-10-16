using System;
using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Filters.Codecs;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M2: JBIG2 decoding for the validated subset (page-information + MMR generic region). The expected
/// bitmap is MuPDF's own rendering of the same JBIG2 image PDF, so Poso is checked against the reference
/// renderer rather than a hand-derived pattern. Streams needing unsupported regions must degrade to null
/// (no partial/incorrect image) instead of throwing.
/// </summary>
public class Jbig2Tests
{
    // A minimal single-page JBIG2 file (40x20): file header + page-info (type 48) + an
    // immediate-lossless generic region (type 39) whose payload is the MMR codestream already
    // bit-validated against MuPDF in CcittTests.
    private const string Jbig2Base64 =
        "l0pCMg0KGgoBAAAAAQAAAAAwALAAAAAUAAAAKAAAABQAAADIAAAAyAAAAAAAAAABJwCwAAAA"
        + "5wAAACgAAAAUAAAAAAAAAAAAASaouyOi6I+byPEdEeLwILwQJJi4QKgxFBAnjsjoEhZQ5xyu"
        + "BqwghaRiGkiO0FCs44QKDOOECBIRUIE4QKu4xgiOggrpW7I6CCI7b+C4plOyPhGdcWq6EQgo"
        + "WUOECdYIvMpynKjYJIRSStIER+2gQKIIIRBAh6iwwQ9sMpxBJQyoEECSQI2CCCCsIJlDnHCC"
        + "uxPnxpBBXRxyhwi6BHHBAmrKHSs4+hhJMd42gkGOGKCRQ7RQ6CCiEXQbCCI+myhwoxYJwYYQ"
        + "TCKHcdAiOqRXME9rgAgAgAAAAAIxALAAAAAAAAAAAzMAsAAAAAA=";

    // MuPDF's per-pixel rendering of this exact image PDF (1 = black), sampled at 40x20 source resolution.
    private static readonly string[] Expected =
    {
        "1001111010010110000110111010111001110100",
        "1001010111000010010100001111110110000011",
        "1111011010011100000010110100000011110111",
        "0101111100111000111000101011110111000100",
        "0011010100000110100101111111100100111110",
        "1010010110000111100001111101010110111101",
        "0111011010011110111100111110010110100001",
        "0111110001001111011011100100010111110011",
        "1111110001011111111110011101110101011010",
        "1110011100100100100010011110101111110101",
        "0100101100110011010011110011111110111101",
        "1100000111011111010001111000100111100010",
        "0010001111111010101110000011111100001011",
        "1001011011101001100111111111000001111011",
        "1000010110100111101101011100101101011101",
        "0101101110011011101000010001000111011110",
        "0111100101110000111000011100001100101111",
        "1011010111001000001101110110011110010100",
        "0011111001101111000011011001011111011100",
        "0111010001010000001111101101001110011100",
    };

    // The same image, split so the page-information segment lives in /JBIG2Globals and only the generic
    // region segment is in the image stream (the standard layout for symbol-dictionary scans).
    private const string Jbig2GlobalsPageInfoBase64 =
        "AAAAADAAsAAAABQAAAAoAAAAFAAAAMgAAADIAAAAAA==";
    private const string Jbig2GlobalsRegionBase64 =
        "AAAAAScAsAAAAOcAAAAoAAAAFAAAAAAAAAAAAAEmqLsjouiPm8jxHRHi8CC8ECSYuECoMRQQJ47I6BIWUOccrgasIIWk"
        + "YhpIjtBQrOOECgzjhAgSEVCBOECruMYIjoIK6VuyOggiO2/guKZTsj4RnXFquhEIKFlDhAnWCLzKcpyo2CSEUkrSBEft"
        + "oECiCCEQQIeosMEPbDKcQSUMqBBAkkCNggggrCCZQ5xwgrsT58aQQV0ccocIugRxwQJqyh0rOPoYSTHeNoJBjhigkUO0"
        + "UOggohF0GwgiPpsocKMWCcGGEEwih3HQIjqkVzBPa4AIAIA=";

    [Fact]
    public void Jbig2MmrRegion_MatchesMuPdfReference()
    {
        byte[] jbig2 = Convert.FromBase64String(Jbig2Base64);
        byte[] pdf = PdfFixtures.CreateJbig2ImagePdf(jbig2);

        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        // Sample the centre of each 5x5 device block (source 40x20 scaled to 200x100).
        for (int gy = 0; gy < 20; gy++)
        {
            for (int gx = 0; gx < 40; gx++)
            {
                int dx = gx * 5 + 2;
                int dy = gy * 5 + 2;
                int offset = ((dy * image.Width) + dx) * 4;
                bool black = (px[offset] + px[offset + 1] + px[offset + 2]) < 384;
                bool want = Expected[gy][gx] == '1';
                Assert.True(black == want, $"pixel ({gx},{gy}) expected {(want ? "black" : "white")} got {(black ? "black" : "white")}");
            }
        }
    }

    /// <summary>
    /// A JBIG2 image declared as a 1-bit /ImageMask must paint the current fill colour as a stencil. The
    /// decode path used to return before /ImageMask was read, laying down an opaque black-and-white plate
    /// that covered whatever the page had drawn underneath.
    /// </summary>
    [Fact]
    public void Jbig2MmrRegion_PaintsItsInkAsAStencil()
    {
        byte[] jbig2 = Convert.FromBase64String(Jbig2Base64);
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateJbig2MaskPdf(jbig2));
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.Blue), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        for (int gy = 0; gy < 20; gy++)
        {
            for (int gx = 0; gx < 40; gx++)
            {
                int offset = ((((gy * 5) + 2) * image.Width) + (gx * 5) + 2) * 4;
                int blue = px[offset], green = px[offset + 1], red = px[offset + 2];
                bool painted = red > 200 && green < 60 && blue < 60;
                bool backdrop = blue > 200 && red < 60 && green < 60;
                Assert.True(painted || backdrop, $"pixel ({gx},{gy}) is neither the fill colour nor the page: {red},{green},{blue}");
                Assert.Equal(Expected[gy][gx] == '1', painted);
            }
        }
    }

    [Fact]
    public void Jbig2Globals_SplitSegments_MatchesMuPdfReference()
    {
        byte[] globals = Convert.FromBase64String(Jbig2GlobalsPageInfoBase64);
        byte[] region = Convert.FromBase64String(Jbig2GlobalsRegionBase64);
        byte[] pdf = PdfFixtures.CreateJbig2GlobalsPdf(region, globals);

        using PdfDocument document = PdfDocument.FromArray(pdf);
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(200, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        // The globals concatenation must let the image decode: no ImageCodec degradation reported.
        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);
        AssertSnapshotMatchesExpected(context);
    }

    private static void AssertSnapshotMatchesExpected(IGraphicsContext context)
    {
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        for (int gy = 0; gy < 20; gy++)
        {
            for (int gx = 0; gx < 40; gx++)
            {
                int dx = gx * 5 + 2;
                int dy = gy * 5 + 2;
                int offset = ((dy * image.Width) + dx) * 4;
                bool black = (px[offset] + px[offset + 1] + px[offset + 2]) < 384;
                bool want = Expected[gy][gx] == '1';
                Assert.True(black == want, $"pixel ({gx},{gy}) expected {(want ? "black" : "white")} got {(black ? "black" : "white")}");
            }
        }
    }

    [Fact]
    public void Decoder_TextRegion_DegradesToNull()
    {
        // header + a single immediate text region segment (type 6): outside the supported subset.
        var data = new List<byte>();
        data.AddRange([0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0, 0, 0, 1]);
        // segment: number=0 (4), flags=6 (1), referred=0 (1), pageAssoc=0xB0 (1), length=0 (4)
        data.AddRange([0, 0, 0, 0, 0x06, 0x00, 0xB0, 0, 0, 0, 0]);

        Assert.Null(Jbig2Decoder.Decode(data.ToArray()));
    }

    [Fact]
    public void Decoder_Garbage_ReturnsNullWithoutThrowing()
    {
        Assert.Null(Jbig2Decoder.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]));
    }
}
