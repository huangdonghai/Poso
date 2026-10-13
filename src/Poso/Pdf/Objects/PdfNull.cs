namespace Poso.Pdf.Objects;

/// <summary>PDF null object (singleton). A null value in a dictionary is equivalent to the key being absent.</summary>
public sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();

    private PdfNull()
    {
    }

    public override PdfObjectType Type => PdfObjectType.Null;

    public override string ToString() => "null";
}
