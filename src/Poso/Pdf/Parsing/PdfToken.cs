namespace Poso.Pdf.Parsing;

/// <summary>Lexical token kind.</summary>
public enum PdfTokenKind
{
    None = 0,
    Eof,
    /// <summary>Integer; the content is located at [Start, Start+Length) in the data buffer.</summary>
    Integer,
    /// <summary>Real; the content is located at [Start, Start+Length) in the data buffer.</summary>
    Real,
    /// <summary>Keyword (obj/endobj/stream/endstream/R/true/false/null/xref/trailer/startxref, etc.).</summary>
    Keyword,
    /// <summary>Name, without the leading slash, may contain #XX escapes.</summary>
    Name,
    /// <summary>Raw content of a literal string (still contains escape sequences).</summary>
    LiteralString,
    /// <summary>Raw hexadecimal digits of a hexadecimal string.</summary>
    HexString,
    ArrayOpen,
    ArrayClose,
    DictOpen,
    DictClose,
    /// <summary>PostScript procedure delimiter <c>{</c> (used by Type 4 functions).</summary>
    BraceOpen,
    /// <summary>PostScript procedure delimiter <c>}</c>.</summary>
    BraceClose,
}

/// <summary>
/// Lexical token. It uses a buffer index range rather than <see cref="ReadOnlySpan{T}"/>,
/// so it can be stored safely in class fields; scanning performs zero allocations.
/// </summary>
public readonly struct PdfToken
{
    public PdfToken(PdfTokenKind kind, int start, int length, long integerValue = 0, double realValue = 0)
    {
        Kind = kind;
        Start = start;
        Length = length;
        IntegerValue = integerValue;
        RealValue = realValue;
    }

    public PdfTokenKind Kind { get; }

    public int Start { get; }

    public int Length { get; }

    public long IntegerValue { get; }

    public double RealValue { get; }
}
