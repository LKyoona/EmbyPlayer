using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using EmbyRemotePlayer.Models;
using EmbyRemotePlayer.Services;
using EmbyRemotePlayer.ViewModels;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace EmbyRemotePlayer.Views;

public partial class PlayerWindow : Window
{
    private readonly EmbyApiClient _api;
    private PlaybackPlan _plan;
    private readonly HashSet<string> _playedItemIds = new(StringComparer.Ordinal);
    public string CurrentItemId => _plan.Item.Id;
    public IReadOnlyCollection<string> PlayedItemIds => _playedItemIds;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _reportTimer;
    private readonly DispatcherTimer _startupTimer;
    private readonly DispatcherTimer _cursorTimer;
    private readonly PlayerViewModel _viewModel;

    private Media? _media;
    private PlaybackMethod _method = PlaybackMethod.DirectPlay;
    private long _streamBaseTicks;
    private long _pendingSeekTicks;
    private long _lastPositionTicks;
    private long _runtimeTotalTicks;
    private bool _isSeeking;
    private bool _updatingAudioTracks;
    private bool _updatingSubtitleTracks;
    private bool _updatingMediaVersions;
    private bool _switchingMediaSource;
    private bool _pauseAfterMediaStart;
    private bool _startedReported;
    private bool _fallbackAttempted;
    private bool _softwareDecodeFallbackAttempted;
    private bool _forceSoftwareDecode;
    private bool _failureHandling;
    private bool _closing;
    private bool _playingObserved;
    private bool _isFullscreen;
    private bool _switchingEpisode;
    private bool _autoPlayNextEpisode = true;
    private bool _allowTranscodingByPolicy = true;
    private int? _selectedAudioStreamIndex;
    private int? _selectedSubtitleStreamIndex;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private ResizeMode _previousResizeMode;
    private bool _previousTopmost;
    private WindowChrome? _previousWindowChrome;
    private NativeRect _previousNativeWindowRect;
    private bool _hasPreviousNativeWindowRect;
    private CancellationTokenSource? _episodeLoadCts;
    private EmbyItem? _previousEpisode;
    private EmbyItem? _nextEpisode;
    private NativePoint _lastCursorPosition;
    private bool _hasCursorPosition;
    private bool _lastLeftButtonDown;
    private bool _thumbSeekDrag;
    private long _seekDragTargetTicks;
    private bool _seekCommitInProgress;
    private long _seekCommitTargetTicks;
    private bool _videoClickCandidate;
    private NativePoint _videoClickDownPoint;
    private DateTime _videoClickDownUtc;
    private int _playbackGestureGeneration;
    private int _seekOperationGeneration;
    private float _bufferingPercent = 100f;
    private int _activeNetworkCachingMilliseconds;
    private bool _defaultExternalSubtitleAttached;
    private int _loadingGeneration;

    // Live stream telemetry is sampled from LibVLC's cumulative media read statistics.
    // We deliberately compute bytes/second ourselves so the UI can display familiar KB/s / MB/s.
    private int _lastNetworkReadBytes;
    private DateTime _lastNetworkSampleUtc;
    private double _smoothedNetworkBytesPerSecond;
    private bool _networkSampleReady;

    public PlayerWindow(EmbyApiClient api, PlaybackPlan plan, AppSettings settings, Action saveSettings)
    {
        _api = api;
        _plan = plan;
        _playedItemIds.Add(plan.Item.Id);
        _settings = settings;
        _saveSettings = saveSettings;
        InitializeComponent();
        App.ConfigureAdaptiveWindow(this, 0.94, 0.90, 960, 560, 1920, 1180);
        SizeChanged += PlayerWindow_SizeChanged;

        Core.Initialize();
        var vlcArgs = settings.HardwareDecoding
            ? new[] { "--avcodec-hw=any", "--no-video-title-show", "--http-reconnect" }
            : new[] { "--avcodec-hw=none", "--no-video-title-show", "--http-reconnect" };
        _libVlc = new LibVLC(vlcArgs);
        _mediaPlayer = new VlcMediaPlayer(_libVlc)
        {
            NetworkCaching = checked((uint)Math.Max(0, settings.NetworkCachingMilliseconds)),
            Volume = settings.PreferredVolume
        };
        VideoView.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Opening += MediaPlayer_Opening;
        _mediaPlayer.Buffering += MediaPlayer_Buffering;
        _mediaPlayer.Playing += MediaPlayer_Playing;
        _mediaPlayer.Paused += MediaPlayer_Paused;
        _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
        _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, UiTimer_Tick, Dispatcher);
        _reportTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, ReportTimer_Tick, Dispatcher);
        _startupTimer = new DispatcherTimer(TimeSpan.FromSeconds(18), DispatcherPriority.Background, StartupTimer_Tick, Dispatcher);
        // 60 Hz input polling keeps native LibVLC HWND clicks feeling immediate without tying playback
        // feedback to the slower metadata/UI refresh timer.
        _cursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, CursorTimer_Tick, Dispatcher);

        _viewModel = new PlayerViewModel(
            togglePlayback: () => TogglePlayback(showGesture: true),
            seekBackward: () => SeekRelative(-10),
            seekForward: () => SeekRelative(10),
            toggleMute: ToggleMute,
            toggleFullscreen: ToggleFullscreen,
            close: Close,
            minimize: () => WindowState = WindowState.Minimized,
            volumeChanged: ApplyVolumeFromViewModel);
        DataContext = _viewModel;
        _viewModel.Volume = settings.PreferredVolume;
        // A pointer resting anywhere on the chrome (left controls, slider, title bar, episode
        // panel) must keep the chrome alive; without this the 3s timer faded controls out from
        // under a stationary cursor and the next click fell through to the video surface.
        _viewModel.KeepVisibleCheck = () =>
            ControlsPanel.IsMouseOver || TitleBarPanel.IsMouseOver || EpisodePanel.Visibility == Visibility.Visible;

        ResetPlanState(plan);
    }

    private void PlayerWindow_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsivePlayerMetrics(e.NewSize.Width, e.NewSize.Height);

    private void ApplyResponsivePlayerMetrics(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        var compact = width < 1180 || height < 700;
        var spacious = width >= 1800 && height >= 950;
        ControlsPanel.Margin = compact ? new Thickness(18, 0, 18, 8) : spacious ? new Thickness(44, 0, 44, 16) : new Thickness(32, 0, 32, 10);
        TitleBarPanel.Margin = compact ? new Thickness(12, 2, 12, 0) : spacious ? new Thickness(28, 8, 28, 0) : new Thickness(18, 4, 18, 0);
        VolumeSlider.Width = compact ? 112 : spacious ? 156 : 142;
        TitleText.FontSize = compact ? 16 : spacious ? 20 : 18;
        CurrentTimeText.FontSize = compact ? 11 : spacious ? 13 : 12;
        EpisodePanel.Width = compact ? 338 : spacious ? 420 : 388;
        EpisodePanel.Margin = compact ? new Thickness(0, 62, 10, 82) : spacious ? new Thickness(0, 78, 20, 116) : new Thickness(0, 70, 14, 104);
        var reservedSide = Math.Max(LeftControlsGroup?.ActualWidth ?? 0, RightControlsGroup?.ActualWidth ?? 0);
        var availableForTitle = Math.Max(140, width - reservedSide * 2 - 48);
        BottomTitleText.MaxWidth = compact
            ? Math.Min(360, availableForTitle)
            : spacious ? Math.Min(760, availableForTitle) : Math.Min(560, availableForTitle);

        // The side groups can settle one render after a DPI/fullscreen resize. Re-evaluate only
        // the centered title constraint; this never changes window geometry.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || BottomTitleText is null) return;
            var side = Math.Max(LeftControlsGroup?.ActualWidth ?? 0, RightControlsGroup?.ActualWidth ?? 0);
            var available = Math.Max(140, ActualWidth - side * 2 - 48);
            BottomTitleText.MaxWidth = compact ? Math.Min(360, available) : spacious ? Math.Min(760, available) : Math.Min(560, available);
            UpdatePlaybackTimestampBubble();
        }), DispatcherPriority.Render);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsivePlayerMetrics(ActualWidth, ActualHeight);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (_closing) return;

        ConfigurePlanUi();
        var speeds = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
        SpeedComboBox.ItemsSource = speeds.Select(value => new SpeedOption(value)).ToList();
        SpeedComboBox.SelectedItem = SpeedComboBox.Items.Cast<SpeedOption>().OrderBy(option => Math.Abs(option.Value - _settings.PlaybackSpeed)).First();
        _viewModel.Volume = _settings.PreferredVolume;
        UpdateMuteButton();

        PositionSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(PositionThumb_DragStarted), true);
        PositionSlider.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(PositionThumb_DragDelta), true);
        PositionSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(PositionThumb_DragCompleted), true);
        AttachSeekRailHandlers();

        ShowLoading("正在准备播放…", "正在连接媒体服务器");
        try { StartCurrentPlan(_plan.ResumePositionTicks); }
        catch (Exception ex) { await HandlePlaybackFailureAsync("无法启动媒体流：" + ex.Message + " "); }

        _uiTimer.Start();
        _reportTimer.Start();
        _cursorTimer.Start();
        ShowPlaybackChrome();
        _ = LoadUserPlaybackPreferencesAsync();
        _ = LoadEpisodeContextAsync();
    }

    private void ResetPlanState(PlaybackPlan plan, long? initialPositionTicks = null, bool preserveEpisodeContext = false)
    {
        _selectedAudioStreamIndex = plan.MediaSource.DefaultAudioStreamIndex;
        _selectedSubtitleStreamIndex = plan.MediaSource.DefaultSubtitleStreamIndex is int sub && sub >= 0 ? sub : null;
        _runtimeTotalTicks = 0;
        _lastPositionTicks = Math.Max(0, initialPositionTicks ?? plan.ResumePositionTicks);
        _streamBaseTicks = 0;
        _pendingSeekTicks = 0;
        _startedReported = false;
        _playingObserved = false;
        _fallbackAttempted = false;
        _softwareDecodeFallbackAttempted = false;
        _forceSoftwareDecode = false;
        _failureHandling = false;
        _bufferingPercent = 0f;
        _activeNetworkCachingMilliseconds = 0;
        _defaultExternalSubtitleAttached = false;
        ResetNetworkTelemetry();
        if (!preserveEpisodeContext)
        {
            _previousEpisode = null;
            _nextEpisode = null;
        }
    }

    private void ConfigurePlanUi()
    {
        Title = _plan.Item.Name + " — Emby Remote Player";
        _viewModel.Title = _plan.Item.Name;
        _viewModel.BottomTitle = BuildBottomTitle(_plan.Item);
        var total = GetTotalTicks();
        _viewModel.DurationSeconds = Math.Max(1, TimeSpan.FromTicks(total).TotalSeconds);
        _viewModel.PositionSeconds = Math.Min(_viewModel.DurationSeconds, TimeSpan.FromTicks(Math.Max(0, _plan.ResumePositionTicks)).TotalSeconds);
        EpisodeButton.Visibility = IsEpisode(_plan.Item) ? Visibility.Visible : Visibility.Collapsed;
        EpisodePanel.Visibility = Visibility.Collapsed;
        EpisodeItemsControl.ItemsSource = null;
        EpisodePanelTitleText.Text = IsEpisode(_plan.Item) ? "正在加载剧集…" : string.Empty;
        ConfigureMediaVersionSelector();
        ApplyCapabilityVisibility();
    }

    private void ConfigureMediaVersionSelector()
    {
        if (MediaVersionsComboBox is null) return;
        try
        {
            _updatingMediaVersions = true;
            var sources = (_plan.AvailableMediaSources ?? Array.Empty<MediaSourceInfo>())
                .Where(source => source is not null)
                .ToList();
            var options = sources.Select(source => new MediaSourceOption(source)).ToList();
            MediaVersionsComboBox.ItemsSource = options;
            var selected = options.FirstOrDefault(option => MediaSourceMatches(option.Source, _plan.MediaSource))
                           ?? options.FirstOrDefault();
            MediaVersionsComboBox.SelectedItem = selected;
            MediaVersionsComboBox.Tag = "⇄";
            MediaVersionsComboBox.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingMediaVersions = false; }
    }

    private static bool MediaSourceMatches(MediaSourceInfo left, MediaSourceInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id))
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(left.Path) && !string.IsNullOrWhiteSpace(right.Path))
            return string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Container, right.Container, StringComparison.OrdinalIgnoreCase)
               && left.Bitrate == right.Bitrate;
    }

    private static MediaSourceInfo? FindEquivalentMediaSource(IEnumerable<MediaSourceInfo> sources, MediaSourceInfo requested)
        => sources.FirstOrDefault(source => MediaSourceMatches(source, requested));

    private static MediaStreamInfo? FindSelectedStream(MediaSourceInfo source, string type, int? streamIndex)
        => streamIndex is int wanted
            ? source.MediaStreams.FirstOrDefault(stream => stream.Index == wanted && string.Equals(stream.Type, type, StringComparison.OrdinalIgnoreCase))
            : null;

    private static int? MatchStreamIndex(MediaSourceInfo source, string type, MediaStreamInfo? previous, int? fallback)
    {
        var streams = source.MediaStreams.Where(stream => string.Equals(stream.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
        if (previous is null) return fallback;

        var exactTitle = streams.FirstOrDefault(stream =>
            !string.IsNullOrWhiteSpace(previous.DisplayTitle)
            && string.Equals(stream.DisplayTitle, previous.DisplayTitle, StringComparison.OrdinalIgnoreCase));
        if (exactTitle is not null) return exactTitle.Index;

        var semantic = streams.FirstOrDefault(stream =>
            string.Equals(stream.Language, previous.Language, StringComparison.OrdinalIgnoreCase)
            && string.Equals(stream.Codec, previous.Codec, StringComparison.OrdinalIgnoreCase)
            && (previous.Channels is null || stream.Channels == previous.Channels));
        return semantic?.Index ?? fallback;
    }

    private static bool IsEpisode(EmbyItem item) =>
        string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(item.SeriesId)
        || (!string.IsNullOrWhiteSpace(item.SeriesName) && (item.IndexNumber.HasValue || item.ParentIndexNumber.HasValue));

    private void ApplyCapabilityVisibility()
    {
        var streams = _plan.MediaSource.MediaStreams ?? [];
        var audioCount = streams.Count(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase));
        var subtitleCount = streams.Count(stream => string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase));
        AudioTracksComboBox.Visibility = audioCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleTracksComboBox.Visibility = subtitleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        TranscodeButton.Visibility = CanUseTranscoding() && _plan.DirectPlaySupported ? Visibility.Visible : Visibility.Collapsed;
        PreviousEpisodeButton.Visibility = _previousEpisode is null ? Visibility.Collapsed : Visibility.Visible;
        NextEpisodeButton.Visibility = _nextEpisode is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool CanUseTranscoding() => _allowTranscodingByPolicy &&
        (_plan.MediaSource.SupportsTranscoding || !string.IsNullOrWhiteSpace(_plan.MediaSource.TranscodingUrl)) &&
        _plan.TranscodeUri is not null;

    // Compatibility fallback is intentionally broader than the manual "quality" button.
    // Older Emby servers sometimes omit capability flags while their generic HLS endpoint works.
    private bool CanTryCompatibilityTranscode() => _plan.TranscodeUri is not null;

    private void StartCurrentPlan(long absoluteStartTicks)
    {
        // Keep v0.5 behaviour: when the server explicitly says direct playback is unavailable,
        // prefer the compatible HLS path. Otherwise let LibVLC decode the original stream first.
        if (!_plan.DirectPlaySupported && _plan.TranscodingSupported && _plan.TranscodeUri is not null)
            StartMedia(_api.BuildTranscodeUri(_plan, absoluteStartTicks), PlaybackMethod.Transcode, absoluteStartTicks);
        else
            StartMedia(_plan.DirectUri, GetDirectMethod(), absoluteStartTicks);
    }

    private void StartMedia(Uri uri, PlaybackMethod method, long absoluteStartTicks)
    {
        _playingObserved = false;
        _bufferingPercent = 0f;
        _defaultExternalSubtitleAttached = false;
        ResetNetworkTelemetry();
        ErrorBanner.Visibility = Visibility.Collapsed;
        _startupTimer.Stop();
        _mediaPlayer.Stop();
        _media?.Dispose();
        _media = new Media(_libVlc, uri);

        _activeNetworkCachingMilliseconds = GetAdaptiveNetworkCachingMilliseconds(method);
        _media.AddOption($":network-caching={_activeNetworkCachingMilliseconds}");
        _media.AddOption(":http-reconnect");
        if (_forceSoftwareDecode)
        {
            // Per-media override: if a GPU/driver cannot decode this specific file, retry it
            // with FFmpeg software decoding without changing the user's global preference.
            _media.AddOption(":avcodec-hw=none");
        }

        _method = method;
        NetworkDebugService.LogPlayerStream(uri, method.ToString());
        _streamBaseTicks = method == PlaybackMethod.Transcode ? Math.Max(0, absoluteStartTicks) : 0;
        _pendingSeekTicks = method == PlaybackMethod.Transcode ? 0 : Math.Max(0, absoluteStartTicks);
        ModeText.Text = method == PlaybackMethod.Transcode ? "转码" : method == PlaybackMethod.DirectStream ? "直连" : "原画";
        ShowLoading(method == PlaybackMethod.Transcode ? "正在准备兼容播放…" : "正在加载视频…",
            $"{ModeText.Text} · 正在建立媒体流");

        _mediaPlayer.Media = _media;
        // Do not attach any network subtitle before Play(). Even a single slow subtitle endpoint
        // can delay the video decoder. The preferred subtitle is attached after Playing.
        if (!_mediaPlayer.Play()) throw new InvalidOperationException("LibVLC 无法启动该媒体流。");
        _startupTimer.Start();
    }

    private int GetAdaptiveNetworkCachingMilliseconds(PlaybackMethod method)
    {
        var configured = Math.Clamp(_settings.NetworkCachingMilliseconds, 500, 15000);
        // VOD on the public internet benefits from a small real prebuffer. Keep direct streams
        // responsive while giving HLS/transcoded streams a little more headroom.
        return method == PlaybackMethod.Transcode ? Math.Max(configured, 4500) : Math.Max(configured, 3000);
    }

    private void AttachDefaultExternalSubtitle()
    {
        if (_defaultExternalSubtitleAttached) return;
        try
        {
            var subtitles = _plan.MediaSource.MediaStreams
                .Where(stream => string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)
                                 && stream.IsExternal
                                 && !string.IsNullOrWhiteSpace(stream.DeliveryUrl))
                .ToList();
            if (subtitles.Count == 0) return;

            var preferredIndex = _plan.MediaSource.DefaultSubtitleStreamIndex;
            var preferred = subtitles.FirstOrDefault(stream => stream.Index == preferredIndex)
                            ?? subtitles.FirstOrDefault(stream => stream.IsDefault);
            if (preferred?.DeliveryUrl is null) return;

            var subtitleUri = _api.ResolveMediaUri(preferred.DeliveryUrl);
            if (_mediaPlayer.AddSlave(MediaSlaveType.Subtitle, subtitleUri.ToString(), true))
            {
                _selectedSubtitleStreamIndex = preferred.Index;
                _defaultExternalSubtitleAttached = true;
            }
        }
        catch (Exception ex)
        {
            // Subtitle failure is non-fatal. The video must continue to start/play.
            LogService.Warning("Unable to attach default external subtitle.", ex);
        }
    }

    private void MediaPlayer_Opening(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            ShowLoading("正在连接媒体…", $"{ModeText.Text} · 建立流连接");
        }));
    }

    private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        var cache = float.IsFinite(e.Cache) ? Math.Clamp(e.Cache, 0f, 100f) : 0f;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            _bufferingPercent = cache;
            UpdateBufferVisualization();
            if (cache < 99.5f)
                ShowLoading("正在缓冲…", "网络波动，正在补充播放数据", cache);
            else if (_playingObserved)
                HideLoading();
        }));
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_closing) return;
            _playingObserved = true;
            _bufferingPercent = 100f;
            _startupTimer.Stop();
            SetPlayPauseVisual(isPlaying: true);
            HideLoading();
            UpdateBufferVisualization();
            ApplyPlaybackSpeed();
            if (_pendingSeekTicks > 0)
            {
                var target = _pendingSeekTicks;
                _pendingSeekTicks = 0;
                await Task.Delay(350);
                if (!_closing && _method != PlaybackMethod.Transcode) _mediaPlayer.Time = target / TimeSpan.TicksPerMillisecond;
            }
            if (_pauseAfterMediaStart)
            {
                _pauseAfterMediaStart = false;
                await Task.Delay(90);
                if (!_closing) _mediaPlayer.Pause();
            }
            await Task.Delay(220);
            AttachDefaultExternalSubtitle();
            RefreshTracks();
            _ = RefreshTracksLaterAsync();
            if (!_startedReported)
            {
                _startedReported = true;
                await ReportStartedSafeAsync();
            }
        }));
    }

    private async Task RefreshTracksLaterAsync()
    {
        await Task.Delay(1100);
        if (!_closing) await Dispatcher.InvokeAsync(RefreshTracks);
    }

    private void MediaPlayer_Paused(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => SetPlayPauseVisual(isPlaying: false)));

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_closing || _isSeeking || _seekCommitInProgress) return;
        var ticks = Math.Max(0, e.Time) * TimeSpan.TicksPerMillisecond;
        _lastPositionTicks = _method == PlaybackMethod.Transcode ? _streamBaseTicks + ticks : ticks;
    }

    private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        var total = Math.Max(0, e.Length) * TimeSpan.TicksPerMillisecond + (_method == PlaybackMethod.Transcode ? _streamBaseTicks : 0);
        if (total <= 0) return;
        _runtimeTotalTicks = Math.Max(_runtimeTotalTicks, total);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var effective = GetTotalTicks();
            _viewModel.DurationSeconds = Math.Max(1, TimeSpan.FromTicks(effective).TotalSeconds);
        }));
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(async () =>
    {
        if (_closing || _switchingEpisode || _switchingMediaSource) return;
        _lastPositionTicks = GetTotalTicks();
        if (_autoPlayNextEpisode && _nextEpisode is not null) await SwitchEpisodeAsync(_nextEpisode);
        else Close();
    }));

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(async () =>
    {
        if (_closing || _switchingMediaSource) return;
        await HandlePlaybackFailureAsync("播放器无法读取当前媒体流。");
    }));

    private async void StartupTimer_Tick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        if (!_playingObserved) await HandlePlaybackFailureAsync("媒体加载超时。 ");
    }

    private async Task HandlePlaybackFailureAsync(string reason)
    {
        if (_closing || _failureHandling) return;
        _failureHandling = true;
        try
        {
            var resume = Math.Max(GetCurrentPositionTicks(), _plan.ResumePositionTicks);

            // Stage 1: hardware decode can fail on individual HEVC/10-bit/HDR/old GPU drivers.
            // Retry the original stream with software decoding before asking Emby to transcode.
            if (_method != PlaybackMethod.Transcode && _settings.HardwareDecoding && !_softwareDecodeFallbackAttempted)
            {
                _softwareDecodeFallbackAttempted = true;
                _forceSoftwareDecode = true;
                ShowLoading("正在切换解码器…", "硬件解码不可用，改用软件解码");
                StartMedia(_plan.DirectUri, GetDirectMethod(), resume);
                return;
            }

            // Stage 2: restore the v0.5 generic Emby HLS fallback. This is deliberately
            // attempted even when older servers omit SupportsTranscoding flags. If the account
            // truly forbids transcoding, the endpoint will fail and we stop cleanly.
            if (_method != PlaybackMethod.Transcode && !_fallbackAttempted && CanTryCompatibilityTranscode())
            {
                _fallbackAttempted = true;
                ShowLoading("正在切换兼容播放…", "请求 Emby H.264 / AAC 流");
                await SwitchToCompatibilityTranscodeAsync(resume);
                return;
            }

            // Stage 3: if HLS itself failed, one last direct/software attempt is useful for
            // servers that report direct-play support incorrectly.
            if (_method == PlaybackMethod.Transcode && !_softwareDecodeFallbackAttempted)
            {
                _softwareDecodeFallbackAttempted = true;
                _forceSoftwareDecode = true;
                ShowLoading("正在恢复播放…", "兼容流不可用，回退本地软件解码");
                StartMedia(_plan.DirectUri, GetDirectMethod(), resume);
                return;
            }

            HideLoading();
            ShowError(reason + "已尝试硬件解码、软件解码和 Emby 兼容流。请打开 F12 网络控制台查看具体媒体错误。");
        }
        catch (Exception ex)
        {
            LogService.Warning("Automatic playback recovery failed.", ex);
            if (!_closing && _method == PlaybackMethod.Transcode && !_softwareDecodeFallbackAttempted)
            {
                try
                {
                    _softwareDecodeFallbackAttempted = true;
                    _forceSoftwareDecode = true;
                    ShowLoading("正在恢复播放…", "兼容流启动失败，回退本地软件解码");
                    StartMedia(_plan.DirectUri, GetDirectMethod(), Math.Max(GetCurrentPositionTicks(), _plan.ResumePositionTicks));
                    return;
                }
                catch (Exception directEx)
                {
                    LogService.Warning("Final direct/software fallback failed.", directEx);
                }
            }
            HideLoading();
            ShowError(reason + "自动兼容播放失败：" + ex.Message);
        }
        finally { _failureHandling = false; }
    }

    private async Task SwitchToCompatibilityTranscodeAsync(long startTicks)
    {
        if (_plan.TranscodeUri is null)
        {
            ShowError("服务器没有可尝试的兼容播放地址。");
            return;
        }

        try
        {
            if (_method == PlaybackMethod.Transcode)
            {
                try { await _api.StopActiveEncodingsAsync(); } catch { }
            }
            StartMedia(_api.BuildTranscodeUri(_plan, Math.Max(0, startTicks)), PlaybackMethod.Transcode, startTicks);
            await ReportProgressSafeAsync("QualityChange");
        }
        catch (Exception ex)
        {
            LogService.Warning("Compatibility transcoding fallback failed.", ex);
            throw;
        }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _isSeeking) return;
        if (_seekCommitInProgress)
        {
            // LibVLC resolves remote seeks asynchronously. Keep the UI anchored to the user's
            // requested timestamp instead of letting the 250 ms refresh loop snap it back.
            _viewModel.PositionSeconds = TimeSpan.FromTicks(Math.Max(0, _seekCommitTargetTicks)).TotalSeconds;
            UpdatePlaybackTimestampBubble(_viewModel.PositionSeconds);
            UpdateNetworkTelemetry();
            UpdateMuteButton();
            return;
        }
        var current = GetCurrentPositionTicks();
        var total = GetTotalTicks();
        if (total > 0)
            _viewModel.DurationSeconds = Math.Max(1, TimeSpan.FromTicks(total).TotalSeconds);
        _viewModel.PositionSeconds = TimeSpan.FromTicks(total > 0 ? Math.Min(current, total) : current).TotalSeconds;
        UpdateBufferVisualization();
        UpdateNetworkTelemetry();
        UpdateMuteButton();
        UpdatePlaybackTimestampBubble();
    }

    private async void ReportTimer_Tick(object? sender, EventArgs e) => await ReportProgressSafeAsync("TimeUpdate");

    private void TogglePlayback(bool showGesture)
    {
        if (_closing || _media is null) return;

        // Command LibVLC first. Yielding a Render frame before Pause()/Play() used to defer the
        // real state change while the UI already showed "paused" — audio kept playing a beat
        // after the picture froze. Feedback paints right after the call returns, same frame.
        var willPause = _mediaPlayer.IsPlaying;
        if (willPause)
        {
            _mediaPlayer.Pause();
            _ = ReportProgressSafeAsync("Pause");
        }
        else
        {
            if (!_mediaPlayer.Play())
            {
                SetPlayPauseVisual(isPlaying: false);
                return;
            }
            _ = ReportProgressSafeAsync("Unpause");
        }

        if (showGesture) ShowPlaybackGesture(paused: willPause);
        SetPlayPauseVisual(isPlaying: !willPause);
    }

    private void SetPlayPauseVisual(bool isPlaying) => _viewModel.IsPlaying = isPlaying;

    private void ShowPlaybackGesture(bool paused)
    {
        if (PlaybackGestureOverlay is null || PlaybackGestureBadge is null || PlaybackGesturePlayIcon is null || PlaybackGesturePauseIcon is null) return;

        var generation = ++_playbackGestureGeneration;
        PlaybackGesturePlayIcon.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        PlaybackGesturePauseIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        PlaybackGestureOverlay.Visibility = Visibility.Visible;

        PlaybackGestureBadge.BeginAnimation(OpacityProperty, null);
        PlaybackGestureScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        PlaybackGestureScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        PlaybackGestureBadge.Opacity = 0;
        PlaybackGestureScale.ScaleX = PlaybackGestureScale.ScaleY = 0.92;

        var easeOut = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var easeIn = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };

        var opacity = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(245),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        opacity.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.92, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(48)), easeOut));
        opacity.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(0.92, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105))));
        opacity.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(245)), easeIn));
        opacity.Completed += (_, _) =>
        {
            if (!_closing && generation == _playbackGestureGeneration && PlaybackGestureOverlay is not null)
            {
                PlaybackGestureOverlay.Visibility = Visibility.Collapsed;
                PlaybackGestureBadge.Opacity = 0;
            }
        };

        var scale = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(245),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        scale.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.92, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scale.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.035, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(82)), easeOut));
        scale.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)), easeOut));

        PlaybackGestureBadge.BeginAnimation(OpacityProperty, opacity, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        PlaybackGestureScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        PlaybackGestureScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    private void SeekRelative(int seconds)
    {
        var total = GetTotalTicks();
        var target = Math.Max(0, GetCurrentPositionTicks() + seconds * TimeSpan.TicksPerSecond);
        if (total > 0) target = Math.Min(total, target);
        _ = SeekToAsync(target);
    }

    private void PositionThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_closing) return;
        _isSeeking = true;
        _thumbSeekDrag = true;
        _seekDragTargetTicks = SecondsToTicks(PositionSlider.Value);
        _viewModel.BeginInteraction();
        UpdateSeekPreviewFromCurrentCursor(PositionSlider.Value);
    }

    private void PositionThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_thumbSeekDrag || !_isSeeking) return;
        _seekDragTargetTicks = SecondsToTicks(PositionSlider.Value);
        _viewModel.PositionSeconds = PositionSlider.Value;
        UpdateSeekPreviewFromCurrentCursor(PositionSlider.Value);
    }

    private void PositionThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_thumbSeekDrag || !_isSeeking) return;
        _seekDragTargetTicks = SecondsToTicks(PositionSlider.Value);
        FinishSeekInteraction();
    }

    private void AttachSeekRailHandlers()
    {
        // Track clicks are handled on the actual RepeatButtons, not on the outer Slider.
        // This avoids WPF Slider class handling / mouse capture stealing the click before
        // our absolute timestamp seek is committed. Thumb dragging remains untouched.
        PositionSlider.ApplyTemplate();
        var track = PositionSlider.Template.FindName("PART_Track", PositionSlider) as Track;
        if (track is null)
        {
            LogService.Warning("Seek rail handlers were not attached because PART_Track was not found.");
            return;
        }

        var handler = new MouseButtonEventHandler(PositionRail_PreviewMouseLeftButtonDown);
        track.DecreaseRepeatButton?.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handler, true);
        track.IncreaseRepeatButton?.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handler, true);
    }

    private async void PositionRail_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing || PositionSlider.ActualWidth <= 1 || PositionSlider.Maximum <= PositionSlider.Minimum) return;

        // We are attached directly to the rail RepeatButtons. Mark this handled so their built-in
        // LargeChange command cannot alter the value after our absolute timestamp calculation.
        e.Handled = true;

        var point = e.GetPosition(PositionSlider);
        var value = GetSliderValueFromPointer(point);
        var target = SecondsToTicks(value);

        _thumbSeekDrag = false;
        _isSeeking = false;
        _seekDragTargetTicks = target;

        // Move the visible playhead synchronously without replacing the existing Binding.
        PositionSlider.SetCurrentValue(RangeBase.ValueProperty, value);
        _viewModel.PositionSeconds = value;
        UpdateSeekPreview(point, value);
        UpdatePlaybackTimestampBubble(value);
        ShowPlaybackChrome();

        LogService.Info($"Track click seek committed. target={FormatTime(target)} ticks={target}; current={FormatTime(GetCurrentPositionTicks())}; method={_method}; seekable={_mediaPlayer.IsSeekable}");

        // Await the exact same final seek pipeline used by Thumb.DragCompleted. No MouseUp,
        // RepeatButton command, capture, or Slider class handler participates in this path.
        _viewModel.BeginInteraction();
        try
        {
            await SeekToAsync(target);
        }
        finally
        {
            SeekPreviewBubble.Visibility = Visibility.Collapsed;
            SeekPreviewPointer.Visibility = Visibility.Collapsed;
            _viewModel.EndInteraction();
            ShowPlaybackChrome();
        }
    }

    private void PositionSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (PositionSlider.ActualWidth <= 1) return;
        var point = e.GetPosition(PositionSlider);

        if (_thumbSeekDrag && _isSeeking)
        {
            UpdateSeekPreviewFromCurrentCursor(PositionSlider.Value);
        }
        else if (!_isSeeking)
        {
            // When the pointer is over the Thumb itself, the preview must describe the actual
            // playhead value rather than an arbitrary pixel inside the Thumb's hit target.
            // This keeps e.g. 01:54 stable instead of jumping several seconds/minutes.
            if (FindVisualParent<Thumb>(e.OriginalSource as DependencyObject) is not null
                && TryGetSeekThumbCenter(out var thumbCenter))
            {
                UpdateSeekPreview(thumbCenter, PositionSlider.Value);
            }
            else
            {
                UpdateSeekPreview(point, GetSliderValueFromPointer(point));
            }
        }
    }

    private void PositionSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isSeeking) return;
        SeekPreviewBubble.Visibility = Visibility.Collapsed;
        SeekPreviewPointer.Visibility = Visibility.Collapsed;
    }


    private void UpdateSeekFromTrackPointer(System.Windows.Point sliderPoint)
    {
        var value = GetSliderValueFromPointer(sliderPoint);
        _viewModel.PositionSeconds = value;
        _seekDragTargetTicks = SecondsToTicks(value);
        UpdateSeekPreviewFromCurrentCursor(value);
    }

    private double GetSliderValueFromPointer(System.Windows.Point sliderPoint)
    {
        var track = PositionSlider.Template.FindName("PART_Track", PositionSlider) as Track;
        if (track is null || track.ActualWidth <= 0)
        {
            var ratioFallback = Math.Clamp(sliderPoint.X / Math.Max(1, PositionSlider.ActualWidth), 0, 1);
            return PositionSlider.Minimum + ratioFallback * (PositionSlider.Maximum - PositionSlider.Minimum);
        }

        var pointOnTrack = PositionSlider.TranslatePoint(sliderPoint, track);
        var thumbWidth = track.Thumb?.ActualWidth > 0 ? track.Thumb.ActualWidth : 20.0;
        var usable = Math.Max(1.0, track.ActualWidth - thumbWidth);
        var centerX = Math.Clamp(pointOnTrack.X, thumbWidth / 2.0, track.ActualWidth - thumbWidth / 2.0);
        var ratio = Math.Clamp((centerX - thumbWidth / 2.0) / usable, 0.0, 1.0);
        return PositionSlider.Minimum + ratio * (PositionSlider.Maximum - PositionSlider.Minimum);
    }

    private bool TryGetSeekThumbCenter(out System.Windows.Point center)
    {
        center = default;
        try
        {
            var track = PositionSlider.Template.FindName("PART_Track", PositionSlider) as Track;
            var thumb = track?.Thumb;
            if (thumb is null || thumb.ActualWidth <= 0 || thumb.ActualHeight <= 0) return false;

            center = thumb.TranslatePoint(
                new System.Windows.Point(thumb.ActualWidth / 2.0, thumb.ActualHeight / 2.0),
                PositionSlider);
            return double.IsFinite(center.X) && double.IsFinite(center.Y);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateSeekPreviewFromCurrentCursor(double seconds)
    {
        if (!GetCursorPos(out var point)) return;
        try
        {
            var local = PositionSlider.PointFromScreen(new System.Windows.Point(point.X, point.Y));
            local.X = Math.Clamp(local.X, 0, PositionSlider.ActualWidth);
            local.Y = PositionSlider.ActualHeight / 2.0;
            UpdateSeekPreview(local, seconds);
        }
        catch { }
    }

    private void UpdateSeekPreview(System.Windows.Point sliderPoint, double seconds)
    {
        var width = Math.Max(1.0, PositionSlider.ActualWidth);
        var pointerX = Math.Clamp(sliderPoint.X, 0.0, width);
        var targetTicks = SecondsToTicks(seconds);

        SeekPreviewText.Text = FormatTime(targetTicks);
        SeekPreviewBubble.Visibility = Visibility.Visible;
        SeekPreviewPointer.Visibility = Visibility.Visible;
        UpdatePlaybackTimestampBubble(_isSeeking ? seconds : null);
        SeekPreviewBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bubbleWidth = Math.Max(60.0, SeekPreviewBubble.DesiredSize.Width);
        var bubbleLeft = Math.Clamp(pointerX - bubbleWidth / 2.0, 0.0, Math.Max(0.0, width - bubbleWidth));
        Canvas.SetLeft(SeekPreviewBubble, bubbleLeft);
        Canvas.SetLeft(SeekPreviewPointer, Math.Clamp(pointerX - 5.0, 0.0, Math.Max(0.0, width - 10.0)));
    }

    private void UpdatePlaybackTimestampBubble(double? overrideSeconds = null)
    {
        if (_closing || PlaybackTimestampBubble is null || PlaybackTimestampCanvas is null || PositionSlider is null) return;
        try
        {
            var min = PositionSlider.Minimum;
            var max = Math.Max(min + 0.001, PositionSlider.Maximum);
            var value = Math.Clamp(overrideSeconds ?? _viewModel.PositionSeconds, min, max);
            var ratio = Math.Clamp((value - min) / (max - min), 0.0, 1.0);

            var track = PositionSlider.Template.FindName("PART_Track", PositionSlider) as Track;
            double centerX;
            if (track is not null && track.ActualWidth > 0)
            {
                var thumbWidth = track.Thumb?.ActualWidth > 0 ? track.Thumb.ActualWidth : 44.0;
                var usable = Math.Max(1.0, track.ActualWidth - thumbWidth);
                var centerOnTrack = thumbWidth / 2.0 + ratio * usable;
                centerX = track.TranslatePoint(new System.Windows.Point(centerOnTrack, track.ActualHeight / 2.0), PlaybackTimestampCanvas).X;
            }
            else
            {
                centerX = ratio * Math.Max(1.0, PositionSlider.ActualWidth);
            }

            PlaybackTimestampBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var bubbleWidth = Math.Max(84.0, PlaybackTimestampBubble.DesiredSize.Width);
            Canvas.SetLeft(PlaybackTimestampBubble, Math.Clamp(centerX - bubbleWidth / 2.0, 0.0, Math.Max(0.0, PositionSlider.ActualWidth - bubbleWidth)));
        }
        catch { }
    }

    private void FinishSeekInteraction()
    {
        if (!_isSeeking) return;
        var target = Math.Max(0, _seekDragTargetTicks);
        _thumbSeekDrag = false;
        _isSeeking = false;
        if (PositionSlider.IsMouseCaptured) PositionSlider.ReleaseMouseCapture();
        SeekPreviewBubble.Visibility = Visibility.Collapsed;
        SeekPreviewPointer.Visibility = Visibility.Collapsed;
        _viewModel.EndInteraction();
        LogService.Info($"Thumb drag seek committed. target={FormatTime(target)} ticks={target}; current={FormatTime(GetCurrentPositionTicks())}; method={_method}; seekable={_mediaPlayer.IsSeekable}");
        _ = SeekToAsync(target);
        ShowPlaybackChrome();
    }

    private static long SecondsToTicks(double seconds) =>
        (long)(Math.Max(0, seconds) * TimeSpan.TicksPerSecond);

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private async Task SeekToAsync(long targetTicks)
    {
        var generation = ++_seekOperationGeneration;
        var total = GetTotalTicks();
        targetTicks = Math.Max(0, total > 0 ? Math.Min(total, targetTicks) : targetTicks);
        _lastPositionTicks = targetTicks;
        _seekCommitTargetTicks = targetTicks;
        _seekCommitInProgress = true;
        _viewModel.PositionSeconds = TimeSpan.FromTicks(targetTicks).TotalSeconds;
        UpdatePlaybackTimestampBubble(_viewModel.PositionSeconds);

        try
        {
            // For an existing timestamp/HLS stream, start a new Emby stream at the requested
            // absolute timestamp instead of trying to seek inside the current segment timeline.
            if (_method == PlaybackMethod.Transcode)
            {
                ShowLoading("正在跳转…", $"重新请求时间片 · {FormatTime(targetTicks)}");
                await SwitchToCompatibilityTranscodeAsync(targetTicks);
                _lastPositionTicks = targetTicks;
                return;
            }

            // Direct media traffic is performed by LibVLC itself. Its HTTP Range requests do not
            // pass through EmbyApiClient, so they are not listed in our managed Network Inspector.
            // Try the native Range seek first because it preserves DirectPlay quality.
            if (_mediaPlayer.IsSeekable)
            {
                try
                {
                    ShowLoading("正在跳转…", $"定位至 {FormatTime(targetTicks)}");
                    _mediaPlayer.Time = targetTicks / TimeSpan.TicksPerMillisecond;

                    long actualTicks = 0;
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(200);
                        if (_closing || generation != _seekOperationGeneration) return;
                        actualTicks = Math.Max(0, _mediaPlayer.Time) * TimeSpan.TicksPerMillisecond;
                        if (Math.Abs(actualTicks - targetTicks) <= TimeSpan.FromSeconds(5).Ticks)
                        {
                            _lastPositionTicks = actualTicks;
                            await ReportProgressSafeAsync("TimeUpdate");
                            HideLoading();
                            return;
                        }
                    }
                    LogService.Warning($"Native seek did not land within 1s. target={targetTicks}; actual={actualTicks}. Switching to Emby timestamp stream.");
                }
                catch (Exception ex)
                {
                    LogService.Warning("Native direct-stream seek failed; switching to Emby timestamp stream.", ex);
                }
            }

            // Some remote containers report IsSeekable=true but ignore the actual byte-range seek.
            // In that case explicitly request a new HLS stream with StartTimeTicks so the server
            // begins producing media at the exact requested point.
            if (CanTryCompatibilityTranscode())
            {
                try
                {
                    ShowLoading("正在跳转…", $"重新请求时间片 · {FormatTime(targetTicks)}");
                    await SwitchToCompatibilityTranscodeAsync(targetTicks);
                    _lastPositionTicks = targetTicks;
                    return;
                }
                catch (Exception ex)
                {
                    LogService.Warning("Timestamp compatibility seek failed.", ex);
                }
            }

            // Last local fallback for servers that refuse the timestamp endpoint.
            _mediaPlayer.Time = targetTicks / TimeSpan.TicksPerMillisecond;
            await Task.Delay(250);
            if (_closing || generation != _seekOperationGeneration) return;
            var finalTicks = Math.Max(0, _mediaPlayer.Time) * TimeSpan.TicksPerMillisecond;
            if (Math.Abs(finalTicks - targetTicks) <= TimeSpan.FromSeconds(5).Ticks)
            {
                _lastPositionTicks = finalTicks;
                await ReportProgressSafeAsync("TimeUpdate");
                HideLoading();
                return;
            }

            ShowError("当前媒体源没有接受指定时间跳转，服务器也未提供可用的时间戳流。");
        }
        catch (Exception ex)
        {
            ShowError("当前媒体流不支持跳转到指定时间：" + ex.Message);
        }
        finally
        {
            // A later click/drag supersedes this operation. A stale seek must never clear the
            // commit lock or snap the playhead back after a newer target has already been chosen.
            if (generation == _seekOperationGeneration)
            {
                _seekCommitInProgress = false;
                var actual = GetCurrentPositionTicks();
                if (actual > 0) _lastPositionTicks = actual;
                var visualTicks = _method == PlaybackMethod.Transcode
                    ? Math.Max(targetTicks, actual)
                    : actual > 0 ? actual : targetTicks;
                _viewModel.PositionSeconds = TimeSpan.FromTicks(Math.Max(0, visualTicks)).TotalSeconds;
                UpdatePlaybackTimestampBubble(_viewModel.PositionSeconds);
            }
        }
    }

    private void ToggleMute()
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        UpdateMuteButton();
        _ = ReportProgressSafeAsync("VolumeChange");
        ShowPlaybackChrome();
    }

    private void UpdateMuteButton()
    {
        if (MuteButton is null || VolumeWaveSmall is null || VolumeWaveLarge is null || VolumeMuteSlash is null) return;
        var muted = _mediaPlayer.Mute || _mediaPlayer.Volume <= 0;
        _viewModel.IsMuted = muted;
        VolumeMuteSlash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeWaveSmall.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        VolumeWaveLarge.Visibility = !muted && _mediaPlayer.Volume >= 45 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyVolumeFromViewModel(double value)
    {
        if (_closing) return;
        _mediaPlayer.Volume = (int)Math.Clamp(value, 0, 100);
        if (value > 0 && _mediaPlayer.Mute) _mediaPlayer.Mute = false;
        UpdateMuteButton();
    }

    private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.BeginInteraction();
        ShowPlaybackChrome();
    }

    private async void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _settings.PreferredVolume = _mediaPlayer.Volume;
        _saveSettings();
        await ReportProgressSafeAsync("VolumeChange");
        _viewModel.EndInteraction();
        ShowPlaybackChrome();
    }

    private void VolumeSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _viewModel.EndInteraction();
        ShowPlaybackChrome();
    }

    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var step = Math.Abs(e.Delta) >= 120 ? 5 : Math.Max(1, (int)Math.Round(Math.Abs(e.Delta) / 24d));
        _viewModel.Volume = Math.Clamp(_viewModel.Volume + (e.Delta > 0 ? step : -step), 0, 100);
        _settings.PreferredVolume = _mediaPlayer.Volume;
        _saveSettings();
        ShowPlaybackChrome();
        e.Handled = true;
    }

    private void RefreshTracks()
    {
        try
        {
            _updatingAudioTracks = true;
            var embyAudio = _plan.MediaSource.MediaStreams.Where(s => string.Equals(s.Type, "Audio", StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Index).ToList();
            var audio = _mediaPlayer.AudioTrackDescription.Select((track, index) =>
            {
                var stream = index < embyAudio.Count ? embyAudio[index] : null;
                var name = !string.IsNullOrWhiteSpace(stream?.DisplayTitle) ? stream!.DisplayTitle! : !string.IsNullOrWhiteSpace(track.Name) ? track.Name! : $"音轨 {track.Id}";
                return new TrackOption(track.Id, name, stream?.Index);
            }).ToList();
            AudioTracksComboBox.ItemsSource = audio;
            var selected = audio.FirstOrDefault(t => t.EmbyStreamIndex == _selectedAudioStreamIndex) ?? audio.FirstOrDefault(t => t.Id == _mediaPlayer.AudioTrack) ?? audio.FirstOrDefault();
            AudioTracksComboBox.SelectedItem = selected;
            if (selected?.EmbyStreamIndex is int streamIndex) _selectedAudioStreamIndex = streamIndex;
            AudioTracksComboBox.Visibility = Math.Max(audio.Count, embyAudio.Count) > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { LogService.Warning("Unable to enumerate audio tracks.", ex); }
        finally { _updatingAudioTracks = false; }

        try
        {
            _updatingSubtitleTracks = true;
            var embySubs = _plan.MediaSource.MediaStreams.Where(s => string.Equals(s.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Index).ToList();
            var subtitleIndex = 0;
            var subtitles = _mediaPlayer.SpuDescription.Select(track =>
            {
                if (track.Id == -1) return new TrackOption(-1, "关闭字幕", null);
                var stream = subtitleIndex < embySubs.Count ? embySubs[subtitleIndex++] : null;
                var name = !string.IsNullOrWhiteSpace(stream?.DisplayTitle) ? stream!.DisplayTitle! : !string.IsNullOrWhiteSpace(track.Name) ? track.Name! : $"字幕 {track.Id}";
                return new TrackOption(track.Id, name, stream?.Index);
            }).ToList();
            if (subtitles.All(t => t.Id != -1)) subtitles.Insert(0, new TrackOption(-1, "关闭字幕", null));
            SubtitleTracksComboBox.ItemsSource = subtitles;
            var selected = _selectedSubtitleStreamIndex is int wanted ? subtitles.FirstOrDefault(t => t.EmbyStreamIndex == wanted) : subtitles.FirstOrDefault(t => t.Id == -1);
            selected ??= subtitles.FirstOrDefault(t => t.Id == _mediaPlayer.Spu) ?? subtitles.FirstOrDefault();
            SubtitleTracksComboBox.SelectedItem = selected;
            _selectedSubtitleStreamIndex = selected?.Id == -1 ? null : selected?.EmbyStreamIndex ?? _selectedSubtitleStreamIndex;
            SubtitleTracksComboBox.Visibility = embySubs.Count > 0 || subtitles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { LogService.Warning("Unable to enumerate subtitle tracks.", ex); }
        finally { _updatingSubtitleTracks = false; }
    }

    private async void AudioTracksComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudioTracks || AudioTracksComboBox.SelectedItem is not TrackOption option) return;
        _mediaPlayer.SetAudioTrack(option.Id);
        if (option.EmbyStreamIndex is int streamIndex) _selectedAudioStreamIndex = streamIndex;
        await ReportProgressSafeAsync("AudioTrackChange");
        ShowPlaybackChrome();
    }

    private async void SubtitleTracksComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSubtitleTracks || SubtitleTracksComboBox.SelectedItem is not TrackOption option) return;
        _mediaPlayer.SetSpu(option.Id);
        _selectedSubtitleStreamIndex = option.Id == -1 ? null : option.EmbyStreamIndex;
        await ReportProgressSafeAsync("SubtitleTrackChange");
        ShowPlaybackChrome();
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is not SpeedOption option) return;
        _settings.PlaybackSpeed = option.Value;
        SpeedComboBox.Tag = option.Value == 1.0 ? "1×" : $"{option.Value:0.##}×";
        if (_settings.RememberPlaybackSpeed) _saveSettings();
        ApplyPlaybackSpeed();
        ShowPlaybackChrome();
    }

    private void ApplyPlaybackSpeed()
    {
        if (SpeedComboBox.SelectedItem is SpeedOption option) _mediaPlayer.SetRate((float)option.Value);
    }

    private async void MediaVersionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMediaVersions || _switchingMediaSource || MediaVersionsComboBox.SelectedItem is not MediaSourceOption option) return;
        MediaVersionsComboBox.Tag = "⇄";
        if (MediaSourceMatches(option.Source, _plan.MediaSource)) return;
        await SwitchMediaSourceAsync(option.Source);
    }

    private async Task SwitchMediaSourceAsync(MediaSourceInfo requestedSource)
    {
        if (_closing || _switchingEpisode || _switchingMediaSource || MediaSourceMatches(requestedSource, _plan.MediaSource)) return;

        _switchingMediaSource = true;
        MediaVersionsComboBox.IsEnabled = false;
        _viewModel.BeginInteraction();
        var oldPlan = _plan;
        var oldMethod = _method;
        var position = GetCurrentPositionTicks();
        var wasPlaying = _mediaPlayer.IsPlaying;
        var oldAudio = FindSelectedStream(oldPlan.MediaSource, "Audio", _selectedAudioStreamIndex);
        var subtitlesWereOff = _selectedSubtitleStreamIndex is null;
        var oldSubtitle = FindSelectedStream(oldPlan.MediaSource, "Subtitle", _selectedSubtitleStreamIndex);

        try
        {
            ShowPlaybackChrome();
            ShowLoading("正在切换媒体版本…", new MediaSourceOption(requestedSource).Name);

            // Negotiate a fresh session at the current timestamp. This avoids reusing stale direct/
            // transcoding URLs and lets Emby return source-specific capabilities for the chosen file.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var playbackInfo = await _api.GetPlaybackInfoAsync(oldPlan.Item.Id, position, timeout.Token);
            if (_closing) return;

            var resolvedSource = FindEquivalentMediaSource(playbackInfo.MediaSources, requestedSource)
                                 ?? throw new EmbyApiException("服务器刷新 PlaybackInfo 后没有返回所选媒体版本。");
            if (string.IsNullOrWhiteSpace(resolvedSource.Id))
                throw new EmbyApiException("所选媒体版本缺少 MediaSourceId，无法安全切换。");
            var newPlan = _api.CreatePlaybackPlan(oldPlan.Item, playbackInfo, resolvedSource.Id);

            await ReportStoppedSafeAsync(oldPlan, position, oldMethod);
            if (oldMethod == PlaybackMethod.Transcode)
            {
                try { await _api.StopActiveEncodingsAsync(); } catch { }
            }

            _plan = newPlan;
            ResetPlanState(newPlan, position, preserveEpisodeContext: true);
            _selectedAudioStreamIndex = MatchStreamIndex(newPlan.MediaSource, "Audio", oldAudio, newPlan.MediaSource.DefaultAudioStreamIndex);
            _selectedSubtitleStreamIndex = subtitlesWereOff
                ? null
                : MatchStreamIndex(newPlan.MediaSource, "Subtitle", oldSubtitle,
                    newPlan.MediaSource.DefaultSubtitleStreamIndex is int subtitle && subtitle >= 0 ? subtitle : null);

            var total = GetTotalTicks();
            _viewModel.DurationSeconds = Math.Max(1, TimeSpan.FromTicks(total).TotalSeconds);
            _viewModel.PositionSeconds = TimeSpan.FromTicks(Math.Max(0, position)).TotalSeconds;
            ConfigureMediaVersionSelector();
            ApplyCapabilityVisibility();

            _pauseAfterMediaStart = !wasPlaying;
            StartCurrentPlan(position);
            LogService.Info($"Media source switched: item={newPlan.Item.Id}; source={newPlan.MediaSource.Id ?? newPlan.MediaSource.Name}; position={position}");
        }
        catch (Exception ex)
        {
            _pauseAfterMediaStart = false;
            HideLoading();
            ShowError("切换媒体版本失败：" + ex.Message);
            LogService.Warning("Unable to switch media source.", ex);

            // Restore selector to the actually playing source when negotiation failed.
            _plan = oldPlan;
            ConfigureMediaVersionSelector();
        }
        finally
        {
            _switchingMediaSource = false;
            MediaVersionsComboBox.IsEnabled = true;
            _viewModel.EndInteraction();
            ShowPlaybackChrome();
        }
    }

    private async void TranscodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseTranscoding())
        {
            ApplyCapabilityVisibility();
            ShowError("当前服务器或账号没有提供转码能力。播放器会继续使用原画直连。");
            return;
        }
        var position = GetCurrentPositionTicks();
        if (_method == PlaybackMethod.Transcode)
        {
            try { await _api.StopActiveEncodingsAsync(); } catch { }
            StartMedia(_plan.DirectUri, GetDirectMethod(), position);
            await ReportProgressSafeAsync("QualityChange");
        }
        else await SwitchToTranscodeAsync(position);
        ShowPlaybackChrome();
    }

    private async Task SwitchToTranscodeAsync(long startTicks)
    {
        if (!CanUseTranscoding()) { ShowError("当前媒体源不支持服务端转码。"); return; }
        try
        {
            TranscodeButton.IsEnabled = false;
            if (_method == PlaybackMethod.Transcode) { try { await _api.StopActiveEncodingsAsync(); } catch { } }
            StartMedia(_api.BuildTranscodeUri(_plan, Math.Max(0, startTicks)), PlaybackMethod.Transcode, startTicks);
            await ReportProgressSafeAsync("QualityChange");
        }
        catch (Exception ex) { ShowError("无法启动服务端转码：" + ex.Message); }
        finally { TranscodeButton.IsEnabled = true; }
    }

    private async Task LoadUserPlaybackPreferencesAsync()
    {
        try
        {
            var cached = _api.CurrentUser;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var user = cached ?? await _api.GetCurrentUserAsync(timeout.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                _autoPlayNextEpisode = user.Configuration?.EnableNextEpisodeAutoPlay ?? true;
                _allowTranscodingByPolicy = user.Policy?.EnableVideoPlaybackTranscoding != false;
                ApplyCapabilityVisibility();
            });
        }
        catch (Exception ex) { LogService.Warning("Unable to load playback preferences; using media capabilities only.", ex); }
    }

    private async Task LoadEpisodeContextAsync()
    {
        _episodeLoadCts?.Cancel();
        _episodeLoadCts?.Dispose();
        _episodeLoadCts = new CancellationTokenSource();
        var token = _episodeLoadCts.Token;
        var item = _plan.Item;

        // Some list/playback responses are intentionally lightweight and omit SeriesId/SeasonId.
        // Hydrate the item in the background only when it looks episodic; playback never waits for this.
        if (IsEpisode(item) && string.IsNullOrWhiteSpace(item.SeriesId))
        {
            try
            {
                using var hydrateTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                hydrateTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                var hydrated = await _api.GetItemAsync(item.Id, hydrateTimeout.Token).ConfigureAwait(false);
                if (IsEpisode(hydrated))
                {
                    item = hydrated;
                    _plan = _plan with { Item = hydrated };
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_closing || token.IsCancellationRequested) return;
                        _viewModel.Title = hydrated.Name;
                        _viewModel.BottomTitle = BuildBottomTitle(hydrated);
                        EpisodeButton.Visibility = Visibility.Visible;
                        EpisodePanelTitleText.Text = "正在加载剧集…";
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warning("Unable to hydrate episode metadata.", ex);
            }
        }

        if (!IsEpisode(item) || string.IsNullOrWhiteSpace(item.SeriesId))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                EpisodeButton.Visibility = IsEpisode(item) ? Visibility.Visible : Visibility.Collapsed;
                EpisodePanel.Visibility = Visibility.Collapsed;
                PreviousEpisodeButton.Visibility = Visibility.Collapsed;
                NextEpisodeButton.Visibility = Visibility.Collapsed;
                if (IsEpisode(item)) EpisodePanelTitleText.Text = "当前服务器没有返回剧集索引信息";
            });
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var episodes = await _api.GetEpisodesAsync(item.SeriesId!, null, timeout.Token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _closing) return;
            var ordered = episodes.OrderBy(ep => ep.ParentIndexNumber ?? int.MaxValue).ThenBy(ep => ep.IndexNumber ?? int.MaxValue).ToList();
            var currentIndex = ordered.FindIndex(ep => string.Equals(ep.Id, item.Id, StringComparison.Ordinal));
            var previous = currentIndex > 0 ? ordered[currentIndex - 1] : null;
            var next = currentIndex >= 0 && currentIndex + 1 < ordered.Count ? ordered[currentIndex + 1] : null;
            var options = ordered.Select(ep => new EpisodeOption(ep, string.Equals(ep.Id, item.Id, StringComparison.Ordinal))).ToList();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || token.IsCancellationRequested) return;
                _previousEpisode = previous;
                _nextEpisode = next;
                EpisodeItemsControl.ItemsSource = options;
                var seriesTitle = string.IsNullOrWhiteSpace(item.SeriesName) ? "当前剧集" : item.SeriesName;
                EpisodePanelTitleText.Text = $"{seriesTitle} · 全部剧集 · {ordered.Count} 集";
                ApplyCapabilityVisibility();
                if (EpisodePanel.Visibility == Visibility.Visible)
                {
                    _ = ScrollCurrentEpisodeIntoViewAsync();
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Warning("Unable to load episode context.", ex);
            await Dispatcher.InvokeAsync(() => { if (!_closing) EpisodePanelTitleText.Text = "剧集暂时无法加载，不影响当前播放"; });
        }
    }

    private void EpisodeButton_Click(object sender, RoutedEventArgs e)
    {
        SetEpisodePanelOpen(EpisodePanel.Visibility != Visibility.Visible);
        ShowPlaybackChrome();
    }

    private void CloseEpisodePanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetEpisodePanelOpen(false);
        ShowPlaybackChrome();
    }

    private void SetEpisodePanelOpen(bool open)
    {
        var wasOpen = EpisodePanel.Visibility == Visibility.Visible;
        if (open == wasOpen) return;
        if (open)
            CloseSelectorPopupsExcept(null);
        EpisodePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            _viewModel.BeginInteraction();
            _ = ScrollCurrentEpisodeIntoViewAsync();
        }
        else _viewModel.EndInteraction();
    }

    private async Task ScrollCurrentEpisodeIntoViewAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (_closing || EpisodePanel.Visibility != Visibility.Visible) return;
            EpisodeItemsControl.UpdateLayout();
            EpisodeScrollViewer.UpdateLayout();
            var current = EpisodeItemsControl.Items.Cast<object>()
                .OfType<EpisodeOption>()
                .FirstOrDefault(option => option.IsCurrent);
            if (current is null) return;

            var currentIndex = EpisodeItemsControl.Items.IndexOf(current);
            if (EpisodeItemsControl.ItemContainerGenerator.ContainerFromItem(current) is FrameworkElement container)
            {
                container.UpdateLayout();
                var point = container.TranslatePoint(new Point(0, 0), EpisodeScrollViewer);
                var targetOffset = EpisodeScrollViewer.VerticalOffset + point.Y
                    - Math.Max(0, (EpisodeScrollViewer.ViewportHeight - container.ActualHeight) / 2.0);
                if (!double.IsNaN(targetOffset) && !double.IsInfinity(targetOffset))
                {
                    EpisodeScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset));
                    return;
                }
            }

            // ItemsControl is non-virtualized, but the item container can still be unavailable
            // during the first layout pass. Episode rows have a fixed 68 DIP height plus 2 DIP
            // vertical margin, so use a deterministic index-based fallback instead of leaving the
            // panel at the top of the season list.
            if (currentIndex >= 0)
            {
                const double episodeRowPitch = 70.0;
                var centeredOffset = currentIndex * episodeRowPitch
                    - Math.Max(0, (EpisodeScrollViewer.ViewportHeight - episodeRowPitch) / 2.0);
                EpisodeScrollViewer.ScrollToVerticalOffset(Math.Max(0, centeredOffset));
            }
        }
        catch (Exception ex)
        {
            LogService.Warning("Unable to center the current episode in the episode panel.", ex);
        }
    }

    private async void EpisodeItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmbyItem item) await SwitchEpisodeAsync(item);
    }

    private async void PreviousEpisodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previousEpisode is not null) await SwitchEpisodeAsync(_previousEpisode);
    }

    private async void NextEpisodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nextEpisode is not null) await SwitchEpisodeAsync(_nextEpisode);
    }

    private async Task SwitchEpisodeAsync(EmbyItem item)
    {
        if (_closing || _switchingEpisode || string.Equals(item.Id, _plan.Item.Id, StringComparison.Ordinal)) return;
        _switchingEpisode = true;
        var oldPlan = _plan;
        var oldPosition = GetCurrentPositionTicks();
        var oldMethod = _method;
        try
        {
            SetEpisodePanelOpen(false);
            ShowPlaybackChrome();
            ShowLoading("正在打开下一集…", "正在请求播放信息");
            await ReportStoppedSafeAsync(oldPlan, oldPosition, oldMethod);
            if (oldMethod == PlaybackMethod.Transcode) { try { await _api.StopActiveEncodingsAsync(); } catch { } }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var newPlan = await _api.CreatePlaybackPlanAsync(item, cancellationToken: timeout.Token);
            if (_closing) return;
            _plan = newPlan;
            _playedItemIds.Add(newPlan.Item.Id);
            ResetPlanState(newPlan);
            ConfigurePlanUi();
            StartCurrentPlan(newPlan.ResumePositionTicks);
            _ = LoadEpisodeContextAsync();
        }
        catch (Exception ex)
        {
            HideLoading();
            ShowError("切换剧集失败：" + ex.Message);
            LogService.Warning("Unable to switch episode.", ex);
        }
        finally { _switchingEpisode = false; }
    }

    private Task ReportStartedSafeAsync() => SafeReportAsync(() => _api.ReportPlaybackStartedAsync(CreateReport()));
    private Task ReportProgressSafeAsync(string eventName) => _closing ? Task.CompletedTask : SafeReportAsync(() => _api.ReportPlaybackProgressAsync(CreateReport(eventName)));

    private async Task ReportStoppedSafeAsync(PlaybackPlan plan, long positionTicks, PlaybackMethod method)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _api.ReportPlaybackStoppedAsync(new PlaybackReport(plan.Item.Id, plan.MediaSource.Id ?? plan.Item.Id, plan.PlaySessionId,
                Math.Max(0, positionTicks), false, _mediaPlayer.Mute, _mediaPlayer.Volume, method, "Stop",
                _selectedAudioStreamIndex, _selectedSubtitleStreamIndex), timeout.Token);
        }
        catch { }
    }

    private static async Task SafeReportAsync(Func<Task> action)
    {
        try { await action(); } catch { }
    }

    private PlaybackReport CreateReport(string? eventName = null) => new(
        _plan.Item.Id,
        _plan.MediaSource.Id ?? _plan.Item.Id,
        _plan.PlaySessionId,
        GetCurrentPositionTicks(),
        !_mediaPlayer.IsPlaying,
        _mediaPlayer.Mute,
        _mediaPlayer.Volume,
        _method,
        eventName,
        _selectedAudioStreamIndex,
        _selectedSubtitleStreamIndex);

    private PlaybackMethod GetDirectMethod() => _plan.MediaSource.SupportsDirectPlay && string.IsNullOrWhiteSpace(_plan.MediaSource.DirectStreamUrl) ? PlaybackMethod.DirectPlay : PlaybackMethod.DirectStream;

    private long GetCurrentPositionTicks()
    {
        if (_closing) return Math.Max(0, _lastPositionTicks);
        var time = _mediaPlayer.Time;
        if (time < 0) return Math.Max(0, _lastPositionTicks);
        var ticks = time * TimeSpan.TicksPerMillisecond;
        _lastPositionTicks = Math.Max(0, _method == PlaybackMethod.Transcode ? _streamBaseTicks + ticks : ticks);
        return _lastPositionTicks;
    }

    private long GetTotalTicks()
    {
        var declared = Math.Max(0, _plan.Item.RunTimeTicks ?? _plan.MediaSource.RunTimeTicks ?? 0);
        return Math.Max(declared, _runtimeTotalTicks);
    }

    private static string FormatTime(long ticks)
    {
        var value = TimeSpan.FromTicks(Math.Max(0, ticks));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static string BuildBottomTitle(EmbyItem item)
    {
        if (IsEpisode(item))
        {
            var episode = item.IndexNumber is int episodeNumber ? $"E{episodeNumber:00}" : null;
            var season = item.ParentIndexNumber is int seasonNumber ? $"S{seasonNumber:00}" : null;
            var prefix = string.Concat(season, episode);
            var series = string.IsNullOrWhiteSpace(item.SeriesName) ? item.Name : item.SeriesName;
            return string.IsNullOrWhiteSpace(prefix) ? series ?? item.Name : $"{series}  {prefix}";
        }
        return item.Name;
    }

    private void ShowLoading(string title, string? detail = null, float? cachePercent = null)
    {
        if (LoadingOverlay is null) return;
        LoadingText.Text = title;
        LoadingDetailText.Text = detail ?? string.Empty;
        if (cachePercent is float percent)
        {
            var safe = Math.Clamp(percent, 0f, 100f);
            BufferCacheProgress.Value = safe;
            BufferCacheProgress.Visibility = Visibility.Visible;
            BufferCacheText.Text = $"缓冲 {safe:0}%";
            BufferCacheText.Visibility = Visibility.Visible;
        }
        else
        {
            BufferCacheProgress.Visibility = Visibility.Collapsed;
            BufferCacheText.Visibility = Visibility.Collapsed;
        }
        _loadingGeneration++;
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        LoadingOverlay.Opacity = 1;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoading()
    {
        if (LoadingOverlay is null || LoadingOverlay.Visibility != Visibility.Visible) return;
        var generation = ++_loadingGeneration;
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (generation == _loadingGeneration && _bufferingPercent >= 99.5f && LoadingOverlay is not null)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingOverlay.Opacity = 1;
            }
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, animation);
    }

    private void UpdateBufferVisualization()
    {
        if (BufferedProgressRail is null || TimelineRailHost is null || TimelineRailHost.ActualWidth <= 0) return;
        var total = GetTotalTicks();
        if (total <= 0)
        {
            BufferedProgressRail.Width = 0;
            return;
        }

        var current = Math.Clamp(_lastPositionTicks, 0, total);
        // LibVLC exposes the fill percentage of its network cache, not a fake "downloaded movie"
        // percentage. Reflect that honestly as a small buffered-ahead region on the timeline.
        var cacheRatio = Math.Clamp(_bufferingPercent / 100.0, 0.0, 1.0);
        var aheadTicks = (long)(_activeNetworkCachingMilliseconds * TimeSpan.TicksPerMillisecond * cacheRatio);
        var bufferedThrough = Math.Min(total, current + aheadTicks);
        BufferedProgressRail.Width = TimelineRailHost.ActualWidth * bufferedThrough / total;
    }

    private void ResetNetworkTelemetry()
    {
        _lastNetworkReadBytes = 0;
        _lastNetworkSampleUtc = DateTime.MinValue;
        _smoothedNetworkBytesPerSecond = 0;
        _networkSampleReady = false;
        _viewModel.NetworkRateText = "↓ 检测中";
        if (NetworkTelemetryBorder is not null) NetworkTelemetryBorder.Visibility = Visibility.Collapsed;
    }

    private void UpdateNetworkTelemetry()
    {
        if (_media is null || NetworkTelemetryBorder is null) return;

        try
        {
            var now = DateTime.UtcNow;
            var stats = _media.Statistics;
            var readBytes = stats.ReadBytes;

            if (!_networkSampleReady)
            {
                _lastNetworkReadBytes = readBytes;
                _lastNetworkSampleUtc = now;
                _networkSampleReady = true;
                NetworkTelemetryBorder.Visibility = Visibility.Visible;
                _viewModel.NetworkRateText = "↓ 检测中";
                return;
            }

            var elapsed = (now - _lastNetworkSampleUtc).TotalSeconds;
            if (elapsed < 0.8) return;

            var delta = readBytes - _lastNetworkReadBytes;
            _lastNetworkReadBytes = readBytes;
            _lastNetworkSampleUtc = now;

            if (delta < 0)
            {
                _smoothedNetworkBytesPerSecond = 0;
                _viewModel.NetworkRateText = "↓ 检测中";
                return;
            }

            var instant = delta / Math.Max(0.1, elapsed);
            if (_smoothedNetworkBytesPerSecond <= 0)
                _smoothedNetworkBytesPerSecond = instant;
            else
            {
                var alpha = instant > _smoothedNetworkBytesPerSecond ? 0.42 : 0.18;
                _smoothedNetworkBytesPerSecond += (instant - _smoothedNetworkBytesPerSecond) * alpha;
            }

            NetworkTelemetryBorder.Visibility = Visibility.Visible;
            _viewModel.NetworkRateText = "↓ " + FormatNetworkRate(_smoothedNetworkBytesPerSecond);

        }
        catch
        {
            // Telemetry is advisory. Playback must never fail because statistics are unavailable.
        }
    }

    private static string FormatNetworkRate(double bytesPerSecond)
    {
        bytesPerSecond = Math.Max(0, bytesPerSecond);
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):0.00} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:0} KB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void PlayerSurface_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        ShowPlaybackChrome();

    }

    private void CursorTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || !IsVisible || !GetCursorPos(out var point)) return;

        var leftDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
        var moved = !_hasCursorPosition || point.X != _lastCursorPosition.X || point.Y != _lastCursorPosition.Y;
        _lastCursorPosition = point;
        _hasCursorPosition = true;

        System.Windows.Point local;
        try { local = PointFromScreen(new System.Windows.Point(point.X, point.Y)); }
        catch { _lastLeftButtonDown = leftDown; return; }

        var inside = local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight;
        if (inside && moved) ShowPlaybackChrome();

        // The OS cursor poll is only a preview/visibility fallback. It never writes Slider.Value;
        // WPF Track/Thumb remains the single owner of seek geometry so the thumb cannot "fly".
        var overSeek = TryGetSeekPointer(point, out var seekPoint);
        if (_isSeeking && ControlsPanel.Opacity > 0.05)
        {
            // During an active drag the timestamp follows the OS cursor horizontally even when the
            // pointer slips a few pixels above/below the thin rail. Slider.Value still comes only
            // from WPF Track/Thumb, so preview tracking cannot move the thumb.
            try
            {
                // GetCursorPos is in physical pixels. PointFromScreen converts it to WPF DIPs
                // for the monitor's current DPI before it is mixed with ActualWidth/Track geometry.
                var localSeek = PositionSlider.PointFromScreen(new System.Windows.Point(point.X, point.Y));
                seekPoint = new System.Windows.Point(
                    Math.Clamp(localSeek.X, 0, PositionSlider.ActualWidth),
                    PositionSlider.ActualHeight / 2.0);
                UpdateSeekPreview(seekPoint, PositionSlider.Value);
            }
            catch { }
        }
        else if (!overSeek)
        {
            // Normal hover preview is owned by WPF PositionSlider_PreviewMouseMove. Keeping the
            // 60 Hz Win32 cursor poll out of that path prevents two coordinate sources from
            // alternately overwriting the preview bubble and causing visible jitter.
            SeekPreviewBubble.Visibility = Visibility.Collapsed;
            SeekPreviewPointer.Visibility = Visibility.Collapsed;
        }

        // Toggle playback anywhere inside the visible VideoView picture surface, including areas
        // where WPF's transparent overlay is the OS hit window. Interactive chrome is excluded
        // explicitly. This is more reliable than requiring WindowFromPoint() to hit LibVLC's child
        // HWND, which caused valid picture clicks to be ignored in some regions.
        var interactive = inside && IsScreenPointOverInteractiveChrome(point);
        var overVideoHandle = inside && IsScreenPointOverVideoSurface(point);
        if (leftDown && !_lastLeftButtonDown)
        {
            _videoClickCandidate = overVideoHandle && !interactive && !overSeek && !_isSeeking && _playingObserved;
            if (_videoClickCandidate)
            {
                _videoClickDownPoint = point;
                _videoClickDownUtc = DateTime.UtcNow;
            }
        }
        else if (!leftDown && _lastLeftButtonDown && _videoClickCandidate)
        {
            var dx = point.X - _videoClickDownPoint.X;
            var dy = point.Y - _videoClickDownPoint.Y;
            var distanceSquared = dx * dx + dy * dy;
            var elapsed = DateTime.UtcNow - _videoClickDownUtc;
            if (overVideoHandle && !interactive && !overSeek && distanceSquared <= 100 && elapsed <= TimeSpan.FromMilliseconds(650))
            {
                TogglePlayback(showGesture: true);
                ShowPlaybackChrome();
            }
            _videoClickCandidate = false;
        }
        else if (!overVideoHandle || interactive || overSeek)
        {
            _videoClickCandidate = false;
        }

        _lastLeftButtonDown = leftDown;
    }

    private bool TryGetSeekPointer(NativePoint screenPoint, out System.Windows.Point localPoint)
    {
        localPoint = default;
        if (PositionSlider is null || PositionSlider.ActualWidth <= 0 || PositionSlider.ActualHeight <= 0) return false;
        try
        {
            // screenPoint is a physical-pixel Win32 coordinate. Always convert through WPF so
            // 125%/150%/200% DPI scaling cannot skew the seek ratio.
            var local = PositionSlider.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            if (local.X < 0 || local.X > PositionSlider.ActualWidth
                || local.Y < -5 || local.Y > PositionSlider.ActualHeight + 5) return false;

            localPoint = new System.Windows.Point(
                Math.Clamp(local.X, 0, PositionSlider.ActualWidth),
                Math.Clamp(local.Y, 0, PositionSlider.ActualHeight));
            return true;
        }
        catch { return false; }
    }

    private bool IsScreenPointOverVideoSurface(NativePoint screenPoint)
    {
        if (!IsVisible || VideoView is null || VideoView.ActualWidth <= 0 || VideoView.ActualHeight <= 0) return false;

        var playerHwnd = new WindowInteropHelper(this).Handle;
        if (playerHwnd == IntPtr.Zero || GetForegroundWindow() != playerHwnd) return false;

        // VideoView defines the legal gesture rectangle. The global mouse poll can never control
        // playback from another app because the player must be foreground and the point must be
        // inside this exact screen-space rectangle.
        return IsScreenPointOverElement(VideoView, screenPoint);
    }

    private bool IsScreenPointOverInteractiveChrome(NativePoint screenPoint)
    {
        if (EpisodePanel.Visibility == Visibility.Visible && IsScreenPointOverElement(EpisodePanel, screenPoint)) return true;
        if (ControlsPanel.Opacity > 0.08 && IsScreenPointOverElement(ControlsPanel, screenPoint, 6)) return true;
        if (TitleBarPanel.Opacity > 0.08 && IsScreenPointOverElement(TitleBarPanel, screenPoint, 4)) return true;
        return AudioTracksComboBox.IsDropDownOpen || SubtitleTracksComboBox.IsDropDownOpen || SpeedComboBox.IsDropDownOpen || MediaVersionsComboBox.IsDropDownOpen;
    }

    private static bool IsScreenPointOverElement(FrameworkElement element, NativePoint point, double padding = 0)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        try
        {
            // GetCursorPos is physical pixels while WPF element sizes are DIPs. Convert the point
            // through the element before comparing bounds; otherwise DPI scaling truncates the
            // right/bottom portion of VideoView and makes picture clicks appear randomly dead.
            var local = element.PointFromScreen(new System.Windows.Point(point.X, point.Y));
            return local.X >= -padding && local.X <= element.ActualWidth + padding
                   && local.Y >= -padding && local.Y <= element.ActualHeight + padding;
        }
        catch { return false; }
    }

    private void ShowPlaybackChrome() => _viewModel.NotifyPointerActivity();

    private void SelectorComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        ShowPlaybackChrome();

        // Only one transient layer may exist at a time. Opening any selector must collapse the
        // episode panel first, then close sibling selectors before opening the requested one.
        if (EpisodePanel.Visibility == Visibility.Visible)
            SetEpisodePanelOpen(false);

        CloseSelectorPopupsExcept(combo);
        if (!combo.IsDropDownOpen)
        {
            combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsAnySelectorPopupOpen())
        {
            HandleClickWithSelectorOpen(e);
            return;
        }

        // A closed selector ComboBox can keep the mouse capture after its popup was dismissed
        // programmatically (e.g. switch or dismissal clicks). While captured, every physical
        // click routes into that dead ComboBox — fullscreen/selector buttons appear unresponsive
        // "for a while". Detect, release, and re-dispatch this click to the real target so the
        // user never needs a spare click to revive the player.
        if (TryReleaseStuckMouseCapture() && GetCursorPos(out var revive))
        {
            e.Handled = true;
            var selector = GetSelectorAtScreenPoint(revive);
            if (selector is not null)
            {
                if (EpisodePanel.Visibility == Visibility.Visible) SetEpisodePanelOpen(false);
                selector.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
                ShowPlaybackChrome();
                return;
            }

            var button = GetPlayerButtonAtScreenPoint(revive);
            if (button is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RaisePlayerButtonClick(button)));
                return;
            }
        }
    }

    private void HandleClickWithSelectorOpen(MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;

        // A native WPF ComboBox captures the mouse while its popup is open. Because of that,
        // e.OriginalSource can still point at the old ComboBox even when the physical cursor is
        // over another player control. Hit-test the real screen position instead of trusting the
        // routed source. This preserves native ComboBox capture (menus stay open) while still
        // allowing one physical click to close the old menu and activate the new target.
        var targetSelector = GetSelectorAtScreenPoint(cursor);
        if (targetSelector is not null)
        {
            var shouldOpen = !targetSelector.IsDropDownOpen;
            if (EpisodePanel.Visibility == Visibility.Visible)
                SetEpisodePanelOpen(false);
            CloseSelectorPopupsExcept(null);
            if (shouldOpen)
                targetSelector.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
            e.Handled = true;
            ShowPlaybackChrome();
            return;
        }

        var targetButton = GetPlayerButtonAtScreenPoint(cursor);
        if (targetButton is not null)
        {
            CloseSelectorPopupsExcept(null);
            e.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RaisePlayerButtonClick(targetButton)));
            return;
        }

        // Clicking elsewhere only dismisses the current selector. Because the old ComboBox still
        // owns native mouse capture during this preview event, letting the routed event continue
        // would reach that same ComboBox and reopen it. Consume this dismissal click explicitly.
        CloseSelectorPopupsExcept(null);
        e.Handled = true;
    }

    private void RaisePlayerButtonClick(Button button)
    {
        if (_closing || !button.IsEnabled || button.Visibility != Visibility.Visible) return;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        var command = button.Command;
        var parameter = button.CommandParameter;
        if (command is not null && command.CanExecute(parameter)) command.Execute(parameter);
    }

    /// <summary>
    /// Releases mouse captures that would otherwise eat clicks: a player selector ComboBox
    /// holding capture while its popup is already closed, or any capture whose owner is far
    /// from the physical cursor (e.g. a seek thumb whose mouse-up was swallowed by the
    /// LibVLC child HWND). Returns true when a stuck capture was released.
    /// </summary>
    private bool TryReleaseStuckMouseCapture()
    {
        if (Mouse.Captured is ComboBox { IsDropDownOpen: false } combo && IsPlayerSelector(combo))
        {
            Mouse.Capture(null);
            return true;
        }

        if (Mouse.Captured is not FrameworkElement captured) return false;
        if (!GetCursorPos(out var cursor)) return false;

        try
        {
            var local = captured.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
            var bounds = new Rect(-80, -80, captured.ActualWidth + 160, captured.ActualHeight + 160);
            if (bounds.Contains(local)) return false;
        }
        catch { }

        Mouse.Capture(null);
        return true;
    }

    private ComboBox? GetSelectorAtScreenPoint(NativePoint point)
    {
        foreach (var combo in new[] { AudioTracksComboBox, SubtitleTracksComboBox, SpeedComboBox, MediaVersionsComboBox })
        {
            if (combo.Visibility == Visibility.Visible && combo.IsEnabled && IsScreenPointOverElement(combo, point, 2))
                return combo;
        }
        return null;
    }

    private Button? GetPlayerButtonAtScreenPoint(NativePoint point)
    {
        foreach (var button in new Button[]
                 {
                     PlayPauseButton, SeekBackwardButton, SeekForwardButton, MuteButton,
                     EpisodeButton, TranscodeButton, FullscreenButton
                 })
        {
            if (button.Visibility == Visibility.Visible && button.IsEnabled && IsScreenPointOverElement(button, point, 2))
                return button;
        }
        return null;
    }

    private bool IsPlayerSelector(ComboBox? combo) =>
        ReferenceEquals(combo, AudioTracksComboBox)
        || ReferenceEquals(combo, SubtitleTracksComboBox)
        || ReferenceEquals(combo, SpeedComboBox)
        || ReferenceEquals(combo, MediaVersionsComboBox);

    private void CloseSelectorPopupsExcept(ComboBox? keepOpen)
    {
        foreach (var combo in new[] { AudioTracksComboBox, SubtitleTracksComboBox, SpeedComboBox, MediaVersionsComboBox })
        {
            if (!ReferenceEquals(combo, keepOpen) && combo.IsDropDownOpen)
                combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        }
    }

    private void RightControlsGroup_MouseEnter(object sender, MouseEventArgs e)
    {
        // Do not let the 3-second auto-hide timer remove a control while the user is aiming at it.
        _viewModel.KeepControlsVisible();
    }

    private void RightControlsGroup_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!IsAnySelectorPopupOpen()) _viewModel.NotifyPointerActivity();
    }

    private void InteractivePopup_Opened(object? sender, EventArgs e)
    {
        _viewModel.BeginInteraction();
        if (sender is ComboBox combo)
        {
            // Keep WPF's native mouse capture intact. Releasing it here causes ComboBox to treat
            // the popup as dismissed immediately on some Windows/WPF versions.
            AnimateSelectorPopup(combo);
        }
    }

    private static void AnimateSelectorPopup(ComboBox combo)
    {
        try
        {
            combo.ApplyTemplate();
            if (combo.Template.FindName("PART_Popup", combo) is not Popup popup || popup.Child is not FrameworkElement surface)
                return;

            surface.BeginAnimation(UIElement.OpacityProperty, null);
            surface.Opacity = 1;

            if (surface.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                surface.RenderTransform = translate;
            }

            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slide = new DoubleAnimation
            {
                From = 7,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(115),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            var fade = new DoubleAnimation
            {
                From = 0.88,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(90),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };

            translate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
            surface.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
        catch
        {
            // The selector remains fully usable even if the short composition animation is unavailable.
        }
    }

    private void InteractivePopup_Closed(object? sender, EventArgs e)
    {
        _viewModel.EndInteraction();
        // A ComboBox keeps keyboard focus after mouse selection. Clear it so the next arrow/space
        // keystroke returns to player shortcuts instead of silently changing a selector value.
        Keyboard.ClearFocus();
        _viewModel.NotifyPointerActivity();
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen) EnterTrueFullscreen();
        else ExitTrueFullscreen();
        _viewModel.NotifyPointerActivity();
    }

    private void EnterTrueFullscreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        _previousWindowStyle = WindowStyle;
        _previousWindowState = WindowState;
        _previousResizeMode = ResizeMode;
        _previousTopmost = Topmost;
        _previousWindowChrome = WindowChrome.GetWindowChrome(this);
        _hasPreviousNativeWindowRect = GetWindowRect(handle, out _previousNativeWindowRect);

        // The player already uses WindowStyle=None. Avoid WPF Width/Height/WindowState animation
        // here; move the HWND directly in physical pixels so DPI scaling cannot leave a taskbar strip.
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;

        WindowChrome.SetWindowChrome(this, FullscreenChrome);
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        _isFullscreen = true;
        _viewModel.IsFullscreen = true;

        ApplyMonitorBounds(handle, info.rcMonitor, topmost: true);
        Activate();
        SetForegroundWindow(handle);
        // WindowChrome updates can post a non-client recalculation. Re-assert the exact monitor
        // bounds on the next render pass without changing WPF layout state again.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !_isFullscreen) return;
            // Only touch the HWND again when WindowChrome/DPI really changed its physical bounds.
            // Avoiding a redundant second resize keeps fullscreen entry to one compositor frame.
            if (!GetWindowRect(handle, out var actual) || !NativeRectEquals(actual, info.rcMonitor))
                ApplyMonitorBounds(handle, info.rcMonitor, topmost: true);
        }), DispatcherPriority.Render);
    }

    private void ExitTrueFullscreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        _isFullscreen = false;
        _viewModel.IsFullscreen = false;
        WindowChrome.SetWindowChrome(this, _previousWindowChrome);
        ResizeMode = _previousResizeMode;
        Topmost = _previousTopmost;

        if (_hasPreviousNativeWindowRect)
        {
            var rect = _previousNativeWindowRect;
            SetWindowPos(handle, _previousTopmost ? HwndTopmost : HwndNotTopmost,
                rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top),
                SwpFrameChanged | SwpShowWindow | SwpNoOwnerZOrder);
        }

        if (_previousWindowState == WindowState.Maximized)
            WindowState = WindowState.Maximized;

    }

    private static void ApplyMonitorBounds(IntPtr handle, NativeRect rect, bool topmost)
    {
        SetWindowPos(handle, topmost ? HwndTopmost : HwndNotTopmost,
            rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top),
            SwpFrameChanged | SwpShowWindow | SwpNoOwnerZOrder);
    }

    private static bool NativeRectEquals(NativeRect a, NativeRect b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static readonly WindowChrome FullscreenChrome = new()
    {
        CaptionHeight = 0,
        ResizeBorderThickness = new Thickness(0),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false
    };

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Keep selector navigation predictable: when a popup is open, its arrows/Enter belong to
        // the menu. Escape closes the menu before it is allowed to affect fullscreen/panels.
        if (IsAnySelectorPopupOpen())
        {
            if (e.Key == Key.Escape)
            {
                CloseSelectorPopups();
                e.Handled = true;
                ShowPlaybackChrome();
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (EpisodePanel.Visibility == Visibility.Visible)
            {
                SetEpisodePanelOpen(false);
                e.Handled = true;
            }
            else if (_isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            if (e.Handled) ShowPlaybackChrome();
            return;
        }

        if (e.Key == Key.F12)
        {
            DebugConsoleWindow.ShowFor(this);
            e.Handled = true;
            ShowPlaybackChrome();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;

        // YouTube-style speed stepping uses Shift+, / Shift+. (< / >).
        if (shift && e.Key == Key.OemComma)
        {
            StepPlaybackSpeed(-1);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.OemPeriod)
        {
            StepPlaybackSpeed(1);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.N && _nextEpisode is not null)
        {
            _ = SwitchEpisodeAsync(_nextEpisode);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.P && _previousEpisode is not null)
        {
            _ = SwitchEpisodeAsync(_previousEpisode);
            e.Handled = true;
        }
        else if (!shift)
        {
            switch (e.Key)
            {
                // Netflix: Space/Enter. YouTube additionally standardizes K.
                case Key.Space:
                case Key.Return:
                case Key.K:
                    _viewModel.TogglePlaybackCommand.Execute(null);
                    e.Handled = true;
                    break;

                // Netflix uses arrows for ±10s; YouTube uses J/L for the same 10-second jump.
                case Key.Left:
                case Key.J:
                    SeekRelative(-10);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.L:
                    SeekRelative(10);
                    e.Handled = true;
                    break;

                case Key.Up:
                    _viewModel.Volume = Math.Min(100, _viewModel.Volume + 5);
                    e.Handled = true;
                    break;
                case Key.Down:
                    _viewModel.Volume = Math.Max(0, _viewModel.Volume - 5);
                    e.Handled = true;
                    break;
                case Key.M:
                    _viewModel.ToggleMuteCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.F:
                case Key.F11:
                    ToggleFullscreen();
                    e.Handled = true;
                    break;

                case Key.C:
                    ToggleSubtitlesFromKeyboard();
                    e.Handled = true;
                    break;

                case Key.Home:
                    SeekAbsoluteTicks(0);
                    e.Handled = true;
                    break;
                case Key.End:
                {
                    var total = GetTotalTicks();
                    if (total > 0) SeekAbsoluteTicks(Math.Max(0, total - TimeSpan.FromSeconds(1).Ticks));
                    e.Handled = total > 0;
                    break;
                }
                default:
                {
                    var digit = GetSeekDigit(e.Key);
                    if (digit >= 0)
                    {
                        SeekToPercentage(digit);
                        e.Handled = true;
                    }
                    break;
                }
            }
        }

        if (e.Handled) ShowPlaybackChrome();
    }

    private static int GetSeekDigit(Key key) => key switch
    {
        Key.D0 or Key.NumPad0 => 0,
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        Key.D9 or Key.NumPad9 => 9,
        _ => -1
    };

    private void SeekToPercentage(int digit)
    {
        var total = GetTotalTicks();
        if (total <= 0) return;
        var ratio = Math.Clamp(digit / 10d, 0d, 0.9d);
        SeekAbsoluteTicks((long)(total * ratio));
    }

    private void SeekAbsoluteTicks(long targetTicks)
    {
        _ = SeekToAsync(Math.Max(0, targetTicks));
    }

    private void StepPlaybackSpeed(int direction)
    {
        var options = SpeedComboBox.Items.Cast<SpeedOption>().OrderBy(option => option.Value).ToList();
        if (options.Count == 0) return;
        var current = SpeedComboBox.SelectedItem as SpeedOption;
        var index = current is null ? options.FindIndex(option => option.Value >= 1.0) : options.IndexOf(current);
        if (index < 0) index = 0;
        var targetIndex = Math.Clamp(index + Math.Sign(direction), 0, options.Count - 1);
        if (targetIndex == index) return;
        SpeedComboBox.SelectedItem = options[targetIndex];
    }

    private void ToggleSubtitlesFromKeyboard()
    {
        if (SubtitleTracksComboBox.Visibility != Visibility.Visible) return;
        var options = SubtitleTracksComboBox.Items.Cast<TrackOption>().ToList();
        if (options.Count == 0) return;
        var current = SubtitleTracksComboBox.SelectedItem as TrackOption;
        TrackOption? target;
        if (current is null || current.Id == -1)
            target = options.FirstOrDefault(option => option.Id != -1);
        else
            target = options.FirstOrDefault(option => option.Id == -1);
        if (target is not null) SubtitleTracksComboBox.SelectedItem = target;
    }

    private bool IsAnySelectorPopupOpen() =>
        AudioTracksComboBox.IsDropDownOpen || SubtitleTracksComboBox.IsDropDownOpen || SpeedComboBox.IsDropDownOpen || MediaVersionsComboBox.IsDropDownOpen;

    private void CloseSelectorPopups()
    {
        AudioTracksComboBox.IsDropDownOpen = false;
        SubtitleTracksComboBox.IsDropDownOpen = false;
        SpeedComboBox.IsDropDownOpen = false;
        MediaVersionsComboBox.IsDropDownOpen = false;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        _lastPositionTicks = GetCurrentPositionTicks();
        _closing = true;
        _episodeLoadCts?.Cancel();
        _startupTimer.Stop();
        _uiTimer.Stop();
        _reportTimer.Stop();
        _cursorTimer.Stop();
        _viewModel.Dispose();
        _settings.PreferredVolume = _mediaPlayer.Volume;
        _saveSettings();
        _mediaPlayer.Stop();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _api.ReportPlaybackStoppedAsync(new PlaybackReport(_plan.Item.Id, _plan.MediaSource.Id ?? _plan.Item.Id, _plan.PlaySessionId,
                _lastPositionTicks, false, _mediaPlayer.Mute, _mediaPlayer.Volume, _method), timeout.Token);
            if (_method == PlaybackMethod.Transcode) await _api.StopActiveEncodingsAsync(timeout.Token);
        }
        catch { }
        finally
        {
            _episodeLoadCts?.Dispose();
            VideoView.MediaPlayer = null;
            _media?.Dispose();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
        }
    }

    private const int VkLeftButton = 0x01;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    private sealed record SpeedOption(double Value)
    {
        public override string ToString() => Value == 1.0 ? "正常" : $"{Value:0.##}×";
    }

    private sealed class EpisodeOption
    {
        public EpisodeOption(EmbyItem item, bool current)
        {
            Item = item;
            Number = item.ParentIndexNumber is int season && item.IndexNumber is int episode
                ? $"S{season:00}E{episode:00}"
                : item.IndexNumber?.ToString("00") ?? "–";
            Title = string.IsNullOrWhiteSpace(item.Name) ? $"第 {item.IndexNumber ?? 0} 集" : item.Name;
            var runtime = item.RunTimeTicks is > 0 ? FormatTime(item.RunTimeTicks.Value) : string.Empty;
            Meta = string.Join(" · ", new[] { item.SeasonName, runtime, item.UserData?.Played == true ? "已看完" : null }.Where(v => !string.IsNullOrWhiteSpace(v))!);
            Progress = Math.Clamp(item.UserData?.PlayedPercentage ?? 0, 0, 100);
            IsCurrent = current;
            CurrentVisibility = current ? Visibility.Visible : Visibility.Collapsed;
        }
        public EmbyItem Item { get; }
        public string Number { get; }
        public string Title { get; }
        public string Meta { get; }
        public double Progress { get; }
        public bool IsCurrent { get; }
        public Visibility CurrentVisibility { get; }
    }
}
