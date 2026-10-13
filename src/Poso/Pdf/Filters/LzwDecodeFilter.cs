using Poso.Pdf.Objects;

namespace Poso.Pdf.Filters;

/// <summary>LZWDecode (PDF 32000-1:2008 7.4.4): variable-length codes of 9 to 12 bits; 256 = clear table, 257 = EOD.</summary>
public static class LzwDecodeFilter
{
    public static byte[] Decode(byte[] data, PdfDictionary? parms)
    {
        int earlyChange = (int)(parms?.GetIntegerValue("EarlyChange") ?? 1);

        var output = new MemoryStream(data.Length * 2);
        var table = new List<byte[]>(4096);
        ResetTable(table);

        byte[]? previous = null;
        int codeWidth = 9;
        int bitBuffer = 0;
        int bitCount = 0;
        bool done = false;

        foreach (byte b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;

            while (bitCount >= codeWidth && !done)
            {
                int code = (bitBuffer >> (bitCount - codeWidth)) & ((1 << codeWidth) - 1);
                bitCount -= codeWidth;

                switch (code)
                {
                    case 256:
                        ResetTable(table);
                        codeWidth = 9;
                        previous = null;
                        continue;

                    case 257:
                        done = true;
                        continue;
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                }
                else if (previous is not null)
                {
                    // KwKwK case: the code is not yet in the table; use previous + previous[0].
                    entry = Concat(previous, previous[0]);
                }
                else
                {
                    throw new PdfFormatException($"Corrupt LZW data: invalid code {code}");
                }

                output.Write(entry, 0, entry.Length);

                if (previous is not null && table.Count < 4096)
                {
                    table.Add(Concat(previous, entry[0]));
                    if (table.Count + earlyChange >= (1 << codeWidth) && codeWidth < 12)
                    {
                        codeWidth++;
                    }
                }

                previous = entry;
            }
        }

        return output.ToArray();
    }

    private static void ResetTable(List<byte[]> table)
    {
        table.Clear();
        for (int i = 0; i < 256; i++)
        {
            table.Add(new[] { (byte)i });
        }

        table.Add(Array.Empty<byte>()); // 256 clear
        table.Add(Array.Empty<byte>()); // 257 eod
    }

    private static byte[] Concat(byte[] prefix, byte last)
    {
        var result = new byte[prefix.Length + 1];
        Array.Copy(prefix, result, prefix.Length);
        result[^1] = last;
        return result;
    }
}
