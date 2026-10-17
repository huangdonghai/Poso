using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf.Colors;
using Poso.Pdf.Fonts;
using Poso.Pdf.Functions;
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
    private readonly PdfDiagnostics _diag;
    private readonly Stack<GraphicsState> _stateStack = new();
    private readonly Dictionary<int, PdfFont> _fonts = new();

    // Rendered ExtGState soft masks, keyed by mask stream and size, released when the page finishes.
    private readonly Dictionary<(PdfDictionary Mask, int Width, int Height), IImage?> _softMasks = new();
    private GraphicsState _state = new();
    private int _formDepth;
    private Matrix3x2 _pageBaseMatrix;
    private readonly Stack<bool> _marks = new();
    private bool _suppressContent;

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
    private IccProfile? _fillIcc;
    private IccProfile? _strokeIcc;

    // Scratch reused across text-showing ops to avoid a per-op List allocation (the hot path for a
    // text-dense page). Only ever live during a non-Type3 ShowText loop, which does not recurse.
    private readonly List<int> _codesScratch = new();
    private PdfFunction? _fillTint;
    private PdfFunction? _strokeTint;
    private ColorSpaceKind _fillBaseSpace = ColorSpaceKind.DeviceGray;
    private ColorSpaceKind _strokeBaseSpace = ColorSpaceKind.DeviceGray;
    private int _fillBaseComponents = 1;
    private int _strokeBaseComponents = 1;
    private Shading.PdfShading? _fillShading;
    private Shading.PdfShading? _strokeShading;
    private Matrix3x2 _fillShadingMatrix = Matrix3x2.Identity;
    private Matrix3x2 _strokeShadingMatrix = Matrix3x2.Identity;
    private PdfStream? _fillTiling;
    private Matrix3x2 _fillTilingMatrix = Matrix3x2.Identity;
    private float[] _fillLabWhite = DefaultLabWhitePoint;
    private float[] _fillLabRange = DefaultLabRange;
    private float[] _strokeLabWhite = DefaultLabWhitePoint;
    private float[] _strokeLabRange = DefaultLabRange;
    private CalColor _fillCal = new();
    private CalColor _strokeCal = new();

    private static readonly float[] DefaultLabWhitePoint = { 0.9505f, 1f, 1.089f };
    private static readonly float[] DefaultLabRange = { -100f, 100f, -100f, 100f };

    /// <summary>Width reported by the d0/d1 operator of a Type3 glyph procedure (glyph space units).</summary>
    public float Type3GlyphWidthGlyphSpace { get; private set; }

    /// <summary>Optional text extraction sink.</summary>
    public ITextSink? TextSink { get; set; }

    /// <summary>
    /// Optional callback invoked for every unsupported feature that is skipped during a lenient
    /// render (never called in strict mode, where such features throw instead).
    /// </summary>
    public Action<PdfUnsupportedFeature>? OnUnsupported { get; set; }

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
        _diag = new PdfDiagnostics(options.Strict, feature => OnUnsupported?.Invoke(feature));
    }

    /// <summary>Renders a whole page: sets up the base transform and executes its content streams.</summary>
    public void RenderPage(PdfPage page)
    {
        _state = new GraphicsState();
        _stateStack.Clear();
        _path = null;
        _hasCurrentPoint = false;

        float rotation = NormalizeRotation((int)(page.Rotation + _options.RotationDegrees));
        float cropX = page.CropBox.X;
        float cropY = page.CropBox.Y;
        float cropWidth = page.CropBox.Width;
        float cropHeight = page.CropBox.Height;

        // /UserUnit rescales the box coordinates into default user space, so it belongs on the same
        // leg of the transform as the render scale.
        float unit = page.UserUnit * _options.Scale;

        // Translation that brings the rotated page into the positive quadrant (y-up space)
        float tx, ty;
        switch (rotation)
        {
            case 90:
                tx = 0;
                ty = cropWidth * unit;
                break;
            case 180:
                tx = cropWidth * unit;
                ty = cropHeight * unit;
                break;
            case 270:
                tx = cropHeight * unit;
                ty = 0;
                break;
            default:
                tx = 0;
                ty = 0;
                break;
        }

        float deviceHeight = rotation is 90 or 270 ? cropWidth * unit : cropHeight * unit;

        // user space (y-up) -> device pixels (y-down):
        // crop translate, scale, clockwise rotation (PDF /Rotate is clockwise on screen),
        // y-flip, then translate into the positive quadrant
        Matrix3x2 baseMatrix =
            Matrix3x2.CreateTranslation(-cropX, -cropY) *
            Matrix3x2.CreateScale(unit, unit) *
            Matrix3x2.CreateRotation(-rotation * MathF.PI / 180f) *
            Matrix3x2.CreateTranslation(tx, ty) *
            Matrix3x2.CreateScale(1f, -1f) *
            Matrix3x2.CreateTranslation(0f, deviceHeight);

        _state.Ctm = baseMatrix;
        _pageBaseMatrix = baseMatrix;
        _context.SetMatrix(baseMatrix);

        if (_options.Background is { } background)
        {
            _context.Clear(background);
        }

        // The page's content is one transparency group, and a group starts from an empty backdrop: the
        // background only appears *underneath* the finished group. Clearing to the background instead
        // gives every non-normal blend mode a white backdrop to blend against, and max(white, source)
        // is white - which erases the wash of a cover that paints it with /BM /Lighten.
        _context.PushLayer(null, 255, BlendMode.Normal, null);
        try
        {
            ExecuteContent(page.GetContent(_diag), page.Resources);
        }
        finally
        {
            // The group sits one level below anything the page's content leaked; without that the
            // pending Restore pops the leak instead of the group and the page never composites out.
            BalanceCanvasSaves(1);
            _context.PopLayer();
            foreach (IImage? image in _softMasks.Values)
            {
                image?.Dispose();
            }

            _softMasks.Clear();
        }
    }

    /// <summary>
    /// Renders a single tiling-pattern cell into this interpreter's context using the supplied base
    /// transform (pattern space -> tile pixels). Resets local state so the cell is self-contained.
    /// </summary>
    public void RenderCell(byte[] content, PdfDictionary? resources, Matrix3x2 baseMatrix)
    {
        _state = new GraphicsState();
        _stateStack.Clear();
        _path = null;
        _hasCurrentPoint = false;
        _marks.Clear();
        _suppressContent = false;
        _state.Ctm = baseMatrix;
        _pageBaseMatrix = baseMatrix;
        _context.SetMatrix(baseMatrix);
        ExecuteContent(content, resources);
    }

    /// <summary>
    /// Renders the page's annotations on top of the content. Appearance streams (/AP) are composited
    /// like form XObjects mapped into the annotation rectangle; markup annotations
    /// (highlight/underline/strikeout/squiggly) without an appearance are drawn from their quadrilateral
    /// points. Link/Popup annotations and hidden ones are skipped.
    /// </summary>
    public void RenderAnnotations(PdfPage page)
    {
        if (_document.Resolve(page.Dictionary.Get("Annots")) is not PdfArray annots)
        {
            return;
        }

        foreach (PdfObject item in annots)
        {
            if (_document.Resolve(item) is not PdfDictionary annot)
            {
                continue;
            }

            string? subtype = annot.GetNameValue("Subtype");
            if (subtype is null or "Link" or "Popup")
            {
                continue;
            }

            long flags = annot.GetIntegerValue("F") ?? 0;
            if ((flags & 0x03) != 0) // Invisible | Hidden
            {
                continue;
            }

            RectF rect = ParseRectangle(annot.GetArray("Rect"));
            if (rect.IsEmpty)
            {
                continue;
            }

            float opacity = (float)(annot.GetNumberValue("CA") ?? 1d);

            PdfStream? appearance = ResolveAppearance(annot);
            if (appearance is not null)
            {
                RenderAppearance(appearance, rect, opacity);
                continue;
            }

            PosoColor color = ParseColor(annot.Get("C"), subtype == "Highlight" ? new PosoColor(255, 255, 0) : PosoColor.Red);
            RenderMarkupFallback(subtype, annot, rect, color, opacity);
        }
    }

    private PdfStream? ResolveAppearance(PdfDictionary annot)
    {
        if (_document.Resolve(annot.Get("AP")) is not PdfDictionary ap)
        {
            return null;
        }

        PdfObject? candidate = ap.Get("N") ?? ap.Get("D");
        return _document.Resolve(candidate) as PdfStream;
    }

    private void RenderAppearance(PdfStream appearance, RectF rect, float opacity)
    {
        _context.Save();
        _stateStack.Push(_state.Clone());
        try
        {
            RectF bbox = ParseRectangle(appearance.Dictionary.GetArray("BBox"));
            if (bbox.IsEmpty)
            {
                return;
            }

            float sx = rect.Width / bbox.Width;
            float sy = rect.Height / bbox.Height;
            Matrix3x2 toRect = new(sx, 0f, 0f, sy, rect.X - bbox.X * sx, rect.Y - bbox.Y * sy);
            Matrix3x2 ctm = toRect * _pageBaseMatrix;
            _state.Ctm = ctm;
            _context.SetMatrix(ctm);
            _state.FillAlpha *= opacity;
            _state.StrokeAlpha *= opacity;
            _context.ClipRect(bbox);

            PdfDictionary? resources = _document.Resolve(appearance.Dictionary.Get("Resources")) as PdfDictionary;
            ExecuteContent(appearance.Decode(_diag), resources);
        }
        finally
        {
            _context.Restore();
            _state = _stateStack.Pop();
        }
    }

    private void RenderMarkupFallback(string subtype, PdfDictionary annot, RectF rect, PosoColor color, float opacity)
    {
        List<RectF> quads = ParseQuadPoints(annot.GetArray("QuadPoints"));
        if (quads.Count == 0)
        {
            quads.Add(rect);
        }

        // Highlights are translucent so the text beneath stays legible; the rest are opaque marks.
        byte alpha = (byte)Math.Clamp((int)MathF.Round(opacity * (subtype == "Highlight" ? 0.4f : 1f) * 255f), 0, 255);
        PosoColor paint = color.WithAlpha(alpha);

        _context.SetMatrix(_pageBaseMatrix);

        foreach (RectF quad in quads)
        {
            RectF bar = subtype switch
            {
                "Underline" => new RectF(quad.X, quad.Y, quad.Width, Math.Max(1f, quad.Height * 0.1f)),
                "StrikeOut" or "Squiggly" => new RectF(quad.X, quad.Y + (quad.Height * 0.4f), quad.Width, Math.Max(1f, quad.Height * 0.1f)),
                _ => quad,
            };

            var path = new PathBuilder();
            path.MoveTo(bar.X, bar.Y);
            path.LineTo(bar.X + bar.Width, bar.Y);
            path.LineTo(bar.X + bar.Width, bar.Y + bar.Height);
            path.LineTo(bar.X, bar.Y + bar.Height);
            path.Close();

            _context.FillPath(path.Build(), paint, false);
        }
    }

    private static RectF ParseRectangle(PdfArray? array)
    {
        if (array is not { Count: >= 4 })
        {
            return RectF.Empty;
        }

        return RectF.FromLbrb(
            array.GetNumber(0)?.AsFloat() ?? 0f,
            array.GetNumber(1)?.AsFloat() ?? 0f,
            array.GetNumber(2)?.AsFloat() ?? 0f,
            array.GetNumber(3)?.AsFloat() ?? 0f);
    }

    private static List<RectF> ParseQuadPoints(PdfArray? array)
    {
        var result = new List<RectF>();
        if (array is null)
        {
            return result;
        }

        for (int i = 0; i + 7 < array.Count; i += 8)
        {
            float x1 = array.GetNumber(i)?.AsFloat() ?? 0f;
            float y1 = array.GetNumber(i + 1)?.AsFloat() ?? 0f;
            float x2 = array.GetNumber(i + 2)?.AsFloat() ?? 0f;
            float y2 = array.GetNumber(i + 3)?.AsFloat() ?? 0f;
            float x3 = array.GetNumber(i + 4)?.AsFloat() ?? 0f;
            float y3 = array.GetNumber(i + 5)?.AsFloat() ?? 0f;
            float x4 = array.GetNumber(i + 6)?.AsFloat() ?? 0f;
            float y4 = array.GetNumber(i + 7)?.AsFloat() ?? 0f;
            float left = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
            float right = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
            float bottom = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
            float top = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
            result.Add(RectF.FromLbrb(left, bottom, right, top));
        }

        return result;
    }

    private PosoColor ParseColor(PdfObject? colorObject, PosoColor fallback)
    {
        if (_document.Resolve(colorObject) is not PdfArray array || array.Count == 0)
        {
            return fallback;
        }

        var values = new float[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return values.Length switch
        {
            1 => PosoColor.FromGray(values[0]),
            3 => PosoColor.FromRgb(values[0], values[1], values[2]),
            4 => PosoColor.FromCmyk(values[0], values[1], values[2], values[3]),
            _ => fallback,
        };
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
                    try
                    {
                        operands.Push(parser.ParseObject());
                    }
                    catch (PdfFormatException)
                    {
                        // A content stream that is not really content - an undecodable filter left the
                        // compressed bytes in place - is full of tokens no operand can be. MuPDF carries
                        // on past them; stopping here would lose the whole page.
                    }

                    lexer.Position = Math.Max(lexer.Position, parser.Position);
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
                // Canvas save/restore does not track the paint blend mode, so re-sync it from the
                // restored graphics state.
                _context.SetBlendMode(_state.BlendMode);
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
            PdfDictionary? extGState = _document.Resolve(ResolveResource(resources, "ExtGState", name)) as PdfDictionary;
            if (extGState is not null)
            {
                ApplyExtGState(extGState);
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
            _context.OpenTextClip();
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "ET"u8))
        {
            // The glyphs a text object accumulated are one clip, applied now.
            _context.CloseTextClip();
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
            // TD is equivalent to "tx ty Td" plus "-ty TL" (the leading parameter TL = -ty). This class
            // stores _state.Leading as -TL (the y-step T* / ' / " apply), so Leading = ty, not -ty.
            _state.Leading = ty;
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
                            // In vertical writing the TJ kerning displacement advances along the writing axis.
                            _textMatrix = (_state.Font?.IsVertical == true
                                ? Matrix3x2.CreateTranslation(0f, displacement)
                                : Matrix3x2.CreateTranslation(displacement, 0f)) * _textMatrix;
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

        // Colors. Each of these names a non-pattern space, so it also deselects any pattern.
        if (lexer.Match(token, "g"u8))
        {
            _fillSpace = ColorSpaceKind.DeviceGray;
            _fillComponents = 1;
            _fillLookup = null;
            _fillIcc = null;
            ClearPatternColor(stroke: false);
            _state.FillColor = PosoColor.FromGray(PopNumber(operands));
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "G"u8))
        {
            _strokeSpace = ColorSpaceKind.DeviceGray;
            _strokeComponents = 1;
            _strokeLookup = null;
            _strokeIcc = null;
            ClearPatternColor(stroke: true);
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
            _fillIcc = null;
            ClearPatternColor(stroke: false);
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
            _strokeIcc = null;
            ClearPatternColor(stroke: true);
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
            _fillIcc = null;
            ClearPatternColor(stroke: false);
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
            _strokeIcc = null;
            ClearPatternColor(stroke: true);
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
            if (_fillSpace == ColorSpaceKind.Pattern)
            {
                SelectPattern(operands, resources, stroke: false);
            }
            else
            {
                ClearPatternColor(stroke: false);
                ApplyColorOperands(operands, _fillSpace, _fillComponents, _fillLookup, _fillLookupComponents, _fillTint, _fillBaseSpace, _fillBaseComponents, _fillLabWhite, _fillLabRange, _fillCal, _fillIcc, out _state.FillColor);
            }

            operands.Clear();
            return;
        }

        if (lexer.Match(token, "SC"u8) || lexer.Match(token, "SCN"u8))
        {
            if (_strokeSpace == ColorSpaceKind.Pattern)
            {
                SelectPattern(operands, resources, stroke: true);
            }
            else
            {
                ClearPatternColor(stroke: true);
                ApplyColorOperands(operands, _strokeSpace, _strokeComponents, _strokeLookup, _strokeLookupComponents, _strokeTint, _strokeBaseSpace, _strokeBaseComponents, _strokeLabWhite, _strokeLabRange, _strokeCal, _strokeIcc, out _state.StrokeColor);
            }

            operands.Clear();
            return;
        }

        // XObjects
        if (lexer.Match(token, "Do"u8))
        {
            PdfName? name = PopName(operands);
            // Any resource value may be written as an indirect reference, /XObject included.
            PdfDictionary? xobjects = _document.Resolve(resources?.Get("XObject")) as PdfDictionary;
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
            PaintShadingResource(operands, resources);
            operands.Clear();
            return;
        }

        // Marked content and compatibility operators.
        if (lexer.Match(token, "BDC"u8))
        {
            BeginMark(IsMarkHidden(operands, resources));
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "BMC"u8) || lexer.Match(token, "BX"u8))
        {
            BeginMark(false);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "EMC"u8) || lexer.Match(token, "EX"u8))
        {
            EndMark();
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "MP"u8) || lexer.Match(token, "DP"u8))
        {
            // A single marked-content item has no paired EMC; nothing to scope out.
            operands.Clear();
            return;
        }

        // Unknown operator: ignore
        operands.Clear();
    }

    // ---- Extended graphics state ----

    private void ApplyExtGState(PdfDictionary extGState)
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

        if (extGState.Get("BM") is { } blendObject)
        {
            _state.BlendMode = ParseBlendMode(_document.Resolve(blendObject));
        }

        _state.AlphaIsolate = extGState.GetBooleanValue("AIS") ?? _state.AlphaIsolate;
        _state.TextKnockout = extGState.GetBooleanValue("TK") ?? _state.TextKnockout;

        // /SMask is a Mask dictionary whose /G names the group to mask with, or the name /None.
        _state.SoftMask = _document.Resolve(extGState.Get("SMask")) as PdfDictionary;

        _context.SetBlendMode(_state.BlendMode);
    }

    private static BlendMode ParseBlendMode(PdfObject? value) => (value switch
    {
        PdfName name => name.Value,
        PdfArray array when array.Count >= 1 => (array[0] as PdfName)?.Value,
        _ => null,
    }) switch
    {
        "Multiply" => BlendMode.Multiply,
        "Screen" => BlendMode.Screen,
        "Overlay" => BlendMode.Overlay,
        "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten,
        "ColorBurn" or "ColourBurn" => BlendMode.ColorBurn,
        "ColorDodge" or "ColourDodge" => BlendMode.ColorDodge,
        "HardLight" => BlendMode.HardLight,
        "SoftLight" => BlendMode.SoftLight,
        "Difference" => BlendMode.Difference,
        "Exclusion" => BlendMode.Exclusion,
        "Luminosity" => BlendMode.Luminosity,
        "Hue" => BlendMode.Hue,
        "Saturation" => BlendMode.Saturation,
        "Color" or "Colour" => BlendMode.Color,
        _ => BlendMode.Normal,
    };

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

        // Reuse a scratch buffer for the decoded codes to avoid a List allocation per text-showing op.
        // Type3 glyphs execute a nested charproc that can itself show text, so those take a fresh list;
        // every other font's loop is recursion-free.
        List<int> codes = font is Type3PdfFont ? new List<int>(bytes.Length) : _codesScratch;
        codes.Clear();
        font.DecodeCodes(bytes, codes);
        if (codes.Count == 0)
        {
            return;
        }

        Span<GlyphPosition> glyphs = codes.Count <= 512
            ? stackalloc GlyphPosition[codes.Count]
            : new GlyphPosition[codes.Count];
        int glyphCount = 0;
        float tx = 0f;
        // Glyph positions and the text sink are expressed in text space (em units): they are
        // transformed by tsMatrix, which already scales by the font size. Feeding them the user-space
        // pen (widthEm * fontSize) would apply the font size twice, so spacing blows up for Tf != 1.
        // tx stays in user space and is only used to advance the text matrix between show-text ops.
        float penEm = 0f;
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
                    // Vertical writing (WMode 2): glyphs stay upright but advance downward along the
                    // writing axis (negative text-space Y), rather than left-to-right along X. Mirroring
                    // MuPDF's vertical position vector v = (h.w/2, DW2y): the glyph is shifted left by
                    // half its horizontal advance to centre it on the vertical baseline (v.x), and the
                    // baseline is dropped one VY so the em-cell top sits at the pen.
                    glyphs[glyphCount++] = font.IsVertical
                        ? new GlyphPosition(glyphId, -widthEm * 0.5f, -penEm - font.VerticalOriginEm)
                        : new GlyphPosition(glyphId, penEm, 0f);
                }

                TextSink?.OnGlyph(font, code, glyphId, GetUnicodeUnits(font, code), textToUser, penEm, fontSize, widthEm);
            }

            float wordSpacing = font.AppliesWordSpacing(code) ? _state.WordSpacing : 0f;
            float advance = (widthEm * fontSize) + _state.CharSpacing + wordSpacing;
            advance *= _state.HorizontalScale;
            tx += advance;

            if (fontSize != 0f)
            {
                // Text-space advance: the em width plus spacing converted back to em (tsMatrix re-applies
                // the font size and horizontal scale, so neither belongs here).
                penEm += widthEm + (_state.CharSpacing + wordSpacing) / fontSize;
            }

            if (font is Type3PdfFont)
            {
                // Advance the text matrix per glyph (Type3 widths come from d0)
                _textMatrix = Matrix3x2.CreateTranslation(advance, 0f) * _textMatrix;
                textToUser = tsMatrix * _textMatrix;
            }
        }

        if (draw && glyphCount > 0 && !_suppressContent && font.GraphicsFont is { } graphicsFont)
        {
            if (graphicsFont.UsedFallback && System.Threading.Interlocked.Exchange(ref font.FallbackReported, 1) == 0)
            {
                // The PDF carried a glyph program the back end could not load. Text still draws, but in a
                // face with different coverage, so a script the substitute lacks comes out as .notdef boxes.
                _diag.Report(PdfUnsupportedKind.FontFallback, $"'{font.BaseFontName}': its embedded program could not be loaded and text drew in '{graphicsFont.Name}'");
            }

            Matrix3x2 drawMatrix = tsMatrix * _textMatrix;
            _context.ConcatMatrix(drawMatrix);

            // The line width and dash lengths are specified in PDF user space, but the glyph outlines
            // are handed to the back end in em space under a matrix that also scales by the font size
            // (Tf * Tm). Divide them by that em-to-user scale so a stroked-text pass (Tr 1/2/3) keeps
            // its user-space weight; otherwise a large Tm scale fattens the outline into a blob.
            StrokeStyle textStroke = _state.Stroke;
            float sx = MathF.Sqrt(drawMatrix.M11 * drawMatrix.M11 + drawMatrix.M12 * drawMatrix.M12);
            float sy = MathF.Sqrt(drawMatrix.M21 * drawMatrix.M21 + drawMatrix.M22 * drawMatrix.M22);
            float textScale = MathF.Sqrt(sx * sy);
            if (textScale > 1e-6f && textScale != 1f)
            {
                textStroke = textStroke with
                {
                    Width = textStroke.Width / textScale,
                    DashArray = textStroke.DashArray is { } dash ? ToScaledDash(dash, textScale) : null,
                };
            }

            if (_state.RenderMode is TextRenderMode.FillClip or TextRenderMode.StrokeClip
                or TextRenderMode.FillStrokeClip or TextRenderMode.Clip)
            {
                _context.AddTextClipRun(graphicsFont, glyphs.Slice(0, glyphCount), drawMatrix);
            }

            _context.DrawGlyphs(graphicsFont, fontSize, glyphs.Slice(0, glyphCount), FillPaintColor(), StrokePaintColor(), _state.RenderMode, textStroke);

            // Reset the drawing CTM to the current graphics state without popping the state stack:
            // a clip set by a text rendering mode (Tr 4-7) must persist until the next Q, while the
            // transient text matrix must not leak into subsequent operators.
            _context.SetMatrix(_state.Ctm);
        }

        if (font is not Type3PdfFont)
        {
            // Advance the text matrix along the writing axis: right for horizontal, down for vertical.
            _textMatrix = (font.IsVertical
                ? Matrix3x2.CreateTranslation(0f, -tx)
                : Matrix3x2.CreateTranslation(tx, 0f)) * _textMatrix;
        }
    }

    /// <summary>Scales a dash pattern from user space into the em/text space a glyph outline is drawn in.</summary>
    private static ReadOnlyMemory<float> ToScaledDash(ReadOnlyMemory<float> dash, float scale)
    {
        float[] scaled = new float[dash.Length];
        for (int i = 0; i < scaled.Length; i++)
        {
            scaled[i] = dash.Span[i] / scale;
        }

        return scaled;
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
        ExecuteContent(glyphStream.Decode(_diag), font.Resources);
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

        PdfObject? fontObject = (_document.Resolve(resources.Get("Font")) as PdfDictionary)?.Get(fontName.Value);
        PdfIndirectRef? reference = fontObject as PdfIndirectRef;
        PdfDictionary? dictionary = _document.Resolve(fontObject) as PdfDictionary;
        if (dictionary is null)
        {
            return null;
        }

        // Type3 fonts accumulate mutable glyph-width state while their charprocs run, so keep them
        // per-interpreter. Every other font is parsed + realised once and shared across the document:
        // the interpreter is rebuilt per page render, so a per-interpreter cache alone would re-parse
        // every font on every page (the dominant cost for font-heavy documents).
        bool isType3 = dictionary.GetNameValue("Subtype") == "Type3";
        if (reference is not null && !isType3)
        {
            return _document.GetOrAddFont(reference.ObjectNumber, _factory, dictionary);
        }

        if (reference is not null && _fonts.TryGetValue(reference.ObjectNumber, out PdfFont? local))
        {
            return local;
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
        if (_path is { IsEmpty: false } && (fill || stroke) && !_suppressContent)
        {
            IPath path = _path.Build();
            if (fill)
            {
                if (_fillTiling is not null)
                {
                    PaintTiling(path, evenOdd);
                }
                else if (_fillShading is { } fillShading)
                {
                    _context.Save();
                    _context.ClipPath(path, evenOdd);
                    PaintShading(fillShading, _fillShadingMatrix);
                    _context.Restore();
                }
                else
                {
                    _context.FillPath(path, FillPaintColor(), evenOdd);
                }
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
        if (_path is { IsEmpty: false } && !_suppressContent)
        {
            _context.ClipPath(_path.Build(), evenOdd);
        }
    }

    private void ClearPath()
    {
        _path = null;
        _hasCurrentPoint = false;
    }

    // ---- Marked content / optional content (layers) ----

    private void BeginMark(bool ownSuppressed)
    {
        bool cumulative = ownSuppressed || (_marks.Count > 0 && _marks.Peek());
        _marks.Push(cumulative);
        _suppressContent = cumulative;
    }

    private void EndMark()
    {
        if (_marks.Count > 0)
        {
            _marks.Pop();
        }

        _suppressContent = _marks.Count > 0 && _marks.Peek();
    }

    private bool IsMarkHidden(Stack<PdfObject> operands, PdfDictionary? resources)
    {
        foreach (PdfObject operand in operands.ToArray())
        {
            int? objectNumber = ResolveOptionalContentObject(operand, resources);
            if (objectNumber is { } number && !_document.IsOptionalContentGroupVisible(number))
            {
                return true;
            }
        }

        return false;
    }

    private int? ResolveOptionalContentObject(PdfObject operand, PdfDictionary? resources)
    {
        PdfObject? value = operand;
        if (operand is PdfName name && resources?.GetDictionary("Properties")?.Get(name.Value) is { } entry)
        {
            value = entry;
        }

        if (value is PdfIndirectRef reference)
        {
            if (_document.Resolve(value) is PdfDictionary dictionary)
            {
                if (dictionary.GetNameValue("Type") is "OCG" or "OCMD")
                {
                    return reference.ObjectNumber;
                }

                if (dictionary.Get("OC") is PdfIndirectRef inner)
                {
                    return inner.ObjectNumber;
                }

                return null;
            }

            return reference.ObjectNumber;
        }

        if (value is PdfDictionary direct && direct.Get("OC") is PdfIndirectRef directRef)
        {
            return directRef.ObjectNumber;
        }

        return null;
    }

    // ---- Color spaces ----

    private void SetColorSpace(PdfObject? space, PdfDictionary? resources, bool stroke)
    {
        PdfObject? resolved = space;

        if (space is PdfName name && _document.Resolve(resources?.Get("ColorSpace")) is PdfDictionary resourceSpaces &&
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
        PdfFunction? tint = null;
        ColorSpaceKind baseKind = ColorSpaceKind.DeviceGray;
        int baseComponents = 1;
        float[] labWhite = DefaultLabWhitePoint;
        float[] labRange = DefaultLabRange;
        CalColor cal = new();
        IccProfile? icc = null;

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
                        PdfStream? iccStream = _document.Resolve(array[1]) as PdfStream;
                        int n = Math.Max(1, (int)(iccStream?.Dictionary.GetIntegerValue("N") ?? 3));
                        // Apply the embedded ICC profile for matrix/TRC RGB/Gray; other profile types keep the
                        // device approximation chosen by component count below.
                        icc = iccStream is not null ? IccProfile.TryCreate(iccStream.Decode()) : null;
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
                        lookup = PdfImageDecoder.ReadIndexedPalette(_document.Resolve(array[3]));
                        break;

                    case "Separation":
                        kind = ColorSpaceKind.Separation;
                        components = 1;
                        (baseKind, baseComponents) = DescribeBaseSpace(array.Count >= 3 ? array[2] : null);
                        tint = array.Count >= 4 ? PdfFunction.Create(_document, array[3]) : null;
                        break;

                    case "DeviceN":
                        kind = ColorSpaceKind.DeviceN;
                        components = Math.Max(1, (_document.Resolve(array[1]) as PdfArray)?.Count ?? 1);
                        (baseKind, baseComponents) = DescribeBaseSpace(array.Count >= 3 ? array[2] : null);
                        tint = array.Count >= 4 ? PdfFunction.Create(_document, array[3]) : null;
                        break;

                    case "Pattern":
                        kind = ColorSpaceKind.Pattern;
                        components = 1;
                        break;

                    case "Lab":
                        kind = ColorSpaceKind.Lab;
                        components = 3;
                        if (array.Count >= 2 && _document.Resolve(array[1]) is PdfDictionary labDictionary)
                        {
                            float[]? wp = ColorFloats(labDictionary.GetArray("WhitePoint"));
                            if (wp is { Length: >= 3 })
                            {
                                labWhite = new[] { wp[0], wp[1], wp[2] };
                            }

                            float[]? lr = ColorFloats(labDictionary.GetArray("Range"));
                            if (lr is { Length: >= 4 })
                            {
                                labRange = new[] { lr[0], lr[1], lr[2], lr[3] };
                            }
                        }

                        break;

                    case "CalGray":
                        kind = ColorSpaceKind.CalGray;
                        components = 1;
                        cal = ParseCalColor(array.Count >= 2 ? _document.Resolve(array[1]) : null);
                        break;

                    case "CalRGB":
                        kind = ColorSpaceKind.CalRgb;
                        components = 3;
                        cal = ParseCalColor(array.Count >= 2 ? _document.Resolve(array[1]) : null);
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
            _strokeTint = tint;
            _strokeBaseSpace = baseKind;
            _strokeBaseComponents = baseComponents;
            _strokeLabWhite = labWhite;
            _strokeLabRange = labRange;
            _strokeCal = cal;
            _strokeIcc = icc;
        }
        else
        {
            _fillSpace = kind;
            _fillComponents = components;
            _fillLookup = lookup;
            _fillLookupComponents = lookupComponents;
            _fillTint = tint;
            _fillBaseSpace = baseKind;
            _fillBaseComponents = baseComponents;
            _fillLabWhite = labWhite;
            _fillLabRange = labRange;
            _fillCal = cal;
            _fillIcc = icc;
        }

        // Switching to a non-pattern color space clears any active shading pattern for that side.
        if (kind != ColorSpaceKind.Pattern)
        {
            ClearPatternColor(stroke);
        }
    }

    /// <summary>
    /// Drops the selected tiling/shading pattern for one side of the graphics state. Every colour
    /// operator that names a device or ICC space (g, rg, k, sc, ...) replaces the current pattern
    /// colour, so the pattern must not survive it - otherwise a later solid fill paints the leftover
    /// tiling cell instead of the colour (PDF 32000-1:2008 8.6.8).
    /// </summary>
    private void ClearPatternColor(bool stroke)
    {
        if (stroke)
        {
            _strokeShading = null;
        }
        else
        {
            _fillShading = null;
            _fillTiling = null;
        }
    }

    /// <summary>Describes the base color space of a Separation/DeviceN array: its kind and component count.</summary>
    private (ColorSpaceKind Kind, int Components) DescribeBaseSpace(PdfObject? baseSpaceObject)
    {
        PdfObject? baseSpace = _document.Resolve(baseSpaceObject);
        switch (baseSpace)
        {
            case PdfName name:
                return (NameToKind(name.Value), ComponentsOfName(name));

            case PdfArray array when array.Count >= 2 && array[0] is PdfName family && family.Value == "ICCBased":
                int n = Math.Max(1, (int)((_document.Resolve(array[1]) as PdfStream)?.Dictionary.GetIntegerValue("N") ?? 3));
                return (ComponentsToKind(n), n);

            default:
                return (ColorSpaceKind.DeviceRgb, 3);
        }
    }

    private static ColorSpaceKind NameToKind(string value) => value switch
    {
        "DeviceGray" or "G" or "CalGray" => ColorSpaceKind.DeviceGray,
        "DeviceRGB" or "RGB" or "CalRGB" => ColorSpaceKind.DeviceRgb,
        "DeviceCMYK" or "CMYK" => ColorSpaceKind.DeviceCmyk,
        _ => ColorSpaceKind.DeviceRgb,
    };

    private static ColorSpaceKind ComponentsToKind(int components) => components switch
    {
        1 => ColorSpaceKind.DeviceGray,
        4 => ColorSpaceKind.DeviceCmyk,
        _ => ColorSpaceKind.DeviceRgb,
    };

    // ---- Patterns and shadings ----

    private void SelectPattern(Stack<PdfObject> operands, PdfDictionary? resources, bool stroke)
    {
        PdfObject? operand = operands.Count > 0 ? operands.Pop() : null;
        PdfObject? patternObject = ResolvePatternObject(operand, resources);
        PdfDictionary? pattern = patternObject as PdfDictionary ?? (patternObject as PdfStream)?.Dictionary;
        if (pattern is null)
        {
            _diag.Report(PdfUnsupportedKind.PatternColorSpace, "Pattern color used without a resolvable pattern dictionary");
            return;
        }

        int patternType = (int)(pattern.GetIntegerValue("PatternType") ?? 1);
        if (patternType == 2 && pattern.Get("Shading") is { } shadingObject)
        {
            Shading.PdfShading? shading = Shading.PdfShading.Create(_document, shadingObject);
            if (shading is { IsSupported: true })
            {
                Matrix3x2 matrix = ParsePatternMatrix(pattern);
                if (stroke)
                {
                    _strokeShading = shading;
                    _strokeShadingMatrix = matrix;
                }
                else
                {
                    _fillShading = shading;
                    _fillShadingMatrix = matrix;
                }

                return;
            }
        }

        if (patternType == 1 && !stroke && patternObject is PdfStream tilingStream)
        {
            // Tiling pattern: the cell is rasterized on demand at paint time (PaintTiling).
            _fillTiling = tilingStream;
            _fillTilingMatrix = ParsePatternMatrix(pattern);
            return;
        }

        _diag.Report(PdfUnsupportedKind.PatternColorSpace, "Unsupported pattern (only axial/radial shading and fill tiling patterns are supported)");
    }

    private PdfObject? ResolvePatternObject(PdfObject? operand, PdfDictionary? resources)
    {
        PdfObject? resolved = _document.Resolve(operand);
        if (resolved is PdfName name)
        {
            PdfObject? entry = (_document.Resolve(resources?.Get("Pattern")) as PdfDictionary)?.Get(name.Value);
            return _document.Resolve(entry);
        }

        return resolved;
    }

    private PdfDictionary? ResolvePattern(PdfObject? operand, PdfDictionary? resources)
    {
        return ResolvePatternObject(operand, resources) as PdfDictionary;
    }

    private static Matrix3x2 ParsePatternMatrix(PdfDictionary pattern)
    {
        if (pattern.GetArray("Matrix") is not { Count: >= 6 } array)
        {
            return Matrix3x2.Identity;
        }

        return new Matrix3x2(
            array.GetNumber(0)?.AsFloat() ?? 1f,
            array.GetNumber(1)?.AsFloat() ?? 0f,
            array.GetNumber(2)?.AsFloat() ?? 0f,
            array.GetNumber(3)?.AsFloat() ?? 1f,
            array.GetNumber(4)?.AsFloat() ?? 0f,
            array.GetNumber(5)?.AsFloat() ?? 0f);
    }

    private void PaintShadingResource(Stack<PdfObject> operands, PdfDictionary? resources)
    {
        // The sh operator paints a named shading from Resources/Shading (mesh shadings are used this way).
        PdfObject? operand = operands.Count > 0 ? operands.Peek() : null;
        Shading.PdfShading? shading = null;
        if (operand is PdfName name && resources?.GetDictionary("Shading")?.Get(name.Value) is { } entry)
        {
            shading = Shading.PdfShading.Create(_document, entry);
        }
        else
        {
            shading = Shading.PdfShading.Create(_document, operand);
        }

        if (shading is { IsSupported: true })
        {
            // The shading is defined directly in user space (no pattern matrix).
            PaintShading(shading, Matrix3x2.Identity);
            return;
        }

        // Fall back to the current pattern's shading (axial/radial selected via scn).
        PaintShading();
    }

    private void PaintShading()
    {
        Shading.PdfShading? shading = _fillShading ?? _strokeShading;
        Matrix3x2 matrix = _fillShading is not null ? _fillShadingMatrix : _strokeShadingMatrix;
        if (shading is null)
        {
            _diag.Report(PdfUnsupportedKind.Shading, "The sh operator requires a current shading pattern");
            return;
        }

        PaintShading(shading, matrix);
    }

    private void PaintShading(Shading.PdfShading shading, in Matrix3x2 shadingToUser)
    {
        RectF clip = _context.ClipBounds;
        if (clip.IsEmpty)
        {
            return;
        }

        Matrix3x2 shadingToDevice = shadingToUser * _state.Ctm;
        IImage? image = shading.Rasterize(_factory, clip, shadingToDevice);
        if (image is null)
        {
            return;
        }

        try
        {
            _context.Save();
            _context.SetMatrix(Matrix3x2.Identity);
            _context.DrawDeviceRaster(image, clip, 1f);
            _context.Restore();
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>
    /// Fills the path with a Type 1 tiling pattern: rasterizes one cell of the pattern content stream
    /// at device scale, then blits it across the clipped path bounds on the (pattern -> device) lattice.
    /// </summary>
    private void PaintTiling(IPath path, bool evenOdd)
    {
        PdfStream stream = _fillTiling!;
        PdfDictionary dict = stream.Dictionary;

        float bx0 = 0f, by0 = 0f, bx1 = 100f, by1 = 100f;
        if (dict.GetArray("BBox") is { Count: >= 4 } bbox)
        {
            bx0 = bbox.GetNumber(0)?.AsFloat() ?? 0f;
            by0 = bbox.GetNumber(1)?.AsFloat() ?? 0f;
            bx1 = bbox.GetNumber(2)?.AsFloat() ?? 0f;
            by1 = bbox.GetNumber(3)?.AsFloat() ?? 0f;
        }

        float cellWidth = bx1 - bx0;
        float cellHeight = by1 - by0;
        float xStep = dict.GetNumber("XStep")?.AsFloat() ?? cellWidth;
        float yStep = dict.GetNumber("YStep")?.AsFloat() ?? cellHeight;
        if (cellWidth <= 0f || cellHeight <= 0f || xStep <= 0f || yStep <= 0f)
        {
            _context.FillPath(path, FillPaintColor(), evenOdd);
            return;
        }

        Matrix3x2 dt = _fillTilingMatrix * _state.Ctm;
        float sx = MathF.Sqrt((dt.M11 * dt.M11) + (dt.M12 * dt.M12));
        float sy = MathF.Sqrt((dt.M21 * dt.M21) + (dt.M22 * dt.M22));
        if (sx <= 0f || sy <= 0f)
        {
            return;
        }

        int tw = Math.Clamp((int)MathF.Round(xStep * sx), 1, 4096);
        int th = Math.Clamp((int)MathF.Round(yStep * sy), 1, 4096);

        Matrix3x2 tileMatrix =
            Matrix3x2.CreateTranslation(-bx0, -by0) *
            Matrix3x2.CreateScale(sx, -sy) *
            Matrix3x2.CreateTranslation(0f, th);

        IImage tile;
        using (IGraphicsContext cell = _factory.CreateContext(tw, th))
        {
            var nested = new PdfContentInterpreter(_document, _factory, cell, _options) { OnUnsupported = OnUnsupported };
            nested.RenderCell(stream.Decode(_diag), _document.Resolve(dict.Get("Resources")) as PdfDictionary, tileMatrix);
            tile = cell.Snapshot();
        }

        try
        {
            _context.Save();
            _context.ClipPath(path, evenOdd);
            _context.SetMatrix(Matrix3x2.Identity);
            RectF bounds = _context.ClipBounds;
            if (bounds.IsEmpty || !Matrix3x2.Invert(dt, out Matrix3x2 inv))
            {
                return;
            }

            float left = bounds.X, top = bounds.Y, right = bounds.X + bounds.Width, bottom = bounds.Y + bounds.Height;
            float minPx = float.MaxValue, minPy = float.MaxValue, maxPx = float.MinValue, maxPy = float.MinValue;
            foreach ((float cx, float cy) in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
            {
                Vector2 p = Vector2.Transform(new Vector2(cx, cy), inv);
                minPx = Math.Min(minPx, p.X);
                maxPx = Math.Max(maxPx, p.X);
                minPy = Math.Min(minPy, p.Y);
                maxPy = Math.Max(maxPy, p.Y);
            }

            int i0 = (int)MathF.Floor(minPx / xStep);
            int i1 = (int)MathF.Ceiling(maxPx / xStep);
            int j0 = (int)MathF.Floor(minPy / yStep);
            int j1 = (int)MathF.Ceiling(maxPy / yStep);
            if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > 200000)
            {
                return; // guard against pathological tile counts
            }

            for (int i = i0; i <= i1; i++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    float px = i * xStep;
                    float py = j * yStep;
                    Vector2 a = Vector2.Transform(new Vector2(px, py + yStep), dt);
                    Vector2 c = Vector2.Transform(new Vector2(px + xStep, py), dt);
                    float rx = Math.Min(a.X, c.X);
                    float ry = Math.Min(a.Y, c.Y);
                    float rw = Math.Abs(c.X - a.X);
                    float rh = Math.Abs(c.Y - a.Y);
                    _context.DrawDeviceRaster(tile, new RectF(rx, ry, rw, rh), 1f);
                }
            }
        }
        finally
        {
            tile.Dispose();
            _context.Restore();
        }
    }

    private void ApplyColorOperands(
        Stack<PdfObject> operands,
        ColorSpaceKind kind,
        int components,
        byte[]? lookup,
        int lookupComponents,
        PdfFunction? tint,
        ColorSpaceKind baseSpace,
        int baseComponents,
        float[] labWhite,
        float[] labRange,
        CalColor cal,
        IccProfile? icc,
        out PosoColor color)
    {
        if (kind == ColorSpaceKind.Pattern)
        {
            _diag.Report(PdfUnsupportedKind.PatternColorSpace, "Pattern color spaces are not supported yet");
            color = PosoColor.Black;
            return;
        }

        int count = Math.Max(1, components);
        Span<float> values = stackalloc float[count];
        for (int i = count - 1; i >= 0; i--)
        {
            values[i] = PopNumber(operands);
        }

        if (kind is ColorSpaceKind.Separation or ColorSpaceKind.DeviceN)
        {
            color = SeparationColor(tint, baseSpace, baseComponents, values);
            return;
        }

        if (kind == ColorSpaceKind.Lab)
        {
            float l = values.Length > 0 ? values[0] : 0f;
            float a = values.Length > 1 ? values[1] : 0f;
            float b = values.Length > 2 ? values[2] : 0f;
            color = Cielab.ToRgb(l, a, b, labWhite);
            return;
        }

        if (kind is ColorSpaceKind.CalGray or ColorSpaceKind.CalRgb)
        {
            color = cal.ToRgb(values);
            return;
        }

        if (icc is not null)
        {
            icc.ToRgb(values, out byte ir, out byte ig, out byte ib);
            color = new PosoColor(ir, ig, ib);
            return;
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

    /// <summary>Evaluates a Separation/DeviceN tint through its tint-transform function onto the base space.</summary>
    private static PosoColor SeparationColor(PdfFunction? tint, ColorSpaceKind baseSpace, int baseComponents, ReadOnlySpan<float> tints)
    {
        Span<float> output = stackalloc float[Math.Max(1, baseComponents)];
        if (tint is not null)
        {
            tint.Evaluate(tints, output);
        }
        else
        {
            for (int i = 0; i < output.Length && i < tints.Length; i++)
            {
                output[i] = tints[i];
            }
        }

        return ColorFromBase(baseSpace, output);
    }

    private static PosoColor ColorFromBase(ColorSpaceKind kind, ReadOnlySpan<float> v) => kind switch
    {
        ColorSpaceKind.DeviceGray => PosoColor.FromGray(v.Length > 0 ? v[0] : 0f),
        ColorSpaceKind.DeviceCmyk when v.Length >= 4 => PosoColor.FromCmyk(v[0], v[1], v[2], v[3]),
        ColorSpaceKind.DeviceRgb when v.Length >= 3 => PosoColor.FromRgb(v[0], v[1], v[2]),
        _ => v.Length >= 3 ? PosoColor.FromRgb(v[0], v[1], v[2]) : PosoColor.FromGray(v.Length > 0 ? v[0] : 0f),
    };

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

    private CalColor ParseCalColor(PdfObject? obj)
    {
        if (obj is not PdfDictionary dictionary)
        {
            return new CalColor();
        }

        float[]? wp = ColorFloats(dictionary.GetArray("WhitePoint"));
        float[]? bp = ColorFloats(dictionary.GetArray("BlackPoint"));
        float[]? gamma = ColorFloats(dictionary.GetArray("Gamma"));
        if (gamma is null && dictionary.Get("Gamma") is PdfNumber singleGamma)
        {
            float g = singleGamma.AsFloat();
            gamma = new[] { g, g, g };
        }

        float[]? matrix = ColorFloats(dictionary.GetArray("Matrix"));
        return new CalColor
        {
            WhitePoint = wp is { Length: >= 3 } ? wp : new[] { 0.9505f, 1f, 1.089f },
            BlackPoint = bp is { Length: >= 3 } ? bp : new[] { 0f, 0f, 0f },
            Gamma = gamma is { Length: >= 3 } ? gamma : new[] { 1f, 1f, 1f },
            Matrix = matrix is { Length: >= 9 } ? matrix : null,
        };
    }

    private static float[]? ColorFloats(PdfArray? array)
    {
        if (array is null || array.Count == 0)
        {
            return null;
        }

        var values = new float[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return values;
    }

    private PosoColor FillPaintColor() =>
        _state.FillColor.WithAlpha(ScaleAlpha(_state.FillColor.A, _state.FillAlpha));

    private PosoColor StrokePaintColor() =>
        _state.StrokeColor.WithAlpha(ScaleAlpha(_state.StrokeColor.A, _state.StrokeAlpha));

    private static byte ScaleAlpha(byte alpha, float alphaScale) =>
        (byte)Math.Clamp((int)MathF.Round(alpha * alphaScale), 0, 255);

    // ---- XObjects and images ----

    /// <summary>
    /// Hand the back end back with exactly <paramref name="saveDepth"/> pending saves. An unbalanced
    /// <c>q</c> is illegal but common in the wild - a producer that writes <c>1 0 0 1 0 0 cm q </c> as a
    /// form's whole content - and because the interpreter pops its own state stack rather than the back
    /// end's save stack, one stray level shifts every later <c>Restore</c>. Under a page-level
    /// transparency group that means the group is never composited and the page comes out blank.
    /// </summary>
    private void BalanceCanvasSaves(int saveDepth)
    {
        for (int leaked = _context.SaveDepth - saveDepth; leaked > 0; leaked--)
        {
            _context.Restore();
        }
    }

    private void ExecuteForm(PdfStream form)
    {
        if (_suppressContent)
        {
            return;
        }

        if (_formDepth >= MaxFormDepth)
        {
            throw new PdfFormatException("Form XObject nesting is too deep (possible cycle)");
        }

        _formDepth++;
        int outerSaves = _context.SaveDepth;
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

            // A form whose dictionary carries a transparency group (/Group /S /Transparency) renders its
            // content on an isolated off-surface that is composited back with the alpha/blend that were in
            // effect at the Do operator. The group opacity is applied once at the boundary, so element
            // alphas inside start from opaque. Back-ends without layer support treat PushLayer as a no-op.
            // An ExtGState /SMask masks the element as a whole, so it needs the same boundary: a form
            // that is not itself a group gets a layer purely to carry the mask.
            IImage? softMask = _state.SoftMask is { } maskStream ? SoftMaskImage(maskStream, _context.ClipBounds) : null;
            bool pushedLayer = IsTransparencyGroup(dictionary) || softMask is not null;
            if (pushedLayer)
            {
                byte groupAlpha = (byte)Math.Clamp((int)MathF.Round(_state.FillAlpha * 255f), 0, 255);
                _context.PushLayer(null, groupAlpha, _state.BlendMode, softMask);
                _state.FillAlpha = 1f;
                _state.StrokeAlpha = 1f;

                // The mask belongs to this element, not to the content inside it.
                _state.SoftMask = null;
            }

            try
            {
                ExecuteContent(form.Decode(_diag), formResources);
            }
            finally
            {
                // A form may legally be left with q still open; the group has to be popped before that
                // stray save level, otherwise the layer never composites back.
                BalanceCanvasSaves(outerSaves + 1 + (pushedLayer ? 1 : 0));

                if (pushedLayer)
                {
                    _context.PopLayer();
                }
            }

            _context.Restore();
            _state = _stateStack.Pop();
        }
        finally
        {
            _formDepth--;
        }
    }

    /// <summary>
    /// True when a form (or page) dictionary declares a transparency group, i.e. its /Group dictionary
    /// has /S /Transparency. Other group subtypes (none exist in practice) are ignored.
    /// </summary>
    private bool IsTransparencyGroup(PdfDictionary dictionary)
    {
        PdfDictionary? group = _document.Resolve(dictionary.Get("Group")) as PdfDictionary;
        return group is not null && group.GetNameValue("S") == "Transparency";
    }

    /// <summary>
    /// Renders an ExtGState <c>/SMask</c> group into the coverage image the back ends multiply an
    /// element by: every channel carries the factor, so a grey image is enough. <c>/Luminosity</c>
    /// inverts the mask's luminance - black keeps the element, white erases it - and <c>/Alpha</c>
    /// takes the mask's own alpha. The group is composited over <c>/BC</c> first, because a backdrop is
    /// part of the mask's definition rather than of its content.
    /// </summary>
    private IImage? SoftMaskImage(PdfDictionary mask, in RectF region)
    {
        if (region.IsEmpty)
        {
            return null;
        }

        int width = Math.Clamp((int)MathF.Ceiling(region.Width), 1, 4096);
        int height = Math.Clamp((int)MathF.Ceiling(region.Height), 1, 4096);
        if (_softMasks.TryGetValue((mask, width, height), out IImage? cached))
        {
            return cached;
        }

        IImage? built = BuildSoftMask(mask, width, height);
        _softMasks[(mask, width, height)] = built;
        return built;
    }

    private IImage? BuildSoftMask(PdfDictionary dict, int width, int height)
    {
        if (_document.Resolve(dict.Get("G")) is not PdfStream form)
        {
            return null;
        }

        byte[] content = form.Decode(_diag);

        float bx0 = 0f, by0 = 0f, bx1 = 1f, by1 = 1f;
        if (form.Dictionary.GetArray("BBox") is { Count: >= 4 } bbox)
        {
            bx0 = bbox.GetNumber(0)?.AsFloat() ?? 0f;
            by0 = bbox.GetNumber(1)?.AsFloat() ?? 0f;
            bx1 = bbox.GetNumber(2)?.AsFloat() ?? 0f;
            by1 = bbox.GetNumber(3)?.AsFloat() ?? 0f;
        }

        float cellWidth = bx1 - bx0;
        float cellHeight = by1 - by0;
        if (cellWidth <= 0f || cellHeight <= 0f)
        {
            return null;
        }

        Matrix3x2 baseMatrix =
            Matrix3x2.CreateTranslation(-bx0, -by0) *
            Matrix3x2.CreateScale(width / cellWidth, -height / cellHeight) *
            Matrix3x2.CreateTranslation(0f, height);

        bool byAlpha = dict.GetNameValue("S") == "Alpha";
        PdfFunction? transfer = PdfFunction.Create(_document, dict.Get("TR"));
        PosoColor backdrop = SoftMaskBackdrop(dict.GetArray("BC"));

        byte[] pixels;
        using (IGraphicsContext surface = _factory.CreateContext(width, height))
        {
            surface.Clear(backdrop);
            var nested = new PdfContentInterpreter(_document, _factory, surface, _options) { OnUnsupported = OnUnsupported };
            nested.RenderCell(content, _document.Resolve(form.Dictionary.Get("Resources")) as PdfDictionary, baseMatrix);

            using IImage rendered = surface.Snapshot();
            int stride = rendered.BytesPerPixel;
            pixels = new byte[rendered.Width * rendered.Height * stride];
            rendered.CopyPixels(pixels);
        }

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            float coverage = byAlpha
                ? pixels[i + 3] / 255f
                : 1f - (((0.3f * pixels[i + 2]) + (0.59f * pixels[i + 1]) + (0.11f * pixels[i])) / 255f);

            if (transfer is not null)
            {
                Span<float> input = stackalloc float[] { coverage };
                Span<float> output = stackalloc float[1];
                transfer.Evaluate(input, output);
                coverage = output[0];
            }

            byte factor = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(coverage, 0f, 1f) * 255f), 0, 255);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = factor;
            pixels[i + 3] = 255;
        }

        return _factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, pixels);
    }

    /// <summary>
    /// The <c>/BC</c> backdrop of a soft-mask group. Omitting it means opaque black in the mask colour
    /// space, which under a luminosity mask leaves the element untouched.
    /// </summary>
    private static PosoColor SoftMaskBackdrop(PdfArray? components) => components switch
    {
        { Count: >= 4 } cmyk => PosoColor.FromCmyk(
            cmyk.GetNumber(0)?.AsFloat() ?? 0f, cmyk.GetNumber(1)?.AsFloat() ?? 0f,
            cmyk.GetNumber(2)?.AsFloat() ?? 0f, cmyk.GetNumber(3)?.AsFloat() ?? 0f),
        { Count: 3 } rgb => PosoColor.FromRgb(
            rgb.GetNumber(0)?.AsFloat() ?? 0f, rgb.GetNumber(1)?.AsFloat() ?? 0f, rgb.GetNumber(2)?.AsFloat() ?? 0f),
        { Count: >= 1 } gray => PosoColor.FromGray(gray.GetNumber(0)?.AsFloat() ?? 0f),
        _ => PosoColor.Black,
    };

    private void DrawImage(PdfStream image)
    {
        if (_suppressContent)
        {
            return;
        }

        IImage? decoded = PdfImageDecoder.Decode(_document, _factory, image, image.Dictionary, _state.FillColor, _diag);
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

        ApplyInlineAbbreviations(dictionary);

        // Binary data runs until whitespace followed by "EI"
        int dataStart = lexer.Position;
        int dataEnd = FindInlineImageEnd(lexer.Data, dataStart);
        byte[] raw = lexer.Data.AsSpan(dataStart, dataEnd - dataStart).ToArray();

        int eI = PdfParser.IndexOfKeyword(lexer.Data, dataEnd, "EI"u8);
        lexer.Position = eI >= 0 ? eI + 2 : lexer.Data.Length;

        IImage? decoded = PdfImageDecoder.DecodeInline(_document, _factory, new PdfDictionary(dictionary), raw, _state.FillColor, _diag);
        if (decoded is not null && !_suppressContent)
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

    /// <summary>
    /// Fold an inline image's abbreviated keys into the full names every other image reader uses, and do
    /// the same for its abbreviated filter names. 32000-2:2020 8.9.7 settles a dictionary that carries
    /// both forms: the abbreviation wins.
    /// </summary>
    private static void ApplyInlineAbbreviations(Dictionary<string, PdfObject> dictionary)
    {
        foreach ((string abbreviated, string full) in InlineAbbreviations)
        {
            if (dictionary.TryGetValue(abbreviated, out PdfObject? value))
            {
                dictionary[full] = value;
                dictionary.Remove(abbreviated);
            }
        }

        switch (dictionary.GetValueOrDefault("Filter"))
        {
            case PdfName name:
                dictionary["Filter"] = FilterName(name);
                break;

            case PdfArray array:
                var expanded = new List<PdfObject>(array.Count);
                foreach (PdfObject item in array)
                {
                    expanded.Add(item is PdfName itemNamed ? FilterName(itemNamed) : item);
                }

                dictionary["Filter"] = new PdfArray(expanded);
                break;
        }
    }

    private static PdfName FilterName(PdfName name) =>
        InlineFilterNames.TryGetValue(name.Value, out string? full) ? new PdfName(full) : name;

    private static readonly Dictionary<string, string> InlineFilterNames = new(StringComparer.Ordinal)
    {
        ["AHx"] = "ASCIIHexDecode",
        ["A85"] = "ASCII85Decode",
        ["Fl"] = "FlateDecode",
        ["LZW"] = "LZWDecode",
        ["DCT"] = "DCTDecode",
        ["CCF"] = "CCITTFaxDecode",
        ["RLE"] = "RunLengthDecode",
    };

    private static readonly (string Abbreviated, string Full)[] InlineAbbreviations =
    [
        ("BPC", "BitsPerComponent"),
        ("CS", "ColorSpace"),
        ("D", "Decode"),
        ("DP", "DecodeParms"),
        ("F", "Filter"),
        ("H", "Height"),
        ("IM", "ImageMask"),
        ("I", "Interpolate"),
        ("L", "Length"),
        ("W", "Width"),
    ];

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
