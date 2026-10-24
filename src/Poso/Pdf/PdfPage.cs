using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf.Objects;

namespace Poso.Pdf;

/// <summary>
/// PDF page: size (MediaBox/CropBox), rotation, content streams and resources.
/// Inheritable attributes (Resources/MediaBox/CropBox/Rotate) are merged during page tree traversal
/// and passed in here.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _dictionary;
    private readonly Dictionary<string, PdfObject> _inherited;
    private byte[]? _contentCache;
    private PdfTextPage? _textPage;

    internal PdfPage(PdfDocument document, int index, PdfDictionary dictionary, Dictionary<string, PdfObject> inherited)
    {
        _document = document;
        Index = index;
        _dictionary = dictionary;
        _inherited = inherited;

        RectF declaredMedia = ParseBox("MediaBox") ?? DefaultBox;
        _mediaBox = declaredMedia.IsEmpty ? DefaultBox : declaredMedia;

        // The visible page is the crop box clipped to the media box, and a file whose boxes leave
        // nothing - an empty /CropBox, or one wholly outside the media box - is rendered from the
        // default box rather than as a zero-sized page that no back end can draw.
        RectF crop = ParseBox("CropBox") is { } declaredCrop ? declaredCrop.Intersect(_mediaBox) : _mediaBox;
        _cropBox = crop.IsEmpty ? DefaultBox : crop;
        _rotation = NormalizeRotation(inherited.TryGetValue("Rotate", out var rotate) && rotate is PdfNumber rotateNumber ? rotateNumber.AsInt() : 0);
        _userUnit = UserUnitOf(inherited);
    }

    /// <summary>The page box MuPDF falls back to when the file's own boxes describe nothing.</summary>
    private static readonly RectF DefaultBox = RectF.FromLbrb(0, 0, 612, 792); // Letter

    private readonly RectF _mediaBox;
    private readonly RectF _cropBox;
    private readonly int _rotation;

    /// <summary>
    /// <c>/UserUnit</c> (PDF 32000-1:2008 7.7.2.3, ExtensionLevel 3): the number of default user space
    /// units per point, which rescales the box coordinates into real ones. It is inheritable, and a
    /// value outside 1..the implementation limit is treated as absent.
    /// </summary>
    private static float UserUnitOf(Dictionary<string, PdfObject> inherited) =>
        inherited.TryGetValue("UserUnit", out var value) && value is PdfNumber number
            ? Math.Clamp(number.AsFloat(), 1f, 14400f)
            : 1f;

    private readonly float _userUnit;

    /// <summary>Zero-based index of the page within the document.</summary>
    public int Index { get; }

    public PdfDocument Document => _document;

    /// <summary>Page dictionary (without inheritable attributes).</summary>
    public PdfDictionary Dictionary => _dictionary;

    /// <summary>Media box.</summary>
    public RectF MediaBox => _mediaBox;

    /// <summary>Crop box (defaults to the media box).</summary>
    public RectF CropBox => _cropBox;

    /// <summary>Page rotation (normalized to 0/90/180/270).</summary>
    public int Rotation => _rotation;

    /// <summary>
    /// The ratio from the page dictionary's units to default user space units. Boxes are stored as they
    /// are written; <see cref="Width"/> and <see cref="Height"/> report the scaled, real size.
    /// </summary>
    public float UserUnit => _userUnit;

    /// <summary>Page width after rotation (in default user space units).</summary>
    public float Width => (_rotation is 90 or 270 ? _cropBox.Height : _cropBox.Width) * _userUnit;

    /// <summary>Page height after rotation (in default user space units).</summary>
    public float Height => (_rotation is 90 or 270 ? _cropBox.Width : _cropBox.Height) * _userUnit;

    /// <summary>Page resource dictionary (XObjects, fonts, etc.).</summary>
    public PdfDictionary? Resources =>
        _inherited.TryGetValue("Resources", out var resources) ? _document.Resolve(resources) as PdfDictionary : null;

    /// <summary>Page content stream sequence (/Contents may be a stream or an array of streams).</summary>
    public IReadOnlyList<PdfStream> GetContentStreams()
    {
        var result = new List<PdfStream>();
        switch (_document.Resolve(_dictionary.Get("Contents")))
        {
            case PdfStream stream:
                result.Add(stream);
                break;

            case PdfArray array:
                foreach (PdfObject item in array)
                {
                    if (_document.Resolve(item) is PdfStream itemStream)
                    {
                        result.Add(itemStream);
                    }
                }

                break;
        }

        return result;
    }

    /// <summary>Concatenated and decoded page content stream (cached).</summary>
    public byte[] GetContent(PdfDiagnostics? diagnostics = null)
    {
        if (_contentCache is not null)
        {
            return _contentCache;
        }

        IReadOnlyList<PdfStream> streams = GetContentStreams();
        if (streams.Count == 0)
        {
            _contentCache = Array.Empty<byte>();
        }
        else if (streams.Count == 1)
        {
            _contentCache = streams[0].Decode(diagnostics);
        }
        else
        {
            using var combined = new MemoryStream();
            foreach (PdfStream stream in streams)
            {
                byte[] decoded = stream.Decode(diagnostics);
                combined.Write(decoded, 0, decoded.Length);
                combined.WriteByte((byte)'\n');
            }

            _contentCache = combined.ToArray();
        }

        return _contentCache;
    }

    /// <summary>Gets an inheritable attribute (the merged page tree result).</summary>
    internal PdfObject? GetInherited(string key) => _inherited.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Renders this page into a drawing context. The context should be sized to the rotated
    /// page size multiplied by <see cref="PdfRenderOptions.Scale"/>.
    /// </summary>
    public void Render(IGraphicsFactory factory, IGraphicsContext context, in PdfRenderOptions options, Action<PdfUnsupportedFeature>? onUnsupported = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(context);

        var interpreter = new Content.PdfContentInterpreter(Document, factory, context, options)
        {
            OnUnsupported = onUnsupported,
        };
        interpreter.RenderPage(this);
        interpreter.RenderAnnotations(this);
    }

    /// <summary>Link annotations of this page (lazily loaded).</summary>
    public IReadOnlyList<PdfLink> Links => _links ??= PdfLink.Load(Document, this);

    private IReadOnlyList<PdfLink>? _links;

    /// <summary>
    /// The page's extracted text when <see cref="GetText"/> has already run, otherwise null. Never
    /// extracts: text extraction costs tens of milliseconds on a text-heavy page, so a caller that
    /// asks from an input handler uses this to stay non-blocking.
    /// </summary>
    public PdfTextPage? PeekText() => _textPage;

    /// <summary>
    /// Extracts the text of this page (cached). Runs the content interpreter with a no-op
    /// drawing context, so only text operators matter.
    /// </summary>
    public PdfTextPage GetText(IGraphicsFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (_textPage is not null)
        {
            return _textPage;
        }

        var interpreter = new Content.PdfContentInterpreter(
            Document,
            factory,
            new Content.NullGraphicsContext(),
            PdfRenderOptions.Default);
        var collector = new TextCollector();
        interpreter.TextSink = collector;
        interpreter.RenderPage(this);
        collector.Finish();

        _textPage = collector.Page;
        return _textPage;
    }

    /// <summary>
    /// One of the page's boxes. A box may be written as an indirect reference, and its elements may be
    /// indirect too (/MediaBox [4 0 R 5 0 R 6 0 R 7 0 R]), so both levels are resolved here rather than
    /// read as a type test.
    /// </summary>
    private RectF? ParseBox(string key)
    {
        if (!_inherited.TryGetValue(key, out var value) || value is PdfNull)
        {
            return null;
        }

        return _document.Resolve(value) switch
        {
            PdfArray array when array.Count >= 4 && _document.GetArrayNumber(array, 0) is not null => RectF.FromLbrb(
                _document.GetArrayNumber(array, 0)?.AsFloat() ?? 0,
                _document.GetArrayNumber(array, 1)?.AsFloat() ?? 0,
                _document.GetArrayNumber(array, 2)?.AsFloat() ?? 0,
                _document.GetArrayNumber(array, 3)?.AsFloat() ?? 0),
            _ => null,
        };
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
