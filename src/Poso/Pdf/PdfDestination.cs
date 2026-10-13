using Poso.Pdf.Objects;

namespace Poso.Pdf;

/// <summary>Destination view type.</summary>
public enum PdfDestinationType
{
    /// <summary>[page /XYZ left top zoom] — null left/top keeps the current value.</summary>
    Xyz,

    /// <summary>[page /Fit].</summary>
    Fit,

    /// <summary>[page /FitH top].</summary>
    FitH,

    /// <summary>[page /FitV left].</summary>
    FitV,

    /// <summary>[page /FitR left bottom right top].</summary>
    FitR,

    /// <summary>[page /FitB].</summary>
    FitB,

    /// <summary>[page /FitBH top].</summary>
    FitBH,

    /// <summary>[page /FitBV left].</summary>
    FitBV,
}

/// <summary>An in-document jump target (the GoTo action of a bookmark or link).</summary>
public sealed class PdfDestination
{
    private PdfDestination(int pageIndex, PdfDestinationType type, float[] parameters)
    {
        PageIndex = pageIndex;
        Type = type;
        Parameters = parameters;
    }

    /// <summary>Target page index (zero-based).</summary>
    public int PageIndex { get; }

    public PdfDestinationType Type { get; }

    /// <summary>View parameters (meaning varies with <see cref="Type"/>; at most 4).</summary>
    public float[] Parameters { get; }

    public float GetParameter(int index) => index >= 0 && index < Parameters.Length ? Parameters[index] : 0f;

    /// <summary>
    /// Parses a destination: array form [pageRef /Type params...], dictionary form { /D [...] },
    /// or a name that is resolved through the document's named destination table.
    /// </summary>
    public static PdfDestination? Parse(PdfDocument document, PdfObject? value)
    {
        if (value is PdfString name)
        {
            PdfObject? named = document.CatalogInfo.LookupNamedDestination(name.AsTextString());
            return named switch
            {
                PdfArray namedArray => FromArray(document, namedArray),
                PdfDictionary namedDictionary => Parse(document, namedDictionary),
                _ => null,
            };
        }

        PdfObject? resolved = document.Resolve(value);
        if (resolved is PdfDictionary dictionary)
        {
            resolved = document.Resolve(dictionary.Get("D"));
        }

        return resolved is PdfArray array ? FromArray(document, array) : null;
    }

    /// <summary>Parses a destination from an array.</summary>
    public static PdfDestination? FromArray(PdfDocument document, PdfArray array)
    {
        if (array.Count < 2)
        {
            return null;
        }

        int pageIndex = ResolvePageIndex(document, array[0]);
        if (pageIndex < 0)
        {
            return null;
        }

        PdfDestinationType type = (array[1] as PdfName)?.Value switch
        {
            "XYZ" => PdfDestinationType.Xyz,
            "Fit" => PdfDestinationType.Fit,
            "FitH" => PdfDestinationType.FitH,
            "FitV" => PdfDestinationType.FitV,
            "FitR" => PdfDestinationType.FitR,
            "FitB" => PdfDestinationType.FitB,
            "FitBH" => PdfDestinationType.FitBH,
            "FitBV" => PdfDestinationType.FitBV,
            _ => PdfDestinationType.Fit,
        };

        var parameters = new float[4];
        for (int i = 2; i < Math.Min(array.Count, 6); i++)
        {
            parameters[i - 2] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return new PdfDestination(pageIndex, type, parameters);
    }

    private static int ResolvePageIndex(PdfDocument document, PdfObject? pageObject)
    {
        PdfObject? resolved = document.Resolve(pageObject);
        if (resolved is not PdfDictionary pageDictionary)
        {
            return -1;
        }

        IReadOnlyList<PdfPage> pages = document.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            if (ReferenceEquals(pages[i].Dictionary, pageDictionary))
            {
                return i;
            }
        }

        return -1;
    }
}
