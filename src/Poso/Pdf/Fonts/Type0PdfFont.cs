using Poso.Graphics;
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
    private readonly PredefinedCMap? _predefined;
    private readonly string? _ordering;
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
        _ordering = CidOrdering();
        _predefined = encodingObject is PdfName encodingName
            ? PredefinedCMap.ForName(encodingName.Value, _ordering)
            : null;
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
        if (_predefined is { } predefined)
        {
            // The predefined CMap fixes the byte layout even though its CID table is not available here:
            // a Shift-JIS or EUC string mixes one- and two-byte codes, and reading every pair as one code
            // turns "(57)" plus two kanji into four nonsense codes.
            int position = 0;
            while (position < bytes.Length)
            {
                int used = predefined.Decode(bytes, position, out int code);
                if (used == 0)
                {
                    position++;
                    continue;
                }

                codes.Add(code);
                position += used;
            }

            return;
        }

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

            // The program is a name-keyed CFF, so it has no CID space of its own - yet the PDF declares
            // this as a CID font and numbers its codes in the encoding's order, not the CFF's.
            // pdf.js issue7901.pdf is one: code 1 is U+20AC (Euro) while glyph 1 of the program is
            // "space", so the identity fallback below painted "üäÍ†Ë…" for every line. The glyph names
            // bridge the two: ToUnicode says which character a code stands for, the Adobe Glyph List
            // turns that into a glyph name, and the CFF charset turns the name into a glyph id.
            if (TryGetGlyphIdByName(code, out ushort named))
            {
                return named;
            }
        }

        // A font the document does not embed is drawn in a substitute face, and that face knows nothing
        // about the CID space: its glyphs are reached by character, so the code's Unicode value is the
        // only common ground. PDFium substitutes a CJK face for these fonts and paints them.
        if (!HasEmbeddedProgram && _predefined is { } predefined)
        {
            uint unicode = predefined.ToUnicode(code);
            if (unicode != 0 && GraphicsFont is { } face && face.GetGlyphId(unicode) is ushort substitute && substitute != 0)
            {
                return substitute;
            }
        }

        return (ushort)Math.Clamp(code, 0, ushort.MaxValue);
    }

    /// <summary>
    /// A non-embedded CID font has to draw in a system face, and the default substitute (Arial) has no
    /// CJK glyphs. The ordering names the collection: Adobe's GB1/Japan1/Korea1/CNS1 map onto the usual
    /// system families for each. A candidate the machine does not have is skipped by the callers, which
    /// compare the face's family name.
    /// </summary>
    protected override IFont? CreateGraphicsFont()
    {
        IFont? face = base.CreateGraphicsFont();
        if (face is null || HasEmbeddedProgram)
        {
            return face;
        }

        foreach (string family in SubstituteFamilies())
        {
            IFont? candidate = Factory?.CreateFont(default, family);
            if (candidate is null)
            {
                continue;
            }

            if (string.Equals(candidate.Name, family, StringComparison.OrdinalIgnoreCase))
            {
                face.Dispose();
                return candidate;
            }

            candidate.Dispose();
        }

        return face;
    }

    private static readonly Dictionary<string, string[]> CjkFamilies = new(StringComparer.Ordinal)
    {
        ["GB1"] = new[] { "Microsoft YaHei", "SimSun", "Noto Sans CJK SC", "Noto Sans SC", "PingFang SC", "WenQuanYi Zen Hei" },
        ["Japan1"] = new[] { "Yu Gothic", "MS Gothic", "Noto Sans CJK JP", "Noto Sans JP", "Hiragino Sans" },
        ["Korea1"] = new[] { "Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR", "Apple SD Gothic Neo" },
        ["CNS1"] = new[] { "Microsoft JhengHei", "PMingLiU", "Noto Sans CJK TC", "Noto Sans TC", "PingFang TC" },
    };

    private string[] SubstituteFamilies() =>
        _ordering is { } ordering && CjkFamilies.TryGetValue(ordering, out string[]? families) ? families : Array.Empty<string>();

    /// <summary>The descendant's /CIDSystemInfo /Ordering (Adobe-GB1/Japan1/Korea1/CNS1 and friends).</summary>
    private string? CidOrdering()
    {
        PdfDictionary? info = Document.Resolve(_descendantFont?.Get("CIDSystemInfo")) as PdfDictionary;
        return (Document.Resolve(info?.Get("Ordering")) as PdfString)?.AsTextString();
    }

    private Dictionary<uint, ushort>? _unicodeToGlyph;

    /// <summary>
    /// Resolves a code through its Unicode value and the embedded CFF's glyph names. Built lazily: a
    /// program addressed by CID never needs it.
    /// </summary>
    private bool TryGetGlyphIdByName(int code, out ushort glyphId)
    {
        glyphId = 0;
        if (EmbeddedGlyphNames is not { } program || _toUnicode is null)
        {
            return false;
        }

        // A space is the one code the embedded ToUnicode table is least reliable about: pdf.js
        // issue7901.pdf maps code 32 to U+00FE, so following it painted a thorn in every word break.
        // The PDF spec fixes the glyph name for a space, and the CFF charset resolves that exactly.
        if (code == 0x20 && program.TryGetGlyphId("space", out glyphId))
        {
            return true;
        }

        uint unicode = _toUnicode.GetFirstUnit(code);
        if (unicode == 0)
        {
            return false;
        }

        _unicodeToGlyph ??= BuildUnicodeToGlyph(program);
        return _unicodeToGlyph.TryGetValue(unicode, out glyphId);
    }

    /// <summary>Maps every code point the program names onto its glyph id; the first name wins.</summary>
    private static Dictionary<uint, ushort> BuildUnicodeToGlyph(CffProgram program)
    {
        var map = new Dictionary<uint, ushort>(program.GlyphCount);
        for (int gid = 0; gid < program.GlyphCount; gid++)
        {
            string? name = program.GetNameForGlyph(gid);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            uint unicode = AdobeGlyphList.GetUnicode(name!);
            if (unicode != 0)
            {
                map.TryAdd(unicode, (ushort)gid);
            }
        }

        return map;
    }

    public override float GetWidthEm(int code) =>
        _cidWidthEm.TryGetValue(code, out float width) ? width : _defaultWidthEm;

    /// <summary>
    /// A CID addresses a glyph of the descendant program, which numbers its glyphs in its own order:
    /// CIDFontType2 through the /CIDToGIDMap, CIDFontType0 through the charset of its CID-keyed CFF.
    /// Without that step the outline of the wrong glyph would be painted at the right position.
    /// </summary>
    internal override int ResolveOutlineGlyph(OutlineSource outlines, int code)
    {
        if (_cidToGid is { } map && code >= 0 && code < map.Length)
        {
            return map[code];
        }

        if (_descendantFont?.GetNameValue("Subtype") == "CIDFontType0")
        {
            _ = EmbeddedFontData;
            if (EmbeddedCidToGid is { } cidMap && cidMap.TryGetValue(code, out ushort gid))
            {
                return gid;
            }

            // A name-keyed program used as a CID font: bridge code to glyph through its Unicode value
            // and the program's own charset, the same route GetGlyphId takes for the back end. The glyph
            // ids agree because both readers walk the same charset.
            if (TryGetGlyphIdByName(code, out ushort named))
            {
                return named;
            }

            return _toUnicode is null ? code : -1;
        }

        return code;
    }

    public override uint GetUnicode(int code) => _toUnicode?.GetFirstUnit(code) ?? _predefined?.ToUnicode(code) ?? 0;

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
