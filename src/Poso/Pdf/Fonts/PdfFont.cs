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

    /// <summary>True for simple (single-byte encoded) fonts.</summary>
    public abstract bool IsSimpleFont { get; }

    /// <summary>True for Type0 (CID) fonts.</summary>
    public virtual bool IsType0 => false;

    /// <summary>True for Type3 fonts.</summary>
    public virtual bool IsType3 => false;

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
        return Factory.CreateFont(data is null ? default : (ReadOnlySpan<byte>)data, BaseFontName);
    }

    private byte[]? LoadEmbeddedFontData()
    {
        if (!TryGetEmbeddedFontData(out byte[]? data) || data is not { Length: > 4 })
        {
            return null;
        }

        // A bare CFF (FontFile Type1, FontFile3 Type1C/CFF) is a glyph program, not a font container:
        // package it as OpenType so the back end can load it, and keep the parsed program around so
        // glyph names from the PDF encoding can be resolved to glyph IDs.
        if (data[0] == 1 && data[1] == 0 && data[2] == 4)
        {
            if (!CffProgram.TryCreate(data, out CffProgram? program) || program is null)
            {
                return null;
            }

            EmbeddedGlyphNames = program;
            return BuildCffContainer(data, program);
        }

        // A native binary Type 1 (FontFile) program is neither a container nor a CFF: convert its
        // charstrings to Type 2 and rebuild them as a CFF so it renders through the exact same path
        // as a Type1C font. CID-keyed or malformed programs fall through to the plain-data return.
        if ((data[0] == 0x25 /* '%' cleartext header */ || (data[0] == 0x80 && data[1] == 0x01 /* .pfb */))
            && Type1Program.TryCreate(data, out Type1Program? type1) && type1 is not null)
        {
            byte[] cff = Type1ToCff.Build(type1, string.IsNullOrEmpty(BaseFontName) ? "PosoType1" : BaseFontName);
            if (CffProgram.TryCreate(cff, out CffProgram? program) && program is not null)
            {
                EmbeddedGlyphNames = program;
                return BuildCffContainer(cff, program);
            }
        }

        return data;
    }

    /// <summary>Wraps a bare CFF program into the OpenType container the back end loads, using the
    /// FontDescriptor ascent/descent for the vertical metrics.</summary>
    private byte[] BuildCffContainer(byte[] cff, CffProgram program)
    {
        PdfDictionary? descriptor = FontDescriptor;
        return CffOpenType.Build(
            cff,
            program,
            (float)(descriptor?.GetNumberValue("Ascent") ?? 750d),
            (float)(descriptor?.GetNumberValue("Descent") ?? -250d),
            string.IsNullOrEmpty(BaseFontName) ? "PosoFont" : BaseFontName);
    }

    /// <summary>The FontDescriptor of this font; Type0 fonts keep it on their descendant.</summary>
    protected PdfDictionary? FontDescriptor =>
        Document.Resolve(Dictionary.Get("FontDescriptor")) as PdfDictionary
        ?? Document.Resolve((Document.Resolve(Dictionary.GetArray("DescendantFonts")?[0]) as PdfDictionary)?.Get("FontDescriptor")) as PdfDictionary;

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
        PdfArray? widths = Dictionary.GetArray("Widths");

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
