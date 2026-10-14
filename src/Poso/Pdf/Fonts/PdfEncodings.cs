namespace Poso.Pdf.Fonts;

/// <summary>PDF standard encoding tables (PDF 32000-1:2008 Annex D): byte code to glyph name. An empty string means undefined.</summary>
public static class PdfEncodings
{
    /// <summary>StandardEncoding (Latin letters plus a small set of symbols).</summary>
    public static readonly string[] Standard = BuildTable(new (int Code, string Name)[]
    {
        (0x20, "space"), (0x21, "exclam"), (0x22, "quotedbl"), (0x23, "numbersign"), (0x24, "dollar"),
        (0x25, "percent"), (0x26, "ampersand"), (0x27, "quoteright"), (0x28, "parenleft"), (0x29, "parenright"),
        (0x2A, "asterisk"), (0x2B, "plus"), (0x2C, "comma"), (0x2D, "hyphen"), (0x2E, "period"),
        (0x2F, "slash"), (0x30, "zero"), (0x31, "one"), (0x32, "two"), (0x33, "three"), (0x34, "four"),
        (0x35, "five"), (0x36, "six"), (0x37, "seven"), (0x38, "eight"), (0x39, "nine"), (0x3A, "colon"),
        (0x3B, "semicolon"), (0x3C, "less"), (0x3D, "equal"), (0x3E, "greater"), (0x3F, "question"),
        (0x40, "at"), (0x41, "A"), (0x42, "B"), (0x43, "C"), (0x44, "D"), (0x45, "E"), (0x46, "F"),
        (0x47, "G"), (0x48, "H"), (0x49, "I"), (0x4A, "J"), (0x4B, "K"), (0x4C, "L"), (0x4D, "M"),
        (0x4E, "N"), (0x4F, "O"), (0x50, "P"), (0x51, "Q"), (0x52, "R"), (0x53, "S"), (0x54, "T"),
        (0x55, "U"), (0x56, "V"), (0x57, "W"), (0x58, "X"), (0x59, "Y"), (0x5A, "Z"),
        (0x5B, "bracketleft"), (0x5C, "backslash"), (0x5D, "bracketright"), (0x5E, "asciicircum"),
        (0x5F, "underscore"), (0x60, "quoteleft"), (0x61, "a"), (0x62, "b"), (0x63, "c"), (0x64, "d"),
        (0x65, "e"), (0x66, "f"), (0x67, "g"), (0x68, "h"), (0x69, "i"), (0x6A, "j"), (0x6B, "k"),
        (0x6C, "l"), (0x6D, "m"), (0x6E, "n"), (0x6F, "o"), (0x70, "p"), (0x71, "q"), (0x72, "r"),
        (0x73, "s"), (0x74, "t"), (0x75, "u"), (0x76, "v"), (0x77, "w"), (0x78, "x"), (0x79, "y"),
        (0x7A, "z"), (0x7B, "braceleft"), (0x7C, "bar"), (0x7D, "braceright"), (0x7E, "asciitilde"),
        (0xA1, "exclamdown"), (0xA2, "cent"), (0xA3, "sterling"), (0xA4, "fraction"), (0xA5, "yen"),
        (0xA6, "florin"), (0xA7, "section"), (0xA8, "currency"), (0xA9, "quotesingle"),
        (0xAA, "quotedblleft"), (0xAB, "guillemotleft"), (0xAC, "guilsinglleft"), (0xAD, "guilsinglright"),
        (0xAE, "fi"), (0xAF, "fl"), (0xB1, "endash"), (0xB2, "dagger"), (0xB3, "daggerdbl"),
        (0xB4, "periodcentered"), (0xB6, "paragraph"), (0xB7, "bullet"), (0xB8, "quotesinglbase"),
        (0xB9, "quotedblbase"), (0xBA, "quotedblright"), (0xBB, "guillemotright"), (0xBC, "ellipsis"),
        (0xBD, "perthousand"), (0xBF, "questiondown"),
        (0xC1, "grave"), (0xC2, "acute"), (0xC3, "circumflex"), (0xC4, "tilde"), (0xC5, "macron"),
        (0xC6, "breve"), (0xC7, "dotaccent"), (0xC8, "dieresis"), (0xC9, "ring"), (0xCA, "cedilla"),
        (0xCB, "hungarumlaut"), (0xCC, "ogonek"), (0xCD, "caron"), (0xCE, "emdash"),
        (0xD0, "AE"), (0xD1, "ordfeminine"), (0xD2, "Lslash"), (0xD3, "Oslash"), (0xD4, "OE"),
        (0xD5, "ordmasculine"), (0xD6, "germandbls"),
        (0xE0, "dotlessi"), (0xE1, "lslash"), (0xE2, "oslash"), (0xE3, "oe"),
        (0xE8, "Scaron"), (0xE9, "scaron"), (0xEA, "Zcaron"), (0xEB, "zcaron"),
        (0xF1, "ff"), (0xF2, "fi"), (0xF3, "fl"), (0xF4, "ffi"), (0xF5, "ffl"),
    });

    /// <summary>WinAnsiEncoding (CP1252 subset; the de facto standard for Western European PDFs).</summary>
    public static readonly string[] WinAnsi = BuildTable(new (int Code, string Name)[]
    {
        (0x20, "space"), (0x21, "exclam"), (0x22, "quotedbl"), (0x23, "numbersign"), (0x24, "dollar"),
        (0x25, "percent"), (0x26, "ampersand"), (0x27, "quotesingle"), (0x28, "parenleft"), (0x29, "parenright"),
        (0x2A, "asterisk"), (0x2B, "plus"), (0x2C, "comma"), (0x2D, "hyphen"), (0x2E, "period"),
        (0x2F, "slash"), (0x30, "zero"), (0x31, "one"), (0x32, "two"), (0x33, "three"), (0x34, "four"),
        (0x35, "five"), (0x36, "six"), (0x37, "seven"), (0x38, "eight"), (0x39, "nine"), (0x3A, "colon"),
        (0x3B, "semicolon"), (0x3C, "less"), (0x3D, "equal"), (0x3E, "greater"), (0x3F, "question"),
        (0x40, "at"), (0x41, "A"), (0x42, "B"), (0x43, "C"), (0x44, "D"), (0x45, "E"), (0x46, "F"),
        (0x47, "G"), (0x48, "H"), (0x49, "I"), (0x4A, "J"), (0x4B, "K"), (0x4C, "L"), (0x4D, "M"),
        (0x4E, "N"), (0x4F, "O"), (0x50, "P"), (0x51, "Q"), (0x52, "R"), (0x53, "S"), (0x54, "T"),
        (0x55, "U"), (0x56, "V"), (0x57, "W"), (0x58, "X"), (0x59, "Y"), (0x5A, "Z"),
        (0x5B, "bracketleft"), (0x5C, "backslash"), (0x5D, "bracketright"), (0x5E, "asciicircum"),
        (0x5F, "underscore"), (0x60, "grave"), (0x61, "a"), (0x62, "b"), (0x63, "c"), (0x64, "d"),
        (0x65, "e"), (0x66, "f"), (0x67, "g"), (0x68, "h"), (0x69, "i"), (0x6A, "j"), (0x6B, "k"),
        (0x6C, "l"), (0x6D, "m"), (0x6E, "n"), (0x6F, "o"), (0x70, "p"), (0x71, "q"), (0x72, "r"),
        (0x73, "s"), (0x74, "t"), (0x75, "u"), (0x76, "v"), (0x77, "w"), (0x78, "x"), (0x79, "y"),
        (0x7A, "z"), (0x7B, "braceleft"), (0x7C, "bar"), (0x7D, "braceright"), (0x7E, "asciitilde"),
        (0x80, "Euro"), (0x82, "quotesinglbase"), (0x83, "florin"), (0x84, "quotedblbase"),
        (0x85, "ellipsis"), (0x86, "dagger"), (0x87, "daggerdbl"), (0x88, "circumflex"),
        (0x89, "perthousand"), (0x8A, "Scaron"), (0x8B, "guilsinglleft"), (0x8C, "OE"),
        (0x8E, "Zcaron"), (0x91, "quoteleft"), (0x92, "quoteright"), (0x93, "quotedblleft"),
        (0x94, "quotedblright"), (0x95, "bullet"), (0x96, "endash"), (0x97, "emdash"), (0x98, "tilde"),
        (0x99, "trademark"), (0x9A, "scaron"), (0x9B, "guilsinglright"), (0x9C, "oe"), (0x9E, "zcaron"),
        (0x9F, "Ydieresis"),
        (0xA0, "space"), (0xA1, "exclamdown"), (0xA2, "cent"), (0xA3, "sterling"), (0xA4, "currency"),
        (0xA5, "yen"), (0xA6, "brokenbar"), (0xA7, "section"), (0xA8, "dieresis"), (0xA9, "copyright"),
        (0xAA, "ordfeminine"), (0xAB, "guillemotleft"), (0xAC, "logicalnot"), (0xAD, "hyphen"),
        (0xAE, "registered"), (0xAF, "macron"), (0xB0, "degree"), (0xB1, "plusminus"),
        (0xB2, "twosuperior"), (0xB3, "threesuperior"), (0xB4, "acute"), (0xB5, "mu"), (0xB6, "paragraph"),
        (0xB7, "periodcentered"), (0xB8, "cedilla"), (0xB9, "onesuperior"), (0xBA, "ordmasculine"),
        (0xBB, "guillemotright"), (0xBC, "onequarter"), (0xBD, "onehalf"), (0xBE, "threequarters"),
        (0xBF, "questiondown"),
        (0xC0, "Agrave"), (0xC1, "Aacute"), (0xC2, "Acircumflex"), (0xC3, "Atilde"), (0xC4, "Adieresis"),
        (0xC5, "Aring"), (0xC6, "AE"), (0xC7, "Ccedilla"), (0xC8, "Egrave"), (0xC9, "Eacute"),
        (0xCA, "Ecircumflex"), (0xCB, "Edieresis"), (0xCC, "Igrave"), (0xCD, "Iacute"),
        (0xCE, "Icircumflex"), (0xCF, "Idieresis"), (0xD0, "Eth"), (0xD1, "Ntilde"), (0xD2, "Ograve"),
        (0xD3, "Oacute"), (0xD4, "Ocircumflex"), (0xD5, "Otilde"), (0xD6, "Odieresis"),
        (0xD7, "multiply"), (0xD8, "Oslash"), (0xD9, "Ugrave"), (0xDA, "Uacute"), (0xDB, "Ucircumflex"),
        (0xDC, "Udieresis"), (0xDD, "Yacute"), (0xDE, "Thorn"), (0xDF, "germandbls"),
        (0xE0, "agrave"), (0xE1, "aacute"), (0xE2, "acircumflex"), (0xE3, "atilde"), (0xE4, "adieresis"),
        (0xE5, "aring"), (0xE6, "ae"), (0xE7, "ccedilla"), (0xE8, "egrave"), (0xE9, "eacute"),
        (0xEA, "ecircumflex"), (0xEB, "edieresis"), (0xEC, "igrave"), (0xED, "iacute"),
        (0xEE, "icircumflex"), (0xEF, "idieresis"), (0xF0, "eth"), (0xF1, "ntilde"), (0xF2, "ograve"),
        (0xF3, "oacute"), (0xF4, "ocircumflex"), (0xF5, "otilde"), (0xF6, "odieresis"),
        (0xF7, "divide"), (0xF8, "oslash"), (0xF9, "ugrave"), (0xFA, "uacute"), (0xFB, "ucircumflex"),
        (0xFC, "udieresis"), (0xFD, "yacute"), (0xFE, "thorn"), (0xFF, "ydieresis"),
    });

    /// <summary>MacRomanEncoding (classic Mac encoding; PDF 32000-1 Table D.2).</summary>
    public static readonly string[] MacRoman = BuildTable(new (int Code, string Name)[]
    {
        // 0x20-0x7E printable ASCII: identical to WinAnsiEncoding's low range.
        (0x20, "space"), (0x21, "exclam"), (0x22, "quotedbl"), (0x23, "numbersign"), (0x24, "dollar"),
        (0x25, "percent"), (0x26, "ampersand"), (0x27, "quotesingle"), (0x28, "parenleft"), (0x29, "parenright"),
        (0x2A, "asterisk"), (0x2B, "plus"), (0x2C, "comma"), (0x2D, "hyphen"), (0x2E, "period"),
        (0x2F, "slash"), (0x30, "zero"), (0x31, "one"), (0x32, "two"), (0x33, "three"), (0x34, "four"),
        (0x35, "five"), (0x36, "six"), (0x37, "seven"), (0x38, "eight"), (0x39, "nine"), (0x3A, "colon"),
        (0x3B, "semicolon"), (0x3C, "less"), (0x3D, "equal"), (0x3E, "greater"), (0x3F, "question"),
        (0x40, "at"), (0x41, "A"), (0x42, "B"), (0x43, "C"), (0x44, "D"), (0x45, "E"), (0x46, "F"),
        (0x47, "G"), (0x48, "H"), (0x49, "I"), (0x4A, "J"), (0x4B, "K"), (0x4C, "L"), (0x4D, "M"),
        (0x4E, "N"), (0x4F, "O"), (0x50, "P"), (0x51, "Q"), (0x52, "R"), (0x53, "S"), (0x54, "T"),
        (0x55, "U"), (0x56, "V"), (0x57, "W"), (0x58, "X"), (0x59, "Y"), (0x5A, "Z"),
        (0x5B, "bracketleft"), (0x5C, "backslash"), (0x5D, "bracketright"), (0x5E, "asciicircum"),
        (0x5F, "underscore"), (0x60, "grave"), (0x61, "a"), (0x62, "b"), (0x63, "c"), (0x64, "d"),
        (0x65, "e"), (0x66, "f"), (0x67, "g"), (0x68, "h"), (0x69, "i"), (0x6A, "j"), (0x6B, "k"),
        (0x6C, "l"), (0x6D, "m"), (0x6E, "n"), (0x6F, "o"), (0x70, "p"), (0x71, "q"), (0x72, "r"),
        (0x73, "s"), (0x74, "t"), (0x75, "u"), (0x76, "v"), (0x77, "w"), (0x78, "x"), (0x79, "y"),
        (0x7A, "z"), (0x7B, "braceleft"), (0x7C, "bar"), (0x7D, "braceright"), (0x7E, "asciitilde"),
        (0x80, "Adieresis"), (0x81, "Aring"), (0x82, "Ccedilla"), (0x83, "Eacute"), (0x84, "Ntilde"),
        (0x85, "Odieresis"), (0x86, "Udieresis"), (0x87, "aacute"), (0x88, "agrave"), (0x89, "acircumflex"),
        (0x8A, "adieresis"), (0x8B, "atilde"), (0x8C, "aring"), (0x8D, "ccedilla"), (0x8E, "eacute"),
        (0x8F, "egrave"), (0x90, "ecircumflex"), (0x91, "edieresis"), (0x92, "iacute"), (0x93, "igrave"),
        (0x94, "icircumflex"), (0x95, "idieresis"), (0x96, "ntilde"), (0x97, "oacute"), (0x98, "ograve"),
        (0x99, "ocircumflex"), (0x9A, "odieresis"), (0x9B, "otilde"), (0x9C, "uacute"), (0x9D, "ugrave"),
        (0x9E, "ucircumflex"), (0x9F, "udieresis"),
        (0xA0, "dagger"), (0xA1, "degree"), (0xA2, "cent"), (0xA3, "sterling"), (0xA4, "section"),
        (0xA5, "bullet"), (0xA6, "paragraph"), (0xA7, "germandbls"), (0xA8, "registered"),
        (0xA9, "copyright"), (0xAA, "trademark"), (0xAB, "acute"), (0xAC, "dieresis"), (0xAD, "notequal"),
        (0xAE, "AE"), (0xAF, "Oslash"), (0xB0, "infinity"), (0xB1, "plusminus"), (0xB2, "lessequal"),
        (0xB3, "greaterequal"), (0xB4, "yen"), (0xB5, "mu"), (0xB6, "partialdiff"), (0xB7, "summation"),
        (0xB8, "product"), (0xB9, "pi"), (0xBA, "integral"), (0xBB, "ordfeminine"), (0xBC, "ordmasculine"),
        (0xBD, "Omega"), (0xBE, "ae"), (0xBF, "oslash"),
        (0xC0, "questiondown"), (0xC1, "exclamdown"), (0xC2, "logicalnot"), (0xC3, "radical"),
        (0xC4, "florin"), (0xC5, "approxequal"), (0xC6, "Delta"), (0xC7, "guillemotleft"),
        (0xC8, "guillemotright"), (0xC9, "ellipsis"), (0xCA, "space"), (0xCB, "Agrave"),
        (0xCC, "Atilde"), (0xCD, "Otilde"), (0xCE, "OE"), (0xCF, "oe"), (0xD0, "endash"), (0xD1, "emdash"),
        (0xD2, "quotedblleft"), (0xD3, "quotedblright"), (0xD4, "quoteleft"), (0xD5, "quoteright"),
        (0xD6, "divide"), (0xD7, "lozenge"), (0xD8, "ydieresis"), (0xD9, "Ydieresis"), (0xDA, "fraction"),
        (0xDB, "Euro"), (0xDC, "guilsinglleft"), (0xDD, "guilsinglright"), (0xDE, "fi"), (0xDF, "fl"),
        (0xE0, "daggerdbl"), (0xE1, "periodcentered"), (0xE2, "quotesinglbase"), (0xE3, "quotedblbase"),
        (0xE4, "perthousand"), (0xE5, "Acircumflex"), (0xE6, "Ecircumflex"), (0xE7, "Aacute"),
        (0xE8, "Edieresis"), (0xE9, "Egrave"), (0xEA, "Iacute"), (0xEB, "Icircumflex"), (0xEC, "Idieresis"),
        (0xED, "Igrave"), (0xEE, "Oacute"), (0xEF, "Ocircumflex"),
        (0xF0, "ring"), (0xF1, "cedilla"), (0xF2, "hungarumlaut"), (0xF3, "ogonek"), (0xF4, "caron"),
    });

    private static string[] BuildTable((int Code, string Name)[] entries)
    {
        var table = new string[256];
        foreach ((int code, string name) in entries)
        {
            if (code >= 0 && code < 256)
            {
                table[code] = name;
            }
        }

        return table;
    }
}
