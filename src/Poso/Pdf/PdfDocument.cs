using System.Text;
using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;
using Poso.Pdf.Structure;

namespace Poso.Pdf;

/// <summary>
/// PDF document. Loads from a file path, stream or byte array, parses the cross-reference table
/// and provides object resolution. The whole file is kept in memory (simplifies random access
/// and thread safety for viewer scenarios).
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly byte[] _buffer;
    private readonly PdfXref _xref;
    private readonly Dictionary<long, PdfObject> _objectCache = new();
    private readonly object _gate = new();
    private bool _disposed;

    private PdfDocument(byte[] buffer)
    {
        _buffer = buffer;
        Version = ParseVersion(_buffer);
        _xref = PdfXref.Load(_buffer, Resolve);

        if (_xref.Trailer.Get("Encrypt") is not null)
        {
            throw new PdfUnsupportedException("Encrypted PDFs are not supported yet (planned for v0.2 with the standard security handler)");
        }
    }

    /// <summary>Loads a PDF document from a file.</summary>
    public static PdfDocument Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new PdfDocument(File.ReadAllBytes(path));
    }

    /// <summary>Loads a PDF document from a stream.</summary>
    public static PdfDocument Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] data;
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            data = buffer.ToArray();
        }
        else
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            data = copy.ToArray();
        }

        if (!leaveOpen)
        {
            stream.Dispose();
        }

        return new PdfDocument(data);
    }

    /// <summary>Loads a PDF document from a byte array (the buffer is not copied).</summary>
    public static PdfDocument FromArray(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PdfDocument(data);
    }

    /// <summary>PDF version (e.g. "1.7"); "1.4" when parsing fails.</summary>
    public string Version { get; }

    /// <summary>Document trailer dictionary.</summary>
    public PdfDictionary Trailer => _xref.Trailer;

    /// <summary>Document catalog dictionary (/Root).</summary>
    public PdfDictionary? Catalog => Resolve(Trailer.Get("Root")) as PdfDictionary;

    /// <summary>Document information dictionary (/Info).</summary>
    public PdfDictionary? Info => Resolve(Trailer.Get("Info")) as PdfDictionary;

    /// <summary>Whether the cross-reference table was rebuilt by full-text scanning (corrupt file).</summary>
    public bool WasRebuilt => _xref.WasRebuilt;

    /// <summary>Document catalog object (entry point for the page tree, outlines and named destinations).</summary>
    public Structure.PdfCatalog CatalogInfo => _catalog ??= Structure.PdfCatalog.Load(this);

    private Structure.PdfCatalog? _catalog;

    /// <summary>All pages (lazily loaded, thread-safe).</summary>
    public IReadOnlyList<PdfPage> Pages
    {
        get
        {
            if (_pages is not null)
            {
                return _pages;
            }

            lock (_gate)
            {
                return _pages ??= Structure.PdfPageTree.LoadPages(this);
            }
        }
    }

    private IReadOnlyList<PdfPage>? _pages;

    /// <summary>Number of pages.</summary>
    public int PageCount => Pages.Count;

    /// <summary>Outline (bookmark) tree root; null when the document has no outline.</summary>
    public PdfOutline? Outline => _outline ??= PdfOutline.Load(this);

    private PdfOutline? _outline;

    /// <summary>Resolves an indirect reference; non-reference objects are returned as-is; null and PdfNull return null.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        switch (obj)
        {
            case null:
            case PdfNull:
                return null;

            case PdfIndirectRef reference:
                return GetObject(reference.ObjectNumber, reference.Generation);

            default:
                return obj;
        }
    }

    /// <summary>Gets an indirect object by object number and generation (cached, thread-safe).</summary>
    public PdfObject? GetObject(int objectNumber, int generation = 0)
    {
        ThrowIfDisposed();

        // While loading the cross-reference table the resolver callback may run before _xref is
        // assigned (e.g. an xref stream whose /Length is an indirect object); returning null here
        // makes the caller fall back to searching for endstream.
        PdfXref? xref = _xref;
        if (xref is null)
        {
            return null;
        }

        long key = ((long)objectNumber << 32) | (uint)generation;

        lock (_gate)
        {
            if (_objectCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            PdfXrefEntry? entry = xref.GetEntry(objectNumber, generation);
            PdfObject? result = null;

            if (entry is not null)
            {
                switch (entry.Value.Type)
                {
                    case PdfXrefEntryType.InFile:
                        var parser = new PdfParser(_buffer, (int)entry.Value.Offset) { Resolver = Resolve };
                        result = parser.TryParseIndirectObject()?.Object;
                        break;

                    case PdfXrefEntryType.InObjectStream:
                        result = xref.GetObjectStreamObject(entry.Value.StreamObjectNumber, objectNumber, Resolve);
                        break;
                }
            }

            if (result is not null)
            {
                _objectCache[key] = result;
            }

            return result;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfDocument));
        }
    }

    private static string ParseVersion(byte[] data)
    {
        int limit = Math.Min(data.Length, 1024);
        for (int i = 0; i + 4 < limit; i++)
        {
            if (data[i] == (byte)'%' &&
                data[i + 1] == (byte)'P' &&
                data[i + 2] == (byte)'D' &&
                data[i + 3] == (byte)'F' &&
                data[i + 4] == (byte)'-')
            {
                int start = i + 5;
                int end = start;
                while (end < limit && (data[end] is >= (byte)'0' and <= (byte)'9' || data[end] == (byte)'.'))
                {
                    end++;
                }

                return end > start ? Encoding.ASCII.GetString(data, start, end - start) : "1.4";
            }
        }

        return "1.4";
    }
}
