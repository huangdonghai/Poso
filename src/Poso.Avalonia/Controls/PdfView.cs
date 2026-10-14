using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Poso.Avalonia.Imaging;
using Poso.Avalonia.Rendering;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf;

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
/// rotation, page navigation, text search highlighting and link activation.
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
    private PageRenderPipeline? _pipeline;
    private Point _panOrigin;
    private Vector _panStartOffset;
    private bool _panning;
    private bool _suppressPageChanged;
    private List<PdfSearchHit> _searchHits = new();
    private int _activeHit = -1;

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
        AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
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

    /// <summary>Searches the document and highlights the hits; returns the number of hits.</summary>
    public int Search(string query)
    {
        ClearSearch();

        if (Source is null || string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        IGraphicsFactory? factory = GraphicsFactory;
        if (factory is null)
        {
            return 0;
        }

        var hits = new List<PdfSearchHit>();
        for (int i = 0; i < PageCount; i++)
        {
            PdfTextPage text = Source.Pages[i].GetText(factory);
            foreach (TextHit hit in text.Search(query))
            {
                hits.Add(new PdfSearchHit(i, hit.Bounds));
            }
        }

        _searchHits = hits;
        InvalidateOverlays();
        RefreshOverlays();

        if (hits.Count > 0)
        {
            GoToHit(0);
        }

        return hits.Count;
    }

    /// <summary>Shows the next search hit.</summary>
    public bool NextHit() => GoToHit(_activeHit + 1);

    /// <summary>Shows the previous search hit.</summary>
    public bool PreviousHit() => GoToHit(_activeHit - 1);

    /// <summary>Clears search highlights.</summary>
    public void ClearSearch()
    {
        _searchHits = new List<PdfSearchHit>();
        _activeHit = -1;
        InvalidateOverlays();
        RefreshOverlays();
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
            UpdatePageSizes();
            UpdateOverlaysAndVisibility();
        }
        else if (change.Property == PageLayoutProperty)
        {
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
        _searchHits = new List<PdfSearchHit>();
        _activeHit = -1;
        _pageOffsets = Array.Empty<double>();
        _averagePageHeight = 0;
        _overlayScale = double.NaN;
        _overlayRotation = int.MinValue;
        _lastFitZoom = 0;
        InvalidateOverlays();

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

        Size pageSize = ZoomMath.PageSize(page, Rotation);

        // Highlights (page user space is y-up: flip into the device space of the page view)
        var highlights = new List<Rect>();
        int activeIndex = -1;
        if (_hitsByPage.TryGetValue(pageIndex, out List<(RectF Rect, bool Active)>? hits))
        {
            for (int i = 0; i < hits.Count; i++)
            {
                highlights.Add(ToDeviceRect(hits[i].Rect, pageSize, zoom));
                if (hits[i].Active && activeIndex < 0)
                {
                    activeIndex = highlights.Count - 1;
                }
            }
        }

        var links = new List<(Rect, PdfLink)>();
        foreach (PdfLink link in page.Links)
        {
            links.Add((ToDeviceRect(link.Rect, pageSize, zoom), link));
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
        if (Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _panning = true;
        _panOrigin = e.GetPosition(_scrollViewer);
        _panStartOffset = _scrollViewer.Offset;
        e.Pointer.Capture(_scrollViewer);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        Point current = e.GetPosition(_scrollViewer);
        Vector delta = current - _panOrigin;
        _scrollViewer.Offset = new Vector(
            Math.Max(0, _panStartOffset.X - delta.X),
            Math.Max(0, _panStartOffset.Y - delta.Y));
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
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

    /// <summary>
    /// Converts a page-space rectangle (y-up, origin at the crop box) into the device rectangle
    /// of a page view (y-down).
    /// </summary>
    private static Rect ToDeviceRect(RectF rect, Size pageSize, double zoom)
    {
        double left = rect.Left * zoom;
        double right = rect.Right * zoom;
        double top = (pageSize.Height - rect.Top) * zoom;
        double bottom = (pageSize.Height - rect.Bottom) * zoom;
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
}

/// <summary>A search hit: page index plus the hit rectangle in page user space (y-up).</summary>
public readonly record struct PdfSearchHit(int PageIndex, RectF Bounds);
