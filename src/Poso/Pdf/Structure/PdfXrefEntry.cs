namespace Poso.Pdf.Structure;

/// <summary>Cross-reference entry types.</summary>
public enum PdfXrefEntryType
{
    /// <summary>Free object.</summary>
    Free = 0,

    /// <summary>The object is located at file offset <see cref="PdfXrefEntry.Offset"/>.</summary>
    InFile = 1,

    /// <summary>The object is stored in object stream <see cref="PdfXrefEntry.StreamObjectNumber"/> at index <see cref="PdfXrefEntry.IndexInStream"/>.</summary>
    InObjectStream = 2,
}

/// <summary>Cross-reference entry.</summary>
public readonly struct PdfXrefEntry
{
    public PdfXrefEntry(PdfXrefEntryType type, long offset, int streamObjectNumber, int indexInStream, int generation)
    {
        Type = type;
        Offset = offset;
        StreamObjectNumber = streamObjectNumber;
        IndexInStream = indexInStream;
        Generation = generation;
    }

    public PdfXrefEntryType Type { get; }

    public long Offset { get; }

    public int StreamObjectNumber { get; }

    public int IndexInStream { get; }

    public int Generation { get; }
}
