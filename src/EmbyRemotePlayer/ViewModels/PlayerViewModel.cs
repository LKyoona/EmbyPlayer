namespace EmbyRemotePlayer.ViewModels;

/// <summary>
/// Presentation state for PlayerWindow. The LibVLC/Emby transport remains in the view's
/// controller layer, while all user-facing state and button commands are exposed here.
/// </summary>
public sealed class PlayerViewModel : NotifyObject, IDisposable
{
    private readonly DispatcherTimer _autoHideTimer;
    private readonly Action<double>? _volumeChanged;
    private int _interactionDepth;
    private bool _disposed;

    private string _title = string.Empty;
    private string _bottomTitle = string.Empty;
    private string _networkRateText = "↓ 检测中";
    private double _positionSeconds;
    private double _durationSeconds = 1;
    private double _volume = 80;
    private bool _isPlaying;
    private bool _isMuted;
    private bool _isFullscreen;
    private bool _isControlsVisible = true;

    public PlayerViewModel(
        Action togglePlayback,
        Action seekBackward,
        Action seekForward,
        Action toggleMute,
        Action toggleFullscreen,
        Action close,
        Action minimize,
        Action<double>? volumeChanged = null)
    {
        _volumeChanged = volumeChanged;

        TogglePlaybackCommand = new RelayCommand(togglePlayback);
        SeekBackwardCommand = new RelayCommand(seekBackward);
        SeekForwardCommand = new RelayCommand(seekForward);
        ToggleMuteCommand = new RelayCommand(toggleMute);
        ToggleFullscreenCommand = new RelayCommand(toggleFullscreen);
        CloseCommand = new RelayCommand(close);
        MinimizeCommand = new RelayCommand(minimize);

        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _autoHideTimer.Tick += AutoHideTimer_Tick;
        RestartAutoHide();
    }

    public ICommand TogglePlaybackCommand { get; }
    public ICommand SeekBackwardCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand MinimizeCommand { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string BottomTitle
    {
        get => _bottomTitle;
        set => SetProperty(ref _bottomTitle, value);
    }

    public string NetworkRateText
    {
        get => _networkRateText;
        set => SetProperty(ref _networkRateText, value);
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(1, DurationSeconds));
            if (!SetProperty(ref _positionSeconds, clamped)) return;
            RaisePropertyChanged(nameof(CurrentTimeText));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            var normalized = Math.Max(1, value);
            if (!SetProperty(ref _durationSeconds, normalized)) return;
            if (_positionSeconds > normalized)
            {
                _positionSeconds = normalized;
                RaisePropertyChanged(nameof(PositionSeconds));
                RaisePropertyChanged(nameof(CurrentTimeText));
            }
            RaisePropertyChanged(nameof(TotalTimeText));
        }
    }

    public string CurrentTimeText => FormatTime(PositionSeconds);
    public string TotalTimeText => DurationSeconds <= 1.001 ? "--:--" : FormatTime(DurationSeconds);

    public double Volume
    {
        get => _volume;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, normalized)) return;
            _volumeChanged?.Invoke(normalized);
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set => SetProperty(ref _isFullscreen, value);
    }

    public bool IsControlsVisible
    {
        get => _isControlsVisible;
        private set => SetProperty(ref _isControlsVisible, value);
    }

    public void NotifyPointerActivity()
    {
        if (_disposed) return;
        IsControlsVisible = true;
        RestartAutoHide();
    }

    public void BeginInteraction()
    {
        if (_disposed) return;
        _interactionDepth++;
        IsControlsVisible = true;
        _autoHideTimer.Stop();
    }

    public void EndInteraction()
    {
        if (_disposed) return;
        if (_interactionDepth > 0) _interactionDepth--;
        RestartAutoHide();
    }

    public void KeepControlsVisible()
    {
        if (_disposed) return;
        IsControlsVisible = true;
        _autoHideTimer.Stop();
    }

    private void RestartAutoHide()
    {
        _autoHideTimer.Stop();
        if (_interactionDepth == 0)
            _autoHideTimer.Start();
    }

    /// <summary>
    /// Optional UI-supplied check: return true when the pointer is currently resting on
    /// interactive chrome. The auto-hide timer consults it so a stationary cursor parked on
    /// a control never has the bar fade out from underneath (which turned the next click
    /// into a fall-through video toggle).
    /// </summary>
    public Func<bool>? KeepVisibleCheck { get; set; }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (_interactionDepth > 0) return;
        if (KeepVisibleCheck?.Invoke() == true)
        {
            IsControlsVisible = true;
            _autoHideTimer.Start();
            return;
        }
        IsControlsVisible = false;
    }

    private static string FormatTime(double totalSeconds)
    {
        if (!double.IsFinite(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoHideTimer.Stop();
        _autoHideTimer.Tick -= AutoHideTimer_Tick;
    }
}
