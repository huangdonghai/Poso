using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf.Fonts;
using Poso.Pdf.Images;
using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;

namespace Poso.Pdf.Content;

/// <summary>
/// Receives glyphs during interpretation with their user-space text matrix
/// (used by the text extraction layer).
/// </summary>
public interface ITextSink
{
    void OnGlyph(PdfFont font, int code, ushort glyphId, uint[] unicodeUnits, Matrix3x2 textToUser, float x, float fontSize, float advanceEm);
}

/// <summary>
/// PDF content stream interpreter (PDF 32000-1:2008 chapters 8-9): executes the graphics state,
/// path, text, image and XObject operators of a page, form XObject or Type3 glyph procedure,
/// issuing all drawing through <see cref="IGraphicsContext"/>.
/// </summary>
public sealed class PdfContentInterpreter
{
    private const int MaxFormDepth = 12;

    private readonly PdfDocument _document;
    private readonly IGraphicsFactory _factory;
    private readonly IGraphicsContext _context;
    private readonly PdfRenderOptions _options;
    private readonly Stack<GraphicsState> _stateStack = new();
    private readonly Dictionary<int, PdfFont> _fonts = new();
    private GraphicsState _state = new();
    private int _formDepth;

    // Current path (user space)
    private PathBuilder? _path;
    private float _currentX;
    private float _currentY;
    private float _startX;
    private float _startY;
    private bool _hasCurrentPoint;

    // Text matrices
    private Matrix3x2 _textMatrix = Matrix3x2.Identity;
    private Matrix3x2 _textLineMatrix = Matrix3x2.Identity;

    // Fill/stroke color spaces
    private ColorSpaceKind _fillSpace = ColorSpaceKind.DeviceGray;
    private ColorSpaceKind _strokeSpace = ColorSpaceKind.DeviceGray;
    private int _fillComponents = 1;
    private int _strokeComponents = 1;
    private byte[]? _fillLookup;
    private byte[]? _strokeLookup;
    private int _fillLookupComponents;
    private int _strokeLookupComponents;

    /// <summary>Width reported by the d0/d1 operator of a Type3 glyph procedure (glyph space units).</summary>
    public float Type3GlyphWidthGlyphSpace { get; private set; }

    /// <summary>Optional text extraction sink.</summary>
    public ITextSink? TextSink { get; set; }

    public PdfContentInterpreter(
        PdfDocument document,
        IGraphicsFactory factory,
        IGraphicsContext context,
        PdfRenderOptions options)
    {
        _document = document;
        _factory = factory;
        _context = context;
        _options = options;
    }

    /// <summary>Renders a whole page: sets up the base transform and executes its content streams.</summary>
    public void RenderPage(PdfPage page)
    {
        _state = new GraphicsState();
        _stateStack.Clear();
        _path = null;
        _hasCurrentPoint = false;

        float rotation = NormalizeRotation((int)(page.Rotation + _options.RotationDegrees));
        float scale = _options.Scale;
        float cropX = page.CropBox.X;
        float cropY = page.CropBox.Y;
        float cropWidth = page.CropBox.Width;
        float cropHeight = page.CropBox.Height;

        // Translation that brings the rotated page into the positive quadrant (y-up space)
        float tx, ty;
        switch (rotation)
        {
            case 90:
                tx = 0;
                ty = cropWidth * scale;
                break;
            case 180:
                tx = cropWidth * scale;
                ty = cropHeight * scale;
                break;
            case 270:
                tx = cropHeight * scale;
                ty = 0;
                break;
            default:
                tx = 0;
                ty = 0;
                break;
        }

        float deviceHeight = rotation is 90 or 270 ? cropWidth * scale : cropHeight * scale;

        // user space (y-up) -> device pixels (y-down):
        // crop translate, scale, clockwise rotation (PDF /Rotate is clockwise on screen),
        // y-flip, then translate into the positive quadrant
        Matrix3x2 baseMatrix =
            Matrix3x2.CreateTranslation(-cropX, -cropY) *
            Matrix3x2.CreateScale(scale, scale) *
            Matrix3x2.CreateRotation(-rotation * MathF.PI / 180f) *
            Matrix3x2.CreateTranslation(tx, ty) *
            Matrix3x2.CreateScale(1f, -1f) *
            Matrix3x2.CreateTranslation(0f, deviceHeight);

        _state.Ctm = baseMatrix;
        _context.SetMatrix(baseMatrix);

        if (_options.Background is { } background)
        {
            _context.Clear(background);
        }

        PdfDictionary? resources = page.Resources;
        ExecuteContent(page.GetContent(), resources);
    }

    /// <summary>Executes a content stream with the given resources (page, form or Type3 glyph).</summary>
    public void ExecuteContent(byte[] content, PdfDictionary? resources)
    {
        if (content.Length == 0)
        {
            return;
        }

        var lexer = new PdfLexer(content);
        var operands = new Stack<PdfObject>();

        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            switch (token.Kind)
            {
                case PdfTokenKind.Integer:
                    operands.Push(new PdfNumber(token.IntegerValue, true));
                    break;

                case PdfTokenKind.Real:
                    operands.Push(new PdfNumber(token.RealValue, false));
                    break;

                case PdfTokenKind.Name:
                    operands.Push(new PdfName(PdfName.Decode(lexer.Data, token.Start, token.Length)));
                    break;

                case PdfTokenKind.LiteralString:
                    operands.Push(new PdfString(PdfString.DecodeLiteral(lexer.Data, token.Start, token.Length)));
                    break;

                case PdfTokenKind.HexString:
                    operands.Push(new PdfString(PdfString.DecodeHex(lexer.Data, token.Start, token.Length)));
                    break;

                case PdfTokenKind.ArrayOpen:
                case PdfTokenKind.DictOpen:
                {
                    // Nested objects: hand over to the full parser
                    int rewind = token.Kind == PdfTokenKind.ArrayOpen ? 1 : 2;
                    var parser = new PdfParser(content, lexer.Position - rewind);
                    PdfObject value = parser.ParseObject();
                    lexer.Position = parser.Position;
                    operands.Push(value);
                    break;
                }

                case PdfTokenKind.Keyword:
                    if (lexer.Match(token, "BI"u8))
                    {
                        ExecuteInlineImage(lexer, operands);
                    }
                    else
                    {
                        ExecuteOperator(lexer, token, operands, resources);
                    }

                    break;
            }
        }
    }

    // ---- Operator dispatch ----

    private void ExecuteOperator(PdfLexer lexer, PdfToken token, Stack<PdfObject> operands, PdfDictionary? resources)
    {
        // Graphics state
        if (lexer.Match(token, "q"u8))
        {
            _context.Save();
            _stateStack.Push(_state.Clone());
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Q"u8))
        {
            if (_stateStack.Count > 0)
            {
                _state = _stateStack.Pop();
                _context.Restore();
            }

            operands.Clear();
            return;
        }

        if (lexer.Match(token, "cm"u8))
        {
            float f = PopNumber(operands);
            float e = PopNumber(operands);
            float d = PopNumber(operands);
            float c = PopNumber(operands);
            float b = PopNumber(operands);
            float a = PopNumber(operands);
            var matrix = new Matrix3x2(a, b, c, d, e, f);
            _state.Ctm = matrix * _state.Ctm;
            _context.ConcatMatrix(matrix);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "w"u8))
        {
            _state.Stroke = _state.Stroke with { Width = PopNumber(operands) };
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "J"u8))
        {
            _state.Stroke = _state.Stroke with { Cap = (LineCap)(int)PopNumber(operands) };
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "j"u8))
        {
            _state.Stroke = _state.Stroke with { Join = (LineJoin)(int)PopNumber(operands) };
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "M"u8))
        {
            _state.Stroke = _state.Stroke with { MiterLimit = PopNumber(operands) };
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "d"u8))
        {
            float phase = PopNumber(operands);
            PdfArray? dash = PopArray(operands);
            _state.Stroke = _state.Stroke with
            {
                DashArray = dash is null ? null : ToFloatMemory(dash),
                DashPhase = phase,
            };
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "ri"u8) || lexer.Match(token, "i"u8))
        {
            operands.Clear(); // Rendering intent / flatness: ignored
            return;
        }

        if (lexer.Match(token, "gs"u8))
        {
            PdfName? name = PopName(operands);
            PdfDictionary? extGState = ResolveResource(resources, "ExtGState", name) as PdfDictionary;
            if (extGState is not null)
            {
                if (extGState.GetNumberValue("LW") is double lineWidth)
                {
                    _state.Stroke = _state.Stroke with { Width = (float)lineWidth };
                }

                if (extGState.GetNumberValue("ca") is double fillAlpha)
                {
                    _state.FillAlpha = (float)fillAlpha;
                }

                if (extGState.GetNumberValue("CA") is double strokeAlpha)
                {
                    _state.StrokeAlpha = (float)strokeAlpha;
                }
            }

            operands.Clear();
            return;
        }

        // Path construction
        if (lexer.Match(token, "m"u8))
        {
            float y = PopNumber(operands);
            float x = PopNumber(operands);
            MoveTo(x, y);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "l"u8))
        {
            float y = PopNumber(operands);
            float x = PopNumber(operands);
            LineTo(x, y);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "c"u8))
        {
            float y3 = PopNumber(operands);
            float x3 = PopNumber(operands);
            float y2 = PopNumber(operands);
            float x2 = PopNumber(operands);
            float y1 = PopNumber(operands);
            float x1 = PopNumber(operands);
            CurveTo(x1, y1, x2, y2, x3, y3);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "v"u8))
        {
            float y3 = PopNumber(operands);
            float x3 = PopNumber(operands);
            float y2 = PopNumber(operands);
            float x2 = PopNumber(operands);
            CurveTo(_currentX, _currentY, x2, y2, x3, y3);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "y"u8))
        {
            float y3 = PopNumber(operands);
            float x3 = PopNumber(operands);
            float y1 = PopNumber(operands);
            float x1 = PopNumber(operands);
            CurveTo(x1, y1, x3, y3, x3, y3);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "h"u8))
        {
            CloseSubpath();
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "re"u8))
        {
            float height = PopNumber(operands);
            float width = PopNumber(operands);
            float y = PopNumber(operands);
            float x = PopNumber(operands);
            MoveTo(x, y);
            LineTo(x + width, y);
            LineTo(x + width, y + height);
            LineTo(x, y + height);
            CloseSubpath();
            operands.Clear();
            return;
        }

        // Path painting
        if (lexer.Match(token, "S"u8))
        {
            PaintPath(fill: false, stroke: true, evenOdd: false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "s"u8))
        {
            CloseSubpath();
            PaintPath(fill: false, stroke: true, evenOdd: false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "f"u8) || lexer.Match(token, "F"u8))
        {
            PaintPath(fill: true, stroke: false, evenOdd: false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "f*"u8))
        {
            PaintPath(fill: true, stroke: false, evenOdd: true);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "B"u8))
        {
            PaintPath(fill: true, stroke: true, evenOdd: false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "B*"u8))
        {
            PaintPath(fill: true, stroke: true, evenOdd: true);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "b"u8))
        {
            CloseSubpath();
            PaintPath(fill: true, stroke: true, evenOdd: false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "b*"u8))
        {
            CloseSubpath();
            PaintPath(fill: true, stroke: true, evenOdd: true);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "n"u8))
        {
            ClearPath();
            operands.Clear();
            return;
        }

        // Clipping
        if (lexer.Match(token, "W"u8))
        {
            ApplyClip(evenOdd: false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "W*"u8))
        {
            ApplyClip(evenOdd: true);
            operands.Clear();
            return;
        }

        // Text objects
        if (lexer.Match(token, "BT"u8))
        {
            _textMatrix = Matrix3x2.Identity;
            _textLineMatrix = _textMatrix;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "ET"u8))
        {
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tc"u8))
        {
            _state.CharSpacing = PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tw"u8))
        {
            _state.WordSpacing = PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tz"u8))
        {
            _state.HorizontalScale = PopNumber(operands) / 100f;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "TL"u8))
        {
            _state.Leading = -PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Ts"u8))
        {
            _state.TextRise = PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tr"u8))
        {
            _state.RenderMode = (TextRenderMode)(int)PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tf"u8))
        {
            float fontSize = PopNumber(operands);
            PdfName? fontName = PopName(operands);
            _state.Font = ResolveFont(resources, fontName);
            _state.FontSize = fontSize;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Td"u8))
        {
            float ty = PopNumber(operands);
            float tx = PopNumber(operands);
            _textMatrix = Matrix3x2.CreateTranslation(tx, ty) * _textLineMatrix;
            _textLineMatrix = _textMatrix;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "TD"u8))
        {
            float ty = PopNumber(operands);
            float tx = PopNumber(operands);
            _state.Leading = -ty;
            _textMatrix = Matrix3x2.CreateTranslation(tx, ty) * _textLineMatrix;
            _textLineMatrix = _textMatrix;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tm"u8))
        {
            float f = PopNumber(operands);
            float e = PopNumber(operands);
            float d = PopNumber(operands);
            float c = PopNumber(operands);
            float b = PopNumber(operands);
            float a = PopNumber(operands);
            _textMatrix = new Matrix3x2(a, b, c, d, e, f);
            _textLineMatrix = _textMatrix;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "T*"u8))
        {
            _textMatrix = Matrix3x2.CreateTranslation(0f, _state.Leading) * _textLineMatrix;
            _textLineMatrix = _textMatrix;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "Tj"u8))
        {
            ShowText(PopString(operands));
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "TJ"u8))
        {
            PdfArray? array = PopArray(operands);
            if (array is not null)
            {
                foreach (PdfObject element in array)
                {
                    switch (element)
                    {
                        case PdfString text:
                            ShowText(text);
                            break;

                        case PdfNumber number:
                            float displacement = -(number.AsFloat() / 1000f) * _state.FontSize * _state.HorizontalScale;
                            _textMatrix = Matrix3x2.CreateTranslation(displacement, 0f) * _textMatrix;
                            break;
                    }
                }
            }

            operands.Clear();
            return;
        }

        if (lexer.Match(token, "'"u8))
        {
            PdfString? text = PopString(operands);
            _textMatrix = Matrix3x2.CreateTranslation(0f, _state.Leading) * _textLineMatrix;
            _textLineMatrix = _textMatrix;
            if (text is not null)
            {
                ShowText(text);
            }

            operands.Clear();
            return;
        }

        if (lexer.Match(token, "\""u8))
        {
            PdfString? text = PopString(operands);
            float charSpacing = PopNumber(operands);
            float wordSpacing = PopNumber(operands);
            _state.WordSpacing = wordSpacing;
            _state.CharSpacing = charSpacing;
            _textMatrix = Matrix3x2.CreateTranslation(0f, _state.Leading) * _textLineMatrix;
            _textLineMatrix = _textMatrix;
            if (text is not null)
            {
                ShowText(text);
            }

            operands.Clear();
            return;
        }

        // Type3 glyph metrics
        if (lexer.Match(token, "d0"u8))
        {
            PopNumber(operands); // wy (unused)
            Type3GlyphWidthGlyphSpace = PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "d1"u8))
        {
            PopNumber(operands); // lly
            PopNumber(operands); // llx
            PopNumber(operands); // ury
            PopNumber(operands); // urx
            PopNumber(operands); // wy
            Type3GlyphWidthGlyphSpace = PopNumber(operands);
            operands.Clear();
            return;
        }

        // Colors
        if (lexer.Match(token, "g"u8))
        {
            _fillSpace = ColorSpaceKind.DeviceGray;
            _fillComponents = 1;
            _fillLookup = null;
            _state.FillColor = PosoColor.FromGray(PopNumber(operands));
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "G"u8))
        {
            _strokeSpace = ColorSpaceKind.DeviceGray;
            _strokeComponents = 1;
            _strokeLookup = null;
            _state.StrokeColor = PosoColor.FromGray(PopNumber(operands));
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "rg"u8))
        {
            float b = PopNumber(operands);
            float g = PopNumber(operands);
            float r = PopNumber(operands);
            _fillSpace = ColorSpaceKind.DeviceRgb;
            _fillComponents = 3;
            _fillLookup = null;
            _state.FillColor = PosoColor.FromRgb(r, g, b);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "RG"u8))
        {
            float b = PopNumber(operands);
            float g = PopNumber(operands);
            float r = PopNumber(operands);
            _strokeSpace = ColorSpaceKind.DeviceRgb;
            _strokeComponents = 3;
            _strokeLookup = null;
            _state.StrokeColor = PosoColor.FromRgb(r, g, b);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "k"u8))
        {
            float k = PopNumber(operands);
            float y = PopNumber(operands);
            float m = PopNumber(operands);
            float c = PopNumber(operands);
            _fillSpace = ColorSpaceKind.DeviceCmyk;
            _fillComponents = 4;
            _fillLookup = null;
            _state.FillColor = PosoColor.FromCmyk(c, m, y, k);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "K"u8))
        {
            float k = PopNumber(operands);
            float y = PopNumber(operands);
            float m = PopNumber(operands);
            float c = PopNumber(operands);
            _strokeSpace = ColorSpaceKind.DeviceCmyk;
            _strokeComponents = 4;
            _strokeLookup = null;
            _state.StrokeColor = PosoColor.FromCmyk(c, m, y, k);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "cs"u8) || lexer.Match(token, "CS"u8))
        {
            PdfObject? space = operands.Count > 0 ? operands.Pop() : null;
            bool stroke = lexer.Match(token, "CS"u8);
            SetColorSpace(space, resources, stroke);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "sc"u8) || lexer.Match(token, "scn"u8))
        {
            ApplyColorOperands(operands, _fillSpace, _fillComponents, _fillLookup, _fillLookupComponents, out _state.FillColor);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "SC"u8) || lexer.Match(token, "SCN"u8))
        {
            ApplyColorOperands(operands, _strokeSpace, _strokeComponents, _strokeLookup, _strokeLookupComponents, out _state.StrokeColor);
            operands.Clear();
            return;
        }

        // XObjects
        if (lexer.Match(token, "Do"u8))
        {
            PdfName? name = PopName(operands);
            PdfDictionary? xobjects = resources?.GetDictionary("XObject");
            PdfStream? xobject = _document.Resolve(xobjects?.Get(name?.Value ?? string.Empty)) as PdfStream;

            if (xobject is not null)
            {
                switch (xobject.Dictionary.GetNameValue("Subtype"))
                {
                    case "Image":
                        DrawImage(xobject);
                        break;

                    case "Form":
                        ExecuteForm(xobject);
                        break;
                }
            }

            operands.Clear();
            return;
        }

        if (lexer.Match(token, "sh"u8))
        {
            throw new PdfUnsupportedException("Shading operators (sh) are not supported yet (planned for v0.2)");
        }

        // Marked content and compatibility operators: operands are consumed, no effect
        if (lexer.Match(token, "BMC"u8) || lexer.Match(token, "BDC"u8) ||
            lexer.Match(token, "EMC"u8) || lexer.Match(token, "MP"u8) ||
            lexer.Match(token, "DP"u8) || lexer.Match(token, "BX"u8) ||
            lexer.Match(token, "EX"u8))
        {
            operands.Clear();
            return;
        }

        // Unknown operator: ignore
        operands.Clear();
    }

    // ---- Text ----

    private void ShowText(PdfString? text)
    {
        if (text is null)
        {
            return;
        }

        PdfFont? font = _state.Font;
        if (font is null)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = text.Value;
        if (bytes.Length == 0)
        {
            return;
        }

        float fontSize = _state.FontSize;

        // TsMatrix = Scale(Tfs*Th, Tfs) * Translate(0, rise)
        Matrix3x2 tsMatrix =
            Matrix3x2.CreateScale(fontSize * _state.HorizontalScale, fontSize) *
            Matrix3x2.CreateTranslation(0f, _state.TextRise);
        Matrix3x2 textToUser = tsMatrix * _textMatrix;

        var codes = new List<int>(bytes.Length);
        font.DecodeCodes(bytes, codes);
        if (codes.Count == 0)
        {
            return;
        }

        GlyphPosition[] glyphs = new GlyphPosition[codes.Count];
        int glyphCount = 0;
        float tx = 0f;
        bool draw = fontSize != 0f && _state.RenderMode != TextRenderMode.Invisible;

        foreach (int code in codes)
        {
            float widthEm;

            if (font is Type3PdfFont type3Font)
            {
                // Type3 glyphs are drawn immediately at the current text matrix
                widthEm = ExecuteType3Glyph(type3Font, code, tsMatrix * _textMatrix);
            }
            else
            {
                widthEm = font.GetWidthEm(code);
                ushort glyphId = font.GetGlyphId(code);
                if (glyphId != 0)
                {
                    glyphs[glyphCount++] = new GlyphPosition(glyphId, tx, 0f);
                }

                TextSink?.OnGlyph(font, code, glyphId, GetUnicodeUnits(font, code), textToUser, tx, fontSize, widthEm);
            }

            float advance = (widthEm * fontSize) + _state.CharSpacing;
            if (font.AppliesWordSpacing(code))
            {
                advance += _state.WordSpacing;
            }

            advance *= _state.HorizontalScale;
            tx += advance;

            if (font is Type3PdfFont)
            {
                // Advance the text matrix per glyph (Type3 widths come from d0)
                _textMatrix = Matrix3x2.CreateTranslation(advance, 0f) * _textMatrix;
                textToUser = tsMatrix * _textMatrix;
            }
        }

        if (draw && glyphCount > 0 && font.GraphicsFont is { } graphicsFont)
        {
            _context.Save();
            _context.ConcatMatrix(tsMatrix * _textMatrix);
            _context.DrawGlyphs(graphicsFont, fontSize, glyphs.AsSpan(0, glyphCount), FillPaintColor(), _state.RenderMode, _state.Stroke);
            _context.Restore();
        }

        if (font is not Type3PdfFont)
        {
            _textMatrix = Matrix3x2.CreateTranslation(tx, 0f) * _textMatrix;
        }
    }

    private float ExecuteType3Glyph(Type3PdfFont font, int code, Matrix3x2 textSpaceMatrix)
    {
        PdfStream? glyphStream = font.GetGlyphStream(code);
        if (glyphStream is null)
        {
            return 0f;
        }

        Type3GlyphWidthGlyphSpace = 0f;
        _context.Save();
        // canvas = FontMatrix * textSpaceMatrix * CTM
        _context.ConcatMatrix(font.FontMatrix * textSpaceMatrix);
        ExecuteContent(glyphStream.Decode(), font.Resources);
        _context.Restore();

        return Type3GlyphWidthGlyphSpace * Math.Abs(font.FontMatrix.M11);
    }

    private static uint[] GetUnicodeUnits(PdfFont font, int code)
    {
        if (font is Type0PdfFont type0 && type0.TryGetUnicodeUnits(code, out uint[] units))
        {
            return units;
        }

        uint single = font.GetUnicode(code);
        return single != 0 ? new[] { single } : Array.Empty<uint>();
    }

    private PdfFont? ResolveFont(PdfDictionary? resources, PdfName? fontName)
    {
        if (fontName is null || resources is null)
        {
            return null;
        }

        PdfObject? fontObject = resources.GetDictionary("Font")?.Get(fontName.Value);
        PdfIndirectRef? reference = fontObject as PdfIndirectRef;

        if (reference is not null && _fonts.TryGetValue(reference.ObjectNumber, out PdfFont? cached))
        {
            return cached;
        }

        PdfDictionary? dictionary = _document.Resolve(fontObject) as PdfDictionary;
        if (dictionary is null)
        {
            return null;
        }

        PdfFont font = PdfFont.Create(_document, _factory, dictionary);
        if (reference is not null)
        {
            _fonts[reference.ObjectNumber] = font;
        }

        return font;
    }

    // ---- Paths ----

    private void MoveTo(float x, float y)
    {
        _path ??= new PathBuilder();
        _path.MoveTo(x, y);
        _currentX = x;
        _currentY = y;
        _startX = x;
        _startY = y;
        _hasCurrentPoint = true;
    }

    private void LineTo(float x, float y)
    {
        if (!_hasCurrentPoint)
        {
            return;
        }

        _path!.LineTo(x, y);
        _currentX = x;
        _currentY = y;
    }

    private void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        if (!_hasCurrentPoint)
        {
            return;
        }

        _path!.CubicTo(x1, y1, x2, y2, x3, y3);
        _currentX = x3;
        _currentY = y3;
    }

    private void CloseSubpath()
    {
        if (!_hasCurrentPoint)
        {
            return;
        }

        _path!.Close();
        _currentX = _startX;
        _currentY = _startY;
    }

    private void PaintPath(bool fill, bool stroke, bool evenOdd)
    {
        if (_path is { IsEmpty: false } && (fill || stroke))
        {
            IPath path = _path.Build();
            if (fill)
            {
                _context.FillPath(path, FillPaintColor(), evenOdd);
            }

            if (stroke)
            {
                _context.StrokePath(path, _state.Stroke, StrokePaintColor());
            }
        }

        ClearPath();
    }

    private void ApplyClip(bool evenOdd)
    {
        if (_path is { IsEmpty: false })
        {
            _context.ClipPath(_path.Build(), evenOdd);
        }
    }

    private void ClearPath()
    {
        _path = null;
        _hasCurrentPoint = false;
    }

    // ---- Color spaces ----

    private void SetColorSpace(PdfObject? space, PdfDictionary? resources, bool stroke)
    {
        PdfObject? resolved = space;

        if (space is PdfName name && resources?.GetDictionary("ColorSpace") is { } resourceSpaces &&
            resourceSpaces.Get(name.Value) is { } resourceSpace)
        {
            resolved = _document.Resolve(resourceSpace);
        }
        else
        {
            resolved = _document.Resolve(space);
        }

        ColorSpaceKind kind = ColorSpaceKind.Unknown;
        int components = 1;
        byte[]? lookup = null;
        int lookupComponents = 3;

        switch (resolved)
        {
            case PdfName spaceName:
                (kind, components) = spaceName.Value switch
                {
                    "DeviceGray" or "G" or "CalGray" => (ColorSpaceKind.DeviceGray, 1),
                    "DeviceRGB" or "RGB" or "CalRGB" => (ColorSpaceKind.DeviceRgb, 3),
                    "DeviceCMYK" or "CMYK" => (ColorSpaceKind.DeviceCmyk, 4),
                    "Pattern" => (ColorSpaceKind.Pattern, 1),
                    _ => (ColorSpaceKind.Unknown, 1),
                };
                break;

            case PdfArray array when array.Count >= 2 && array[0] is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased":
                        int n = Math.Max(1, (int)((_document.Resolve(array[1]) as PdfStream)?.Dictionary.GetIntegerValue("N") ?? 3));
                        (kind, components) = n switch
                        {
                            1 => (ColorSpaceKind.DeviceGray, 1),
                            3 => (ColorSpaceKind.DeviceRgb, 3),
                            4 => (ColorSpaceKind.DeviceCmyk, 4),
                            _ => (ColorSpaceKind.Unknown, n),
                        };
                        break;

                    case "Indexed" or "I":
                        kind = ColorSpaceKind.Indexed;
                        components = 1;
                        lookupComponents = ComponentsOfName(_document.Resolve(array[1]) as PdfName);
                        lookup = (_document.Resolve(array[3]) as PdfString)?.Bytes ?? Array.Empty<byte>();
                        break;

                    case "Separation":
                        // Approximated as gray (first component)
                        (kind, components) = (ColorSpaceKind.DeviceGray, 1);
                        break;

                    case "DeviceN":
                        components = array.Count - 1;
                        kind = ColorSpaceKind.Unknown;
                        break;
                }

                break;
        }

        if (stroke)
        {
            _strokeSpace = kind;
            _strokeComponents = components;
            _strokeLookup = lookup;
            _strokeLookupComponents = lookupComponents;
        }
        else
        {
            _fillSpace = kind;
            _fillComponents = components;
            _fillLookup = lookup;
            _fillLookupComponents = lookupComponents;
        }
    }

    private void ApplyColorOperands(
        Stack<PdfObject> operands,
        ColorSpaceKind kind,
        int components,
        byte[]? lookup,
        int lookupComponents,
        out PosoColor color)
    {
        if (kind == ColorSpaceKind.Pattern)
        {
            throw new PdfUnsupportedException("Pattern color spaces are not supported yet (planned for v0.2)");
        }

        int count = Math.Max(1, components);
        Span<float> values = stackalloc float[count];
        for (int i = count - 1; i >= 0; i--)
        {
            values[i] = PopNumber(operands);
        }

        color = kind switch
        {
            ColorSpaceKind.DeviceGray => PosoColor.FromGray(values[0]),
            ColorSpaceKind.DeviceRgb => PosoColor.FromRgb(values[0], values[1], values[2]),
            ColorSpaceKind.DeviceCmyk => PosoColor.FromCmyk(values[0], values[1], values[2], values[3]),
            ColorSpaceKind.Indexed => IndexedColor(lookup, lookupComponents, values[0]),
            _ => PosoColor.Black,
        };
    }

    private static PosoColor IndexedColor(byte[]? lookup, int lookupComponents, float index)
    {
        if (lookup is null || lookup.Length == 0 || lookupComponents <= 0)
        {
            return PosoColor.Black;
        }

        int maxIndex = (lookup.Length / lookupComponents) - 1;
        int clamped = (int)MathF.Round(Math.Clamp(index, 0f, maxIndex));
        int offset = clamped * lookupComponents;

        return lookupComponents switch
        {
            1 => PosoColor.FromGray(lookup[offset] / 255f),
            3 => PosoColor.FromRgb(lookup[offset] / 255f, lookup[offset + 1] / 255f, lookup[offset + 2] / 255f),
            4 => PosoColor.FromCmyk(lookup[offset] / 255f, lookup[offset + 1] / 255f, lookup[offset + 2] / 255f, lookup[offset + 3] / 255f),
            _ => PosoColor.Black,
        };
    }

    private static int ComponentsOfName(PdfName? name) => name?.Value switch
    {
        "DeviceGray" or "G" or "CalGray" => 1,
        "DeviceRGB" or "RGB" or "CalRGB" => 3,
        "DeviceCMYK" or "CMYK" => 4,
        _ => 3,
    };

    private PosoColor FillPaintColor() =>
        _state.FillColor.WithAlpha(ScaleAlpha(_state.FillColor.A, _state.FillAlpha));

    private PosoColor StrokePaintColor() =>
        _state.StrokeColor.WithAlpha(ScaleAlpha(_state.StrokeColor.A, _state.StrokeAlpha));

    private static byte ScaleAlpha(byte alpha, float alphaScale) =>
        (byte)Math.Clamp((int)MathF.Round(alpha * alphaScale), 0, 255);

    // ---- XObjects and images ----

    private void ExecuteForm(PdfStream form)
    {
        if (_formDepth >= MaxFormDepth)
        {
            throw new PdfFormatException("Form XObject nesting is too deep (possible cycle)");
        }

        _formDepth++;
        try
        {
            // A form XObject behaves as if its content were wrapped in q/Q
            _context.Save();
            _stateStack.Push(_state.Clone());

            PdfDictionary dictionary = form.Dictionary;

            if (dictionary.GetArray("Matrix") is { Count: >= 6 } matrixArray)
            {
                var matrix = new Matrix3x2(
                    matrixArray.GetNumber(0)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(1)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(2)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(3)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(4)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(5)?.AsFloat() ?? 0f);
                _state.Ctm = matrix * _state.Ctm;
                _context.ConcatMatrix(matrix);
            }

            if (dictionary.GetArray("BBox") is { Count: >= 4 } bbox)
            {
                RectF rect = RectF.FromLbrb(
                    bbox.GetNumber(0)?.AsFloat() ?? 0f,
                    bbox.GetNumber(1)?.AsFloat() ?? 0f,
                    bbox.GetNumber(2)?.AsFloat() ?? 0f,
                    bbox.GetNumber(3)?.AsFloat() ?? 0f);
                _context.ClipRect(rect);
            }

            PdfDictionary? formResources = _document.Resolve(dictionary.Get("Resources")) as PdfDictionary;
            ExecuteContent(form.Decode(), formResources);

            _context.Restore();
            _state = _stateStack.Pop();
        }
        finally
        {
            _formDepth--;
        }
    }

    private void DrawImage(PdfStream image)
    {
        IImage? decoded = PdfImageDecoder.Decode(_document, _factory, image, image.Dictionary, _state.FillColor);
        if (decoded is null)
        {
            return;
        }

        try
        {
            // Image XObjects are painted into the unit square in user space
            _context.DrawImage(
                decoded,
                new RectF(0f, 0f, 1f, 1f),
                new RectF(0f, 0f, decoded.Width, decoded.Height),
                _state.FillAlpha);
        }
        finally
        {
            decoded.Dispose();
        }
    }

    // ---- Inline images (BI/ID/EI) ----

    private void ExecuteInlineImage(PdfLexer lexer, Stack<PdfObject> operands)
    {
        var dictionary = new Dictionary<string, PdfObject>(StringComparer.Ordinal);

        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && lexer.Match(token, "ID"u8))
            {
                break;
            }

            if (token.Kind != PdfTokenKind.Name)
            {
                continue;
            }

            string key = PdfName.Decode(lexer.Data, token.Start, token.Length);
            PdfObject? value = ReadInlineValue(lexer);
            if (value is not null && value is not PdfNull)
            {
                dictionary[key] = value;
            }
        }

        // Binary data runs until whitespace followed by "EI"
        int dataStart = lexer.Position;
        int dataEnd = FindInlineImageEnd(lexer.Data, dataStart);
        byte[] raw = lexer.Data.AsSpan(dataStart, dataEnd - dataStart).ToArray();

        int eI = PdfParser.IndexOfKeyword(lexer.Data, dataEnd, "EI"u8);
        lexer.Position = eI >= 0 ? eI + 2 : lexer.Data.Length;

        IImage? decoded = PdfImageDecoder.DecodeInline(_document, _factory, new PdfDictionary(dictionary), raw, _state.FillColor);
        if (decoded is not null)
        {
            try
            {
                _context.DrawImage(
                    decoded,
                    new RectF(0f, 0f, 1f, 1f),
                    new RectF(0f, 0f, decoded.Width, decoded.Height),
                    _state.FillAlpha);
            }
            finally
            {
                decoded.Dispose();
            }
        }

        operands.Clear();
    }

    private PdfObject? ReadInlineValue(PdfLexer lexer)
    {
        PdfToken token = lexer.NextToken();
        switch (token.Kind)
        {
            case PdfTokenKind.Integer:
                return new PdfNumber(token.IntegerValue, true);

            case PdfTokenKind.Real:
                return new PdfNumber(token.RealValue, false);

            case PdfTokenKind.Name:
                return new PdfName(PdfName.Decode(lexer.Data, token.Start, token.Length));

            case PdfTokenKind.LiteralString:
                return new PdfString(PdfString.DecodeLiteral(lexer.Data, token.Start, token.Length));

            case PdfTokenKind.HexString:
                return new PdfString(PdfString.DecodeHex(lexer.Data, token.Start, token.Length));

            case PdfTokenKind.ArrayOpen:
            {
                var parser = new PdfParser(lexer.Data, lexer.Position - 1);
                PdfObject value = parser.ParseObject();
                lexer.Position = parser.Position;
                return value;
            }

            default:
                return null;
        }
    }

    private static int FindInlineImageEnd(byte[] data, int from)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] is (byte)'\0' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)' ' &&
                data[i + 1] == (byte)'E' &&
                data[i + 2] == (byte)'I')
            {
                char next = i + 3 < data.Length ? (char)data[i + 3] : ' ';
                if (PdfLexer.IsWhiteSpace((byte)next) || PdfLexer.IsDelimiter((byte)next) || i + 3 >= data.Length)
                {
                    return i;
                }
            }
        }

        return data.Length;
    }

    // ---- Helpers ----

    private static PdfObject? ResolveResource(PdfDictionary? resources, string category, PdfName? name)
    {
        if (name is null || resources is null)
        {
            return null;
        }

        return resources.GetDictionary(category)?.Get(name.Value);
    }

    private static float PopNumber(Stack<PdfObject> operands) =>
        operands.Count > 0 && operands.Pop() is PdfNumber number ? number.AsFloat() : 0f;

    private static PdfName? PopName(Stack<PdfObject> operands) =>
        operands.Count > 0 ? operands.Pop() as PdfName : null;

    private static PdfString? PopString(Stack<PdfObject> operands) =>
        operands.Count > 0 ? operands.Pop() as PdfString : null;

    private static PdfArray? PopArray(Stack<PdfObject> operands) =>
        operands.Count > 0 ? operands.Pop() as PdfArray : null;

    private static ReadOnlyMemory<float> ToFloatMemory(PdfArray array)
    {
        var values = new float[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return values;
    }

    private static int NormalizeRotation(int rotation)
    {
        int normalized = ((rotation % 360) + 360) % 360;
        return normalized switch
        {
            0 or 90 or 180 or 270 => normalized,
            < 45 => 0,
            < 135 => 90,
            < 225 => 180,
            _ => 270,
        };
    }
}
