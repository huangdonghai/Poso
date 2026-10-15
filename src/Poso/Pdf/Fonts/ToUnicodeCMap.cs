using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;

namespace Poso.Pdf.Fonts;

/// <summary>
/// ToUnicode CMap: maps CIDs (or simple font codes) to Unicode code units.
/// Supports bfchar and bfrange (string and array destination forms).
/// </summary>
public sealed class ToUnicodeCMap
{
    private readonly Dictionary<int, uint[]> _map;

    private ToUnicodeCMap(Dictionary<int, uint[]> map)
    {
        _map = map;
    }

    /// <summary>Parses a decoded ToUnicode stream; returns null when it contains no mappings.</summary>
    public static ToUnicodeCMap? Parse(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        var lexer = new PdfLexer(data);
        var map = new Dictionary<int, uint[]>();

        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind != PdfTokenKind.Keyword)
            {
                continue;
            }

            if (lexer.Match(token, "beginbfchar"u8))
            {
                ParseBfChar(lexer, map);
            }
            else if (lexer.Match(token, "beginbfrange"u8))
            {
                ParseBfRange(lexer, map);
            }
        }

        return map.Count > 0 ? new ToUnicodeCMap(map) : null;
    }

    /// <summary>Gets the Unicode code units for a code (UTF-16BE units; may be empty).</summary>
    public bool TryGetUnits(int code, out uint[] units)
    {
        if (_map.TryGetValue(code, out uint[]? found))
        {
            units = found;
            return true;
        }

        units = Array.Empty<uint>();
        return false;
    }

    /// <summary>Gets the first Unicode code unit for a code; 0 when unmapped.</summary>
    public uint GetFirstUnit(int code) =>
        _map.TryGetValue(code, out uint[]? units) && units.Length > 0 ? units[0] : 0;

    private static void ParseBfChar(PdfLexer lexer, Dictionary<int, uint[]> map)
    {
        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && lexer.Match(token, "endbfchar"u8))
            {
                return;
            }

            if (token.Kind != PdfTokenKind.HexString)
            {
                continue;
            }

            int code = HexToInt(lexer.Data, token.Start, token.Length);
            uint[]? units = ReadDestination(lexer);
            if (units is not null)
            {
                map[code] = units;
            }
        }
    }

    private static void ParseBfRange(PdfLexer lexer, Dictionary<int, uint[]> map)
    {
        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && lexer.Match(token, "endbfrange"u8))
            {
                return;
            }

            if (token.Kind != PdfTokenKind.HexString)
            {
                continue;
            }

            int low = HexToInt(lexer.Data, token.Start, token.Length);

            PdfToken highToken = lexer.NextToken();
            if (highToken.Kind != PdfTokenKind.HexString)
            {
                continue;
            }

            int high = HexToInt(lexer.Data, highToken.Start, highToken.Length);

            lexer.SkipWhiteSpace();
            if (lexer.PeekKind() == PdfTokenKind.ArrayOpen)
            {
                // Array form: <lo> <hi> [<dst1> <dst2> ...]
                lexer.NextToken();
                int code = low;
                while (code <= high && !lexer.AtEnd)
                {
                    lexer.SkipWhiteSpace();
                    if (lexer.PeekKind() == PdfTokenKind.ArrayClose)
                    {
                        lexer.NextToken();
                        break;
                    }

                    uint[]? units = ReadDestination(lexer);
                    if (units is not null)
                    {
                        map[code] = units;
                    }

                    code++;
                }
            }
            else
            {
                // String form: <lo> <hi> <dst>, incrementing the last unit per code
                PdfToken destination = lexer.NextToken();
                if (destination.Kind is not (PdfTokenKind.HexString or PdfTokenKind.LiteralString))
                {
                    continue;
                }

                byte[] destinationBytes = ReadStringBytes(lexer, destination);
                uint[] baseUnits = DecodeUtf16(destinationBytes, 0, destinationBytes.Length);
                for (int code = low; code <= high; code++)
                {
                    map[code] = IncrementUnits(baseUnits, code - low);
                }
            }
        }
    }

    /// <summary>Reads a bfchar destination: a hex string or a literal string.</summary>
    private static uint[]? ReadDestination(PdfLexer lexer)
    {
        lexer.SkipWhiteSpace();
        PdfToken token = lexer.NextToken();
        if (token.Kind is not (PdfTokenKind.HexString or PdfTokenKind.LiteralString))
        {
            return null;
        }

        byte[] bytes = ReadStringBytes(lexer, token);
        return DecodeUtf16(bytes, 0, bytes.Length);
    }

    /// <summary>Reads a string token's bytes: a hex string is hex-decoded, so the UTF-16 units are
    /// read from the value rather than from the ASCII digits that spell it.</summary>
    private static byte[] ReadStringBytes(PdfLexer lexer, PdfToken token) => token.Kind switch
    {
        PdfTokenKind.HexString => PdfString.DecodeHex(lexer.Data, token.Start, token.Length),
        _ => PdfString.DecodeLiteral(lexer.Data, token.Start, token.Length),
    };

    /// <summary>
    /// Decodes a destination string to UTF-16BE code units: a FE FF BOM forces UTF-16BE;
    /// a single byte is a Latin-1 style value; an even length is read as UTF-16BE.
    /// </summary>
    private static uint[] DecodeUtf16(byte[] data, int start, int length)
    {
        if (length == 0)
        {
            return Array.Empty<uint>();
        }

        if (length >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
        {
            return ReadUnits(data, start + 2, length - 2);
        }

        if (length == 1)
        {
            return new[] { (uint)data[start] };
        }

        return length % 2 == 0
            ? ReadUnits(data, start, length)
            : new[] { (uint)data[start] };
    }

    private static uint[] ReadUnits(byte[] data, int start, int length)
    {
        int count = length / 2;
        var units = new uint[count];
        for (int i = 0; i < count; i++)
        {
            units[i] = (uint)((data[start + (i * 2)] << 8) | data[start + (i * 2) + 1]);
        }

        return units;
    }

    private static uint[] IncrementUnits(uint[] units, int delta)
    {
        if (delta == 0 || units.Length == 0)
        {
            return units;
        }

        var result = (uint[])units.Clone();
        int carry = delta;
        for (int i = result.Length - 1; i >= 0 && carry > 0; i--)
        {
            int next = (int)result[i] + carry;
            result[i] = (uint)(next & 0xFFFF);
            carry = next >> 16;
        }

        return result;
    }

    private static int HexToInt(byte[] data, int start, int length)
    {
        int value = 0;
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            byte b = data[i];
            int digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => (b - (byte)'a') + 10,
                >= (byte)'A' and <= (byte)'F' => (b - (byte)'A') + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                break;
            }

            value = (value * 16) + digit;
        }

        return value;
    }
}
