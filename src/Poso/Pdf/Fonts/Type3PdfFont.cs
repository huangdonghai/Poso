using System.Numerics;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Type3 font: glyphs are content streams (CharProcs) executed by the interpreter. The glyph
/// advance is reported by the d0 operator inside the glyph procedure (falling back to /Widths).
/// </summary>
public sealed class Type3PdfFont : PdfFont
{
    private readonly PdfDictionary _charProcs;
    private readonly Matrix3x2 _fontMatrix;
    private readonly string?[] _codeToName = new string?[256];
    private readonly Dictionary<int, float> _codeWidthEm = new();
    private readonly float _glyphSpaceScale;

    public Type3PdfFont(PdfDocument document, PdfDictionary dictionary, string baseFontName)
        : base(document, dictionary, baseFontName)
    {
        _charProcs = Document.Resolve(dictionary.Get("CharProcs")) as PdfDictionary ?? new PdfDictionary();

        Matrix3x2? matrix = ParseMatrix(dictionary.GetArray("FontMatrix"));
        _fontMatrix = matrix ?? Matrix3x2.CreateScale(0.001f);
        _glyphSpaceScale = Math.Abs(_fontMatrix.M11);

        BuildEncoding();
        BuildWidths();
    }

    public override bool IsSimpleFont => true;

    public override bool IsType3 => true;

    /// <summary>The font matrix (glyph space to text space).</summary>
    public Matrix3x2 FontMatrix => _fontMatrix;

    /// <summary>Resources available to the glyph procedures.</summary>
    public PdfDictionary? Resources => Document.Resolve(Dictionary.Get("Resources")) as PdfDictionary;

    public override void DecodeCodes(ReadOnlySpan<byte> bytes, List<int> codes)
    {
        foreach (byte b in bytes)
        {
            codes.Add(b);
        }
    }

    public override ushort GetGlyphId(int code) => 0;

    public override float GetWidthEm(int code) =>
        _codeWidthEm.TryGetValue(code, out float width) ? width : 0f;

    public override uint GetUnicode(int code) => 0;

    /// <summary>Gets the glyph name mapped to a code (null when undefined).</summary>
    public string? GetGlyphName(int code) => code >= 0 && code <= 255 ? _codeToName[code] : null;

    /// <summary>Gets the CharProcs stream of a code (null when undefined).</summary>
    public PdfStream? GetGlyphStream(int code)
    {
        string? name = GetGlyphName(code);
        return name is null ? null : Document.Resolve(_charProcs.Get(name)) as PdfStream;
    }

    private void BuildEncoding()
    {
        // Type3 fonts have no base encoding; only /Differences applies
        PdfDictionary? encodingDictionary = Document.Resolve(Dictionary.Get("Encoding")) as PdfDictionary;
        PdfArray? differences = encodingDictionary?.GetArray("Differences");

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

    private void BuildWidths()
    {
        // /Widths on Type3 fonts are in glyph space units (1/1000 of the glyph space by convention)
        PdfArray? widths = Dictionary.GetArray("Widths");
        double? firstChar = Dictionary.GetNumberValue("FirstChar");
        double? lastChar = Dictionary.GetNumberValue("LastChar");

        if (widths is null || firstChar is null || lastChar is null)
        {
            return;
        }

        for (int code = (int)firstChar.Value; code <= (int)lastChar.Value; code++)
        {
            int index = code - (int)firstChar.Value;
            if (index >= 0 && index < widths.Count)
            {
                float glyphSpaceWidth = widths.GetNumber(index)?.AsFloat() ?? 0f;
                _codeWidthEm[code] = glyphSpaceWidth * _glyphSpaceScale;
            }
        }
    }

    private static Matrix3x2? ParseMatrix(PdfArray? array)
    {
        if (array is null || array.Count < 6)
        {
            return null;
        }

        return new Matrix3x2(
            array.GetNumber(0)?.AsFloat() ?? 0f,
            array.GetNumber(1)?.AsFloat() ?? 0f,
            array.GetNumber(2)?.AsFloat() ?? 0f,
            array.GetNumber(3)?.AsFloat() ?? 0f,
            array.GetNumber(4)?.AsFloat() ?? 0f,
            array.GetNumber(5)?.AsFloat() ?? 0f);
    }
}
