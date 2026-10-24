using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Poso.Pdf;
using Xunit;
using Fixture = Poso.Tests.HeadlessViewer.Fixture;

namespace Poso.Tests;

/// <summary>
/// PdfView document-wide search: it runs off the UI thread, publishes hits as pages are read, and a
/// newer search takes over from an older one. Reading a page's text costs milliseconds per page, so
/// the old synchronous sweep froze the viewer for seconds on a long document.
/// </summary>
public class PdfViewSearchTests
{
    private const int LongDocumentPages = 120;

    [Fact]
    public Task SearchAsync_FindsTheHitOnEveryPageAndJumpsToTheFirst() => HeadlessViewer.OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(3));
        using Fixture fixture = new(document);

        int count = await fixture.Viewer.SearchAsync("Poso");
        fixture.Settle();

        Assert.Equal(3, count);
        Assert.Equal(3, fixture.Viewer.SearchHits.Count);
        Assert.Equal(new[] { 0, 1, 2 }, fixture.Viewer.SearchHits.Select(hit => hit.PageIndex));
        Assert.Equal(0, fixture.Viewer.ActiveHitIndex);
        Assert.False(fixture.Viewer.IsSearching);
    });

    [Fact]
    public Task SearchAsync_ReturnsBeforeTheScanHasFinished() => HeadlessViewer.OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(LongDocumentPages));
        using Fixture fixture = new(document);

        var clock = Stopwatch.StartNew();
        Task<int> scan = fixture.Viewer.SearchAsync("Poso");
        clock.Stop();

        // The call only queued the work: the sweep of 120 pages cannot have happened yet, and it did
        // not run on this thread
        Assert.True(fixture.Viewer.IsSearching);
        Assert.Empty(fixture.Viewer.SearchHits);
        Assert.True(clock.ElapsedMilliseconds < 50, $"starting a search blocked the UI thread for {clock.ElapsedMilliseconds}ms");

        int count = await scan;
        fixture.Settle();

        Assert.Equal(LongDocumentPages, count);
        Assert.Equal(LongDocumentPages, fixture.Viewer.SearchHits.Count);
        Assert.False(fixture.Viewer.IsSearching);
    });

    [Fact]
    public Task SearchAsync_SupersedesAnEarlierSearch() => HeadlessViewer.OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(LongDocumentPages));
        using Fixture fixture = new(document);

        Task<int> first = fixture.Viewer.SearchAsync("Poso");
        Task<int> second = fixture.Viewer.SearchAsync("Zebra");

        Assert.Equal(0, await second);
        Assert.Equal(0, await first);
        fixture.Settle();

        // The abandoned sweep contributes nothing, not even later
        Assert.Empty(fixture.Viewer.SearchHits);
        Assert.False(fixture.Viewer.IsSearching);
    });

    [Fact]
    public Task ClearSearch_StopsTheRunningScan() => HeadlessViewer.OnUiAsync(async () =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(LongDocumentPages));
        using Fixture fixture = new(document);

        Task<int> scan = fixture.Viewer.SearchAsync("Poso");
        fixture.Viewer.ClearSearch();
        Assert.False(fixture.Viewer.IsSearching);

        await scan;
        fixture.Settle();

        Assert.Empty(fixture.Viewer.SearchHits);
    });

    [Fact]
    public void SearchAsync_WithNoQueryClearsTheHits() => HeadlessViewer.OnUi(() =>
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateTextPagesPdf(2));
        using Fixture fixture = new(document);

        fixture.Viewer.ClearSearch();
        Assert.Equal(0, fixture.Viewer.SearchAsync("   ").Result);
        Assert.Empty(fixture.Viewer.SearchHits);
        Assert.False(fixture.Viewer.IsSearching);
    });
}
