using Poso.Graphics;

namespace Poso.PeachDrawing;

/// <summary>
/// Image abstraction bridging Poso's <see cref="IImage"/> (BGRA, either straight or premultiplied) and
/// PeachDrawing's <see cref="PeachCore.Image"/> (premultiplied RGBA via <see cref="GetPixels"/>).
/// The canonical storage is the BGRA buffer the core layer produces; the RGBA form Peach's raster canvas
/// samples is converted lazily and cached.
/// </summary>
internal sealed class PeachImage : IImage
{
    private readonly byte[] _bgra;
    private readonly bool _premultiplied;
    private PeachCore.PixelBuffer? _rgbaCache;
    private CanvasImage? _canvasImage;

    /// <summary>Wraps tightly packed BGRA pixels (stride = width * 4).</summary>
    public PeachImage(int width, int height, byte[] bgra, bool premultiplied)
    {
        if (bgra is null || bgra.Length < width * height * 4)
        {
            throw new ArgumentException("The pixel buffer must hold width * height * 4 bytes", nameof(bgra));
        }

        Width = width;
        Height = height;
        _bgra = bgra;
        _premultiplied = premultiplied;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format => _premultiplied ? PixelFormat.Bgra8888Premul : PixelFormat.Bgra8888;

    public int BytesPerPixel => 4;

    public void CopyPixels(Span<byte> destination) => _bgra.AsSpan(0, Width * Height * 4).CopyTo(destination);

    /// <summary>
    /// The image as PeachDrawing's <see cref="PeachCore.Image"/> for canvas draw calls. Backed by the
    /// same pixels, converted to premultiplied RGBA8 on first use.
    /// </summary>
    public PeachCore.Image AsCanvasImage() => _canvasImage ??= new CanvasImage(this);

    /// <summary>Swaps BGRA bytes into premultiplied RGBA (the Peach surface layout).</summary>
    private PeachCore.PixelBuffer GetPremulRgba()
    {
        if (_rgbaCache is { } cached)
        {
            return cached;
        }

        int count = Width * Height;
        var rgba = new byte[count * 4];
        if (_premultiplied)
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                rgba[o] = _bgra[o + 2];
                rgba[o + 1] = _bgra[o + 1];
                rgba[o + 2] = _bgra[o];
                rgba[o + 3] = _bgra[o + 3];
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                byte a = _bgra[o + 3];
                rgba[o] = (byte)((_bgra[o + 2] * a + 255) / 256);
                rgba[o + 1] = (byte)((_bgra[o + 1] * a + 255) / 256);
                rgba[o + 2] = (byte)((_bgra[o] * a + 255) / 256);
                rgba[o + 3] = a;
            }
        }

        var buffer = new PeachCore.PixelBuffer(Width, Height, rgba);
        _rgbaCache = buffer;
        return buffer;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Thin PeachDrawing adapter over a <see cref="PeachImage"/>: reports natural size in pixels (scale 1)
    /// and hands the raster canvas its premultiplied RGBA form.
    /// </summary>
    private sealed class CanvasImage : PeachCore.Image
    {
        private readonly PeachImage _owner;

        public CanvasImage(PeachImage owner)
        {
            _owner = owner;
        }

        public override double Width => _owner.Width;

        public override double Height => _owner.Height;

        public override bool Interpolate { get; set; } = true;

        public override PeachCore.PixelBuffer? GetPixels() => _owner.GetPremulRgba();

        public override void Dispose()
        {
        }
    }
}
