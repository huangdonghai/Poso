namespace Poso.Graphics;

/// <summary>RGBA color (components 0-255).</summary>
public readonly record struct PosoColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly PosoColor Transparent = new(0, 0, 0, 0);
    public static readonly PosoColor Black = new(0, 0, 0);
    public static readonly PosoColor White = new(255, 255, 255);
    public static readonly PosoColor Red = new(255, 0, 0);
    public static readonly PosoColor Green = new(0, 128, 0);
    public static readonly PosoColor Blue = new(0, 0, 255);

    public static PosoColor FromRgb(byte r, byte g, byte b) => new(r, g, b);

    public static PosoColor FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);

    /// <summary>Convert grayscale (0-1) to a color.</summary>
    public static PosoColor FromGray(float gray) => FromScaled(gray, gray, gray);

    public static PosoColor FromRgb(float r, float g, float b) => FromScaled(r, g, b);

    /// <summary>Convert CMYK (0-1) to RGB using a naive conversion formula.</summary>
    public static PosoColor FromCmyk(float c, float m, float y, float k)
    {
        float r = 1f - Math.Min(1f, c + k);
        float g = 1f - Math.Min(1f, m + k);
        float b = 1f - Math.Min(1f, y + k);
        return FromScaled(r, g, b);
    }

    public float Alpha => A / 255f;

    public PosoColor WithAlpha(byte alpha) => new(R, G, B, alpha);

    private static PosoColor FromScaled(float r, float g, float b) =>
        new(ToByte(r), ToByte(g), ToByte(b));

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>Line cap style.</summary>
public enum LineCap
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

/// <summary>Line join style.</summary>
public enum LineJoin
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

/// <summary>Stroke style.</summary>
public readonly record struct StrokeStyle
{
    public StrokeStyle(float width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter, float miterLimit = 10f, ReadOnlyMemory<float>? dashArray = null, float dashPhase = 0f)
    {
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = miterLimit;
        DashArray = dashArray;
        DashPhase = dashPhase;
    }

    public float Width { get; init; }

    public LineCap Cap { get; init; }

    public LineJoin Join { get; init; }

    public float MiterLimit { get; init; }

    /// <summary>Dash array (PDF user space units; values come in pairs); null or empty means a solid line.</summary>
    public ReadOnlyMemory<float>? DashArray { get; init; }

    public float DashPhase { get; init; }

    public bool IsDashed => DashArray is { } dash && dash.Length > 0;

    public static readonly StrokeStyle Default = new(1f);
}

/// <summary>Text rendering mode (PDF text state Tr operator).</summary>
public enum TextRenderMode
{
    Fill = 0,
    Stroke = 1,
    FillStroke = 2,
    Invisible = 3,
    FillClip = 4,
    StrokeClip = 5,
    FillStrokeClip = 6,
    Clip = 7,
}

/// <summary>
/// Positioned glyph. Coordinates are in text space (y-up, like PDF user space): the caller applies
/// the text matrix and the font size through the graphics state matrix, so the outline handed to
/// <see cref="IGraphicsContext.DrawGlyphs"/> sits on the baseline at y = 0.
/// </summary>
public readonly record struct GlyphPosition
{
    public GlyphPosition(ushort glyphId, float x, float y)
    {
        GlyphId = glyphId;
        X = x;
        Y = y;
    }

    public ushort GlyphId { get; }

    /// <summary>Glyph origin x in text space units (relative to the text matrix origin).</summary>
    public float X { get; }

    /// <summary>Glyph origin y in text space units; non-zero only for a vertical displacement (Tw/Tz style runs).</summary>
    public float Y { get; }
}

/// <summary>Pixel format.</summary>
public enum PixelFormat
{
    /// <summary>8-bit blue-green-red, premultiplied alpha (matches the default Avalonia/Skia bitmap format).</summary>
    Bgra8888Premul,

    /// <summary>8-bit blue-green-red, straight alpha.</summary>
    Bgra8888,

    /// <summary>8-bit red-green-blue, premultiplied alpha.</summary>
    Rgba8888Premul,

    /// <summary>8-bit grayscale.</summary>
    Gray8,
}
