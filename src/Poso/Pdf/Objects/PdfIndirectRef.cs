namespace Poso.Pdf.Objects;

/// <summary>Indirect reference <c>N G R</c>.</summary>
public sealed class PdfIndirectRef : PdfObject
{
    public PdfIndirectRef(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public int ObjectNumber { get; }

    public int Generation { get; }

    public override PdfObjectType Type => PdfObjectType.IndirectReference;

    public override string ToString() => $"{ObjectNumber} {Generation} R";

    public override bool Equals(object? obj) =>
        obj is PdfIndirectRef other && other.ObjectNumber == ObjectNumber && other.Generation == Generation;

    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);
}
