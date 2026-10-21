namespace Poso.PeachDrawing;

/// <summary>
/// Maps the standard 14 font names to system font families and styles (PDF 32000-1:2008 9.6.2) for the
/// PeachDrawing backend. Mirrors the Skia backend's <c>StandardFonts</c> (which cannot be shared: its
/// style type is a SkiaSharp one).
/// </summary>
internal static class PeachStandardFonts
{
    /// <summary>
    /// Families a caller may name directly - the CJK substitutes a non-embedded composite font asks for.
    /// The PDF standard-14 mapping below must not rewrite these into Arial, which has no CJK glyphs.
    /// </summary>
    private static readonly HashSet<string> PassThroughFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft YaHei", "SimSun", "MS Gothic", "Yu Gothic", "Malgun Gothic", "Microsoft JhengHei", "PMingLiU",
        "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans CJK KR", "Noto Sans CJK TC",
        "Noto Sans SC", "Noto Sans JP", "Noto Sans KR", "Noto Sans TC",
        "PingFang SC", "PingFang TC", "Hiragino Sans", "Apple SD Gothic Neo", "WenQuanYi Zen Hei",
    };

    /// <summary>Maps a base font name to a system font family name.</summary>
    public static string MapFamily(string baseFontName)
    {
        if (string.IsNullOrWhiteSpace(baseFontName))
        {
            return "Arial";
        }

        if (PassThroughFamilies.Contains(baseFontName))
        {
            return baseFontName;
        }

        string name = Normalize(baseFontName);

        if (ContainsAny(name, "symbol"))
        {
            return "Segoe UI Symbol";
        }

        if (ContainsAny(name, "zapfdingbats", "dingbat"))
        {
            // Glyphs are addressed by Unicode - the Adobe Glyph List turns a109 into U+2660 and friends -
            // so the family has to cover the Dingbats block. Wingdings keeps its shapes in the private use
            // area and has no glyph for U+2660/U+2701, which left pdf.js issue15716.pdf blank on this
            // back end while Skia, which maps the same names to the same code points, drew them.
            return "Segoe UI Symbol";
        }

        if (ContainsAny(name, "courier"))
        {
            return "Courier New";
        }

        if (ContainsAny(name, "times", "serif", "georgia", "garamond", "book", "minion", "palatino"))
        {
            return "Times New Roman";
        }

        // Helvetica/Arial/Verdana/Tahoma/Calibri and unknown sans-serif names default here
        return "Arial";
    }

    /// <summary>Infers the bold/italic style from a base font name and builds the family query for it.</summary>
    public static PeachText.TypefaceQuery MapQuery(string baseFontName)
    {
        if (string.IsNullOrWhiteSpace(baseFontName))
        {
            return default;
        }

        string name = Normalize(baseFontName);
        double weight = ContainsAny(name, "bold", "black", "heavy", "semibold") ? 700d : 400d;
        bool italic = ContainsAny(name, "italic", "oblique");
        return new PeachText.TypefaceQuery(weight, 5, italic);
    }

    private static string Normalize(string name) => name.Replace(",", "-").Replace(" ", "-").ToLowerInvariant();

    private static bool ContainsAny(string name, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (name.Contains(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
