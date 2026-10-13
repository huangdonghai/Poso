namespace Poso.Pdf.Objects;

/// <summary>PDF object type.</summary>
public enum PdfObjectType
{
    Null,
    Boolean,
    Number,
    String,
    Name,
    Array,
    Dictionary,
    Stream,
    IndirectReference,
}

/// <summary>Base class of all PDF objects.</summary>
public abstract class PdfObject
{
    public abstract PdfObjectType Type { get; }
}
