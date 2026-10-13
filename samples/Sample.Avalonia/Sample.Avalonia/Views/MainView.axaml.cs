using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Poso.Avalonia.Controls;
using Poso.Pdf;
using Sample.Avalonia.ViewModels;

namespace Sample.Avalonia.Views;

public partial class MainView : UserControl
{
    private bool _sampleLoaded;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Wire the new view model (DataContext is set before the control is attached)
        if (ViewModel is { } current)
        {
            current.GoToPageRequested += OnGoToPageRequested;
            current.FindRequested += OnFindRequested;
            current.HitNavigationRequested += OnHitNavigationRequested;
            current.OpenFileRequested += OnOpenFileRequested;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        Viewer.PageChanged += OnPageChanged;
        Viewer.LinkActivated += OnLinkActivated;

        if (!_sampleLoaded)
        {
            _sampleLoaded = true;

            // A command-line file wins over the bundled sample document
            if (App.StartupDocumentPath is { } startupPath && ViewModel is { } viewModel)
            {
                OpenDocument(viewModel, startupPath);
            }
            else
            {
                LoadSampleDocument();
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        Viewer.PageChanged -= OnPageChanged;
        Viewer.LinkActivated -= OnLinkActivated;
    }

    /// <summary>Loads the bundled sample document on first show.</summary>
    private void LoadSampleDocument()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://Sample.Avalonia/Assets/sample.pdf"));
            viewModel.LoadDocument(stream, "sample.pdf");
        }
        catch (Exception exception)
        {
            viewModel.StatusText = $"Cannot load the sample document: {exception.Message}";
        }
    }

    private async void OnOpenFileRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF documents") { Patterns = new[] { "*.pdf" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });

        if (files.Count > 0 && files[0].Path.LocalPath is { } path)
        {
            OpenDocument(viewModel, path);
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string? path = FirstDroppedFile(e);
        if (path is not null)
        {
            OpenDocument(viewModel, path);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only accept file drops so the copy cursor shows
        e.DragEffects = FirstDroppedFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string? FirstDroppedFile(DragEventArgs e)
    {
        IStorageItem[] files = e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>();
        string? path = files.OfType<IStorageFile>().FirstOrDefault()?.Path.LocalPath;

        return path is not null && File.Exists(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    /// <summary>Opens a document; shows a message in the status bar when the file cannot be parsed.</summary>
    public void OpenDocument(MainViewModel viewModel, string path)
    {
        try
        {
            viewModel.LoadDocument(path);
        }
        catch (Exception exception)
        {
            viewModel.StatusText = $"Cannot open {System.IO.Path.GetFileName(path)}: {exception.Message}";
        }
    }

    private void OnGoToPageRequested(object? sender, int pageIndex)
    {
        Viewer?.GoToPage(pageIndex);
    }

    private void OnPageChanged(object? sender, PageChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.CurrentPage = e.PageIndex + 1;

            foreach (ThumbnailItem thumbnail in viewModel.Thumbnails)
            {
                thumbnail.IsSelected = thumbnail.PageNumber == e.PageIndex + 1;
            }
        }
    }

    private void OnLinkActivated(object? sender, LinkActivatedEventArgs e)
    {
        if (e.Link.Kind == PdfActionKind.Uri && e.Link.Uri is { } uri)
        {
            // External links open in the default browser
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                if (ViewModel is { } viewModel)
                {
                    viewModel.StatusText = $"Cannot open link: {exception.Message}";
                }
            }
        }
        else if (ViewModel is { } viewModel)
        {
            viewModel.StatusText = $"Internal link to page {(e.Link.Destination?.PageIndex + 1)?.ToString() ?? "?"}";
        }
    }

    private void OnFindRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel || Viewer is not { } viewer)
        {
            return;
        }

        viewModel.SearchHitCount = viewer.Search(viewModel.SearchQuery);
        viewModel.ActiveHit = viewModel.SearchHitCount > 0 ? 0 : -1;
    }

    private void OnHitNavigationRequested(object? sender, int direction)
    {
        if (ViewModel is not { } viewModel || Viewer is not { } viewer || viewModel.SearchHitCount == 0)
        {
            return;
        }

        int current = viewModel.ActiveHit;
        int next = direction > 0 ? current + 1 : current - 1;
        if (next < 0)
        {
            next = viewModel.SearchHitCount - 1;
        }
        else if (next >= viewModel.SearchHitCount)
        {
            next = 0;
        }

        viewer.GoToHit(next);
        viewModel.ActiveHit = next;
    }
}
