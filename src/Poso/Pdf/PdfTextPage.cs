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
/// Extracted text of a page: the full text, per-line spans with positions, and keyword search.
/// Coordinates are in page user space (y-up, origin at the crop box corner).
/// </summary>
public sealed class PdfTextPage
{
    private readonly StringBuilder _text = new();
    private readonly List<RectF> _charRects = new();
    private readonly List<TextLine> _lines = new();
    private int _lineStart;

    internal PdfTextPage()
    {
    }

    /// <summary>The full extracted text (lines separated by \n).</summary>
    public string Text => _text.ToString();

    /// <summary>Extracted lines with their bounding boxes.</summary>
    public IReadOnlyList<TextLine> Lines => _lines;

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

    internal void AddGlyph(RectF bounds, uint[] unicodeUnits, bool newLine)
    {
        if (newLine && _text.Length > _lineStart)
        {
            _text.Append('\n');
            _charRects.Add(RectF.Empty);
            CloseLine();
        }

        foreach (uint unit in unicodeUnits)
        {
            _text.Append((char)unit);
            _charRects.Add(bounds);
        }

        if (unicodeUnits.Length == 0)
        {
            // No Unicode mapping: keep the glyph position so hits still line up
            _text.Append('\uFFFD');
            _charRects.Add(bounds);
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
}

/// <summary>Collects glyphs from the content interpreter into a <see cref="PdfTextPage"/>.</summary>
internal sealed class TextCollector : ITextSink
{
    private readonly PdfTextPage _page = new();
    private float _lastBaselineY = float.MinValue;
    private bool _hasLast;

    public PdfTextPage Page => _page;

    public void OnGlyph(PdfFont font, int code, ushort glyphId, uint[] unicodeUnits, Matrix3x2 textToUser, float x, float fontSize, float advanceEm)
    {
        // x and advanceEm are in text space (em); textToUser (which folds in the font size) maps them to user space.
        Vector2 origin = Vector2.Transform(new Vector2(x, 0f), textToUser);
        Vector2 end = Vector2.Transform(new Vector2(x + advanceEm, 0f), textToUser);

        float width = MathF.Abs(end.X - origin.X) + (MathF.Abs(end.Y - origin.Y) * 0.2f);
        if (width <= 0f)
        {
            width = fontSize * 0.5f;
        }

        float scaleY = MathF.Sqrt((textToUser.M12 * textToUser.M12) + (textToUser.M22 * textToUser.M22));
        float ascent = fontSize * 0.8f * scaleY;
        float descent = fontSize * 0.2f * scaleY;

        // Page user space is y-up: ascent goes up, descent goes down
        RectF bounds = RectF.FromLbrb(origin.X, origin.Y - descent, origin.X + width, origin.Y + ascent);

        bool newLine = _hasLast && MathF.Abs(origin.Y - _lastBaselineY) > (fontSize * 0.5f);
        _page.AddGlyph(bounds, unicodeUnits, newLine);

        _lastBaselineY = origin.Y;
        _hasLast = true;
    }

    public void Finish() => _page.Finish();
}
