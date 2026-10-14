using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;

namespace Poso.Pdf.Structure;

/// <summary>
/// Cross-reference table loader: supports traditional xref tables, xref streams (including /Prev incremental chains),
/// /XRefStm for hybrid-reference files, and full-text scanning to rebuild the object table from <c>N G obj</c>
/// patterns when the xref is corrupt.
/// Merge strategy: traverse the startxref chain from newest to oldest; the first entry seen wins
/// (newer definitions override older ones).
/// </summary>
public sealed class PdfXref
{
    private static readonly byte[] StartXrefKeyword = "startxref"u8.ToArray();
    private static readonly byte[] TrailerKeyword = "trailer"u8.ToArray();
    private static readonly byte[] ObjKeyword = "obj"u8.ToArray();

    private readonly Dictionary<long, PdfXrefEntry> _entries = new();
    private readonly Dictionary<int, PdfObjectStream> _objectStreams = new();
    private readonly object _gate = new();
    private Security.PdfEncryptionContext? _encryption;

    public PdfXref()
    {
    }

    /// <summary>Document trailer dictionary (containing /Root, /Size, /Info, /Encrypt, etc.).</summary>
    public PdfDictionary Trailer { get; private set; } = null!;

    /// <summary>Whether the table was rebuilt by full-text scanning (when the xref is corrupt).</summary>
    public bool WasRebuilt { get; private set; }

    public static PdfXref Load(byte[] data, Func<PdfIndirectRef, PdfObject?>? resolver, Security.PdfEncryptionContext? encryption = null)
    {
        var xref = new PdfXref { _encryption = encryption };
        if (!xref.TryLoadFromStartXref(data, resolver))
        {
            xref.RebuildByScanning(data);
        }

        return xref;
    }

    public PdfXrefEntry? GetEntry(int objectNumber, int generation = 0)
    {
        long key = MakeKey(objectNumber, generation);
        if (_entries.TryGetValue(key, out var entry))
        {
            return entry;
        }

        // Compatibility with entries whose generation is non-zero: fall back to a lookup by object number.
        foreach (var candidate in _entries)
        {
            if ((int)(candidate.Key >> 32) == objectNumber)
            {
                return candidate.Value;
            }
        }

        return null;
    }

    public PdfObject? GetObjectStreamObject(int streamObjectNumber, int objectNumber, Func<PdfIndirectRef, PdfObject?> resolver)
    {
        lock (_gate)
        {
            if (!_objectStreams.TryGetValue(streamObjectNumber, out var objectStream))
            {
                if (resolver(new PdfIndirectRef(streamObjectNumber, 0)) is not PdfStream stream)
                {
                    return null;
                }

                objectStream = new PdfObjectStream(streamObjectNumber, stream, resolver);
                _objectStreams[streamObjectNumber] = objectStream;
            }

            return objectStream.GetObject(objectNumber);
        }
    }

    private static long MakeKey(int objectNumber, int generation) => ((long)objectNumber << 32) | (uint)generation;

    private void AddEntry(int objectNumber, int generation, PdfXrefEntry entry)
    {
        // Traverse the chain from newest to oldest: first entry seen wins.
        long key = MakeKey(objectNumber, generation);
        if (!_entries.ContainsKey(key))
        {
            _entries[key] = entry;
        }
    }

    private void AddEntryOverwrite(int objectNumber, int generation, PdfXrefEntry entry)
    {
        // Rebuild by scanning: later definitions in the file override earlier ones (incremental update semantics).
        _entries[MakeKey(objectNumber, generation)] = entry;
    }

    private bool TryLoadFromStartXref(byte[] data, Func<PdfIndirectRef, PdfObject?>? resolver)
    {
        int startXrefIndex = LastIndexOfKeyword(data, "startxref"u8);
        if (startXrefIndex < 0)
        {
            return false;
        }

        var lexer = new PdfLexer(data, startXrefIndex + StartXrefKeyword.Length);
        lexer.SkipWhiteSpace();
        var token = lexer.NextToken();
        if (token.Kind != PdfTokenKind.Integer)
        {
            return false;
        }

        PdfDictionary? trailerWithRoot = null;
        PdfDictionary? firstSectionTrailer = null;
        var visited = new HashSet<long>();
        long current = token.IntegerValue;

        while (current >= 0 && current < data.Length && visited.Add(current))
        {
            var sectionLexer = new PdfLexer(data, (int)current);
            sectionLexer.SkipWhiteSpace();
            var head = sectionLexer.NextToken();

            PdfDictionary? sectionTrailer;
            long previous = -1;
            int xrefStmOffset = -1;

            if (head.Kind == PdfTokenKind.Keyword && sectionLexer.Match(head, "xref"u8))
            {
                sectionTrailer = ParseXrefTable(data, (int)current, out previous);
                if (sectionTrailer is null)
                {
                    return false;
                }

                if (sectionTrailer.GetIntegerValue("XRefStm") is long hybrid)
                {
                    xrefStmOffset = (int)hybrid;
                }
            }
            else
            {
                sectionTrailer = ParseXrefStream(data, (int)current, resolver, out previous);
                if (sectionTrailer is null)
                {
                    return false;
                }
            }

            firstSectionTrailer ??= sectionTrailer;
            if (trailerWithRoot is null && sectionTrailer.Get("Root") is not null)
            {
                trailerWithRoot = sectionTrailer;
            }

            if (xrefStmOffset >= 0 && xrefStmOffset < data.Length && visited.Add(xrefStmOffset))
            {
                ParseXrefStream(data, xrefStmOffset, resolver, out _);
            }

            current = previous;
        }

        if (trailerWithRoot is null)
        {
            // The chain is complete but /Root was not found: fall back to rebuild by scanning.
            if (firstSectionTrailer is not null)
            {
                Trailer = firstSectionTrailer;
            }

            return false;
        }

        Trailer = trailerWithRoot;
        return true;
    }

    private PdfDictionary? ParseXrefTable(byte[] data, int offset, out long previous)
    {
        previous = -1;
        var lexer = new PdfLexer(data, offset);
        lexer.SkipWhiteSpace();
        var head = lexer.NextToken();
        if (head.Kind != PdfTokenKind.Keyword || !lexer.Match(head, "xref"u8))
        {
            return null;
        }

        while (true)
        {
            lexer.SkipWhiteSpace();
            if (lexer.AtEnd)
            {
                return null;
            }

            if (lexer.PeekKind() == PdfTokenKind.Keyword)
            {
                var keyword = lexer.NextToken();
                if (!lexer.Match(keyword, "trailer"u8))
                {
                    return null;
                }

                var parser = new PdfParser(data, lexer.Position) { Encryption = _encryption, DecryptStrings = false };
                if (parser.ParseObject() is not PdfDictionary trailer)
                {
                    return null;
                }

                previous = trailer.GetIntegerValue("Prev") ?? -1;
                return trailer;
            }

            var startToken = lexer.NextToken();
            var countToken = lexer.NextToken();
            if (startToken.Kind != PdfTokenKind.Integer || countToken.Kind != PdfTokenKind.Integer)
            {
                return null;
            }

            long start = startToken.IntegerValue;
            long count = countToken.IntegerValue;

            for (long i = 0; i < count; i++)
            {
                lexer.SkipWhiteSpace();
                if (lexer.AtEnd)
                {
                    return null;
                }

                var offsetToken = lexer.NextToken();
                var generationToken = lexer.NextToken();
                var typeToken = lexer.NextToken();
                if (offsetToken.Kind != PdfTokenKind.Integer ||
                    generationToken.Kind != PdfTokenKind.Integer ||
                    typeToken.Kind != PdfTokenKind.Keyword)
                {
                    return null;
                }

                if (lexer.Match(typeToken, "n"u8))
                {
                    AddEntry((int)(start + i), (int)generationToken.IntegerValue,
                        new PdfXrefEntry(PdfXrefEntryType.InFile, offsetToken.IntegerValue, 0, 0, (int)generationToken.IntegerValue));
                }
            }
        }
    }

    private PdfDictionary? ParseXrefStream(byte[] data, int offset, Func<PdfIndirectRef, PdfObject?>? resolver, out long previous)
    {
        previous = -1;
        var parser = new PdfParser(data, offset) { Resolver = resolver, Encryption = _encryption, DecryptStrings = false };
        var parsed = parser.TryParseIndirectObject();
        if (parsed is null || parsed.Value.Object is not PdfStream stream)
        {
            return null;
        }

        PdfDictionary dictionary = stream.Dictionary;
        if (dictionary.GetNameValue("Type") != "Xref")
        {
            return null;
        }

        PdfArray? widths = dictionary.GetArray("W");
        if (widths is null || widths.Count < 3)
        {
            return null;
        }

        int w1 = widths.GetNumber(0)?.AsInt() ?? 0;
        int w2 = widths.GetNumber(1)?.AsInt() ?? 0;
        int w3 = widths.GetNumber(2)?.AsInt() ?? 0;
        if (w1 is < 0 or > 8 || w2 is < 0 or > 8 || w3 is < 0 or > 8)
        {
            return null;
        }

        long size = dictionary.GetIntegerValue("Size") ?? 0;
        var sections = new List<(long Start, long Count)>();
        if (dictionary.GetArray("Index") is { Count: >= 2 } index)
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
            {
                sections.Add((index.GetNumber(i)?.AsLong() ?? 0, index.GetNumber(i + 1)?.AsLong() ?? 0));
            }
        }
        else
        {
            sections.Add((0, size));
        }

        byte[] decoded;
        try
        {
            decoded = stream.Decode();
        }
        catch (PdfException)
        {
            return null;
        }

        int rowLength = w1 + w2 + w3;
        if (rowLength <= 0)
        {
            return null;
        }

        int position = 0;
        foreach (var (start, count) in sections)
        {
            for (long i = 0; i < count; i++)
            {
                if (position + rowLength > decoded.Length)
                {
                    break;
                }

                long fieldType = ReadField(decoded, position, w1);
                long field2 = ReadField(decoded, position + w1, w2);
                long field3 = ReadField(decoded, position + w1 + w2, w3);
                position += rowLength;

                int entryType = w1 == 0 ? 1 : (int)fieldType;
                switch (entryType)
                {
                    case 1:
                        AddEntry((int)(start + i), (int)field3,
                            new PdfXrefEntry(PdfXrefEntryType.InFile, field2, 0, 0, (int)field3));
                        break;

                    case 2:
                        AddEntry((int)(start + i), 0,
                            new PdfXrefEntry(PdfXrefEntryType.InObjectStream, 0, (int)field2, (int)field3, 0));
                        break;
                }
            }
        }

        previous = dictionary.GetIntegerValue("Prev") ?? -1;
        return dictionary;
    }

    private static long ReadField(byte[] data, int start, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[start + i];
        }

        return value;
    }

    private void RebuildByScanning(byte[] data)
    {
        WasRebuilt = true;

        // 1) Collect the last trailer dictionary containing /Root.
        PdfDictionary? trailer = null;
        int searchFrom = 0;
        while (true)
        {
            int trailerIndex = PdfParser.IndexOfKeyword(data, searchFrom, TrailerKeyword);
            if (trailerIndex < 0)
            {
                break;
            }

            try
            {
                var parser = new PdfParser(data, trailerIndex + TrailerKeyword.Length) { Encryption = _encryption, DecryptStrings = false };
                if (parser.ParseObject() is PdfDictionary candidate && candidate.Get("Root") is not null)
                {
                    trailer = candidate;
                }
            }
            catch (PdfException)
            {
                // Ignore corrupt trailer sections.
            }

            searchFrom = trailerIndex + TrailerKeyword.Length;
        }

        // 2) Scan all "N G obj" occurrences.
        int? catalogObjectNumber = null;
        long maxObjectNumber = 0;
        int position = 0;

        while (position < data.Length)
        {
            int objIndex = PdfParser.IndexOfKeyword(data, position, ObjKeyword);
            if (objIndex < 0)
            {
                break;
            }

            if (TryParseObjectHeaderBefore(data, objIndex, out int objectNumber, out int generation, out int headerStart))
            {
                maxObjectNumber = Math.Max(maxObjectNumber, objectNumber);
                AddEntryOverwrite(objectNumber, generation,
                    new PdfXrefEntry(PdfXrefEntryType.InFile, headerStart, 0, 0, generation));

                if (catalogObjectNumber is null)
                {
                    try
                    {
                        var probe = new PdfParser(data, headerStart) { Encryption = _encryption, DecryptStrings = false };
                        var parsed = probe.TryParseIndirectObject();
                        if (parsed?.Object is PdfDictionary dict && dict.GetNameValue("Type") == "Catalog")
                        {
                            catalogObjectNumber = objectNumber;
                        }
                    }
                    catch (PdfException)
                    {
                        // Ignore objects that cannot be parsed.
                    }
                }
            }

            position = objIndex + ObjKeyword.Length;
        }

        // 3) Assemble the trailer.
        if (trailer is null || trailer.Get("Root") is null)
        {
            if (catalogObjectNumber is not null)
            {
                var map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                if (trailer is not null)
                {
                    foreach (string key in trailer.Keys)
                    {
                        if (trailer[key] is { } value)
                        {
                            map[key] = value;
                        }
                    }
                }

                map["Root"] = new PdfIndirectRef(catalogObjectNumber.Value, 0);
                map["Size"] = new PdfNumber(maxObjectNumber + 1, true);
                trailer = new PdfDictionary(map);
            }
        }

        if (trailer is null || trailer.Get("Root") is null)
        {
            throw new PdfFormatException("Unable to locate the PDF cross-reference table or document catalog (the file may be severely corrupt)");
        }

        Trailer = trailer;
    }

    /// <summary>Parse the "N G" object header backwards from the position of the obj keyword.</summary>
    private static bool TryParseObjectHeaderBefore(byte[] data, int objIndex, out int objectNumber, out int generation, out int headerStart)
    {
        objectNumber = 0;
        generation = 0;
        headerStart = 0;

        int p = objIndex - 1;
        while (p >= 0 && PdfLexer.IsWhiteSpace(data[p]))
        {
            p--;
        }

        int generationEnd = p + 1;
        int generationStart = p;
        while (generationStart >= 0 && data[generationStart] >= (byte)'0' && data[generationStart] <= (byte)'9')
        {
            generationStart--;
        }

        generationStart++;
        if (generationStart >= generationEnd)
        {
            return false;
        }

        p = generationStart - 1;
        while (p >= 0 && PdfLexer.IsWhiteSpace(data[p]))
        {
            p--;
        }

        int numberEnd = p + 1;
        int numberStart = p;
        while (numberStart >= 0 && data[numberStart] >= (byte)'0' && data[numberStart] <= (byte)'9')
        {
            numberStart--;
        }

        numberStart++;
        if (numberStart >= numberEnd)
        {
            return false;
        }

        // The object number must be preceded by whitespace or the file header (to avoid matching content bytes such as "123obj").
        if (numberStart > 0 && !PdfLexer.IsWhiteSpace(data[numberStart - 1]))
        {
            return false;
        }

        objectNumber = (int)PdfLexer.ParseInteger(data, numberStart, numberEnd - numberStart);
        generation = (int)PdfLexer.ParseInteger(data, generationStart, generationEnd - generationStart);
        headerStart = numberStart;
        return true;
    }

    private static int LastIndexOfKeyword(byte[] data, ReadOnlySpan<byte> keyword)
    {
        byte first = keyword[0];
        for (int i = data.Length - keyword.Length; i >= 0; i--)
        {
            if (data[i] == first && data.AsSpan(i, keyword.Length).SequenceEqual(keyword))
            {
                return i;
            }
        }

        return -1;
    }
}
