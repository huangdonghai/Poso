namespace Poso.Graphics;

/// <summary>
/// Bitmap image. Pixels are tightly packed (stride = Width * BytesPerPixel) and are copied
/// to the caller's buffer (e.g. Avalonia's WriteableBitmap) via <see cref="CopyPixels"/>.
/// </summary>
public interface IImage : IDisposable
{
    int Width { get; }

    int Height { get; }

    PixelFormat Format { get; }

    int BytesPerPixel { get; }

    /// <summary>Copy pixel data; the destination buffer must hold at least Width * Height * BytesPerPixel bytes.</summary>
    void CopyPixels(Span<byte> destination);
}
