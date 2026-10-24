using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Poso.Pdf;
using Poso.Skia;
using Xunit;
using Vector = Avalonia.Vector;
using Fixture = Poso.Tests.HeadlessViewer.Fixture;

namespace Poso.Tests;

/// <summary>
/// PdfView text-selection interaction driven through Avalonia's headless input stack: left-drag
/// selects, double-click selects a word, a plain click clears, panning has moved to the middle
/// button (or space + left), and the text cursor follows the selectable text.
/// </summary>
public class PdfViewSelectionTests
{
    /// <summary>Baseline of the "Hello Poso" line in <see cref="PdfFixtures.CreateGraphicsPdf"/>.</summary>
    private static readonly Vector2 LineStart = new(18f, 53f);

    private static readonly Vector2 LineEnd = new(82f, 53f);

    private static readonly Vector2 InsideWord = new(68f, 53f);

    [Fact]
    public void DragSelect_SelectsTheTextBetweenTheTwoPoints() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.Drag((0, LineStart), (0, new Vector2(50f, 53f)), (0, LineEnd));

        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void DragSelect_PaintsTheHighlightOverTheSelectedLine() => HeadlessViewer.OnUi(() =>
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
    public void DragSelect_HonoursTheViewRotation() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.Viewer.Rotation = 90;
        fixture.Settle();

        fixture.Drag((0, LineStart), (0, LineEnd));

        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void DragAcrossPages_SelectsTheTailOfTheFirstAndTheHeadOfTheNext() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(2));
        using Fixture fixture = new(document);

        // "Hello Poso" starts at x=20 with 6pt glyphs: x=45 is inside the second 'o' of "Hello"
        // (caret 4) and x=30 is inside the 'e' (caret 2).
        fixture.Drag((0, new Vector2(45f, 53f)), (1, new Vector2(30f, 53f)));

        Assert.Equal("o Poso\nHe", fixture.Viewer.SelectedText);
    });

    [Fact]
    public void DoubleClick_SelectsTheWord() => HeadlessViewer.OnUi(() =>
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
    public void Click_ClearsTheSelection() => HeadlessViewer.OnUi(() =>
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
    public void LeftDrag_SelectsInsteadOfPanning() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        // Zoomed in past the viewport, so a pan would have somewhere to go: dragging right to left
        // would push the horizontal offset up if the left button still panned.
        fixture.SetZoom(4);
        Vector before = fixture.ScrollOffset;

        fixture.Drag((0, LineEnd), (0, new Vector2(50f, 53f)), (0, LineStart));

        Assert.Equal("Hello Poso", fixture.Viewer.SelectedText);
        Assert.Equal(before, fixture.ScrollOffset);
    });

    [Fact]
    public void MiddleDrag_PansWithoutSelecting() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        fixture.SetZoom(4);
        Vector before = fixture.ScrollOffset;

        Point from = fixture.ToWindowPoint(0, LineEnd);
        Point to = new(from.X - 100, from.Y);
        fixture.Window.MouseDown(from, MouseButton.Middle, RawInputModifiers.MiddleMouseButton);
        fixture.Window.MouseMove(to, RawInputModifiers.MiddleMouseButton);
        fixture.Window.MouseUp(to, MouseButton.Middle, RawInputModifiers.None);
        fixture.Settle();

        Assert.Equal(string.Empty, fixture.Viewer.SelectedText);
        Assert.True(
            fixture.ScrollOffset.X > before.X,
            $"middle-drag should pan the viewport, offset stayed {fixture.ScrollOffset}");
    });

    [Fact]
    public void SpaceDrag_PansWithoutSelecting() => HeadlessViewer.OnUi(() =>
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
    public Task DragHeldBelowTheViewport_AutoScrollsAndExtendsTheSelection() => HeadlessViewer.OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(2));
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
    public Task CopySelection_CopiesOnlyWhenTextIsSelected() => HeadlessViewer.OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        Assert.False(await fixture.Viewer.CopySelectionAsync());

        fixture.Drag((0, LineStart), (0, LineEnd));

        Assert.True(await fixture.Viewer.CopySelectionAsync());
    });

    [Fact]
    public void TextCursor_FollowsTheTextAndLeavesTheDefaultEverywhereElse() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateGraphicsPdf());
        using Fixture fixture = new(document);

        // The cursor decision only reads text that has already been extracted
        document.Pages[0].GetText(SkiaGraphicsFactory.Default);

        fixture.Hover(new Vector2(23f, 53f));
        AssertTextCursor(fixture.PageCursor);

        // Same page, same line height, but past the last glyph: default cursor again
        fixture.Hover(new Vector2(150f, 53f));
        Assert.Null(fixture.PageCursor);

        // Off the page the page view asks for nothing
        fixture.HoverPanel(new Point(200, 398));
        Assert.Null(fixture.PageCursor);
    });

    [Fact]
    public Task HoveringAnUnreadPage_PrefetchesItsTextOffTheUIThread() => HeadlessViewer.OnUiAsync(async () =>
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
        AssertTextCursor(fixture.PageCursor);
    });

    /// <summary>
    /// Avalonia's Cursor exposes nothing comparable, so the text cursor is recognised by the standard
    /// type it was built from ("Ibeam"); a page that asks for no cursor at all reports null.
    /// </summary>
    private static void AssertTextCursor(Cursor? cursor) => Assert.Equal("Ibeam", cursor?.ToString());

    private static bool IsSelectionTint(WriteableBitmap frame, Point windowPoint)
    {
        int x = Math.Clamp((int)windowPoint.X, 0, frame.PixelSize.Width - 1);
        int y = Math.Clamp((int)windowPoint.Y, 0, frame.PixelSize.Height - 1);

        using ILockedFramebuffer locked = frame.Lock();
        int offset = (y * locked.RowBytes) + (x * 4);
        bool bgra = locked.Format == PixelFormat.Bgra8888;
        int redIndex = bgra ? 2 : 0;
        int blueIndex = bgra ? 0 : 2;

        // The selection is a translucent blue over paper, so blue clearly leads red
        byte blue = Marshal.ReadByte(locked.Address, offset + blueIndex);
        byte red = Marshal.ReadByte(locked.Address, offset + redIndex);
        return blue > red + 30 && blue > 200;
    }
}
