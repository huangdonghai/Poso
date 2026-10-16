using Poso.Pdf.Objects;

namespace Poso.Pdf.Structure;

/// <summary>Page tree traversal: handles inheritable attributes (Resources/MediaBox/CropBox/Rotate) of /Pages intermediate nodes and /Page leaves.</summary>
public static class PdfPageTree
{
    private const int MaxPages = 5_000_000;

    /// <summary>Inheritable page attributes defined by the PDF specification.</summary>
    private static readonly string[] InheritableKeys = { "Resources", "MediaBox", "CropBox", "Rotate", "UserUnit" };

    public static List<PdfPage> LoadPages(PdfDocument document)
    {
        PdfDictionary? catalog = document.Catalog;
        if (catalog is null)
        {
            throw new PdfFormatException("The document is missing a catalog (/Root)");
        }

        PdfDictionary? root = document.Resolve(catalog.Get("Pages")) as PdfDictionary;
        if (root is null)
        {
            throw new PdfFormatException("The document catalog is missing the page tree root (/Pages)");
        }

        var pages = new List<PdfPage>();
        var inherited = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        Visit(document, root, inherited, pages);
        return pages;
    }

    private static void Visit(PdfDocument document, PdfDictionary node, Dictionary<string, PdfObject> inherited, List<PdfPage> pages)
    {
        if (pages.Count >= MaxPages)
        {
            throw new PdfFormatException($"The page count exceeds the limit ({MaxPages}); the file may be corrupt");
        }

        // Merge the current node's inheritable attributes (the node itself takes precedence over ancestors).
        var merged = MergeInheritable(node, inherited);

        PdfObject? kids = document.Resolve(node.Get("Kids"));
        if (kids is not PdfArray kidsArray)
        {
            // Leaf page.
            pages.Add(new PdfPage(document, pages.Count, node, merged));
            return;
        }

        foreach (PdfObject kid in kidsArray)
        {
            PdfDictionary? kidDictionary = document.Resolve(kid) as PdfDictionary;
            if (kidDictionary is null)
            {
                continue;
            }

            // Merge the child node's own inheritable attributes as well (leaf pages often carry their own /MediaBox).
            Dictionary<string, PdfObject> kidInherited = MergeInheritable(kidDictionary, merged);

            // A node with /Kids is treated as an intermediate node (some files omit /Type).
            if (document.Resolve(kidDictionary.Get("Kids")) is PdfArray)
            {
                Visit(document, kidDictionary, kidInherited, pages);
            }
            else
            {
                pages.Add(new PdfPage(document, pages.Count, kidDictionary, kidInherited));
            }
        }
    }

    private static Dictionary<string, PdfObject> MergeInheritable(PdfDictionary node, Dictionary<string, PdfObject> inherited)
    {
        var merged = new Dictionary<string, PdfObject>(inherited, StringComparer.Ordinal);
        foreach (string key in InheritableKeys)
        {
            if (node.Get(key) is { } value && value is not PdfNull)
            {
                merged[key] = value;
            }
        }

        return merged;
    }
}
