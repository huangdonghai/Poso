using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;

namespace Poso.Pdf.Structure;

/// <summary>Object stream (/Type /ObjStm): stores indirect objects inside a container as an "N offset" header followed by consecutive objects.</summary>
public sealed class PdfObjectStream
{
    private readonly int _objectNumber;
    private readonly PdfStream _stream;
    private readonly Func<PdfIndirectRef, PdfObject?> _resolver;
    private byte[]? _data;
    private int _first;
    private Dictionary<int, int>? _index;
    private readonly Dictionary<int, PdfObject> _cache = new();

    public PdfObjectStream(int objectNumber, PdfStream stream, Func<PdfIndirectRef, PdfObject?> resolver)
    {
        _objectNumber = objectNumber;
        _stream = stream;
        _resolver = resolver;
    }

    public int ObjectNumber => _objectNumber;

    public PdfObject? GetObject(int objectNumber)
    {
        if (_cache.TryGetValue(objectNumber, out var cached))
        {
            return cached;
        }

        EnsureLoaded();
        if (_index is null || !_index.TryGetValue(objectNumber, out int offset))
        {
            return null;
        }

        var parser = new PdfParser(_data!, _first + offset) { Resolver = _resolver };
        PdfObject value = parser.ParseObject();
        _cache[objectNumber] = value;
        return value;
    }

    private void EnsureLoaded()
    {
        if (_data is not null)
        {
            return;
        }

        _data = _stream.Decode();
        long count = ResolveInteger(_stream.Dictionary.Get("N")) ?? 0;
        _first = (int)(ResolveInteger(_stream.Dictionary.Get("First")) ?? 0);

        var index = new Dictionary<int, int>((int)Math.Min(count, 4096));
        var lexer = new PdfLexer(_data, 0, _first);
        for (long i = 0; i < count; i++)
        {
            var numberToken = lexer.NextToken();
            var offsetToken = lexer.NextToken();
            if (numberToken.Kind != PdfTokenKind.Integer || offsetToken.Kind != PdfTokenKind.Integer)
            {
                break;
            }

            index[(int)numberToken.IntegerValue] = (int)offsetToken.IntegerValue;
        }

        _index = index;
    }

    private long? ResolveInteger(PdfObject? value) => value switch
    {
        PdfNumber number => number.AsLong(),
        PdfIndirectRef reference => (_resolver(reference) as PdfNumber)?.AsLong(),
        _ => null,
    };
}
