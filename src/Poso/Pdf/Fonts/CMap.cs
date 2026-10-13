using Poso.Pdf.Parsing;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Embedded CMap encoding for Type0 fonts: maps byte sequences to CIDs.
/// Supports codespacerange (valid code lengths), cidrange and cidchar.
/// Identity-H/Identity-V encodings are handled by <see cref="Identity"/>.
/// </summary>
public sealed class CMap
{
    private readonly List<(int Low, int High, int Cid)> _ranges = new();
    private readonly Dictionary<int, int> _singles = new();
    private readonly List<int> _codeLengths = new();
    private readonly bool _isIdentity;

    private CMap(bool isIdentity)
    {
        _isIdentity = isIdentity;
    }

    /// <summary>Identity encoding: 2-byte big-endian codes map directly to CIDs.</summary>
    public static CMap Identity { get; } = new CMap(true);

    /// <summary>The longest valid code length in bytes (2 for Identity).</summary>
    public int MaxCodeLength => _codeLengths.Count > 0 ? _codeLengths[^1] : 2;

    /// <summary>Tries to decode a code at the start of the span; returns the CID on success.</summary>
    public bool TryLookup(ReadOnlySpan<byte> code, out int cid)
    {
        cid = 0;

        if (_isIdentity)
        {
            if (code.Length < 2)
            {
                return false;
            }

            cid = (code[0] << 8) | code[1];
            return true;
        }

        foreach (int length in _codeLengths.OrderByDescending(l => l))
        {
            if (code.Length < length)
            {
                continue;
            }

            int value = 0;
            for (int i = 0; i < length; i++)
            {
                value = (value << 8) | code[i];
            }

            if (_singles.TryGetValue(value, out int singleCid))
            {
                cid = singleCid;
                return true;
            }

            foreach (var (low, high, rangeCid) in _ranges)
            {
                if (value >= low && value <= high)
                {
                    cid = rangeCid + (value - low);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Parses an embedded CMap stream; returns null when nothing could be parsed.</summary>
    public static CMap? Parse(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        var cmap = new CMap(false);
        var lexer = new PdfLexer(data);

        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind != PdfTokenKind.Keyword)
            {
                continue;
            }

            if (lexer.Match(token, "begincodespacerange"u8))
            {
                ParseCodeSpaceRange(lexer, cmap);
            }
            else if (lexer.Match(token, "begincidrange"u8))
            {
                ParseCidRange(lexer, cmap);
            }
            else if (lexer.Match(token, "begincidchar"u8))
            {
                ParseCidChar(lexer, cmap);
            }
        }

        return cmap._ranges.Count > 0 || cmap._singles.Count > 0 ? cmap : null;
    }

    private static void ParseCodeSpaceRange(PdfLexer lexer, CMap cmap)
    {
        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && lexer.Match(token, "endcodespacerange"u8))
            {
                return;
            }

            if (token.Kind != PdfTokenKind.HexString)
            {
                continue;
            }

            int length = HexDigitCount(lexer.Data, token.Start, token.Length);
            if (length > 0 && !cmap._codeLengths.Contains(length))
            {
                cmap._codeLengths.Add(length);
            }
        }
    }

    private static void ParseCidRange(PdfLexer lexer, CMap cmap)
    {
        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && lexer.Match(token, "endcidrange"u8))
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

            PdfToken cidToken = lexer.NextToken();
            if (cidToken.Kind != PdfTokenKind.Integer)
            {
                continue;
            }

            cmap._ranges.Add((low, high, (int)cidToken.IntegerValue));
        }
    }

    private static void ParseCidChar(PdfLexer lexer, CMap cmap)
    {
        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && lexer.Match(token, "endcidchar"u8))
            {
                return;
            }

            if (token.Kind != PdfTokenKind.HexString)
            {
                continue;
            }

            int code = HexToInt(lexer.Data, token.Start, token.Length);

            PdfToken cidToken = lexer.NextToken();
            if (cidToken.Kind == PdfTokenKind.Integer)
            {
                cmap._singles[code] = (int)cidToken.IntegerValue;
            }
        }
    }

    private static int HexDigitCount(byte[] data, int start, int length)
    {
        int count = 0;
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            byte b = data[i];
            bool digit = b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
            if (digit)
            {
                count++;
            }
        }

        return count / 2;
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
