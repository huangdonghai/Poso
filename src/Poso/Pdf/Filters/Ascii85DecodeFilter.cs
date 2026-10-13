namespace Poso.Pdf.Filters;

/// <summary>ASCII85Decode: '!'..'u' five-character groups, 'z' represents four zero bytes, terminated by '~&gt;'.</summary>
public static class Ascii85DecodeFilter
{
    private static readonly byte[] ZeroGroup = new byte[4];

    public static byte[] Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length * 4 / 5 + 4);
        uint group = 0;
        int count = 0;

        foreach (byte b in data)
        {
            if (b == (byte)'~')
            {
                break;
            }

            if (b is (byte)'\0' or (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)' ')
            {
                continue;
            }

            if (b == (byte)'z' && count == 0)
            {
                output.Write(ZeroGroup, 0, 4); // Four zero bytes.
                continue;
            }

            if (b < (byte)'!' || b > (byte)'u')
            {
                break;
            }

            group = (group * 85) + (uint)(b - (byte)'!');
            count++;

            if (count == 5)
            {
                output.WriteByte((byte)(group >> 24));
                output.WriteByte((byte)(group >> 16));
                output.WriteByte((byte)(group >> 8));
                output.WriteByte((byte)group);
                group = 0;
                count = 0;
            }
        }

        if (count > 0)
        {
            // Fewer than 5 trailing characters: pad with 'u' (84) and output count-1 bytes.
            for (int i = count; i < 5; i++)
            {
                group = (group * 85) + 84;
            }

            Span<byte> bytes = stackalloc byte[4];
            bytes[0] = (byte)(group >> 24);
            bytes[1] = (byte)(group >> 16);
            bytes[2] = (byte)(group >> 8);
            bytes[3] = (byte)group;
            output.Write(bytes.Slice(0, count - 1));
        }

        return output.ToArray();
    }
}
