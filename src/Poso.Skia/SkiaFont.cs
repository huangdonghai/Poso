using Poso.Graphics;
using SkiaSharp;

namespace Poso.Skia;

/// <summary>Skia implementation of <see cref="IFont"/>: embedded fonts load via SKTypeface.FromData; the standard 14 fonts fall back to system font families.</summary>
public sealed class SkiaFont : IFont
{
    private readonly SKTypeface _typeface;

    // Unit font size (1f): advance widths are returned directly in em units
    private readonly SKFont _font;

    private SkiaFont(SKTypeface typeface)
    {
        _typeface = typeface;
        _font = new SKFont(typeface, 1f);
    }

    public static SkiaFont? Create(ReadOnlySpan<byte> fontData, string baseFontName)
    {
        if (!fontData.IsEmpty)
        {
            using var skData = SKData.CreateCopy(fontData);
            if (SKTypeface.FromData(skData) is { } embedded)
            {
                return new SkiaFont(embedded);
            }
        }

        return CreateFallback(baseFontName);
    }

    private static SkiaFont? CreateFallback(string baseFontName)
    {
        string family = StandardFonts.MapFamily(baseFontName);
        SKFontStyle style = StandardFonts.MapStyle(baseFontName);

        if (SKTypeface.FromFamilyName(family, style) is { } styled)
        {
            return new SkiaFont(styled);
        }

        if (SKTypeface.FromFamilyName(family) is { } plain)
        {
            return new SkiaFont(plain);
        }

        return SKTypeface.Default is { } fallback ? new SkiaFont(fallback) : null;
    }

    public string Name => _typeface.FamilyName ?? "Unknown";

    public SKTypeface Typeface => _typeface;

    /// <summary>The underlying unit-size (1em) Skia font, for the batched glyph-run draw path.</summary>
    internal SKFont Font => _font;

    public ushort GetGlyphId(uint codePoint) => (ushort)_font.GetGlyph((int)codePoint);

    public float GetAdvanceWidth(ushort glyphId)
    {
        Span<ushort> glyphs = stackalloc ushort[1] { glyphId };
        return _font.MeasureText(glyphs, null);
    }

    /// <summary>
    /// Gets the glyph outline in PDF text space (em units, origin at the baseline, y-up like
    /// <see cref="TryGetGlyphOutline"/>). Skia hands out outlines in its own y-down drawing space,
    /// so y is negated here - the page flip lives in the context base matrix. The caller must dispose it.
    /// </summary>
    public SKPath? GetGlyphOutlinePath(ushort glyphId)
    {
        SKPath? outline = _font.GetGlyphPath(glyphId);

        // y-down to y-up: negate y; x and the advance direction stay as they are
        outline?.Transform(new SKMatrix(1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 1f));

        return outline;
    }

    public bool TryGetGlyphOutline(ushort glyphId, IPathBuilder path)
    {
        using SKPath? outline = _font.GetGlyphPath(glyphId);
        if (outline is null || outline.IsEmpty)
        {
            return false;
        }

        // Skia returns the outline in its y-down drawing space; negate y to get the y-up
        // font space documented by IFont, and elevate quadratic curves to cubics
        float currentX = 0;
        float currentY = 0;
        bool hasCurrent = false;

        using (SKPath.Iterator iterator = outline.CreateIterator(false))
        {
            Span<SKPoint> points = stackalloc SKPoint[4];
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        currentX = points[0].X;
                        currentY = -points[0].Y;
                        hasCurrent = true;
                        path.MoveTo(currentX, currentY);
                        break;

                    case SKPathVerb.Line:
                        currentX = points[0].X;
                        currentY = -points[0].Y;
                        hasCurrent = true;
                        path.LineTo(currentX, currentY);
                        break;

                    case SKPathVerb.Quad:
                    {
                        float x1 = points[0].X;
                        float y1 = -points[0].Y;
                        float x2 = points[1].X;
                        float y2 = -points[1].Y;
                        if (hasCurrent)
                        {
                            float c1x = currentX + (2f / 3f) * (x1 - currentX);
                            float c1y = currentY + (2f / 3f) * (y1 - currentY);
                            float c2x = x2 + (2f / 3f) * (x1 - x2);
                            float c2y = y2 + (2f / 3f) * (y1 - y2);
                            path.CubicTo(c1x, c1y, c2x, c2y, x2, y2);
                        }

                        currentX = x2;
                        currentY = y2;
                        hasCurrent = true;
                        break;
                    }

                    case SKPathVerb.Conic:
                    {
                        // Weightless quadratic approximation (conics are rare in font outlines)
                        float x1 = points[0].X;
                        float y1 = -points[0].Y;
                        float x2 = points[1].X;
                        float y2 = -points[1].Y;
                        if (hasCurrent)
                        {
                            float c1x = currentX + (2f / 3f) * (x1 - currentX);
                            float c1y = currentY + (2f / 3f) * (y1 - currentY);
                            float c2x = x2 + (2f / 3f) * (x1 - x2);
                            float c2y = y2 + (2f / 3f) * (y1 - y2);
                            path.CubicTo(c1x, c1y, c2x, c2y, x2, y2);
                        }

                        currentX = x2;
                        currentY = y2;
                        hasCurrent = true;
                        break;
                    }

                    case SKPathVerb.Cubic:
                        currentX = points[2].X;
                        currentY = -points[2].Y;
                        hasCurrent = true;
                        path.CubicTo(
                            points[0].X, -points[0].Y,
                            points[1].X, -points[1].Y,
                            points[2].X, -points[2].Y);
                        break;

                    case SKPathVerb.Close:
                        path.Close();
                        break;
                }
            }
        }

        return true;
    }

    public void Dispose()
    {
        _font.Dispose();
        _typeface.Dispose();
    }
}
