namespace Poso.Pdf.Parsing;

/// <summary>
/// PDF lexical scanner (PDF 32000-1:2008, section 7.2). Sequential byte-index-based scanning with zero allocations.
/// Lexical rules: whitespace = NUL/TAB/LF/FF/CR/SP; comment = % to end of line; delimiters = ( ) &lt; &gt; [ ] { } / %.
/// </summary>
public sealed class PdfLexer
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _pos;

    public PdfLexer(byte[] data, int start = 0, int length = -1)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));

        // A cross-reference entry may point past the end of the file - a truncated or hand-edited table
        // does exactly that - and the scan loop indexes _data[_pos] before it looks at _end, so an
        // unclamped start throws IndexOutOfRangeException instead of reporting "nothing here".
        // pdf.js operator_list_cycle.pdf sends its reader beyond the last byte that way.
        _pos = Math.Clamp(start, 0, data.Length);
        _end = length < 0 ? data.Length : Math.Clamp(start + length, 0, data.Length);
    }

    public int Position
    {
        get => _pos;
        set => _pos = value;
    }

    public bool AtEnd => _pos >= _end;

    public byte[] Data => _data;

    public static bool IsWhiteSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>Skip whitespace and comments.</summary>
    public void SkipWhiteSpace()
    {
        while (_pos < _end)
        {
            byte b = _data[_pos];
            if (IsWhiteSpace(b))
            {
                _pos++;
                continue;
            }

            if (b == (byte)'%')
            {
                _pos++;
                while (_pos < _end && _data[_pos] != (byte)'\n' && _data[_pos] != (byte)'\r')
                {
                    _pos++;
                }

                continue;
            }

            break;
        }
    }

    /// <summary>Read the next token.</summary>
    public PdfToken NextToken()
    {
        SkipWhiteSpace();
        if (_pos >= _end)
        {
            return new PdfToken(PdfTokenKind.Eof, _pos, 0);
        }

        byte c = _data[_pos];
        int start = _pos;

        switch (c)
        {
            case (byte)'/':
                _pos++;
                int nameStart = _pos;
                while (_pos < _end && !IsWhiteSpace(_data[_pos]) && !IsDelimiter(_data[_pos]))
                {
                    _pos++;
                }

                return new PdfToken(PdfTokenKind.Name, nameStart, _pos - nameStart);

            case (byte)'(':
                return ReadLiteralString();

            case (byte)'<':
                if (_pos + 1 < _end && _data[_pos + 1] == (byte)'<')
                {
                    _pos += 2;
                    return new PdfToken(PdfTokenKind.DictOpen, start, 2);
                }

                return ReadHexString();

            case (byte)'>':
                if (_pos + 1 < _end && _data[_pos + 1] == (byte)'>')
                {
                    _pos += 2;
                    return new PdfToken(PdfTokenKind.DictClose, start, 2);
                }

                // A lone '>' does close a dictionary; ParseDictionary accepts this token as such. It is
                // reported as a keyword only so that everywhere a dictionary is not open, the upper
                // layer still gets to complain.
                _pos++;
                return new PdfToken(PdfTokenKind.Keyword, start, 1);

            case (byte)'[':
                _pos++;
                return new PdfToken(PdfTokenKind.ArrayOpen, start, 1);

            case (byte)']':
                _pos++;
                return new PdfToken(PdfTokenKind.ArrayClose, start, 1);

            case (byte)'{':
                _pos++;
                return new PdfToken(PdfTokenKind.BraceOpen, start, 1);

            case (byte)'}':
                _pos++;
                return new PdfToken(PdfTokenKind.BraceClose, start, 1);

            default:
                if (c is (byte)'+' or (byte)'-' or (byte)'.' || (c >= (byte)'0' && c <= (byte)'9'))
                {
                    return ReadNumber();
                }

                while (_pos < _end && !IsWhiteSpace(_data[_pos]) && !IsDelimiter(_data[_pos]))
                {
                    _pos++;
                }

                // A stray delimiter with no case above (a lone ')', typically) would leave the position
                // unchanged, and every token loop - the ToUnicode/CMap readers especially - would spin
                // forever on it. pdf.js issue8061.pdf's ToUnicode stream is binary garbage containing ')',
                // which hung the whole render. Always consume at least one byte.
                if (_pos == start)
                {
                    _pos++;
                }

                return new PdfToken(PdfTokenKind.Keyword, start, _pos - start);
        }
    }

    /// <summary>Peek at the next token without consuming the position.</summary>
    public PdfToken PeekToken()
    {
        int saved = _pos;
        PdfToken token = NextToken();
        _pos = saved;
        return token;
    }

    /// <summary>Peek at the token kind without consuming the position.</summary>
    public PdfTokenKind PeekKind()
    {
        int saved = _pos;
        PdfTokenKind kind = NextToken().Kind;
        _pos = saved;
        return kind;
    }

    /// <summary>Whether the token text equals the given ASCII keyword.</summary>
    public bool Match(PdfToken token, ReadOnlySpan<byte> keyword) =>
        token.Length == keyword.Length &&
        _data.AsSpan(token.Start, token.Length).SequenceEqual(keyword);

    /// <summary>Get the text of a token (used only for error messages and similar cases).</summary>
    public string GetText(PdfToken token) =>
        System.Text.Encoding.ASCII.GetString(_data, token.Start, token.Length);

    private PdfToken ReadNumber()
    {
        int start = _pos;
        if (_data[_pos] is (byte)'+' or (byte)'-')
        {
            _pos++;
        }

        bool hasDot = false;
        bool hasDigit = false;
        while (_pos < _end)
        {
            byte b = _data[_pos];
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                hasDigit = true;
                _pos++;
            }
            else if (b == (byte)'.' && !hasDot)
            {
                hasDot = true;
                _pos++;
            }
            else
            {
                break;
            }
        }

        int length = _pos - start;
        if (!hasDigit)
        {
            // For example a standalone "+" or "--"
            return new PdfToken(PdfTokenKind.Keyword, start, length);
        }

        if (hasDot)
        {
            return new PdfToken(PdfTokenKind.Real, start, length, 0, ParseReal(_data, start, length));
        }

        return new PdfToken(PdfTokenKind.Integer, start, length, ParseInteger(_data, start, length), 0);
    }

    private PdfToken ReadLiteralString()
    {
        int contentStart = _pos + 1;
        int i = _pos + 1;
        int depth = 1;
        while (i < _end)
        {
            byte b = _data[i];
            if (b == (byte)'\\')
            {
                i++;
                if (i < _end)
                {
                    byte e = _data[i];
                    if (e >= (byte)'0' && e <= (byte)'7')
                    {
                        int digits = 0;
                        while (digits < 3 && i < _end && _data[i] >= (byte)'0' && _data[i] <= (byte)'7')
                        {
                            i++;
                            digits++;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                continue;
            }

            if (b == (byte)'(')
            {
                depth++;
                i++;
                continue;
            }

            if (b == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                i++;
                continue;
            }

            i++;
        }

        int contentLength = i - contentStart;
        _pos = i < _end ? i + 1 : _end; // Skip the closing parenthesis
        return new PdfToken(PdfTokenKind.LiteralString, contentStart, contentLength);
    }

    private PdfToken ReadHexString()
    {
        int digitsStart = _pos + 1;
        int i = digitsStart;
        while (i < _end && _data[i] != (byte)'>')
        {
            i++;
        }

        int length = i - digitsStart;
        _pos = i < _end ? i + 1 : _end; // Skip '>'
        return new PdfToken(PdfTokenKind.HexString, digitsStart, length);
    }

    internal static long ParseInteger(byte[] data, int start, int length)
    {
        int i = start;
        int end = start + length;
        bool negative = false;
        if (i < end && (data[i] == (byte)'+' || data[i] == (byte)'-'))
        {
            negative = data[i] == (byte)'-';
            i++;
        }

        long value = 0;
        bool overflow = false;
        while (i < end && data[i] >= (byte)'0' && data[i] <= (byte)'9')
        {
            int digit = data[i] - (byte)'0';
            if (value > (long.MaxValue - digit) / 10)
            {
                overflow = true;
            }
            else
            {
                value = (value * 10) + digit;
            }

            i++;
        }

        if (overflow)
        {
            // Very large integers fall back to a real value
            return (long)ParseReal(data, start, length);
        }

        return negative ? -value : value;
    }

    internal static double ParseReal(byte[] data, int start, int length)
    {
        int i = start;
        int end = start + length;
        bool negative = false;
        if (i < end && (data[i] == (byte)'+' || data[i] == (byte)'-'))
        {
            negative = data[i] == (byte)'-';
            i++;
        }

        double value = 0;
        while (i < end && data[i] >= (byte)'0' && data[i] <= (byte)'9')
        {
            value = (value * 10) + (data[i] - (byte)'0');
            i++;
        }

        if (i < end && data[i] == (byte)'.')
        {
            i++;
            double scale = 0.1;
            while (i < end && data[i] >= (byte)'0' && data[i] <= (byte)'9')
            {
                value += (data[i] - (byte)'0') * scale;
                scale /= 10;
                i++;
            }
        }

        return negative ? -value : value;
    }
}
