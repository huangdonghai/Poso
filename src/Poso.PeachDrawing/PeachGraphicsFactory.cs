using Poso.Graphics;

namespace Poso.PeachDrawing;

/// <summary>
/// PeachDrawing implementation of <see cref="IGraphicsFactory"/>: the pure-managed software raster
/// back-end (no native dependencies). <see cref="Default"/> is the shared instance;
/// <see cref="CreateContext"/> allocates a fresh RGBA surface per page render.
/// </summary>
public sealed class PeachGraphicsFactory : IGraphicsFactory
{
    /// <summary>
    /// One shared <c>RasterRenderContext</c>: it owns the machine font registration (done once in its
    /// constructor) and the settings the canvases read back (anti-aliasing, hinting). Canvas creation
    /// itself only allocates, so sharing it across the bounded concurrent renders is safe.
    /// </summary>
    internal static readonly PeachRaster.RasterRenderContext RenderContext = CreateRenderContext();

    private static PeachRaster.RasterRenderContext CreateRenderContext()
    {
        var context = new PeachRaster.RasterRenderContext();
        // Match the Skia back-end's unhinted analytic outlines: PDF fidelity comparisons run against
        // MuPDF, which never TrueType-hints; the shared font-synthesis metrics would shift glyph shapes.
        context.TextHinting = PeachCore.TextHinting.None;
        return context;
    }

    /// <summary>Shared default instance (stateless, thread-safe).</summary>
    public static PeachGraphicsFactory Default { get; } = new();

    public IGraphicsContext CreateContext(int width, int height) => new PeachGraphicsContext(width, height);

    public IImage? DecodeImage(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data.ToArray());
            PeachCore.Image? image = RenderContext.ImageFromStream(stream);
            if (image is null)
            {
                return null;
            }

            using (image)
            {
                if (image.GetPixels() is not { } pixels)
                {
                    return null;
                }

                // Peach delivers premultiplied RGBA8; the Poso pipeline standardises on BGRA.
                ReadOnlySpan<byte> source = pixels.PremultipliedRgba.Span;
                int count = pixels.Width * pixels.Height;
                if (source.Length < count * 4)
                {
                    return null;
                }

                var bgra = new byte[count * 4];
                for (int i = 0; i < count; i++)
                {
                    int p = i * 4;
                    bgra[p] = source[p + 2];
                    bgra[p + 1] = source[p + 1];
                    bgra[p + 2] = source[p];
                    bgra[p + 3] = source[p + 3];
                }

                return new PeachImage(pixels.Width, pixels.Height, bgra, premultiplied: true);
            }
        }
        catch (Exception)
        {
            // A corrupt or unsupported stream must not fail the page: the caller reports and skips.
            return null;
        }
    }

    public IFont? CreateFont(ReadOnlySpan<byte> fontData, string baseFontName) =>
        PeachFont.Create(fontData, baseFontName);

    public IImage CreateImage(int width, int height, PixelFormat format, ReadOnlySpan<byte> pixels)
    {
        if (format != PixelFormat.Bgra8888Premul)
        {
            throw new NotSupportedException($"The PeachDrawing back-end only supports {PixelFormat.Bgra8888Premul} images for now");
        }

        int required = width * height * 4;
        if (pixels.Length < required)
        {
            throw new ArgumentException($"The pixel buffer must hold at least {required} bytes", nameof(pixels));
        }

        // Copy: the caller's span is a scratch buffer that gets reused between images.
        var buffer = new byte[required];
        pixels.Slice(0, required).CopyTo(buffer);
        return new PeachImage(width, height, buffer, premultiplied: true);
    }
}
