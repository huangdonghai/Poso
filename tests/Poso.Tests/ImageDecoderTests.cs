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
    public void Indexed_TruncatedPalette_RendersWithoutCrashing()
    {
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
