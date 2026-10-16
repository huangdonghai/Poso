using System;
using System.Collections.Generic;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using SkiaSharp;
using Xunit;

namespace Poso.Tests;

public class ImageDecoderTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    /// <summary>
    /// A SOF1 extended sequential JPEG must render on every back end. PeachDrawing's codec has no SOF1
    /// support and hands back nothing at all, which left a book cover - whose only content is one such
    /// image - blank on that back end while Skia drew it correctly. The image path now falls back to the
    /// managed decoder, which reads baseline and extended sequential.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void ExtendedSequentialJpeg_RendersOnEveryBackend(IGraphicsFactory factory)
    {
        byte[] jpeg = ExtendedSequentialJpeg();
        Assert.Equal(0xC1, StartOfFrameMarker(jpeg)); // guards the fixture, not the product

        using PdfDocument document = PdfDocument.FromArray(
            PdfFixtures.CreateDctDecodeImagePdf(jpeg, 64, 64, "/DeviceRGB"));
        using IGraphicsContext context = factory.CreateContext(64, 64);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        int offset = (((image.Height / 2) * image.Width) + (image.Width / 2)) * image.BytesPerPixel;
        (byte B, byte G, byte R) centre = (pixels[offset], pixels[offset + 1], pixels[offset + 2]);

        Assert.True(centre.R > 150 && centre.G < 110 && centre.B < 140,
            $"the red patch should decode on every backend (got {centre})");
    }

    /// <summary>
    /// A 64x64 solid (220,40,60) JPEG whose frame header is extended sequential (SOF1). Embedded rather
    /// than encoded at run time because the SkiaSharp build under test has no JPEG encoder.
    /// </summary>
    private const string SolidRedSof1JpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAIBAQEBAQIBAQECAgICAgQDAgICAgUEBAMEBgUGBgYFBgYGBwkIBgcJBwYGCAsI"
        + "CQoKCgoKBggLDAsKDAkKCgr/2wBDAQICAgICAgUDAwUKBwYHCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoK"
        + "CgoKCgoKCgoKCgoKCgr/wQARCABAAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAA"
        + "AgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6"
        + "Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG"
        + "x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREA"
        + "AgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5"
        + "OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPE"
        + "xcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5/ooor8vP9YAooooAKKKKACiiigAooooA"
        + "KKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigD/2Q==";

    private static byte[] ExtendedSequentialJpeg() => Convert.FromBase64String(SolidRedSof1JpegBase64);

    /// <summary>
    /// The JPEG's start-of-frame marker, found by walking the marker segments. Searching for the byte
    /// pair anywhere is not enough: the same pair occurs inside Huffman table payloads, and a naive
    /// search there silently leaves the frame header at SOF0 - which every backend decodes, so the test
    /// passes without exercising anything.
    /// </summary>
    private static int StartOfFrameMarker(byte[] jpeg)
    {
        int i = 2;
        while (i + 1 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = jpeg[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
            {
                return marker;
            }

            if (marker is 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }

        throw new InvalidOperationException("the fixture JPEG has no start-of-frame marker");
    }

    public static IEnumerable<object[]> Backends()
    {
        yield return [SkiaGraphicsFactory.Default];
        yield return [Poso.PeachDrawing.PeachGraphicsFactory.Default];
    }

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
