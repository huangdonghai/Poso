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

    /// <summary>
    /// Ceiling for a color space's component count. The specification allows at most 31 colorants in a
    /// DeviceN space, but /N and the colorant array come from the file, and an absurd value would be
    /// used as the size of a stack buffer while collecting the operands of a colour operator.
    /// </summary>
    private const int MaxColorComponents = 64;

    private readonly PdfDocument _document;
    private readonly IGraphicsFactory _factory;
    private readonly IGraphicsContext _context;
    private readonly PdfRenderOptions _options;
    private readonly PdfDiagnostics _diag;
    private readonly Stack<GraphicsState> _stateStack = new();
    private readonly Dictionary<int, PdfFont> _fonts = new();

    // Rendered ExtGState soft masks, keyed by mask stream and size, released when the page finishes.
    private readonly Dictionary<(PdfDictionary Mask, RectF Region), IImage?> _softMasks = new();
    private GraphicsState _state = new();
    /// <summary>
    /// Which forms one render has open right now, shared by every interpreter it spawns: a tiling cell or
    /// a soft mask runs through a fresh interpreter, so state kept per instance restarts there and a form
    /// that paints itself - directly, or through a partner that paints it back - recurses until the stack
    /// gives out.
    /// </summary>
    private readonly FormChain _sharedForms;
    private Matrix3x2 _pageBaseMatrix;

    /// <summary>
    /// The CTM as it stood when the content stream now executing was entered: the page's base transform
    /// for page content, and for a form the transform its <c>Do</c> left the content in. A shading pattern
    /// is placed relative to this rather than to the live CTM - MuPDF records the enclosing stream's
    /// transform when the pattern is set, so a <c>cm</c> anywhere inside the stream that paints with it
    /// moves the shape being filled but not the gradient.
    /// </summary>
    private Matrix3x2 _streamEntryMatrix;
    private readonly Stack<bool> _marks = new();
    private bool _suppressContent;

    // Current path (user space)
    private PathBuilder? _path;
    private float _currentX;
    private float _currentY;
    private float _startX;
    private float _startY;
    private bool _hasCurrentPoint;

    // A W/W* marks the pending clip rule; it is applied when the path is consumed (n, or a paint
    // operator), not where W appears. pdf.js clippath.pdf opens with a lone W before its rectangle and
    // expects the clip to come from that rectangle: MuPDF and PDFium clip at path end, so applying W
    // eagerly on the then-empty path left the page unclipped.
    private bool _pendingClip;
    private bool _pendingClipEvenOdd;

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
    private PosoColor? _fillTilingColor;
    private PdfStream? _strokeTiling;
    private Matrix3x2 _strokeTilingMatrix = Matrix3x2.Identity;
    private PosoColor? _strokeTilingColor;
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

    // True while a d1 (stencil) Type3 glyph procedure runs: colour operators inside it are ignored and
    // the text's own colour paints the shape.
    private bool _type3Stencil;

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
        : this(document, factory, context, options, new FormChain())
    {
    }

    internal PdfContentInterpreter(
        PdfDocument document,
        IGraphicsFactory factory,
        IGraphicsContext context,
        PdfRenderOptions options,
        FormChain sharedForms)
    {
        _document = document;
        _factory = factory;
        _context = context;
        _options = options;
        _sharedForms = sharedForms;
        _diag = new PdfDiagnostics(options.Strict, feature => OnUnsupported?.Invoke(feature));
    }

    /// <summary>
    /// The forms a render has open, and how deep that chain is. Shared so that a form reached again
    /// through a tiling cell or a soft mask is still recognised as already being drawn.
    /// </summary>
    internal sealed class FormChain
    {
        private readonly HashSet<PdfStream> _open = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Opens a form, or says no when it is already being drawn further up the chain. MuPDF draws
        /// nothing for such a form and lets the rest of the page stand, which is what the answer is for:
        /// a file whose XObjects refer back to each other is painted to the last level that means
        /// something rather than throwing the page away - or, with no bound at all, overflowing the stack.
        /// </summary>
        internal bool Enter(PdfStream form)
        {
            if (_open.Count >= MaxFormDepth || !_open.Add(form))
            {
                return false;
            }

            return true;
        }

        internal void Leave(PdfStream form) => _open.Remove(form);
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
        _streamEntryMatrix = baseMatrix;
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
    public void RenderCell(byte[] content, PdfDictionary? resources, Matrix3x2 baseMatrix, PosoColor? cellColor = null)
    {
        _state = new GraphicsState();
        if (cellColor is { } color)
        {
            // An uncoloured (PaintType 2) pattern paints its cell in the colour scn supplied, so the cell
            // starts from that colour instead of the default black.
            _state.FillColor = color;
            _state.StrokeColor = color;
        }

        _stateStack.Clear();
        _path = null;
        _hasCurrentPoint = false;
        _marks.Clear();
        _suppressContent = false;
        _state.Ctm = baseMatrix;
        _pageBaseMatrix = baseMatrix;
        _streamEntryMatrix = baseMatrix;
        _context.SetMatrix(baseMatrix);
        ExecuteContent(content, resources);
    }

    /// <summary>
    /// The annotation subtypes whose whole appearance is defined by their quad points, so a file that
    /// leaves out /AP can still be drawn (ISO 32000-1 12.5.6.2).
    /// </summary>
    private static readonly string[] MarkupSubtypes = ["Highlight", "Underline", "StrikeOut", "Squiggly"];

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

            // A markup annotation carries its own geometry in /QuadPoints, so an absent appearance is
            // still drawable. Anything else without one has no geometry here at all: a widget, above
            // all, gets its appearance synthesised from /MK, /DA and the field value, which this
            // viewer does not do - but filling its rectangle with a mark the file never asked for is
            // a worse answer than leaving the page as the content drew it.
            if (subtype is "Square" or "Circle")
            {
                RenderShapeFallback(annot, rect, opacity, ellipse: subtype == "Circle");
                continue;
            }

            if (Array.IndexOf(MarkupSubtypes, subtype) < 0)
            {
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

        // An annotation with appearance states - a check box or a radio button above all - keeps a
        // dictionary of streams keyed by state name, and /AS names the one to show. Reading that as a
        // stream finds nothing at all, which used to leave the whole widget to the markup fallback.
        if (_document.Resolve(candidate) is PdfDictionary states)
        {
            string? state = annot.GetNameValue("AS") ?? annot.GetNameValue("V");
            if (state is not null && _document.Resolve(states.Get(state)) is PdfStream named)
            {
                return named;
            }

            // A file that names a state it does not carry, or names none, falls back the way a viewer
            // does: the /Off state, and failing that the first stream the dictionary holds.
            if (_document.Resolve(states.Get("Off")) is PdfStream off)
            {
                return off;
            }

            foreach (string key in states.Keys)
            {
                if (_document.Resolve(states.Get(key)) is PdfStream first)
                {
                    return first;
                }
            }

            return null;
        }

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

            // An appearance is a form, so its own coordinate system is the stream entry space - which is
            // where a shading pattern's /Matrix is measured from (8.7.3.1 places it in the default space of
            // the content stream). Leaving the page's entry matrix here drops the BBox-to-Rect placement:
            // pdf.js issue7821.pdf fills its stamp through a PatternType 2 whose axis then lands entirely
            // past the visible box, so the whole stamp came out one flat colour - the gradient's far stop.
            Matrix3x2 outerEntry = _streamEntryMatrix;
            _streamEntryMatrix = ctm;
            _state.Ctm = ctm;
            _context.SetMatrix(ctm);
            _state.FillAlpha *= opacity;
            _state.StrokeAlpha *= opacity;
            _context.ClipRect(bbox);

            try
            {
                PdfDictionary? resources = _document.Resolve(appearance.Dictionary.Get("Resources")) as PdfDictionary;
                ExecuteContent(appearance.Decode(_diag), resources);
            }
            finally
            {
                _streamEntryMatrix = outerEntry;
            }
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

    /// <summary>
    /// Square and Circle annotations have no required appearance stream (ISO 32000-1 12.5.5.2/12.5.5.3):
    /// the viewer draws a rectangle, or an ellipse inscribed in the rectangle, stroked in /C and filled
    /// with /IC when present. pdf.js annotation-square-circle-without-appearance.pdf is exactly that case.
    /// </summary>
    private void RenderShapeFallback(PdfDictionary annot, RectF rect, float opacity, bool ellipse)
    {
        PosoColor stroke = ParseColor(annot.Get("C"), PosoColor.Black);
        PosoColor? interior = annot.Get("IC") is { } interiorColor
            ? ParseColor(interiorColor, PosoColor.Black)
            : null;

        float width = 1f;
        if (_document.Resolve(annot.Get("BS")) is PdfDictionary style && style.GetNumberValue("W") is double w)
        {
            width = (float)w;
        }
        else if (annot.GetArray("Border") is { Count: >= 3 } border)
        {
            width = border.GetNumber(2)?.AsFloat() ?? 1f;
        }

        if (width <= 0f)
        {
            width = 1f;
        }

        // Keep the stroke inside the annotation rectangle, the way a viewer does.
        float inset = width * 0.5f;
        RectF box = new(
            rect.X + inset,
            rect.Y + inset,
            Math.Max(0.1f, rect.Width - width),
            Math.Max(0.1f, rect.Height - width));

        var path = new PathBuilder();
        if (ellipse)
        {
            const float k = 0.5522847f; // cubic approximation of a quarter circle
            float cx = box.X + (box.Width * 0.5f);
            float cy = box.Y + (box.Height * 0.5f);
            float rx = box.Width * 0.5f;
            float ry = box.Height * 0.5f;
            path.MoveTo(cx + rx, cy);
            path.CubicTo(cx + rx, cy + (ry * k), cx + (rx * k), cy + ry, cx, cy + ry);
            path.CubicTo(cx - (rx * k), cy + ry, cx - rx, cy + (ry * k), cx - rx, cy);
            path.CubicTo(cx - rx, cy - (ry * k), cx - (rx * k), cy - ry, cx, cy - ry);
            path.CubicTo(cx + (rx * k), cy - ry, cx + rx, cy - (ry * k), cx + rx, cy);
            path.Close();
        }
        else
        {
            path.MoveTo(box.X, box.Y);
            path.LineTo(box.X + box.Width, box.Y);
            path.LineTo(box.X + box.Width, box.Y + box.Height);
            path.LineTo(box.X, box.Y + box.Height);
            path.Close();
        }

        byte alpha = (byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255);
        IPath shape = path.Build();

        _context.SetMatrix(_pageBaseMatrix);
        if (interior is { } fill)
        {
            _context.FillPath(shape, fill.WithAlpha(alpha), false);
        }

        _context.StrokePath(shape, _state.Stroke with { Width = width, DashArray = null }, stroke.WithAlpha(alpha));
    }

    private RectF ParseRectangle(PdfArray? array)
    {
        if (array is not { Count: >= 4 })
        {
            return RectF.Empty;
        }

        return RectF.FromLbrb(
            _document.GetArrayNumber(array, 0)?.AsFloat() ?? 0f,
            _document.GetArrayNumber(array, 1)?.AsFloat() ?? 0f,
            _document.GetArrayNumber(array, 2)?.AsFloat() ?? 0f,
            _document.GetArrayNumber(array, 3)?.AsFloat() ?? 0f);
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

        // A Type3 glyph procedure with no /Resources of its own inherits the resources in force where the
        // text was shown (pdf.js pr4922.pdf's glyph procedures draw text with the page's fonts).
        PdfDictionary? outerResources = _currentResources;
        _currentResources = resources;
        try
        {
            ExecuteContentLoop(content, resources);
        }
        finally
        {
            _currentResources = outerResources;
        }
    }

    private void ExecuteContentLoop(byte[] content, PdfDictionary? resources)
    {
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
            ApplyPendingClip();
            ClearPath();
            operands.Clear();
            return;
        }

        // Clipping - deferred until the path is consumed
        if (lexer.Match(token, "W"u8))
        {
            _pendingClip = true;
            _pendingClipEvenOdd = false;
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "W*"u8))
        {
            _pendingClip = true;
            _pendingClipEvenOdd = true;
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
            // d0: the glyph is coloured - its own operators, including colour, stand as written.
            _type3Stencil = false;
            PopNumber(operands); // wy (unused)
            Type3GlyphWidthGlyphSpace = PopNumber(operands);
            operands.Clear();
            return;
        }

        if (lexer.Match(token, "d1"u8))
        {
            // d1: the glyph is a stencil - MuPDF and PDFium paint it with the text's colour and ignore
            // colour operators inside the procedure (pdf.js issue12705.pdf's glyphs fill white under d1,
            // and every reference still shows black text).
            _type3Stencil = true;
            PopNumber(operands); // lly
            PopNumber(operands); // llx
            PopNumber(operands); // ury
            PopNumber(operands); // urx
            PopNumber(operands); // wy
            Type3GlyphWidthGlyphSpace = PopNumber(operands);
            operands.Clear();
            return;
        }

        if (_type3Stencil && IsColorOperator(lexer, token))
        {
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
            // A hidden /OC block suppresses path painting and clipping, but the shading operator used to
            // paint regardless - and because the clip was skipped it spilled over the whole page. pdf.js
            // issue11144_reduced.pdf: an OFF layer's radial shading turned the page solid red.
            if (!_suppressContent)
            {
                PaintShadingResource(operands, resources);
            }

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
        if (_state.SoftMask is not null)
        {
            _state.SoftMaskCtm = _state.Ctm;
        }

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
                // A Type3 glyph paints by running its own content stream, so the text rendering mode has to
                // be honoured here: with a mode of 3 or 7 the text shall not be rendered at all
                // (ISO 32000-2 9.3.6), and the advance comes from /Widths instead. Running the procedure
                // whatever the mode made pdf.js issue5421.pdf's "3 Tr" line show up on a page both
                // references leave blank.
                if (_state.RenderMode is TextRenderMode.Invisible or TextRenderMode.Clip)
                {
                    widthEm = type3Font.GetWidthEm(code);
                }
                else
                {
                    // Type3 glyphs are drawn immediately at the current text matrix
                    widthEm = ExecuteType3Glyph(type3Font, code, tsMatrix * _textMatrix);
                }
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

            // The mask covers the painted glyphs, not the clip the same text object leaves behind: a clip
            // set here has to outlive the group, so it is added before the layer opens rather than inside it.
            bool masked = BeginMaskedElement();
            try
            {
                _context.DrawGlyphs(graphicsFont, fontSize, glyphs.Slice(0, glyphCount), FillPaintColor(), StrokePaintColor(), _state.RenderMode, textStroke);
            }
            finally
            {
                if (masked)
                {
                    EndMaskedElement();
                }
            }

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

    private static bool IsColorOperator(PdfLexer lexer, PdfToken token) =>
        lexer.Match(token, "g"u8) || lexer.Match(token, "G"u8) ||
        lexer.Match(token, "rg"u8) || lexer.Match(token, "RG"u8) ||
        lexer.Match(token, "k"u8) || lexer.Match(token, "K"u8) ||
        lexer.Match(token, "cs"u8) || lexer.Match(token, "CS"u8) ||
        lexer.Match(token, "sc"u8) || lexer.Match(token, "SC"u8) ||
        lexer.Match(token, "scn"u8) || lexer.Match(token, "SCN"u8);

    // Resources in force for the content stream being executed; a Type3 glyph procedure with no
    // /Resources inherits these.
    private PdfDictionary? _currentResources;

    private float ExecuteType3Glyph(Type3PdfFont font, int code, Matrix3x2 textSpaceMatrix)
    {
        PdfStream? glyphStream = font.GetGlyphStream(code);
        if (glyphStream is null)
        {
            return 0f;
        }

        Type3GlyphWidthGlyphSpace = 0f;
        bool outerStencil = _type3Stencil;
        _type3Stencil = false;
        PdfDictionary? glyphResources = font.Resources ?? _currentResources;
        _context.Save();
        // canvas = FontMatrix * textSpaceMatrix * CTM
        _context.ConcatMatrix(font.FontMatrix * textSpaceMatrix);
        try
        {
            ExecuteContent(glyphStream.Decode(_diag), glyphResources);
        }
        finally
        {
            _context.Restore();
            _type3Stencil = outerStencil;
        }

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
        ApplyPendingClip();
        if (_path is { IsEmpty: false } && (fill || stroke) && !_suppressContent)
        {
            IPath path = _path.Build();
            bool masked = BeginMaskedElement();
            try
            {
                if (fill)
                {
                    if (_fillTiling is not null)
                    {
                        PaintTiling(path, _fillTiling, _fillTilingMatrix, evenOdd, stroke: false);
                    }
                    else if (_fillShading is { } fillShading)
                    {
                        _context.Save();
                        _context.ClipPath(path, evenOdd);
                        PaintShading(fillShading, _fillShadingMatrix, _streamEntryMatrix);
                        _context.Restore();
                    }
                    else
                    {
                        _context.FillPath(path, FillPaintColor(), evenOdd);
                    }
                }

                if (stroke)
                {
                    // A pattern can be the stroke colour too (PDF 32000-1:2008 8.7.4.3). The pattern is
                    // not walked along the path: it is painted once and confined to the stroke outline.
                    if (_strokeTiling is not null)
                    {
                        PaintTiling(path, _strokeTiling, _strokeTilingMatrix, evenOdd, stroke: true);
                    }
                    else if (_strokeShading is { } strokeShading)
                    {
                        _context.Save();
                        _context.ClipStroke(path, _state.Stroke);
                        PaintShading(strokeShading, _strokeShadingMatrix, _streamEntryMatrix);
                        _context.Restore();
                    }
                    else
                    {
                        _context.StrokePath(path, _state.Stroke, StrokePaintColor());
                    }
                }
            }
            finally
            {
                if (masked)
                {
                    EndMaskedElement();
                }
            }
        }

        ClearPath();
    }

    private void ApplyPendingClip()
    {
        if (!_pendingClip)
        {
            return;
        }

        _pendingClip = false;
        ApplyClip(_pendingClipEvenOdd);
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
            PdfObject? oc = ResolveOptionalContent(operand, resources);
            if (oc is not null && !OptionalContentVisible(oc))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A form XObject may carry its own /OC (an OCG or an OCMD membership dictionary). When that optional
    /// content is off the form must not draw at all - pdf.js issue12007_reduced.pdf keeps its two big image
    /// forms under OCGs that its /OCProperties turns off, and drawing them buried the page.
    /// </summary>
    private bool FormOptionalContentVisible(PdfDictionary form)
    {
        PdfObject? oc = form.Get("OC");
        return oc is null || OptionalContentVisible(oc);
    }

    private bool OptionalContentVisible(PdfObject oc)
    {
        // A marked-content property is often an inline dictionary that has nothing to do with layers at
        // all - "/Span << /ActualText (...) >> BDC" and the artifact properties are everywhere - so only a
        // membership dictionary is evaluated. Anything else carries no layer that could be off.
        if (_document.Resolve(oc) is PdfDictionary dictionary)
        {
            switch (dictionary.GetNameValue("Type"))
            {
                case "OCG":
                    // A direct OCG dictionary carries no object number to look up, so nothing can have
                    // turned it off; an indirect one is the layer itself.
                    return oc is not PdfIndirectRef group || _document.IsOptionalContentGroupVisible(group.ObjectNumber);
                case "OCMD":
                    break;
                default:
                    // A marked-content property is often an inline dictionary with nothing to do with
                    // layers - "/Span << /ActualText (...) >> BDC" is everywhere - so it is not hidden.
                    return true;
            }

            var groups = new List<int>();
            CollectOcgNumbers(dictionary.Get("OCGs"), groups);
            if (groups.Count == 0)
            {
                return true;
            }

            bool anyOn = false, allOn = true, anyOff = false, allOff = true;
            foreach (int number in groups)
            {
                bool visible = _document.IsOptionalContentGroupVisible(number);
                anyOn |= visible;
                allOn &= visible;
                anyOff |= !visible;
                allOff &= !visible;
            }

            return (dictionary.GetNameValue("P") ?? "AnyOn") switch
            {
                "AllOn" => allOn,
                "AnyOff" => anyOff,
                "AllOff" => allOff,
                _ => anyOn,
            };
        }

        return oc is PdfIndirectRef reference && _document.IsOptionalContentGroupVisible(reference.ObjectNumber);
    }

    private void CollectOcgNumbers(PdfObject? value, List<int> into)
    {
        switch (value)
        {
            case PdfIndirectRef reference:
                if (_document.Resolve(reference) is PdfArray array)
                {
                    foreach (PdfObject item in array)
                    {
                        CollectOcgNumbers(item, into);
                    }
                }
                else
                {
                    into.Add(reference.ObjectNumber);
                }

                break;

            case PdfArray arrayValue:
                foreach (PdfObject item in arrayValue)
                {
                    CollectOcgNumbers(item, into);
                }

                break;
        }
    }

    /// <summary>
    /// The OCG or membership dictionary a marked-content <c>/OC</c> operand names: the operand itself, or,
    /// when it is a name, the entry in the resource dictionary's /Properties. A membership dictionary has
    /// to be evaluated rather than looked up as a group - pdf.js issue269_2.pdf wraps every layer in an
    /// /OCMD, and asking whether "object 106 is in /OFF" answered yes for all of them, so the layers that
    /// are off painted over the photograph that is on.
    /// </summary>
    private PdfObject? ResolveOptionalContent(PdfObject operand, PdfDictionary? resources)
    {
        PdfObject? value = operand;
        if (operand is PdfName name && (_document.Resolve(resources?.Get("Properties")) as PdfDictionary)?.Get(name.Value) is { } entry)
        {
            value = entry;
        }

        return value is PdfIndirectRef or PdfDictionary ? value : null;
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
        ColorSpaceKind patternBaseKind = ColorSpaceKind.Unknown;

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

            // A pattern space may be written with no base space at all - [/Pattern] - which means the
            // base is whatever the current space is (PDF 32000-1:2008 8.7.3.1). Requiring a second entry
            // left such a space unrecognised, so scn fell through to a solid colour and a shading
            // pattern painted nothing.
            case PdfArray { Count: 1 } patternOnly when patternOnly[0] is PdfName { Value: "Pattern" }:
                kind = ColorSpaceKind.Pattern;
                components = 1;
                break;

            // [/Pattern <base>] names the space an uncoloured (PaintType 2) tiling pattern's cells are
            // painted in, and scn carries the colour for them ahead of the pattern name. pdf.js
            // issue16038.pdf writes [/Pattern /DeviceRGB] and then "0 0 1 /pgfpat21 scn", whose cells
            // stroke a line: read without the base, the line came out in the default black instead of
            // blue, and the whole page of hatching turned grey.
            case PdfArray patternBase when patternBase.Count >= 2 && patternBase[0] is PdfName { Value: "Pattern" }:
                kind = ColorSpaceKind.Pattern;
                (ColorSpaceKind tileBaseKind, int tileBaseComponents) = DescribeBaseSpace(patternBase[1]);
                patternBaseKind = tileBaseKind;
                components = Math.Max(1, tileBaseComponents);
                break;

            case PdfArray array when array.Count >= 2 && array[0] is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased":
                        PdfStream? iccStream = _document.Resolve(array[1]) as PdfStream;
                        int n = Math.Clamp((int)(iccStream?.Dictionary.GetIntegerValue("N") ?? 3), 1, MaxColorComponents);
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
                        components = Math.Clamp((_document.Resolve(array[1]) as PdfArray)?.Count ?? 1, 1, MaxColorComponents);
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

        _patternBaseKind = patternBaseKind;

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
    /// <summary>Base colour space of the active Pattern space, for an uncoloured tiling pattern's colour.</summary>
    private ColorSpaceKind _patternBaseKind = ColorSpaceKind.Unknown;

    /// <summary>
    /// The colour an uncoloured (PaintType 2) tiling pattern's cells are painted in, taken from the operands
    /// scn/SCN carried ahead of the pattern name. A coloured pattern (PaintType 1) paints with its own
    /// operators and gets null here.
    /// </summary>
    private PosoColor? TilingCellColor(Stack<PdfObject> operands, PdfDictionary pattern, bool stroke)
    {
        if ((pattern.GetIntegerValue("PaintType") ?? 1) != 2)
        {
            return null;
        }

        // SelectPattern already popped the pattern name, so what is left is the colour, bottom-first.
        if (operands.Count == 0 || operands.Peek() is PdfName)
        {
            return null;
        }

        var colorOperands = new Stack<PdfObject>(operands.ToArray().Reverse());
        return _patternBaseKind switch
        {
            ColorSpaceKind.Pattern or ColorSpaceKind.Unknown => null,
            _ => ResolveTilingCellColor(colorOperands),
        };
    }

    private PosoColor? ResolveTilingCellColor(Stack<PdfObject> colorOperands)
    {
        int components = _patternBaseKind switch
        {
            ColorSpaceKind.DeviceGray => 1,
            ColorSpaceKind.DeviceCmyk => 4,
            _ => 3,
        };

        if (colorOperands.Count < components)
        {
            return null;
        }

        ApplyColorOperands(colorOperands, _patternBaseKind, components, null, 0, null,
            ColorSpaceKind.Unknown, 0, null, null, null, null, out PosoColor color);
        return color;
    }

    private void ClearPatternColor(bool stroke)
    {
        if (stroke)
        {
            _strokeShading = null;
            _strokeTiling = null;
            _strokeTilingColor = null;
        }
        else
        {
            _fillShading = null;
            _fillTiling = null;
            _fillTilingColor = null;
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
                int n = Math.Clamp((int)((_document.Resolve(array[1]) as PdfStream)?.Dictionary.GetIntegerValue("N") ?? 3), 1, MaxColorComponents);
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

        if (patternType == 1 && patternObject is PdfStream tilingStream)
        {
            // Tiling pattern: the cell is rasterized on demand at paint time (PaintTiling).
            Matrix3x2 matrix = ParsePatternMatrix(pattern);
            PosoColor? cellColor = TilingCellColor(operands, pattern, stroke);
            if (stroke)
            {
                _strokeTiling = tilingStream;
                _strokeTilingMatrix = matrix;
                _strokeTilingColor = cellColor;
            }
            else
            {
                _fillTiling = tilingStream;
                _fillTilingMatrix = matrix;
                _fillTilingColor = cellColor;
            }

            return;
        }

        _diag.Report(PdfUnsupportedKind.PatternColorSpace, "Unsupported pattern (only axial/radial shading and tiling patterns are supported)");
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
        if (operand is PdfName name && (_document.Resolve(resources?.Get("Shading")) as PdfDictionary)?.Get(name.Value) is { } entry)
        {
            shading = Shading.PdfShading.Create(_document, entry);
        }
        else
        {
            shading = Shading.PdfShading.Create(_document, operand);
        }

        if (shading is { IsSupported: true })
        {
            // The shading is defined directly in user space (no pattern matrix), and `sh` is painted by the
            // CTM as it stands - unlike a shading pattern, which is placed by the stream's entry transform.
            PaintShading(shading, Matrix3x2.Identity, _state.Ctm);
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

        PaintShading(shading, matrix, _streamEntryMatrix);
    }

    /// <summary>Device pixels a shading raster may span on one side before it is split.</summary>
    private const int ShadingTile = 1024;

    private void PaintShading(Shading.PdfShading shading, in Matrix3x2 shadingToUser, in Matrix3x2 userToDevice)
    {
        RectF clip = _context.ClipBounds;
        if (clip.IsEmpty)
        {
            return;
        }

        Matrix3x2 shadingToDevice = shadingToUser * userToDevice;

        // A shading is rasterized one image per painted area, and the rasterizer refuses anything taller or
        // wider than 8192 pixels - it would rather lose the shading than allocate it. A page-wide gradient
        // on a large device surface reaches that: pdf.js issue14165.pdf is 1190.52 points across, so at 7x
        // its 8334-pixel width went over and the striped gradient vanished from the page entirely while
        // PDFium still drew it. Tiling the area keeps every raster small and the picture identical, because
        // each tile samples the same shading-to-device transform and lands on its own device pixels.
        for (float y = clip.Top; y < clip.Bottom; y += ShadingTile)
        {
            float tileHeight = Math.Min(ShadingTile, clip.Bottom - y);
            for (float x = clip.Left; x < clip.Right; x += ShadingTile)
            {
                var tile = new RectF(x, y, Math.Min(ShadingTile, clip.Right - x), tileHeight);
                IImage? image = shading.Rasterize(_factory, tile, shadingToDevice);
                if (image is null)
                {
                    continue;
                }

                try
                {
                    _context.Save();
                    _context.SetMatrix(Matrix3x2.Identity);
                    _context.DrawDeviceRaster(image, tile, 1f);
                    _context.Restore();
                }
                finally
                {
                    image.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Fills the path with a Type 1 tiling pattern: rasterizes one cell of the pattern content stream
    /// at device scale, then blits it across the clipped path bounds on the (pattern -> device) lattice.
    /// A stroke paints the same lattice, clipped to the stroke outline instead of the path interior.
    /// </summary>
    private void PaintTiling(IPath path, PdfStream stream, in Matrix3x2 patternMatrix, bool evenOdd, bool stroke)
    {
        PdfDictionary dict = stream.Dictionary;

        float bx0 = 0f, by0 = 0f, bx1 = 100f, by1 = 100f;
        if (dict.GetArray("BBox") is { Count: >= 4 } bbox)
        {
            bx0 = _document.GetArrayNumber(bbox, 0)?.AsFloat() ?? 0f;
            by0 = _document.GetArrayNumber(bbox, 1)?.AsFloat() ?? 0f;
            bx1 = _document.GetArrayNumber(bbox, 2)?.AsFloat() ?? 0f;
            by1 = _document.GetArrayNumber(bbox, 3)?.AsFloat() ?? 0f;
        }

        float cellWidth = bx1 - bx0;
        float cellHeight = by1 - by0;
        float xStep = dict.GetNumber("XStep")?.AsFloat() ?? cellWidth;
        float yStep = dict.GetNumber("YStep")?.AsFloat() ?? cellHeight;
        if (cellWidth <= 0f || cellHeight <= 0f || xStep <= 0f || yStep <= 0f)
        {
            if (stroke)
            {
                _context.StrokePath(path, _state.Stroke, StrokePaintColor());
            }
            else
            {
                _context.FillPath(path, FillPaintColor(), evenOdd);
            }

            return;
        }

        Matrix3x2 dt = patternMatrix * _state.Ctm;
        float sx = MathF.Sqrt((dt.M11 * dt.M11) + (dt.M12 * dt.M12));
        float sy = MathF.Sqrt((dt.M21 * dt.M21) + (dt.M22 * dt.M22));
        if (sx <= 0f || sy <= 0f)
        {
            return;
        }

        // The cell is rasterised at the BBox's device size, not the step's: /XStep and /YStep are the
        // lattice spacing and can far exceed the cell (a "large-steps" pattern draws one cell then a big
        // gap), so sizing the buffer by the step squashes the content and the 4096 cap then mangles it.
        int tw = Math.Clamp((int)MathF.Round(cellWidth * sx), 1, 4096);
        int th = Math.Clamp((int)MathF.Round(cellHeight * sy), 1, 4096);

        Matrix3x2 tileMatrix =
            Matrix3x2.CreateTranslation(-bx0, -by0) *
            Matrix3x2.CreateScale(tw / cellWidth, -(float)th / cellHeight) *
            Matrix3x2.CreateTranslation(0f, th);

        IImage tile;
        using (IGraphicsContext cell = _factory.CreateContext(tw, th))
        {
            var nested = new PdfContentInterpreter(_document, _factory, cell, _options, _sharedForms) { OnUnsupported = OnUnsupported };
            nested.RenderCell(stream.Decode(_diag), _document.Resolve(dict.Get("Resources")) as PdfDictionary, tileMatrix,
                stroke ? _strokeTilingColor : _fillTilingColor);
            tile = cell.Snapshot();
        }

        try
        {
            _context.Save();
            if (stroke)
            {
                _context.ClipStroke(path, _state.Stroke);
            }
            else
            {
                _context.ClipPath(path, evenOdd);
            }

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

            // A pattern /Matrix can rotate or shear the lattice. Flattening each cell to a bounding
            // rectangle drew every cell wrong (pdf.js issue2177.pdf uses [1.4 1 -.5 .7 0 0]), so a sheared
            // matrix places the cells through the pattern-to-device matrix itself: the tile bitmap follows
            // the PDF image convention (first row at the cell's top), which is what the back ends expect of
            // a destination rect given in the y-up pattern space. An axis-aligned matrix keeps the plain
            // device-space blit - the overwhelmingly common case, and it needs no per-tile matrix change.
            bool sheared = _factory.SupportsTransformedImagePlacement &&
                (Math.Abs(dt.M12) > 1e-5f || Math.Abs(dt.M21) > 1e-5f);
            if (sheared)
            {
                _context.SetMatrix(dt);
            }

            for (int i = i0; i <= i1; i++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    // Cell (i, j): the BBox content translated to the (i, j) lattice point. The drawn rect
                    // spans the cell (BBox) extent, not a whole step - with large steps the content is one
                    // small cell placed far apart, and a step-sized rect smears it across the gap.
                    float ox = i * xStep;
                    float oy = j * yStep;
                    if (sheared)
                    {
                        _context.DrawImage(
                            tile,
                            new RectF(bx0 + ox, by0 + oy, cellWidth, cellHeight),
                            new RectF(0f, 0f, tw, th),
                            1f);
                        continue;
                    }

                    Vector2 a = Vector2.Transform(new Vector2(bx0 + ox, by1 + oy), dt);
                    Vector2 c = Vector2.Transform(new Vector2(bx1 + ox, by0 + oy), dt);
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

        // A lookup shorter than one entry leaves nothing to clamp to: maxIndex is -1 and Math.Clamp
        // throws when min > max, which cost pdf.js issue1985.pdf the whole page instead of the colour it
        // does describe. Indexing past the end of a truncated palette is the same case one step later.
        if (maxIndex < 0)
        {
            return PosoColor.Black;
        }

        int clamped = (int)MathF.Round(Math.Clamp(index, 0f, maxIndex));
        int offset = clamped * lookupComponents;
        if (offset + lookupComponents > lookup.Length)
        {
            return PosoColor.Black;
        }

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

    private CalColor ParseCalColor(PdfObject? obj) => CalColor.FromDictionary(obj as PdfDictionary);

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

        if (!FormOptionalContentVisible(form.Dictionary))
        {
            return; // the form's own /OC is off
        }

        if (!_sharedForms.Enter(form))
        {
            return;
        }

        int outerSaves = _context.SaveDepth;
        int outerStateDepth = _stateStack.Count;
        try
        {
            // A form XObject behaves as if its content were wrapped in q/Q
            _context.Save();
            _stateStack.Push(_state.Clone());

            PdfDictionary dictionary = form.Dictionary;

            Matrix3x2 matrix = FormMatrix(form);
            if (!matrix.IsIdentity)
            {
                _state.Ctm = matrix * _state.Ctm;
                _context.ConcatMatrix(matrix);
            }

            if (FormBox(form) is { } box)
            {
                _context.ClipRect(box);
            }

            PdfDictionary? formResources = _document.Resolve(dictionary.Get("Resources")) as PdfDictionary;

            // A form whose dictionary carries a transparency group (/Group /S /Transparency) renders its
            // content on an isolated off-surface that is composited back with the alpha/blend that were in
            // effect at the Do operator. The group opacity is applied once at the boundary, so element
            // alphas inside start from opaque. Back-ends without layer support treat PushLayer as a no-op.
            //
            // An ExtGState /SMask is consumed at that boundary, not left for the content. MuPDF renders a
            // 50% mask over a group at 50%, where the same mask over a group that re-sets the identical
            // mask inside lands at 25%: the group's own pixels carry the mask in force at Do, and the
            // content starts unmasked. A plain form is the opposite - the mask stays in the graphics state
            // and travels into the content, so the form's own /SMask /None can clear it there.
            bool transparencyGroup = IsTransparencyGroup(dictionary);
            byte groupAlpha = (byte)Math.Clamp((int)MathF.Round(_state.FillAlpha * 255f), 0, 255);

            // A non-isolated group (/I false, the spec default) blends its elements against the page
            // beneath, not against an empty buffer. MuPDF gets there by copying the destination into the
            // group's buffer; for a group that is fully opaque the same result falls out of rendering the
            // content inline, with no buffer to seed and no double composite. A group that carries opacity
            // (or a soft mask) still has to composite as one unit at the boundary, so it keeps the layer -
            // separating those cases is what pdf.js knockout_blend_multiply.pdf needs: its cyan rect inside a
            // non-isolated group multiplies with the yellow page and must come out green.
            //
            // The enclosing group being a knockout group (/K true) forces the layer as well: a knockout
            // group clears what it already holds wherever the next element paints, so the nested group must
            // blend with an emptied backdrop instead of with the blue beneath it - which is what the inline
            // path would hand it (pdf.js knockout_inner_backdrop.pdf: its red and green rects inside a
            // nested non-isolated group multiply with the enclosing blue and come out black instead).
            bool pushedLayer = transparencyGroup &&
                (groupAlpha < 255 || _state.SoftMask is not null || GroupIsIsolated(dictionary) || _state.GroupIsKnockout);
            if (pushedLayer)
            {
                IImage? groupMask = _state.SoftMask is { } mask ? SoftMaskImage(mask, _context.ClipBounds) : null;
                _context.PushLayer(null, groupAlpha, _state.BlendMode, groupMask);
                _state.FillAlpha = 1f;
                _state.StrokeAlpha = 1f;
                _state.SoftMask = null;
            }

            if (transparencyGroup)
            {
                _state.GroupIsKnockout = GroupIsKnockout(dictionary);
            }

            Matrix3x2 outerStreamEntry = _streamEntryMatrix;
            _streamEntryMatrix = _state.Ctm;
            try
            {
                ExecuteContent(form.Decode(_diag), formResources);
            }
            finally
            {
                _streamEntryMatrix = outerStreamEntry;

                // A form may legally be left with q still open; the group has to be popped before that
                // stray save level, otherwise the layer never composites back. The stray q also pushed a
                // graphics state that is never Q'd: leave it on _stateStack and the outer Pop below would
                // pop the form's own saved state instead, stranding it (with any transparency / alpha it
                // carried, e.g. a form drawn under ca .5) so the next page-level Q restores it - watching
                // pdf.js issue1905_reduced.pdf paint every following image at 50% opacity did exactly that.
                BalanceCanvasSaves(outerSaves + 1 + (pushedLayer ? 1 : 0));
                while (_stateStack.Count > outerStateDepth + 1)
                {
                    _stateStack.Pop();
                }

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
            _sharedForms.Leave(form);
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
    /// True when a form's transparency group is isolated (/I true). The spec default is false, and a
    /// non-isolated group blends its elements against the page beneath it instead of an empty buffer.
    /// </summary>
    private bool GroupIsIsolated(PdfDictionary dictionary) =>
        (_document.Resolve(dictionary.Get("Group")) as PdfDictionary)?.GetBooleanValue("I") ?? false;

    /// <summary>
    /// True when a form's transparency group knocks out (/K true): each element removes the group's
    /// earlier contents from beneath it. The spec default is false.
    /// </summary>
    private bool GroupIsKnockout(PdfDictionary dictionary) =>
        (_document.Resolve(dictionary.Get("Group")) as PdfDictionary)?.GetBooleanValue("K") ?? false;

    /// <summary>
    /// Renders an ExtGState <c>/SMask</c> group into the coverage image the back ends multiply an
    /// element by: every channel carries the factor, so a grey image is enough. <c>/Luminosity</c> takes
    /// the luminance of the group as it stands - white keeps the element, black erases it - and
    /// <c>/Alpha</c> takes the mask's own alpha. Only a luminosity mask has a <c>/BC</c> backdrop to
    /// composite over; an alpha mask's unpainted area simply contributes no coverage.
    /// </summary>
    private IImage? SoftMaskImage(PdfDictionary mask, in RectF region)
    {
        if (region.IsEmpty)
        {
            return null;
        }

        // The surface covers the device region the back end composites the layer over, and it rounds that
        // region's origin down to a whole pixel; sampling on the same grid is what keeps the mask from
        // landing a fraction of a pixel away from the geometry it masks.
        int originX = (int)MathF.Floor(region.X);
        int originY = (int)MathF.Floor(region.Y);
        int width = Math.Clamp((int)MathF.Ceiling(region.Right) - originX, 1, 4096);
        int height = Math.Clamp((int)MathF.Ceiling(region.Bottom) - originY, 1, 4096);

        (PdfDictionary, RectF) key = (mask, new RectF(originX, originY, width, height));
        if (!_softMasks.TryGetValue(key, out IImage? cached))
        {
            cached = BuildSoftMask(mask, originX, originY, width, height);
            _softMasks[key] = cached;
        }

        return cached;
    }

    private IImage? BuildSoftMask(PdfDictionary dict, int originX, int originY, int width, int height)
    {
        if (_document.Resolve(dict.Get("G")) is not PdfStream form)
        {
            return null;
        }

        byte[] content = form.Decode(_diag);

        // The group is page geometry: the form's own /Matrix, then the CTM that was current when /gs set
        // the mask, then the shift that brings the surface's region origin to the surface corner.
        // Stretching the BBox over the element instead moves the mask wherever the two extents differ.
        Matrix3x2 baseMatrix =
            FormMatrix(form) *
            _state.SoftMaskCtm *
            Matrix3x2.CreateTranslation(-originX, -originY);

        bool byAlpha = dict.GetNameValue("S") == "Alpha";
        PdfFunction? transfer = PdfFunction.Create(_document, dict.Get("TR"));

        // The group's own box, in the surface's pixels. A form's content is clipped to it, so nothing is
        // painted outside; the mask value there is the transfer function applied to the transparent input
        // the group leaves behind, not zero. PDFium paints pdf.js smask_alpha_oob_transfer.pdf that way -
        // its group box is a quarter of the element and its /TR maps 0 to 0.5, so the whole element
        // outside the box comes out at half strength - and pdf.js says the same in as many words
        // ("Alpha SMasks ignore /BC for the alpha output"; the out-of-bounds input alpha is 0 and is put
        // through the transfer map). Scissoring to the box instead loses that, and a mask smaller than
        // its element then erases outside its box rather than lifting.
        RectF? groupBox = FormBox(form) is { } box ? BoundingBoxOf(box, baseMatrix) : null;

        // A /BC backdrop is part of a luminosity mask - it is the colour the group is composited over
        // before its luminance is read - and has no meaning for an alpha mask, where the group's alpha is
        // the coverage.
        PosoColor backdrop = byAlpha ? new PosoColor(0, 0, 0, 0) : SoftMaskBackdrop(dict.GetArray("BC"));

        byte[] pixels;
        using (IGraphicsContext surface = _factory.CreateContext(width, height))
        {
            surface.Clear(backdrop);
            if (groupBox is { } clipped)
            {
                surface.SetMatrix(Matrix3x2.Identity);
                surface.ClipRect(clipped);
            }

            var nested = new PdfContentInterpreter(_document, _factory, surface, _options, _sharedForms) { OnUnsupported = OnUnsupported };
            nested.RenderCell(content, _document.Resolve(form.Dictionary.Get("Resources")) as PdfDictionary, baseMatrix);

            using IImage rendered = surface.Snapshot();
            int stride = rendered.BytesPerPixel;
            pixels = new byte[rendered.Width * rendered.Height * stride];
            rendered.CopyPixels(pixels);
        }

        Span<float> input = stackalloc float[1];
        Span<float> output = stackalloc float[1];

        for (int pixel = 0; pixel * 4 + 3 < pixels.Length; pixel++)
        {
            int i = pixel * 4;

            // The channels arrive premultiplied, so reading them directly is the group composited over
            // the backdrop it was cleared to. A luminosity mask takes that luminance as its coverage:
            // white keeps the element, black erases it. An alpha mask takes the group's own alpha, which
            // is zero wherever the group painted nothing - inside its box or out of it - and that zero
            // goes through the transfer function like any other value.
            float coverage = byAlpha
                ? pixels[i + 3] / 255f
                : ((0.3f * pixels[i + 2]) + (0.59f * pixels[i + 1]) + (0.11f * pixels[i])) / 255f;

            if (transfer is not null)
            {
                input[0] = coverage;
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
    /// Begins the isolated group an ExtGState <c>/SMask</c> calls for around one element. The group's
    /// pixels are multiplied by the mask coverage and then blended back with the element's blend mode, so
    /// the mode moves from the element to the group boundary - inside, the draws composite normally. The
    /// element's own alpha needs no handling here because it is already carried by its colours. Returns
    /// false when nothing masks the element, in which case the caller draws straight to the surface.
    /// </summary>
    private bool BeginMaskedElement()
    {
        if (_state.SoftMask is not { } mask)
        {
            return false;
        }

        IImage? image = SoftMaskImage(mask, _context.ClipBounds);
        if (image is null)
        {
            return false;
        }

        _context.PushLayer(null, 255, _state.BlendMode, image);
        _context.SetBlendMode(BlendMode.Normal);
        return true;
    }

    private void EndMaskedElement()
    {
        _context.PopLayer();
        _context.SetBlendMode(_state.BlendMode);
    }

    /// <summary>A form XObject's own <c>/Matrix</c>, which maps its content space into its parent's.</summary>
    private Matrix3x2 FormMatrix(PdfStream form) =>
        form.Dictionary.GetArray("Matrix") is { Count: >= 6 } matrixArray
            ? new Matrix3x2(
                _document.GetArrayNumber(matrixArray, 0)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(matrixArray, 1)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(matrixArray, 2)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(matrixArray, 3)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(matrixArray, 4)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(matrixArray, 5)?.AsFloat() ?? 0f)
            : Matrix3x2.Identity;

    /// <summary>A form XObject's own <c>/BBox</c>, or null when it declares none.</summary>
    private RectF? FormBox(PdfStream form) =>
        form.Dictionary.GetArray("BBox") is { Count: >= 4 } bbox
            ? RectF.FromLbrb(
                _document.GetArrayNumber(bbox, 0)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(bbox, 1)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(bbox, 2)?.AsFloat() ?? 0f,
                _document.GetArrayNumber(bbox, 3)?.AsFloat() ?? 0f)
            : null;

    /// <summary>The axis-aligned box that encloses a rectangle after a transform has been applied to it.</summary>
    private static RectF BoundingBoxOf(in RectF rect, in Matrix3x2 matrix)
    {
        Vector2 min = new(float.MaxValue, float.MaxValue);
        Vector2 max = new(float.MinValue, float.MinValue);
        Span<Vector2> corners = stackalloc Vector2[]
        {
            new(rect.Left, rect.Top), new(rect.Right, rect.Top),
            new(rect.Left, rect.Bottom), new(rect.Right, rect.Bottom),
        };

        foreach (Vector2 corner in corners)
        {
            Vector2 point = Vector2.Transform(corner, matrix);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        return new RectF(min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }

    /// <summary>
    /// The <c>/BC</c> backdrop of a luminosity soft-mask group. Omitting it means opaque black in the mask
    /// colour space, which under a luminosity mask erases wherever the group paints nothing.
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

        // An image mask is a stencil: what paints through it is the current fill, and a fill that is a
        // pattern cannot be folded into the raster as a flat colour.
        if ((image.Dictionary.GetBooleanValue("ImageMask") ?? false) &&
            (_fillShading is not null || _fillTiling is not null))
        {
            DrawImageMaskThroughPattern(image);
            return;
        }

        IImage? decoded = PdfImageDecoder.Decode(_document, _factory, image, image.Dictionary, _state.FillColor, _diag);
        if (decoded is null)
        {
            return;
        }

        try
        {
            bool masked = CarriesOwnMask(image) ? false : BeginMaskedElement();
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
                if (masked)
                {
                    EndMaskedElement();
                }
            }
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>
    /// An ImageMask XObject shown while the fill colour is a pattern. The mask selects where the pattern
    /// paints (ISO 32000-1 8.9.6.3), so the stencil is resampled onto the device grid as a layer mask and
    /// the pattern is painted through it. pdf.js issue13372.pdf sets a shading pattern and then draws a
    /// 646x761 bilevel mask; folding the pattern into the stencil colour painted the whole block instead.
    /// </summary>
    private void DrawImageMaskThroughPattern(PdfStream image)
    {
        using IImage? decoded = PdfImageDecoder.Decode(_document, _factory, image, image.Dictionary, PosoColor.White, _diag);
        if (decoded is null)
        {
            return;
        }

        var maskPixels = new byte[decoded.Width * decoded.Height * 4];
        decoded.CopyPixels(maskPixels);

        RectF region = _context.ClipBounds;
        if (region.IsEmpty || !Matrix3x2.Invert(_state.Ctm, out Matrix3x2 deviceToUser))
        {
            return;
        }

        int originX = (int)MathF.Floor(region.X);
        int originY = (int)MathF.Floor(region.Y);
        int width = Math.Clamp((int)MathF.Ceiling(region.Right) - originX, 1, 4096);
        int height = Math.Clamp((int)MathF.Ceiling(region.Bottom) - originY, 1, 4096);

        // The image occupies the unit square of the space at the Do operator, so each device pixel maps
        // back into it through the CTM; the first sample row sits at v = 1.
        var coverage = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector2 u = Vector2.Transform(new Vector2(originX + x + 0.5f, originY + y + 0.5f), deviceToUser);
                byte value = 0;
                if (u.X >= 0f && u.X < 1f && u.Y >= 0f && u.Y < 1f)
                {
                    int sx = Math.Clamp((int)(u.X * decoded.Width), 0, decoded.Width - 1);
                    int sy = Math.Clamp((int)((1f - u.Y) * decoded.Height), 0, decoded.Height - 1);
                    value = maskPixels[((sy * decoded.Width) + sx) * 4 + 3];
                }

                int o = ((y * width) + x) * 4;
                coverage[o] = value;
                coverage[o + 1] = value;
                coverage[o + 2] = value;
                coverage[o + 3] = 255;
            }
        }

        using IImage stencil = _factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, coverage);

        var square = new PathBuilder();
        square.MoveTo(0f, 0f);
        square.LineTo(1f, 0f);
        square.LineTo(1f, 1f);
        square.LineTo(0f, 1f);
        square.Close();
        IPath unit = square.Build();

        _context.PushLayer(null, 255, _state.BlendMode, stencil);
        try
        {
            _context.Save();
            _context.SetMatrix(_state.Ctm);
            _context.ClipPath(unit, false);
            if (_fillShading is { } shading)
            {
                PaintShading(shading, _fillShadingMatrix, _streamEntryMatrix);
            }
            else if (_fillTiling is { } tiling)
            {
                PaintTiling(unit, tiling, _fillTilingMatrix, false, stroke: false);
            }

            _context.Restore();
        }
        finally
        {
            _context.PopLayer();
        }
    }

    /// <summary>
    /// Whether an image dictionary carries a mask of its own. MuPDF lets that mask stand in for the
    /// ExtGState <c>/SMask</c> and skips the group mask rather than multiply both in, so an image with an
    /// <c>/SMask</c> of its own is the one element on a masked page that keeps its full strength.
    /// </summary>
    private static bool CarriesOwnMask(PdfStream image) =>
        image.Dictionary.Get("SMask") is not null || image.Dictionary.Get("Mask") is not null;

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

        // Binary data runs until whitespace followed by "EI". The single white-space character that
        // separates ID from its data is not part of the data (a CRLF counts as that one line ending).
        // Including it shifted every inline image by one byte, so pdf.js issue11124.pdf - whose samples
        // legitimately contain the characters "EI " (not preceded by white space) - decoded off by one
        // and its colours scrambled.
        int dataStart = lexer.Position;
        if (dataStart < lexer.Data.Length && lexer.Data[dataStart] == (byte)'\r' &&
            dataStart + 1 < lexer.Data.Length && lexer.Data[dataStart + 1] == (byte)'\n')
        {
            dataStart += 2;
        }
        else if (dataStart < lexer.Data.Length && PdfLexer.IsWhiteSpace(lexer.Data[dataStart]))
        {
            dataStart++;
        }

        int dataEnd = FindInlineImageEnd(lexer.Data, dataStart);
        byte[] raw = lexer.Data.AsSpan(dataStart, dataEnd - dataStart).ToArray();

        int eI = PdfParser.IndexOfKeyword(lexer.Data, dataEnd, "EI"u8);
        lexer.Position = eI >= 0 ? eI + 2 : lexer.Data.Length;

        IImage? decoded = PdfImageDecoder.DecodeInline(_document, _factory, new PdfDictionary(dictionary), raw, _state.FillColor, _diag);
        if (decoded is not null && !_suppressContent)
        {
            try
            {
                bool masked = dictionary.ContainsKey("SMask") || dictionary.ContainsKey("Mask")
                    ? false
                    : BeginMaskedElement();
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
                    if (masked)
                    {
                        EndMaskedElement();
                    }
                }
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

    private PdfObject? ResolveResource(PdfDictionary? resources, string category, PdfName? name)
    {
        if (name is null || resources is null)
        {
            return null;
        }

        // The category dictionary may itself sit behind an indirect reference (ISO 32000-1 7.3.1 lets any
        // resource be direct or indirect): pdf.js issue18529.pdf writes /ExtGState 18 0 R, and a lookup
        // accepting only an inline dictionary dropped every gs operator in the file - with the /SMask going
        // unnoticed and its element painted at full strength instead of faded.
        return (_document.Resolve(resources.Get(category)) as PdfDictionary)?.Get(name.Value);
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
