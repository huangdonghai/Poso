namespace Poso.Pdf;

/// <summary>Base class for PDF processing exceptions.</summary>
public class PdfException : Exception
{
    public PdfException(string message) : base(message)
    {
    }

    public PdfException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The PDF file structure is corrupted or does not conform to the specification.</summary>
public sealed class PdfFormatException : PdfException
{
    public PdfFormatException(string message) : base(message)
    {
    }
}

/// <summary>The PDF uses a feature that is not supported by the current version (for example, encrypted documents).</summary>
public sealed class PdfUnsupportedException : PdfException
{
    public PdfUnsupportedException(string message) : base(message)
    {
    }
}
