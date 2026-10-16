using Poso.Pdf.Objects;

namespace Poso.Pdf.Parsing;

/// <summary>
/// PDF syntax parser: combines lexical tokens into PDF objects (indirect objects, dictionaries, arrays, streams, etc.).
/// </summary>
public sealed class PdfParser
{
    private const int MaxDepth = 96;

    private readonly PdfLexer _lexer;
    private int _currentObjectNumber;
    private int _currentObjectGeneration;

    public PdfParser(byte[] data, int start = 0)
    {
        _lexer = new PdfLexer(data, start);
    }

    /// <summary>Resolution callback used when parsing an indirect reference (for example when /Length is an indirect object).</summary>
    public Func<PdfIndirectRef, PdfObject?>? Resolver { get; set; }

    /// <summary>Shared encryption context; when set, strings and stream data are decrypted on creation.</summary>
    public Security.PdfEncryptionContext? Encryption { get; set; }

    /// <summary>
    /// When false, dictionary/array <see cref="PdfString"/> values are left encrypted (used for the
    /// trailer, cross-reference stream dictionaries, and object-stream contents where strings are
    /// either not encrypted or already decrypted at the stream level). Stream data still decrypts.
    /// </summary>
    public bool DecryptStrings { get; set; } = true;

    /// <summary>Object number/generation of the indirect object currently being parsed.</summary>
    public int CurrentObjectNumber => _currentObjectNumber;

    public int CurrentObjectGeneration => _currentObjectGeneration;

    public int Position
    {
        get => _lexer.Position;
        set => _lexer.Position = value;
    }

    public bool AtEnd => _lexer.AtEnd;

    public void SkipWhiteSpace() => _lexer.SkipWhiteSpace();

    /// <summary>Parse an arbitrary object. The caller must ensure the current position is at the start of an object.</summary>
    public PdfObject ParseObject(int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new PdfFormatException("Object nesting is too deep (the file may be corrupt or malicious)");
        }

        PdfToken token = _lexer.NextToken();
        switch (token.Kind)
        {
            case PdfTokenKind.Integer:
            {
                // May be an "N G R" indirect reference
                int saved = _lexer.Position;
                PdfToken second = _lexer.NextToken();
                if (second.Kind == PdfTokenKind.Integer)
                {
                    PdfToken third = _lexer.NextToken();
                    if (third.Kind == PdfTokenKind.Keyword && _lexer.Match(third, "R"u8))
                    {
                        return new PdfIndirectRef((int)token.IntegerValue, (int)second.IntegerValue);
                    }
                }

                _lexer.Position = saved;
                return new PdfNumber(token.IntegerValue, true);
            }

            case PdfTokenKind.Real:
                return new PdfNumber(token.RealValue, false);

            case PdfTokenKind.Name:
                return new PdfName(PdfName.Decode(_lexer.Data, token.Start, token.Length));

            case PdfTokenKind.LiteralString:
                return new PdfString(DecryptString(PdfString.DecodeLiteral(_lexer.Data, token.Start, token.Length)));

            case PdfTokenKind.HexString:
                return new PdfString(DecryptString(PdfString.DecodeHex(_lexer.Data, token.Start, token.Length)));

            case PdfTokenKind.ArrayOpen:
                return ParseArray(depth);

            case PdfTokenKind.DictOpen:
                return ParseDictionary(depth);

            case PdfTokenKind.Keyword:
                if (_lexer.Match(token, "true"u8))
                {
                    return PdfBoolean.True;
                }

                if (_lexer.Match(token, "false"u8))
                {
                    return PdfBoolean.False;
                }

                if (_lexer.Match(token, "null"u8))
                {
                    return PdfNull.Instance;
                }

                throw new PdfFormatException($"Unexpected keyword '{_lexer.GetText(token)}'");

            case PdfTokenKind.Eof:
                throw new PdfFormatException("Unexpected end of file (incomplete object)");

            default:
                throw new PdfFormatException($"Unexpected token {token.Kind}");
        }
    }

    /// <summary>
    /// Try to parse <c>N G obj &lt;object&gt; endobj</c> at the current position.
    /// On success returns (reference, object); returns null when the position is not at the start of an indirect object.
    /// </summary>
    public (PdfIndirectRef Reference, PdfObject Object)? TryParseIndirectObject()
    {
        _lexer.SkipWhiteSpace();
        if (_lexer.AtEnd)
        {
            return null;
        }

        PdfToken t1 = _lexer.NextToken();
        if (t1.Kind != PdfTokenKind.Integer)
        {
            return null;
        }

        PdfToken t2 = _lexer.NextToken();
        if (t2.Kind != PdfTokenKind.Integer)
        {
            return null;
        }

        PdfToken t3 = _lexer.NextToken();
        if (t3.Kind != PdfTokenKind.Keyword || !_lexer.Match(t3, "obj"u8))
        {
            return null;
        }

        var reference = new PdfIndirectRef((int)t1.IntegerValue, (int)t2.IntegerValue);
        _currentObjectNumber = (int)t1.IntegerValue;
        _currentObjectGeneration = (int)t2.IntegerValue;
        PdfObject value = ParseObject();
        SkipToEndObj();
        return (reference, value);
    }

    /// <summary>Skip to endobj (tolerant: ignores extra content before endobj).</summary>
    public void SkipToEndObj()
    {
        int guard = 0;
        while (!_lexer.AtEnd && guard++ < 1_000_000)
        {
            PdfToken token = _lexer.NextToken();
            if (token.Kind == PdfTokenKind.Keyword && _lexer.Match(token, "endobj"u8))
            {
                return;
            }
        }
    }

    private PdfObject ParseArray(int depth)
    {
        var items = new List<PdfObject>();
        while (true)
        {
            _lexer.SkipWhiteSpace();
            if (_lexer.AtEnd)
            {
                throw new PdfFormatException("Unterminated array (missing ']')");
            }

            if (_lexer.PeekKind() == PdfTokenKind.ArrayClose)
            {
                _lexer.NextToken();
                break;
            }

            items.Add(ParseObject(depth + 1));
        }

        return new PdfArray(items);
    }

    /// <summary>
    /// A dictionary ends at '>>', or at a lone '>' - which the lexer reports as a one-character keyword
    /// and which producers do emit.
    /// </summary>
    private bool ClosesDictionary()
    {
        if (_lexer.PeekKind() == PdfTokenKind.DictClose)
        {
            return true;
        }

        return _lexer.PeekKind() == PdfTokenKind.Keyword && _lexer.Match(_lexer.PeekToken(), ">"u8);
    }

    private PdfObject ParseDictionary(int depth)
    {
        var map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        while (true)
        {
            _lexer.SkipWhiteSpace();
            if (_lexer.AtEnd)
            {
                throw new PdfFormatException("Unterminated dictionary (missing '>>')");
            }

            if (ClosesDictionary())
            {
                _lexer.NextToken();
                break;
            }

            PdfToken key = _lexer.NextToken();
            if (key.Kind != PdfTokenKind.Name)
            {
                throw new PdfFormatException($"Dictionary keys must be names, but found {key.Kind}");
            }

            _lexer.SkipWhiteSpace();
            if (ClosesDictionary())
            {
                // Closing directly after a key: treat it as corruption
                _lexer.NextToken();
                break;
            }

            PdfObject value = ParseObject(depth + 1);
            if (value is not PdfNull)
            {
                map[PdfName.Decode(_lexer.Data, key.Start, key.Length)] = value;
            }
        }

        // Stream object: "<< ... >> stream ... endstream"
        _lexer.SkipWhiteSpace();
        if (!_lexer.AtEnd && _lexer.PeekKind() == PdfTokenKind.Keyword)
        {
            int saved = _lexer.Position;
            PdfToken keyword = _lexer.NextToken();
            if (_lexer.Match(keyword, "stream"u8))
            {
                return ParseStreamData(map);
            }

            _lexer.Position = saved;
        }

        return new PdfDictionary(map);
    }

    private PdfStream ParseStreamData(Dictionary<string, PdfObject> map)
    {
        // After the "stream" keyword: the specification requires CRLF or LF immediately after it; tolerantly skip spaces/tabs and at most one line end
        while (!_lexer.AtEnd && (_lexer.Data[_lexer.Position] == (byte)' ' || _lexer.Data[_lexer.Position] == (byte)'\t'))
        {
            _lexer.Position++;
        }

        if (!_lexer.AtEnd)
        {
            byte next = _lexer.Data[_lexer.Position];
            if (next == (byte)'\r')
            {
                _lexer.Position++;
                if (!_lexer.AtEnd && _lexer.Data[_lexer.Position] == (byte)'\n')
                {
                    _lexer.Position++;
                }
            }
            else if (next == (byte)'\n')
            {
                _lexer.Position++;
            }
        }

        int dataStart = _lexer.Position;
        byte[] data = _lexer.Data;

        // Determine the stream length: prefer /Length (a number or an indirect reference), otherwise fall back to searching for endstream
        long length = -1;
        if (map.TryGetValue("Length", out var lengthObject))
        {
            if (lengthObject is PdfNumber number)
            {
                length = number.AsLong();
            }
            else if (lengthObject is PdfIndirectRef reference && Resolver is not null)
            {
                if (Resolver(reference) is PdfNumber resolved)
                {
                    length = resolved.AsLong();
                }
            }
        }

        int dataEnd;
        if (length >= 0 && dataStart + length <= data.Length && EndStreamFollows(data, (int)(dataStart + length)))
        {
            dataEnd = (int)(dataStart + length);
        }
        else
        {
            dataEnd = FindEndStream(data, dataStart);
        }

        byte[] raw = new byte[dataEnd - dataStart];
        Array.Copy(data, dataStart, raw, 0, raw.Length);

        // Move to just after endstream
        int endStreamIndex = IndexOfKeyword(data, dataEnd, "endstream"u8);
        _lexer.Position = endStreamIndex >= 0 ? endStreamIndex + 9 : (int)Math.Min(dataEnd + 16, data.Length);

        var stream = new PdfStream(new PdfDictionary(map), raw);
        if (Encryption is { } encryption)
        {
            stream.AttachEncryption(encryption, _currentObjectNumber, _currentObjectGeneration);
        }

        return stream;
    }

    private byte[] DecryptString(byte[] value)
    {
        if (Encryption is null)
        {
            return value;
        }

        return Encryption.DecryptStringIfNeeded(value, _currentObjectNumber, _currentObjectGeneration, DecryptStrings);
    }

    private static bool EndStreamFollows(byte[] data, int position)
    {
        int p = position;
        while (p < data.Length && data[p] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
        {
            p++;
        }

        return p + 9 <= data.Length && data.AsSpan(p, 9).SequenceEqual("endstream"u8);
    }

    private static int FindEndStream(byte[] data, int from)
    {
        int index = IndexOfKeyword(data, from, "endstream"u8);
        return index >= 0 ? index : data.Length;
    }

    /// <summary>Search for an ASCII keyword in the buffer.</summary>
    public static int IndexOfKeyword(byte[] data, int from, ReadOnlySpan<byte> keyword)
    {
        int first = keyword[0];
        int last = data.Length - keyword.Length;
        for (int i = Math.Max(from, 0); i <= last; i++)
        {
            if (data[i] != first)
            {
                continue;
            }

            if (data.AsSpan(i, keyword.Length).SequenceEqual(keyword))
            {
                return i;
            }
        }

        return -1;
    }
}
