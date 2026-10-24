using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Poso.Avalonia.Imaging;
using Poso.Avalonia.Rendering;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;
using Vector = Avalonia.Vector;

namespace Poso.Avalonia.Controls;

/// <summary>Event data for <see cref="PdfView.PageChanged"/>.</summary>
public sealed class PageChangedEventArgs : EventArgs
{
    public PageChangedEventArgs(int pageIndex)
    {
        PageIndex = pageIndex;
    }

    /// <summary>Zero-based index of the newly current page.</summary>
    public int PageIndex { get; }
}

/// <summary>Event data for <see cref="PdfView.LinkActivated"/>.</summary>
public sealed class LinkActivatedEventArgs : EventArgs
{
    public LinkActivatedEventArgs(PdfLink link)
    {
        Link = link;
    }

    public PdfLink Link { get; }
}

/// <summary>
/// A PDF viewer control: loads a <see cref="PdfDocument"/>, renders pages through the injected
/// <see cref="IGraphicsFactory"/> backend (e.g. the Skia backend) and provides zooming, panning,
/// rotation, page navigation, text selection and search highlighting, and link activation.
/// </summary>
public sealed class PdfView : ContentControl
{
    /// <summary>Default spacing between pages (device independent pixels).</summary>
    public const double DefaultPageSpacing = 12d;

    /// <summary>Pages rendered ahead of and behind the viewport.</summary>
    private const int RenderMarginPages = 2;

    /// <summary>Pages outside the render margin whose bitmap is kept attached.</summary>
    private const int BitmapKeepMarginPages = 10;

    private static readonly PosoColor DefaultBackground = new(0xF2, 0xF2, 0xF2);

    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _pagesPanel;
    private readonly List<PdfPageView> _pageViews = new();
    private readonly DispatcherTimer _dragScrollTimer;
    private readonly DispatcherTimer _hoverTextTimer;
    private PageRenderPipeline? _pipeline;
    private Point _panOrigin;
    private Vector _panStartOffset;
    private bool _panning;
    private bool _spaceHeld;
    private bool _suppressPageChanged;
    private List<PdfSearchHit> _searchHits = new();
    private int _activeHit = -1;
    private int _searchGeneration;
    private CancellationTokenSource? _searchCts;

    // Text selection: carets are (page index, position in PdfTextPage.Text) pairs. Anchor and focus
    // are normalized into reading order when the highlight and copied text are computed.
    private bool _selecting;
    private bool _hasAnchor;
    private (int Page, int Caret) _selectionAnchor;
    private (int Page, int Caret) _selectionFocus;
    private readonly HashSet<int> _selectionPages = new();
    private bool _selectionDirty;
    private string _selectedText = string.Empty;
    private Vector _dragPoint;
    private int _clickCount;
    private long _lastClickTime;
    private Point _lastClickPosition;

    // Hover: the text cursor follows selectable text, so the pointer position is tracked and the
    // page under it is read for text off the UI thread after a short dwell.
    private Point _hoverPoint;
    private int _hoverPage = -1;
    private int _textCursorPage = -1;
    private readonly HashSet<int> _textPrefetched = new();

    // The hosting top-level, tracked so we can re-render at the new device resolution when the
    // HiDPI scaling factor changes (e.g. the window moves to a monitor with a different DPI).
    private TopLevel? _topLevel;
    private double _lastDeviceScale = -1d;

    // Top edge of every page in content coordinates (device pixels), filled by UpdatePageSizes
    private double[] _pageOffsets = Array.Empty<double>();
    private double _averagePageHeight;

    // Overlay bookkeeping: overlays are rebuilt only when these change, never on every layout pass
    private double _overlayScale = double.NaN;
    private int _overlayRotation = int.MinValue;
    private int _overlayRevision;
    private Dictionary<int, List<(RectF Rect, bool Active)>>? _hitsByPage;

    static PdfView()
    {
        FocusableProperty.OverrideMetadata(typeof(PdfView), new StyledPropertyMetadata<bool>(true));
    }

    public PdfView()
    {
        _pagesPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = DefaultPageSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _pagesPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
        };

        Content = _scrollViewer;

        // handledEventsToo: the inner ScrollViewer consumes the wheel when it can scroll, but single-page
        // mode still needs the event to turn the page when there is nothing to scroll.
        _dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _dragScrollTimer.Tick += OnDragScrollTick;

        _hoverTextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTextTimer.Tick += OnHoverTextTick;

        AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Bubble);
        LayoutUpdated += OnLayoutUpdated;
    }

    // ---- Styled properties ----

    /// <summary>The document to display.</summary>
    public static readonly StyledProperty<PdfDocument?> SourceProperty =
        AvaloniaProperty.Register<PdfView, PdfDocument?>(nameof(Source));

    public PdfDocument? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Manual zoom factor (1 = 100%). Used when <see cref="ZoomMode"/> is Custom.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PdfView, double>(nameof(Zoom), 1d);

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, ZoomMath.ClampZoom(value));
    }

    /// <summary>Current zoom mode.</summary>
    public static readonly StyledProperty<ZoomMode> ZoomModeProperty =
        AvaloniaProperty.Register<PdfView, ZoomMode>(nameof(ZoomMode), ZoomMode.Custom);

    public ZoomMode ZoomMode
    {
        get => GetValue(ZoomModeProperty);
        set => SetValue(ZoomModeProperty, value);
    }

    /// <summary>Additional page rotation in degrees (added to each page's own /Rotate).</summary>
    public static readonly StyledProperty<int> RotationProperty =
        AvaloniaProperty.Register<PdfView, int>(nameof(Rotation), 0);

    public int Rotation
    {
        get => ZoomMath.Normalize(GetValue(RotationProperty));
        set => SetValue(RotationProperty, ZoomMath.Normalize(value));
    }

    /// <summary>Single page or continuous scrolling.</summary>
    public static readonly StyledProperty<PageLayout> PageLayoutProperty =
        AvaloniaProperty.Register<PdfView, PageLayout>(nameof(PageLayout), PageLayout.Continuous);

    public PageLayout PageLayout
    {
        get => GetValue(PageLayoutProperty);
        set => SetValue(PageLayoutProperty, value);
    }

    /// <summary>Zero-based index of the current page.</summary>
    public static readonly StyledProperty<int> CurrentPageProperty =
        AvaloniaProperty.Register<PdfView, int>(nameof(CurrentPage), 0);

    public int CurrentPage
    {
        get => GetValue(CurrentPageProperty);
        private set => SetValue(CurrentPageProperty, value);
    }

    /// <summary>Page background color (drawn behind the page bitmap).</summary>
    public static readonly StyledProperty<PosoColor> PageBackgroundProperty =
        AvaloniaProperty.Register<PdfView, PosoColor>(nameof(PageBackground), DefaultBackground);

    public PosoColor PageBackground
    {
        get => GetValue(PageBackgroundProperty);
        set => SetValue(PageBackgroundProperty, value);
    }

    /// <summary>
    /// The graphics backend used for rendering. The application must inject one
    /// (e.g. <c>new SkiaGraphicsFactory()</c>) before setting <see cref="Source"/>.
    /// </summary>
    public static readonly StyledProperty<IGraphicsFactory?> GraphicsFactoryProperty =
        AvaloniaProperty.Register<PdfView, IGraphicsFactory?>(nameof(GraphicsFactory));

    public IGraphicsFactory? GraphicsFactory
    {
        get => GetValue(GraphicsFactoryProperty);
        set => SetValue(GraphicsFactoryProperty, value);
    }

    /// <summary>Spacing between pages in device independent pixels.</summary>
    public static readonly StyledProperty<double> PageSpacingProperty =
        AvaloniaProperty.Register<PdfView, double>(nameof(PageSpacing), DefaultPageSpacing);

    public double PageSpacing
    {
        get => GetValue(PageSpacingProperty);
        set => SetValue(PageSpacingProperty, value);
    }

    // ---- Events ----

    /// <summary>Raised when the current page changes.</summary>
    public event EventHandler<PageChangedEventArgs>? PageChanged;

    /// <summary>Raised when a link hotspot is clicked.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>Raised when the selected text changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when a background search publishes hits, finishes, or is cleared.</summary>
    public event EventHandler? SearchHitsChanged;

    /// <summary>True while a background search is still reading pages.</summary>
    public bool IsSearching { get; private set; }

    // ---- Public API ----

    /// <summary>Number of pages in the current document (0 when none).</summary>
    public int PageCount => Source?.PageCount ?? 0;

    /// <summary>Current search hits (page index plus page-space rectangle).</summary>
    public IReadOnlyList<PdfSearchHit> SearchHits => _searchHits;

    /// <summary>Index of the active search hit, or -1.</summary>
    public int ActiveHitIndex => _activeHit;

    /// <summary>Scrolls to the given page and makes it current.</summary>
    public void GoToPage(int pageIndex)
    {
        if (Source is null || pageIndex < 0 || pageIndex >= PageCount)
        {
            return;
        }

        if (PageLayout == PageLayout.SinglePage)
        {
            ShowSinglePage(pageIndex);
        }
        else
        {
            ScrollPageIntoView(pageIndex, center: true);
        }

        SetCurrentPage(pageIndex);
    }

    /// <summary>Goes to the next page.</summary>
    public void NextPage() => GoToPage(CurrentPage + 1);

    /// <summary>Goes to the previous page.</summary>
    public void PreviousPage() => GoToPage(CurrentPage - 1);

    /// <summary>
    /// Searches the document on a background thread and highlights the hits. Reading a page's text
    /// costs milliseconds (measured: ~5ms on the PDF specification, ~50ms on a typeset book), so it
    /// never runs on the UI thread. Hits arrive page by page - each arrival raises
    /// <see cref="SearchHitsChanged"/> and the first one scrolls into view - and the returned count is
    /// the total once the scan finishes. Starting another search cancels this one.
    /// </summary>
    public Task<int> SearchAsync(string query)
    {
        ClearSearch();

        PdfDocument? document = Source;
        IGraphicsFactory? factory = GraphicsFactory;
        if (document is null || factory is null || string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(0);
        }

        CancellationTokenSource cts = new();
        _searchCts = cts;
        IsSearching = true;

        return ScanAsync(document, factory, query, ++_searchGeneration, cts);
    }

    private Task<int> ScanAsync(PdfDocument document, IGraphicsFactory factory, string query, int generation, CancellationTokenSource cts)
    {
        return Task.Run(() =>
        {
            // Pages are read one at a time: the scan shares the machine with the render pipeline, and
            // a sweep that saturates every core makes the viewer stutter while the user scrolls.
            var batch = new List<PdfSearchHit>();
            var clock = Stopwatch.StartNew();
            int total = 0;

            try
            {
                for (int index = 0; index < document.PageCount; index++)
                {
                    if (cts.IsCancellationRequested)
                    {
                        break;
                    }

                    PdfTextPage text = document.Pages[index].GetText(factory);
                    foreach (TextHit hit in text.Search(query))
                    {
                        batch.Add(new PdfSearchHit(index, hit.Bounds));
                        total++;
                    }

                    if (batch.Count > 0 && clock.ElapsedMilliseconds >= 60)
                    {
                        PublishHits(batch, generation, finished: false);
                        batch = new List<PdfSearchHit>();
                        clock.Restart();
                    }
                }
            }
            catch (Exception)
            {
                // A page whose content cannot be parsed ends the scan with what has been found
            }

            PublishHits(batch, generation, finished: true);
            cts.Dispose();
            return total;
        });
    }

    /// <summary>
    /// Hands a batch of hits to the UI thread, which drops them once a newer search has taken over.
    /// </summary>
    private void PublishHits(List<PdfSearchHit> hits, int generation, bool finished)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _searchGeneration)
            {
                return;
            }

            if (finished)
            {
                IsSearching = false;
            }

            if (hits.Count > 0)
            {
                _searchHits.AddRange(hits);
                InvalidateOverlays();

                if (_activeHit < 0)
                {
                    GoToHit(0);
                }
                else
                {
                    UpdateOverlaysAndVisibility();
                }
            }
            else if (finished)
            {
                UpdateOverlaysAndVisibility();
            }

            SearchHitsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Shows the next search hit.</summary>
    public bool NextHit() => GoToHit(_activeHit + 1);

    /// <summary>Shows the previous search hit.</summary>
    public bool PreviousHit() => GoToHit(_activeHit - 1);

    /// <summary>Clears search highlights and stops a search that is still running.</summary>
    public void ClearSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchGeneration++;
        IsSearching = false;
        _searchHits = new List<PdfSearchHit>();
        _activeHit = -1;
        InvalidateOverlays();
        RefreshOverlays();
        SearchHitsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The selected text, or an empty string when nothing is selected.</summary>
    public string SelectedText => _selectedText;

    /// <summary>Copies the current selection to the clipboard; returns false when there is nothing to copy.</summary>
    public async Task<bool> CopySelectionAsync()
    {
        if (_selectedText.Length == 0)
        {
            return false;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(_selectedText);
        return true;
    }

    /// <summary>Clears the selection and its highlight.</summary>
    public void ClearSelection()
    {
        if (_selectionAnchor == _selectionFocus && _selectedText.Length == 0)
        {
            return;
        }

        _selecting = false;
        _hasAnchor = false;
        _dragScrollTimer.Stop();
        _selectionAnchor = default;
        _selectionFocus = default;
        UpdateSelection();
    }

    /// <summary>Marks every page overlay as stale so the next pass rebuilds them.</summary>
    private void InvalidateOverlays()
    {
        _overlayRevision++;
        _hitsByPage = null;
    }

    /// <summary>Effective zoom factor (fit modes are resolved against the viewport).</summary>
    public double EffectiveZoom
    {
        get
        {
            if (Source is null || Source.PageCount == 0)
            {
                return Zoom;
            }

            if (ZoomMode == ZoomMode.Custom)
            {
                return Zoom;
            }

            PdfPage page = Source.Pages[Math.Clamp(CurrentPage, 0, PageCount - 1)];
            return ZoomMath.FitScale(page, ViewportSize, ZoomMode, Rotation);
        }
    }

    /// <summary>Viewport size (the ScrollViewer's visible area).</summary>
    public Size ViewportSize => _scrollViewer.Viewport;

    // ---- Property change handling ----

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            OnSourceChanged();
        }
        else if (change.Property == GraphicsFactoryProperty)
        {
            OnSourceChanged();
        }
        else if (change.Property == ZoomProperty || change.Property == ZoomModeProperty ||
                 change.Property == RotationProperty || change.Property == PageSpacingProperty)
        {
            _pipeline?.Invalidate();
            _selectionDirty = true;
            UpdatePageSizes();
            UpdateOverlaysAndVisibility();
        }
        else if (change.Property == PageLayoutProperty)
        {
            _selectionDirty = true;
            UpdatePageSizes();
            UpdateOverlaysAndVisibility();
        }
        else if (change.Property == PageBackgroundProperty)
        {
            ApplyPageBackground();
        }
    }

    private void OnSourceChanged()
    {
        if (_pipeline is not null)
        {
            // Detach first so in-flight renders of the old document cannot reach the new views
            _pipeline.Rendered -= OnPageRendered;
            _pipeline.Clear();
        }

        _pipeline = null;
        _pageViews.Clear();
        _pagesPanel.Children.Clear();
        _searchCts?.Cancel();
        _searchCts = null;
        _searchGeneration++;
        IsSearching = false;
        _searchHits = new List<PdfSearchHit>();
        _activeHit = -1;
        _pageOffsets = Array.Empty<double>();
        _averagePageHeight = 0;
        _overlayScale = double.NaN;
        _overlayRotation = int.MinValue;
        _lastFitZoom = 0;
        InvalidateOverlays();

        bool hadSelection = _selectedText.Length > 0;
        _selecting = false;
        _hasAnchor = false;
        _dragScrollTimer.Stop();
        _hoverTextTimer.Stop();
        _hoverPage = -1;
        _textCursorPage = -1;
        _textPrefetched.Clear();
        _selectionAnchor = default;
        _selectionFocus = default;
        _selectedText = string.Empty;
        _selectionPages.Clear();
        _selectionDirty = false;
        if (hadSelection)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        PdfDocument? document = Source;
        IGraphicsFactory? factory = GraphicsFactory;

        if (document is null || factory is null)
        {
            CurrentPage = 0;
            return;
        }

        _pipeline = new PageRenderPipeline(document, factory);
        _pipeline.Rendered += OnPageRendered;

        SolidColorBrush pageBackground = CreatePageBackgroundBrush();
        for (int i = 0; i < document.PageCount; i++)
        {
            // The paper background keeps a not-yet-rendered page a solid sheet instead of letting the
            // (dark) viewer background show through, which otherwise flashes when paging to an uncached page.
            var view = new PdfPageView { PageIndex = i, Background = pageBackground };
            view.LinkActivated += OnLinkActivated;
            _pageViews.Add(view);
            _pagesPanel.Children.Add(view);
        }

        CurrentPage = 0;
        UpdatePageSizes();
        UpdateOverlaysAndVisibility();
    }

    private void OnPageRendered(int pageIndex, WriteableBitmap bitmap)
    {
        if (pageIndex >= 0 && pageIndex < _pageViews.Count)
        {
            _pageViews[pageIndex].SetBitmap(bitmap);
        }
    }

    private void OnLinkActivated(object? sender, PdfLink link)
    {
        if (link.Kind == PdfActionKind.GoTo && link.Destination is { } destination)
        {
            GoToPage(destination.PageIndex);
        }

        LinkActivated?.Invoke(this, new LinkActivatedEventArgs(link));
    }

    // ---- Layout and rendering ----

    private void UpdatePageSizes()
    {
        PdfDocument? document = Source;
        if (document is null || _pageViews.Count == 0)
        {
            return;
        }

        double zoom = EffectiveZoom;
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            return;
        }

        if (_pageOffsets.Length != _pageViews.Count)
        {
            _pageOffsets = new double[_pageViews.Count];
        }

        double offset = 0;
        double heightSum = 0;

        for (int i = 0; i < _pageViews.Count; i++)
        {
            PdfPage page = document.Pages[i];
            Size size = ZoomMath.PageSize(page, Rotation);
            double height = Math.Max(1, size.Height * zoom);

            _pageOffsets[i] = offset;
            offset += height + PageSpacing;
            heightSum += height;

            var view = _pageViews[i];
            view.Width = size.Width * zoom;
            view.Height = size.Height * zoom;
        }

        _averagePageHeight = heightSum / _pageViews.Count;
    }

    private void UpdateOverlaysAndVisibility()
    {
        PdfDocument? document = Source;
        PageRenderPipeline? pipeline = _pipeline;
        if (document is null || pipeline is null || _pageViews.Count == 0)
        {
            return;
        }

        double zoom = EffectiveZoom;
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            return;
        }

        double scale = Quantize(zoom);

        // The bitmap must be rendered at device-pixel resolution: on a HiDPI display the visual's
        // RenderScaling (>1) would otherwise make the compositor upscale a DIP-sized bitmap and blur it.
        // Layout and overlay geometry stay in DIP (logical) space; only the render target is scaled up.
        double deviceScale = DeviceScaleFactor;
        _lastDeviceScale = deviceScale;
        double renderScale = Quantize(zoom * deviceScale);

        // Overlay geometry only depends on the scale and the rotation, so it is rebuilt when
        // those change - never on each layout pass (re-adding children would dirty the layout
        // again and Avalonia would flag the resulting pass loop).
        if (scale != _overlayScale || Rotation != _overlayRotation)
        {
            _overlayScale = scale;
            _overlayRotation = Rotation;
            InvalidateOverlays();
        }

        if (_selectionDirty)
        {
            UpdateSelection();
        }

        bool singlePage = PageLayout == PageLayout.SinglePage;
        for (int i = 0; i < _pageViews.Count; i++)
        {
            _pageViews[i].IsVisible = !singlePage || i == CurrentPage;
        }

        (int first, int last) = HotPageRange();
        if (last < first)
        {
            return;
        }

        int keepFirst = Math.Max(0, first - BitmapKeepMarginPages);
        int keepLast = Math.Min(_pageViews.Count - 1, last + BitmapKeepMarginPages);

        // Releases the bitmaps of pages far away from the viewport; the render cache hands them
        // back through TryGet when the user scrolls to them again.
        for (int i = 0; i < keepFirst; i++)
        {
            _pageViews[i].SetBitmap(null);
        }

        for (int i = keepLast + 1; i < _pageViews.Count; i++)
        {
            _pageViews[i].SetBitmap(null);
        }

        pipeline.CancelOutside(keepFirst, keepLast);

        for (int i = first; i <= last; i++)
        {
            PdfPageView view = _pageViews[i];

            if (view.OverlayRevision != _overlayRevision)
            {
                ApplyOverlay(view, document.Pages[i], i, zoom);
                view.OverlayRevision = _overlayRevision;
            }

            WriteableBitmap? cached = pipeline.TryGet(i, renderScale, Rotation);
            if (cached is not null)
            {
                view.SetBitmap(cached);
            }

            pipeline.EnsureRendered(i, renderScale, Rotation);
        }
    }

    /// <summary>Builds the highlights and link hotspots of a single page.</summary>
    private void ApplyOverlay(PdfPageView view, PdfPage page, int pageIndex, double zoom)
    {
        if (_hitsByPage is null)
        {
            var grouped = new Dictionary<int, List<(RectF Rect, bool Active)>>();
            for (int i = 0; i < _searchHits.Count; i++)
            {
                PdfSearchHit hit = _searchHits[i];
                if (!grouped.TryGetValue(hit.PageIndex, out List<(RectF, bool)>? list))
                {
                    list = new List<(RectF, bool)>();
                    grouped[hit.PageIndex] = list;
                }

                list.Add((hit.Bounds, i == _activeHit));
            }

            _hitsByPage = grouped;
        }

        // Page user space is y-up and unrotated; the matrix folds in the crop box, /UserUnit, the
        // page's own /Rotate and the view rotation, the same way the renderer's base matrix does.
        Matrix3x2 pageToView = ZoomMath.PageToViewMatrix(page, Rotation, zoom);

        var highlights = new List<Rect>();
        int activeIndex = -1;
        if (_hitsByPage.TryGetValue(pageIndex, out List<(RectF Rect, bool Active)>? hits))
        {
            for (int i = 0; i < hits.Count; i++)
            {
                highlights.Add(ZoomMath.ToViewRect(hits[i].Rect, pageToView));
                if (hits[i].Active && activeIndex < 0)
                {
                    activeIndex = highlights.Count - 1;
                }
            }
        }

        var links = new List<(Rect, PdfLink)>();
        foreach (PdfLink link in page.Links)
        {
            links.Add((ZoomMath.ToViewRect(link.Rect, pageToView), link));
        }

        view.SetOverlay(highlights, activeIndex, links);
    }

    /// <summary>Page indexes close enough to the viewport to be laid out, overlaid and rendered.</summary>
    private (int First, int Last) HotPageRange()
    {
        int count = _pageViews.Count;

        if (PageLayout == PageLayout.SinglePage)
        {
            int index = Math.Clamp(CurrentPage, 0, count - 1);
            return (index, index);
        }

        if (_pageOffsets.Length != count)
        {
            // Page sizes are not known yet: render the top of the document
            return (0, Math.Min(count, RenderMarginPages * 2 + 1) - 1);
        }

        double stride = _averagePageHeight + PageSpacing;
        double margin = RenderMarginPages * stride;
        double top = Math.Max(0, _scrollViewer.Offset.Y - margin);
        double bottom = _scrollViewer.Offset.Y + Math.Max(_scrollViewer.Viewport.Height, stride) + margin;

        int first = Math.Clamp(IndexOfOffset(top) - 1, 0, count - 1);
        int last = Math.Clamp(IndexOfOffset(bottom) - 1, first, count - 1);
        return (first, last);
    }

    /// <summary>Index of the first page whose top edge is at or below the given content offset.</summary>
    private int IndexOfOffset(double offset)
    {
        int low = 0;
        int high = _pageOffsets.Length - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_pageOffsets[mid] < offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// The host window's device pixels per DIP (<see cref="TopLevel.RenderScaling"/>), or 1 when the
    /// control is not yet attached to a top-level. Rendering multiplies the zoom by this so bitmaps are
    /// produced at native device resolution and stay crisp on HiDPI displays.
    /// </summary>
    private double DeviceScaleFactor => _topLevel?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _topLevel = topLevel;
            topLevel.PropertyChanged += OnTopLevelPropertyChanged;
            // Apply the current scaling (the initial layout pass may have run before attachment).
            UpdateOverlaysAndVisibility();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
        {
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
            _topLevel = null;
        }
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // RenderScaling has no public AvaloniaProperty token to compare against, so detect the change
        // by value: when the device scale differs from what the last render used, re-render at the new
        // resolution (the pipeline keys on scale, so fresh high-res bitmaps are scheduled).
        if (Math.Abs(DeviceScaleFactor - _lastDeviceScale) > 1e-9)
        {
            UpdateOverlaysAndVisibility();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (Source is null || _pageViews.Count == 0)
        {
            return;
        }

        if (ZoomMode != ZoomMode.Custom)
        {
            double newZoom = EffectiveZoom;
            if (Math.Abs(newZoom - _lastFitZoom) > 0.0001)
            {
                _lastFitZoom = newZoom;
                UpdatePageSizes();
            }
        }

        UpdateOverlaysAndVisibility();

        if (PageLayout == PageLayout.Continuous)
        {
            UpdateCurrentPageFromScroll();
        }
    }

    private double _lastFitZoom;

    private void UpdateCurrentPageFromScroll()
    {
        if (_pageViews.Count == 0)
        {
            return;
        }

        double viewportCenter = _scrollViewer.Offset.Y + (_scrollViewer.Viewport.Height / 2);
        int best = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < _pageViews.Count; i++)
        {
            PdfPageView view = _pageViews[i];
            if (!view.IsVisible)
            {
                continue;
            }

            double pageCenter = view.Bounds.Y + (view.Bounds.Height / 2);
            double distance = Math.Abs(pageCenter - viewportCenter);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        if (best != CurrentPage)
        {
            SetCurrentPage(best);
        }
    }

    private void SetCurrentPage(int pageIndex)
    {
        if (_suppressPageChanged || pageIndex == CurrentPage)
        {
            return;
        }

        _suppressPageChanged = true;
        try
        {
            CurrentPage = pageIndex;
            PageChanged?.Invoke(this, new PageChangedEventArgs(pageIndex));

            if (PageLayout == PageLayout.SinglePage)
            {
                ShowSinglePage(pageIndex);
                UpdateOverlaysAndVisibility();
            }
        }
        finally
        {
            _suppressPageChanged = false;
        }
    }

    private void ShowSinglePage(int pageIndex)
    {
        for (int i = 0; i < _pageViews.Count; i++)
        {
            _pageViews[i].IsVisible = i == pageIndex;
        }

        ScrollPageIntoView(pageIndex, center: false);
    }

    private void ScrollPageIntoView(int pageIndex, bool center)
    {
        if (pageIndex < 0 || pageIndex >= _pageViews.Count)
        {
            return;
        }

        PdfPageView view = _pageViews[pageIndex];
        double offset = center
            ? view.Bounds.Y - ((_scrollViewer.Viewport.Height - view.Bounds.Height) / 2)
            : view.Bounds.Y;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, Math.Max(0, offset));
    }

    /// <summary>Shows the hit at the given index (zero-based); returns false when out of range.</summary>
    public bool GoToHit(int index)
    {
        if (_searchHits.Count == 0 || index < 0 || index >= _searchHits.Count)
        {
            return false;
        }

        _activeHit = index;
        PdfSearchHit hit = _searchHits[index];

        if (PageLayout == PageLayout.SinglePage)
        {
            ShowSinglePage(hit.PageIndex);
        }
        else
        {
            ScrollPageIntoView(hit.PageIndex, center: true);
        }

        SetCurrentPage(hit.PageIndex);
        InvalidateOverlays();
        UpdateOverlaysAndVisibility();
        return true;
    }

    private void RefreshOverlays() => UpdateOverlaysAndVisibility();

    // ---- Input ----

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Source is null || _pageViews.Count == 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double oldZoom = EffectiveZoom;
            double newZoom = ZoomMath.ClampZoom(oldZoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));

            if (ZoomMode != ZoomMode.Custom)
            {
                ZoomMode = ZoomMode.Custom;
            }

            Point anchor = e.GetPosition(_scrollViewer);
            Zoom = newZoom;

            Vector offset = ZoomMath.AnchorOffset(_scrollViewer.Offset, oldZoom, newZoom, anchor);
            _scrollViewer.Offset = offset;
            e.Handled = true;
        }
        else if (PageLayout == PageLayout.SinglePage && _scrollViewer.Extent.Height <= _scrollViewer.Viewport.Height + 1)
        {
            // Single-page mode has nothing to scroll while the page fits the viewport, so the wheel turns
            // the page (scroll down = next). When zoomed past the viewport the ScrollViewer scrolls instead.
            GoToPage(CurrentPage + (e.Delta.Y < 0 ? 1 : -1));
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Source is null || _pageViews.Count == 0)
        {
            return;
        }

        PointerPoint pointer = e.GetCurrentPoint(this);

        // Panning follows the reader convention now that the left button selects: middle-drag, or
        // space held down while dragging with the left button.
        if (pointer.Properties.IsMiddleButtonPressed ||
            (pointer.Properties.IsLeftButtonPressed && _spaceHeld))
        {
            _panning = true;
            _panOrigin = e.GetPosition(_scrollViewer);
            _panStartOffset = _scrollViewer.Offset;
            e.Pointer.Capture(_scrollViewer);
            e.Handled = true;
            return;
        }

        if (!pointer.Properties.IsLeftButtonPressed || GraphicsFactory is null)
        {
            return;
        }

        // Keyboard copy and the space-pan gesture need focus on this control
        Focus();

        Point panelPoint = e.GetPosition(_pagesPanel);
        if (!TryGetPageAt(panelPoint, out int pageIndex))
        {
            ClearSelection();
            return;
        }

        PdfTextPage? text = GetTextPage(pageIndex);
        if (text is null)
        {
            return;
        }

        int caret = CaretAt(pageIndex, e.GetPosition(_pageViews[pageIndex]));
        (int Page, int Caret) position = (pageIndex, caret);

        bool extend = _hasAnchor;
        _clickCount = IsSameClickSpot(panelPoint) ? Math.Min(_clickCount + 1, 3) : 1;
        _lastClickTime = Environment.TickCount64;
        _lastClickPosition = panelPoint;
        _hasAnchor = true;

        if (_clickCount == 2)
        {
            (int start, int end) = text.GetWordRange(caret);
            _selectionAnchor = (pageIndex, start);
            _selectionFocus = (pageIndex, end);
        }
        else if (_clickCount >= 3)
        {
            (int start, int end) = text.GetLineRange(caret);
            _selectionAnchor = (pageIndex, start);
            _selectionFocus = (pageIndex, end);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && extend)
        {
            _selectionFocus = position;
        }
        else
        {
            _selectionAnchor = position;
            _selectionFocus = position;
        }

        _selecting = true;
        _dragPoint = e.GetPosition(_scrollViewer);
        e.Pointer.Capture(this);
        UpdateSelection();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_selecting)
        {
            _dragPoint = e.GetPosition(_scrollViewer);
            UpdateSelectionFocus(e.GetPosition(_pagesPanel));
            UpdateDragScroll(_dragPoint);
            e.Handled = true;
            return;
        }

        if (_panning)
        {
            Point current = e.GetPosition(_scrollViewer);
            Vector delta = current - _panOrigin;
            _scrollViewer.Offset = new Vector(
                Math.Max(0, _panStartOffset.X - delta.X),
                Math.Max(0, _panStartOffset.Y - delta.Y));
            e.Handled = true;
            return;
        }

        UpdateHover(e.GetPosition(_pagesPanel));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            _dragScrollTimer.Stop();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Only react when this control lost the capture; child elements (the ScrollViewer) losing it
        // as the selection capture takes over must not end the drag.
        if (!ReferenceEquals(e.Source, this))
        {
            return;
        }

        _selecting = false;
        _panning = false;
        _dragScrollTimer.Stop();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = true;
            e.Handled = true;
            return;
        }

        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.C && _selectedText.Length > 0)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selectedText.Length > 0)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
            e.Handled = true;
        }
    }

    // ---- Text selection ----

    /// <summary>The nearest caret position to a point in pages-panel coordinates.</summary>
    private (int Page, int Caret) CaretAt(Point panelPoint)
    {
        int page = NearestPage(panelPoint);
        if (page < 0)
        {
            return _selectionFocus;
        }

        Rect bounds = _pageViews[page].Bounds;
        var local = new Point(panelPoint.X - bounds.X, panelPoint.Y - bounds.Y);
        return (page, CaretAt(page, local));
    }

    /// <summary>The nearest caret position to a point in a page view's own coordinates.</summary>
    private int CaretAt(int pageIndex, Point viewPoint)
    {
        PdfTextPage? text = GetTextPage(pageIndex);
        if (text is null)
        {
            return 0;
        }

        return text.GetCaretIndex(PagePoint(pageIndex, viewPoint));
    }

    /// <summary>Maps a point in a page view's own coordinates to that page's user space.</summary>
    private Vector2 PagePoint(int pageIndex, Point viewPoint)
    {
        Matrix3x2 pageToView = ZoomMath.PageToViewMatrix(Source!.Pages[pageIndex], Rotation, EffectiveZoom);
        return ZoomMath.ToPagePoint(viewPoint, pageToView);
    }

    /// <summary>
    /// Tracks the pointer over the pages: the text cursor is shown only where the page actually has
    /// text, and a page that has not been read yet is read off the UI thread once the pointer dwells.
    /// </summary>
    private void UpdateHover(Point panelPoint)
    {
        _hoverPoint = panelPoint;

        int page = TryGetPageAt(panelPoint, out int index) ? index : -1;
        if (page != _hoverPage)
        {
            _hoverPage = page;
            RestartHoverPrefetch(page);
        }

        UpdateTextCursor();
    }

    private void RestartHoverPrefetch(int pageIndex)
    {
        _hoverTextTimer.Stop();

        // Extraction takes tens of milliseconds on a text-heavy page (measured: ~5ms on the PDF
        // specification, ~50ms on a typeset book), so it never runs from the pointer handler itself.
        if (pageIndex >= 0 &&
            Source?.Pages[pageIndex].PeekText() is null &&
            !_textPrefetched.Contains(pageIndex))
        {
            _hoverTextTimer.Start();
        }
    }

    private void OnHoverTextTick(object? sender, EventArgs e)
    {
        _hoverTextTimer.Stop();

        int pageIndex = _hoverPage;
        PdfDocument? document = Source;
        IGraphicsFactory? factory = GraphicsFactory;
        if (document is null || factory is null ||
            pageIndex < 0 || pageIndex >= document.PageCount ||
            document.Pages[pageIndex].PeekText() is not null ||
            !_textPrefetched.Add(pageIndex))
        {
            return;
        }

        PdfPage page = document.Pages[pageIndex];
        _ = Task.Run(() =>
        {
            try
            {
                page.GetText(factory);
            }
            catch
            {
                // A page whose text cannot be read simply keeps the default cursor
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(Source, document))
                {
                    UpdateTextCursor();
                }
            });
        });
    }

    private void UpdateTextCursor()
    {
        PdfDocument? document = Source;
        int page = -1;

        if (document is not null && GraphicsFactory is not null &&
            TryGetPageAt(_hoverPoint, out int index))
        {
            Rect bounds = _pageViews[index].Bounds;
            var local = new Point(_hoverPoint.X - bounds.X, _hoverPoint.Y - bounds.Y);
            PdfTextPage? text = document.Pages[index].PeekText();
            if (text is not null && text.IsOverText(PagePoint(index, local)))
            {
                page = index;
            }
        }

        if (page == _textCursorPage)
        {
            return;
        }

        if (_textCursorPage >= 0 && _textCursorPage < _pageViews.Count)
        {
            _pageViews[_textCursorPage].SetTextCursor(false);
        }

        if (page >= 0)
        {
            _pageViews[page].SetTextCursor(true);
        }

        _textCursorPage = page;
    }

    private void UpdateSelectionFocus(Point panelPoint)
    {
        (int Page, int Caret) caret = CaretAt(panelPoint);
        if (caret == _selectionFocus)
        {
            return;
        }

        _selectionFocus = caret;
        UpdateSelection();
    }

    /// <summary>
    /// Recomputes the selected text, the per-page highlight boxes and raises
    /// <see cref="SelectionChanged"/> when the text changed.
    /// </summary>
    private void UpdateSelection()
    {
        _selectionDirty = false;

        if (Source is null || _pageViews.Count == 0)
        {
            return;
        }

        (int Page, int Caret) anchor = _selectionAnchor;
        (int Page, int Caret) focus = _selectionFocus;
        if (anchor.Page > focus.Page || (anchor.Page == focus.Page && anchor.Caret > focus.Caret))
        {
            (anchor, focus) = (focus, anchor);
        }

        var ranges = new List<(int Page, int Start, int End)>();
        if (anchor.Page == focus.Page)
        {
            if (focus.Caret > anchor.Caret)
            {
                ranges.Add((anchor.Page, anchor.Caret, focus.Caret));
            }
        }
        else
        {
            int anchorLength = GetTextPage(anchor.Page)?.Length ?? 0;
            if (anchor.Caret < anchorLength)
            {
                ranges.Add((anchor.Page, anchor.Caret, anchorLength));
            }

            for (int page = anchor.Page + 1; page < focus.Page; page++)
            {
                int length = GetTextPage(page)?.Length ?? 0;
                if (length > 0)
                {
                    ranges.Add((page, 0, length));
                }
            }

            if (focus.Caret > 0)
            {
                ranges.Add((focus.Page, 0, focus.Caret));
            }
        }

        var text = new StringBuilder();
        var placed = new List<(int Page, List<Rect> Rects)>();
        double zoom = EffectiveZoom;
        bool canPlace = double.IsFinite(zoom) && zoom > 0;

        foreach ((int page, int start, int end) in ranges)
        {
            PdfTextPage? textPage = GetTextPage(page);
            if (textPage is null)
            {
                continue;
            }

            // Cross-page selections read as lines in a single copy
            if (text.Length > 0 && text[^1] != '\n')
            {
                text.Append('\n');
            }

            text.Append(textPage.GetText(start, end - start));

            if (!canPlace)
            {
                continue;
            }

            PdfTextSelection selection = textPage.GetSelection(start, end);
            if (selection.Bounds.Count == 0)
            {
                continue;
            }

            Matrix3x2 pageToView = ZoomMath.PageToViewMatrix(Source.Pages[page], Rotation, zoom);
            var rects = new List<Rect>(selection.Bounds.Count);
            foreach (RectF bounds in selection.Bounds)
            {
                rects.Add(ZoomMath.ToViewRect(bounds, pageToView));
            }

            placed.Add((page, rects));
        }

        var selectedPages = new HashSet<int>();
        foreach ((int page, List<Rect> rects) in placed)
        {
            if (page >= 0 && page < _pageViews.Count)
            {
                _pageViews[page].SetSelection(rects);
                selectedPages.Add(page);
            }
        }

        foreach (int page in _selectionPages)
        {
            if (!selectedPages.Contains(page) && page >= 0 && page < _pageViews.Count)
            {
                _pageViews[page].SetSelection(null);
            }
        }

        _selectionPages.Clear();
        _selectionPages.UnionWith(selectedPages);

        string selected = text.ToString();
        if (selected != _selectedText)
        {
            _selectedText = selected;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryGetPageAt(Point panelPoint, out int pageIndex)
    {
        for (int i = 0; i < _pageViews.Count; i++)
        {
            PdfPageView view = _pageViews[i];
            if (view.IsVisible && view.Bounds.Contains(panelPoint))
            {
                pageIndex = i;
                return true;
            }
        }

        pageIndex = -1;
        return false;
    }

    private int NearestPage(Point panelPoint)
    {
        int best = -1;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < _pageViews.Count; i++)
        {
            PdfPageView view = _pageViews[i];
            if (!view.IsVisible)
            {
                continue;
            }

            Rect bounds = view.Bounds;
            double dx = Math.Max(Math.Max(bounds.X - panelPoint.X, 0), panelPoint.X - bounds.Right);
            double dy = Math.Max(Math.Max(bounds.Y - panelPoint.Y, 0), panelPoint.Y - bounds.Bottom);
            double distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private PdfTextPage? GetTextPage(int pageIndex)
    {
        if (Source is null || GraphicsFactory is null || pageIndex < 0 || pageIndex >= Source.PageCount)
        {
            return null;
        }

        return Source.Pages[pageIndex].GetText(GraphicsFactory);
    }

    private bool IsSameClickSpot(Point point) =>
        _clickCount > 0 &&
        Environment.TickCount64 - _lastClickTime <= 500 &&
        Math.Abs(point.X - _lastClickPosition.X) <= 4 &&
        Math.Abs(point.Y - _lastClickPosition.Y) <= 4;

    private void UpdateDragScroll(Vector pointer)
    {
        double height = _scrollViewer.Viewport.Height;
        bool outside = height > 0 && (pointer.Y < 0 || pointer.Y > height);
        if (outside)
        {
            if (!_dragScrollTimer.IsEnabled)
            {
                _dragScrollTimer.Start();
            }
        }
        else
        {
            _dragScrollTimer.Stop();
        }
    }

    private void OnDragScrollTick(object? sender, EventArgs e)
    {
        if (!_selecting)
        {
            _dragScrollTimer.Stop();
            return;
        }

        double height = _scrollViewer.Viewport.Height;
        if (height <= 0)
        {
            return;
        }

        double overflow = _dragPoint.Y < 0 ? _dragPoint.Y : (_dragPoint.Y > height ? _dragPoint.Y - height : 0);
        if (overflow == 0)
        {
            _dragScrollTimer.Stop();
            return;
        }

        double max = Math.Max(0, _scrollViewer.Extent.Height - height);
        double step = Math.Clamp(Math.Abs(overflow) * 0.25, 8, 48) * Math.Sign(overflow);
        double offset = Math.Clamp(_scrollViewer.Offset.Y + step, 0, max);
        if (Math.Abs(offset - _scrollViewer.Offset.Y) < 0.5)
        {
            return;
        }

        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, offset);

        // The pointer holds still while pages move under it: re-map it to page coordinates so the
        // focus caret walks into the page scrolling into view.
        Point? panelOrigin = _pagesPanel.TranslatePoint(new Point(0, 0), _scrollViewer);
        if (panelOrigin is { } origin)
        {
            UpdateSelectionFocus(new Point(_dragPoint.X - origin.X, _dragPoint.Y - origin.Y));
        }
    }

    // ---- Helpers ----

    private static double Quantize(double zoom) => Math.Round(ZoomMath.ClampZoom(zoom), 2);

    private SolidColorBrush CreatePageBackgroundBrush()
    {
        PosoColor c = PageBackground;
        return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private void ApplyPageBackground()
    {
        SolidColorBrush brush = CreatePageBackgroundBrush();
        foreach (PdfPageView view in _pageViews)
        {
            view.Background = brush;
        }
    }

}

/// <summary>A search hit: page index plus the hit rectangle in page user space (y-up).</summary>
public readonly record struct PdfSearchHit(int PageIndex, RectF Bounds);
