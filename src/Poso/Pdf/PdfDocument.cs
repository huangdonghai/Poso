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
    private readonly Security.PdfEncryptionContext _encryption = new();
    private PdfXref _xref;
    private readonly Dictionary<long, PdfObject> _objectCache = new();
    private readonly object _gate = new();
    private readonly Dictionary<(int, Graphics.IGraphicsFactory), Fonts.PdfFont> _fontCache = new();
    private readonly object _fontGate = new();
    private bool _disposed;
    private bool _hasEncrypt;

    private PdfDocument(byte[] buffer, string? password)
    {
        _buffer = buffer;
        _encryption.Password = password;
        Version = ParseVersion(_buffer);

        // Pass 1: read the cross-reference structure with no key yet (the encryption dictionary and
        // the document /ID are always in the clear, so the trailer is readable regardless).
        _xref = PdfXref.Load(_buffer, Resolve, _encryption);

        // Establish the standard security handler from the trailer /Encrypt + /ID and the password.
        InitializeEncryption();

        // Pass 2 (encrypted files only): rebuild the cross-reference now that the key is known, so
        // cross-reference stream data and object streams decrypt correctly.
        if (_encryption.Handler is not null)
        {
            lock (_gate)
            {
                _objectCache.Clear();
            }

            _xref = PdfXref.Load(_buffer, Resolve, _encryption);
        }
    }

    /// <summary>
    /// Returns the font for a font resource's object number, creating and fully realising it (including
    /// the backend typeface) exactly once under a lock. Without this, every page render builds a fresh
    /// interpreter and re-parses each embedded font (decompress + CFF/OpenType build + typeface), which
    /// dominates cost for font-heavy documents; sharing the parsed font removes that repetition.
    /// Cached per (object, factory) pair: a backend font object only renders in the backend that made
    /// it, so switching factories mid-document (e.g. a live back-end toggle) must not hand out the
    /// previous back-end's typeface. Type3 fonts are excluded by the caller (they hold
    /// mutable glyph-width state accumulated while their charprocs run).
    /// </summary>
    internal Fonts.PdfFont GetOrAddFont(int objectNumber, Graphics.IGraphicsFactory factory, PdfDictionary dictionary)
    {
        lock (_fontGate)
        {
            var key = (objectNumber, factory);
            if (_fontCache.TryGetValue(key, out Fonts.PdfFont? cached))
            {
                return cached;
            }

            Fonts.PdfFont font = Fonts.PdfFont.Create(this, factory, dictionary);
            _ = font.GraphicsFont; // realise the backend typeface once, here, so shared instances stay immutable
            _fontCache[key] = font;
            return font;
        }
    }

    private void InitializeEncryption()
    {
        PdfObject? encryptObject = _xref.Trailer.Get("Encrypt");
        if (encryptObject is null)
        {
            return;
        }

        _hasEncrypt = true;
        if (encryptObject is PdfIndirectRef reference)
        {
            _encryption.EncryptionDictObjectNumber = reference.ObjectNumber;
        }

        PdfDictionary? encryptDictionary = ResolveEncryptionDictionary(encryptObject);
        if (encryptDictionary is null)
        {
            return;
        }

        byte[]? id0 = (_xref.Trailer.GetArray("ID") is { Count: >= 1 } id && id[0] is PdfString first) ? first.Bytes : null;
        _encryption.Handler = Security.PdfEncryption.TryCreate(encryptDictionary, id0, _encryption.Password, diagnostics: null);
    }

    /// <summary>
    /// Resolves the /Encrypt dictionary, falling back to a raw scan for the object header when the
    /// cross-reference is not yet trustworthy (encrypted files that use cross-reference streams).
    /// The encryption dictionary itself is never encrypted, so scanning is safe.
    /// </summary>
    private PdfDictionary? ResolveEncryptionDictionary(PdfObject? encryptObject)
    {
        if (Resolve(encryptObject) is PdfDictionary direct)
        {
            return direct;
        }

        if (encryptObject is not PdfIndirectRef reference)
        {
            return null;
        }

        int offset = FindObjectOffset(reference.ObjectNumber);
        if (offset < 0)
        {
            return null;
        }

        var parser = new Parsing.PdfParser(_buffer, offset) { Resolver = Resolve, Encryption = _encryption, DecryptStrings = false };
        return parser.TryParseIndirectObject()?.Object as PdfDictionary;
    }

    private int FindObjectOffset(int objectNumber)
    {
        string marker = objectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ";
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(marker);
        for (int i = 0; i + needle.Length + 4 <= _buffer.Length; i++)
        {
            if (!(_buffer.AsSpan(i, needle.Length).SequenceEqual(needle)))
            {
                continue;
            }

            // Require the object number to start a line (preceded by whitespace or the header).
            if (i > 0 && !Parsing.PdfLexer.IsWhiteSpace(_buffer[i - 1]))
            {
                continue;
            }

            int p = i + needle.Length;
            while (p < _buffer.Length && _buffer[p] >= (byte)'0' && _buffer[p] <= (byte)'9')
            {
                p++;
            }

            while (p < _buffer.Length && Parsing.PdfLexer.IsWhiteSpace(_buffer[p]))
            {
                p++;
            }

            if (p + 3 <= _buffer.Length && _buffer.AsSpan(p, 3).SequenceEqual("obj"u8))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the document is encrypted and a valid password has been supplied.</summary>
    public bool IsEncrypted => _encryption.Handler is not null;

    /// <summary>True when the supplied (or empty) password matches the document user password entry.</summary>
    public bool UserPasswordVerified => _encryption.Handler?.UserPasswordVerified ?? false;

    /// <summary>
    /// True when the document declares encryption (has an /Encrypt entry) but the current password does
    /// not verify the user-password entry �?i.e. a correct password is still required to open it. This is
    /// computable even when a wrong key prevented the cross-reference from decoding (AES-256 path).
    /// </summary>
    public bool NeedsPassword => _hasEncrypt && !UserPasswordVerified;

    // ---- Optional content (layers), ISO 32000-1 8.11.2 ----

    private readonly HashSet<int> _ocgOn = new();
    private readonly HashSet<int> _ocgOff = new();
    private bool _ocgOnExplicit;
    private bool _ocgLoaded;

    /// <summary>Whether an optional content group (by object number) is currently visible.</summary>
    public bool IsOptionalContentGroupVisible(int objectNumber)
    {
        EnsureOptionalContent();
        if (_ocgOnExplicit)
        {
            return _ocgOn.Contains(objectNumber);
        }

        return !_ocgOff.Contains(objectNumber);
    }

    /// <summary>Toggle an optional content group's visibility (viewer layer on/off).</summary>
    public void SetOptionalContentGroupVisible(int objectNumber, bool visible)
    {
        EnsureOptionalContent();
        if (visible)
        {
            _ocgOn.Add(objectNumber);
            _ocgOff.Remove(objectNumber);
        }
        else
        {
            _ocgOff.Add(objectNumber);
            _ocgOn.Remove(objectNumber);
            _ocgOnExplicit = true;
        }
    }

    private void EnsureOptionalContent()
    {
        if (_ocgLoaded)
        {
            return;
        }

        _ocgLoaded = true;
        PdfObject? propertiesObject = CatalogInfo.Dictionary.Get("OCProperties");
        if (Resolve(propertiesObject) is not PdfDictionary properties)
        {
            return;
        }

        if (Resolve(properties.Get("D")) is not PdfDictionary defaults)
        {
            return;
        }

        if (defaults.GetArray("ON") is { } on)
        {
            _ocgOnExplicit = true;
            CollectObjectNumbers(on, _ocgOn);
        }

        if (defaults.GetArray("OFF") is { } off)
        {
            CollectObjectNumbers(off, _ocgOff);
        }
    }

    private void CollectObjectNumbers(PdfArray array, HashSet<int> into)
    {
        foreach (PdfObject item in array)
        {
            if (item is PdfIndirectRef reference)
            {
                into.Add(reference.ObjectNumber);
            }
        }
    }

    /// <summary>Loads a PDF document from a file.</summary>
    public static PdfDocument Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new PdfDocument(File.ReadAllBytes(path), null);
    }

    /// <summary>Loads an encrypted PDF from a file with the owner or user password.</summary>
    public static PdfDocument Open(string path, string? password)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new PdfDocument(File.ReadAllBytes(path), password);
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

        return new PdfDocument(data, null);
    }

    /// <summary>Loads an encrypted PDF from a stream with the owner or user password.</summary>
    public static PdfDocument Open(Stream stream, string? password, bool leaveOpen = false)
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

        return new PdfDocument(data, password);
    }

    /// <summary>Loads a PDF document from a byte array (the buffer is not copied).</summary>
    public static PdfDocument FromArray(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PdfDocument(data, null);
    }

    /// <summary>Loads an encrypted PDF from a byte array (the buffer is not copied).</summary>
    public static PdfDocument FromArray(byte[] data, string? password)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PdfDocument(data, password);
    }

    // ---- Password-prompt open helpers (viewer / UI integration) ----

    /// <summary>
    /// Opens a document from a file, transparently handling password protection. The file is first
    /// attempted without a password (which succeeds for unencrypted and owner-only/permissions files,
    /// so <paramref name="passwordPrompt"/> is never called for those). When a password is required the
    /// callback is consulted repeatedly until it returns one that unlocks the document, or <c>null</c>
    /// to give up (throws <see cref="PdfPasswordRejectedException"/>).
    /// </summary>
    public static PdfDocument Open(string path, Func<string?> passwordPrompt)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(passwordPrompt);
        return OpenWithPrompt(File.ReadAllBytes(path), passwordPrompt);
    }

    /// <summary>Opens a document from a stream with password-prompt support (see the path overload).</summary>
    public static PdfDocument Open(Stream stream, Func<string?> passwordPrompt, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(passwordPrompt);
        return OpenWithPrompt(ReadAllBytes(stream, leaveOpen), passwordPrompt);
    }

    /// <summary>Opens a document from a byte array with password-prompt support (see the path overload).</summary>
    public static PdfDocument FromArray(byte[] data, Func<string?> passwordPrompt)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(passwordPrompt);
        return OpenWithPrompt(data, passwordPrompt);
    }

    /// <summary>
    /// Asynchronously opens a document from a file, consulting <paramref name="passwordPrompt"/> only when
    /// the document is password protected. The async shape suits UI hosts that show a modal dialog without
    /// blocking the interface thread (see the synchronous <see cref="Open(string, Func{string?})")/> overload.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(string path, Func<Task<string?>> passwordPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(passwordPrompt);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await OpenWithPromptAsync(data, passwordPrompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asynchronously opens a document from a stream with password-prompt support.</summary>
    public static async Task<PdfDocument> OpenAsync(Stream stream, Func<Task<string?>> passwordPrompt, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(passwordPrompt);
        byte[] data = await ReadAllBytesAsync(stream, leaveOpen, cancellationToken).ConfigureAwait(false);
        return await OpenWithPromptAsync(data, passwordPrompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asynchronously opens a document from a byte array with password-prompt support.</summary>
    public static async Task<PdfDocument> FromArrayAsync(byte[] data, Func<Task<string?>> passwordPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(passwordPrompt);
        return await OpenWithPromptAsync(data, passwordPrompt, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PdfDocument> OpenWithPromptAsync(byte[] data, Func<Task<string?>> passwordPrompt, CancellationToken cancellationToken)
    {
        // Fast path: unencrypted or permissions-only documents open with an empty password (no prompt).
        PdfDocument? unlocked = TryOpenWithPassword(data, null);
        if (unlocked is not null)
        {
            return unlocked;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? candidate = await passwordPrompt().ConfigureAwait(false);
            if (candidate is null)
            {
                throw new PdfPasswordRejectedException(
                    "This document is password protected and could not be opened (no or incorrect password).");
            }

            PdfDocument? attempt = TryOpenWithPassword(data, candidate);
            if (attempt is not null)
            {
                return attempt;
            }
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, bool leaveOpen, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        byte[] data = copy.ToArray();

        if (!leaveOpen)
        {
            stream.Dispose();
        }

        return data;
    }

    private static PdfDocument OpenWithPrompt(byte[] data, Func<string?> passwordPrompt)
    {
        // Fast path: unencrypted or permissions-only documents open with an empty password.
        PdfDocument? unlocked = TryOpenWithPassword(data, null);
        if (unlocked is not null)
        {
            return unlocked;
        }

        // Encrypted: ask until the supplied password verifies or the caller gives up.
        while (true)
        {
            string? candidate = passwordPrompt();
            if (candidate is null)
            {
                throw new PdfPasswordRejectedException(
                    "This document is password protected and could not be opened (no or incorrect password).");
            }

            PdfDocument? attempt = TryOpenWithPassword(data, candidate);
            if (attempt is not null)
            {
                return attempt;
            }
        }
    }

    private static PdfDocument? TryOpenWithPassword(byte[] data, string? password)
    {
        PdfDocument document;
        try
        {
            document = new PdfDocument(data, password);
        }
        catch (PdfException)
        {
            // A wrong key can make cross-reference / object-stream decoding fail; treat it as needing a password.
            return null;
        }

        if (!document.NeedsPassword)
        {
            return document;
        }

        document.Dispose();
        return null;
    }

    private static byte[] ReadAllBytes(Stream stream, bool leaveOpen)
    {
        byte[] data;
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
        {
            data = segment.ToArray();
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

        return data;
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

    /// <summary>Interactive form (AcroForm); null when the document declares no form.</summary>
    public PdfAcroForm? AcroForm => _acroForm ??= PdfAcroForm.Load(this);

    private PdfAcroForm? _acroForm;

    /// <summary>
    /// Flattened list of every form field (root fields plus all descendants). Empty when the document has
    /// no form. Reading field values and structure only; interactive editing is not supported.
    /// </summary>
    public IReadOnlyList<PdfFormField> GetFormFields()
    {
        if (AcroForm is not { } form)
        {
            return Array.Empty<PdfFormField>();
        }

        var fields = new List<PdfFormField>();
        void AddRecursive(PdfFormField field)
        {
            fields.Add(field);
            foreach (PdfFormField child in field.Children)
            {
                AddRecursive(child);
            }
        }

        foreach (PdfFormField root in form.Fields)
        {
            AddRecursive(root);
        }

        return fields;
    }

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
                        var parser = new PdfParser(_buffer, (int)entry.Value.Offset) { Resolver = Resolve, Encryption = _encryption };
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

        // Release the backend typefaces held by the shared font cache (they live for the document's
        // lifetime, so free them here rather than waiting on finalisation).
        lock (_fontGate)
        {
            foreach (Fonts.PdfFont font in _fontCache.Values)
            {
                (font.GraphicsFont as IDisposable)?.Dispose();
            }

            _fontCache.Clear();
        }
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
