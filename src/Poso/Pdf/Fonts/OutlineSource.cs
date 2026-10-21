using Poso.Graphics;

namespace Poso.Pdf.Fonts;

/// <summary>
/// A glyph outline source: the embedded program itself, addressed by glyph id or by glyph name.
/// </summary>
/// <remarks>
/// The back end is asked first and only falls back to this when it could not load the program, so the
/// two never disagree about which face a page is drawn in. What this exists for is the case the back end
/// cannot serve: a program it rejects comes back as a system substitute, and the substitute draws
/// different letterforms than the PDF asked for.
/// </remarks>
internal abstract class OutlineSource
{
    public abstract int GlyphCount { get; }

    /// <summary>Appends the glyph's contours to <paramref name="path"/>, in em units with y up.</summary>
    public abstract bool TryGetOutline(int glyphId, IPathBuilder path);

    /// <summary>Resolves a PostScript glyph name to the program's glyph id.</summary>
    public virtual bool TryGetGlyphIdForName(string name, out ushort glyphId)
    {
        glyphId = 0;
        return false;
    }
}
