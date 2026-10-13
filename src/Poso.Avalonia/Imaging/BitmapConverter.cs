using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PixelFormat = Avalonia.Platform.PixelFormat;
using Poso.Graphics;

namespace Poso.Avalonia.Imaging;

/// <summary>Converts core <see cref="IImage"/> bitmaps into Avalonia <see cref="WriteableBitmap"/>s.</summary>
public static class BitmapConverter
{
    /// <summary>
    /// Creates a WriteableBitmap from a core image. The source must be Bgra8888Premul
    /// (the format produced by the Skia backend); the copy is a zero-conversion blit.
    /// </summary>
    public static unsafe WriteableBitmap ToWriteableBitmap(IImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Format != Poso.Graphics.PixelFormat.Bgra8888Premul || image.BytesPerPixel != 4)
        {
            throw new NotSupportedException(
                $"Unsupported image format {image.Format}; only {Poso.Graphics.PixelFormat.Bgra8888Premul} is supported");
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using ILockedFramebuffer framebuffer = bitmap.Lock();
        int rowBytes = image.Width * 4;
        int required = rowBytes * image.Height;

        if (framebuffer.RowBytes == rowBytes)
        {
            Span<byte> destination = new((void*)framebuffer.Address, required);
            image.CopyPixels(destination);
        }
        else
        {
            byte[] source = new byte[required];
            image.CopyPixels(source);
            for (int y = 0; y < image.Height; y++)
            {
                Span<byte> destination = new((void*)(framebuffer.Address + (y * framebuffer.RowBytes)), rowBytes);
                source.AsSpan(y * rowBytes, rowBytes).CopyTo(destination);
            }
        }

        return bitmap;
    }
}
