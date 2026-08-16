using System.Security.Cryptography;
using System.Threading.Channels;
using EmbyRemotePlayer.Models;

namespace EmbyRemotePlayer.Services;

public enum ImageRequestPriority
{
    Immediate = 0,
    Visible = 1,
    Prefetch = 2
}

/// <summary>
/// Viewport-oriented image pipeline.
/// - fixed size buckets to maximize cache reuse
/// - request coalescing so the same image is never downloaded twice concurrently
/// - priority promotion when a prefetched image becomes visible
/// - cancellation of queued/in-flight work when its last page consumer disappears
/// - three priority queues (detail/visible/prefetch)
/// - bounded network workers and decode workers
/// - bounded in-memory LRU plus persistent disk cache
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private const int MaxImageBytes = 24 * 1024 * 1024;
    private const long MaxMemoryBytes = 220L * 1024 * 1024;
    private const long MaxDiskBytes = 768L * 1024 * 1024;
    private const int WorkerCount = 8;
    private const int DecodeConcurrency = 2;

    private static readonly int[] WidthBuckets = [256, 512, 1024, 1600];

    private readonly EmbyApiClient _api;
    private readonly string _serverKey;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<ImageRequestEntry> _immediateQueue;
    private readonly Channel<ImageRequestEntry> _visibleQueue;
    private readonly Channel<ImageRequestEntry> _prefetchQueue;
    private readonly ConcurrentDictionary<string, ImageRequestEntry> _inflight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _decodeThrottle = new(DecodeConcurrency, DecodeConcurrency);
    private readonly Task[] _workers;

    private readonly object _memoryGate = new();
    private readonly Dictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _memoryBytes;
    private bool _disposed;

    public ImageCacheService(EmbyApiClient api)
    {
        _api = api;
        _serverKey = api.ServerBaseUri.AbsoluteUri;
        AppPaths.EnsureDirectories();

        _immediateQueue = CreateQueue();
        _visibleQueue = CreateQueue();
        _prefetchQueue = CreateQueue();
        _workers = Enumerable.Range(0, WorkerCount).Select(_ => Task.Run(WorkerLoopAsync)).ToArray();
        _ = Task.Run(CleanupDiskCacheAsync);
    }

    private static Channel<ImageRequestEntry> CreateQueue() => Channel.CreateUnbounded<ImageRequestEntry>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public Task<BitmapSource?> GetPrimaryAsync(
        EmbyItem item,
        int maxWidth,
        CancellationToken cancellationToken = default,
        ImageRequestPriority priority = ImageRequestPriority.Visible) =>
        GetAsync(item.EffectivePrimaryImageItemId, "Primary", item.EffectivePrimaryImageTag, maxWidth, cancellationToken, priority);

    /// <summary>
    /// Episode cards must use the episode's own still image. Do not silently collapse
    /// multiple episodes onto the series poster/backdrop, because every episode can
    /// have a distinct Primary image in Emby. If this episode truly has no Primary
    /// image, return null and let the UI decide an explicit fallback.
    /// </summary>
    public Task<BitmapSource?> GetEpisodePrimaryAsync(
        EmbyItem item,
        int maxWidth,
        CancellationToken cancellationToken = default,
        ImageRequestPriority priority = ImageRequestPriority.Visible)
    {
        if (!string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
            return GetPrimaryAsync(item, maxWidth, cancellationToken, priority);

        // Some list endpoints (notably Resume on some Emby servers) omit ImageTags even
        // though /Items/{episodeId}/Images/Primary exists. In that case request the
        // episode image without a tag, but keep a per-episode cache identity so different
        // episodes can never alias to one Series/Season image.
        var cacheIdentity = item.OwnPrimaryImageTag
                            ?? item.Etag
                            ?? item.PresentationUniqueKey
                            ?? "episode-current";
        return GetAsyncCore(item.Id, "Primary", item.OwnPrimaryImageTag, cacheIdentity, maxWidth, cancellationToken, priority);
    }

    public Task<BitmapSource?> GetBackdropAsync(
        EmbyItem item,
        int maxWidth,
        CancellationToken cancellationToken = default,
        ImageRequestPriority priority = ImageRequestPriority.Visible) =>
        GetAsync(item.EffectiveBackdropItemId, "Backdrop", item.EffectiveBackdropTag, maxWidth, cancellationToken, priority);

    public Task<BitmapSource?> GetAsync(
        string itemId,
        string imageType,
        string? tag,
        int requestedWidth,
        CancellationToken cancellationToken = default,
        ImageRequestPriority priority = ImageRequestPriority.Visible)
    {
        if (string.IsNullOrWhiteSpace(tag)) return Task.FromResult<BitmapSource?>(null);
        return GetAsyncCore(itemId, imageType, tag, tag, requestedWidth, cancellationToken, priority);
    }

    private async Task<BitmapSource?> GetAsyncCore(
        string itemId,
        string imageType,
        string? requestTag,
        string cacheIdentity,
        int requestedWidth,
        CancellationToken cancellationToken,
        ImageRequestPriority priority)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ImageCacheService));
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(cacheIdentity)) return null;
        cancellationToken.ThrowIfCancellationRequested();

        var maxWidth = NormalizeWidth(requestedWidth);
        var key = BuildKey(itemId, imageType, cacheIdentity, maxWidth);
        if (TryGetMemory(key, out var cached)) return cached;

        var candidate = new ImageRequestEntry(key, itemId, imageType, requestTag, maxWidth, priority);
        var request = _inflight.GetOrAdd(key, candidate);
        if (ReferenceEquals(request, candidate))
        {
            QueueRequest(request, priority);
        }
        else
        {
            candidate.Dispose();
            if (request.Promote(priority)) QueueRequest(request, priority);
        }

        request.AddWaiter();
        try
        {
            return await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (request.ReleaseWaiter() == 0 && !request.Task.IsCompleted)
            {
                RemoveInflightIfSame(request.Key, request);
                request.Cancel();
            }
        }
    }

    /// <summary>
    /// Fire-and-forget warming for a deliberately small look-ahead window.
    /// UI-driven prefetch normally uses the page cancellation token directly;
    /// these helpers are kept for callers that intentionally want cache warming.
    /// </summary>
    public void PrefetchPrimary(EmbyItem item, int requestedWidth)
    {
        if (_disposed || string.IsNullOrWhiteSpace(item.EffectivePrimaryImageTag)) return;
        _ = GetPrimaryAsync(item, requestedWidth, _shutdown.Token, ImageRequestPriority.Prefetch);
    }

    public void PrefetchBackdrop(EmbyItem item, int requestedWidth)
    {
        if (_disposed || string.IsNullOrWhiteSpace(item.EffectiveBackdropTag)) return;
        _ = GetBackdropAsync(item, requestedWidth, _shutdown.Token, ImageRequestPriority.Prefetch);
    }

    private void QueueRequest(ImageRequestEntry request, ImageRequestPriority priority)
    {
        var writer = priority switch
        {
            ImageRequestPriority.Immediate => _immediateQueue.Writer,
            ImageRequestPriority.Prefetch => _prefetchQueue.Writer,
            _ => _visibleQueue.Writer
        };

        if (writer.TryWrite(request)) return;

        RemoveInflightIfSame(request.Key, request);
        request.Completion.TrySetResult(null);
    }

    private async Task WorkerLoopAsync()
    {
        var shutdownToken = _shutdown.Token;
        try
        {
            while (!shutdownToken.IsCancellationRequested)
            {
                var request = await ReadNextAsync(shutdownToken).ConfigureAwait(false);
                if (request is null || !request.TryStart()) continue;

                using var workCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, request.CancellationToken);
                var token = workCts.Token;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (TryGetMemory(request.Key, out var cached))
                    {
                        request.Completion.TrySetResult(cached);
                        continue;
                    }

                    var bitmap = await LoadCoreAsync(request, token).ConfigureAwait(false);
                    if (bitmap is not null) PutMemory(request.Key, bitmap);
                    request.Completion.TrySetResult(bitmap);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(token);
                }
                catch (OutOfMemoryException ex)
                {
                    LogService.Error($"Image decoding ran out of memory for item {request.ItemId} ({request.ImageType}, width {request.Width}).", ex);
                    TrimMemoryAggressively();
                    request.Completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    LogService.Warning($"Unable to load {request.ImageType} image for item {request.ItemId}.", ex);
                    request.Completion.TrySetResult(null);
                }
                finally
                {
                    RemoveInflightIfSame(request.Key, request);
                    request.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
    }

    private async Task<ImageRequestEntry?> ReadNextAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_immediateQueue.Reader.TryRead(out var immediate)) return immediate;
            if (_visibleQueue.Reader.TryRead(out var visible)) return visible;
            if (_prefetchQueue.Reader.TryRead(out var prefetch)) return prefetch;

            var a = _immediateQueue.Reader.WaitToReadAsync(token).AsTask();
            var b = _visibleQueue.Reader.WaitToReadAsync(token).AsTask();
            var c = _prefetchQueue.Reader.WaitToReadAsync(token).AsTask();
            await Task.WhenAny(a, b, c).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<BitmapSource?> LoadCoreAsync(ImageRequestEntry request, CancellationToken token)
    {
        var path = GetCachePath(request.Key);
        byte[]? bytes = null;

        if (File.Exists(path))
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes) throw new InvalidDataException("Cached image is invalid.");
                TouchCacheFile(path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Warning($"Discarding a corrupt cached image: {path}", ex);
                bytes = null;
                TryDelete(path);
            }
        }

        if (bytes is null)
        {
            bytes = await _api.DownloadImageAsync(request.ItemId, request.ImageType, request.Tag, request.Width, token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                throw new InvalidDataException($"Image payload size {bytes.Length} is outside the safe range.");
            await WriteCacheFileAsync(path, bytes, token).ConfigureAwait(false);
        }

        await _decodeThrottle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return Decode(bytes, request.Width);
        }
        finally
        {
            _decodeThrottle.Release();
        }
    }

    private bool TryGetMemory(string key, out BitmapSource? bitmap)
    {
        lock (_memoryGate)
        {
            if (!_memory.TryGetValue(key, out var entry))
            {
                bitmap = null;
                return false;
            }

            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            bitmap = entry.Bitmap;
            return true;
        }
    }

    private void PutMemory(string key, BitmapSource bitmap)
    {
        var bytes = EstimateDecodedBytes(bitmap);
        lock (_memoryGate)
        {
            if (_memory.TryGetValue(key, out var old))
            {
                _memoryBytes -= old.EstimatedBytes;
                _lru.Remove(old.Node);
            }

            var node = _lru.AddFirst(key);
            _memory[key] = new MemoryEntry(bitmap, bytes, node);
            _memoryBytes += bytes;

            while (_memoryBytes > MaxMemoryBytes && _lru.Last is { } tail)
            {
                var victimKey = tail.Value;
                _lru.RemoveLast();
                if (_memory.Remove(victimKey, out var victim)) _memoryBytes -= victim.EstimatedBytes;
            }
        }
    }

    private void TrimMemoryAggressively()
    {
        lock (_memoryGate)
        {
            var target = MaxMemoryBytes / 3;
            while (_memoryBytes > target && _lru.Last is { } tail)
            {
                var victimKey = tail.Value;
                _lru.RemoveLast();
                if (_memory.Remove(victimKey, out var victim)) _memoryBytes -= victim.EstimatedBytes;
            }
        }
        GC.Collect(1, GCCollectionMode.Optimized, blocking: false, compacting: false);
    }

    private static long EstimateDecodedBytes(BitmapSource bitmap)
    {
        var pixels = (long)Math.Max(1, bitmap.PixelWidth) * Math.Max(1, bitmap.PixelHeight);
        return Math.Min(int.MaxValue, pixels * 4L);
    }

    private static BitmapSource Decode(byte[] bytes, int maxWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = maxWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task WriteCacheFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            await Task.Delay(1500, _shutdown.Token).ConfigureAwait(false);
            var directory = new DirectoryInfo(AppPaths.ImageCacheDirectory);
            if (!directory.Exists) return;

            var files = directory.EnumerateFiles("*.img", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            long total = files.Sum(file => file.Length);
            if (total <= MaxDiskBytes) return;

            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= MaxDiskBytes) break;
                var length = file.Length;
                TryDelete(file.FullName);
                total -= length;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Warning("Image disk cache cleanup failed.", ex);
        }
    }

    private string BuildKey(string itemId, string imageType, string tag, int width) =>
        $"{_serverKey}|{itemId}|{imageType}|{tag}|{width}";

    private static int NormalizeWidth(int requestedWidth)
    {
        requestedWidth = Math.Clamp(requestedWidth, 64, WidthBuckets[^1]);
        foreach (var bucket in WidthBuckets)
            if (requestedWidth <= bucket) return bucket;
        return WidthBuckets[^1];
    }

    private static string GetCachePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(AppPaths.ImageCacheDirectory, hash + ".img");
    }

    private static void TouchCacheFile(string path)
    {
        try
        {
            var current = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - current > TimeSpan.FromHours(12)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void RemoveInflightIfSame(string key, ImageRequestEntry request)
    {
        ((ICollection<KeyValuePair<string, ImageRequestEntry>>)_inflight)
            .Remove(new KeyValuePair<string, ImageRequestEntry>(key, request));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        foreach (var request in _inflight.Values) request.Cancel();
        _immediateQueue.Writer.TryComplete();
        _visibleQueue.Writer.TryComplete();
        _prefetchQueue.Writer.TryComplete();
    }

    private sealed record MemoryEntry(BitmapSource Bitmap, long EstimatedBytes, LinkedListNode<string> Node);

    private sealed class ImageRequestEntry : IDisposable
    {
        private int _waiters;
        private int _started;
        private int _priority;
        private readonly CancellationTokenSource _cancellation = new();

        public ImageRequestEntry(string key, string itemId, string imageType, string? tag, int width, ImageRequestPriority priority)
        {
            Key = key;
            ItemId = itemId;
            ImageType = imageType;
            Tag = tag;
            Width = width;
            _priority = (int)priority;
        }

        public string Key { get; }
        public string ItemId { get; }
        public string ImageType { get; }
        public string? Tag { get; }
        public int Width { get; }
        public TaskCompletionSource<BitmapSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BitmapSource?> Task => Completion.Task;
        public CancellationToken CancellationToken => _cancellation.Token;

        public void AddWaiter() => Interlocked.Increment(ref _waiters);
        public int ReleaseWaiter() => Interlocked.Decrement(ref _waiters);
        public bool TryStart() => Interlocked.CompareExchange(ref _started, 1, 0) == 0;

        public bool Promote(ImageRequestPriority priority)
        {
            var target = (int)priority;
            while (true)
            {
                var current = Volatile.Read(ref _priority);
                if (target >= current || Volatile.Read(ref _started) != 0) return false;
                if (Interlocked.CompareExchange(ref _priority, target, current) == current) return true;
            }
        }

        public void Cancel()
        {
            try { _cancellation.Cancel(); } catch { }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
