using Poso.Pdf.Objects;

namespace Poso.Pdf.Structure;

/// <summary>Document catalog (/Root): entry point for the page tree root, outlines, named destinations, etc.</summary>
public sealed class PdfCatalog
{
    private readonly PdfDocument _document;

    private PdfCatalog(PdfDocument document, PdfDictionary dictionary)
    {
        _document = document;
        Dictionary = dictionary;
    }

    public static PdfCatalog Load(PdfDocument document)
    {
        PdfDictionary? dictionary = document.Catalog;
        if (dictionary is null)
        {
            throw new PdfFormatException("The document is missing a catalog (/Root)");
        }

        return new PdfCatalog(document, dictionary);
    }

    public PdfDictionary Dictionary { get; }

    /// <summary>Version override (/Version, e.g. "1.7").</summary>
    public string? Version => Dictionary.GetNameValue("Version");

    /// <summary>Page tree root node.</summary>
    public PdfDictionary? PageTreeRoot => _document.Resolve(Dictionary.Get("Pages")) as PdfDictionary;

    /// <summary>Outline root (/Outlines).</summary>
    public PdfDictionary? Outlines => _document.Resolve(Dictionary.Get("Outlines")) as PdfDictionary;

    /// <summary>Page mode (/PageMode, e.g. /UseOutlines).</summary>
    public string? PageMode => Dictionary.GetNameValue("PageMode");

    /// <summary>Page layout (/PageLayout, e.g. /SinglePage /OneColumn).</summary>
    public string? PageLayout => Dictionary.GetNameValue("PageLayout");

    /// <summary>Traditional named destinations dictionary (/Dests).</summary>
    public PdfDictionary? Dests => _document.Resolve(Dictionary.Get("Dests")) as PdfDictionary;

    /// <summary>Name tree root (/Names, whose /Dests child is the named destination tree).</summary>
    public PdfDictionary? Names => _document.Resolve(Dictionary.Get("Names")) as PdfDictionary;

    /// <summary>Look up a named destination by name: first the /Dests dictionary, then the /Names/Dests name tree.</summary>
    public PdfObject? LookupNamedDestination(string name)
    {
        PdfDictionary? dests = Dests;
        if (dests is not null)
        {
            if (dests.Get(name) is { } direct)
            {
                return direct;
            }

            // Some files store named destinations in the /Names subtree of /Dests.
            if (dests.GetDictionary("Names") is { } namesRoot)
            {
                PdfObject? found = LookupNameTree(namesRoot, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        PdfDictionary? names = Names;
        if (names?.GetDictionary("Dests") is { } destTree)
        {
            return LookupNameTree(destTree, name);
        }

        return null;
    }

    /// <summary>Name tree lookup: /Names is [key1 value1 key2 value2 ...], /Kids is an array of subtrees.</summary>
    public PdfObject? LookupNameTree(PdfDictionary node, string name)
    {
        var queue = new Queue<PdfDictionary>();
        queue.Enqueue(node);
        int guard = 0;

        while (queue.Count > 0 && guard++ < 100_000)
        {
            PdfDictionary current = queue.Dequeue();

            PdfArray? names = _document.Resolve(current.Get("Names")) as PdfArray;
            if (names is not null)
            {
                for (int i = 0; i + 1 < names.Count; i += 2)
                {
                    if (names[i] is PdfString key && key.AsTextString() == name)
                    {
                        return _document.Resolve(names[i + 1]);
                    }
                }
            }

            PdfArray? kids = _document.Resolve(current.Get("Kids")) as PdfArray;
            if (kids is not null)
            {
                foreach (PdfObject kid in kids)
                {
                    if (_document.Resolve(kid) is PdfDictionary kidDictionary)
                    {
                        queue.Enqueue(kidDictionary);
                    }
                }
            }
        }

        return null;
    }
}
