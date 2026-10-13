using Poso.Pdf.Objects;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Type0 (composite/CID) font: codes are decoded through the CMap encoding (Identity or an
/// embedded CMap stream), widths come from /W on the descendant CIDFont, and Unicode mappings
/// come from the ToUnicode CMap.
/// </summary>
public sealed class Type0PdfFont : PdfFont
{
    private readonly CMap _encoding;
    private readonly ToUnicodeCMap? _toUnicode;
    private readonly PdfDictionary? _descendantFont;
    private readonly Dictionary<int, float> _cidWidthEm = new();
    private readonly float _defaultWidthEm;

    public Type0PdfFont(PdfDocument document, PdfDictionary dictionary, string baseFontName)
        : base(document, dictionary, baseFontName)
    {
        PdfObject? encodingObject = document.Resolve(dictionary.Get("Encoding"));
        _encoding = encodingObject switch
        {
            PdfStream stream => CMap.Parse(stream.Decode()) ?? CMap.Identity,
            PdfName => CMap.Identity,
            _ => CMap.Identity,
        };

        _descendantFont = (document.Resolve(dictionary.GetArray("DescendantFonts")?[0]) as PdfDictionary);
        _toUnicode = document.Resolve(dictionary.Get("ToUnicode")) is PdfStream toUnicodeStream
            ? ToUnicodeCMap.Parse(toUnicodeStream.Decode())
            : null;

        _defaultWidthEm = (float)((_descendantFont?.GetNumberValue("DW") ?? 1000d) / 1000d);
        BuildWidths();
    }

    public override bool IsSimpleFont => false;

    public override bool IsType0 => true;

    public override void DecodeCodes(ReadOnlySpan<byte> bytes, List<int> codes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            bool decoded = false;
            for (int length = _encoding.MaxCodeLength; length >= 1; length--)
            {
                if (i + length <= bytes.Length && _encoding.TryLookup(bytes.Slice(i, length), out int cid))
                {
                    codes.Add(cid);
                    i += length;
                    decoded = true;
                    break;
                }
            }

            if (!decoded)
            {
                // Undefined code: skip the byte (renders as notdef)
                codes.Add(bytes[i]);
                i++;
            }
        }
    }

    public override ushort GetGlyphId(int code) => (ushort)Math.Clamp(code, 0, ushort.MaxValue);

    public override float GetWidthEm(int code) =>
        _cidWidthEm.TryGetValue(code, out float width) ? width : _defaultWidthEm;

    public override uint GetUnicode(int code) => _toUnicode?.GetFirstUnit(code) ?? 0;

    /// <summary>Gets all Unicode code units for a code (used by text extraction).</summary>
    public bool TryGetUnicodeUnits(int code, out uint[] units)
    {
        if (_toUnicode is not null && _toUnicode.TryGetUnits(code, out uint[]? found))
        {
            units = found;
            return true;
        }

        units = Array.Empty<uint>();
        return false;
    }

    /// <summary>Parses the /W width array on the descendant CIDFont.</summary>
    private void BuildWidths()
    {
        PdfArray? widths = _descendantFont?.GetArray("W");
        if (widths is null)
        {
            return;
        }

        int i = 0;
        while (i < widths.Count)
        {
            if (widths[i] is PdfNumber first && widths[i + 1] is PdfArray array)
            {
                int startCid = first.AsInt();
                for (int k = 0; k < array.Count; k++)
                {
                    _cidWidthEm[startCid + k] = (array.GetNumber(k)?.AsFloat() ?? 0f) / 1000f;
                }

                i += 2;
            }
            else if (widths[i] is PdfNumber cidLow &&
                     widths[i + 1] is PdfNumber cidHigh &&
                     widths[i + 2] is PdfNumber width)
            {
                for (int cid = cidLow.AsInt(); cid <= cidHigh.AsInt(); cid++)
                {
                    _cidWidthEm[cid] = width.AsFloat() / 1000f;
                }

                i += 3;
            }
            else
            {
                i++;
            }
        }
    }
}
