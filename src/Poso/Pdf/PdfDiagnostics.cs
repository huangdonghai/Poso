namespace Poso.Pdf;

/// <summary>Categories of PDF features the renderer can recognize but does not (yet) execute.</summary>
public enum PdfUnsupportedKind
{
    Shading,
    PatternColorSpace,
    ImageCodec,
    CryptFilter,
    StreamFilter,
    Annotation,
    FontFallback,
    Other,
}

/// <summary>
/// A PDF feature that was skipped because it is not supported yet. Reported through the renderer's
/// diagnostics sink in lenient mode; in <see cref="PdfDiagnostics.Strict"/> mode it throws instead.
/// </summary>
public sealed record PdfUnsupportedFeature(PdfUnsupportedKind Kind, string Message);

/// <summary>
/// Collects "unsupported feature" reports during a single render pass. Lenient instances record and
/// continue (so one unsupported element never aborts the whole page); a <see cref="Strict"/> instance
/// throws <see cref="PdfUnsupportedException"/> so tests can assert a document is fully supported.
/// </summary>
public sealed class PdfDiagnostics
{
    private readonly List<PdfUnsupportedFeature>? _features;
    private readonly Action<PdfUnsupportedFeature>? _sink;

    public PdfDiagnostics(bool strict = false, Action<PdfUnsupportedFeature>? sink = null, bool collect = false)
    {
        Strict = strict;
        _sink = sink;
        _features = collect ? new List<PdfUnsupportedFeature>() : null;
    }

    /// <summary>An inert instance that records nothing and never throws (used by callers without render options).</summary>
    public static PdfDiagnostics Lenient { get; } = new(strict: false);

    /// <summary>When true, <see cref="Report"/> throws instead of continuing.</summary>
    public bool Strict { get; }

    /// <summary>Features recorded during this pass (empty unless created with <c>collect: true</c>).</summary>
    public IReadOnlyList<PdfUnsupportedFeature> Features => _features is null ? Array.Empty<PdfUnsupportedFeature>() : _features;

    /// <summary>Records an unsupported feature, or throws when <see cref="Strict"/>.</summary>
    public void Report(PdfUnsupportedKind kind, string message)
    {
        if (Strict)
        {
            throw new PdfUnsupportedException(message);
        }

        PdfUnsupportedFeature feature = new(kind, message);
        _features?.Add(feature);
        _sink?.Invoke(feature);
    }
}
