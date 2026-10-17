using System;
using System.Collections.Generic;
using System.Linq;
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
    /// An image in a Separation/DeviceN space carries one sample per colorant, and the space's tint
    /// transform says what colour those make. Reading the samples as base components instead walks the
    /// raster at the wrong stride, so the plate is scrambled as well as grey.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void DeviceNImage_RunsItsTintTransformPerPixel(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateDeviceNImagePdf());
        using IGraphicsContext context = factory.CreateContext(100, 100);
        var features = new List<PdfUnsupportedFeature>();
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White), features.Add);

        Assert.DoesNotContain(features, f => f.Kind == PdfUnsupportedKind.ImageCodec);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        (int R, int G, int B) At(int x, int y)
        {
            int offset = ((y * image.Width) + x) * image.BytesPerPixel;
            return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }

        (int R, int G, int B)[] quadrants = [At(25, 25), At(75, 25), At(25, 75), At(75, 75)];
        string all = string.Join(" ", quadrants.Select(q => $"({q.R},{q.G},{q.B})"));

        // The transform maps the two colorants onto red and green, so the four pixels are the four
        // combinations of them - and no grey approximation of a single component can produce them.
        Assert.True(quadrants.Any(q => q.R > 200 && q.G < 40 && q.B < 40), $"no red-dominant pixel in {all}");
        Assert.True(quadrants.Any(q => q.G > 200 && q.R < 40 && q.B < 40), $"no green-dominant pixel in {all}");
        Assert.True(quadrants.Any(q => q.R is > 100 and < 160 && q.G is > 100 and < 160 && q.B < 40), $"no mixed pixel in {all}");
        Assert.True(quadrants.Any(q => q.R < 40 && q.G < 40 && q.B < 40), $"no empty pixel in {all}");
    }

    /// <summary>
    /// A raw DeviceCMYK image must be converted by the same maths as every other CMYK colour in the
    /// document. The one-minus-black shortcut is far darker than the coated-paper conversion MuPDF
    /// applies, and it is photographs - where most samples are mid-dark - that carry it: a book page
    /// came out 13% wrong against the oracle from nothing but this.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void RawCmykImage_UsesTheSharedCmykConversion(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateRawCmykImagePdf());
        using IGraphicsContext context = factory.CreateContext(100, 100);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        int offset = (((image.Height / 2) * image.Width) + (image.Width / 2)) * image.BytesPerPixel;
        (byte B, byte G, byte R) centre = (pixels[offset], pixels[offset + 1], pixels[offset + 2]);

        // MuPDF paints this sample (99,88,80); the shortcut paints (36,35,21).
        Assert.True(Math.Abs(centre.R - 99) <= 10 && Math.Abs(centre.G - 88) <= 10 && Math.Abs(centre.B - 80) <= 10,
            $"the image must come out MuPDF's conversion (got {centre})");
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

    public static IEnumerable<object[]> MaskedJpegCases()
    {
        foreach (object[] backend in Backends())
        {
            yield return [backend[0], false];
            yield return [backend[0], true];
        }
    }

    /// <summary>
    /// A soft mask on a JPEG has to be applied once, at the mask's own coverage, whether the mask is
    /// plain samples or itself codec-encoded. A coverage that is squared or otherwise mis-scaled leaves
    /// the image far too faint.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaskedJpegCases))]
    public void SoftMaskedJpeg_CompositesAtTheMaskCoverage(IGraphicsFactory factory, bool maskIsJpeg)
    {
        byte[] jpeg = ExtendedSequentialJpeg();

        // A JPEG-encoded mask carries no samples to read: its own decoded red channel is the coverage.
        byte maskCoverage = maskIsJpeg ? (byte)220 : (byte)64;
        using PdfDocument document = PdfDocument.FromArray(
            PdfFixtures.CreateSoftMaskedDctImagePdf(jpeg, 64, maskCoverage, maskIsJpeg));
        using IGraphicsContext context = factory.CreateContext(64, 64);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int o = (((image.Height / 2) * image.Width) + (image.Width / 2)) * image.BytesPerPixel;
        (byte B, byte G, byte R) masked = (px[o], px[o + 1], px[o + 2]);

        // The image is a solid (220,40,60) patch; blend it over white at maskCoverage/255.
        static int Expect(int channel, byte alpha) => (channel * alpha + (255 - alpha) * 255 + 127) / 255;
        Assert.True(
            Math.Abs(masked.R - Expect(220, maskCoverage)) <= 6
            && Math.Abs(masked.G - Expect(40, maskCoverage)) <= 6
            && Math.Abs(masked.B - Expect(60, maskCoverage)) <= 6,
            $"expected ({Expect(220, maskCoverage)},{Expect(40, maskCoverage)},{Expect(60, maskCoverage)}) "
            + $"at {maskCoverage}/255 over white (got {masked})");
    }

    /// <summary>
    /// A stencil mask on a JPEG has to reach the pixels. The codec is handed the compressed bytes and
    /// never sees the image dictionary, so the mask must be folded into whatever the back end returns -
    /// otherwise a masked JPEG paints its whole rectangle. A JPX cover plate in 算法导论 did exactly
    /// that and buried the page underneath itself (83% of the pixels off MuPDF).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void MaskedJpeg_CutsOutTheMaskedHalf(IGraphicsFactory factory)
    {
        using PdfDocument document = PdfDocument.FromArray(
            PdfFixtures.CreateMaskedDctImagePdf(ExtendedSequentialJpeg(), 64, 64));
        using IGraphicsContext context = factory.CreateContext(128, 64);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        (byte B, byte G, byte R) Sample(int x, int y)
        {
            int o = ((y * image.Width) + x) * image.BytesPerPixel;
            return (px[o], px[o + 1], px[o + 2]);
        }

        // The image occupies the right half of the 128x64 page; its left half is unmasked.
        Assert.True(Sample(16, 32).G > 150, $"left of the image must stay green (got {Sample(16, 32)})");
        Assert.True(Sample(80, 32).R > 150, $"the unmasked half must show the JPEG (got {Sample(80, 32)})");
        Assert.True(Sample(112, 32).G > 150, $"the masked half must show through to the green (got {Sample(112, 32)})");
    }

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

    /// <summary>
    /// A DCTDecode image must honour its /Decode array. Adobe CMYK JPEGs store the complements of the
    /// colourants and producers write <c>/Decode [1 0 1 0 1 0 1 0]</c> to turn them back; the JPEG branch
    /// of the image decoder returned before the array was ever read, so a book cover came out sepia
    /// where every viewer shows blue-grey - 86% of its pixels wrong against MuPDF.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void CmykJpegWithInvertingDecode_AppliesTheDecodeArray(IGraphicsFactory factory)
    {
        byte[] jpeg = Convert.FromBase64String(AdobeCmykJpegBase64);

        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateDctDecodeImagePdf(
            jpeg, 16, 16, "/DeviceCMYK", "[1 0 1 0 1 0 1 0]"));
        using IGraphicsContext context = factory.CreateContext(16, 16);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        // The file stores the complements of ink (230, 50, 25, 0), so the decoded image must show that.
        PosoColor expected = PosoColor.FromCmyk(230 / 255f, 50 / 255f, 25 / 255f, 0f);

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);
        int offset = ((8 * image.Width) + 8) * image.BytesPerPixel;
        (int B, int G, int R) actual = (pixels[offset], pixels[offset + 1], pixels[offset + 2]);

        Assert.True(Math.Abs(actual.R - expected.R) < 12 && Math.Abs(actual.G - expected.G) < 12 && Math.Abs(actual.B - expected.B) < 12,
            $"expected the decoded cyan (got {actual}, want {expected})");
    }

    /// <summary>A 16x16 solid CMYK (230,50,25,0) JPEG carrying an Adobe APP14 marker with transform 0.</summary>
    private const string AdobeCmykJpegBase64 =
        "/9j/7gAOQWRvYmUAZAAAAAAA/9sAQwACAQEBAQECAQEBAgICAgIEAwICAgIFBAQDBAYFBgYGBQYGBgcJCAYHCQcGBggLCAkK"
        + "CgoKCgYICwwLCgwJCgoK/8AAFAgAEAAQBEMRAE0RAFkRAEsRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//E"
        + "ALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2"
        + "Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrC"
        + "w8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAA4EQwBNAFkASwAAPwD8Y6/TSv2Yr9/KKKKKKKKKKKKK"
        + "/9k=";

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
        // MuPDF paints full key as (35,31,32), not pure black: its DeviceCMYK conversion goes through a
        // coated-paper profile. What this index has to prove is that the table was read at all.
        Assert.True(lumaAt(1) < 90, $"index 1 is full key (near black), got luma {lumaAt(1)}");

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
