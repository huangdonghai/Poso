using Poso.Graphics;
using SkiaSharp;

namespace Poso.Skia;

/// <summary>Skia implementation of <see cref="IGraphicsFactory"/>.</summary>
public sealed class SkiaGraphicsFactory : IGraphicsFactory
{
    static SkiaGraphicsFactory()
    {
        // Skia's default glyph (font) cache holds only 2048 glyphs. That thrashes on glyph-rich
        // scripts (CJK books have tens of thousands of distinct glyphs), forcing a ~100us re-raster
        // of each uncached glyph on every render. Raise the count and byte budgets so rasterised
        // glyphs persist across pages and zoom levels. These are LRU ceilings, not pre-allocated.
        SKGraphics.SetFontCacheCountLimit(100_000);
        SKGraphics.SetFontCacheLimit(64L * 1024 * 1024);
    }

    /// <summary>Shared default instance (stateless, thread-safe).</summary>
    public static SkiaGraphicsFactory Default { get; } = new();

    public IGraphicsContext CreateContext(int width, int height) => new SkiaGraphicsContext(width, height);

    public IImage? DecodeImage(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        using var skData = SKData.CreateCopy(data);
        SKBitmap? decoded = SKBitmap.Decode(skData);
        if (decoded is null)
        {
            return null;
        }

        // The codec may return Gray8 (grayscale JPEG) or Rgba8888; normalize to the
        // Bgra8888 format every consumer of IImage expects.
        if (decoded.ColorType != SKColorType.Bgra8888)
        {
            SKBitmap? converted = decoded.Copy(SKColorType.Bgra8888);
            decoded.Dispose();

            if (converted is null)
            {
                return null;
            }

            decoded = converted;
        }

        return new SkiaImage(decoded);
    }

    public IFont? CreateFont(ReadOnlySpan<byte> fontData, string baseFontName) =>
        SkiaFont.Create(fontData, baseFontName);

    public IImage CreateImage(int width, int height, PixelFormat format, ReadOnlySpan<byte> pixels)
    {
        if (format != PixelFormat.Bgra8888Premul)
        {
            throw new NotSupportedException($"The Skia backend only supports {PixelFormat.Bgra8888Premul} images for now");
        }

        int rowBytes = width * 4;
        int required = rowBytes * height;
        if (pixels.Length < required)
        {
            throw new ArgumentException($"The pixel buffer must hold at least {required} bytes", nameof(pixels));
        }

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        byte[] source = pixels.ToArray();
        IntPtr destination = bitmap.GetPixels();
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(source, y * rowBytes, destination + (y * bitmap.RowBytes), rowBytes);
        }

        return new SkiaImage(bitmap);
    }
}
