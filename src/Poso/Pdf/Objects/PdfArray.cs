using System.Collections;

namespace Poso.Pdf.Objects;

/// <summary>PDF array object.</summary>
public sealed class PdfArray : PdfObject, IEnumerable<PdfObject>
{
    private readonly List<PdfObject> _items;

    public PdfArray(IEnumerable<PdfObject> items)
    {
        _items = new List<PdfObject>(items);
    }

    public int Count => _items.Count;

    public PdfObject this[int index] => _items[index];

    public IReadOnlyList<PdfObject> Items => _items;

    public PdfNumber? GetNumber(int index) => index >= 0 && index < _items.Count ? _items[index] as PdfNumber : null;

    public PdfName? GetName(int index) => index >= 0 && index < _items.Count ? _items[index] as PdfName : null;

    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public override PdfObjectType Type => PdfObjectType.Array;

    public override string ToString() => "[" + string.Join(" ", _items) + "]";
}
