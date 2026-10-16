namespace Poso.Pdf.Fonts;

/// <summary>
/// Reader for a CFF INDEX structure (a count, an offset size, the offsets and the data blob).
/// Every CFF table - names, strings, charstrings - uses this layout.
/// </summary>
internal readonly struct CffIndex
{
    private CffIndex(int count, int offsetSize, int dataStart)
    {
        Count = count;
        OffsetSize = offsetSize;
        DataStart = dataStart;
    }

    /// <summary>Number of items in the index.</summary>
    public int Count { get; }

    private int OffsetSize { get; }

    private int DataStart { get; }

    /// <summary>Reads the index header and moves the position past the whole index.</summary>
    public static bool TryRead(byte[] data, ref int position, out CffIndex index)
    {
        index = default;
        if (position + 2 > data.Length)
        {
            return false;
        }

        int count = (data[position] << 8) | data[position + 1];
        position += 2;
        if (count == 0)
        {
            index = new CffIndex(0, 0, position);
            return true;
        }

        int offsetSize = data[position++];
        if (offsetSize is < 1 or > 4 || position + ((count + 1) * offsetSize) > data.Length)
        {
            return false;
        }

        int lastOffset = ReadOffset(data, position + (count * offsetSize), offsetSize);
        int dataStart = position + ((count + 1) * offsetSize);
        if (lastOffset < 1 || dataStart + lastOffset - 1 > data.Length)
        {
            return false;
        }

        index = new CffIndex(count, offsetSize, dataStart);
        position = dataStart + lastOffset - 1;
        return true;
    }

    /// <summary>Item zero as raw bytes (used for the Top DICT and the Private DICT).</summary>
    public byte[] ReadItem(byte[] data, int item)
    {
        (int start, int end) = Bounds(data, item);
        return start < 0 ? Array.Empty<byte>() : data.AsSpan(DataStart + start, end - start).ToArray();
    }

    /// <summary>All items as ASCII strings (the Name and String INDEX).</summary>
    public string[] ReadStrings(byte[] data)
    {
        string[] strings = new string[Count];
        for (int i = 0; i < Count; i++)
        {
            (int start, int end) = Bounds(data, i);
            strings[i] = start < 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(data, DataStart + start, end - start);
        }

        return strings;
    }

    private (int Start, int End) Bounds(byte[] data, int item)
    {
        if (OffsetSize == 0 || item < 0 || item >= Count)
        {
            return (-1, -1);
        }

        // The offsets array sits immediately before the data blob: offset[i] at offsetsStart + i * size
        int offsetsStart = DataStart - ((Count + 1) * OffsetSize);
        int start = ReadOffset(data, offsetsStart + (item * OffsetSize), OffsetSize) - 1;
        int end = ReadOffset(data, offsetsStart + ((item + 1) * OffsetSize), OffsetSize) - 1;
        return start < 0 || end < start || DataStart + end > data.Length ? (-1, -1) : (start, end);
    }

    private static int ReadOffset(byte[] data, int position, int size)
    {
        int value = 0;
        for (int i = 0; i < size; i++)
        {
            value = (value << 8) | data[position + i];
        }

        return value;
    }
}

/// <summary>
/// A parsed CFF DICT: operand stacks paired with their operator. Two byte operators (the
/// <c>12</c> escape) are keyed as <c>(12 &lt;&lt; 8) | suffix</c>.
/// </summary>
internal sealed class CffDict
{
    private readonly Dictionary<int, List<float>> _values = new();

    private CffDict()
    {
    }

    public static bool TryParse(byte[] dict, out CffDict result)
    {
        result = new CffDict();
        int position = 0;
        var operands = new List<float>();

        while (position < dict.Length)
        {
            byte b = dict[position];

            if (b == 12)
            {
                if (position + 1 >= dict.Length)
                {
                    return false;
                }

                result.Store((12 << 8) | dict[position + 1], operands);
                position += 2;
                operands.Clear();
                continue;
            }

            if (b <= 21)
            {
                result.Store(b, operands);
                position++;
                operands.Clear();
                continue;
            }

            try
            {
                operands.Add(ReadNumber(dict, ref position));
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        return true;
    }

    private void Store(int key, List<float> operands)
    {
        if (!_values.TryGetValue(key, out List<float>? existing))
        {
            _values[key] = [.. operands];
        }
    }

    /// <summary>True when the operator appears in the DICT.</summary>
    public bool Contains(int key) => _values.ContainsKey(key);

    /// <summary>The single operand of an operator, or zero when absent.</summary>
    public float Get(int key) => _values.TryGetValue(key, out List<float>? values) && values.Count > 0 ? values[^1] : 0f;

    /// <summary>The operand array of an operator, or null when absent.</summary>
    public float[]? GetArray(int key) => _values.TryGetValue(key, out List<float>? values) ? values.ToArray() : null;

    private static float ReadNumber(byte[] data, ref int i)
    {
        byte b = data[i++];
        if (b is >= 32 and <= 246)
        {
            return b - 139;
        }

        if (b is >= 247 and <= 250)
        {
            return ((b - 247) * 256) + data[i++] + 108;
        }

        if (b is >= 251 and <= 254)
        {
            return -((b - 251) * 256) - data[i++] - 108;
        }

        switch (b)
        {
            case 27:
                // Reserved: skip the four following bytes
                i += 4;
                return 0f;

            case 28:
            {
                short value = (short)((data[i] << 8) | data[i + 1]);
                i += 2;
                return value;
            }

            case 29:
            {
                int value = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                i += 4;
                return value;
            }

            case 30:
                return ReadReal(data, ref i);

            case 255:
            {
                int bits = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                i += 4;
                return BitConverter.Int32BitsToSingle(bits);
            }

            default:
                return 0f;
        }
    }

    private static float ReadReal(byte[] data, ref int i)
    {
        Span<char> buffer = stackalloc char[32];
        int length = 0;
        bool done = false;

        while (!done && i < data.Length && length < buffer.Length)
        {
            byte value = data[i++];
            for (int nibbleIndex = 0; nibbleIndex < 2 && !done; nibbleIndex++)
            {
                int nibble = (value >> ((1 - nibbleIndex) * 4)) & 0xF;
                if (nibble <= 9)
                {
                    buffer[length++] = (char)('0' + nibble);
                }
                else
                {
                    switch (nibble)
                    {
                        case 10:
                            buffer[length++] = '.';
                            break;
                        case 11:
                            buffer[length++] = 'E';
                            break;
                        case 12:
                            buffer[length++] = 'E';
                            buffer[length++] = '-';
                            break;
                        case 14:
                            buffer[length++] = '-';
                            break;
                        case 15:
                            done = true;
                            break;
                    }
                }
            }
        }

        return float.TryParse(buffer[..length], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : 0f;
    }
}
