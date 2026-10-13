namespace Poso.Pdf.Filters;

/// <summary>ASCIIHexDecode: terminated by '&gt;', whitespace ignored, odd digit counts padded with 0.</summary>
public static class AsciiHexDecodeFilter
{
    public static byte[] Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length / 2 + 1);
        int high = -1;

        foreach (byte b in data)
        {
            if (b == (byte)'>')
            {
                break;
            }

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
                output.WriteByte((byte)((high << 4) | value));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.WriteByte((byte)(high << 4));
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
