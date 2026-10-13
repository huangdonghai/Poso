namespace Poso.Pdf.Filters;

/// <summary>RunLengthDecode: length bytes 0-127 copy the next n+1 bytes; 129-255 repeat the next byte 257-n times; 128 = EOD.</summary>
public static class RunLengthDecodeFilter
{
    public static byte[] Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            byte length = data[i++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                int count = length + 1;
                int available = Math.Min(count, data.Length - i);
                output.Write(data, i, available);
                i += count;
            }
            else
            {
                if (i >= data.Length)
                {
                    break;
                }

                byte value = data[i++];
                int count = 257 - length;
                for (int n = 0; n < count; n++)
                {
                    output.WriteByte(value);
                }
            }
        }

        return output.ToArray();
    }
}
