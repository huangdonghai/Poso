using System.IO.Compression;

namespace Poso.Pdf.Filters;

/// <summary>BrotliDecode: a Brotli (RFC 7932) stream. Not part of ISO 32000, but pdf.js accepts it and some
/// prototype generators use it for every stream, including object and xref streams.</summary>
public static class BrotliDecodeFilter
{
    public static byte[] Decode(byte[] data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var input = new MemoryStream(data, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(data.Length * 3, 4096));
        try
        {
            brotli.CopyTo(output);
        }
        catch (InvalidDataException)
        {
            // Data corrupt/truncated: return the portion already decompressed.
        }

        return output.ToArray();
    }
}
