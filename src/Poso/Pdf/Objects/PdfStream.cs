namespace Poso.Pdf.Objects;

/// <summary>PDF stream object: a dictionary plus raw (undecoded) bytes.</summary>
public sealed class PdfStream : PdfObject
{
    private readonly PdfDictionary _dictionary;
    private readonly byte[] _rawData;
    private byte[]? _decoded;
    private readonly object _decodeGate = new();

    public PdfStream(PdfDictionary dictionary, byte[] rawData)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _rawData = rawData ?? throw new ArgumentNullException(nameof(rawData));
    }

    public PdfDictionary Dictionary => _dictionary;

    /// <summary>Raw bytes before filter decoding.</summary>
    public ReadOnlySpan<byte> RawData => _rawData;

    /// <summary>
    /// Apply the filter chain to decode and cache the result.
    /// Image codecs such as DCT/JPX/CCITT/JBIG2 are not handled in the core; the data is returned as-is to the imaging layer.
    /// </summary>
    public byte[] Decode()
    {
        if (_decoded is not null)
        {
            return _decoded;
        }

        lock (_decodeGate)
        {
            _decoded ??= Filters.PdfFilters.Apply(this);
            return _decoded;
        }
    }

    public override PdfObjectType Type => PdfObjectType.Stream;
}
