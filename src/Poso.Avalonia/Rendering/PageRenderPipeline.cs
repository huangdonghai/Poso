using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Poso.Avalonia.Imaging;
using Poso.Graphics;
using Poso.Pdf;

namespace Poso.Avalonia.Rendering;

/// <summary>
/// Asynchronous page render pipeline with a byte-budgeted LRU bitmap cache.
/// Renders run on the thread pool (bounded concurrency); the <see cref="Rendered"/> event
/// is raised on the UI thread.
/// </summary>
internal sealed class PageRenderPipeline
{
    private const long DefaultCacheBytes = 192L * 1024 * 1024;
    private const int MaxConcurrentRenders = 4;

    private readonly PdfDocument _document;
    private readonly IGraphicsFactory _factory;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _renderGate = new(MaxConcurrentRenders, MaxConcurrentRenders);
    private readonly object _stateLock = new();
    private readonly Dictionary<long, CacheEntry> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private readonly Dictionary<long, CancellationTokenSource> _pending = new();
    private readonly long _maxCacheBytes;
    private long _cacheBytes;

    public PageRenderPipeline(PdfDocument document, IGraphicsFactory factory, long? maxCacheBytes = null)
    {
        _document = document;
        _factory = factory;
        _dispatcher = Dispatcher.UIThread;
        _maxCacheBytes = maxCacheBytes ?? DefaultCacheBytes;
    }

    /// <summary>Raised on the UI thread when a page bitmap becomes available.</summary>
    public event Action<int, WriteableBitmap>? Rendered;

    /// <summary>Returns the cached bitmap for a page, or null when it must be rendered first.</summary>
    public WriteableBitmap? TryGet(int pageIndex, double scale, int rotationDegrees)
    {
        long key = MakeKey(pageIndex, scale, rotationDegrees);
        lock (_stateLock)
        {
            if (_cache.TryGetValue(key, out CacheEntry entry))
            {
                // Refresh LRU position
                _lru.Remove(key);
                _lru.AddFirst(key);
                return entry.Bitmap;
            }
        }

        return null;
    }

    /// <summary>Schedules a render for the page if it is not cached or in flight.</summary>
    public void EnsureRendered(int pageIndex, double scale, int rotationDegrees)
    {
        long key = MakeKey(pageIndex, scale, rotationDegrees);
        CancellationTokenSource cts;
        lock (_stateLock)
        {
            if (_cache.ContainsKey(key) || _pending.ContainsKey(key))
            {
                return;
            }

            cts = new CancellationTokenSource();
            _pending[key] = cts;
        }

        RenderAsync(key, pageIndex, scale, rotationDegrees, cts);
    }

    /// <summary>Cancels in-flight renders (call when zoom or rotation changes).</summary>
    public void Invalidate()
    {
        lock (_stateLock)
        {
            foreach (CancellationTokenSource cts in _pending.Values)
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Cancels queued renders whose page left the given range. Keeps a fast scroll from building
    /// a backlog of pages that are no longer anywhere near the viewport.
    /// </summary>
    public void CancelOutside(int firstPage, int lastPage)
    {
        lock (_stateLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            List<long>? stale = null;
            foreach (KeyValuePair<long, CancellationTokenSource> pair in _pending)
            {
                int pageIndex = (int)(pair.Key >> 42);
                if (pageIndex < firstPage || pageIndex > lastPage)
                {
                    (stale ??= new List<long>()).Add(pair.Key);
                }
            }

            if (stale is null)
            {
                return;
            }

            foreach (long key in stale)
            {
                if (_pending.TryGetValue(key, out CancellationTokenSource? cts))
                {
                    _pending.Remove(key);
                    cts.Cancel();
                }
            }
        }
    }

    /// <summary>Drops all cached bitmaps and cancels pending renders.</summary>
    public void Clear()
    {
        lock (_stateLock)
        {
            foreach (CancellationTokenSource cts in _pending.Values)
            {
                cts.Cancel();
            }

            _cache.Clear();
            _lru.Clear();
            _cacheBytes = 0;
        }
    }

    private async void RenderAsync(long key, int pageIndex, double scale, int rotationDegrees, CancellationTokenSource cts)
    {
        try
        {
            PdfPage page = _document.Pages[pageIndex];
            Size size = ZoomMath.PageSize(page, rotationDegrees);
            int width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));

            // Sanity limits: skip absurdly large render targets (malformed pages)
            if (width > 16384 || height > 16384)
            {
                return;
            }

            var options = new PdfRenderOptions((float)scale, rotationDegrees - page.Rotation, PosoColor.White);

            WriteableBitmap? bitmap;
            await _renderGate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                bitmap = await Task.Run(() =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    using IGraphicsContext context = _factory.CreateContext(width, height);
                    page.Render(_factory, context, options);
                    using IImage image = context.Snapshot();
                    return BitmapConverter.ToWriteableBitmap(image);
                }, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _renderGate.Release();
            }

            bool cancelled;
            lock (_stateLock)
            {
                cancelled = cts.IsCancellationRequested;
                if (!cancelled && bitmap is not null)
                {
                    Store(key, bitmap);
                }
            }

            if (bitmap is null || cancelled)
            {
                bitmap?.Dispose();
                return;
            }

            _dispatcher.Post(() => Rendered?.Invoke(pageIndex, bitmap));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer render request
        }
        catch (Exception)
        {
            // Never let a per-page failure kill the process (async void): drop the page
        }
        finally
        {
            lock (_stateLock)
            {
                // Only drop the entry when it still belongs to this request: a cancelled render may
                // already have been replaced by a newer one for the same key.
                if (_pending.TryGetValue(key, out CancellationTokenSource? current) && ReferenceEquals(current, cts))
                {
                    _pending.Remove(key);
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Stores a rendered bitmap; callers must hold the state lock.</summary>
    private void Store(long key, WriteableBitmap bitmap)
    {
        long size = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        _cache[key] = new CacheEntry(bitmap, size);
        _lru.AddFirst(key);
        _cacheBytes += size;

        while (_cacheBytes > _maxCacheBytes && _lru.Count > 1)
        {
            LinkedListNode<long>? oldest = _lru.Last;
            if (oldest is null)
            {
                break;
            }

            _lru.RemoveLast();
            if (_cache.Remove(oldest.Value, out CacheEntry evicted))
            {
                _cacheBytes -= evicted.Size;
                // The bitmap may still be referenced by a page view; leave disposal to the GC.
            }
        }
    }

    private static long MakeKey(int pageIndex, double scale, int rotationDegrees)
    {
        // Quantize the scale to 1% steps so tiny zoom changes reuse cached renders
        int quantized = (int)Math.Round(Math.Clamp(scale, 0.01, 100d) * 100d);
        return ((long)pageIndex << 42) | ((long)(rotationDegrees / 90 + 4) << 36) | (uint)quantized;
    }

    private readonly record struct CacheEntry(WriteableBitmap Bitmap, long Size);
}
