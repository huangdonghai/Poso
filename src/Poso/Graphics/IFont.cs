namespace Poso.Graphics;

/// <summary>
/// Font abstraction. Glyph IDs and advance widths are in em units (1.0 = full em width);
/// the caller (content interpreter) scales them to user space by font size.
/// </summary>
public interface IFont : IDisposable
{
    /// <summary>Font name (base font name or embedded font name).</summary>
    string Name { get; }

    /// <summary>Unicode code point to glyph ID; returns 0 when not found.</summary>
    ushort GetGlyphId(uint codePoint);

    /// <summary>Glyph advance width (in em units).</summary>
    float GetAdvanceWidth(ushort glyphId);

    /// <summary>
    /// Try to get the glyph outline (in em units, y-up font space, origin at the baseline).
    /// Returns false when the backend does not support it (the caller falls back to text rendering).
    /// </summary>
    bool TryGetGlyphOutline(ushort glyphId, IPathBuilder path);
}
