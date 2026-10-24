using System.Globalization;
using System.Numerics;
using System.Text;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf.Content;
using Poso.Pdf.Fonts;

namespace Poso.Pdf;

/// <summary>A positioned run of text on a page (coordinates in page user space, y-up).</summary>
public readonly record struct TextSpan(RectF Bounds, string Text);

/// <summary>A line of extracted text with its bounding box.</summary>
public readonly record struct TextLine(RectF Bounds, string Text);

/// <summary>A search hit: the bounding box of the matched glyphs (page user space, y-up).</summary>
public readonly record struct TextHit(RectF Bounds);

/// <summary>
/// A text selection: the selected text and one box per line (vertical text: per column), in page
/// user space (y-up). Line breaks inside the range are part of <see cref="Text"/>; they have no box.
/// </summary>
public readonly record struct PdfTextSelection(string Text, IReadOnlyList<RectF> Bounds)
{
    public bool IsEmpty => Text.Length == 0;

    public static PdfTextSelection Empty { get; } = new(string.Empty, Array.Empty<RectF>());
}

/// <summary>
/// Extracted text of a page: the full text, per-line spans with positions, keyword search and
/// character-level selection. Coordinates are page user space (y-up), before the page's /Rotate.
/// </summary>
public sealed class PdfTextPage
{
    private readonly StringBuilder _text = new();
    private readonly List<RectF> _charRects = new();
    // Reading direction of the glyph each text unit came from (normalized user-space advance vector).
    // Selection uses it to decide whether a point falls before or after a glyph, which also keeps
    // vertical and rotated text working without assuming a horizontal writing mode.
    private readonly List<Vector2> _charDirections = new();
    private readonly List<TextLine> _lines = new();
    private int _lineStart;

    internal PdfTextPage()
    {
    }

    /// <summary>The full extracted text (lines separated by \n).</summary>
    public string Text => _text.ToString();

    /// <summary>Extracted lines with their bounding boxes.</summary>
    public IReadOnlyList<TextLine> Lines => _lines;

    /// <summary>Length of <see cref="Text"/> in UTF-16 units.</summary>
    public int Length => _text.Length;

    /// <summary>
    /// Maps a point in page user space to the nearest caret position (0..<see cref="Length"/>) in
    /// reading order. A caret sits between two characters; the returned index is one.
    /// </summary>
    public int GetCaretIndex(Vector2 point)
    {
        int best = -1;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < _charRects.Count; i++)
        {
            RectF rect = _charRects[i];
            if (rect.IsEmpty)
            {
                // Line break: no glyph to hit
                continue;
            }

            float dx = MathF.Max(MathF.Max(rect.Left - point.X, 0f), point.X - rect.Right);
            float dy = MathF.Max(MathF.Max(rect.Top - point.Y, 0f), point.Y - rect.Bottom);
            float distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        if (best < 0)
        {
            return 0;
        }

        RectF bounds = _charRects[best];
        var center = new Vector2(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
        bool after = Vector2.Dot(point - center, _charDirections[best]) >= 0f;
        int caret = after ? best + 1 : best;

        // Never let a caret split a surrogate pair: both halves share one glyph box, so snap out.
        if (caret > 0 && caret < _text.Length &&
            char.IsLowSurrogate(_text[caret]) && char.IsHighSurrogate(_text[caret - 1]))
        {
            caret = after ? caret + 1 : caret - 1;
        }

        return caret;
    }

    /// <summary>
    /// True when the point falls inside the box of some glyph, i.e. there is text there to select.
    /// Used to decide between a text cursor and the default one without extracting anything.
    /// </summary>
    public bool IsOverText(Vector2 point)
    {
        for (int i = 0; i < _charRects.Count; i++)
        {
            RectF rect = _charRects[i];
            if (!rect.IsEmpty && rect.Contains(point.X, point.Y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the text in the given UTF-16 range (clamped to the page's text).</summary>
    public string GetText(int start, int length)
    {
        int from = Math.Clamp(start, 0, _text.Length);
        int count = Math.Clamp(length, 0, _text.Length - from);
        return _text.ToString(from, count);
    }

    /// <summary>
    /// Returns the text and per-line boxes of the range between two caret positions. The range is
    /// normalized (start/end may be given in either order).
    /// </summary>
    public PdfTextSelection GetSelection(int start, int end)
    {
        int from = Math.Clamp(Math.Min(start, end), 0, _text.Length);
        int to = Math.Clamp(Math.Max(start, end), 0, _text.Length);
        if (from >= to)
        {
            return PdfTextSelection.Empty;
        }

        var boxes = new List<RectF>();
        RectF line = RectF.Empty;

        for (int i = from; i < to; i++)
        {
            RectF rect = _charRects[i];
            if (rect.IsEmpty)
            {
                // A line break ends the box of the line that preceded it
                if (!line.IsEmpty)
                {
                    boxes.Add(line);
                    line = RectF.Empty;
                }

                continue;
            }

            line = line.IsEmpty ? rect : Union(line, rect);
        }

        if (!line.IsEmpty)
        {
            boxes.Add(line);
        }

        return new PdfTextSelection(_text.ToString(from, to - from), boxes);
    }

    /// <summary>
    /// Selects the word around a caret position (used by double-click). Whitespace and punctuation
    /// form their own runs; a caret on whitespace selects the word before it when there is one.
    /// </summary>
    public (int Start, int End) GetWordRange(int caretIndex)
    {
        if (_text.Length == 0)
        {
            return (0, 0);
        }

        int index = Math.Clamp(caretIndex, 0, _text.Length - 1);
        if (index > 0 && _text[index] == '\n')
        {
            index--;
        }

        TextClass kind = Classify(_text[index]);
        if (kind == TextClass.Space)
        {
            // Double-clicking the gap after a word means that word, not the gap
            int back = index;
            while (back > 0 && Classify(_text[back - 1]) == TextClass.Space)
            {
                back--;
            }

            if (back > 0)
            {
                index = back - 1;
                kind = Classify(_text[index]);
            }
            else
            {
                int forward = index;
                while (forward < _text.Length - 1 && Classify(_text[forward + 1]) == TextClass.Space)
                {
                    forward++;
                }

                index = forward;
                kind = Classify(_text[index]);
            }
        }

        int start = index;
        int end = index + 1;
        while (start > 0 && Classify(_text[start - 1]) == kind)
        {
            start--;
        }

        while (end < _text.Length && Classify(_text[end]) == kind)
        {
            end++;
        }

        if (start > 0 && char.IsLowSurrogate(_text[start]) && char.IsHighSurrogate(_text[start - 1]))
        {
            start--;
        }

        if (end < _text.Length && char.IsLowSurrogate(_text[end]) && char.IsHighSurrogate(_text[end - 1]))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>
    /// Selects the whole line around a caret position (used by triple-click). For vertical text a
    /// line is a column, because line breaks are recorded at every column change.
    /// </summary>
    public (int Start, int End) GetLineRange(int caretIndex)
    {
        if (_text.Length == 0)
        {
            return (0, 0);
        }

        int index = Math.Clamp(caretIndex, 0, _text.Length);
        if (index == _text.Length || _text[index] == '\n')
        {
            if (index > 0)
            {
                index--;
            }
        }

        int start = index;
        while (start > 0 && _text[start - 1] != '\n')
        {
            start--;
        }

        int end = index;
        while (end < _text.Length && _text[end] != '\n')
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>Searches for a keyword (case-insensitive) and returns the bounding boxes of the hits.</summary>
    public IReadOnlyList<TextHit> Search(string query)
    {
        if (string.IsNullOrEmpty(query) || _text.Length == 0)
        {
            return Array.Empty<TextHit>();
        }

        var hits = new List<TextHit>();
        string text = _text.ToString();
        int index = 0;

        while ((index = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = Math.Min(index + query.Length, _charRects.Count);
            RectF? union = null;

            for (int i = index; i < end; i++)
            {
                RectF rect = _charRects[i];
                if (rect.IsEmpty)
                {
                    continue;
                }

                union = union is null ? rect : Union(union.Value, rect);
            }

            if (union is { IsEmpty: false })
            {
                hits.Add(new TextHit(union.Value));
            }

            index = end;
        }

        return hits;
    }

    internal void AddGlyph(RectF bounds, uint[] unicodeUnits, bool newLine, Vector2 direction)
    {
        if (direction.LengthSquared() < 1e-12f)
        {
            direction = Vector2.UnitX;
        }
        else
        {
            direction = Vector2.Normalize(direction);
        }

        if (newLine && _text.Length > _lineStart)
        {
            _text.Append('\n');
            _charRects.Add(RectF.Empty);
            _charDirections.Add(Vector2.Zero);
            CloseLine();
        }

        foreach (uint unit in unicodeUnits)
        {
            _text.Append((char)unit);
            _charRects.Add(bounds);
            _charDirections.Add(direction);
        }

        if (unicodeUnits.Length == 0)
        {
            // No Unicode mapping: keep the glyph position so hits still line up
            _text.Append('\uFFFD');
            _charRects.Add(bounds);
            _charDirections.Add(direction);
        }
    }

    internal void Finish() => CloseLine();

    private void CloseLine()
    {
        int end = _text.Length;
        if (end <= _lineStart)
        {
            _lineStart = end;
            return;
        }

        int length = end - _lineStart;
        string lineText = _text.ToString(_lineStart, length).TrimEnd('\n');
        if (lineText.Length > 0)
        {
            _lines.Add(new TextLine(UnionRange(_lineStart, end), lineText));
        }

        _lineStart = end;
    }

    private RectF UnionRange(int from, int to)
    {
        RectF result = RectF.Empty;
        for (int i = from; i < to; i++)
        {
            RectF rect = _charRects[i];
            if (rect.IsEmpty)
            {
                continue;
            }

            result = result.IsEmpty ? rect : Union(result, rect);
        }

        return result;
    }

    private static RectF Union(RectF a, RectF b) =>
        RectF.FromLbrb(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));

    private enum TextClass
    {
        Word,
        Space,
        Punctuation,
    }

    private static TextClass Classify(char c)
    {
        if (char.IsWhiteSpace(c))
        {
            return TextClass.Space;
        }

        if (char.IsSurrogate(c) || char.IsLetterOrDigit(c) || c == '_' || c == '\uFFFD')
        {
            return TextClass.Word;
        }

        return char.GetUnicodeCategory(c) switch
        {
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => TextClass.Word,
            _ => TextClass.Punctuation,
        };
    }
}

/// <summary>Collects glyphs from the content interpreter into a <see cref="PdfTextPage"/>.</summary>
internal sealed class TextCollector : ITextSink
{
    private readonly PdfTextPage _page = new();
    private float _lastBaselineY = float.MinValue;
    private float _lastColumnX = float.MinValue;
    private bool _hasLast;

    public PdfTextPage Page => _page;

    public void OnGlyph(PdfFont font, int code, ushort glyphId, uint[] unicodeUnits, Matrix3x2 textToUser, float x, float fontSize, float advanceEm)
    {
        // x and advanceEm are in text space (em); textToUser (which folds in the font size) maps them to user space.
        bool vertical = font.IsVertical;
        Vector2 origin, end;
        if (vertical)
        {
            // Vertical writing: the pen moves down the writing axis (negative text-space Y); the glyph
            // box is the em cell, so its horizontal extent is the font size and its vertical extent the advance.
            origin = Vector2.Transform(new Vector2(0f, -x), textToUser);
            end = Vector2.Transform(new Vector2(0f, -x - advanceEm), textToUser);
        }
        else
        {
            origin = Vector2.Transform(new Vector2(x, 0f), textToUser);
            end = Vector2.Transform(new Vector2(x + advanceEm, 0f), textToUser);
        }

        float scaleY = MathF.Sqrt((textToUser.M12 * textToUser.M12) + (textToUser.M22 * textToUser.M22));
        float scaleX = MathF.Sqrt((textToUser.M11 * textToUser.M11) + (textToUser.M21 * textToUser.M21));

        RectF bounds;
        bool newLine;
        if (vertical)
        {
            // scaleX/scaleY are already user-units-per-em (textToUser folds in the font size), so the
            // em cell is ~scaleX wide; the glyph occupies the advance interval [end.Y .. origin.Y] going down.
            float cellHalfWidth = scaleX * 0.5f;
            float top = Math.Max(origin.Y, end.Y);
            float bottom = Math.Min(origin.Y, end.Y);
            bounds = RectF.FromLbrb(origin.X - cellHalfWidth, bottom, origin.X + cellHalfWidth, top);
            // A vertical line break is a column change (horizontal jump), not the per-glyph downward step.
            newLine = _hasLast && MathF.Abs(origin.X - _lastColumnX) > (scaleX * 0.5f);
            _lastColumnX = origin.X;
        }
        else
        {
            float width = MathF.Abs(end.X - origin.X) + (MathF.Abs(end.Y - origin.Y) * 0.2f);
            if (width <= 0f)
            {
                width = scaleX * 0.5f;
            }

            // scaleX/scaleY are already user units per em (textToUser folds in the font size), so the
            // line box is a fraction of an em in user space - multiplying by fontSize again made every
            // box fontSize times too tall.
            float ascent = scaleY * 0.8f;
            float descent = scaleY * 0.2f;

            // Page user space is y-up: ascent goes up, descent goes down
            bounds = RectF.FromLbrb(origin.X, origin.Y - descent, origin.X + width, origin.Y + ascent);
            newLine = _hasLast && MathF.Abs(origin.Y - _lastBaselineY) > (fontSize * 0.5f);
            _lastBaselineY = origin.Y;
        }

        _page.AddGlyph(bounds, unicodeUnits, newLine, end - origin);
        _hasLast = true;
    }

    public void Finish() => _page.Finish();
}
