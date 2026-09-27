using System.IO.Compression;
using System.Text.RegularExpressions;
using EmbyRemotePlayer.Models;

namespace EmbyRemotePlayer.Services;

public sealed class EmbyApiClient : IDisposable
{
    public const string ClientName = "Emby Remote Player";
    public const string ClientVersion = "0.9.0.1";
    public const int DefaultHomeRowLimit = 24;

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

        // Hijack-proxy Emby servers (LongEmby family) expose every unscraped movie as BOTH a
        // real Movie item and a raw "Folder" shell whose Children endpoint answers 404. A flat
        // Recursive=false listing therefore floods the grid with unplayable shells, and clicking
        // one dies at PlaybackInfo (410 Gone) or browse (404). A recursive typed query returns
        // the real entities directly: Movie,Series keeps movie libraries flat and TV libraries
        // structured (Series rows drill into Seasons/Episodes afterwards).
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["ParentId"] = parentId,
            ["Recursive"] = "true",
            ["IncludeItemTypes"] = "Movie,Series,MusicVideo,Trailer",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        var items = (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Browse, cancellationToken).ConfigureAwait(false)).Items;
        if (items.Count > 0 || parentId is null) return items;

        // Plain Folder children (scraped series libraries) may legitimately return zero typed
        // entries; fall back to the unfiltered listing so Season/Folder structures survive.
        var fallback = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["ParentId"] = parentId,
            ["Recursive"] = "false",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(fallback, CachePolicy.Browse, cancellationToken).ConfigureAwait(false)).Items;
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
            ["IncludeItemTypes"] = "Movie,Series,Season,Episode,Video,BoxSet,Person",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Limit"] = "150",
            ["Fields"] = ListFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Search, cancellationToken).ConfigureAwait(false)).Items;
    }

    public async Task<List<EmbyItem>> GetPersonItemsAsync(string personId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["PersonIds"] = personId,
            ["Recursive"] = "true",
            ["IncludeItemTypes"] = "Movie,Series,Episode,Video",
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

    /// <summary>
    /// Direct-playable videos beneath any container, in server-recommended order. Used to
    /// resolve Folder shells to real movies on hijack proxies whose Children endpoints 404.
    /// </summary>
    public async Task<List<EmbyItem>> GetPlayableChildrenAsync(string parentId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["ParentId"] = parentId,
            ["Recursive"] = "true",
            ["IncludeItemTypes"] = "Movie,Episode,Video,MusicVideo,Trailer",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending",
            ["Limit"] = "10",
            ["Fields"] = ListFields + ",MediaSources",
            ["EnableImages"] = "false"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Browse, cancellationToken).ConfigureAwait(false)).Items;
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

    /// <summary>Episodes the user should watch next (finished S01E03 → suggests S01E04).</summary>
    public async Task<List<EmbyItem>> GetNextUpAsync(int limit = DefaultHomeRowLimit, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var clampedLimit = Math.Clamp(limit, 1, 50);
        return (await GetCachedJsonAsync<ItemsResult>(BuildNextUpEndpoint(clampedLimit), CachePolicy.NextUp, cancellationToken).ConfigureAwait(false)).Items;
    }

    private string BuildNextUpEndpoint(int limit) => AddQuery("Shows/NextUp", new Dictionary<string, string?>
    {
        ["UserId"] = UserId,
        ["Limit"] = limit.ToString(),
        ["Fields"] = EpisodeFields + ",Overview",
        ["EnableImages"] = "true",
        ["ImageTypeLimit"] = "1"
    });

    public async Task<List<EmbyItem>> GetSuggestionsAsync(int limit = 16, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var clampedLimit = Math.Clamp(limit, 1, 40);
        return (await GetCachedJsonAsync<ItemsResult>(BuildSuggestionsEndpoint(clampedLimit), CachePolicy.Similar, cancellationToken).ConfigureAwait(false)).Items;
    }

    private string BuildSuggestionsEndpoint(int limit) => AddQuery($"Users/{EscapePath(UserId!)}/Suggestions", new Dictionary<string, string?>
    {
        ["IncludeItemTypes"] = "Movie,Series",
        ["Limit"] = limit.ToString(),
        ["Fields"] = HomeFields,
        ["EnableImages"] = "true",
        ["ImageTypeLimit"] = "1"
    });

    public async Task<List<EmbyItem>> GetTopRatedAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var clampedLimit = Math.Clamp(limit, 1, 50);
        return (await GetCachedJsonAsync<ItemsResult>(BuildTopRatedEndpoint(clampedLimit), CachePolicy.Browse, cancellationToken).ConfigureAwait(false)).Items;
    }

    private string BuildTopRatedEndpoint(int limit) => AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
    {
        ["Recursive"] = "true",
        ["IncludeItemTypes"] = "Movie,Series",
        ["SortBy"] = "CommunityRating",
        ["SortOrder"] = "Descending",
        ["MinCommunityRating"] = "7",
        ["Limit"] = limit.ToString(),
        ["Fields"] = HomeFields,
        ["EnableImages"] = "true",
        ["ImageTypeLimit"] = "1"
    });

    public async Task<List<EmbyItem>> GetGenresAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var endpoint = AddQuery("Genres", new Dictionary<string, string?>
        {
            ["UserId"] = UserId,
            ["Recursive"] = "true",
            ["IncludeItemTypes"] = "Movie,Series",
            ["SortBy"] = "SortName",
            ["SortOrder"] = "Ascending"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Library, cancellationToken).ConfigureAwait(false)).Items;
    }

    public async Task<List<EmbyItem>> GetItemsByGenreAsync(string genreName, int limit = 20, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var clampedLimit = Math.Clamp(limit, 1, 50);
        var endpoint = AddQuery($"Users/{EscapePath(UserId!)}/Items", new Dictionary<string, string?>
        {
            ["Genres"] = genreName,
            ["Recursive"] = "true",
            ["IncludeItemTypes"] = "Movie,Series",
            ["SortBy"] = "CommunityRating",
            ["SortOrder"] = "Descending",
            ["Limit"] = clampedLimit.ToString(),
            ["Fields"] = HomeFields,
            ["EnableImages"] = "true",
            ["ImageTypeLimit"] = "1"
        });
        return (await GetCachedJsonAsync<ItemsResult>(endpoint, CachePolicy.Browse, cancellationToken).ConfigureAwait(false)).Items;
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
        catch (EmbyApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest
                                                or HttpStatusCode.NotFound
                                                or HttpStatusCode.MethodNotAllowed
                                                or HttpStatusCode.Gone)
        {
            // A number of Emby-compatible reverse proxies only hijack the legacy GET
            // PlaybackInfo request. Their POST endpoint deliberately answers 410 Gone
            // ("hijack playback required"), so retry the same negotiation as GET.
            try
            {
                return await GetJsonAsync<PlaybackInfoResponse>(endpoint, true, cancellationToken).ConfigureAwait(false);
            }
            catch (EmbyApiException fallbackError) when (fallbackError.StatusCode is HttpStatusCode.BadRequest
                                                               or HttpStatusCode.NotFound
                                                               or HttpStatusCode.MethodNotAllowed
                                                               or HttpStatusCode.Gone)
            {
                // Some proxies reject both PlaybackInfo shapes even though the hydrated
                // item already contains fully usable MediaSources. Preserve direct play
                // instead of failing before LibVLC receives a URL.
                var item = await GetItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                if (item.MediaSources is not { Count: > 0 }) throw;

                LogService.Warning($"PlaybackInfo negotiation was rejected for {itemId}; using media sources from item metadata.", fallbackError);
                return new PlaybackInfoResponse
                {
                    MediaSources = item.MediaSources,
                    PlaySessionId = Guid.NewGuid().ToString("N")
                };
            }
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

    /// <summary>
    /// Reads only a small, bounded portion of a media endpoint. Keeping this check outside
    /// LibVLC lets the UI distinguish a rejected/empty server stream from a local decoder error.
    /// </summary>
    public async Task<MediaProbeResult> ProbeMediaUriAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? requiredHeaders = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));

        try
        {
            return await ProbeMediaUriCoreAsync(uri, requiredHeaders, 0, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MediaProbeResult(false, null, "媒体服务器响应超时", "连接或上游片源在 9 秒内没有返回媒体数据。", true);
        }
        catch (HttpRequestException ex)
        {
            return new MediaProbeResult(false, null, "无法连接媒体地址", ex.Message, true);
        }
        catch (Exception ex)
        {
            return new MediaProbeResult(false, null, "媒体地址检查失败", ex.Message, false);
        }
    }

    private async Task<MediaProbeResult> ProbeMediaUriCoreAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? requiredHeaders,
        int playlistDepth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 131071);
        if (string.Equals(uri.Host, ServerBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            var auth = $"MediaBrowser Client=\"{ClientName}\", Device=\"Windows\", DeviceId=\"{DeviceId}\", Version=\"{ClientVersion}\", Token=\"{AccessToken}\"";
            request.Headers.TryAddWithoutValidation("X-Emby-Authorization", auth);
            request.Headers.TryAddWithoutValidation("Authorization", auth);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", AccessToken);
            request.Headers.TryAddWithoutValidation("X-MediaBrowser-Token", AccessToken);
        }
        ApplyRequiredProbeHeaders(request, requiredHeaders);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var bytes = await ReadAtMostAsync(response.Content, 131072, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadText(bytes, contentType);
            if (string.IsNullOrWhiteSpace(detail)) detail = response.ReasonPhrase ?? "服务器拒绝了媒体请求。";
            return new MediaProbeResult(false, statusCode, $"服务器返回 HTTP {statusCode}", detail, true);
        }

        var looksLikePlaylist = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                                || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
        if (looksLikePlaylist)
        {
            var playlist = Encoding.UTF8.GetString(bytes);
            if (!playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                return new MediaProbeResult(false, statusCode, "服务器返回的不是有效 HLS 清单",
                    CompactResponseText(playlist), true);
            }

            var child = playlist.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !line.StartsWith('#') && !string.IsNullOrWhiteSpace(line));
            if (string.IsNullOrWhiteSpace(child))
            {
                return new MediaProbeResult(false, statusCode, "HLS 清单没有媒体分片",
                    "Emby 已返回播放清单，但清单中没有可读取的视频轨或分片。", true);
            }

            if (playlistDepth >= 3)
                return new MediaProbeResult(false, statusCode, "HLS 清单层级异常", "播放清单连续嵌套且没有找到媒体分片。", true);

            var childUri = new Uri(uri, child);
            var childResult = await ProbeMediaUriCoreAsync(childUri, requiredHeaders, playlistDepth + 1, cancellationToken).ConfigureAwait(false);
            return childResult.Success
                ? childResult with { Summary = "服务器已返回 HLS 媒体分片" }
                : childResult;
        }

        if (bytes.Length == 0)
            return new MediaProbeResult(false, statusCode, "媒体响应为空", "服务器返回成功状态，但没有返回任何媒体字节。", true);

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaProbeResult(false, statusCode, "媒体地址返回了文本错误页",
                CompactResponseText(Encoding.UTF8.GetString(bytes)), true);
        }

        return new MediaProbeResult(true, statusCode, $"服务器媒体数据正常（HTTP {statusCode}）",
            $"已读取 {bytes.Length / 1024d:0.#} KB 媒体数据。", false);
    }

    private static void ApplyRequiredProbeHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(limit, 32768));
        var block = new byte[16384];
        while (buffer.Length < limit)
        {
            var wanted = (int)Math.Min(block.Length, limit - buffer.Length);
            var read = await stream.ReadAsync(block.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static string TryReadText(byte[] bytes, string contentType)
    {
        if (bytes.Length == 0) return string.Empty;
        if (!contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return CompactResponseText(Encoding.UTF8.GetString(bytes));
    }

    private static string CompactResponseText(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        if (compact.Length > 260) compact = compact[..260] + "…";
        return compact;
    }

    public async Task<byte[]> DownloadImageAsync(string itemId, string imageType, string? tag, int maxWidth, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var baseEndpoint = $"Items/{EscapePath(itemId)}/Images/{EscapePath(imageType)}";
        var endpoints = new List<string>
        {
            AddQuery(baseEndpoint, new Dictionary<string, string?>
            {
                ["tag"] = tag,
                ["maxWidth"] = maxWidth.ToString(),
                ["quality"] = "82",
                ["api_key"] = AccessToken
            })
        };

        // Proxy/image-enhancer implementations occasionally throw 500 when a tag is
        // supplied, while the canonical untagged or indexed image route still works.
        if (!string.IsNullOrWhiteSpace(tag))
        {
            endpoints.Add(AddQuery(baseEndpoint, new Dictionary<string, string?>
            {
                ["maxWidth"] = maxWidth.ToString(),
                ["quality"] = "82",
                ["api_key"] = AccessToken
            }));
        }
        endpoints.Add(AddQuery($"{baseEndpoint}/0", new Dictionary<string, string?>
        {
            ["maxWidth"] = maxWidth.ToString(),
            ["quality"] = "82",
            ["api_key"] = AccessToken
        }));

        for (var index = 0; index < endpoints.Count; index++)
        {
            using var request = CreateRequest(HttpMethod.Get, endpoints[index], true);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            var canTryCompatibleRoute = index < endpoints.Count - 1
                                        && (response.StatusCode == HttpStatusCode.BadGateway
                                            || response.StatusCode == HttpStatusCode.ServiceUnavailable
                                            || response.StatusCode == HttpStatusCode.GatewayTimeout
                                            || (response.StatusCode == HttpStatusCode.InternalServerError && index == 0 && !string.IsNullOrWhiteSpace(tag)));
            if (canTryCompatibleRoute) continue;

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new EmbyApiException("服务器没有返回可用的图片数据。");
    }

    public async Task<byte[]> DownloadUserImageAsync(string userId, string? tag, int maxWidth, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var baseEndpoint = $"Users/{EscapePath(userId)}/Images/Primary";
        var query = new Dictionary<string, string?>
        {
            ["maxWidth"] = maxWidth.ToString(),
            ["quality"] = "85",
            ["api_key"] = AccessToken
        };
        if (!string.IsNullOrWhiteSpace(tag))
        {
            query["tag"] = tag;
        }

        var endpoint = AddQuery(baseEndpoint, query);
        using var request = CreateRequest(HttpMethod.Get, endpoint, true);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
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

        // Generic HLS fallback. A realtime server transcode must actually keep up with playback:
        // asking for 40 Mbps 4K H.264 was beyond most CPUs and produced segments slower than
        // playback consumed them, i.e. permanent buffering. Cap the fallback at a value the
        // server can encode in real time; the user's MaxStreamingBitrate still governs when
        // the server itself negotiated the transcode URL.
        var transcodeBitrate = Math.Min(_maxStreamingBitrate, 20_000_000);
        var endpoint = AddQuery($"Videos/{EscapePath(item.Id)}/master.m3u8", new Dictionary<string, string?>
        {
            ["MediaSourceId"] = source.Id ?? item.Id,
            ["DeviceId"] = DeviceId,
            ["PlaySessionId"] = playSessionId,
            ["Container"] = "ts",
            ["VideoCodec"] = "h264",
            ["AudioCodec"] = "aac",
            ["VideoBitRate"] = transcodeBitrate.ToString(),
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
        // MaxStaticBitrate is the server's DirectStream/DirectPlay gate. Reporting a hardcoded
        // 120 Mbps told the server any 4K remux was safe to send as-is, which starved slower
        // links into endless buffering. Tie it to the user's negotiated ceiling instead.
        MaxStreamingBitrate = _maxStreamingBitrate,
        MaxStaticBitrate = _maxStreamingBitrate,
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

        // Next-up suggestions and personalized recommendations shift after playback reports.
        _responseCache.MarkStale(BuildNextUpEndpoint(DefaultHomeRowLimit));
        _responseCache.MarkStale(BuildSuggestionsEndpoint(16));

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
        cache.MarkStale(BuildNextUpEndpoint(DefaultHomeRowLimit));
        cache.MarkStale(BuildSuggestionsEndpoint(16));

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

        var nextUpEndpoint = BuildNextUpEndpoint(DefaultHomeRowLimit);
        _responseCache.Warm(nextUpEndpoint, CachePolicy.NextUp.FreshFor, CachePolicy.NextUp.ServeStaleFor,
            token => GetJsonCoreAsync<ItemsResult>(nextUpEndpoint, true, token));

        var suggestionsEndpoint = BuildSuggestionsEndpoint(16);
        _responseCache.Warm(suggestionsEndpoint, CachePolicy.Similar.FreshFor, CachePolicy.Similar.ServeStaleFor,
            token => GetJsonCoreAsync<ItemsResult>(suggestionsEndpoint, true, token));

        var topRatedEndpoint = BuildTopRatedEndpoint(20);
        _responseCache.Warm(topRatedEndpoint, CachePolicy.Browse.FreshFor, CachePolicy.Browse.ServeStaleFor,
            token => GetJsonCoreAsync<ItemsResult>(topRatedEndpoint, true, token));
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
        public static readonly CachePolicy NextUp = new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
    }

    // List/card requests deliberately avoid MediaSources and MediaStreams.
    // Playback stream metadata is large and is fetched only when a detail/play action actually needs it.
    private const string ListFields = "PrimaryImageAspectRatio,Genres,RunTimeTicks,UserData,ChildCount,DateCreated,ProductionYear,CommunityRating";
    private const string HomeFields = ListFields + ",Overview";
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

public sealed record MediaProbeResult(
    bool Success,
    int? StatusCode,
    string Summary,
    string Detail,
    bool ServerOrUpstreamFailure);
