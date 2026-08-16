using System.Net.WebSockets;

namespace EmbyRemotePlayer.Services;

/// <summary>
/// Lightweight Emby websocket listener used only for cache invalidation / live metadata hints.
/// Playback never depends on this connection: when websocket is unavailable the metadata SWR TTLs
/// continue to work as the fallback path.
/// </summary>
public sealed class EmbyEventSocketService : IDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly Uri _serverBaseUri;
    private readonly string _accessToken;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;
    private bool _disposed;

    public EmbyEventSocketService(Uri serverBaseUri, string accessToken, string deviceId)
    {
        _serverBaseUri = serverBaseUri;
        _accessToken = accessToken;
        _deviceId = deviceId;
    }

    public event Action<EmbyServerEvent>? MessageReceived;

    public void Start()
    {
        if (_disposed || _runTask is not null) return;
        _runTask = Task.Run(() => RunAsync(_shutdown.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.SetRequestHeader("User-Agent", $"EmbyRemotePlayer/{EmbyApiClient.ClientVersion}");

                await socket.ConnectAsync(BuildWebSocketUri(), token).ConfigureAwait(false);
                failures = 0;
                LogService.Info("Emby websocket connected; metadata push invalidation is active.");
                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                // Websocket is an optimization, never a requirement for login/playback/navigation.
                // Avoid filling daily logs when a proxy/server permanently blocks websocket upgrades.
                if (failures == 1 || failures % 10 == 0)
                    LogService.Warning("Emby websocket disconnected; SWR TTL fallback remains active.", ex);
            }

            if (token.IsCancellationRequested) break;
            var delay = failures switch
            {
                <= 1 => TimeSpan.FromSeconds(1),
                2 => TimeSpan.FromSeconds(2),
                3 => TimeSpan.FromSeconds(5),
                4 => TimeSpan.FromSeconds(10),
                5 => TimeSpan.FromSeconds(20),
                _ => TimeSpan.FromSeconds(30)
            };
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client reconnect", token).ConfigureAwait(false);
                    }
                    catch { }
                    return;
                }

                if (result.Count > 0)
                {
                    if (message.Length + result.Count > MaxMessageBytes)
                        throw new InvalidDataException("Emby websocket message exceeded the safety limit.");
                    message.Write(buffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || message.Length == 0) continue;
            TryPublish(message.ToArray());
        }
    }

    private void TryPublish(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("MessageType", out var typeElement)) return;
            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type)) return;

            var data = root.TryGetProperty("Data", out var dataElement)
                ? dataElement.Clone()
                : default;
            try { MessageReceived?.Invoke(new EmbyServerEvent(type, data)); }
            catch (Exception ex) { LogService.Warning($"Emby websocket consumer failed for {type}.", ex); }
        }
        catch (JsonException ex)
        {
            LogService.Warning("Ignored malformed Emby websocket message.", ex);
        }
    }

    private Uri BuildWebSocketUri()
    {
        var builder = new UriBuilder(_serverBaseUri)
        {
            Scheme = _serverBaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Port = _serverBaseUri.IsDefaultPort ? -1 : _serverBaseUri.Port,
            Fragment = string.Empty,
            Query = $"api_key={Uri.EscapeDataString(_accessToken)}&deviceId={Uri.EscapeDataString(_deviceId)}"
        };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        MessageReceived = null;
    }
}

public readonly record struct EmbyServerEvent(string MessageType, JsonElement Data);
