using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Poso.Avalonia.Controls;
using Poso.Avalonia.Rendering;
using Poso.Pdf;
using Poso.Skia;
using Vector = Avalonia.Vector;

namespace Poso.Tests;

/// <summary>
/// Shared Avalonia headless harness for viewer tests: one UI-thread session, and a
/// <see cref="Fixture"/> that shows a <see cref="PdfView"/> in a window and drives it with real
/// pointer input. Page points are resolved through <see cref="ZoomMath.PageToViewMatrix"/>, whose
/// own correctness <see cref="ZoomMathTests"/> pins against rendered ink.
/// </summary>
public static class HeadlessViewer
{
    public static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    /// <summary>
    /// Runs the body on the session's UI thread and rethrows what it threw.
    /// </summary>
    public static void OnUi(Action body) => RunOnUiAsync(() =>
    {
        body();
        return Task.CompletedTask;
    }).GetAwaiter().GetResult();

    /// <summary>
    /// Runs an async body on the session's UI thread and awaits it.
    /// </summary>
    public static Task OnUiAsync(Func<Task> body) => RunOnUiAsync(body);

    /// <summary>
    /// Everything goes through the <c>Func&lt;Task&lt;T&gt;&gt;</c> overload: the plain
    /// <c>Dispatch(Action)</c> and <c>Dispatch(Func&lt;Task&gt;)</c> paths do not wait for the body, so
    /// an assertion that threw inside them left the test green - a red-check of this harness is what
    /// found it.
    /// </summary>
    private static Task<bool> RunOnUiAsync(Func<Task> body) =>
        Session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None);

    /// <summary>Application entry point for the headless Avalonia session.</summary>
    public static class HeadlessApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApplication>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    // Real drawing, so CaptureRenderedFrame yields pixels rather than null
                    UseHeadlessDrawing = false,
                    ShouldRenderOnUIThread = true,
                });
    }

    /// <summary>
    /// A themed application: without control templates the ScrollViewer reports a zero extent and
    /// viewport, which silently makes every scrolling and panning assertion vacuous.
    /// </summary>
    public sealed class TestApplication : Application
    {
        public TestApplication()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    /// <summary>A shown window hosting a <see cref="PdfView"/>, plus page-to-window mapping.</summary>
    public sealed class Fixture : IDisposable
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
            Matrix3x2 pageToView = ZoomMath.PageToViewMatrix(
                _document.Pages[pageIndex], Viewer.Rotation, Viewer.EffectiveZoom);
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

        /// <summary>The cursor the first page currently asks for.</summary>
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
