using System;
using Poso.Graphics;
using Poso.Pdf.Filters.Codecs;
using Poso.Skia;
using SkiaSharp;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Regression for the IDCT "column AC all zero" shortcut in <see cref="JpegDecoder"/>: a vertical-stripe
/// image has identical rows, so every coefficient with vertical frequency &gt; 0 is zero and every block
/// column takes the shortcut. It must emit the column's first coefficient down the whole column — an
/// earlier version duplicated it into the top half of the block and zeroed the bottom half (the photo
/// vectors never hit this path, so it shipped green).
/// </summary>
public class JpegDecoderTests
{
    /// <summary>
    /// A sequential scan is supposed to declare Ss=0, Se=63, and encoders write 0 in Se as well - pdf.js
    /// issue12841_reduced.pdf does, and the whole photograph was lost (82.9% of the page against PDFium)
    /// because the decoder refused the scan and PeachDrawing, which has no JPEG codec of its own, had
    /// nothing to fall back on. Se only means something to a progressive scan, and the SOF marker has
    /// already refused those, so a zero there says nothing about the data.
    /// </summary>
    [Fact]
    public void SequentialScanWithZeroSe_StillDecodes()
    {
        const int W = 16, H = 16;
        using var bitmap = new SKBitmap(W, H, SKColorType.Gray8, SKAlphaType.Opaque);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 16), (byte)(y * 16), 128));
            }
        }

        using SKImage skiaImage = SKImage.FromBitmap(bitmap);
        using SKData encoded = skiaImage.Encode(SKEncodedImageFormat.Jpeg, 90);
        byte[] jpeg = encoded.ToArray();

        byte[] patched = (byte[])jpeg.Clone();
        int sos = FindSos(patched);
        int scanComponents = patched[sos + 4];
        int seOffset = sos + 6 + (2 * scanComponents);
        Assert.Equal(63, patched[seOffset]);
        patched[seOffset] = 0;

        JpegDecoder.Result? expected = JpegDecoder.Decode(jpeg);
        JpegDecoder.Result? actual = JpegDecoder.Decode(patched);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Width, actual!.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Planes[0], actual.Planes[0]);
    }

    /// <summary>The offset of the SOS marker, walking the segments rather than searching for a byte pair.</summary>
    private static int FindSos(byte[] jpeg)
    {
        int pos = 2; // past SOI
        while (pos + 3 < jpeg.Length)
        {
            Assert.Equal(0xFF, jpeg[pos]);
            byte marker = jpeg[pos + 1];
            if (marker == 0xDA)
            {
                return pos;
            }

            int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            pos += 2 + length;
        }

        throw new InvalidOperationException("no SOS marker");
    }

    [Fact]
    public void VerticalStripes_AllColumnShortcut_MatchesSkia()
    {
        const int W = 16, H = 16;
        using var bitmap = new SKBitmap(W, H, SKColorType.Gray8, SKAlphaType.Opaque);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                bitmap.SetPixel(x, y, x % 2 == 0 ? SKColors.Black : SKColors.White);
            }
        }

        using SKImage skiaImage = SKImage.FromBitmap(bitmap);
        using SKData encoded = skiaImage.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] jpeg = encoded.ToArray();

        JpegDecoder.Result? result = JpegDecoder.Decode(jpeg);
        Assert.NotNull(result);

        // Identical input rows must produce identical output rows (the transform is exact for this input).
        byte[] plane = result!.Planes[0];
        for (int y = 1; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Assert.True(plane[(y * W) + x] == plane[x], $"row {y} differs from row 0 at x={x}");
            }
        }

        // And the values must match the reference codec (Skia = libjpeg-turbo).
        using IImage reference = SkiaGraphicsFactory.Default.DecodeImage(jpeg)!;
        byte[] bgra = new byte[W * H * 4];
        reference.CopyPixels(bgra);
        for (int i = 0; i < W * H; i++)
        {
            Assert.True(Math.Abs(plane[i] - bgra[i * 4]) <= 8,
                $"pixel {i}: ours {plane[i]} vs skia {bgra[i * 4]}");
        }
    }
}
