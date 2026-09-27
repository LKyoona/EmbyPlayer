namespace EmbyRemotePlayer.Models;

public sealed class ServerProfile
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public bool AutoConnect { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public string? ServerVersion { get; set; }
    public string? UserPrimaryImageTag { get; set; }

    [JsonIgnore]
    public string CredentialTarget => $"EmbyRemotePlayer:v2:{ProfileId}";

    [JsonIgnore]
    public string FriendlyName => string.IsNullOrWhiteSpace(DisplayName)
        ? (string.IsNullOrWhiteSpace(UserName) ? ServerUrl : $"{UserName} 的 Emby")
        : DisplayName;
}

public sealed class AppSettings
{
    public List<ServerProfile> Profiles { get; set; } = [];
    public string? LastProfileId { get; set; }
    public int PreferredVolume { get; set; } = 80;
    public int MaxStreamingBitrate { get; set; } = 40_000_000;
    public int NetworkCachingMilliseconds { get; set; } = 1500;
    public bool HardwareDecoding { get; set; } = true;
    public bool RememberPlaybackSpeed { get; set; } = true;
    public double PlaybackSpeed { get; set; } = 1.0;
    public bool SidebarCollapsed { get; set; } = false;
}

public enum BrowseKind
{
    Home,
    LibraryRoot,
    Parent,
    Search,
    Favorites
}

public sealed record NavigationState(
    BrowseKind Kind,
    string Title,
    string? ParentId = null,
    string? SearchTerm = null);

public sealed record TrackOption(int Id, string Name, int? EmbyStreamIndex = null)
{
    public override string ToString() => Name;
}

public sealed record MediaSourceOption(MediaSourceInfo Source)
{
    public string Name
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Source.Name) ? "默认版本" : Source.Name;
            var video = Source.MediaStreams
                .FirstOrDefault(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
            var audio = Source.MediaStreams
                .FirstOrDefault(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase));

            var technical = new List<string>();
            if (video?.Width is > 0 && video.Height is > 0)
            {
                var resolution = video.Height >= 2160 ? "4K" : video.Height >= 1440 ? "1440p" : video.Height >= 1080 ? "1080p" : $"{video.Height}p";
                technical.Add(resolution);
            }
            var bitrate = Source.Bitrate ?? video?.BitRate;
            if (bitrate is > 0)
                technical.Add(bitrate >= 1_000_000 ? $"{bitrate / 1_000_000d:0.##} Mbps" : $"{bitrate / 1000d:0} Kbps");
            if (!string.IsNullOrWhiteSpace(video?.Codec))
            {
                var codec = video.Codec!.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "HEVC" : video.Codec!.ToUpperInvariant();
                if (video.BitDepth is > 8) codec += $" {video.BitDepth}bit";
                technical.Add(codec);
            }
            if (video?.AverageFrameRate is > 0)
                technical.Add($"{video.AverageFrameRate:0.##}fps");
            if (!string.IsNullOrWhiteSpace(audio?.Codec))
            {
                var audioLabel = audio.Codec!.ToUpperInvariant();
                if (audio.Channels is > 0) audioLabel += $" {audio.Channels}ch";
                technical.Add(audioLabel);
            }
            if (!string.IsNullOrWhiteSpace(Source.Container))
                technical.Add(Source.Container!.ToUpperInvariant());

            if (technical.Count > 0)
                label += " · " + string.Join(" · ", technical);
            return label;
        }
    }

    public override string ToString() => Name;
}
