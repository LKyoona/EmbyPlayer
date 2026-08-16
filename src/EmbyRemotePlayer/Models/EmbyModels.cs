using System.Text.RegularExpressions;

namespace EmbyRemotePlayer.Models;

/// <summary>
/// Messy/shared servers often keep raw filenames as item names, including scraper tags
/// like "21座桥 (2019) {tmdb-69740}". Strip those tags wherever titles are displayed.
/// </summary>
public static class ItemNameCleaner
{
    private static readonly Regex ProviderTag = new(@"\{\s*[A-Za-z]+-[A-Za-z0-9]+\s*\}", RegexOptions.Compiled);

    public static string Clean(string name)
    {
        var cleaned = ProviderTag.Replace(name, string.Empty);
        return string.IsNullOrWhiteSpace(cleaned) ? name.Trim() : cleaned.Trim();
    }
}

public sealed class PublicSystemInfo
{
    public string? Id { get; set; }
    public string? ServerName { get; set; }
    public string? Version { get; set; }
    public string? LocalAddress { get; set; }
    public string? WanAddress { get; set; }
}

public sealed class AuthenticationResult
{
    public EmbyUser? User { get; set; }
    public string? AccessToken { get; set; }
    public string? ServerId { get; set; }
    public SessionInfo? SessionInfo { get; set; }
}

public sealed class SessionInfo
{
    public string? Id { get; set; }
}

public sealed class EmbyUser
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ServerId { get; set; }
    public string? PrimaryImageTag { get; set; }
    public UserPolicy? Policy { get; set; }
    public UserConfiguration? Configuration { get; set; }
}

public sealed class UserPolicy
{
    public bool EnableVideoPlaybackTranscoding { get; set; } = true;
    public bool EnableAudioPlaybackTranscoding { get; set; } = true;
    public bool EnablePlaybackRemuxing { get; set; } = true;
}

public sealed class UserConfiguration
{
    public bool EnableNextEpisodeAutoPlay { get; set; } = true;
    public bool RememberAudioSelections { get; set; } = true;
    public bool RememberSubtitleSelections { get; set; } = true;
    public string? SubtitleMode { get; set; }
}

public sealed class ItemsResult
{
    public List<EmbyItem> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
    public int StartIndex { get; set; }
}

public sealed class EmbyItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string? SortName { get; set; }
    public string? Type { get; set; }
    public string? MediaType { get; set; }
    public string? CollectionType { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public List<string>? Taglines { get; set; }
    public string? SeriesName { get; set; }
    public string? SeriesId { get; set; }
    public string? SeasonId { get; set; }
    public string? SeasonName { get; set; }
    public int? ProductionYear { get; set; }
    public int? IndexNumber { get; set; }
    public int? ParentIndexNumber { get; set; }
    public bool IsFolder { get; set; }
    public bool CanDelete { get; set; }
    public bool CanDownload { get; set; }
    public bool LockData { get; set; }
    public int? LocalTrailerCount { get; set; }
    public int? PartCount { get; set; }
    public long? RunTimeTicks { get; set; }
    public int? ChildCount { get; set; }
    public string? FileName { get; set; }
    public string? ParentId { get; set; }
    public string? ServerId { get; set; }
    public string? DisplayPreferencesId { get; set; }
    public string? Etag { get; set; }
    public string? PresentationUniqueKey { get; set; }
    public string? ForcedSortName { get; set; }
    public string? Path { get; set; }
    public string? Container { get; set; }
    public long? Size { get; set; }
    public long? Bitrate { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public DateTimeOffset? DateCreated { get; set; }
    public DateTimeOffset? PremiereDate { get; set; }
    public string? PrimaryImageTag { get; set; }
    public double? PrimaryImageAspectRatio { get; set; }
    public Dictionary<string, string>? ImageTags { get; set; }
    public List<string>? BackdropImageTags { get; set; }
    public List<string>? ParentBackdropImageTags { get; set; }
    public string? ParentBackdropItemId { get; set; }
    public string? ParentLogoImageTag { get; set; }
    public string? ParentLogoItemId { get; set; }
    public string? SeriesPrimaryImageTag { get; set; }
    public List<string>? Genres { get; set; }
    public List<EmbyChapter>? Chapters { get; set; }
    public List<EmbyPerson>? People { get; set; }
    public List<ExternalUrlInfo>? ExternalUrls { get; set; }
    public Dictionary<string, string>? ProviderIds { get; set; }
    public List<string>? ProductionLocations { get; set; }

    // Different Emby-compatible servers return Studios in different shapes.
    // Preserve the raw JSON and normalize it only in the detail presentation layer,
    // avoiding a server-specific schema that could break deserialization.
    public JsonElement? Studios { get; set; }

    public double? CommunityRating { get; set; }
    public UserItemData? UserData { get; set; }
    public List<MediaSourceInfo>? MediaSources { get; set; }
    public List<MediaStreamInfo>? MediaStreams { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名媒体" : ItemNameCleaner.Clean(Name!);

    [JsonIgnore]
    public string? OwnPrimaryImageTag =>
        PrimaryImageTag ??
        (ImageTags is not null && ImageTags.TryGetValue("Primary", out var tag) ? tag : null);

    [JsonIgnore]
    public string? EffectivePrimaryImageTag =>
        OwnPrimaryImageTag ?? SeriesPrimaryImageTag;

    [JsonIgnore]
    public string EffectivePrimaryImageItemId =>
        !string.IsNullOrWhiteSpace(OwnPrimaryImageTag)
            ? Id
            : (!string.IsNullOrWhiteSpace(SeriesPrimaryImageTag) && !string.IsNullOrWhiteSpace(SeriesId) ? SeriesId! : Id);

    [JsonIgnore]
    public string? EffectiveBackdropTag =>
        BackdropImageTags?.FirstOrDefault() ?? ParentBackdropImageTags?.FirstOrDefault();

    [JsonIgnore]
    public string EffectiveBackdropItemId =>
        BackdropImageTags is { Count: > 0 }
            ? Id
            : (!string.IsNullOrWhiteSpace(ParentBackdropItemId) ? ParentBackdropItemId! : Id);

    [JsonIgnore]
    public string? EffectiveLogoTag =>
        (ImageTags is not null && ImageTags.TryGetValue("Logo", out var ownLogo) ? ownLogo : null) ?? ParentLogoImageTag;

    [JsonIgnore]
    public string EffectiveLogoItemId =>
        ImageTags is not null && ImageTags.ContainsKey("Logo")
            ? Id
            : (!string.IsNullOrWhiteSpace(ParentLogoImageTag) && !string.IsNullOrWhiteSpace(ParentLogoItemId) ? ParentLogoItemId! : Id);

    [JsonIgnore]
    public bool CanPlay => string.Equals(MediaType, "Video", StringComparison.OrdinalIgnoreCase)
                           || Type is "Movie" or "Episode" or "Video" or "Trailer" or "MusicVideo";

    [JsonIgnore]
    public bool CanBrowse => IsFolder || Type is "Series" or "Season" or "BoxSet" or "CollectionFolder" or "Folder" or "UserView" or "Playlist";
}

public sealed class EmbyChapter
{
    public int ChapterIndex { get; set; }
    public string? MarkerType { get; set; }
    public string? Name { get; set; }
    public long StartPositionTicks { get; set; }
}

public sealed class EmbyPerson
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }
    public string? Type { get; set; }
    public string? PrimaryImageTag { get; set; }
}

public sealed class ExternalUrlInfo
{
    public string? Name { get; set; }
    public string? Url { get; set; }
}

public sealed class UserItemData
{
    public double? PlayedPercentage { get; set; }
    public long? PlaybackPositionTicks { get; set; }
    public bool Played { get; set; }
    public bool IsFavorite { get; set; }
    public int? PlayCount { get; set; }
    public int? UnplayedItemCount { get; set; }
    public DateTimeOffset? LastPlayedDate { get; set; }
}

public sealed class PlaybackInfoResponse
{
    public List<MediaSourceInfo> MediaSources { get; set; } = [];
    public string? PlaySessionId { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MediaSourceInfo
{
    public string? Id { get; set; }
    public string? ItemId { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Container { get; set; }
    public string? Protocol { get; set; }
    public string? Type { get; set; }
    public long? Size { get; set; }
    public long? Bitrate { get; set; }
    public long? RunTimeTicks { get; set; }
    public bool SupportsDirectPlay { get; set; }
    public bool SupportsDirectStream { get; set; }
    public bool SupportsTranscoding { get; set; }
    public string? DirectStreamUrl { get; set; }
    public string? TranscodingUrl { get; set; }
    public bool AddApiKeyToDirectStreamUrl { get; set; }
    public int? DefaultAudioStreamIndex { get; set; }
    public int? DefaultSubtitleStreamIndex { get; set; }
    public List<MediaStreamInfo> MediaStreams { get; set; } = [];
}

public sealed class MediaStreamInfo
{
    public int Index { get; set; }
    public string? Type { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? DisplayTitle { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsExternal { get; set; }
    public bool SupportsExternalStream { get; set; }
    public string? DeliveryUrl { get; set; }
    public string? DeliveryMethod { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public int? SampleRate { get; set; }
    public long? BitRate { get; set; }
    public int? BitDepth { get; set; }
    public double? AverageFrameRate { get; set; }
    public string? Profile { get; set; }
    public string? DisplayLanguage { get; set; }
    public bool IsHearingImpaired { get; set; }
    public bool IsTextSubtitleStream { get; set; }
    public string? SubtitleLocationType { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? AspectRatio { get; set; }
    public string? PixelFormat { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? ColorSpace { get; set; }
    public string? ColorTransfer { get; set; }
    public bool IsInterlaced { get; set; }
    public bool IsAnamorphic { get; set; }
    public string? VideoRange { get; set; }
}

public sealed class DeviceProfile
{
    public string Name { get; set; } = "Emby Remote Player";
    public long MaxStreamingBitrate { get; set; } = 120_000_000;
    public long MaxStaticBitrate { get; set; } = 120_000_000;
    public List<DirectPlayProfile> DirectPlayProfiles { get; set; } = [];
    public List<TranscodingProfile> TranscodingProfiles { get; set; } = [];
    public List<SubtitleProfile> SubtitleProfiles { get; set; } = [];
    public List<CodecProfile> CodecProfiles { get; set; } = [];
}

public sealed class DirectPlayProfile
{
    public string Container { get; set; } = string.Empty;
    public string Type { get; set; } = "Video";
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
}

public sealed class TranscodingProfile
{
    public string Container { get; set; } = "ts";
    public string Type { get; set; } = "Video";
    public string VideoCodec { get; set; } = "h264";
    public string AudioCodec { get; set; } = "aac";
    public string Protocol { get; set; } = "hls";
    public bool EstimateContentLength { get; set; }
    public bool EnableMpegtsM2TsMode { get; set; }
    public string Context { get; set; } = "Streaming";
    public bool CopyTimestamps { get; set; } = true;
    public bool EnableSubtitlesInManifest { get; set; } = true;
    public int MaxAudioChannels { get; set; } = 8;
}

public sealed class SubtitleProfile
{
    public string Format { get; set; } = string.Empty;
    public string Method { get; set; } = "External";
}

public sealed class CodecProfile
{
    public string Type { get; set; } = "Video";
    public string? Codec { get; set; }
    public List<ProfileCondition> Conditions { get; set; } = [];
}

public sealed class ProfileCondition
{
    public string Condition { get; set; } = "LessThanEqual";
    public string Property { get; set; } = "VideoBitDepth";
    public string Value { get; set; } = "12";
    public bool IsRequired { get; set; }
}

public sealed record PlaybackPlan(
    EmbyItem Item,
    MediaSourceInfo MediaSource,
    string PlaySessionId,
    Uri DirectUri,
    Uri? TranscodeUri,
    long ResumePositionTicks,
    bool DirectPlaySupported,
    bool TranscodingSupported,
    IReadOnlyList<MediaSourceInfo> AvailableMediaSources);

public enum PlaybackMethod
{
    DirectPlay,
    DirectStream,
    Transcode
}

public sealed record PlaybackReport(
    string ItemId,
    string MediaSourceId,
    string PlaySessionId,
    long PositionTicks,
    bool IsPaused,
    bool IsMuted,
    int VolumeLevel,
    PlaybackMethod Method,
    string? EventName = null,
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null);
