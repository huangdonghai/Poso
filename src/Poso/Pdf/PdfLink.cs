using Poso.Geometry;
using Poso.Pdf.Objects;

namespace Poso.Pdf;

/// <summary>Link action kind.</summary>
public enum PdfActionKind
{
    None,

    /// <summary>External URL.</summary>
    Uri,

    /// <summary>In-document jump.</summary>
    GoTo,
}

/// <summary>
/// A link annotation on a page: a rectangle in unrotated page user space (y-up) plus an action.
/// Rectangles use the same coordinate space as text search hits, so viewers can map them with
/// the same page transform.
/// </summary>
public sealed class PdfLink
{
    internal PdfLink(RectF rect, PdfActionKind kind, string? uri, PdfDestination? destination)
    {
        Rect = rect;
        Kind = kind;
        Uri = uri;
        Destination = destination;
    }

    public RectF Rect { get; }

    public PdfActionKind Kind { get; }

    /// <summary>Target URL when <see cref="Kind"/> is <see cref="PdfActionKind.Uri"/>.</summary>
    public string? Uri { get; }

    /// <summary>Jump target when <see cref="PdfActionKind.Kind"/> is <see cref="PdfActionKind.GoTo"/>.</summary>
    public PdfDestination? Destination { get; }

    /// <summary>Loads the link annotations of a page (empty when none).</summary>
    public static IReadOnlyList<PdfLink> Load(PdfDocument document, PdfPage page)
    {
        PdfArray? annots = document.Resolve(page.Dictionary.Get("Annots")) as PdfArray;
        if (annots is null)
        {
            return Array.Empty<PdfLink>();
        }

        var links = new List<PdfLink>();
        foreach (PdfObject item in annots)
        {
            PdfDictionary? annotation = document.Resolve(item) as PdfDictionary;
            if (annotation is null || annotation.GetNameValue("Subtype") != "Link")
            {
                continue;
            }

            PdfArray? rectArray = annotation.GetArray("Rect");
            if (rectArray is not { Count: >= 4 })
            {
                continue;
            }

            RectF rect = RectF.FromLbrb(
                rectArray.GetNumber(0)?.AsFloat() ?? 0f,
                rectArray.GetNumber(1)?.AsFloat() ?? 0f,
                rectArray.GetNumber(2)?.AsFloat() ?? 0f,
                rectArray.GetNumber(3)?.AsFloat() ?? 0f);

            PdfActionKind kind = PdfActionKind.None;
            string? uri = null;
            PdfDestination? destination = null;

            if (document.Resolve(annotation.Get("A")) is PdfDictionary action)
            {
                switch (action.GetNameValue("S"))
                {
                    case "URI":
                        kind = PdfActionKind.Uri;
                        uri = (document.Resolve(action.Get("URI")) as PdfString)?.AsTextString();
                        break;

                    case "GoTo":
                        kind = PdfActionKind.GoTo;
                        destination = PdfDestination.Parse(document, action.Get("D"));
                        break;

                    case "GoToR":
                        // Remote destinations are not followed in v0.1
                        kind = PdfActionKind.None;
                        break;
                }
            }
            else if (annotation.Get("Dest") is not null)
            {
                kind = PdfActionKind.GoTo;
                destination = PdfDestination.Parse(document, annotation.Get("Dest"));
            }

            if (kind != PdfActionKind.None)
            {
                links.Add(new PdfLink(rect, kind, uri, destination));
            }
        }

        return links;
    }
}
