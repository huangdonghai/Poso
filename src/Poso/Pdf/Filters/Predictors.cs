using Poso.Pdf.Objects;

namespace Poso.Pdf.Filters;

/// <summary>
/// Decoding predictors (/Predictor in /DecodeParms): PNG (10-15) and TIFF (2).
/// </summary>
public static class Predictors
{
    public static byte[] Apply(byte[] data, PdfDictionary? parms)
    {
        if (parms is null)
        {
            return data;
        }

        int predictor = (int)(parms.GetIntegerValue("Predictor") ?? 1);
        if (predictor < 2)
        {
            return data;
        }

        int colors = Math.Max(1, (int)(parms.GetIntegerValue("Colors") ?? 1));
        int bitsPerComponent = Math.Max(1, (int)(parms.GetIntegerValue("BitsPerComponent") ?? 8));
        int columns = Math.Max(1, (int)(parms.GetIntegerValue("Columns") ?? 1));

        int rowLength = (colors * bitsPerComponent * columns + 7) / 8;
        if (rowLength <= 0)
        {
            return data;
        }

        return predictor == 2
            ? ApplyTiff(data, colors, bitsPerComponent, columns, rowLength)
            : ApplyPng(data, rowLength, Math.Max(1, (colors * bitsPerComponent + 7) / 8));
    }

    /// <summary>TIFF predictor 2: horizontal differencing, restored component by component.</summary>
    private static byte[] ApplyTiff(byte[] data, int colors, int bits, int columns, int rowLength)
    {
        var result = new byte[data.Length];
        Array.Copy(data, result, data.Length);

        int components = colors;
        int bytesPerComponent = Math.Max(1, bits / 8);
        int rowBytes = (components * bits * columns + 7) / 8;
        if (rowBytes != rowLength)
        {
            // Only whole-byte components are supported.
            return data;
        }

        int rows = data.Length / rowLength;
        for (int row = 0; row < rows; row++)
        {
            int rowStart = row * rowLength;
            for (int column = 1; column < columns; column++)
            {
                for (int c = 0; c < components; c++)
                {
                    int index = rowStart + (column * components + c) * bytesPerComponent;
                    if (index + bytesPerComponent <= data.Length)
                    {
                        int previous = index - components * bytesPerComponent;
                        for (int b = 0; b < bytesPerComponent; b++)
                        {
                            result[index + b] = (byte)(result[index + b] + result[previous + b]);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>PNG predictor (10-15): each row is prefixed with one filter byte (0=None, 1=Sub, 2=Up, 3=Average, 4=Paeth).</summary>
    private static byte[] ApplyPng(byte[] data, int rowLength, int bpp)
    {
        int stride = rowLength + 1;
        if (data.Length < stride)
        {
            return data;
        }

        int rows = data.Length / stride;
        if (rows == 0)
        {
            return data;
        }

        var result = new byte[rows * rowLength];
        var previousRow = new byte[rowLength];
        var currentRow = new byte[rowLength];

        for (int row = 0; row < rows; row++)
        {
            int sourceStart = row * stride;
            byte filterType = data[sourceStart];

            Array.Copy(data, sourceStart + 1, currentRow, 0, rowLength);
            switch (filterType)
            {
                case 0:
                    break;
                case 1:
                    Sub(currentRow, rowLength, bpp);
                    break;
                case 2:
                    Up(currentRow, previousRow, rowLength);
                    break;
                case 3:
                    Average(currentRow, previousRow, rowLength, bpp);
                    break;
                case 4:
                    Paeth(currentRow, previousRow, rowLength, bpp);
                    break;
                default:
                    break;
            }

            Array.Copy(currentRow, 0, result, row * rowLength, rowLength);
            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return result;
    }

    private static void Sub(byte[] row, int rowLength, int bpp)
    {
        for (int i = bpp; i < rowLength; i++)
        {
            row[i] = (byte)(row[i] + row[i - bpp]);
        }
    }

    private static void Up(byte[] row, byte[] previousRow, int rowLength)
    {
        for (int i = 0; i < rowLength; i++)
        {
            row[i] = (byte)(row[i] + previousRow[i]);
        }
    }

    private static void Average(byte[] row, byte[] previousRow, int rowLength, int bpp)
    {
        for (int i = 0; i < rowLength; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0;
            row[i] = (byte)(row[i] + ((left + previousRow[i]) / 2));
        }
    }

    private static void Paeth(byte[] row, byte[] previousRow, int rowLength, int bpp)
    {
        for (int i = 0; i < rowLength; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0;
            int up = previousRow[i];
            int upLeft = i >= bpp ? previousRow[i - bpp] : 0;
            row[i] = (byte)(row[i] + PaethPredictor(left, up, upLeft));
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }
}
