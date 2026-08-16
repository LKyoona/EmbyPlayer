using System.IO.Compression;
using System.Text.RegularExpressions;
using EmbyRemotePlayer.Models;

namespace EmbyRemotePlayer.Services;

public sealed class EmbyApiClient : IDisposable
{
    public const string ClientName = "Emby Remote Player";
    public const string ClientVersion = "0.8.2.8";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly int _maxStreamingBitrate;
    private ApiResponseCacheService? _responseCache;
    private EmbyEventSocketService? _eventSocket;
    private bool _disposed;

    public EmbyApiClient(string serverUrl, string deviceId, int maxStreamingBitrate = 40_000_000)
    {
        ServerBaseUri = NormalizeServerUri(serverUrl);
        DeviceId = string.IsNullOrWhiteSpace(deviceId) ? $"erp-{Guid.NewGuid():N}" : deviceId;
        _maxStreamingBitrate = Math.Clamp(maxStreamingBitrate, 1_000_000, 120_000_000);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16,
            EnableMultipleHttp2Connections = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(35),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"EmbyRemotePlayer/{ClientVersion}");
    }

    public Uri ServerBaseUri { get; }
    public string DeviceId { get; }
    public string? AccessToken { get; private set; }
    public string? UserId { get; private set; }
    public string? ServerId { get; private set; }
    public EmbyUser? CurrentUser { get; private set; }

    public event Action<string>? MetadataUpdated;

    public void SetSession(string accessToken, string userId, string? serverId)
    {
        AccessToken = accessToken;
        UserId = userId;
        ServerId = serverId;

        _eventSocket?.Dispose();
        _eventSocket = null;
        _responseCache?.Dispose();
        _responseCache = new ApiResponseCacheService($"{ServerBaseUri.AbsoluteUri}|{userId}");
        _responseCache.Updated += endpoint =>
        {
            try { MetadataUpdated?.Invoke(endpoint); } catch { }
        };

        // Emby recommends websocket notifications to reduce repeated HTTP polling. The socket is an
        // optimization only: if it cannot connect, the SWR freshness windows remain the fallback.
        _eventSocket = new EmbyEventSocketService(ServerBaseUri, accessToken, DeviceId);
        _eventSocket.MessageReceived += HandleServerEvent;
        _eventSocket.Start();
    }

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<PublicSystemInfo>("System/Info/Public", false, cancellationToken);

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "Users/AuthenticateByName", false);
        request.Content = JsonContent.Create(new { Username = username.Trim(), Pw = password }, options: JsonOptions);
        using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
        var result = await ReadJsonResponseAsync<AuthenticationResult>(response, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.User?.Id))
            throw new EmbyApiException("服务器没有返回有效的访问令牌或用户 ID。");

        SetSession(result.AccessToken, result.User.Id, result.ServerId ?? result.User.ServerId);
        CurrentUser = result.User;
        return result;
    }

    public async Task<EmbyUser> ValidateSessionAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var user = await GetCachedJsonAsync<EmbyUser>($"Users/{EscapePath(UserId!)}", CachePolicy.User, cancellationToken).ConfigureAwait(false);
        CurrentUser = user;
        return user;
    }

    public Task<EmbyUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        CurrentUser is not null ? Task.FromResult(CurrentUser) : ValidateSessionAsync(cancellationToken);

    public async Task<List<EmbyItem>> GetViewsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var result = await GetCachedJsonAsync<ItemsResult>($"Users/{EscapePath(UserId!)}/Views", CachePolicy.Library, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    public async Task<List<EmbyItem>> GetItemsAsync(string? parentId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["ParentId"] = parentId,
            ["Recursive"] = "false",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Browse, cancellationToken).ConfigureAwait(false)).Items;
    }

    public async Task<List<EmbyItem>> GetSeasonsAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Shows/{EscapePath(seriesId)}/Seasons", new Dictionary<string, string?>
        {
            ["UserId"] = UserId,
            ["Fields"] = EpisodeFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });

        try
        {
            return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Episodes, cancellationToken).ConfigureAwait(false)).Items;
        }
        catch (EmbyApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return (await GetItemsAsync(seriesId, cancellationToken).ConfigureAwait(false))
                .Where(item => string.Equals(item.Type, "Season", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.IndexNumber ?? int.MaxValue)
                .ThenBy(item => item.Name)
                .ToList();
        }
    }

    public async Task<List<EmbyItem>> GetEpisodesAsync(string seriesId, string? seasonId = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        try
        {
            const int pageSize = 50;
            var all = new List<EmbyItem>();
            var startIndex = 0;
            var total = int.MaxValue;

            // Keep every Emby response bounded. Large/long-running series should not allocate a
            // single giant JSON document or stall the player drawer behind hundreds of episodes.
            while (startIndex < total)
            {
                var endpoint = AddQuery($"Shows/{EscapePath(seriesId)}/Episodes", new Dictionary<string, string?>
                {
                    ["UserId"] = UserId,
                    ["SeasonId"] = seasonId,
                    ["StartIndex"] = startIndex.ToString(),
                    ["Limit"] = pageSize.ToString(),
                    ["Fields"] = EpisodeFields,
                    ["EnableImages"] = "true",
                    ["ImageTypeLimit"] = "1"
                });

                var page = await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Episodes, cancellationToken).ConfigureAwait(false);
                all.AddRange(page.Items);
                if (page.TotalRecordCount > 0) total = page.TotalRecordCount;
                if (page.Items.Count == 0 || page.Items.Count < pageSize) break;
                startIndex += page.Items.Count;
            }

            return all
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(item => item.IndexNumber ?? int.MaxValue)
                .ToList();
        }
        catch (EmbyApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            if (string.IsNullOrWhiteSpace(seasonId)) return [];
            return (await GetItemsAsync(seasonId, cancellationToken).ConfigureAwait(false))
                .Where(item => string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.IndexNumber ?? int.MaxValue)
                .ToList();
        }
    }

    public async Task<List<EmbyItem>> GetResumeItemsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items/Resume", new Dictionary<string, string?>
        {
            ["Recursive"] = "true",
            ["MediaTypes"] = "Video",
            ["Limit"] = "30",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });

        try
        {
            return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Resume, cancellationToken).ConfigureAwait(false)).Items;
        }
        catch (EmbyApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
            {
                ["Recursive"] = "true",
                ["Filters"] = "IsResumable",
                ["IncludeItemTypes"] = "Movie,Episode,Video",
                ["SortBy"] = "DatePlayed",
                ["SortOrder"] = "Descending",
                ["Limit"] = "30",
                ["Fields"] = ListFields,
                ["EnableImages"] = "true",
                ["ImageTypeLimit"] = "1"
            });
            return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Resume, cancellationToken).ConfigureAwait(false)).Items;
        }
    }

    public async Task<List<EmbyItem>> GetLatestAsync(string includeItemTypes, string? parentId = null, int limit = 24, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items/Latest", new Dictionary<string, string?>
        {
            ["ParentId"] = parentId,
            ["IncludeItemTypes"] = includeItemTypes,
            ["Limit"] = limit.ToString(),
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1",
            ["GroupItems"] = "false"
        });
        return await GetCachedJsonAsync<List<EmbyItem>>(endpoint, CachePolicy.Latest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<EmbyItem>> GetFavoriteItemsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["Recursive"] = "true",
            ["Filters"] = "IsFavorite",
            ["IncludeItemTypes"] = "Movie,Series,Season,Episode,Video,BoxSet",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Limit"] = "300",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Favorites, cancellationToken).ConfigureAwait(false)).Items;
    }

    public async Task<List<EmbyItem>> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["SearchTerm"] = term.Trim(),
            ["Recursive"] = "true",
            ["IncludeItemTypes"] = "Movie,Series,Season,Episode,Video,BoxSet",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Limit"] = "150",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Search, cancellationToken).ConfigureAwait(false)).Items;
    }

    public Task<EmbyItem> GetItemAsync(string itemId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return GetCachedJsonAsync<EmbyItem>($"Users/{EscapePath(UserId!)}/Items/{EscapePath(itemId)}", CachePolicy.Detail, cancellationToken);
    }

    public async Task<List<EmbyItem>> GetSimilarItemsAsync(string itemId, int limit = 12, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Items/{EscapePath(itemId)}/Similar", new Dictionary<string, string?>
        {
            ["UserId"] = UserId,
            ["Limit"] = Math.Clamp(limit, 1, 30).ToString(),
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Similar, cancellationToken).ConfigureAwait(false)).Items;
    }

    public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync(string itemId, long startTimeTicks = 0, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        startTimeTicks = Math.Max(0, startTimeTicks);
        var endpoint = AddQuery($"Items/{EscapePath(itemId)}/PlaybackInfo", new Dictionary<string, string?>
        {
            ["UserId"] = UserId,
            ["StartTimeTicks"] = startTimeTicks.ToString(),
            ["IsPlayback"] = "true",
            ["AutoOpenLiveStream"] = "true"
        });

        using var request = CreateRequest(HttpMethod.Post, endpoint, true);
        request.Content = JsonContent.Create(new
        {
            UserId,
            DeviceProfile = CreateDeviceProfile(),
            MaxStreamingBitrate = _maxStreamingBitrate
        }, options: JsonOptions);

        try
        {
            using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
            return await ReadJsonResponseAsync<PlaybackInfoResponse>(response, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbyApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return await GetJsonAsync<PlaybackInfoResponse>(endpoint, true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PlaybackPlan> CreatePlaybackPlanAsync(EmbyItem listItem, string? mediaSourceId = null, CancellationToken cancellationToken = default)
    {
        var item = listItem.MediaSources is { Count: > 0 } ? listItem : await GetItemAsync(listItem.Id, cancellationToken).ConfigureAwait(false);
        var resumePositionTicks = Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0);
        var playbackInfo = await GetPlaybackInfoAsync(item.Id, resumePositionTicks, cancellationToken).ConfigureAwait(false);
        return CreatePlaybackPlan(item, playbackInfo, mediaSourceId);
    }

    /// <summary>
    /// Builds a plan from an already negotiated PlaybackInfo response. This is intentionally
    /// separate from the network call so the detail page can pre-negotiate playback while the
    /// user is reading metadata and make the Play click effectively instant.
    /// </summary>
    public PlaybackPlan CreatePlaybackPlan(EmbyItem item, PlaybackInfoResponse playbackInfo, string? mediaSourceId = null)
    {
        var resumePositionTicks = Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0);
        var sources = playbackInfo.MediaSources.Count > 0 ? playbackInfo.MediaSources : item.MediaSources ?? [];
        var source = sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.Ordinal))
                     ?? sources.FirstOrDefault()
                     ?? throw new EmbyApiException("该项目没有可播放的媒体源。");
        var playSessionId = string.IsNullOrWhiteSpace(playbackInfo.PlaySessionId) ? Guid.NewGuid().ToString("N") : playbackInfo.PlaySessionId!;
        var directUri = BuildDirectUri(item, source, playSessionId);
        var transcodeUri = BuildTranscodeUri(item, source, playSessionId, resumePositionTicks);
        var transcodingSupported = source.SupportsTranscoding || !string.IsNullOrWhiteSpace(source.TranscodingUrl) || transcodeUri is not null;

        return new PlaybackPlan(
            item,
            source,
            playSessionId,
            directUri,
            transcodeUri,
            resumePositionTicks,
            source.SupportsDirectPlay || source.SupportsDirectStream || !string.IsNullOrWhiteSpace(source.DirectStreamUrl),
            transcodingSupported,
            sources.ToList());
    }

    public Uri BuildTranscodeUri(PlaybackPlan plan, long startTimeTicks) =>
        BuildTranscodeUri(plan.Item, plan.MediaSource, plan.PlaySessionId, startTimeTicks)
        ?? throw new EmbyApiException("服务器没有提供可用的转码地址。");

    public Uri ResolveMediaUri(string url, bool addApiKeyIfMissing = true)
    {
        EnsureAuthenticated();
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("媒体地址不能为空。", nameof(url));

        var resolved = url.Trim();
        if (addApiKeyIfMissing && !resolved.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
        {
            var isAbsolute = Uri.TryCreate(resolved, UriKind.Absolute, out var absolute);
            if (!isAbsolute || string.Equals(absolute!.Host, ServerBaseUri.Host, StringComparison.OrdinalIgnoreCase))
                resolved = AddQuery(resolved, new Dictionary<string, string?> { ["api_key"] = AccessToken });
        }
        return MakeUri(resolved);
    }

    public async Task<byte[]> DownloadImageAsync(string itemId, string imageType, string? tag, int maxWidth, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Items/{EscapePath(itemId)}/Images/{EscapePath(imageType)}", new Dictionary<string, string?>
        {
            ["tag"] = tag,
            ["maxWidth"] = maxWidth.ToString(),
            ["quality"] = "82"
        });
        using var request = CreateRequest(HttpMethod.Get, endpoint, true);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await SendAsync(request, true, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ReportPlaybackStartedAsync(PlaybackReport report, CancellationToken cancellationToken = default) =>
        PostPlaybackReportAsync("Sessions/Playing", report, cancellationToken);

    public Task ReportPlaybackProgressAsync(PlaybackReport report, CancellationToken cancellationToken = default) =>
        PostPlaybackReportAsync("Sessions/Playing/Progress", report, cancellationToken);

    public Task ReportPlaybackStoppedAsync(PlaybackReport report, CancellationToken cancellationToken = default) =>
        PostPlaybackReportAsync("Sessions/Playing/Stopped", report, cancellationToken);

    public async Task StopActiveEncodingsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AccessToken)) return;
        using var request = CreateRequest(HttpMethod.Delete, "Videos/ActiveEncodings", true);
        using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AccessToken)) return;
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "Sessions/Logout", true);
            using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Warning("Server logout request failed.", ex);
        }
    }

    private async Task PostPlaybackReportAsync(string endpoint, PlaybackReport report, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        var payload = new
        {
            report.ItemId,
            report.MediaSourceId,
            report.PlaySessionId,
            report.PositionTicks,
            IsPaused = report.IsPaused,
            IsMuted = report.IsMuted,
            report.VolumeLevel,
            PlayMethod = report.Method.ToString(),
            EventName = report.EventName,
            AudioStreamIndex = report.AudioStreamIndex,
            SubtitleStreamIndex = report.SubtitleStreamIndex,
            CanSeek = true,
            QueueableMediaTypes = new[] { "Video" }
        };
        using var request = CreateRequest(HttpMethod.Post, endpoint, true);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private Uri BuildDirectUri(EmbyItem item, MediaSourceInfo source, string playSessionId)
    {
        EnsureAuthenticated();
        if (!string.IsNullOrWhiteSpace(source.DirectStreamUrl))
        {
            var direct = source.DirectStreamUrl!;
            var isAbsolute = Uri.TryCreate(direct, UriKind.Absolute, out _);
            if (!direct.Contains("api_key=", StringComparison.OrdinalIgnoreCase)
                && (source.AddApiKeyToDirectStreamUrl || !isAbsolute))
            {
                direct = AddQuery(direct, new Dictionary<string, string?> { ["api_key"] = AccessToken });
            }
            return MakeUri(direct);
        }

        var extension = string.IsNullOrWhiteSpace(source.Container) ? "mkv" : source.Container!.Split(',')[0];
        var endpoint = AddQuery($"Videos/{EscapePath(item.Id)}/stream.{EscapePath(extension)}", new Dictionary<string, string?>
        {
            ["Static"] = "true",
            ["MediaSourceId"] = source.Id ?? item.Id,
            ["DeviceId"] = DeviceId,
            ["PlaySessionId"] = playSessionId,
            ["api_key"] = AccessToken
        });
        return MakeUri(endpoint);
    }

    private Uri? BuildTranscodeUri(EmbyItem item, MediaSourceInfo source, string playSessionId, long startTimeTicks)
    {
        EnsureAuthenticated();
        if (!string.IsNullOrWhiteSpace(source.TranscodingUrl))
        {
            var url = source.TranscodingUrl!;
            if (startTimeTicks > 0) url = SetQueryValue(url, "StartTimeTicks", startTimeTicks.ToString());
            if (!url.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
                url = AddQuery(url, new Dictionary<string, string?> { ["api_key"] = AccessToken });
            return MakeUri(url);
        }

        var endpoint = AddQuery($"Videos/{EscapePath(item.Id)}/master.m3u8", new Dictionary<string, string?>
        {
            ["MediaSourceId"] = source.Id ?? item.Id,
            ["DeviceId"] = DeviceId,
            ["PlaySessionId"] = playSessionId,
            ["Container"] = "ts",
            ["VideoCodec"] = "h264",
            ["AudioCodec"] = "aac",
            ["VideoBitRate"] = _maxStreamingBitrate.ToString(),
            ["AudioBitRate"] = "320000",
            ["MaxAudioChannels"] = "8",
            ["MaxWidth"] = "3840",
            ["MaxHeight"] = "2160",
            ["EnableAutoStreamCopy"] = "true",
            ["AllowVideoStreamCopy"] = "true",
            ["AllowAudioStreamCopy"] = "true",
            ["StartTimeTicks"] = startTimeTicks > 0 ? startTimeTicks.ToString() : null,
            ["api_key"] = AccessToken
        });
        return MakeUri(endpoint);
    }

    private DeviceProfile CreateDeviceProfile() => new()
    {
        MaxStreamingBitrate = _maxStreamingBitrate,
        MaxStaticBitrate = 120_000_000,
        DirectPlayProfiles =
        [
            new() { Container = "mkv,mp4,m4v,mov,webm,avi,ts,m2ts,mpg,mpeg,wmv,flv,ogm,ogg", Type = "Video" },
            new() { Container = "mp3,aac,m4a,flac,ogg,oga,wav,wma,opus", Type = "Audio" }
        ],
        TranscodingProfiles =
        [
            new() { Container = "ts", Type = "Video", VideoCodec = "h264", AudioCodec = "aac,mp3,ac3,eac3", Protocol = "hls", MaxAudioChannels = 8 }
        ],
        SubtitleProfiles =
        [
            new() { Format = "srt", Method = "External" },
            new() { Format = "ass", Method = "External" },
            new() { Format = "ssa", Method = "External" },
            new() { Format = "vtt", Method = "External" },
            new() { Format = "sub", Method = "Embed" },
            new() { Format = "pgs", Method = "Embed" },
            new() { Format = "dvdsub", Method = "Embed" }
        ]
    };

    public void InvalidatePlaybackDerivedMetadata(string itemId)
    {
        if (_responseCache is null || string.IsNullOrWhiteSpace(UserId)) return;

        if (!string.IsNullOrWhiteSpace(itemId))
            _responseCache.MarkStale($"Users/{EscapePath(UserId!)}/Items/{EscapePath(itemId)}");

        var resumeEndpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items/Resume", new Dictionary<string, string?>
        {
            ["Recursive"] = "true", ["MediaTypes"] = "Video", ["Limit"] = "30",
            ["Fields"] = ListFields, ["EnableImages"] = "true", ["ImageTypeLimit"] = "1"
        });
        _responseCache.MarkStale(resumeEndpoint);

        var resumeFallback = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["Recursive"] = "true", ["Filters"] = "IsResumable",
            ["IncludeItemTypes"] = "Movie,Episode,Video", ["SortBy"] = "DatePlayed",
            ["SortOrder"] = "Descending", ["Limit"] = "30", ["Fields"] = ListFields,
            ["EnableImages"] = "true", ["ImageTypeLimit"] = "1"
        });
        _responseCache.MarkStale(resumeFallback);
    }

    private void HandleServerEvent(EmbyServerEvent message)
    {
        var cache = _responseCache;
        if (_disposed || cache is null || string.IsNullOrWhiteSpace(UserId)) return;

        try
        {
            switch (message.MessageType)
            {
                case "LibraryChanged":
                    // Keep the old snapshot available. The next reader renders it immediately and
                    // revalidates in the background instead of showing a loading screen.
                    cache.MarkAllStale();
                    PrefetchHome();
                    break;

                case "UserDataChanged":
                    if (message.Data.ValueKind == JsonValueKind.Object &&
                        message.Data.TryGetProperty("UserId", out var changedUser) &&
                        changedUser.ValueKind == JsonValueKind.String &&
                        !string.Equals(changedUser.GetString(), UserId, StringComparison.OrdinalIgnoreCase))
                        return;

                    MarkResumeMetadataStale(cache);
                    MarkFavoriteMetadataStale(cache);
                    foreach (var itemId in ExtractChangedItemIds(message.Data))
                        cache.MarkStale($"Users/{EscapePath(UserId!)}/Items/{EscapePath(itemId)}");
                    break;

                case "UserUpdated":
                    cache.MarkStale($"Users/{EscapePath(UserId!)}");
                    CurrentUser = null;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Push invalidation must never be able to break playback or navigation.
            LogService.Warning($"Unable to process Emby websocket message {message.MessageType}.", ex);
        }
    }

    private void MarkResumeMetadataStale(ApiResponseCacheService cache)
    {
        var resumeEndpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items/Resume", new Dictionary<string, string?>
        {
            ["Recursive"] = "true", ["MediaTypes"] = "Video", ["Limit"] = "30",
            ["Fields"] = ListFields, ["EnableImages"] = "true", ["ImageTypeLimit"] = "1"
        });
        cache.MarkStale(resumeEndpoint);

        var resumeFallback = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["Recursive"] = "true", ["Filters"] = "IsResumable",
            ["IncludeItemTypes"] = "Movie,Episode,Video", ["SortBy"] = "DatePlayed",
            ["SortOrder"] = "Descending", ["Limit"] = "30", ["Fields"] = ListFields,
            ["EnableImages"] = "true", ["ImageTypeLimit"] = "1"
        });
        cache.MarkStale(resumeFallback);
    }

    private void MarkFavoriteMetadataStale(ApiResponseCacheService cache)
    {
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["Recursive"] = "true", ["Filters"] = "IsFavorite",
            ["IncludeItemTypes"] = "Movie,Series,Season,Episode,Video,BoxSet",
            ["SortBy"] = "SortName", ["SortOrder"] = "Ascending", ["Limit"] = "300",
            ["Fields"] = ListFields, ["EnableImages"] = "true", ["ImageTypeLimit"] = "1"
        });
        cache.MarkStale(endpoint);
    }

    private static IEnumerable<string> ExtractChangedItemIds(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("UserDataList", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ItemId", out var id)) continue;
            var value = id.GetString();
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }

    public void PrefetchItem(string itemId)
    {
        if (_responseCache is null || string.IsNullOrWhiteSpace(itemId)) return;
        var endpoint = $"Users/{EscapePath(UserId!)}/Items/{EscapePath(itemId)}";
        _responseCache.Warm(endpoint, CachePolicy.Detail.FreshFor, CachePolicy.Detail.ServeStaleFor,
            token => GetJsonCoreAsync<EmbyItem>(endpoint, true, token));
    }

    public void PrefetchChildren(string parentId)
    {
        if (_responseCache is null || string.IsNullOrWhiteSpace(parentId)) return;
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["ParentId"] = parentId,
            ["Recursive"] = "false",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        _responseCache.Warm(endpoint, CachePolicy.Browse.FreshFor, CachePolicy.Browse.ServeStaleFor,
            token => GetJsonCoreAsync<ItemsResult>(endpoint, true, token));
    }

    public void PrefetchHome()
    {
        if (_responseCache is null || string.IsNullOrWhiteSpace(UserId)) return;
        _responseCache.Warm($"Users/{EscapePath(UserId!)}/Views", CachePolicy.Library.FreshFor, CachePolicy.Library.ServeStaleFor,
            token => GetJsonCoreAsync<ItemsResult>($"Users/{EscapePath(UserId!)}/Views", true, token));

        var resumeEndpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items/Resume", new Dictionary<string, string?>
        {
            ["Recursive"] = "true", ["MediaTypes"] = "Video", ["Limit"] = "30",
            ["Fields"] = ListFields, ["EnableImages"] = "true", ["ImageTypeLimit"] = "1"
        });
        _responseCache.Warm(resumeEndpoint, CachePolicy.Resume.FreshFor, CachePolicy.Resume.ServeStaleFor,
            token => GetJsonCoreAsync<ItemsResult>(resumeEndpoint, true, token));

        foreach (var type in new[] { "Movie", "Episode" })
        {
            var latestEndpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items/Latest", new Dictionary<string, string?>
            {
                ["IncludeItemTypes"] = type, ["Limit"] = "24", ["Fields"] = ListFields,
                ["EnableImages"] = "true", ["ImageTypeLimit"] = "1", ["GroupItems"] = "false"
            });
            _responseCache.Warm(latestEndpoint, CachePolicy.Latest.FreshFor, CachePolicy.Latest.ServeStaleFor,
                token => GetJsonCoreAsync<List<EmbyItem>>(latestEndpoint, true, token));
        }
    }

    private Task<T> GetCachedJsonAsync<T>(string endpoint, CachePolicy policy, CancellationToken cancellationToken)
    {
        if (_responseCache is null)
            return GetJsonCoreAsync<T>(endpoint, true, cancellationToken);
        return _responseCache.GetAsync(endpoint, policy.FreshFor, policy.ServeStaleFor,
            token => GetJsonCoreAsync<T>(endpoint, true, token), cancellationToken);
    }

    private Task<T> GetJsonAsync<T>(string endpoint, bool includeToken, CancellationToken cancellationToken) =>
        GetJsonCoreAsync<T>(endpoint, includeToken, cancellationToken);

    private async Task<T> GetJsonCoreAsync<T>(string endpoint, bool includeToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, endpoint, includeToken);
        using var response = await SendAsync(request, true, cancellationToken).ConfigureAwait(false);
        return await ReadJsonResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool retryable, CancellationToken cancellationToken)
    {
        using var snapshot = retryable ? await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false) : null;
        var requestId = NetworkDebugService.NextRequestId();
        for (var attempt = 0; ; attempt++)
        {
            HttpRequestMessage current = attempt == 0
                ? request
                : await CloneRequestAsync(snapshot!, cancellationToken).ConfigureAwait(false);
            var disposeCurrent = attempt > 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await NetworkDebugService.LogRequestAsync(current, requestId, attempt + 1, cancellationToken).ConfigureAwait(false);
            try
            {
                var response = await _httpClient.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                await NetworkDebugService.LogResponseAsync(response, requestId, attempt + 1, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                if (!retryable || attempt >= 2 || !IsTransient(response.StatusCode))
                    return response;
                response.Dispose();
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                NetworkDebugService.LogFailure(requestId, attempt + 1, stopwatch.Elapsed, ex);
                if (!retryable || attempt >= 2)
                    throw new EmbyApiException("连接服务器超时，请检查地址、网络或反向代理。", innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                NetworkDebugService.LogFailure(requestId, attempt + 1, stopwatch.Elapsed, ex);
                if (!retryable || attempt >= 2)
                    throw new EmbyApiException("无法连接 Emby 服务器：" + ex.Message, innerException: ex);
            }
            finally
            {
                if (disposeCurrent) current.Dispose();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)code >= 500;

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri) { Version = source.Version, VersionPolicy = source.VersionPolicy };
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (source.Content is not null)
        {
            var bytes = await source.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in source.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, bool includeToken)
    {
        var request = new HttpRequestMessage(method, MakeUri(endpoint))
        {
            // HttpRequestMessage defaults to HTTP/1.1. Set this explicitly because most calls
            // use SendAsync(request), where HttpClient.DefaultRequestVersion is not enough to
            // guarantee HTTP/2 negotiation for a manually-created request.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        var auth = $"MediaBrowser Client=\"{ClientName}\", Device=\"Windows\", DeviceId=\"{DeviceId}\", Version=\"{ClientVersion}\"";
        if (includeToken)
        {
            EnsureAuthenticated();
            auth += $", Token=\"{AccessToken}\"";
        }
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", auth);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        if (includeToken)
        {
            request.Headers.TryAddWithoutValidation("X-Emby-Token", AccessToken);
            request.Headers.TryAddWithoutValidation("X-MediaBrowser-Token", AccessToken);
        }
        return request;
    }

    private async Task<T> ReadJsonResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new EmbyApiException("服务器返回了空数据。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var text = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "认证失败或登录已过期。",
            HttpStatusCode.Forbidden => "当前账号没有执行此操作的权限。",
            HttpStatusCode.NotFound => "服务器接口或媒体不存在。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。",
            _ => $"服务器请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
        };
        if (!string.IsNullOrWhiteSpace(body) && body.Length < 600) text += $"\n{body}";
        throw new EmbyApiException(text, response.StatusCode);
    }

    private Uri MakeUri(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute)) return absolute;
        if (endpoint.StartsWith("/", StringComparison.Ordinal))
        {
            var origin = new Uri(ServerBaseUri.GetLeftPart(UriPartial.Authority) + "/");
            return new Uri(origin, endpoint.TrimStart('/'));
        }
        return new Uri(ServerBaseUri, endpoint);
    }

    private static Uri NormalizeServerUri(string raw)
    {
        var value = raw.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("服务器地址无效，请填写 http:// 或 https:// 地址。", nameof(raw));
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    private static string AddQuery(string endpoint, IReadOnlyDictionary<string, string?> values)
    {
        var parts = values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        var query = string.Join("&", parts);
        if (string.IsNullOrWhiteSpace(query)) return endpoint;
        return endpoint + (endpoint.Contains('?') ? "&" : "?") + query;
    }

    private static string SetQueryValue(string url, string key, string value)
    {
        var pattern = $@"(?<=[?&]){Regex.Escape(key)}=[^&]*";
        var replacement = $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase)
            ? Regex.Replace(url, pattern, replacement, RegexOptions.IgnoreCase)
            : AddQuery(url, new Dictionary<string, string?> { [key] = value });
    }

    private static string EscapePath(string value) => Uri.EscapeDataString(value);

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(AccessToken) || string.IsNullOrWhiteSpace(UserId))
            throw new InvalidOperationException("尚未登录 Emby 服务器。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_eventSocket is not null) _eventSocket.MessageReceived -= HandleServerEvent;
        _eventSocket?.Dispose();
        _eventSocket = null;
        _responseCache?.Dispose();
        _responseCache = null;
        _httpClient.Dispose();
    }

    private sealed record CachePolicy(TimeSpan FreshFor, TimeSpan ServeStaleFor)
    {
        // WebSocket push is the primary freshness signal. These windows are deliberately generous so
        // normal page switching does not poll the server; they remain a fallback when push is unavailable.
        public static readonly CachePolicy User = new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(4));
        public static readonly CachePolicy Resume = new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        public static readonly CachePolicy Latest = new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(8));
        public static readonly CachePolicy Library = new(TimeSpan.FromHours(1), TimeSpan.FromDays(2));
        public static readonly CachePolicy Browse = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(24));
        public static readonly CachePolicy Episodes = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12));
        public static readonly CachePolicy Favorites = new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(4));
        public static readonly CachePolicy Search = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
        public static readonly CachePolicy Detail = new(TimeSpan.FromMinutes(30), TimeSpan.FromDays(2));
        public static readonly CachePolicy Similar = new(TimeSpan.FromHours(2), TimeSpan.FromDays(2));
    }

    // List/card requests deliberately avoid MediaSources and MediaStreams.
    // Playback stream metadata is large and is fetched only when a detail/play action actually needs it.
    private const string ListFields = "PrimaryImageAspectRatio,Genres,RunTimeTicks,UserData,ChildCount,DateCreated,ProductionYear,CommunityRating";
    private const string EpisodeFields = "PrimaryImageAspectRatio,RunTimeTicks,UserData,ProductionYear,DateCreated,SeriesId,SeriesName,SeasonId,SeasonName,ParentIndexNumber,IndexNumber";
}

public sealed class EmbyApiException : Exception
{
    public EmbyApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}
