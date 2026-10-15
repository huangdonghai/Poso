using System.Text;
using Poso.Graphics;

namespace Poso.PeachDrawing;

/// <summary>
/// PeachDrawing implementation of <see cref="IFont"/>: embedded font programs load through
/// <c>FontSet.AddData</c>; the standard-14 names fall back to system families. Glyph outlines come from
/// <c>Typeface.TryGetOutline</c> in design units (y-up, baseline origin - already the <see cref="IFont"/>
/// orientation) and are normalised to em units by dividing by units-per-em.
/// </summary>
public sealed class PeachFont : IFont
{
    // One FontSet serves the whole process; PeachDrawing's font database is not documented as
    // thread-safe, and the render pipeline runs several pages concurrently, so every access is serialised.
    internal static readonly PeachText.FontSet SharedFontSet = new();
    internal static readonly object FontSetLock = new();

    private readonly PeachText.Typeface _typeface;
    private readonly int _unitsPerEm;

    private PeachFont(PeachText.Typeface typeface, int unitsPerEm)
    {
        _typeface = typeface;
        _unitsPerEm = unitsPerEm;
    }

    /// <summary>
    /// Probes the lazily-initialized metrics and wraps the typeface. PeachDrawing loads font tables on
    /// first access and some malformed embedded programs throw (NRE inside its OpenType descriptor);
    /// such a font must fail here, at load, so the caller can fall back instead of crashing mid-render.
    /// </summary>
    private static PeachFont? From(PeachText.Typeface typeface)
    {
        try
        {
            int unitsPerEm = typeface.Metrics.UnitsPerEm;
            return unitsPerEm > 0 ? new PeachFont(typeface, unitsPerEm) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    public static PeachFont? Create(ReadOnlySpan<byte> fontData, string baseFontName)
    {
        if (!fontData.IsEmpty)
        {
            PeachFont? embedded = LoadEmbedded(fontData);
            if (embedded is not null)
            {
                return embedded;
            }
        }

        return CreateFallback(baseFontName);
    }

    private static PeachFont? LoadEmbedded(ReadOnlySpan<byte> fontData)
    {
        // Copy: FontSet keeps the memory alive and the caller's span may be a temporary.
        byte[] data = fontData.ToArray();
        string familyName = "poso-embedded-" + Guid.NewGuid().ToString("n");
        try
        {
            lock (FontSetLock)
            {
                SharedFontSet.AddData(
                    data,
                    new PeachText.AddOptions { FamilyName = familyName });
                if (!SharedFontSet.TryFindFamily(familyName, out PeachText.TypefaceFamily? family) || family is null)
                {
                    return null;
                }

                if (!family.TryMatch(default, out PeachText.TypefaceMatch match))
                {
                    return null;
                }

                return From(match.Typeface);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A malformed embedded program (or one PeachDrawing cannot build a descriptor for):
            // the caller falls back to the name-based system lookup rather than losing the page.
            return null;
        }
    }

    private static PeachFont? CreateFallback(string baseFontName)
    {
        string family = PeachStandardFonts.MapFamily(baseFontName);
        PeachText.TypefaceQuery query = PeachStandardFonts.MapQuery(baseFontName);

        lock (FontSetLock)
        {
            if (TryMatchFamily(family, query) is { } matched)
            {
                return matched;
            }

            // Broaden progressively: default style, then a generic sans, then anything the set can name.
            if (TryMatchFamily(family, default) is { } plain)
            {
                return plain;
            }

            if (TryMatchFamily("Arial", default) is { } arial)
            {
                return arial;
            }

            foreach (string name in PeachText.FontSet.InstalledFamilyNames)
            {
                if (TryMatchFamily(name, default) is { } first)
                {
                    return first;
                }
            }
        }

        return null;
    }

    private static PeachFont? TryMatchFamily(
        string family,
        in PeachText.TypefaceQuery query)
    {
        if (SharedFontSet.TryFindFamily(family, out PeachText.TypefaceFamily? found) && found is not null
            && found.TryMatch(query, out PeachText.TypefaceMatch match))
        {
            return From(match.Typeface);
        }

        return null;
    }

    public string Name
    {
        get
        {
            try
            {
                return _typeface.FamilyName;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                return "Unknown";
            }
        }
    }

    /// <summary>The PeachDrawing typeface, for the drawing context's glyph runs.</summary>
    internal PeachText.Typeface Typeface => _typeface;

    /// <summary>Units per em, probed at load; always positive.</summary>
    internal int UnitsPerEm => _unitsPerEm;

    public ushort GetGlyphId(uint codePoint)
    {
        if (!Rune.IsValid(codePoint))
        {
            return 0;
        }

        try
        {
            return _typeface.TryMapRune(new Rune((int)codePoint), out ushort glyph) ? glyph : (ushort)0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return 0;
        }
    }

    public float GetAdvanceWidth(ushort glyphId)
    {
        try
        {
            return _typeface.GetAdvance(glyphId) / (float)_unitsPerEm;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return 0f;
        }
    }

    public bool TryGetGlyphOutline(ushort glyphId, IPathBuilder path)
    {
        if (!TryGetOutline(glyphId, out PeachOutlines.GlyphOutline? outline) || outline is null || outline.IsEmpty)
        {
            return false;
        }

        // Outlines arrive in design units, y-up with the baseline at the origin: the IFont orientation
        // already - only the units-per-em normalisation is applied.
        float scale = 1f / _unitsPerEm;
        foreach (PeachOutlines.OutlineContour contour in outline.Contours)
        {
            path.MoveTo((float)contour.Start.X * scale, (float)contour.Start.Y * scale);
            foreach (PeachOutlines.OutlineSegment segment in contour.Segments)
            {
                if (segment.IsCubic)
                {
                    path.CubicTo(
                        (float)segment.Control1.X * scale, (float)segment.Control1.Y * scale,
                        (float)segment.Control2.X * scale, (float)segment.Control2.Y * scale,
                        (float)segment.End.X * scale, (float)segment.End.Y * scale);
                }
                else
                {
                    path.LineTo((float)segment.End.X * scale, (float)segment.End.Y * scale);
                }
            }

            path.Close();
        }

        return true;
    }

    /// <summary>
    /// Outline lookup with a hard guard: a font that passes the load-time metrics probe can still fail
    /// lazily on individual malformed glyphs, and a mid-page throw must degrade to "no ink for this
    /// glyph" rather than lose the whole page.
    /// </summary>
    internal bool TryGetOutline(ushort glyphId, out PeachOutlines.GlyphOutline? outline)
    {
        try
        {
            return _typeface.TryGetOutline(glyphId, out outline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            outline = null;
            return false;
        }
    }

    public void Dispose()
    {
        // Typeface instances live in the shared FontSet for the process lifetime (PDF documents reuse
        // fonts across pages and zoom levels); there is nothing per-instance to release.
    }
}
