using Poso.Graphics;
using SkiaSharp;

namespace Poso.Skia;

/// <summary>Skia implementation of <see cref="IImage"/>: wraps an SKBitmap (Bgra8888 premultiplied, matching the Avalonia bitmap format).</summary>
public sealed class SkiaImage : IImage
{
    private readonly SKBitmap _bitmap;

    public SkiaImage(SKBitmap bitmap)
    {
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));

        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            throw new NotSupportedException(
                $"SkiaImage requires Bgra8888 bitmaps (got {bitmap.ColorType}); convert before wrapping.");
        }
    }

    public SKBitmap Bitmap => _bitmap;

    public int Width => _bitmap.Width;

    public int Height => _bitmap.Height;

    public PixelFormat Format => PixelFormat.Bgra8888Premul;

    public int BytesPerPixel => 4;

    public unsafe void CopyPixels(Span<byte> destination)
    {
        int rowBytes = Width * BytesPerPixel;
        int required = rowBytes * Height;
        if (destination.Length < required)
        {
            throw new ArgumentException($"The destination buffer must hold at least {required} bytes", nameof(destination));
        }

        IntPtr pixels = _bitmap.GetPixels();
        if (_bitmap.RowBytes == rowBytes)
        {
            new ReadOnlySpan<byte>((void*)pixels, required).CopyTo(destination);
            return;
        }

        var source = new ReadOnlySpan<byte>((void*)pixels, _bitmap.RowBytes * Height);
        for (int y = 0; y < Height; y++)
        {
            source.Slice(y * _bitmap.RowBytes, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        }
    }

    public void Dispose() => _bitmap.Dispose();
}
