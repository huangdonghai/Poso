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

    /// <summary>
    /// Convert CMYK (components 0-1) to RGB using the pdf.js DeviceCmykCS polynomial fit to the US Web
    /// Coated (SWOP) profile. This tracks a real ICC OutputIntent (e.g. ISO Coated v2 300%) to within a
    /// few units per channel for typical colours, and is far more faithful than the naive 1-(c+k) model,
    /// which renders light CMYK fills too dark and over-saturated. Kept identical to the CMYK JPEG path.
    /// </summary>
    public static PosoColor FromCmyk(float c, float m, float y, float k)
    {
        float rr = 255f +
            c * (-4.387332384609988f * c + 54.48615194189176f * m + 18.82290502165302f * y + 212.25662451639585f * k - 285.2331026137004f) +
            m * (1.7149763477362134f * m - 5.6096736904047315f * y - 17.873870861415444f * k - 5.497006427196366f) +
            y * (-2.5217340131683033f * y - 21.248923337353073f * k + 17.5119270841813f) +
            k * (-21.86122147463605f * k - 189.48180835922747f);

        float gg = 255f +
            c * (8.841041422036149f * c + 60.118027045597366f * m + 6.871425592049007f * y + 31.159100130055922f * k - 79.2970844816548f) +
            m * (-15.310361306967817f * m + 17.575251261109482f * y + 131.35250912493976f * k - 190.9453302588951f) +
            y * (4.444339102852739f * y + 9.8632861493405f * k - 24.86741582555878f) +
            k * (-20.737325471181034f * k - 187.80453709719578f);

        float bb = 255f +
            c * (0.8842522430003296f * c + 8.078677503112928f * m + 30.89978309703729f * y - 0.23883238689178934f * k - 14.183576799673286f) +
            m * (10.49593273432072f * m + 63.02378494754052f * y + 50.606957656360734f * k - 112.23884253719248f) +
            y * (0.03296041114873217f * y + 115.60384449646641f * k - 193.58209356861505f) +
            k * (-22.33816807309886f * k - 180.12613974708367f);

        return new PosoColor(Clamp255(rr), Clamp255(gg), Clamp255(bb));
    }

    private static byte Clamp255(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

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

    /// <summary>
    /// A dash pattern needs pairs: an odd count is malformed, and handing it to a back end that asserts
    /// the pairing (Skia's <c>SKPathEffect.CreateDash</c> throws) loses the whole page.
    /// </summary>
    public bool IsDashed => DashArray is { } dash && dash.Length > 0 && (dash.Length % 2) == 0;

    public static readonly StrokeStyle Default = new(1f);
}

/// <summary>Text rendering mode (PDF text state Tr operator).</summary>
public enum TextRenderMode
{
    Fill = 0,
    Stroke = 1,
    FillStroke = 2,

    /// <summary>Tr 3: the glyphs are neither painted nor clipped. PDF numbers this mode 3; the names
    /// below follow the same numbering, so the clip modes are 4, 5 and 6.</summary>
    Invisible = 3,
    FillClip = 4,
    StrokeClip = 5,
    FillStrokeClip = 6,
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
