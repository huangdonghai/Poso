using System.Text;

namespace Poso.Pdf.Objects;

/// <summary>
/// PDF string object: the decoded byte sequence of a literal string <c>(...)</c> or a hexadecimal string <c>&lt;...&gt;</c>.
/// </summary>
public sealed class PdfString : PdfObject
{
    private readonly byte[] _value;

    public PdfString(byte[] value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Raw byte sequence (callers must not modify it).</summary>
    public ReadOnlySpan<byte> Value => _value;

    public int Length => _value.Length;

    /// <summary>Internal byte array (for library use only; callers must not modify it).</summary>
    internal byte[] Bytes => _value;

    /// <summary>
    /// Parse as a text string: UTF-16BE (BOM FE FF) takes precedence, otherwise decode as PDFDocEncoding (approximated here with Latin-1).
    /// </summary>
    public string AsTextString()
    {
        if (_value.Length >= 2 && _value[0] == 0xFE && _value[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(_value, 2, _value.Length - 2);
        }

        return Encoding.Latin1.GetString(_value);
    }

    public override PdfObjectType Type => PdfObjectType.String;

    public override string ToString() => AsTextString();

    /// <summary>Decode literal string content (handles escape sequences; balanced nesting is already guaranteed by the lexer).</summary>
    internal static byte[] DecodeLiteral(byte[] data, int start, int length)
    {
        var output = new List<byte>(length);
        int i = start;
        int end = start + length;
        while (i < end)
        {
            byte b = data[i];
            if (b != (byte)'\\')
            {
                output.Add(b);
                i++;
                continue;
            }

            i++;
            if (i >= end)
            {
                break;
            }

            byte e = data[i];
            switch (e)
            {
                case (byte)'n': output.Add((byte)'\n'); i++; break;
                case (byte)'r': output.Add((byte)'\r'); i++; break;
                case (byte)'t': output.Add((byte)'\t'); i++; break;
                case (byte)'b': output.Add((byte)'\b'); i++; break;
                case (byte)'f': output.Add((byte)'\f'); i++; break;
                case (byte)'(': output.Add((byte)'('); i++; break;
                case (byte)')': output.Add((byte)')'); i++; break;
                case (byte)'\\': output.Add((byte)'\\'); i++; break;
                case (byte)'\r':
                    // Backslash + line end = line continuation, produces no character
                    i++;
                    if (i < end && data[i] == (byte)'\n')
                    {
                        i++;
                    }

                    break;
                case (byte)'\n':
                    i++;
                    break;
                default:
                    if (e >= (byte)'0' && e <= (byte)'7')
                    {
                        int value = 0;
                        int digits = 0;
                        while (digits < 3 && i < end && data[i] >= (byte)'0' && data[i] <= (byte)'7')
                        {
                            value = (value * 8) + (data[i] - (byte)'0');
                            i++;
                            digits++;
                        }

                        output.Add((byte)value);
                    }
                    else
                    {
                        output.Add(e);
                        i++;
                    }

                    break;
            }
        }

        return output.ToArray();
    }

    /// <summary>Decode a hexadecimal string (whitespace is ignored, an odd number of digits is padded with 0).</summary>
    internal static byte[] DecodeHex(byte[] data, int start, int length)
    {
        var output = new List<byte>(length / 2 + 1);
        int high = -1;
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            byte b = data[i];
            if (b is (byte)'\0' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)' ')
            {
                continue;
            }

            int value = HexValue(b);
            if (value < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = value;
            }
            else
            {
                output.Add((byte)((high << 4) | value));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return output.ToArray();
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => (b - (byte)'a') + 10,
        >= (byte)'A' and <= (byte)'F' => (b - (byte)'A') + 10,
        _ => -1,
    };
}
