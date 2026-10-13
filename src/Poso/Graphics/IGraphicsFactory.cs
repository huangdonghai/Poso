namespace Poso.Graphics;

/// <summary>
/// Graphics backend factory: creates off-screen drawing contexts, decodes encoded images, and creates fonts.
/// Implementers (e.g. SkiaSharp) are assembled and injected by the application layer; the core library does not depend on any specific backend.
/// </summary>
public interface IGraphicsFactory
{
    /// <summary>Create an off-screen drawing context backed by a bitmap (y-down coordinate system).</summary>
    IGraphicsContext CreateContext(int width, int height);

    /// <summary>Decode encoded image data (JPEG/PNG, etc., used for the PDF DCTDecode filter). Returns null on failure.</summary>
    IImage? DecodeImage(ReadOnlySpan<byte> data);

    /// <summary>
    /// Create a font: prefer the embedded font program (skipped when <paramref name="fontData"/> is empty);
    /// otherwise fall back to a system font by base font name (e.g. Helvetica).
    /// </summary>
    IFont? CreateFont(ReadOnlySpan<byte> fontData, string baseFontName);

    /// <summary>Creates an image from tightly packed pixel data (stride = width * BytesPerPixel).</summary>
    IImage CreateImage(int width, int height, PixelFormat format, ReadOnlySpan<byte> pixels);
}
