using System.Security.Cryptography;

namespace EmbyRemotePlayer.Services;

/// <summary>
/// Small stale-while-revalidate cache for read-only Emby metadata.
///
/// Design goals:
/// - navigation should render cached metadata immediately instead of waiting for HTTP;
/// - duplicate requests for the same endpoint are coalesced into one network request;
/// - stale data can be served while a background refresh updates the cache;
/// - successful responses survive app restarts in a bounded disk cache;
/// - cache is scoped by server + user, so accounts never share metadata state.
///
/// PlaybackInfo and other session-creating POST operations intentionally do not use this cache.
/// </summary>
public sealed class ApiResponseCacheService : IDisposable
{
    private const long MaxDiskBytes = 96L * 1024 * 1024;
    private const int MaxMemoryEntries = 320;

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _scope;
    private readonly string _directory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, MemoryEntry> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<CachePayload>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _backgroundRefreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _staleKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _stalePrefixes = new(StringComparer.Ordinal);
    private long _allStaleAfterUtcTicks;
    private long _accessSequence;
    private bool _disposed;

    public event Action<string>? Updated;

    public ApiResponseCacheService(string scope)
    {
        _scope = string.IsNullOrWhiteSpace(scope) ? "default" : scope;
        var scopeHash = Hash(_scope);
        _directory = Path.Combine(AppPaths.ApiCacheDirectory, scopeHash[..16]);
        Directory.CreateDirectory(_directory);
        _ = Task.Run(CleanupDiskAsync);
    }

    public async Task<T> GetAsync<T>(
        string key,
        TimeSpan freshFor,
        TimeSpan serveStaleFor,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ApiResponseCacheService));
        cancellationToken.ThrowIfCancellationRequested();

        var scopedKey = NormalizeKey(key);
        CachePayload? lastKnown = null;
        if (!forceRefresh)
        {
            var cached = await TryReadAsync(scopedKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                lastKnown = cached;
                var age = DateTimeOffset.UtcNow - cached.StoredAtUtc;
                var explicitlyStale = IsMarkedStale(scopedKey, cached.StoredAtUtc);
                if (!explicitlyStale && age <= freshFor)
                    return Materialize<T>(scopedKey, cached);

                if (age <= serveStaleFor)
                {
                    // SWR: render the previous snapshot immediately, then refresh in the background.
                    // WebSocket invalidation uses the same path, so a server-side change never blocks navigation.
                    StartBackgroundRefresh(scopedKey, factory);
                    return Materialize<T>(scopedKey, cached);
                }
            }
        }

        try
        {
            var payload = await RefreshCoalescedAsync(scopedKey, factory, cancellationToken).ConfigureAwait(false);
            return Materialize<T>(scopedKey, payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failing server must never blank a page we have shown before: fall back to the
            // last snapshot for this endpoint — however old — instead of surfacing an error.
            if (lastKnown is not null)
            {
                LogService.Warning($"Serving the last cached snapshot for {key} after a refresh failure.", ex);
                try { return Materialize<T>(scopedKey, lastKnown); }
                catch { throw; }
            }
            throw;
        }
    }

    public async Task<T?> TryGetAnyAsync<T>(string key, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        if (_disposed) return default;
        var scopedKey = NormalizeKey(key);
        var payload = await TryReadAsync(scopedKey, cancellationToken).ConfigureAwait(false);
        if (payload is null || DateTimeOffset.UtcNow - payload.StoredAtUtc > maxAge) return default;
        try { return Materialize<T>(scopedKey, payload); }
        catch { return default; }
    }

    public void Warm<T>(string key, TimeSpan freshFor, TimeSpan serveStaleFor, Func<CancellationToken, Task<T>> factory)
    {
        if (_disposed) return;
        var scopedKey = NormalizeKey(key);
        _ = Task.Run(async () =>
        {
            try
            {
                var cached = await TryReadAsync(scopedKey, _shutdown.Token).ConfigureAwait(false);
                if (cached is not null && !IsMarkedStale(scopedKey, cached.StoredAtUtc) && DateTimeOffset.UtcNow - cached.StoredAtUtc <= freshFor) return;
                await RefreshCoalescedAsync(scopedKey, factory, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) { LogService.Warning($"Metadata prefetch failed for {key}.", ex); }
        });
    }

    /// <summary>
    /// Marks one cached endpoint stale without deleting its last known value. The next reader receives
    /// the cached snapshot immediately while a coalesced background refresh fetches the new value.
    /// </summary>
    public void MarkStale(string key)
    {
        if (_disposed || string.IsNullOrWhiteSpace(key)) return;
        _staleKeys[NormalizeKey(key)] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Marks a family of endpoints stale while keeping their cached snapshots available for SWR.
    /// </summary>
    public void MarkStalePrefix(string prefix)
    {
        if (_disposed || string.IsNullOrWhiteSpace(prefix)) return;
        _stalePrefixes[NormalizeKey(prefix)] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Marks the whole server/user metadata scope stale without flushing memory or disk. This is used
    /// for Emby LibraryChanged events: navigation stays instant and each touched endpoint revalidates
    /// once in the background.
    /// </summary>
    public void MarkAllStale()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _allStaleAfterUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
    }

    public void InvalidatePrefix(string prefix)
    {
        if (_disposed) return;
        prefix = NormalizeKey(prefix);
        foreach (var key in _memory.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _memory.TryRemove(key, out _);

        // Disk entries use hashed file names, so exact prefix invalidation on disk would need an index.
        // We deliberately leave them as stale fallback; normal SWR refresh replaces them on next use.
    }

    public void ClearMemory()
    {
        _memory.Clear();
    }

    private void StartBackgroundRefresh<T>(string key, Func<CancellationToken, Task<T>> factory)
    {
        if (!_backgroundRefreshes.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshCoalescedAsync(key, factory, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Serving stale data is intentional. A failed revalidation should never break the visible page.
                LogService.Warning($"Background metadata refresh failed for {key}.", ex);
            }
            finally
            {
                _backgroundRefreshes.TryRemove(key, out _);
            }
        });
    }

    private async Task<CachePayload> RefreshCoalescedAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken callerToken)
    {
        while (true)
        {
            if (_inflight.TryGetValue(key, out var existing))
                return await existing.WaitAsync(callerToken).ConfigureAwait(false);

            var task = CreatePayloadAsync(key, factory, _shutdown.Token);
            if (_inflight.TryAdd(key, task))
            {
                try { return await task.WaitAsync(callerToken).ConfigureAwait(false); }
                finally { _inflight.TryRemove(key, out _); }
            }
        }
    }

    private async Task<CachePayload> CreatePayloadAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken token)
    {
        var value = await factory(token).ConfigureAwait(false);
        var json = JsonSerializer.SerializeToUtf8Bytes(value, CacheJsonOptions);
        _memory.TryGetValue(key, out var previous);
        var payload = new CachePayload(json, DateTimeOffset.UtcNow, Interlocked.Increment(ref _accessSequence), value);
        _memory[key] = new MemoryEntry(payload.Json, payload.StoredAtUtc, payload.AccessSequence, value);
        TrimMemory();
        _ = WriteDiskAsync(key, payload, _shutdown.Token);

        if (previous is not null && !previous.Json.AsSpan().SequenceEqual(json))
        {
            try { Updated?.Invoke(UnscopeKey(key)); } catch { }
        }
        return payload;
    }

    private async Task<CachePayload?> TryReadAsync(string key, CancellationToken token)
    {
        if (_memory.TryGetValue(key, out var memory))
        {
            var access = Interlocked.Increment(ref _accessSequence);
            _memory[key] = memory with { AccessSequence = access };
            var payload = new CachePayload(memory.Json, memory.StoredAtUtc, access, memory.Value);
            NetworkDebugService.LogMetadataCache(key, "HIT · memory", DateTimeOffset.UtcNow - memory.StoredAtUtc, memory.Json.LongLength);
            return payload;
        }

        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var file = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (file.Length <= sizeof(long) || file.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Cached metadata payload is invalid.");

            var ticks = BitConverter.ToInt64(file, 0);
            var storedAt = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
            var json = file.AsSpan(sizeof(long)).ToArray();
            var access = Interlocked.Increment(ref _accessSequence);
            _memory[key] = new MemoryEntry(json, storedAt, access, null);
            TrimMemory();
            Touch(path); // mtime is LRU only; freshness lives in the binary header.
            NetworkDebugService.LogMetadataCache(key, "HIT · disk", DateTimeOffset.UtcNow - storedAt, json.LongLength);
            return new CachePayload(json, storedAt, access, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Warning($"Discarding corrupt metadata cache file: {path}", ex);
            TryDelete(path);
            return null;
        }
    }

    private async Task WriteDiskAsync(string key, CachePayload payload, CancellationToken token)
    {
        var path = GetPath(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var header = BitConverter.GetBytes(payload.StoredAtUtc.UtcDateTime.Ticks);
            var bytes = new byte[header.Length + payload.Json.Length];
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            Buffer.BlockCopy(payload.Json, 0, bytes, header.Length, payload.Json.Length);
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, path, true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Warning("Unable to persist metadata cache.", ex); }
        finally { TryDelete(temp); }
    }

    public void Invalidate(string key)
    {
        if (_disposed || string.IsNullOrWhiteSpace(key)) return;
        var scopedKey = NormalizeKey(key);
        _memory.TryRemove(scopedKey, out _);
        TryDelete(GetPath(scopedKey));
    }

    private async Task CleanupDiskAsync()
    {
        try
        {
            await Task.Delay(1800, _shutdown.Token).ConfigureAwait(false);
            var directory = new DirectoryInfo(_directory);
            if (!directory.Exists) return;
            var files = directory.EnumerateFiles("*.bin", SearchOption.TopDirectoryOnly).ToList();
            long total = files.Sum(file => file.Length);
            if (total <= MaxDiskBytes) return;

            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= MaxDiskBytes) break;
                var size = file.Length;
                TryDelete(file.FullName);
                total -= size;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Warning("Metadata disk cache cleanup failed.", ex); }
    }

    private void TrimMemory()
    {
        if (_memory.Count <= MaxMemoryEntries) return;
        var victims = _memory
            .OrderBy(pair => pair.Value.AccessSequence)
            .Take(Math.Max(1, _memory.Count - MaxMemoryEntries + 24))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var victim in victims) _memory.TryRemove(victim, out _);
    }

    private bool IsMarkedStale(string scopedKey, DateTimeOffset storedAtUtc)
    {
        var allStaleTicks = Interlocked.Read(ref _allStaleAfterUtcTicks);
        if (allStaleTicks > 0 && storedAtUtc.UtcDateTime.Ticks <= allStaleTicks)
            return true;

        if (_staleKeys.TryGetValue(scopedKey, out var keyCutoff) && storedAtUtc <= keyCutoff)
            return true;

        foreach (var pair in _stalePrefixes)
        {
            if (storedAtUtc <= pair.Value && scopedKey.StartsWith(pair.Key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private string NormalizeKey(string key) => _scope + "|" + key.Trim();

    private string UnscopeKey(string key)
    {
        var prefixLength = _scope.Length + 1;
        return key.Length > prefixLength && key.StartsWith(_scope + "|", StringComparison.Ordinal)
            ? key[prefixLength..]
            : key;
    }

    private string GetPath(string key) => Path.Combine(_directory, Hash(key) + ".bin");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static T Deserialize<T>(byte[] json) => JsonSerializer.Deserialize<T>(json, CacheJsonOptions)
        ?? throw new InvalidDataException("Cached metadata deserialized to null.");

    private T Materialize<T>(string key, CachePayload payload)
    {
        if (payload.Value is T typed) return typed;
        var value = Deserialize<T>(payload.Json);
        if (_memory.TryGetValue(key, out var entry))
            _memory[key] = entry with { Value = value };
        return value;
    }

    private static void Touch(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(6))
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _memory.Clear();
        _inflight.Clear();
        _backgroundRefreshes.Clear();
        _staleKeys.Clear();
        _stalePrefixes.Clear();
        _shutdown.Dispose();
    }

    private sealed record MemoryEntry(byte[] Json, DateTimeOffset StoredAtUtc, long AccessSequence, object? Value);
    private sealed record CachePayload(byte[] Json, DateTimeOffset StoredAtUtc, long AccessSequence, object? Value);
}
