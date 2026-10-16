using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using SkiaSharp;
using Xunit;

namespace Poso.Tests;

public class ImageDecoderTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Indexed_TruncatedPalette_RendersWithoutCrashing()    {
        byte[] data = PdfFixtures.CreateTruncatedIndexedPdf();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"poso-indexed-{Guid.NewGuid():N}.pdf");
        System.IO.File.WriteAllBytes(path, data);

        try
        {
            using PdfDocument document = PdfDocument.Open(path);
            using IGraphicsContext context = Factory.CreateContext(200, 100);
            document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// An Indexed colour space whose colour table is a stream rather than a string. Reading only the
    /// string form silently produced an empty table, and the empty-table guard then paints black - so a
    /// figure whose background is index 0 (CMYK all zero) came out as a solid black block. 264 of the
    /// images in the CJK book corpus use the stream form.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Indexed_StreamPalette_ResolvesTheColourTable(bool peach)
    {
        IGraphicsFactory factory = peach ? Poso.PeachDrawing.PeachGraphicsFactory.Default : Factory;

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateIndexedStreamPalettePdf());
        using IGraphicsContext context = factory.CreateContext(3, 4);
        var features = new System.Collections.Generic.List<PdfUnsupportedFeature>();
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        int lumaAt(int x)
        {
            int offset = ((2 * image.Width) + x) * image.BytesPerPixel;
            return (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3;
        }

        Assert.True(lumaAt(0) > 231, $"index 0 is CMYK all-zero (white), got luma {lumaAt(0)}");
        Assert.True(lumaAt(1) < 24, $"index 1 is full key (black), got luma {lumaAt(1)}");

        int redOffset = ((2 * image.Width) + 2) * image.BytesPerPixel;
        Assert.True(
            pixels[redOffset + 2] > 200 && pixels[redOffset] < 60 && pixels[redOffset + 1] < 60,
            $"index 2 is magenta+yellow (red), got BGRA({pixels[redOffset]},{pixels[redOffset + 1]},{pixels[redOffset + 2]},{pixels[redOffset + 3]})");
    }

    [Fact]
    public void DecodeImage_GrayscalePng_IsNormalizedToBgra()
    {
        // Gray8 sources (grayscale JPEG/PNG) used to break CopyPixels because the row
        // stride did not match the 4 bytes-per-pixel contract
        using var gray = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Gray8, SKAlphaType.Opaque));
        byte[] fill = new byte[gray.ByteCount];
        Array.Fill(fill, (byte)0x80);
        System.Runtime.InteropServices.Marshal.Copy(fill, 0, gray.GetPixels(), fill.Length);

        using var image = SKImage.FromBitmap(gray);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);

        using IImage? decoded = Factory.DecodeImage(png.ToArray());
        Assert.NotNull(decoded);
        Assert.Equal(4, decoded!.Width);
        Assert.Equal(4, decoded!.Height);
        Assert.Equal(PixelFormat.Bgra8888Premul, decoded.Format);

        byte[] pixels = new byte[4 * 4 * 4]; // BGRA
        decoded.CopyPixels(pixels); // must not throw
    }
}
