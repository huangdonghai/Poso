using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Poso.Pdf;

namespace Poso.Avalonia.Controls;

/// <summary>
/// A single rendered page: the page bitmap plus an overlay canvas for search highlights
/// and link hotspots. Created and managed by <see cref="PdfView"/>.
/// </summary>
internal sealed class PdfPageView : ContentControl
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xB3, 0x00));
    private static readonly IBrush ActiveHighlightBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x8C, 0x00));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xFF));
    private static readonly IBrush LinkBrush = Brushes.Transparent;
    private static readonly Cursor TextCursor = new Cursor(StandardCursorType.Ibeam);

    private readonly Image _image;
    private readonly Canvas _selection;
    private readonly Canvas _overlay;

    public PdfPageView()
    {
        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _selection = new Canvas { IsHitTestVisible = false };
        _overlay = new Canvas();

        var grid = new Grid();
        grid.Children.Add(_image);
        grid.Children.Add(_selection);
        grid.Children.Add(_overlay);
        Content = grid;

        // The page size is driven by Width/Height, so no margin is needed
        Margin = new Thickness(0);
    }

    /// <summary>
    /// Shows the text cursor over the page (when the pointer is over selectable text) or drops back
    /// to the inherited default cursor.
    /// </summary>
    public void SetTextCursor(bool overText) => Cursor = overText ? TextCursor : null;

    /// <summary>Zero-based page index.</summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Overlay revision this view was last filled for; <see cref="PdfView"/> bumps its own revision
    /// whenever zoom, rotation or search results change, so overlays are rebuilt at most once per
    /// change and never re-added on every layout pass.
    /// </summary>
    public int OverlayRevision { get; set; } = -1;

    public void SetBitmap(WriteableBitmap? bitmap)
    {
        // Assigning the same instance is a no-op: this keeps layout quiet while pages arrive
        if (!ReferenceEquals(_image.Source, bitmap))
        {
            _image.Source = bitmap;
        }
    }

    /// <summary>
    /// Replaces the text-selection highlight (device coordinates relative to the page). Null or an
    /// empty list removes it. Kept on its own layer so a drag does not rebuild the link hotspots.
    /// </summary>
    public void SetSelection(IReadOnlyList<Rect>? rects)
    {
        if (rects is null || rects.Count == 0)
        {
            if (_selection.Children.Count > 0)
            {
                _selection.Children.Clear();
            }

            return;
        }

        _selection.Children.Clear();
        foreach (Rect rect in rects)
        {
            var highlight = new Rectangle
            {
                Fill = SelectionBrush,
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
            };
            Canvas.SetLeft(highlight, rect.X);
            Canvas.SetTop(highlight, rect.Y);
            _selection.Children.Add(highlight);
        }
    }

    /// <summary>
    /// Replaces the overlay content (search highlights and link hotspots, both in device
    /// coordinates relative to the page).
    /// </summary>
    public void SetOverlay(
        IReadOnlyList<Rect> highlights,
        int activeHighlightIndex,
        IReadOnlyList<(Rect Rect, PdfLink Link)> links)
    {
        if (_overlay.Children.Count == 0 && highlights.Count == 0 && links.Count == 0)
        {
            return;
        }

        _overlay.Children.Clear();

        for (int i = 0; i < highlights.Count; i++)
        {
            var rect = new Rectangle
            {
                Fill = i == activeHighlightIndex ? ActiveHighlightBrush : HighlightBrush,
                Width = Math.Max(1, highlights[i].Width),
                Height = Math.Max(1, highlights[i].Height),
            };
            Canvas.SetLeft(rect, highlights[i].X);
            Canvas.SetTop(rect, highlights[i].Y);
            _overlay.Children.Add(rect);
        }

        foreach ((Rect rect, PdfLink link) in links)
        {
            var hotspot = new Rectangle
            {
                Fill = LinkBrush,
                Width = Math.Max(1, rect.Width),
                Height = Math.Max(1, rect.Height),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = link,
            };
            hotspot.PointerPressed += OnLinkPressed;
            Canvas.SetLeft(hotspot, rect.X);
            Canvas.SetTop(hotspot, rect.Y);
            _overlay.Children.Add(hotspot);
        }
    }

    public event EventHandler<PdfLink>? LinkActivated;

    private void OnLinkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Rectangle { Tag: PdfLink link })
        {
            LinkActivated?.Invoke(this, link);
            e.Handled = true;
        }
    }
}
