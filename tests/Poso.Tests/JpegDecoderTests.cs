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
