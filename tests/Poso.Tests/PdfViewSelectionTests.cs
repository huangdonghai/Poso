using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Poso.Avalonia.Controls;
using Poso.Avalonia.Rendering;
using Poso.Pdf;
using Poso.Skia;
using Xunit;
using Vector = Avalonia.Vector;

namespace Poso.Tests;

/// <summary>
/// PdfView text-selection interaction driven through Avalonia's headless input stack: left-drag
/// selects, double-click selects a word, a plain click clears, and panning has moved to the middle
/// button (or space + left). Coordinates are resolved through the same page-to-view matrix that the
/// overlay uses, which <see cref="ZoomMathTests"/> pins against the renderer's own output.
/// </summary>
public class PdfViewSelectionTests
{
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    /// <summary>Baseline of the "Hello Poso" line in <see cref="PdfFixtures.CreateGraphicsPdf"/>.</summary>
    private static readonly Vector2 LineStart = new(18f, 53f);

    private static readonly Vector2 LineEnd = new(82f, 53f);

    private static readonly Vector2 InsideWord = new(68f, 53f);

    /// <summary>Runs the test body on the headless session's UI thread.</summary>
    private static void OnUi(Action body) => Session.Dispatch(body, CancellationToken.None);

    private static Task OnUiAsync(Func<Task> body) => Session.Dispatch(body, CancellationToken.None);

    [Fact]
    public void DragSelect_SelectsTheTextBetweenTheTwoPoints() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.Drag((0, LineStart), (0, new Vector2(50f, 53f)), (0, LineEnd));

        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void DragSelect_PaintsTheHighlightOverTheSelectedLine() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        // The gap between the two words is inside the selection box but carries no glyph ink, so it
        // reads as the selection tint rather than as paper. Sampling the same pixel before the drag
        // keeps the check honest: without a selection it is plain paper.
        Point gap = fixture.ToWindowPoint(0, new Vector2(53f, 53f));
        WriteableBitmap? before = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(before);
        bool tintedBefore = IsSelectionTint(before, gap);
        before.Dispose();

        fixture.Drag((0, LineStart), (0, LineEnd));

        WriteableBitmap? frame = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        bool blue = IsSelectionTint(frame, gap);
        frame.Dispose();

        Assert.False(tintedBefore, "the pixel should be untinted before the drag");
        Assert.True(blue, "the selected line should be tinted with the selection colour");
    });

    [Fact]
    public void DragSelect_HonoursTheViewRotation() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.Viewer.Rotation = 90;
        fixture.Settle();

        fixture.Drag((0, LineStart), (0, LineEnd));

        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void DragAcrossPages_SelectsTheTailOfTheFirstAndTheHeadOfTheNext() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTwoPageTextPdf());
        using Fixture fixture = new(document);

        // "Hello Poso" starts at x=20 with 6pt glyphs: x=45 is inside the second 'o' of "Hello"
        // (caret 4) and x=30 is inside the 'e' (caret 2).
        fixture.Drag((0, new Vector2(45f, 53f)), (1, new Vector2(30f, 53f)));

        Assert.Equal("o Poso\nHe", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void DoubleClick_SelectsTheWord() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        Point word = fixture.ToWindowPoint(0, InsideWord);
        fixture.Window.MouseDown(word, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseUp(word, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseDown(word, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseUp(word, MouseButton.Left, RawInputModifiers.None);
        fixture.Settle();

        Assert.Equal("Poso", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void Click_ClearsTheSelection() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.Drag((0, LineStart), (0, LineEnd));
        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);

        Point elsewhere = fixture.ToWindowPoint(0, new Vector2(24f, 53f));
        fixture.Window.MouseDown(elsewhere, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseUp(elsewhere, MouseButton.Left, RawInputModifiers.None);
        fixture.Settle();

        Assert.Equal(string.Empty, fixture.Viewer.SelectedText);
    });

    [Fact]
    public void LeftDrag_SelectsInsteadOfPanning() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.SetZoom(4);
        Vector before = fixture.ScrollOffset;

        fixture.Drag((0, LineStart), (0, LineEnd));

        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);
        Assert.Equal(before, fixture.ScrollOffset);
    });

    [Fact]
    public void MiddleDrag_PansWithoutSelecting() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.SetZoom(4);
        Vector before = fixture.ScrollOffset;

        Point from = fixture.ToWindowPoint(0, LineStart);
        Point to = new(from.X, from.Y - 60);
        fixture.Window.MouseDown(from, MouseButton.Middle, RawInputModifiers.None);
        fixture.Window.MouseMove(to, RawInputModifiers.MiddleMouseButton);
        fixture.Window.MouseUp(to, MouseButton.Middle, RawInputModifiers.None);
        fixture.Settle();

        Assert.Equal(string.Empty, fixture.Viewer.SelectedText);
        Assert.NotEqual(before, fixture.ScrollOffset);
    });

    [Fact]
    public Task CopySelection_CopiesOnlyWhenTextIsSelected() => OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        Assert.False(await fixture.Viewer.CopySelectionAsync());

        fixture.Drag((0, LineStart), (0, LineEnd));

        Assert.True(await fixture.Viewer.CopySelectionAsync());
    });

    [Fact]
    public void SpaceDrag_PansWithoutSelecting() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.SetZoom(4);

        // A click first, so the viewer holds focus and the key event reaches its handler
        Point click = fixture.ToWindowPoint(0, LineStart);
        fixture.Window.MouseDown(click, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseUp(click, MouseButton.Left, RawInputModifiers.None);
        fixture.Settle();

        Vector before = fixture.ScrollOffset;
        fixture.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");

        Point from = fixture.ToWindowPoint(0, LineEnd);
        Point to = new(from.X, from.Y - 60);
        fixture.Window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
        fixture.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        fixture.Settle();

        Assert.Equal(string.Empty, fixture.Viewer.SelectedText);
        Assert.NotEqual(before, fixture.ScrollOffset);
    });

    [Fact]
    public Task DragHeldBelowTheViewport_AutoScrollsAndExtendsTheSelection() => OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTwoPageTextPdf());
        using Fixture fixture = new(document);

        // One page is already taller than the 400 DIP viewport
        fixture.SetZoom(4);

        Point start = fixture.ToWindowPoint(0, new Vector2(45f, 53f));
        fixture.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);

        Point below = new(start.X, 460);
        fixture.Window.MouseMove(below, RawInputModifiers.LeftMouseButton);

        double before = fixture.ScrollOffset.Y;
        for (int attempt = 0; attempt < 40 && fixture.ScrollOffset.Y <= before; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(fixture.ScrollOffset.Y > before, "holding the pointer below the viewport should scroll the pages");
        Assert.Contains("Poso", fixture.Viewer.SelectedText);

        fixture.Window.MouseUp(below, MouseButton.Left, RawInputModifiers.None);
        fixture.Settle();
    });

    [Fact]
    public void TextCursor_FollowsTheTextAndLeavesTheDefaultEverywhereElse() => OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        // Search reads every page, so the cursor decision has text to work from without a drag
        fixture.Viewer.Search("Poso");
        fixture.Settle();

        fixture.Hover(new Vector2(23f, 53f));
        Assert.Equal(new Cursor(StandardCursorType.Ibeam), fixture.PageCursor);

        // Same page, same line height, but past the last glyph: default cursor again
        fixture.Hover(new Vector2(150f, 53f));
        Assert.Null(fixture.PageCursor);

        // Over the page gap the page view is not under the pointer at all
        fixture.HoverPanel(new Point(200, 398));
        Assert.Null(fixture.PageCursor);
    });

    [Fact]
    public Task HoveringAnUnreadPage_PrefetchesItsTextOffTheUIThread() => OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.Hover(new Vector2(23f, 53f));

        // Nothing has been read yet, and the pointer handler must not have read it synchronously
        Assert.Null(document.Pages[0].PeekText());
        Assert.Null(fixture.PageCursor);

        for (int attempt = 0; attempt < 80 && fixture.PageCursor is null; attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(document.Pages[0].PeekText());
        Assert.Equal(new Cursor(StandardCursorType.Ibeam), fixture.PageCursor);
    });

    private static bool IsSelectionTint(WriteableBitmap frame, Point windowPoint)
    {
        int x = Math.Clamp((int)windowPoint.X, 0, frame.PixelSize.Width - 1);
        int y = Math.Clamp((int)windowPoint.Y, 0, frame.PixelSize.Height - 1);

        using ILockedFramebuffer locked = frame.Lock();
        int offset = (y * locked.RowBytes) + (x * 4);

        // BGRA: the selection is a translucent blue over paper, so blue clearly leads red
        byte blue = Marshal.ReadByte(locked.Address, offset);
        byte red = Marshal.ReadByte(locked.Address, offset + 2);
        return blue > red + 30 && blue > 200;
    }

    /// <summary>Application entry point for the headless Avalonia session.</summary>
    public static class HeadlessApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { ShouldRenderOnUIThread = true });
    }

    /// <summary>A shown window hosting a <see cref="PdfView"/>, plus page-to-window mapping.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly PdfDocument _document;

        public Fixture(PdfDocument document)
        {
            _document = document;
            Viewer = new PdfView { GraphicsFactory = SkiaGraphicsFactory.Default, Source = document };
            Window = new Window { Content = Viewer, Width = 400, Height = 400 };
            Window.Show();
            Settle();
        }

        public Window Window { get; }

        public PdfView Viewer { get; }

        public Vector ScrollOffset => Viewer.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset;

        public void Settle() => Dispatcher.UIThread.RunJobs();

        public void SetZoom(double zoom)
        {
            Viewer.ZoomMode = ZoomMode.Custom;
            Viewer.Zoom = zoom;
            Settle();
        }

        /// <summary>Maps a point in a page's user space to window coordinates.</summary>
        public Point ToWindowPoint(int pageIndex, Vector2 pagePoint)
        {
            Matrix3x2 pageToView = ZoomMath.PageToViewMatrix(_document.Pages[pageIndex], Viewer.Rotation, Viewer.EffectiveZoom);
            Vector2 view = Vector2.Transform(pagePoint, pageToView);
            Point origin = PageOrigin(pageIndex);
            return new Point(origin.X + view.X, origin.Y + view.Y);
        }

        /// <summary>Presses on the first point, moves through the rest, and releases on the last.</summary>
        public void Drag(params (int Page, Vector2 Point)[] points)
        {
            Point start = ToWindowPoint(points[0].Page, points[0].Point);
            Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);

            for (int i = 1; i < points.Length; i++)
            {
                Window.MouseMove(ToWindowPoint(points[i].Page, points[i].Point), RawInputModifiers.LeftMouseButton);
                Settle();
            }

            Point end = ToWindowPoint(points[^1].Page, points[^1].Point);
            Window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Settle();
        }

        /// <summary>Moves the pointer to a point in a page's user space (no button held).</summary>
        public void Hover(Vector2 pagePoint, int pageIndex = 0)
        {
            Window.MouseMove(ToWindowPoint(pageIndex, pagePoint), RawInputModifiers.None);
            Settle();
        }

        public void HoverPanel(Point panelPoint)
        {
            Point origin = PageOrigin(0);
            Window.MouseMove(new Point(panelPoint.X + origin.X, panelPoint.Y + origin.Y), RawInputModifiers.None);
            Settle();
        }

        /// <summary>The cursor the page under the pointer currently asks for.</summary>
        public Cursor? PageCursor => PageView(0).Cursor;

        public Control PageView(int pageIndex) => Panel.Children[pageIndex];

        private StackPanel Panel => Viewer.GetVisualDescendants().OfType<StackPanel>().Single();

        private Point PageOrigin(int pageIndex)
        {
            Visual pageView = Panel.Children[pageIndex];
            return pageView.TranslatePoint(new Point(0, 0), Window) ?? throw new InvalidOperationException("page not laid out");
        }

        public void Dispose()
        {
            Window.Close();
        }
    }
}
