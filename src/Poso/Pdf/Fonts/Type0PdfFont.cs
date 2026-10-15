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
    private readonly ushort[]? _cidToGid;
    private readonly Dictionary<int, float> _cidWidthEm = new();
    private readonly float _defaultWidthEm;
    private readonly bool _isVertical;

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

        // Vertical writing (WMode 2): either an embedded CMap declaring /WMode 2, or a predefined /
        // Identity CMap name ending in "-V" (e.g. Identity-V, UniGB-UCS2-V).
        _isVertical = encodingObject switch
        {
            PdfStream => _encoding.IsVertical,
            PdfName name => name.Value.EndsWith("-V", StringComparison.Ordinal),
            _ => false,
        };

        _descendantFont = DescendantFont;
        _toUnicode = document.Resolve(dictionary.Get("ToUnicode")) is PdfStream toUnicodeStream
            ? ToUnicodeCMap.Parse(toUnicodeStream.Decode())
            : null;

        // /CIDToGIDMap only applies to CIDFontType2 (TrueType collections); CIDFontType0 takes CID->GID
        // from the CFF program itself, which is addressed by the identity CID order here.
        string? descendantSubtype = _descendantFont?.GetNameValue("Subtype");
        if (descendantSubtype == "CIDFontType2")
        {
            _cidToGid = LoadCidToGidMap(document.Resolve(_descendantFont?.Get("CIDToGIDMap")) as PdfStream);
        }

        _defaultWidthEm = (float)((_descendantFont?.GetNumberValue("DW") ?? 1000d) / 1000d);
        BuildWidths();
    }

    /// <summary>Loads a /CIDToGIDMap stream: an array of 2-byte big-endian glyph IDs indexed by CID.</summary>
    private static ushort[]? LoadCidToGidMap(PdfStream? stream)
    {
        if (stream is null)
        {
            return null; // /Identity (or absent)
        }

        byte[] data = stream.Decode();
        int count = data.Length / 2;
        if (count == 0)
        {
            return null;
        }

        var map = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            map[i] = (ushort)((data[i * 2] << 8) | data[(i * 2) + 1]);
        }

        return map;
    }

    public override bool IsSimpleFont => false;

    public override bool IsType0 => true;

    public override bool IsVertical => _isVertical;

    // Vertical origin: the distance from the writing position down to the glyph baseline, i.e. the
    // vertical position vector VY. This is the descendant CIDFont's /DW2[0], defaulting to the PDF
    // spec value of 880 (thousandths of an em) - exactly what MuPDF uses (pdf_lookup_vmtx -> dvmtx.y),
    // not the FontDescriptor ascent.
    public override float VerticalOriginEm =>
        _isVertical ? (float)(((Document.Resolve(_descendantFont?.Get("DW2")) as PdfArray)?.GetNumber(0)?.Value ?? 880d) / 1000d) : 0f;

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

    public override ushort GetGlyphId(int code)
    {
        if (_cidToGid is { } map && code >= 0 && code < map.Length)
        {
            return map[code];
        }

        // A CIDFontType0 with an embedded CID-keyed CFF addresses glyphs by the CFF's own GID order,
        // which differs from the CID whenever the charset is not the identity mapping (embedded subsets).
        // Touching EmbeddedFontData lazily parses that program and fills EmbeddedCidToGid.
        if (_descendantFont?.GetNameValue("Subtype") == "CIDFontType0")
        {
            _ = EmbeddedFontData;
            if (EmbeddedCidToGid is { } cidMap)
            {
                return cidMap.TryGetValue(code, out ushort gid) ? gid : (ushort)0;
            }
        }

        return (ushort)Math.Clamp(code, 0, ushort.MaxValue);
    }

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
        PdfArray? widths = Document.Resolve(_descendantFont?.Get("W")) as PdfArray;
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
