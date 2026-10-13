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

        _mediaBox = ParseBox(inherited, "MediaBox") ?? RectF.FromLbrb(0, 0, 612, 792); // Default: Letter
        _cropBox = ParseBox(inherited, "CropBox") ?? _mediaBox;
        _rotation = NormalizeRotation(inherited.TryGetValue("Rotate", out var rotate) && rotate is PdfNumber rotateNumber ? rotateNumber.AsInt() : 0);
    }

    private readonly RectF _mediaBox;
    private readonly RectF _cropBox;
    private readonly int _rotation;

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

    /// <summary>Page width after rotation (in points).</summary>
    public float Width => _rotation is 90 or 270 ? _cropBox.Height : _cropBox.Width;

    /// <summary>Page height after rotation (in points).</summary>
    public float Height => _rotation is 90 or 270 ? _cropBox.Width : _cropBox.Height;

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
    public byte[] GetContent()
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
            _contentCache = streams[0].Decode();
        }
        else
        {
            using var combined = new MemoryStream();
            foreach (PdfStream stream in streams)
            {
                byte[] decoded = stream.Decode();
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
    public void Render(IGraphicsFactory factory, IGraphicsContext context, in PdfRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(context);

        var interpreter = new Content.PdfContentInterpreter(Document, factory, context, options);
        interpreter.RenderPage(this);
    }

    /// <summary>Link annotations of this page (lazily loaded).</summary>
    public IReadOnlyList<PdfLink> Links => _links ??= PdfLink.Load(Document, this);

    private IReadOnlyList<PdfLink>? _links;

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

    private static RectF? ParseBox(Dictionary<string, PdfObject> inherited, string key)
    {
        if (!inherited.TryGetValue(key, out var value) || value is PdfNull)
        {
            return null;
        }

        return value switch
        {
            PdfArray array when array.Count >= 4 && array[0] is PdfNumber => RectF.FromLbrb(
                array.GetNumber(0)?.AsFloat() ?? 0,
                array.GetNumber(1)?.AsFloat() ?? 0,
                array.GetNumber(2)?.AsFloat() ?? 0,
                array.GetNumber(3)?.AsFloat() ?? 0),
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
