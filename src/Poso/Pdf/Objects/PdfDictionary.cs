namespace Poso.Pdf.Objects;

/// <summary>
/// PDF dictionary object. Key names are stored without the leading slash; a key whose value is <see cref="PdfNull"/> is treated as absent (PDF specification convention).
/// </summary>
public sealed class PdfDictionary : PdfObject
{
    private readonly Dictionary<string, PdfObject> _map;

    public PdfDictionary()
    {
        _map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
    }

    public PdfDictionary(Dictionary<string, PdfObject> map)
    {
        _map = map ?? new Dictionary<string, PdfObject>(StringComparer.Ordinal);
    }

    public int Count => _map.Count;

    public IEnumerable<string> Keys => _map.Keys;

    public PdfObject? this[string key]
    {
        get => Get(key);
        set
        {
            if (value is null or PdfNull)
            {
                _map.Remove(key);
            }
            else
            {
                _map[key] = value;
            }
        }
    }

    public bool Contains(string key) => _map.ContainsKey(key);

    public PdfObject? Get(string key) => _map.TryGetValue(key, out var value) ? value : null;

    public T? GetAs<T>(string key) where T : PdfObject => Get(key) as T;

    public PdfName? GetName(string key) => Get(key) as PdfName;

    public PdfNumber? GetNumber(string key) => Get(key) as PdfNumber;

    public PdfString? GetString(string key) => Get(key) as PdfString;

    public PdfArray? GetArray(string key) => Get(key) as PdfArray;

    public PdfDictionary? GetDictionary(string key) => Get(key) as PdfDictionary;

    public PdfStream? GetStream(string key) => Get(key) as PdfStream;

    public PdfIndirectRef? GetReference(string key) => Get(key) as PdfIndirectRef;

    public string? GetNameValue(string key) => (Get(key) as PdfName)?.Value;

    public double? GetNumberValue(string key) => (Get(key) as PdfNumber)?.Value;

    public long? GetIntegerValue(string key) => (Get(key) as PdfNumber)?.AsLong();

    public bool? GetBooleanValue(string key) => (Get(key) as PdfBoolean)?.Value;

    public override PdfObjectType Type => PdfObjectType.Dictionary;

    public override string ToString() => "<<" + string.Join(" ", _map.Select(kv => "/" + kv.Key + " " + kv.Value)) + ">>";
}
