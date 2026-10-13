using System.IO.Compression;

namespace Poso.Pdf.Filters;

/// <summary>FlateDecode: zlib (RFC 1950) or raw deflate (RFC 1951) stream.</summary>
public static class FlateDecodeFilter
{
    public static byte[] Decode(byte[] data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        int offset = GetZlibHeaderOffset(data);
        int inputLength = data.Length - offset;
        if (inputLength <= 0)
        {
            return Array.Empty<byte>();
        }

        using var input = new MemoryStream(data, offset, inputLength, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(inputLength * 3, 4096));
        try
        {
            deflate.CopyTo(output);
        }
        catch (InvalidDataException)
        {
            // Data corrupt/truncated: return the portion already decompressed.
        }

        return output.ToArray();
    }

    /// <summary>Validate the two-byte zlib header: CM=8 (deflate), window size &lt;= 7, FCHECK modulo 31 is 0. Otherwise treat the data as raw deflate.</summary>
    private static int GetZlibHeaderOffset(byte[] data)
    {
        if (data.Length >= 2 && (data[0] & 0x0F) == 8 && (data[0] >> 4) <= 7)
        {
            int check = (data[0] << 8) | data[1];
            if (check % 31 == 0)
            {
                return 2;
            }
        }

        return 0;
    }
}
