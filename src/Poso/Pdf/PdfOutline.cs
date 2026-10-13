using Poso.Pdf.Objects;

namespace Poso.Pdf;

/// <summary>
/// An outline (bookmark) node: a title plus an optional destination, with child items.
/// The document root outline has an empty title and only children.
/// </summary>
public sealed class PdfOutline
{
    private PdfOutline(string title, PdfDestination? destination, IReadOnlyList<PdfOutline> children, bool expanded)
    {
        Title = title;
        Destination = destination;
        Children = children;
        Expanded = expanded;
    }

    public string Title { get; }

    public PdfDestination? Destination { get; }

    public IReadOnlyList<PdfOutline> Children { get; }

    /// <summary>True when the node is open (/Count is positive).</summary>
    public bool Expanded { get; }

    /// <summary>Loads the outline tree from the document catalog; returns null when absent.</summary>
    public static PdfOutline? Load(PdfDocument document)
    {
        PdfDictionary? root = document.CatalogInfo.Outlines;
        if (root is null)
        {
            return null;
        }

        var builder = new OutlineBuilder(document);
        List<PdfOutline> children = builder.LoadChildren(root, maxItems: 100_000);
        return new PdfOutline(string.Empty, null, children, expanded: true);
    }

    private sealed class OutlineBuilder
    {
        private readonly PdfDocument _document;
        private int _itemCount;

        public OutlineBuilder(PdfDocument document)
        {
            _document = document;
        }

        public List<PdfOutline> LoadChildren(PdfDictionary node, int maxItems)
        {
            var children = new List<PdfOutline>();
            PdfObject? first = _document.Resolve(node.Get("First"));
            var visited = new HashSet<PdfDictionary>();

            while (first is PdfDictionary item && visited.Add(item) && _itemCount < maxItems)
            {
                _itemCount++;
                children.Add(LoadItem(item));
                first = _document.Resolve(item.Get("Next"));
            }

            return children;
        }

        private PdfOutline LoadItem(PdfDictionary item)
        {
            string title = (_document.Resolve(item.Get("Title")) as PdfString)?.AsTextString() ?? string.Empty;

            PdfDestination? destination = null;
            if (item.Get("Dest") is not null)
            {
                destination = PdfDestination.Parse(_document, item.Get("Dest"));
            }
            else if (_document.Resolve(item.Get("A")) is PdfDictionary action &&
                     action.GetNameValue("S") == "GoTo")
            {
                destination = PdfDestination.Parse(_document, action.Get("D"));
            }

            long count = item.GetIntegerValue("Count") ?? 0;
            List<PdfOutline> children = LoadChildren(item, maxItems: 100_000);

            return new PdfOutline(title, destination, children, expanded: count > 0);
        }
    }
}
