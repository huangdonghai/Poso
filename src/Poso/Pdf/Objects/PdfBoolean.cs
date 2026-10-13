namespace Poso.Pdf.Objects;

/// <summary>PDF boolean object.</summary>
public sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value)
    {
        Value = value;
    }

    public bool Value { get; }

    public static PdfBoolean Get(bool value) => value ? True : False;

    public override PdfObjectType Type => PdfObjectType.Boolean;

    public override string ToString() => Value ? "true" : "false";
}
