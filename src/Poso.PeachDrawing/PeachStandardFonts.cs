namespace Poso.PeachDrawing;

/// <summary>
/// Maps the standard 14 font names to system font families and styles (PDF 32000-1:2008 9.6.2) for the
/// PeachDrawing backend. Mirrors the Skia backend's <c>StandardFonts</c> (which cannot be shared: its
/// style type is a SkiaSharp one).
/// </summary>
internal static class PeachStandardFonts
{
    /// <summary>Maps a base font name to a system font family name.</summary>
    public static string MapFamily(string baseFontName)
    {
        if (string.IsNullOrWhiteSpace(baseFontName))
        {
            return "Arial";
        }

        string name = Normalize(baseFontName);

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
