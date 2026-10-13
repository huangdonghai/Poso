using Poso.Pdf.Objects;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Simple font (Type1/TrueType/MMType1, single-byte encodings): resolves codes to glyph names via
/// the base encoding plus /Differences, and to Unicode via the Adobe Glyph List.
/// </summary>
public sealed class SimplePdfFont : PdfFont
{
    private readonly string?[] _codeToName = new string?[256];
    private readonly uint[] _codeToUnicode = new uint[256];
    private readonly float[] _codeWidthEm = new float[256];
    private readonly bool[] _widthResolved = new bool[256];

    public SimplePdfFont(PdfDocument document, PdfDictionary dictionary, string baseFontName)
        : base(document, dictionary, baseFontName)
    {
        BuildEncoding();
    }

    public override bool IsSimpleFont => true;

    public override void DecodeCodes(ReadOnlySpan<byte> bytes, List<int> codes)
    {
        foreach (byte b in bytes)
        {
            codes.Add(b);
        }
    }

    public override ushort GetGlyphId(int code)
    {
        if (code < 0 || code > 255)
        {
            return 0;
        }

        Poso.Graphics.IFont? graphics = GraphicsFont;
        if (graphics is null)
        {
            return 0;
        }

        // An embedded CFF is addressed by glyph name: the PDF encoding is authoritative, and subsetting
        // usually strips the cmap that would otherwise map code points onto glyph IDs.
        if (EmbeddedGlyphNames is { } cff)
        {
            if (_codeToName[code] is { } name && cff.TryGetGlyphId(name, out ushort embedded))
            {
                return embedded;
            }

            // Custom-named subsets carry no usable names; their own encoding still matches the codes
            if (cff.TryGetGlyphIdForCode(code, out ushort byCode))
            {
                return byCode;
            }
        }

        uint unicode = _codeToUnicode[code];
        return graphics.GetGlyphId(unicode != 0 ? unicode : (uint)code);
    }

    public override float GetWidthEm(int code)
    {
        if (code < 0 || code > 255)
        {
            return 0f;
        }

        if (!_widthResolved[code])
        {
            float width = GetWidthFromWidths(code);
            _codeWidthEm[code] = width != 0f
                ? width / 1000f
                : Dictionary.GetArray("Widths") is not null
                    ? GetMissingWidthEm()
                    : GetGraphicsAdvanceEm(code);
            _widthResolved[code] = true;
        }

        return _codeWidthEm[code];
    }

    public override uint GetUnicode(int code) => code >= 0 && code <= 255 ? _codeToUnicode[code] : 0;

    /// <summary>Gets the glyph name mapped to a code (null when undefined).</summary>
    public string? GetGlyphName(int code) => code >= 0 && code <= 255 ? _codeToName[code] : null;

    /// <summary>Falls back to the backend font advance when the PDF provides no widths.</summary>
    private float GetGraphicsAdvanceEm(int code)
    {
        Poso.Graphics.IFont? graphics = GraphicsFont;
        if (graphics is null)
        {
            return GetMissingWidthEm();
        }

        ushort glyphId = GetGlyphId(code);
        return glyphId != 0 ? graphics.GetAdvanceWidth(glyphId) : GetMissingWidthEm();
    }

    private void BuildEncoding()
    {
        PdfObject? encodingObject = Dictionary.Get("Encoding");
        PdfDictionary? encodingDictionary = Document.Resolve(encodingObject) as PdfDictionary;

        if (encodingDictionary is not null)
        {
            // /BaseEncoding first, then /Differences overrides individual codes
            string? baseEncoding = encodingDictionary.GetNameValue("BaseEncoding");
            string?[]? table = baseEncoding is null ? null : SelectEncodingTable(baseEncoding);
            if (table is null)
            {
                table = GetBuiltInEncodingTable();
            }

            if (table is not null)
            {
                Array.Copy(table, _codeToName, 256);
            }

            ApplyDifferences(encodingDictionary.GetArray("Differences"));
        }
        else
        {
            string?[]? table = encodingObject is PdfName encodingName
                ? SelectEncodingTable(encodingName.Value)
                : GetBuiltInEncodingTable();

            if (table is not null)
            {
                Array.Copy(table, _codeToName, 256);
            }
        }

        for (int i = 0; i < 256; i++)
        {
            string? name = _codeToName[i];
            if (!string.IsNullOrEmpty(name))
            {
                _codeToUnicode[i] = AdobeGlyphList.GetUnicode(name);
            }
        }
    }

    /// <summary>
    /// The built-in encoding used when no /Encoding is present: StandardEncoding for
    /// non-symbolic fonts, none for symbolic fonts (their encoding lives in the font program).
    /// </summary>
    private string?[]? GetBuiltInEncodingTable()
    {
        PdfDictionary? descriptor = Document.Resolve(Dictionary.Get("FontDescriptor")) as PdfDictionary;
        long flags = descriptor?.GetIntegerValue("Flags") ?? 0;
        bool symbolic = (flags & 4) != 0; // Flags bit 3 (value 4): symbolic
        return symbolic ? null : PdfEncodings.Standard;
    }

    private static string?[]? SelectEncodingTable(string name) => name switch
    {
        "WinAnsiEncoding" => PdfEncodings.WinAnsi,
        "MacRomanEncoding" => PdfEncodings.MacRoman,
        "StandardEncoding" or "PDFDocEncoding" => PdfEncodings.Standard,
        "MacExpertEncoding" => null,
        _ => PdfEncodings.Standard,
    };

    private void ApplyDifferences(PdfArray? differences)
    {
        if (differences is null)
        {
            return;
        }

        int code = 0;
        foreach (PdfObject item in differences)
        {
            switch (item)
            {
                case PdfNumber number:
                    code = number.AsInt();
                    break;

                case PdfName name:
                    if (code >= 0 && code < 256)
                    {
                        _codeToName[code] = name.Value;
                    }

                    code++;
                    break;
            }
        }
    }
}
