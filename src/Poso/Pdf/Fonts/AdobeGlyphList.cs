namespace Poso.Pdf.Fonts;

/// <summary>
/// Subset of the Adobe Glyph List: glyph name to Unicode. Covers the glyphs commonly used in
/// Latin text; also supports the <c>uniXXXX</c> / <c>uXXXXXX</c> naming conventions.
/// </summary>
public static class AdobeGlyphList
{
    private static readonly Dictionary<string, uint> Names = new(StringComparer.Ordinal)
    {
        // Basic Latin letters
        ["A"] = 0x0041, ["B"] = 0x0042, ["C"] = 0x0043, ["D"] = 0x0044, ["E"] = 0x0045, ["F"] = 0x0046,
        ["G"] = 0x0047, ["H"] = 0x0048, ["I"] = 0x0049, ["J"] = 0x004A, ["K"] = 0x004B, ["L"] = 0x004C,
        ["M"] = 0x004D, ["N"] = 0x004E, ["O"] = 0x004F, ["P"] = 0x0050, ["Q"] = 0x0051, ["R"] = 0x0052,
        ["S"] = 0x0053, ["T"] = 0x0054, ["U"] = 0x0055, ["V"] = 0x0056, ["W"] = 0x0057, ["X"] = 0x0058,
        ["Y"] = 0x0059, ["Z"] = 0x005A,
        ["a"] = 0x0061, ["b"] = 0x0062, ["c"] = 0x0063, ["d"] = 0x0064, ["e"] = 0x0065, ["f"] = 0x0066,
        ["g"] = 0x0067, ["h"] = 0x0068, ["i"] = 0x0069, ["j"] = 0x006A, ["k"] = 0x006B, ["l"] = 0x006C,
        ["m"] = 0x006D, ["n"] = 0x006E, ["o"] = 0x006F, ["p"] = 0x0070, ["q"] = 0x0071, ["r"] = 0x0072,
        ["s"] = 0x0073, ["t"] = 0x0074, ["u"] = 0x0075, ["v"] = 0x0076, ["w"] = 0x0077, ["x"] = 0x0078,
        ["y"] = 0x0079, ["z"] = 0x007A,

        // Punctuation and symbols
        ["space"] = 0x0020, ["exclam"] = 0x0021, ["quotedbl"] = 0x0022, ["numbersign"] = 0x0023,
        ["dollar"] = 0x0024, ["percent"] = 0x0025, ["ampersand"] = 0x0026, ["quotesingle"] = 0x0027,
        ["parenleft"] = 0x0028, ["parenright"] = 0x0029, ["asterisk"] = 0x002A, ["plus"] = 0x002B,
        ["comma"] = 0x002C, ["hyphen"] = 0x002D, ["period"] = 0x002E, ["slash"] = 0x002F,
        ["zero"] = 0x0030, ["one"] = 0x0031, ["two"] = 0x0032, ["three"] = 0x0033, ["four"] = 0x0034,
        ["five"] = 0x0035, ["six"] = 0x0036, ["seven"] = 0x0037, ["eight"] = 0x0038, ["nine"] = 0x0039,
        ["colon"] = 0x003A, ["semicolon"] = 0x003B, ["less"] = 0x003C, ["equal"] = 0x003D,
        ["greater"] = 0x003E, ["question"] = 0x003F, ["at"] = 0x0040,
        ["bracketleft"] = 0x005B, ["backslash"] = 0x005C, ["bracketright"] = 0x005D,
        ["asciicircum"] = 0x005E, ["underscore"] = 0x005F, ["grave"] = 0x0060,
        ["braceleft"] = 0x007B, ["bar"] = 0x007C, ["braceright"] = 0x007D, ["asciitilde"] = 0x007E,

        // Latin extended characters and typographic symbols
        ["exclamdown"] = 0x00A1, ["cent"] = 0x00A2, ["sterling"] = 0x00A3, ["currency"] = 0x00A4,
        ["yen"] = 0x00A5, ["brokenbar"] = 0x00A6, ["section"] = 0x00A7, ["dieresis"] = 0x00A8,
        ["copyright"] = 0x00A9, ["ordfeminine"] = 0x00AA, ["guillemotleft"] = 0x00AB,
        ["logicalnot"] = 0x00AC, ["softhyphen"] = 0x00AD, ["registered"] = 0x00AE, ["macron"] = 0x00AF,
        ["degree"] = 0x00B0, ["plusminus"] = 0x00B1, ["twosuperior"] = 0x00B2, ["threesuperior"] = 0x00B3,
        ["acute"] = 0x00B4, ["mu"] = 0x00B5, ["paragraph"] = 0x00B6, ["periodcentered"] = 0x00B7,
        ["cedilla"] = 0x00B8, ["onesuperior"] = 0x00B9, ["ordmasculine"] = 0x00BA,
        ["guillemotright"] = 0x00BB, ["onequarter"] = 0x00BC, ["onehalf"] = 0x00BD, ["threequarters"] = 0x00BE,
        ["questiondown"] = 0x00BF,
        ["Agrave"] = 0x00C0, ["Aacute"] = 0x00C1, ["Acircumflex"] = 0x00C2, ["Atilde"] = 0x00C3,
        ["Adieresis"] = 0x00C4, ["Aring"] = 0x00C5, ["AE"] = 0x00C6, ["Ccedilla"] = 0x00C7,
        ["Egrave"] = 0x00C8, ["Eacute"] = 0x00C9, ["Ecircumflex"] = 0x00CA, ["Edieresis"] = 0x00CB,
        ["Igrave"] = 0x00CC, ["Iacute"] = 0x00CD, ["Icircumflex"] = 0x00CE, ["Idieresis"] = 0x00CF,
        ["Eth"] = 0x00D0, ["Ntilde"] = 0x00D1, ["Ograve"] = 0x00D2, ["Oacute"] = 0x00D3,
        ["Ocircumflex"] = 0x00D4, ["Otilde"] = 0x00D5, ["Odieresis"] = 0x00D6, ["multiply"] = 0x00D7,
        ["Oslash"] = 0x00D8, ["Ugrave"] = 0x00D9, ["Uacute"] = 0x00DA, ["Ucircumflex"] = 0x00DB,
        ["Udieresis"] = 0x00DC, ["Yacute"] = 0x00DD, ["Thorn"] = 0x00DE, ["germandbls"] = 0x00DF,
        ["agrave"] = 0x00E0, ["aacute"] = 0x00E1, ["acircumflex"] = 0x00E2, ["atilde"] = 0x00E3,
        ["adieresis"] = 0x00E4, ["aring"] = 0x00E5, ["ae"] = 0x00E6, ["ccedilla"] = 0x00E7,
        ["egrave"] = 0x00E8, ["eacute"] = 0x00E9, ["ecircumflex"] = 0x00EA, ["edieresis"] = 0x00EB,
        ["igrave"] = 0x00EC, ["iacute"] = 0x00ED, ["icircumflex"] = 0x00EE, ["idieresis"] = 0x00EF,
        ["eth"] = 0x00F0, ["ntilde"] = 0x00F1, ["ograve"] = 0x00F2, ["oacute"] = 0x00F3,
        ["ocircumflex"] = 0x00F4, ["otilde"] = 0x00F5, ["odieresis"] = 0x00F6, ["divide"] = 0x00F7,
        ["oslash"] = 0x00F8, ["ugrave"] = 0x00F9, ["uacute"] = 0x00FA, ["ucircumflex"] = 0x00FB,
        ["udieresis"] = 0x00FC, ["yacute"] = 0x00FD, ["thorn"] = 0x00FE, ["ydieresis"] = 0x00FF,

        // Common punctuation and symbols
        ["quoteright"] = 0x2019, ["quoteleft"] = 0x2018, ["quotedblleft"] = 0x201C, ["quotedblright"] = 0x201D,
        ["quotesinglbase"] = 0x201A, ["quotedblbase"] = 0x201E, ["endash"] = 0x2013, ["emdash"] = 0x2014,
        ["bullet"] = 0x2022, ["dagger"] = 0x2020, ["daggerdbl"] = 0x2021, ["ellipsis"] = 0x2026,
        ["perthousand"] = 0x2030, ["guilsinglleft"] = 0x2039, ["guilsinglright"] = 0x203A,
        ["fraction"] = 0x2044, ["trademark"] = 0x2122, ["minus"] = 0x2212, ["Euro"] = 0x20AC,
        ["florin"] = 0x0192, ["circumflex"] = 0x02C6, ["caron"] = 0x02C7, ["breve"] = 0x02D8,
        ["dotaccent"] = 0x02D9, ["ring"] = 0x02DA, ["ogonek"] = 0x02DB, ["tilde"] = 0x02DC,
        ["hungarumlaut"] = 0x02DD, ["grave"] = 0x0060, ["acutecomb"] = 0x0301,
        ["Scaron"] = 0x0160, ["scaron"] = 0x0161, ["Zcaron"] = 0x017D, ["zcaron"] = 0x017E,
        ["Lslash"] = 0x0141, ["lslash"] = 0x0142, ["OE"] = 0x0152, ["oe"] = 0x0153,
        ["Ydieresis"] = 0x0178, ["dotlessi"] = 0x0131, ["dotlessj"] = 0x0237,
        ["fi"] = 0xFB01, ["fl"] = 0xFB02, ["ff"] = 0xFB00, ["ffi"] = 0xFB03, ["ffl"] = 0xFB04,
        ["nbspace"] = 0x00A0, ["hyphen"] = 0x002D, ["space"] = 0x0020, ["middot"] = 0x00B7,
        ["lozenge"] = 0x25CA, ["arrowboth"] = 0x2194, ["arrowleft"] = 0x2190, ["arrowright"] = 0x2192,
        ["arrowup"] = 0x2191, ["arrowdown"] = 0x2193, ["degree"] = 0x00B0, ["radical"] = 0x221A,
        ["infinity"] = 0x221E, ["integral"] = 0x222B, ["approxequal"] = 0x2248, ["notequal"] = 0x2260,
        ["lessequal"] = 0x2264, ["greaterequal"] = 0x2265, ["partialdiff"] = 0x2202, ["summation"] = 0x2211,
        ["product"] = 0x220F, ["pi"] = 0x03C0, ["mu1"] = 0x00B5, ["Omega"] = 0x03A9, ["omega"] = 0x03C9,
        ["alpha"] = 0x03B1, ["beta"] = 0x03B2, ["gamma"] = 0x03B3, ["delta"] = 0x03B4,
        ["epsilon"] = 0x03B5, ["theta"] = 0x03B8, ["lambda"] = 0x03BB, ["sigma"] = 0x03C3,
        ["phi"] = 0x03C6, ["psi"] = 0x03C8,
    };

    /// <summary>Maps a glyph name to Unicode; returns 0 when unknown.</summary>
    public static uint GetUnicode(string glyphName)
    {
        if (string.IsNullOrEmpty(glyphName))
        {
            return 0;
        }

        if (Names.TryGetValue(glyphName, out uint known))
        {
            return known;
        }

        return GetUnicodeFromPattern(glyphName);
    }

    /// <summary>Naming conventions: uniXXXX (4 hex digits), uXXXXXX (up to 6 hex digits).</summary>
    private static uint GetUnicodeFromPattern(string name)
    {
        if (name.Length is >= 7 and <= 10 && name.StartsWith("uni", StringComparison.Ordinal))
        {
            // uniXXXX or uniXXXXXXXX (surrogate pairs)
            ReadOnlySpan<char> hex = name.AsSpan(3);
            uint value = ParseHex(hex);
            if (value != 0)
            {
                return value;
            }
        }

        if (name.Length is >= 2 and <= 7 && name[0] == 'u' && char.IsAsciiHexDigit(name[1]))
        {
            return ParseHex(name.AsSpan(1));
        }

        // gXX / cidXX / index / GlyphNNN and similar names have no Unicode mapping
        return 0;
    }

    private static uint ParseHex(ReadOnlySpan<char> hex)
    {
        uint value = 0;
        int digits = 0;
        foreach (char c in hex)
        {
            int digit = char.IsAsciiHexDigit(c)
                ? (c >= '0' && c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10)
                : -1;
            if (digit < 0)
            {
                break;
            }

            value = (value * 16) + (uint)digit;
            digits++;
            if (digits >= 6)
            {
                break;
            }
        }

        return digits > 0 ? value : 0;
    }
}
