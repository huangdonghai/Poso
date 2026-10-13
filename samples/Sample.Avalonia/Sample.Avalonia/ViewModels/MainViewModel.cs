using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using WriteableBitmap = global::Avalonia.Media.Imaging.WriteableBitmap;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poso.Avalonia.Rendering;
using Poso.Graphics;
using Poso.Pdf;

namespace Sample.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IGraphicsFactory _graphicsFactory;
    private CancellationTokenSource? _thumbnailCancellation;

    public MainViewModel(IGraphicsFactory graphicsFactory)
    {
        _graphicsFactory = graphicsFactory;
    }

    /// <summary>The graphics backend injected by the application (e.g. the Skia backend).</summary>
    public IGraphicsFactory GraphicsFactory => _graphicsFactory;

    [ObservableProperty]
    private PdfDocument? _document;

    [ObservableProperty]
    private string _title = "Poso PDF Viewer";

    [ObservableProperty]
    private int _pageCount;

    /// <summary>One-based current page for display.</summary>
    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private ZoomMode _zoomMode = ZoomMode.FitPage;

    [ObservableProperty]
    private int _rotation;

    [ObservableProperty]
    private PageLayout _pageLayout = PageLayout.Continuous;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _searchHitCount;

    /// <summary>Zero-based index of the active hit, or -1.</summary>
    [ObservableProperty]
    private int _activeHit = -1;

    [ObservableProperty]
    private string _statusText = "Open a PDF to start, or drop a file here.";

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private int _selectedSidebarTab;

    [ObservableProperty]
    private OutlineNode? _selectedOutlineNode;

    [ObservableProperty]
    private ThumbnailItem? _selectedThumbnail;

    public ObservableCollection<OutlineNode> Outline { get; } = new();

    public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();

    public ZoomMode[] ZoomModes { get; } = Enum.GetValues<ZoomMode>();

    public PageLayout[] PageLayouts { get; } = Enum.GetValues<PageLayout>();

    public bool HasDocument => Document is not null;

    /// <summary>Sidebar tab selection: 0 = bookmarks, 1 = thumbnails.</summary>
    public bool IsBookmarkTabSelected => SelectedSidebarTab == 0;

    public bool IsThumbnailTabSelected => SelectedSidebarTab == 1;

    /// <summary>"active / total" summary for the search toolbar.</summary>
    public string SearchStatus => SearchHitCount == 0
        ? "No hits"
        : $"{ActiveHit + 1} / {SearchHitCount}";

    partial void OnSelectedSidebarTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsBookmarkTabSelected));
        OnPropertyChanged(nameof(IsThumbnailTabSelected));
    }

    partial void OnSearchHitCountChanged(int value) => OnPropertyChanged(nameof(SearchStatus));

    partial void OnActiveHitChanged(int value) => OnPropertyChanged(nameof(SearchStatus));

    partial void OnSelectedThumbnailChanged(ThumbnailItem? value)
    {
        if (value is not null)
        {
            GoToPageRequested?.Invoke(this, value.PageNumber - 1);
        }
    }

    /// <summary>Raised when the user asks to run a search (the view executes it).</summary>
    public event EventHandler? FindRequested;

    /// <summary>Raised for hit navigation: +1 = next hit, -1 = previous hit.</summary>
    public event EventHandler<int>? HitNavigationRequested;

    [RelayCommand]
    private void ShowBookmarks() => SelectedSidebarTab = 0;

    [RelayCommand]
    private void ShowThumbnails() => SelectedSidebarTab = 1;

    [RelayCommand]
    private void Find() => FindRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void PreviousHit() => HitNavigationRequested?.Invoke(this, -1);

    [RelayCommand]
    private void NextHit() => HitNavigationRequested?.Invoke(this, 1);

    /// <summary>Raised when the view should jump to a destination (bookmark click).</summary>
    public event EventHandler<int>? GoToPageRequested;

    partial void OnDocumentChanged(PdfDocument? value)
    {
        OnPropertyChanged(nameof(HasDocument));
    }

    /// <summary>Opens a document from disk and rebuilds outline and thumbnails.</summary>
    public void LoadDocument(string path)
    {
        using FileStream stream = File.OpenRead(path);
        ApplyDocument(PdfDocument.Open(stream), System.IO.Path.GetFileName(path), $"Loaded {PageCount} page(s) from {path}");
    }

    /// <summary>Opens a document from a stream (used for the embedded sample asset).</summary>
    public void LoadDocument(Stream stream, string displayName)
    {
        ApplyDocument(PdfDocument.Open(stream), displayName, $"Loaded the sample document ({displayName})");
    }

    private void ApplyDocument(PdfDocument document, string displayName, string status)
    {
        CloseDocument();

        Document = document;
        Title = $"{displayName} - Poso PDF Viewer";
        PageCount = document.PageCount;
        CurrentPage = 1;
        ActiveHit = -1;
        SearchHitCount = 0;
        SearchQuery = string.Empty;
        StatusText = status;

        BuildOutline(document);
        _ = BuildThumbnailsAsync(document);
    }

    public void CloseDocument()
    {
        _thumbnailCancellation?.Cancel();
        Document?.Dispose();
        Document = null;
        Outline.Clear();
        Thumbnails.Clear();
        PageCount = 0;
        CurrentPage = 1;
        SearchHitCount = 0;
        ActiveHit = -1;
        Title = "Poso PDF Viewer";
    }

    private void BuildOutline(PdfDocument document)
    {
        Outline.Clear();
        PdfOutline? root = document.Outline;
        if (root is null)
        {
            return;
        }

        foreach (PdfOutline item in root.Children)
        {
            Outline.Add(new OutlineNode(item));
        }
    }

    private async Task BuildThumbnailsAsync(PdfDocument document)
    {
        var cancellation = new CancellationTokenSource();
        _thumbnailCancellation = cancellation;

        try
        {
            for (int i = 0; i < document.PageCount; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                int pageIndex = i;
                WriteableBitmap? bitmap =
                    await Task.Run(() => RenderThumbnail(document, pageIndex), cancellation.Token);

                if (bitmap is null)
                {
                    continue;
                }

                Thumbnails.Add(new ThumbnailItem(pageIndex + 1, bitmap));
            }
        }
        catch (OperationCanceledException)
        {
            // Document changed while rendering; results are discarded
        }
        finally
        {
            _thumbnailCancellation = null;
        }
    }

    private WriteableBitmap? RenderThumbnail(PdfDocument document, int pageIndex)
    {
        PdfPage page = document.Pages[pageIndex];
        double pageHeight = page.CropBox.Height;
        double pageWidth = page.CropBox.Width;
        if (pageHeight <= 0 || pageWidth <= 0)
        {
            return null;
        }

        double scale = 120d / pageHeight;
        int width = Math.Max(1, (int)Math.Ceiling(pageWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(pageHeight * scale));
        if (!double.IsFinite(scale) || width > 4096 || height > 4096)
        {
            return null;
        }

        try
        {
            using IGraphicsContext context = _graphicsFactory.CreateContext(width, height);
            page.Render(_graphicsFactory, context, new PdfRenderOptions((float)scale));
            using IImage image = context.Snapshot();
            return Poso.Avalonia.Imaging.BitmapConverter.ToWriteableBitmap(image);
        }
        catch
        {
            // A single broken page must not abort thumbnail generation
            return null;
        }
    }

    [RelayCommand]
    private void OpenSidebar() => IsSidebarVisible = !IsSidebarVisible;

    /// <summary>Raised when the user asks to open a file (the view shows the picker).</summary>
    public event EventHandler? OpenFileRequested;

    [RelayCommand]
    private void OpenFile() => OpenFileRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ZoomIn() => SetCustomZoom(Zoom * 1.2);

    [RelayCommand]
    private void ZoomOut() => SetCustomZoom(Zoom / 1.2);

    [RelayCommand]
    private void FitPage()
    {
        ZoomMode = ZoomMode.FitPage;
    }

    [RelayCommand]
    private void FitWidth()
    {
        ZoomMode = ZoomMode.FitWidth;
    }

    [RelayCommand]
    private void RotateClockwise() => Rotation = ZoomMath.Normalize(Rotation + 90);

    [RelayCommand]
    private void RotateCounterClockwise() => Rotation = ZoomMath.Normalize(Rotation - 90);

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            GoToPageRequested?.Invoke(this, CurrentPage - 2);
        }
    }

    [RelayCommand]
    private void NextPage()
    {
        if (Document is not null && CurrentPage < PageCount)
        {
            GoToPageRequested?.Invoke(this, CurrentPage);
        }
    }

    [RelayCommand]
    private void SelectPage(int pageNumber)
    {
        if (pageNumber >= 1 && pageNumber <= PageCount)
        {
            GoToPageRequested?.Invoke(this, pageNumber - 1);
        }
    }

    partial void OnSelectedOutlineNodeChanged(OutlineNode? value)
    {
        if (value?.Destination is { } destination && destination.PageIndex >= 0)
        {
            GoToPageRequested?.Invoke(this, destination.PageIndex);
        }
    }

    private void SetCustomZoom(double zoom)
    {
        ZoomMode = ZoomMode.Custom;
        Zoom = ZoomMath.ClampZoom(zoom);
    }
}

/// <summary>A rendered page thumbnail.</summary>
public partial class ThumbnailItem : ViewModelBase
{
    [ObservableProperty]
    private bool _isSelected;

    public ThumbnailItem(int pageNumber, WriteableBitmap bitmap)
    {
        PageNumber = pageNumber;
        Bitmap = bitmap;
    }

    public int PageNumber { get; }

    public WriteableBitmap Bitmap { get; }
}
