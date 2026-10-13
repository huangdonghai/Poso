using System.Text;

namespace Poso.Pdf.Objects;

/// <summary>PDF name object (keys and enumeration values, without the leading slash).</summary>
public sealed class PdfName : PdfObject
{
    public PdfName(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value { get; }

    public override PdfObjectType Type => PdfObjectType.Name;

    public override string ToString() => "/" + Value;

    public override bool Equals(object? obj) => obj is PdfName other && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>Decode name content (handles <c>#XX</c> hexadecimal escapes).</summary>
    internal static string Decode(byte[] data, int start, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        int end = start + length;
        int hash = -1;
        for (int i = start; i < end; i++)
        {
            if (data[i] == (byte)'#')
            {
                hash = i;
                break;
            }
        }

        if (hash < 0)
        {
            return Encoding.Latin1.GetString(data, start, length);
        }

        var builder = new StringBuilder(length);
        for (int i = start; i < end; i++)
        {
            if (data[i] == (byte)'#' && i + 2 < end)
            {
                int high = HexValue(data[i + 1]);
                int low = HexValue(data[i + 2]);
                if (high >= 0 && low >= 0)
                {
                    builder.Append((char)((high << 4) | low));
                    i += 2;
                    continue;
                }
            }

            builder.Append((char)data[i]);
        }

        return builder.ToString();
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => (b - (byte)'a') + 10,
        >= (byte)'A' and <= (byte)'F' => (b - (byte)'A') + 10,
        _ => -1,
    };
}
