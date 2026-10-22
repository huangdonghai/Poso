using Poso.Graphics;
using SkiaSharp;

namespace Poso.Skia;

/// <summary>Skia implementation of <see cref="IFont"/>: embedded fonts load via SKTypeface.FromData; the standard 14 fonts fall back to system font families.</summary>
public sealed class SkiaFont : IFont
{
    private readonly SKTypeface _typeface;

    // Parsed cmap for the formats Skia's own typeface cannot map (0 and 2). pdf.js bug1200096.pdf's big
    // Times subset carries a (3,1) format 2 table plus a (1,0) format 0 table; SKTypeface.GetGlyph returned
    // 0 for every code, so the text silently painted nothing.
    private readonly Dictionary<int, ushort>? _extraCmap;

    // Whether the program actually carries a /cmap table: a simple font addressed by character cannot be
    // drawn from a face without one, so the caller needs to know (pdf.js TrueType_without_cmap.pdf).
    private readonly bool _hasCmapTable;

    // Unit font size (1f): advance widths are returned directly in em units
    private readonly SKFont _font;

    private SkiaFont(SKTypeface typeface, bool usedFallback, Dictionary<int, ushort>? extraCmap = null, bool hasCmapTable = true)
    {
        _typeface = typeface;
        _font = new SKFont(typeface, 1f);

        // No hinting: a PDF viewer must not snap glyphs to the pixel grid (ISO 32000-1 9.7.3.3), and
        // the outline this font hands out is the one the rasteriser would have hinted.
        _font.Hinting = SKFontHinting.None;
        UsedFallback = usedFallback;
        _extraCmap = extraCmap;
        _hasCmapTable = hasCmapTable;
    }

    public static SkiaFont? Create(ReadOnlySpan<byte> fontData, string baseFontName)
    {
        if (!fontData.IsEmpty)
        {
            using var skData = SKData.CreateCopy(fontData);
            if (SKTypeface.FromData(skData) is { } embedded)
            {
                // Only reject a face with no glyphs at all. A CID/Identity-H font legitimately has no cmap
                // (codes address GIDs directly), so the cmap table must not be a requirement; when one is
                // present but Skia cannot read it, ParseCmap supplies the missing mappings instead.
                if (embedded.GlyphCount > 0)
                {
                    return new SkiaFont(embedded, usedFallback: false, ParseCmap(fontData), hasCmapTable: HasCmapTable(fontData));
                }

                embedded.Dispose();
                return CreateFallback(baseFontName, usedFallback: true);
            }

            return CreateFallback(baseFontName, usedFallback: true);
        }

        return CreateFallback(baseFontName, usedFallback: false);
    }

    private static SkiaFont? CreateFallback(string baseFontName, bool usedFallback)
    {
        string family = StandardFonts.MapFamily(baseFontName);
        SKFontStyle style = StandardFonts.MapStyle(baseFontName);

        if (SKTypeface.FromFamilyName(family, style) is { } styled)
        {
            return new SkiaFont(styled, usedFallback);
        }

        if (SKTypeface.FromFamilyName(family) is { } plain)
        {
            return new SkiaFont(plain, usedFallback);
        }

        return SKTypeface.Default is { } fallback ? new SkiaFont(fallback, usedFallback) : null;
    }

    public string Name => _typeface.FamilyName ?? "Unknown";

    public bool UsedFallback { get; }

    public SKTypeface Typeface => _typeface;

    /// <summary>The underlying unit-size (1em) Skia font, for the batched glyph-run draw path.</summary>
    internal SKFont Font => _font;

    public ushort GetGlyphId(uint codePoint)
    {
        ushort glyphId = (ushort)_font.GetGlyph((int)codePoint);
        if (glyphId == 0 && _extraCmap is not null && _extraCmap.TryGetValue((int)codePoint, out ushort mapped))
        {
            return mapped;
        }

        return glyphId;
    }

    /// <inheritdoc />
    public bool MapsCodePointsToGlyphs => _hasCmapTable;

    /// <summary>Whether the program's table directory lists a /cmap table at all.</summary>
    private static bool HasCmapTable(ReadOnlySpan<byte> font)
    {
        if (font.Length < 12)
        {
            return false;
        }

        int numTables = ReadU16(font, 4);
        if (font.Length < 12 + (numTables * 16))
        {
            return false;
        }

        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + (i * 16);
            if (font[record] == 'c' && font[record + 1] == 'm' && font[record + 2] == 'a' && font[record + 3] == 'p')
            {
                return true;
            }
        }

        return false;
    }

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
        if (outline is null)
        {
            return null;
        }

        // y-down to y-up: negate y; x and the advance direction stay as they are
        outline.Transform(new SKMatrix(1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, 1f));
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

    /// <summary>
    /// Reads the cmap sub-tables SkiaSharp's typeface cannot use: format 0 (a 256-entry byte table) and
    /// format 2 (high-byte mapping). Returns null when the program has neither.
    /// </summary>
    private static Dictionary<int, ushort>? ParseCmap(ReadOnlySpan<byte> font)
    {
        if (font.Length < 12)
        {
            return null;
        }

        int numTables = ReadU16(font, 4);
        if (font.Length < 12 + (numTables * 16))
        {
            return null;
        }

        int cmap = -1;
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + (i * 16);
            if (font[record] == 'c' && font[record + 1] == 'm' && font[record + 2] == 'a' && font[record + 3] == 'p')
            {
                cmap = ReadU32(font, record + 8);
                break;
            }
        }

        if (cmap < 0 || cmap + 4 > font.Length)
        {
            return null;
        }

        int subTables = ReadU16(font, cmap + 2);
        if (cmap + 4 + (subTables * 8) > font.Length)
        {
            return null;
        }

        Dictionary<int, ushort>? map = null;
        for (int i = 0; i < subTables; i++)
        {
            int record = cmap + 4 + (i * 8);
            int platform = ReadU16(font, record);
            int encoding = ReadU16(font, record + 2);
            int sub = cmap + ReadU32(font, record + 4);
            if (sub + 2 > font.Length)
            {
                continue;
            }

            switch (ReadU16(font, sub))
            {
                case 0 when sub + 6 + 256 <= font.Length:
                    map ??= new Dictionary<int, ushort>(256);
                    for (int code = 0; code < 256; code++)
                    {
                        ushort glyph = font[sub + 6 + code];
                        if (glyph != 0 && !map.ContainsKey(code))
                        {
                            map[code] = glyph;
                        }
                    }

                    break;

                case 2 when (platform == 3 && encoding == 1) || platform == 0:
                    map ??= new Dictionary<int, ushort>();
                    ParseCmapFormat2(font, sub, map);
                    break;
            }
        }

        return map;
    }

    private static void ParseCmapFormat2(ReadOnlySpan<byte> font, int sub, Dictionary<int, ushort> map)
    {
        int keys = sub + 6;
        int subHeaders = keys + 512;
        for (int high = 0; high < 256; high++)
        {
            int key = ReadU16(font, keys + (high * 2)) / 8;
            int header = subHeaders + (key * 8);
            if (header + 8 > font.Length)
            {
                continue;
            }

            int firstCode = ReadU16(font, header);
            int entryCount = ReadU16(font, header + 2);
            short delta = (short)ReadU16(font, header + 4);
            int rangeOffset = ReadU16(font, header + 6);
            for (int low = firstCode; low < firstCode + entryCount && low < 256; low++)
            {
                int address = header + 6 + rangeOffset + ((low - firstCode) * 2);
                if (address + 2 > font.Length)
                {
                    continue;
                }

                int glyph = ReadU16(font, address);
                if (glyph == 0)
                {
                    continue;
                }

                glyph = (glyph + delta) & 0xffff;
                int code = (high << 8) | low;
                if (glyph != 0 && !map.ContainsKey(code))
                {
                    map[code] = (ushort)glyph;
                }
            }
        }
    }

    private static int ReadU16(ReadOnlySpan<byte> data, int offset) =>
        offset + 1 < data.Length ? (data[offset] << 8) | data[offset + 1] : 0;

    private static int ReadU32(ReadOnlySpan<byte> data, int offset) =>
        offset + 3 < data.Length
            ? (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]
            : 0;
}
