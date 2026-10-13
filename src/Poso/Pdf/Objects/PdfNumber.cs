namespace Poso.Pdf.Objects;

/// <summary>PDF numeric object (integer or real).</summary>
public sealed class PdfNumber : PdfObject
{
    public PdfNumber(double value, bool isInteger)
    {
        Value = value;
        IsInteger = isInteger;
    }

    public double Value { get; }

    public bool IsInteger { get; }

    public long AsLong() => (long)Value;

    public int AsInt() => (int)Value;

    public float AsFloat() => (float)Value;

    public override PdfObjectType Type => PdfObjectType.Number;

    public override string ToString() => IsInteger ? ((long)Value).ToString() : Value.ToString("0.#####");
}
