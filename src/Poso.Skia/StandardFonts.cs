using SkiaSharp;

namespace Poso.Skia;

/// <summary>Maps the standard 14 font names to system font families and styles (PDF 32000-1:2008 9.6.2).</summary>
internal static class StandardFonts
{
    private static readonly string[] SerifBold = { "Times-Bold", "TimesNewRoman-Bold", "TimesNewRomanPS-BoldMT", "LiberationSerif-Bold" };
    private static readonly string[] SerifItalic = { "Times-Italic", "TimesNewRoman-Italic", "TimesNewRomanPS-ItalicMT", "LiberationSerif-Italic" };
    private static readonly string[] SerifBoldItalic = { "Times-BoldItalic", "TimesNewRoman-BoldItalic", "TimesNewRomanPS-BoldItalicMT", "LiberationSerif-BoldItalic" };
    private static readonly string[] SansBold = { "Helvetica-Bold", "Arial-Bold", "Arial-BoldMT", "Helvetica-BoldOblique", "LiberationSans-Bold" };
    private static readonly string[] SansOblique = { "Helvetica-Oblique", "Arial-Italic", "Helvetica-Italic", "Arial-ItalicMT", "LiberationSans-Italic" };
    private static readonly string[] SansBoldOblique = { "Helvetica-BoldOblique", "Arial-BoldItalic", "Arial-BoldItalicMT", "LiberationSans-BoldItalic" };

    /// <summary>Maps a base font name to a system font family name.</summary>
    public static string MapFamily(string baseFontName)
    {
        if (string.IsNullOrWhiteSpace(baseFontName))
        {
            return SKTypeface.Default.FamilyName;
        }        string name = Normalize(baseFontName);

        if (ContainsAny(name, "symbol"))
        {
            return "Segoe UI Symbol";
        }

        if (ContainsAny(name, "zapfdingbats", "dingbat"))
        {
            return "Wingdings";
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

    /// <summary>Infers the bold/italic style from a base font name.</summary>
    public static SKFontStyle MapStyle(string baseFontName)
    {
        if (string.IsNullOrWhiteSpace(baseFontName))
        {
            return new SKFontStyle(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        }

        string name = Normalize(baseFontName);
        bool bold = ContainsAny(name, "bold", "black", "heavy", "semibold");
        bool italic = ContainsAny(name, "italic", "oblique");

        SKFontStyleWeight weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        SKFontStyleSlant slant = italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        return new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
    }

    /// <summary>Bold name set (kept for callers to query; reserved for extension).</summary>
    public static bool IsBold(string baseFontName) => ContainsAny(Normalize(baseFontName), "bold", "black", "heavy", "semibold");

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
