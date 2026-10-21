using Poso.Graphics;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Core abstraction for a PDF font: resolves encodings, widths and Unicode mappings
/// (the PDF-side data) and delegates glyph rendering to <see cref="IFont"/> (the backend).
/// </summary>
public abstract class PdfFont
{
    protected PdfFont(PdfDocument document, PdfDictionary dictionary, string baseFontName)
    {
        Document = document;
        Dictionary = dictionary;
        BaseFontName = baseFontName;
    }

    public PdfDocument Document { get; }

    public PdfDictionary Dictionary { get; }

    public string BaseFontName { get; }

    /// <summary>
    /// Set the first time a render reports this font's fallback, so a document that draws the same font a
    /// thousand times yields one diagnostic rather than a thousand. Fonts are cached per document, which
    /// makes this a document-wide latch; a lost race only duplicates the report.
    /// </summary>
    internal int FallbackReported;

    /// <summary>True for simple (single-byte encoded) fonts.</summary>
    public abstract bool IsSimpleFont { get; }

    /// <summary>True for Type0 (CID) fonts.</summary>
    public virtual bool IsType0 => false;

    /// <summary>True for Type3 fonts.</summary>
    public virtual bool IsType3 => false;

    /// <summary>True when the font writes vertically (WMode 2). The interpreter advances glyphs along
    /// the writing axis instead of horizontally when this is set.</summary>
    public virtual bool IsVertical => false;

    /// <summary>
    /// The vertical origin (VY) in em: the distance from the writing position down to the glyph
    /// baseline in vertical mode. MuPDF (and the PDF vertical-writing model) place the em-cell top at
    /// the writing position, i.e. the baseline sits one ascent below it; 0 for horizontal fonts.
    /// </summary>
    public virtual float VerticalOriginEm => 0f;

    /// <summary>The graphics backend font (lazily created; null when unavailable).</summary>
    public IFont? GraphicsFont
    {
        get
        {
            if (_graphicsFontResolved)
            {
                return _graphicsFont;
            }

            _graphicsFont = CreateGraphicsFont();
            _graphicsFontResolved = true;
            return _graphicsFont;
        }
    }

    private IFont? _graphicsFont;
    private bool _graphicsFontResolved;

    /// <summary>Decodes string bytes into a code sequence (byte-wise for simple fonts, codespace-aware for CID fonts).</summary>
    public abstract void DecodeCodes(ReadOnlySpan<byte> bytes, List<int> codes);

    /// <summary>Maps a code to a glyph ID; 0 when unmappable.</summary>
    public abstract ushort GetGlyphId(int code);

    /// <summary>Maps a code to its advance width in em units (1.0 = full em).</summary>
    public abstract float GetWidthEm(int code);

    /// <summary>Maps a code to Unicode; 0 when unknown (callers fall back to the code value).</summary>
    public abstract uint GetUnicode(int code);

    /// <summary>True when word spacing applies to this code (single-byte simple fonts, code 32 only).</summary>
    public virtual bool AppliesWordSpacing(int code) => IsSimpleFont && code == 32;

    /// <summary>Type3 fonts: the interpreter reports the glyph width (em units) after executing the glyph procedure.</summary>
    internal virtual void SetType3GlyphWidth(int code, float widthEm)
    {
    }

    public override string ToString() => BaseFontName;

    // ---- Construction ----

    /// <summary>Creates a font from a resource dictionary.</summary>
    public static PdfFont Create(PdfDocument document, IGraphicsFactory factory, PdfDictionary dictionary)
    {
        string subtype = dictionary.GetNameValue("Subtype") ?? "Type1";
        string baseFontName = (dictionary.Get("BaseFont") as PdfName)?.Value ?? subtype;

        PdfFont font = subtype switch
        {
            "Type0" => new Type0PdfFont(document, dictionary, baseFontName),
            "Type3" => new Type3PdfFont(document, dictionary, baseFontName),
            _ => new SimplePdfFont(document, dictionary, baseFontName),
        };

        font.Factory = factory;
        return font;
    }

    protected IGraphicsFactory? Factory { get; private set; }

    /// <summary>The embedded name-keyed CFF program, used to resolve glyph names to glyph IDs; null when the font has none.</summary>
    protected CffProgram? EmbeddedGlyphNames { get; private set; }

    /// <summary>
    /// The CID to glyph-ID map from an embedded CID-keyed CFF (FontFile3 /CIDFontType0C), derived from its
    /// charset; null when the font has no such program. A Type 0 / Identity font addresses glyphs by CID,
    /// but the CFF numbers them by GID, so this map is what makes the right outline get drawn.
    /// </summary>
    protected Dictionary<int, ushort>? EmbeddedCidToGid { get; private set; }

    /// <summary>
    /// The code-to-name table a native Type 1 program carries in its own header. A symbolic font with no
    /// PDF /Encoding has no other source for it - the codes of such a face mean what the program says they
    /// mean - so this is what resolves them.
    /// </summary>
    protected string?[]? ProgramEncoding { get; private set; }

    /// <summary>The embedded font program prepared for a graphics backend (cached); null when the PDF embeds none.</summary>
    protected byte[]? EmbeddedFontData => _embeddedFontData ??= LoadEmbeddedFontData();

    private byte[]? _embeddedFontData;

    protected virtual IFont? CreateGraphicsFont()
    {
        if (Factory is null)
        {
            return null;
        }

        byte[]? data = EmbeddedFontData;
        IFont? face = Factory.CreateFont(data is null ? default : (ReadOnlySpan<byte>)data, BaseFontName);

        // A simple font addresses its glyphs by character, so a program with no character-map cannot be
        // drawn at all: the back end loads the face, hands back ids it then cannot rasterise, and the text
        // paints nothing (pdf.js TrueType_without_cmap.pdf). Substitute a face by the base font name - a
        // wrong glyph beats an invisible line. CID fonts are excluded: they address glyph ids directly and
        // legitimately ship without a cmap.
        if (face is { MapsCodePointsToGlyphs: false } && IsSimpleFont)
        {
            face.Dispose();
            face = Factory.CreateFont(default, BaseFontName);
        }

        return face;
    }

    private byte[]? LoadEmbeddedFontData()
    {
        if (!TryGetEmbeddedFontData(out byte[]? data) || data is not { Length: > 4 })
        {
            return null;
        }

        if (CffOpenType.TryExtractCff(data, out byte[]? wrappedCff) && wrappedCff is not null)
        {
            data = wrappedCff;
        }

        // A bare CFF (FontFile Type1, FontFile3 Type1C/CFF) is a glyph program, not a font container:
        // package it as OpenType so the back end can load it, and keep the parsed program around so
        // glyph names from the PDF encoding can be resolved to glyph IDs.
        if (data[0] == 1 && data[1] == 0 && data[2] == 4)
        {
            if (CffProgram.TryCreate(data, out CffProgram? program) && program is not null)
            {
                EmbeddedGlyphNames = program;
                return BuildCffContainer(data, program);
            }

            // A CID-keyed CFF (embedded CJK / Adobe-* fonts): the renderer parses its FDArray/FDSelect and
            // outlines directly once wrapped, so pass the bytes through unchanged and only keep the
            // charset-derived CID->GID map so the Type 0 codes address the right glyph.
            if (CidCffProgram.TryCreate(data, out CidCffProgram? cid) && cid is not null)
            {
                EmbeddedCidToGid = cid.CidToGid;
                return BuildCffContainer(data, cid);
            }

            return null;
        }

        // A real 'OTTO' container - one that carries a head table, so TryExtractCff left it alone - still
        // holds a CFF table, and a CID font can need the glyph names inside it. pdf.js issue7901.pdf embeds
        // one as a CIDFontType0 whose codes are numbered by the encoding while the CFF's own glyph order
        // starts at "space": without the names, code 1 (U+20AC) painted glyph 1 instead of the Euro. The
        // bytes themselves go to the back end untouched - it can read a finished container.
        if (data.Length > 12 && data[0] == 0x4F && data[1] == 0x54 && data[2] == 0x54 && data[3] == 0x4F &&
            EmbeddedCffTable(data) is { } embeddedCff &&
            CffProgram.TryCreate(embeddedCff, out CffProgram? embeddedProgram) && embeddedProgram is not null)
        {
            EmbeddedGlyphNames = embeddedProgram;
        }

        // A native binary Type 1 (FontFile) program is neither a container nor a CFF: convert its
        // charstrings to Type 2 and rebuild them as a CFF so it renders through the exact same path
        // as a Type1C font. Handle both name-keyed and CID-keyed (Adobe TN 5014) variants.
        if (data[0] == 0x25 /* '%' cleartext header */ || (data[0] == 0x80 && data[1] == 0x01 /* .pfb */))
        {
            string fontName = string.IsNullOrEmpty(BaseFontName) ? "PosoType1" : BaseFontName;
            if (Type1Program.TryCreate(data, out Type1Program? type1) && type1 is not null)
            {
                ProgramEncoding = type1.BuiltInEncoding;
                byte[] cff = Type1ToCff.Build(type1, fontName);
                if (CffProgram.TryCreate(cff, out CffProgram? program) && program is not null)
                {
                    EmbeddedGlyphNames = program;
                    return BuildCffContainer(cff, program);
                }
            }
            else if (Type1CidProgram.IsCidKeyedProgram(data)
                && Type1CidProgram.TryCreate(data, out Type1CidProgram? cid) && cid is not null)
            {
                // CID program: keep GID == CID (no synthetic .notdef) for the Type 0 Identity mapping.
                byte[] cff = Type1ToCff.Build(cid, fontName, prependNotdef: false);
                if (CffProgram.TryCreate(cff, out CffProgram? program) && program is not null)
                {
                    EmbeddedGlyphNames = program;
                    return BuildCffContainer(cff, program);
                }
            }
        }

        return data;
    }

    /// <summary>The bytes of an sfnt container's CFF table, or null when it has none.</summary>
    private static byte[]? EmbeddedCffTable(byte[] sfnt)
    {
        int count = (sfnt[4] << 8) | sfnt[5];
        if (sfnt.Length < 12 + (count * 16))
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            if (sfnt[entry] != (byte)'C' || sfnt[entry + 1] != (byte)'F' ||
                sfnt[entry + 2] != (byte)'F' || sfnt[entry + 3] != (byte)' ')
            {
                continue;
            }

            int offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(entry + 8));
            int length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(entry + 12));
            return offset > 0 && length > 0 && sfnt.Length >= offset + length
                ? sfnt[offset..(offset + length)]
                : null;
        }

        return null;
    }

    /// <summary>Wraps a bare CFF program into the OpenType container the back end loads, using the
    /// FontDescriptor ascent/descent for the vertical metrics.</summary>
    private byte[] BuildCffContainer(byte[] cff, ICffMetrics program)
    {
        PdfDictionary? descriptor = FontDescriptor;
        return CffOpenType.Build(
            cff,
            program,
            (float)(descriptor?.GetNumberValue("Ascent") ?? 750d),
            (float)(descriptor?.GetNumberValue("Descent") ?? -250d),
            string.IsNullOrEmpty(BaseFontName) ? "PosoFont" : BaseFontName);
    }

    /// <summary>The descendant CIDFont of a Type0 font. /DescendantFonts is legal as an indirect
    /// reference to the array as well as an inline array, so the value itself is resolved first.</summary>
    protected PdfDictionary? DescendantFont =>
        Document.Resolve(Dictionary.Get("DescendantFonts")) is PdfArray { Count: > 0 } array
            ? Document.Resolve(array[0]) as PdfDictionary
            : null;

    /// <summary>The FontDescriptor of this font; Type0 fonts keep it on their descendant.</summary>
    protected PdfDictionary? FontDescriptor =>
        Document.Resolve(Dictionary.Get("FontDescriptor")) as PdfDictionary
        ?? Document.Resolve(DescendantFont?.Get("FontDescriptor")) as PdfDictionary;

    /// <summary>Reads the embedded font program (FontFile / FontFile2 / FontFile3).</summary>
    protected bool TryGetEmbeddedFontData(out byte[]? data)
    {
        data = null;
        PdfDictionary? descriptor = FontDescriptor;
        if (descriptor is null)
        {
            return false;
        }

        foreach (string key in new[] { "FontFile", "FontFile2", "FontFile3" })
        {
            if (Document.Resolve(descriptor.Get(key)) is PdfStream stream)
            {
                data = stream.Decode();
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a width (in 1/1000 em) from the /Widths array using FirstChar/LastChar.</summary>
    protected float GetWidthFromWidths(int code)
    {
        double? firstChar = Dictionary.GetNumberValue("FirstChar");
        double? lastChar = Dictionary.GetNumberValue("LastChar");
        PdfArray? widths = Document.Resolve(Dictionary.Get("Widths")) as PdfArray;

        if (widths is not null && firstChar is not null && lastChar is not null && code >= firstChar && code <= lastChar)
        {
            int index = (int)code - (int)firstChar.Value;
            if (index >= 0 && index < widths.Count)
            {
                return widths.GetNumber(index)?.AsFloat() ?? 0f;
            }
        }

        return 0f;
    }

    /// <summary>Default width: /MissingWidth from the font descriptor, or 0.</summary>
    protected float GetMissingWidthEm() =>
        (float)((FontDescriptor?.GetNumberValue("MissingWidth") ?? 0d) / 1000d);
}
