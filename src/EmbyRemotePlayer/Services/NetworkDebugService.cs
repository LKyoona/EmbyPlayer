using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace EmbyRemotePlayer.Services;

public sealed class NetworkDebugEntry
{
    public int RequestId { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ResourceType { get; set; } = "fetch";
    public string Initiator { get; set; } = "EmbyApiClient";
    public int? StatusCode { get; set; }
    public string StatusText { get; set; } = "Pending";
    public double? DurationMs { get; set; }
    public long? ResponseSize { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string RequestHeaders { get; init; } = string.Empty;
    public string ResponseHeaders { get; set; } = string.Empty;
    public string QueryString { get; init; } = string.Empty;
    public string RequestBody { get; init; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    public string IdText => Attempt > 1 ? $"#{RequestId:D4}.{Attempt}" : $"#{RequestId:D4}";
    public string StatusDisplay => StatusCode is int code ? code.ToString() : StatusText;
    public string TypeDisplay => ResourceType switch
    {
        "xhr" => "Fetch/XHR",
        "media" => "Media",
        "image" => "Img",
        "document" => "Doc",
        "cache" => "Cache",
        _ => ResourceType
    };
    public string SizeDisplay => ResponseSize is long size ? FormatBytes(size) : "—";
    public string TimeDisplay => DurationMs is double ms ? (ms >= 1000 ? $"{ms / 1000:0.00} s" : $"{ms:0} ms") : "—";
    public bool IsError => !string.IsNullOrWhiteSpace(Error) || StatusCode is >= 400;

    public string GeneralText
    {
        get
        {
            var builder = new StringBuilder();
            builder.AppendLine("General");
            builder.Append("Request URL: ").AppendLine(Url);
            builder.Append("Request Method: ").AppendLine(Method);
            builder.Append("Status Code: ").AppendLine(StatusCode is int code ? $"{code} {StatusText}" : StatusText);
            builder.Append("Resource Type: ").AppendLine(TypeDisplay);
            builder.Append("Initiator: ").AppendLine(Initiator);
            builder.Append("Started: ").AppendLine(StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            if (DurationMs is double ms) builder.Append("Duration: ").AppendLine($"{ms:0.0} ms");
            if (ResponseSize is long size) builder.Append("Content Length: ").AppendLine($"{size:N0} bytes");
            if (!string.IsNullOrWhiteSpace(ContentType)) builder.Append("Content-Type: ").AppendLine(ContentType);
            if (!string.IsNullOrWhiteSpace(Error)) builder.Append("Error: ").AppendLine(Error);
            return builder.ToString().TrimEnd();
        }
    }

    public string HeadersText
    {
        get
        {
            var builder = new StringBuilder(GeneralText);
            builder.AppendLine().AppendLine().AppendLine("Request Headers");
            builder.Append(string.IsNullOrWhiteSpace(RequestHeaders) ? "<none>" : RequestHeaders);
            builder.AppendLine().AppendLine().AppendLine("Response Headers");
            builder.Append(string.IsNullOrWhiteSpace(ResponseHeaders) ? "<pending>" : ResponseHeaders);
            return builder.ToString();
        }
    }

    public string PayloadText
    {
        get
        {
            var builder = new StringBuilder();
            builder.AppendLine("Query String Parameters");
            builder.Append(string.IsNullOrWhiteSpace(QueryString) ? "<none>" : QueryString);
            builder.AppendLine().AppendLine().AppendLine("Request Payload");
            builder.Append(string.IsNullOrWhiteSpace(RequestBody) ? "<none>" : RequestBody);
            return builder.ToString();
        }
    }

    public string TimingText
    {
        get
        {
            if (ResourceType == "cache")
            {
                return $"缓存命中：{StatusText}\n读取耗时：{(DurationMs ?? 0):0.0} ms\n\n该条目没有向 Emby 服务器发起 HTTP 请求。";
            }

            var builder = new StringBuilder();
            builder.Append("Queued / started: ").AppendLine(StartedAt.ToString("HH:mm:ss.fff"));
            builder.Append("Attempt: ").AppendLine(Attempt.ToString());
            builder.Append("Request → response body: ").AppendLine(DurationMs is double ms ? $"{ms:0.0} ms" : "pending");
            builder.Append("Finished: ").AppendLine(DurationMs is double elapsed ? StartedAt.AddMilliseconds(elapsed).ToString("HH:mm:ss.fff") : "pending");
            builder.AppendLine().Append(NetworkDebugService.IsBodyCaptureEnabled
                ? "注：该数字由客户端 Network Inspector 统计；打开调试窗口时包含响应体抓取/读取时间。它不是 Emby 服务器返回字段，也不是浏览器级 DNS/TCP/TLS 分段瀑布图。"
                : "注：该数字由客户端 Network Inspector 统计；未抓取响应体时主要表示请求至响应头阶段。它不是 Emby 服务器返回字段，也不是浏览器级 DNS/TCP/TLS 分段瀑布图。");
            return builder.ToString();
        }
    }

    public string ToCurl()
    {
        var method = string.IsNullOrWhiteSpace(Method) ? "GET" : Method;
        var command = new StringBuilder("curl");
        command.Append(" -X ").Append(method);
        foreach (var line in RequestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("X-Emby-Token:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("X-MediaBrowser-Token:", StringComparison.OrdinalIgnoreCase)) continue;
            command.Append(" -H \"").Append(EscapeShell(line.TrimEnd('\r'))).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(RequestBody) && !RequestBody.StartsWith('<'))
            command.Append(" --data-raw \"").Append(EscapeShell(RequestBody.Replace("\r", string.Empty).Replace("\n", " "))).Append('"');
        command.Append(" \"").Append(EscapeShell(Url)).Append('"');
        return command.ToString();
    }

    private static string EscapeShell(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d:0.0} MB";
        return $"{bytes / 1024d / 1024d / 1024d:0.00} GB";
    }
}

/// <summary>
/// Structured in-app HTTP inspector inspired by browser DevTools Network.
/// Keeps a bounded request history and redacts credentials before data reaches the UI.
/// </summary>
public static class NetworkDebugService
{
    private const int MaxEntries = 500;
    private const int MaxBodyCharacters = 450_000;
    private static readonly object Gate = new();
    private static readonly List<NetworkDebugEntry> Entries = new();
    private static readonly Dictionary<(int RequestId, int Attempt), NetworkDebugEntry> Pending = new();
    private static int _requestId;
    private static int _bodyCaptureSubscribers;

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex SensitiveJsonValueRegex = new(
        "(\\\"(?:Pw|Password|AccessToken|Token|ApiKey|api_key|Sig|Signature|X-Emby-Token|X-MediaBrowser-Token)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SensitiveQueryRegex = new(
        "(?<key>(?:api_key|access_token|token|x-emby-token|x-mediabrowser-token|sig|signature))=(?<value>[^&?#\\s\"\\\\]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthorizationTokenRegex = new(
        "Token=\\\"[^\\\"]*\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static event Action<NetworkDebugEntry>? EntryChanged;
    public static event Action? Cleared;

    public static int NextRequestId() => Interlocked.Increment(ref _requestId);

    /// <summary>
    /// Response-body capture is intentionally opt-in. Normal playback/navigation keeps only
    /// lightweight headers/timing metadata; opening Network Inspector enables body capture
    /// for subsequent requests. This avoids parsing/pretty-printing every JSON response twice.
    /// </summary>
    public static void BeginBodyCapture() => Interlocked.Increment(ref _bodyCaptureSubscribers);

    public static void EndBodyCapture()
    {
        while (true)
        {
            var current = Volatile.Read(ref _bodyCaptureSubscribers);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref _bodyCaptureSubscribers, current - 1, current) == current) return;
        }
    }

    public static bool IsBodyCaptureEnabled => Volatile.Read(ref _bodyCaptureSubscribers) > 0;

    public static IReadOnlyList<NetworkDebugEntry> GetEntriesSnapshot()
    {
        lock (Gate) return Entries.ToList();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Pending.Clear();
        }
        try { Cleared?.Invoke(); } catch { }
    }

    public static async Task LogRequestAsync(HttpRequestMessage request, int requestId, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            var sanitizedUrl = SanitizeUri(request.RequestUri);
            var requestContent = request.Content is null
                ? new ContentLogResult(string.Empty, null)
                : IsBodyCaptureEnabled
                    ? await ReadContentForLogAsync(request.Content, cancellationToken).ConfigureAwait(false)
                    : new ContentLogResult("<request body capture disabled>", request.Content.Headers.ContentLength);
            var entry = new NetworkDebugEntry
            {
                RequestId = requestId,
                Attempt = attempt,
                StartedAt = DateTimeOffset.Now,
                Method = request.Method.Method,
                Url = sanitizedUrl,
                Name = GetDisplayName(request.RequestUri),
                ResourceType = GetResourceType(request.RequestUri, request.Content?.Headers.ContentType?.MediaType),
                RequestHeaders = FormatHeaders(request.Headers, request.Content?.Headers),
                QueryString = FormatQueryString(request.RequestUri),
                RequestBody = requestContent.Text,
                ContentType = request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                StatusText = attempt > 1 ? $"Pending · retry {attempt}" : "Pending"
            };

            AddOrReplace(entry);
        }
        catch
        {
            // Diagnostics must never change request behavior.
        }
    }

    public static async Task LogResponseAsync(HttpResponseMessage response, int requestId, int attempt, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        try
        {
            var captureBody = IsBodyCaptureEnabled;
            var content = captureBody
                ? await ReadContentForLogAsync(response.Content, cancellationToken).ConfigureAwait(false)
                : new ContentLogResult(string.Empty, response.Content.Headers.ContentLength);
            NetworkDebugEntry? entry;
            lock (Gate) Pending.TryGetValue((requestId, attempt), out entry);
            if (entry is null) return;

            entry.StatusCode = (int)response.StatusCode;
            entry.StatusText = response.ReasonPhrase ?? response.StatusCode.ToString();
            entry.DurationMs = Math.Max(elapsed.TotalMilliseconds, (DateTimeOffset.Now - entry.StartedAt).TotalMilliseconds);
            entry.ContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            entry.ResponseSize = response.Content.Headers.ContentLength ?? content.ByteLength;
            entry.ResponseHeaders = FormatHeaders(response.Headers, response.Content.Headers);
            entry.ResponseBody = captureBody
                ? content.Text
                : "<response body capture disabled; open Network Inspector before repeating the request>";
            if (entry.ResourceType == "fetch")
                entry.ResourceType = GetResourceType(response.RequestMessage?.RequestUri, response.Content.Headers.ContentType?.MediaType);
            Notify(entry);
        }
        catch
        {
            // Diagnostics must never change response behavior.
        }
    }

    public static void LogFailure(int requestId, int attempt, TimeSpan elapsed, Exception exception)
    {
        NetworkDebugEntry? entry;
        lock (Gate) Pending.TryGetValue((requestId, attempt), out entry);
        if (entry is null) return;
        entry.DurationMs = elapsed.TotalMilliseconds;
        entry.StatusText = "Failed";
        entry.Error = $"{exception.GetType().Name}: {exception.Message}";
        entry.ResponseBody = entry.Error;
        Notify(entry);
    }

    public static void LogMetadataCache(string key, string status, TimeSpan age, long responseSize)
    {
        // Cache telemetry is useful while the inspector is open, but should not create
        // noise or allocations during normal browsing.
        if (!IsBodyCaptureEnabled) return;
        try
        {
            var endpoint = key;
            var scopeSeparator = endpoint.LastIndexOf('|');
            if (scopeSeparator >= 0 && scopeSeparator + 1 < endpoint.Length)
                endpoint = endpoint[(scopeSeparator + 1)..];

            var entry = new NetworkDebugEntry
            {
                RequestId = NextRequestId(),
                Attempt = 1,
                StartedAt = DateTimeOffset.Now,
                Method = "CACHE",
                Url = "cache://metadata/" + endpoint,
                Name = GetDisplayNameFromPath(endpoint),
                ResourceType = "cache",
                Initiator = "Metadata SWR Cache",
                StatusText = status,
                DurationMs = 0,
                ResponseSize = responseSize,
                ResponseBody = $"<served locally>\nAge: {age.TotalSeconds:0.0}s\nKey: {endpoint}"
            };
            AddCompleted(entry);
        }
        catch { }
    }

    public static void LogPlayerStream(Uri uri, string method)
    {
        var requestId = NextRequestId();
        var entry = new NetworkDebugEntry
        {
            RequestId = requestId,
            Attempt = 1,
            StartedAt = DateTimeOffset.Now,
            Method = "GET",
            Url = SanitizeUri(uri),
            Name = GetDisplayName(uri),
            ResourceType = "media",
            Initiator = "LibVLC",
            StatusText = "Streaming",
            QueryString = FormatQueryString(uri),
            ResponseBody = $"<媒体流由 LibVLC 直接读取>\nPlayback method: {method}\nURL: {SanitizeUri(uri)}\n\n为避免占用内存，不缓存视频/音频二进制响应体。"
        };
        AddOrReplace(entry);
    }

    public static void LogPlayerProbe(Uri uri, MediaProbeResult result, double durationMs)
    {
        try
        {
            var entry = new NetworkDebugEntry
            {
                RequestId = NextRequestId(),
                Attempt = 1,
                StartedAt = DateTimeOffset.Now.AddMilliseconds(-Math.Max(0, durationMs)),
                Method = "GET",
                Url = SanitizeUri(uri),
                Name = GetDisplayName(uri) + " · probe",
                ResourceType = "media",
                Initiator = "Media health probe",
                StatusCode = result.StatusCode,
                StatusText = result.Success ? "Media bytes received" : result.Summary,
                DurationMs = durationMs,
                QueryString = FormatQueryString(uri),
                ResponseBody = $"媒体端点预检\n结论：{result.Summary}\n详情：{SanitizeUriText(result.Detail)}",
                Error = result.Success ? string.Empty : SanitizeUriText(result.Summary + " · " + result.Detail)
            };
            AddCompleted(entry);
        }
        catch { }
    }

    private static void AddCompleted(NetworkDebugEntry entry)
    {
        lock (Gate)
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                var removed = Entries[0];
                Entries.RemoveAt(0);
                Pending.Remove((removed.RequestId, removed.Attempt));
            }
        }
        Notify(entry);
    }

    private static string GetDisplayNameFromPath(string endpoint)
    {
        var path = endpoint.Split('?', 2)[0].Trim('/');
        if (string.IsNullOrWhiteSpace(path)) return "Metadata cache";
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Metadata cache";
    }

    private static void AddOrReplace(NetworkDebugEntry entry)
    {
        lock (Gate)
        {
            Pending[(entry.RequestId, entry.Attempt)] = entry;
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                var removed = Entries[0];
                Entries.RemoveAt(0);
                Pending.Remove((removed.RequestId, removed.Attempt));
            }
        }
        Notify(entry);
    }

    private static void Notify(NetworkDebugEntry entry)
    {
        try { EntryChanged?.Invoke(entry); } catch { }
    }

    private static string FormatHeaders(HttpHeaders headers, HttpHeaders? contentHeaders)
    {
        var builder = new StringBuilder();
        AppendHeaders(builder, headers);
        if (contentHeaders is not null) AppendHeaders(builder, contentHeaders);
        return builder.ToString().TrimEnd();
    }

    private static void AppendHeaders(StringBuilder builder, HttpHeaders headers)
    {
        foreach (var header in headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = string.Join(", ", header.Value);
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("X-MediaBrowser-Token", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                value = "***";
            }
            else if (header.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                value = AuthorizationTokenRegex.Replace(value, "Token=\"***\"");
            }
            else if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                value = SanitizeUriText(value);
            }
            builder.Append(header.Key).Append(": ").AppendLine(value);
        }
    }

    private readonly record struct ContentLogResult(string Text, long? ByteLength);

    private static async Task<ContentLogResult> ReadContentForLogAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var mediaType = content.Headers.ContentType?.MediaType ?? string.Empty;
        var isText = IsTextMediaType(mediaType);
        if (!isText)
        {
            var size = content.Headers.ContentLength is long length ? $"{length:N0} bytes" : "size unknown";
            return new ContentLogResult($"<binary {mediaType}; {size}>", content.Headers.ContentLength);
        }

        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) return new ContentLogResult(string.Empty, 0);
        var text = DecodeText(bytes, content.Headers.ContentType?.CharSet);
        text = PrettyAndRedactJson(text);
        if (text.Length <= MaxBodyCharacters) return new ContentLogResult(text, bytes.LongLength);
        return new ContentLogResult(text[..MaxBodyCharacters] + $"\n… <truncated {text.Length - MaxBodyCharacters:N0} characters>", bytes.LongLength);
    }

    private static bool IsTextMediaType(string mediaType) =>
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(mediaType);

    private static string DecodeText(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                var normalized = charset.Trim().Trim('"', '\'');
                return Encoding.GetEncoding(normalized).GetString(bytes);
            }
        }
        catch
        {
            // Fall through to BOM/UTF-8 detection.
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string PrettyAndRedactJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            text = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch
        {
            // Not JSON; keep decoded text as-is.
        }
        text = SensitiveJsonValueRegex.Replace(text, "$1***$2");
        // PlaybackInfo often embeds api_key inside DeliveryUrl/stream URL strings.
        // Redact query credentials inside arbitrary JSON string values as well.
        return SensitiveQueryRegex.Replace(text, "${key}=***");
    }

    private static string GetDisplayName(Uri? uri)
    {
        if (uri is null) return "<unknown>";
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1) return segments[0];
        var last = segments[^1];
        var previous = segments[^2];
        if (last.Length > 28 && (previous.Equals("Items", StringComparison.OrdinalIgnoreCase) || previous.Equals("Users", StringComparison.OrdinalIgnoreCase)))
            return previous + "/" + last[..8] + "…";
        if (last.Equals("PlaybackInfo", StringComparison.OrdinalIgnoreCase)) return "PlaybackInfo";
        if (last.Equals("AuthenticateByName", StringComparison.OrdinalIgnoreCase)) return "AuthenticateByName";
        return last;
    }

    private static string GetResourceType(Uri? uri, string? mediaType)
    {
        var type = mediaType ?? string.Empty;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "media";
        if (type.Contains("json", StringComparison.OrdinalIgnoreCase)) return "xhr";
        var path = uri?.AbsolutePath ?? string.Empty;
        if (path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (path.Contains("/Videos/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase)) return "media";
        return "xhr";
    }

    private static string FormatQueryString(Uri? uri)
    {
        if (uri is null || string.IsNullOrWhiteSpace(uri.Query)) return string.Empty;
        var builder = new StringBuilder();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator >= 0 ? pair[..separator] : pair;
            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            key = Uri.UnescapeDataString(key.Replace('+', ' '));
            value = Uri.UnescapeDataString(value.Replace('+', ' '));
            if (key.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                || key.Equals("access_token", StringComparison.OrdinalIgnoreCase)
                || key.Equals("token", StringComparison.OrdinalIgnoreCase)) value = "***";
            builder.Append(key).Append(": ").AppendLine(value);
        }
        return builder.ToString().TrimEnd();
    }

    private static string SanitizeUri(Uri? uri) => uri is null ? "<no uri>" : SanitizeUriText(uri.ToString());
    private static string SanitizeUriText(string value) => SensitiveQueryRegex.Replace(value, "${key}=***");
}
