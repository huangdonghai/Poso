namespace Poso.Pdf.Objects;

/// <summary>PDF stream object: a dictionary plus raw (undecoded) bytes.</summary>
public sealed class PdfStream : PdfObject
{
    private readonly PdfDictionary _dictionary;
    private readonly byte[] _rawData;
    private byte[]? _decoded;
    private readonly object _decodeGate = new();
    private Security.PdfEncryptionContext? _encryption;
    private int _objectId;
    private int _objectGeneration;

    public PdfStream(PdfDictionary dictionary, byte[] rawData)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _rawData = rawData ?? throw new ArgumentNullException(nameof(rawData));
    }

    /// <summary>Attaches the shared encryption context so the stream data can be decrypted on first decode.</summary>
    internal void AttachEncryption(Security.PdfEncryptionContext encryption, int objectNumber, int generation)
    {
        _encryption = encryption;
        _objectId = objectNumber;
        _objectGeneration = generation;
    }

    public PdfDictionary Dictionary => _dictionary;

    /// <summary>Raw bytes before filter decoding.</summary>
    public ReadOnlySpan<byte> RawData => _rawData;

    /// <summary>
    /// Apply the filter chain to decode and cache the result.
    /// Image codecs such as DCT/JPX/CCITT/JBIG2 are not handled in the core; the data is returned as-is to the imaging layer.
    /// </summary>
    public byte[] Decode(PdfDiagnostics? diagnostics = null)
    {
        if (_decoded is not null)
        {
            return _decoded;
        }

        lock (_decodeGate)
        {
            if (_decoded is null)
            {
                byte[] baseData = _encryption is { } encryption
                    ? encryption.DecryptStreamIfNeeded(_rawData, _dictionary, _objectId, _objectGeneration)
                    : _rawData;
                _decoded = Filters.PdfFilters.Apply(baseData, _dictionary, diagnostics);
            }

            return _decoded;
        }
    }

    public override PdfObjectType Type => PdfObjectType.Stream;
}
