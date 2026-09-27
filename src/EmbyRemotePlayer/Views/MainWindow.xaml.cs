using EmbyRemotePlayer.Models;
using EmbyRemotePlayer.Services;
using EmbyRemotePlayer.ViewModels;

namespace EmbyRemotePlayer.Views;

public partial class MainWindow : Window
{
    private readonly ProfileStore _profileStore = new();
    private readonly AppSettings _settings;
    private readonly Stack<BrowseSnapshot> _navigation = new();
    private CancellationTokenSource? _operationCts;
    private EmbyApiClient? _api;
    private ImageCacheService? _images;
    private ServerProfile? _activeProfile;
    private ServerProfile? _editingProfile;
    private EmbyItem? _detailItem;
    private NavigationState? _currentNavigation;
    private PlayerWindow? _playerWindow;
    private bool _loaded;
    private readonly Dictionary<string, EmbyItem> _parentDetailCache = new(StringComparer.Ordinal);
    private List<MediaCardViewModel> _detailEpisodeCards = [];
    private List<MediaCardViewModel> _detailSimilarCards = [];
    private List<DetailPersonViewModel> _detailPeople = [];

    // Metadata/navigation prefetch state. These are intentionally independent from image
    // prefetching: card metadata can be ready before the user clicks without downloading
    // heavyweight playback streams for the whole page.
    private readonly HashSet<string> _prefetchedCardIds = new(StringComparer.Ordinal);
    private Task<PlaybackInfoResponse>? _detailPlaybackInfoTask;
    private PlaybackInfoResponse? _detailPlaybackInfo;
    private string? _detailPlaybackInfoItemId;
    private DateTimeOffset _detailPlaybackInfoAt;
    private CancellationTokenSource? _busyDelayCts;

    // Viewport-driven image state. We never enqueue an entire 100+ item page anymore.
    private CancellationTokenSource? _viewportImageCts;
    private readonly ObservableCollection<HomeRowViewModel> _homeRows = [];
    private readonly Dictionary<string, HomeRowViewModel> _homeRowsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<ScrollViewer, HomeRowViewModel> _homeRowViewports = new();
    private readonly List<EmbyItem> _heroCandidates = [];
    private int _heroIndex;
    private DispatcherTimer? _heroTimer;
    private CancellationTokenSource? _heroCts;
    private bool _heroUsingFrontImage;
    private EmbyItem? _currentHeroItem;
    private List<MediaCardViewModel> _browseCards = [];
    private readonly ObservableCollection<MediaCardViewModel> _browseVisibleCards = [];
    private List<SearchCardSection> _searchSections = [];
    private bool _browseFlatLandscape;

    private const int BrowseInitialRenderCount = 36;
    private const int BrowseRenderBatchSize = 24;
    private const double PosterCellWidth = 198;
    private const double PosterCellHeight = 285;
    private const double LandscapeCellWidth = 354;
    private const double LandscapeCellHeight = 205;

    private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrollStates = new();
    private readonly DispatcherTimer _smoothScrollTimer;

    public MainWindow()
    {
        InitializeComponent();
        var appVer = typeof(MainWindow).Assembly.GetName().Version;
        AppVersionText.Text = appVer is null ? "v0.0.0" : $"v{appVer.ToString(appVer.Revision > 0 ? 4 : 3)}";
        App.ConfigureAdaptiveWindow(
            this,
            widthRatio: 0.90,
            heightRatio: 0.88,
            minimumWidth: 900,
            minimumHeight: 580,
            maximumDesignWidth: 1720,
            maximumDesignHeight: 1040);
        _settings = _profileStore.Load();
        _smoothScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, SmoothScrollTimer_Tick, Dispatcher);
        HomeRowsItemsControl.ItemsSource = _homeRows;
        Loaded += MainWindow_Loaded;
        RefreshProfiles();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        var profile = _settings.Profiles.FirstOrDefault(p => p.ProfileId == _settings.LastProfileId && p.AutoConnect)
                      ?? _settings.Profiles.FirstOrDefault(p => p.AutoConnect);
        if (profile is not null)
            await ConnectSavedProfileAsync(profile, true);
    }

    private void RefreshProfiles()
    {
        ProfilesItemsControl.ItemsSource = _settings.Profiles.OrderByDescending(p => p.LastConnectedAt).ToList();
        EmptyProfilesCard.Visibility = _settings.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        _editingProfile = null;
        DisplayNameTextBox.Text = string.Empty;
        ServerUrlTextBox.Text = string.Empty;
        UsernameTextBox.Text = string.Empty;
        PasswordInput.Password = string.Empty;
        AutoConnectCheckBox.IsChecked = _settings.Profiles.Count == 0;
        ShowLoginPanel();
    }

    private async void ConnectProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ServerProfile profile)
            await ConnectSavedProfileAsync(profile, false);
    }

    private void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ServerProfile profile)
            PrepareProfileLogin(profile, null);
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServerProfile profile) return;
        if (MessageBox.Show($"删除“{profile.FriendlyName}”及其本地登录凭据？", "删除服务器", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profileStore.DeleteProfile(_settings, profile);
        RefreshProfiles();
    }

    private async Task ConnectSavedProfileAsync(ServerProfile profile, bool silent)
    {
        var operationToken = BeginOperation();
        var token = _profileStore.LoadToken(profile);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(profile.UserId))
        {
            PrepareProfileLogin(profile, silent ? null : "登录信息已失效，请重新输入密码。");
            return;
        }

        ShowBusy("正在连接服务器…");
        try
        {
            ReplaceApi(new EmbyApiClient(profile.ServerUrl, profile.DeviceId, _settings.MaxStreamingBitrate));
            _api!.SetSession(token, profile.UserId, profile.ServerId);

            EmbyUser? user = null;
            try
            {
                using var validationTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                validationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                user = await _api.ValidateSessionAsync(validationTimeout.Token);
            }
            catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
            {
                // Some Emby deployments make /Users/{id} unexpectedly slow even while media APIs are healthy.
                // Do not block auto-connect indefinitely; the home requests will still validate the token.
                LogService.Warning("Saved-session validation exceeded 5 seconds; continuing with cached profile data.");
            }

            profile.UserName = user?.Name ?? profile.UserName;
            profile.LastConnectedAt = DateTimeOffset.Now;
            _profileStore.Save(_settings);
            await EnterApplicationAsync(profile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Warning("Saved profile connection failed.", ex);
            PrepareProfileLogin(profile, silent ? "自动连接失败，请重新登录。" : ex.Message);
        }
        finally
        {
            HideBusy();
        }
    }

    private void PrepareProfileLogin(ServerProfile profile, string? error)
    {
        _editingProfile = profile;
        DisplayNameTextBox.Text = profile.DisplayName;
        ServerUrlTextBox.Text = profile.ServerUrl;
        UsernameTextBox.Text = profile.UserName;
        PasswordInput.Password = string.Empty;
        AutoConnectCheckBox.IsChecked = profile.AutoConnect;
        ShowLoginPanel(error);
    }

    private void ShowLoginPanel(string? error = null)
    {
        CancelCurrentOperation();
        ServerPanel.Visibility = Visibility.Collapsed;
        AppPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        LoginErrorText.Text = error ?? string.Empty;
        LoginErrorText.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (string.IsNullOrWhiteSpace(ServerUrlTextBox.Text)) ServerUrlTextBox.Focus();
            else PasswordInput.Focus();
        }));
    }

    private void BackToServersButton_Click(object sender, RoutedEventArgs e) => ShowServerPanel();

    private void ShowServerPanel()
    {
        CancelCurrentOperation();
        HideDetailSheet();
        LoginPanel.Visibility = Visibility.Collapsed;
        AppPanel.Visibility = Visibility.Collapsed;
        ServerPanel.Visibility = Visibility.Visible;
        RefreshProfiles();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await LoginAsync();

    private async void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        var operationToken = BeginOperation();
        LoginErrorText.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(ServerUrlTextBox.Text) || string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            ShowLoginError("请填写服务器地址和用户名。");
            return;
        }

        LoginButton.IsEnabled = false;
        LoginButton.Content = "正在登录…";
        try
        {
            var profile = _editingProfile ?? new ServerProfile();
            profile.DisplayName = DisplayNameTextBox.Text.Trim();
            profile.ServerUrl = ServerUrlTextBox.Text.Trim();
            profile.UserName = UsernameTextBox.Text.Trim();
            profile.AutoConnect = AutoConnectCheckBox.IsChecked == true;
            profile.DeviceId = string.IsNullOrWhiteSpace(profile.DeviceId) ? $"erp-{Guid.NewGuid():N}" : profile.DeviceId;

            ReplaceApi(new EmbyApiClient(profile.ServerUrl, profile.DeviceId, _settings.MaxStreamingBitrate));
            profile.ServerUrl = _api!.ServerBaseUri.ToString().TrimEnd('/');
            var auth = await _api.AuthenticateAsync(profile.UserName, PasswordInput.Password, operationToken);
            PublicSystemInfo? info = null;
            try
            {
                using var infoTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                infoTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                info = await _api.GetPublicSystemInfoAsync(infoTimeout.Token);
            }
            catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
            {
                LogService.Warning("Public server info request timed out after successful login.");
            }
            catch (Exception ex)
            {
                LogService.Warning("Public server info request failed after successful login.", ex);
            }

            profile.ServerId = auth.ServerId ?? auth.User?.ServerId ?? info?.Id ?? string.Empty;
            profile.UserId = auth.User!.Id!;
            profile.UserName = auth.User.Name ?? profile.UserName;
            profile.ServerVersion = info?.Version;
            profile.LastConnectedAt = DateTimeOffset.Now;
            if (string.IsNullOrWhiteSpace(profile.DisplayName)) profile.DisplayName = info?.ServerName ?? profile.UserName + " 的 Emby";

            if (profile.AutoConnect)
            {
                foreach (var other in _settings.Profiles.Where(item => item.ProfileId != profile.ProfileId))
                    other.AutoConnect = false;
            }
            _profileStore.SaveProfile(_settings, profile, auth.AccessToken!);
            RefreshProfiles();
            await EnterApplicationAsync(profile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Warning("Login failed.", ex);
            ShowLoginError(ex.Message);
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "连接并登录";
        }
    }

    private void ShowLoginError(string message)
    {
        LoginErrorText.Text = message;
        LoginErrorText.Visibility = Visibility.Visible;
    }

    private async Task EnterApplicationAsync(ServerProfile profile)
    {
        _activeProfile = profile;
        _images = new ImageCacheService(_api!);
        _settings.LastProfileId = profile.ProfileId;
        _profileStore.Save(_settings);
        CurrentServerText.Text = $"{profile.FriendlyName}\n{profile.UserName}";
        ServerPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        AppPanel.Visibility = Visibility.Visible;
        HideDetailSheet();
        // Start common home reads early. Request coalescing means LoadHomeAsync joins the same
        // request when no cache exists, while repeat visits are served directly from cache.
        _api?.PrefetchHome();
        await LoadHomeAsync();
    }

    private async void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        HideDetailSheet();
        SetActiveNavigation(HomeNavButton);
        await LoadHomeAsync();
    }

    private void SetActiveNavigation(Button? active)
    {
        foreach (var button in new[] { HomeNavButton, LibrariesNavButton, FavoritesNavButton })
            button.Tag = ReferenceEquals(button, active) ? "Active" : null;
    }

    private async Task LoadHomeAsync()
    {
        if (_api is null) return;
        SetActiveNavigation(HomeNavButton);
        var token = BeginOperation();
        BeginViewportImageSession();
        HeaderTitleText.Text = "首页";
        HomeScroll.Visibility = Visibility.Visible;
        BrowseScroll.Visibility = Visibility.Collapsed;
        _navigation.Clear();
        _currentNavigation = new NavigationState(BrowseKind.Home, "首页");

        // The API layer owns freshness now. These calls return typed memory-cache objects in
        // micro/milliseconds when warm, disk cache on restart, or share one in-flight HTTP task.
        var tasks = new List<Task>
        {
            LoadNextUpHomeSectionAsync(token),
            LoadResumeHomeSectionAsync(token),
            LoadLatestMoviesHomeSectionAsync(token),
            LoadLatestEpisodesHomeSectionAsync(token),
            LoadTopRatedHomeSectionAsync(token),
            LoadGenreHomeSectionsAsync(token)
        };

        ShowBusy("正在加载首页…");
        try
        {
            // Render whichever section finishes first instead of blocking the whole home page
            // behind the slowest Latest request.
            if (tasks.Count > 0)
            {
                await Task.WhenAny(tasks);
                HideBusy();
                await Task.WhenAll(tasks);
            }
        }
        catch (OperationCanceledException) { }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            LogService.Warning("Home load rejected because the saved session is no longer valid.", ex);
            if (_activeProfile is not null)
                PrepareProfileLogin(_activeProfile, "登录已过期，请重新输入密码。");
            else
                ShowMainError("登录已过期", ex);
        }
        catch (Exception ex)
        {
            ShowMainError("首页加载失败", ex);
        }
        finally { HideBusy(); }
    }

    private async Task LoadNextUpHomeSectionAsync(CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var items = await _api.GetNextUpAsync(limit: EmbyApiClient.DefaultHomeRowLimit, cancellationToken: token);
            UpsertHomeRow("nextup", rank: 10, "接着看", "追更剧集下一集", "下一集", isLandscape: true, items);
        }
        catch (OperationCanceledException) { throw; }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { throw; }
        catch (Exception ex)
        {
            LogService.Warning("Next-up section failed to load.", ex);
            RemoveHomeRow("nextup");
        }
    }

    private async Task LoadResumeHomeSectionAsync(CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var items = await _api.GetResumeItemsAsync(token);
            UpsertHomeRow("resume", rank: 20, "继续观看", "从上次离开的地方接着看", "继续播放", isLandscape: true, items);
        }
        catch (OperationCanceledException) { throw; }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { throw; }
        catch (Exception ex)
        {
            LogService.Warning("Continue-watching section failed to load.", ex);
            RemoveHomeRow("resume");
        }
    }

    private async Task LoadLatestMoviesHomeSectionAsync(CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var items = await _api.GetLatestAsync("Movie", limit: EmbyApiClient.DefaultHomeRowLimit, cancellationToken: token);
            UpsertHomeRow("latest-movies", rank: 40, "最新电影", "刚刚加入你的电影库", "电影", isLandscape: false, items);
        }
        catch (OperationCanceledException) { throw; }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { throw; }
        catch (Exception ex)
        {
            LogService.Warning("Latest-movies section failed to load.", ex);
            RemoveHomeRow("latest-movies");
        }
    }

    private async Task LoadLatestEpisodesHomeSectionAsync(CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var items = await _api.GetLatestAsync("Episode", limit: EmbyApiClient.DefaultHomeRowLimit, cancellationToken: token);
            UpsertHomeRow("latest-episodes", rank: 50, "最新剧集", "追更内容与新上线单集", "剧集", isLandscape: true, items);
        }
        catch (OperationCanceledException) { throw; }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { throw; }
        catch (Exception ex)
        {
            LogService.Warning("Latest-episodes section failed to load.", ex);
            RemoveHomeRow("latest-episodes");
        }
    }

    private async Task LoadTopRatedHomeSectionAsync(CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var items = await _api.GetTopRatedAsync(limit: 20, cancellationToken: token);
            UpsertHomeRow("top-rated", rank: 60, "高分佳作", "社区评分 7.0 以上口碑佳作", "高分", isLandscape: false, items);
        }
        catch (OperationCanceledException) { throw; }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { throw; }
        catch (Exception ex)
        {
            LogService.Warning("Top-rated section failed to load.", ex);
            RemoveHomeRow("top-rated");
        }
    }

    private async Task LoadGenreHomeSectionsAsync(CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var genres = await _api.GetGenresAsync(token);
            if (genres.Count == 0) return;

            var selectedGenres = genres
                .Where(g => !string.IsNullOrWhiteSpace(g.Name))
                .Take(3)
                .ToList();

            var throttler = new SemaphoreSlim(2, 2);
            var genreTasks = selectedGenres.Select(async (genre, index) =>
            {
                await throttler.WaitAsync(token);
                try
                {
                    var items = await _api.GetItemsByGenreAsync(genre.Name!, limit: 20, cancellationToken: token);
                    var key = $"genre-{genre.Name}";
                    UpsertHomeRow(key, rank: 70 + index, $"{genre.Name}精选", "高口碑类型佳作", genre.Name!, isLandscape: false, items);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    LogService.Warning($"Genre row failed to load for {genre.Name}.", ex);
                    RemoveHomeRow($"genre-{genre.Name}");
                }
                finally
                {
                    throttler.Release();
                }
            });

            await Task.WhenAll(genreTasks);
        }
        catch (OperationCanceledException) { throw; }
        catch (EmbyApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { throw; }
        catch (Exception ex)
        {
            LogService.Warning("Genre sections discovery failed to load.", ex);
        }
    }

    private void UpsertHomeRow(string key, int rank, string title, string subtitle, string badgeText, bool isLandscape, IReadOnlyList<EmbyItem> items)
    {
        if (items.Count == 0)
        {
            RemoveHomeRow(key);
            return;
        }

        var cards = CreateCards(items);
        if (_homeRowsByKey.TryGetValue(key, out var existing))
        {
            existing.Cards.Clear();
            foreach (var card in cards) existing.Cards.Add(card);
            ScheduleHorizontalViewportForRegisteredRow(existing);
            UpdateHomeEmptyState();
            return;
        }

        var row = new HomeRowViewModel(key, rank, title, subtitle, badgeText, isLandscape);
        foreach (var card in cards) row.Cards.Add(card);
        _homeRowsByKey[key] = row;

        // Insert maintaining deterministic Rank order
        var insertIndex = 0;
        while (insertIndex < _homeRows.Count && _homeRows[insertIndex].Rank <= rank)
            insertIndex++;
        _homeRows.Insert(insertIndex, row);
        UpdateHomeEmptyState();
        UpdateHeroCandidates();
    }

    private void RemoveHomeRow(string key)
    {
        if (_homeRowsByKey.Remove(key, out var oldRow))
        {
            _homeRows.Remove(oldRow);
            CleanStaleHomeRowViewports();
            UpdateHomeEmptyState();
            UpdateHeroCandidates();
        }
    }

    private void UpdateHomeEmptyState()
    {
        HomeEmptyStateText.Visibility = _homeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateHeroCandidates()
    {
        var candidates = new List<EmbyItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFromRow(string rowKey, int take = 2)
        {
            if (!_homeRowsByKey.TryGetValue(rowKey, out var row)) return;
            foreach (var card in row.Cards.Take(take))
            {
                if (card.Item.CanPlay && seenIds.Add(card.Item.Id))
                    candidates.Add(card.Item);
            }
        }

        AddFromRow("nextup", 1);
        AddFromRow("resume", 1);
        AddFromRow("top-rated", 2);
        AddFromRow("latest-movies", 2);

        var finalCandidates = candidates.Take(5).ToList();
        if (finalCandidates.Count == 0)
        {
            StopHeroRotation();
            HeroPanel.Visibility = Visibility.Collapsed;
            _heroCandidates.Clear();
            _currentHeroItem = null;
            return;
        }

        var changed = _heroCandidates.Count != finalCandidates.Count ||
                      !_heroCandidates.Select(c => c.Id).SequenceEqual(finalCandidates.Select(c => c.Id));

        if (changed)
        {
            _heroCandidates.Clear();
            _heroCandidates.AddRange(finalCandidates);
            _ = ShowHeroSlideAsync(0);
            StartHeroRotation();
        }
    }

    private async Task ShowHeroSlideAsync(int index)
    {
        if (_heroCandidates.Count == 0)
        {
            HeroPanel.Visibility = Visibility.Collapsed;
            _currentHeroItem = null;
            return;
        }

        _heroIndex = (index % _heroCandidates.Count + _heroCandidates.Count) % _heroCandidates.Count;
        var item = _heroCandidates[_heroIndex];
        _currentHeroItem = item;

        try { _heroCts?.Cancel(); } catch { }
        _heroCts?.Dispose();
        _heroCts = new CancellationTokenSource();
        var token = _heroCts.Token;

        HeroPanel.Visibility = Visibility.Visible;
        HeroTitleText.Text = item.DisplayName;

        var badge = item.UserData?.PlaybackPositionTicks > 0 ? "继续观看"
            : string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) ? "追更单集"
            : (item.CommunityRating is >= 7.0 ? "高分口碑" : "精选推荐");
        HeroBadgeText.Text = badge;

        var metaParts = new List<string>();
        if (item.ProductionYear is > 0) metaParts.Add(item.ProductionYear.Value.ToString());
        if (item.CommunityRating is > 0) metaParts.Add($"★ {item.CommunityRating:0.0}");
        if (item.Genres is { Count: > 0 }) metaParts.Add(string.Join(" / ", item.Genres.Take(2)));
        if (item.RunTimeTicks is > 0) metaParts.Add(FormatDuration(TimeSpan.FromTicks(item.RunTimeTicks.Value)));
        HeroMetaText.Text = string.Join("   ·   ", metaParts);

        var overview = item.Overview;
        HeroOverviewText.Text = string.IsNullOrWhiteSpace(overview) ? "精彩内容，即刻开播。" : overview;

        RenderHeroDots();

        var images = _images;
        if (images is null) return;

        try
        {
            var backdropTask = images.GetBackdropAsync(item, 1600, token, ImageRequestPriority.Immediate);
            var logoTask = string.IsNullOrWhiteSpace(item.EffectiveLogoTag)
                ? Task.FromResult<BitmapSource?>(null)
                : images.GetAsync(item.EffectiveLogoItemId, "Logo", item.EffectiveLogoTag, 512, token, ImageRequestPriority.Immediate);

            var (backdrop, logo) = (await backdropTask.ConfigureAwait(false), await logoTask.ConfigureAwait(false));
            if (token.IsCancellationRequested) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;

                if (logo is not null)
                {
                    HeroLogoImage.Source = logo;
                    HeroLogoImage.Visibility = Visibility.Visible;
                    HeroTitleText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    HeroLogoImage.Source = null;
                    HeroLogoImage.Visibility = Visibility.Collapsed;
                    HeroTitleText.Visibility = Visibility.Visible;
                }

                if (backdrop is not null)
                {
                    var incoming = _heroUsingFrontImage ? HeroBackdropImageB : HeroBackdropImageA;
                    var outgoing = _heroUsingFrontImage ? HeroBackdropImageA : HeroBackdropImageB;
                    _heroUsingFrontImage = !_heroUsingFrontImage;

                    incoming.Source = backdrop;
                    var fadeDuration = TimeSpan.FromMilliseconds(450);
                    var ease = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };

                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, fadeDuration) { EasingFunction = ease };
                    var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, fadeDuration) { EasingFunction = ease };

                    incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Warning($"Hero billboard artwork failed for {item.Id}.", ex);
        }
    }

    private void RenderHeroDots()
    {
        HeroIndicatorsPanel.Children.Clear();
        if (_heroCandidates.Count <= 1) return;

        for (var i = 0; i < _heroCandidates.Count; i++)
        {
            var dotIndex = i;
            var isActive = i == _heroIndex;
            var dot = new Border
            {
                Width = isActive ? 24 : 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = isActive ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _ = ShowHeroSlideAsync(dotIndex);
                RestartHeroRotationTimer();
            };
            HeroIndicatorsPanel.Children.Add(dot);
        }
    }

    private void StartHeroRotation()
    {
        if (_heroTimer is null)
        {
            _heroTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) =>
            {
                if (HomeScroll.Visibility == Visibility.Visible && DetailPage.Visibility != Visibility.Visible && _heroCandidates.Count > 1)
                {
                    _ = ShowHeroSlideAsync(_heroIndex + 1);
                }
            }, Dispatcher);
        }
        _heroTimer.Start();
    }

    private void RestartHeroRotationTimer()
    {
        _heroTimer?.Stop();
        _heroTimer?.Start();
    }

    private void StopHeroRotation() => _heroTimer?.Stop();

    private async void HeroPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHeroItem is null || _api is null) return;
        ShowBusy("正在准备播放…");
        try
        {
            await StartPlaybackAsync(_currentHeroItem, null, CurrentToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowMainError("播放失败", ex);
        }
        finally
        {
            HideBusy();
        }
    }

    private async void HeroDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHeroItem is null) return;
        await ShowDetailsAsync(_currentHeroItem);
    }

    private void CleanStaleHomeRowViewports()
    {
        var stale = _homeRowViewports.Where(pair => !_homeRowsByKey.ContainsKey(pair.Value.Key)).Select(p => p.Key).ToList();
        foreach (var scroller in stale) _homeRowViewports.Remove(scroller);
    }

    private async Task RefreshResumeSectionAsync()
    {
        if (_api is null || DetailPage.Visibility == Visibility.Visible || _currentNavigation?.Kind != BrowseKind.Home || HomeScroll.Visibility != Visibility.Visible) return;
        var token = BeginOperation();
        try
        {
            await Task.WhenAll(LoadResumeHomeSectionAsync(token), LoadNextUpHomeSectionAsync(token));
        }
        catch (OperationCanceledException) { }
    }

    private async void LibrariesButton_Click(object sender, RoutedEventArgs e)
    {
        HideDetailSheet();
        SetActiveNavigation(LibrariesNavButton);
        if (_api is null) return;
        var token = BeginOperation();
        ShowBusy("正在加载媒体库…");
        try
        {
            var items = await _api.GetViewsAsync(token);
            _navigation.Clear();
            await ShowBrowseAsync(new NavigationState(BrowseKind.LibraryRoot, "媒体库"), items, false, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMainError("媒体库加载失败", ex); }
        finally { HideBusy(); }
    }

    private async void FavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        HideDetailSheet();
        SetActiveNavigation(FavoritesNavButton);
        if (_api is null) return;
        var token = BeginOperation();
        ShowBusy("正在加载收藏…");
        try
        {
            var items = await _api.GetFavoriteItemsAsync(token);
            _navigation.Clear();
            await ShowBrowseAsync(new NavigationState(BrowseKind.Favorites, "收藏"), items, false, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMainError("收藏加载失败", ex); }
        finally { HideBusy(); }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholderText is null) return;
        SearchPlaceholderText.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task SearchAsync()
    {
        if (_api is null || string.IsNullOrWhiteSpace(SearchTextBox.Text)) return;
        SetActiveNavigation(null);
        var term = SearchTextBox.Text.Trim();
        var token = BeginOperation();
        ShowBusy($"正在搜索“{term}”…");
        try
        {
            var items = await _api.SearchAsync(term, token);
            // Searching is a navigation step now, not a stack reset: the previous browse
            // context survives so 返回 restores it with its scroll offset.
            PushCurrentBrowseSnapshot();
            await ShowBrowseAsync(new NavigationState(BrowseKind.Search, $"搜索：{term}", SearchTerm: term), items, false, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMainError("搜索失败", ex); }
        finally { HideBusy(); }
    }

    private async void MediaCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_api is null || sender is not FrameworkElement element || element.Tag is not MediaCardViewModel card) return;
        if (_prefetchedCardIds.Contains(card.Item.Id)) return;

        // A short dwell prevents fast pointer sweeps from warming dozens of cards.
        try { await Task.Delay(180, CurrentToken); }
        catch (OperationCanceledException) { return; }
        if (!element.IsMouseOver || _api is null || !_prefetchedCardIds.Add(card.Item.Id)) return;

        if (card.Item.CanBrowse) _api.PrefetchChildren(card.Item.Id);
        if (card.Item.CanPlay) _api.PrefetchItem(card.Item.Id);
    }

    private async void MediaCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaCardViewModel card) return;
        if (card.Item.CanBrowse)
        {
            await BrowseIntoAsync(card.Item);
            return;
        }
        if (IsPersonItem(card.Item))
        {
            await BrowsePersonAsync(card.Item);
            return;
        }
        if (card.Item.CanPlay) await ShowDetailsAsync(card.Item);
    }

    private static bool IsPersonItem(EmbyItem item) =>
        string.Equals(item.Type, "Person", StringComparison.OrdinalIgnoreCase);

    private async Task BrowsePersonAsync(EmbyItem person)
    {
        if (_api is null) return;
        var token = BeginOperation();
        ShowBusy($"正在加载 {person.Name} 的作品…");
        try
        {
            var items = await _api.GetPersonItemsAsync(person.Id, token);
            PushCurrentBrowseSnapshot();
            await ShowBrowseAsync(new NavigationState(BrowseKind.Parent, person.Name, person.Id), items, true, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMainError("人物作品加载失败", ex); }
        finally { HideBusy(); }
    }

    private void PushCurrentBrowseSnapshot()
    {
        if (BrowseScroll.Visibility != Visibility.Visible || _currentNavigation is null) return;
        // Keep the actual list and scroll position in memory. Back navigation should never
        // wait for HTTP or even metadata-cache deserialization.
        _navigation.Push(new BrowseSnapshot(
            _currentNavigation,
            _browseCards.Select(card => card.Item).ToList(),
            BrowseScroll.VerticalOffset));
    }

    private async Task BrowseIntoAsync(EmbyItem parent)
    {
        if (_api is null) return;
        var token = BeginOperation();
        ShowBusy("正在加载内容…");
        try
        {
            IReadOnlyList<EmbyItem> items = await _api.GetItemsAsync(parent.Id, token);
            items = DeduplicateBrowseItems(items);
            PushCurrentBrowseSnapshot();

            // Single-file movie folders: skip the one-card intermediate list and open the
            // detail page directly; 返回 still returns to the list we just left.
            if (items.Count == 1 && items[0].CanPlay && !items[0].CanBrowse)
            {
                await ShowDetailsAsync(items[0]);
                return;
            }

            await ShowBrowseAsync(new NavigationState(BrowseKind.Parent, parent.DisplayName, parent.Id), items, true, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowMainError("内容加载失败", ex); }
        finally { HideBusy(); }
    }

    private Task ShowBrowseAsync(NavigationState state, IReadOnlyList<EmbyItem> items, bool canGoBack, CancellationToken token)
    {
        // Shared servers sometimes list the same title twice (a properly scraped entry plus a
        // raw-filename "{tmdb-…}" entry pointing at the same movie). Collapse those before rendering.
        items = DeduplicateBrowseItems(items);
        _currentNavigation = state;
        HeaderTitleText.Text = state.Title;
        HomeScroll.Visibility = Visibility.Collapsed;
        BrowseScroll.Visibility = Visibility.Visible;
        BrowseBackButton.Visibility = canGoBack || _navigation.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowseStatusText.Text = $"内容  {items.Count}";
        BrowseEmptyText.Text = state.Kind == BrowseKind.Search ? "没有找到相关内容" : "这里还没有内容";
        BrowseEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BeginViewportImageSession();
        _browseCards = CreateCards(items);
        _browseVisibleCards.Clear();

        // Content-driven layout: episodes/videos carry 16:9 stills and belong in landscape
        // cards; movies/series carry 2:3 posters. Mixed lists split into typed sections so
        // no tile ever hosts a mismatched aspect ratio. Library roots always use wide tiles.
        var landscapeCount = 0;
        foreach (var card in _browseCards)
        {
            if (IsLandscapeCardItem(card.Item)) landscapeCount++;
        }

        var useSections = state.Kind == BrowseKind.Search
                          || (landscapeCount > 0 && landscapeCount < _browseCards.Count);
        _browseFlatLandscape = !useSections
                               && (state.Kind == BrowseKind.LibraryRoot || landscapeCount == _browseCards.Count);

        if (useSections)
        {
            // Grouped sections are derived purely from the item list, so the existing
            // BrowseSnapshot(State, Items, Offset) back stack restores them for free.
            BrowseItemsControl.Visibility = Visibility.Collapsed;
            SearchSectionsStack.Visibility = Visibility.Visible;
            BuildSearchSections(_browseCards);
            AppendSearchCards(BrowseInitialRenderCount);
        }
        else
        {
            BrowseItemsControl.Visibility = Visibility.Visible;
            SearchSectionsStack.Visibility = Visibility.Collapsed;
            ClearSearchSections();
            BrowseItemsControl.ItemTemplate = (DataTemplate)FindResource(_browseFlatLandscape ? "LandscapeCardTemplate" : "PosterCardTemplate");
            AppendBrowseCards(BrowseInitialRenderCount);
            BrowseItemsControl.ItemsSource = _browseVisibleCards;
        }
        Dispatcher.BeginInvoke(new Action(ScheduleBrowseViewport), DispatcherPriority.Loaded);
        return Task.CompletedTask;
    }

    private static bool IsLandscapeCardItem(EmbyItem item) =>
        item.Type is "Episode" or "Video" or "MusicVideo" or "Trailer";

    private static readonly System.Text.RegularExpressions.Regex TrailingYearPattern =
        new(@"\s*[\(（](?<year>\d{4})[\)）]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IReadOnlyList<EmbyItem> DeduplicateBrowseItems(IReadOnlyList<EmbyItem> items)
    {
        if (items.Count < 2) return items;

        var result = new List<EmbyItem>(items.Count);
        // name-key → candidates. Plain folders join the movie group but require a strict
        // year match so a real "Season 1"-style folder can never collapse onto a movie.
        var buckets = new Dictionary<string, List<(int Index, int Year, bool Strict)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var nameKey = BuildDedupeNameKey(item);
            var year = ResolveItemYear(item);
            var strict = IsPlainFolderItem(item);

            if (nameKey is not null && buckets.TryGetValue(nameKey, out var candidates))
            {
                var merged = false;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var (index, existingYear, existingStrict) = candidates[i];
                    var compatible = existingYear == year
                                     || (!strict && !existingStrict && (existingYear == 0 || year == 0));
                    if (!compatible) continue;

                    // Keep one entry per title — preferring the one that carries artwork.
                    var existing = result[index];
                    if (string.IsNullOrWhiteSpace(existing.OwnPrimaryImageTag) &&
                        !string.IsNullOrWhiteSpace(item.OwnPrimaryImageTag))
                    {
                        result[index] = item;
                        candidates[i] = (index, year == 0 ? existingYear : year, existingStrict || strict);
                    }
                    merged = true;
                    break;
                }
                if (merged) continue;
            }

            if (nameKey is not null)
            {
                (buckets.TryGetValue(nameKey, out var list) ? list : buckets[nameKey] = []).Add((result.Count, year, strict));
            }
            result.Add(item);
        }

        var collapsed = items.Count - result.Count;
        if (collapsed > 0)
            LogService.Info($"Collapsed {collapsed} duplicate entr{(collapsed == 1 ? "y" : "ies")} while rendering a browse list.");
        return result;
    }

    private static bool IsPlainFolderItem(EmbyItem item) =>
        item.Type is "Folder" or "VideoFolder";

    private static int ResolveItemYear(EmbyItem item)
    {
        if (item.ProductionYear is int year) return year;
        // Raw-filename entries often carry the year only inside the name, e.g. "xx (2019) {tmdb-…}".
        var match = TrailingYearPattern.Match(ItemNameCleaner.Clean(item.Name ?? string.Empty));
        return match.Success && int.TryParse(match.Groups["year"].Value, out var parsed) ? parsed : 0;
    }

    private static string? BuildDedupeNameKey(EmbyItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return null;
        var name = TrailingYearPattern.Replace(ItemNameCleaner.Clean(item.Name!), string.Empty).Trim();
        if (name.Length == 0) return null;

        // Unscraped movies come back as Type "Video" (or plain "Folder") while their
        // properly scraped twin is a "Movie" — normalize all into one video group.
        var typeGroup = item.Type is "Movie" or "Video" or "MusicVideo" or "Folder" or "VideoFolder" ? "V" : item.Type;
        return $"{typeGroup}|{item.SeriesId}|{item.ParentIndexNumber ?? 0}|{item.IndexNumber ?? 0}|{name}";
    }

    private bool IsSearchBrowseActive =>
        SearchSectionsStack.Visibility == Visibility.Visible && _searchSections.Count > 0;

    private static readonly (string Title, string[] Types, bool Landscape)[] SearchSectionOrder =
    {
        ("电影", new[] { "Movie" }, false),
        ("剧集", new[] { "Series", "Season" }, false),
        ("单集", new[] { "Episode", "Video" }, true),
        ("合集", new[] { "BoxSet" }, false),
        ("人物", new[] { "Person" }, false),
    };

    private void BuildSearchSections(IReadOnlyList<MediaCardViewModel> cards)
    {
        SearchSectionsStack.Children.Clear();
        _searchSections.Clear();

        var assigned = new bool[cards.Count];
        foreach (var (title, types, landscape) in SearchSectionOrder)
        {
            var sectionCards = new List<MediaCardViewModel>();
            for (var i = 0; i < cards.Count; i++)
            {
                if (assigned[i]) continue;
                if (types.Contains(cards[i].Item.Type, StringComparer.OrdinalIgnoreCase))
                {
                    sectionCards.Add(cards[i]);
                    assigned[i] = true;
                }
            }
            if (sectionCards.Count > 0)
                _searchSections.Add(CreateSearchSection(title, sectionCards, landscape));
        }

        var rest = new List<MediaCardViewModel>();
        for (var i = 0; i < cards.Count; i++)
        {
            if (!assigned[i]) rest.Add(cards[i]);
        }
        if (rest.Count > 0)
            _searchSections.Add(CreateSearchSection("其他", rest, landscape: false));
    }

    private SearchCardSection CreateSearchSection(string title, List<MediaCardViewModel> sectionCards, bool landscape)
    {
        var section = new SearchCardSection(title, sectionCards, landscape);
        var accent = (Brush)FindResource("AccentBrush");
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 16)
        };

        header.Children.Add(new Border
        {
            Width = 4,
            Height = 26,
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var titleText = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(titleText);

        SearchSectionsStack.Children.Add(header);

        var control = new ItemsControl
        {
            ItemTemplate = (DataTemplate)FindResource(landscape ? "LandscapeCardTemplate" : "PosterCardTemplate"),
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
            ItemsSource = section.VisibleCards,
            Margin = new Thickness(0, 0, 0, 24)
        };
        section.ItemsControl = control;
        SearchSectionsStack.Children.Add(control);
        return section;
    }

    private void ClearSearchSections()
    {
        _searchSections.Clear();
        SearchSectionsStack.Children.Clear();
    }

    private void AppendSearchCards(int count)
    {
        // Fill sections in order until the budget runs out. This guarantees every visible
        // header is followed by rendered cards while the first screen stays cheap.
        var remaining = count;
        foreach (var section in _searchSections)
        {
            if (remaining <= 0) break;
            var start = section.VisibleCards.Count;
            var addCount = Math.Min(remaining, section.Cards.Count - start);
            for (var i = 0; i < addCount; i++)
                section.VisibleCards.Add(section.Cards[start + i]);
            remaining -= addCount;
        }
    }

    private async void BrowseBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigation.Count == 0)
        {
            await LoadHomeAsync();
            return;
        }

        var previous = _navigation.Pop();
        var token = BeginOperation();
        await ShowBrowseAsync(previous.State, previous.Items, _navigation.Count > 0, token);
        await Dispatcher.InvokeAsync(() => BrowseScroll.ScrollToVerticalOffset(previous.VerticalOffset), DispatcherPriority.Loaded);
    }

    private static List<MediaCardViewModel> CreateCards(IReadOnlyList<EmbyItem> items) =>
        items.Select(item => new MediaCardViewModel(item)).ToList();

    private void BeginViewportImageSession()
    {
        SuspendViewportImageSession();
        _viewportImageCts = new CancellationTokenSource();
    }

    private void SuspendViewportImageSession()
    {
        try { _viewportImageCts?.Cancel(); } catch { }
        _viewportImageCts?.Dispose();
        _viewportImageCts = null;
    }

    private CancellationToken ViewportImageToken => _viewportImageCts?.Token ?? CancellationToken.None;


    // Pointer-device aware inertial scrolling. Standard mouse wheels, high-resolution wheels
    // and touchpad-style deltas all feed the same target; the render timer eases toward it.
    // This avoids fixed 150px jumps while still making a normal wheel notch feel useful.
    private void VerticalScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
            && FindHorizontalShelf(e.OriginalSource as DependencyObject, scroller) is { } shelf)
        {
            QueueSmoothScroll(shelf, e.Delta, horizontal: true);
            e.Handled = true;
            return;
        }
        QueueSmoothScroll(scroller, e.Delta, horizontal: false);
        e.Handled = true;
    }

    private static ScrollViewer? FindHorizontalShelf(DependencyObject? origin, ScrollViewer outerScroller)
    {
        for (var current = origin; current is not null && !ReferenceEquals(current, outerScroller); current = GetVisualOrLogicalParent(current))
        {
            if (current is ScrollViewer candidate
                && candidate.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                && candidate.ScrollableWidth > 0)
                return candidate;
        }
        return null;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject child)
    {
        if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child);
        return LogicalTreeHelper.GetParent(child);
    }

    private void HorizontalSection_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer shelf) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            QueueSmoothScroll(shelf, e.Delta, horizontal: true);
            e.Handled = true;
            return;
        }

        var target = DetailPage.Visibility == Visibility.Visible
            ? DetailScroll
            : BrowseScroll.Visibility == Visibility.Visible ? BrowseScroll : HomeScroll;
        QueueSmoothScroll(target, e.Delta, horizontal: false);
        e.Handled = true;
    }

    private void QueueSmoothScroll(ScrollViewer scroller, int wheelDelta, bool horizontal)
    {
        if (wheelDelta == 0) return;
        if (!_smoothScrollStates.TryGetValue(scroller, out var state))
        {
            state = new SmoothScrollState();
            _smoothScrollStates[scroller] = state;
        }

        var current = horizontal ? scroller.HorizontalOffset : scroller.VerticalOffset;
        var maximum = horizontal ? scroller.ScrollableWidth : scroller.ScrollableHeight;
        if (maximum <= 0) return;

        // Preserve raw delta so high-resolution devices naturally move in smaller increments.
        // A 120-delta mouse notch targets ~82 DIPs; Shift-horizontal is slightly stronger.
        var dipPerDelta = horizontal ? 0.86 : 0.68;
        var delta = -wheelDelta * dipPerDelta;
        var baseOffset = state.Active && state.Horizontal == horizontal ? state.Target : current;
        state.Horizontal = horizontal;
        state.Target = Math.Clamp(baseOffset + delta, 0, maximum);
        state.Active = true;

        if (!_smoothScrollTimer.IsEnabled) _smoothScrollTimer.Start();
    }

    private void SmoothScrollTimer_Tick(object? sender, EventArgs e)
    {
        var anyActive = false;
        foreach (var pair in _smoothScrollStates.ToArray())
        {
            var scroller = pair.Key;
            var state = pair.Value;
            if (!state.Active || !scroller.IsLoaded) continue;

            var current = state.Horizontal ? scroller.HorizontalOffset : scroller.VerticalOffset;
            var maximum = state.Horizontal ? scroller.ScrollableWidth : scroller.ScrollableHeight;
            state.Target = Math.Clamp(state.Target, 0, Math.Max(0, maximum));
            var remaining = state.Target - current;
            if (Math.Abs(remaining) < 0.35)
            {
                if (state.Horizontal) scroller.ScrollToHorizontalOffset(state.Target);
                else scroller.ScrollToVerticalOffset(state.Target);
                state.Active = false;
                continue;
            }

            // Critically damped-feeling exponential approach: quick response at first,
            // progressively softer near the target, with no overshoot.
            var next = current + remaining * 0.24;
            if (state.Horizontal) scroller.ScrollToHorizontalOffset(next);
            else scroller.ScrollToVerticalOffset(next);
            anyActive = true;
        }

        if (!anyActive) _smoothScrollTimer.Stop();
    }

    private void DynamicHomeRow_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        if (scroller.DataContext is not HomeRowViewModel row)
        {
            if (!_homeRowViewports.TryGetValue(scroller, out row!)) return;
        }
        else
        {
            _homeRowViewports[scroller] = row;
        }

        ScheduleHorizontalViewport(row.Cards, preferBackdrop: row.PreferBackdrop, scroller, row.CellWidth);
    }

    private void HomeScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleVisibleHomeRowsViewport();

    private void ScheduleVisibleHomeRowsViewport()
    {
        if (_images is null || HomeScroll.Visibility != Visibility.Visible || ViewportImageToken.IsCancellationRequested) return;
        if (HomeScroll.ViewportHeight <= 0) return;

        foreach (var pair in _homeRowViewports.ToArray())
        {
            var scroller = pair.Key;
            var row = pair.Value;
            if (!scroller.IsLoaded || scroller.Visibility != Visibility.Visible) continue;
            try
            {
                var point = scroller.TranslatePoint(new Point(0, 0), HomeScroll);
                var height = Math.Max(120, scroller.ActualHeight);
                const double margin = 520;
                if (point.Y < HomeScroll.ViewportHeight + margin && point.Y + height > -margin)
                {
                    ScheduleHorizontalViewport(row.Cards, preferBackdrop: row.PreferBackdrop, scroller, row.CellWidth);
                }
            }
            catch { }
        }
    }

    private void ScheduleHorizontalViewportForRegisteredRow(HomeRowViewModel row)
    {
        foreach (var (scroller, r) in _homeRowViewports)
        {
            if (ReferenceEquals(r, row) && scroller.IsLoaded)
            {
                ScheduleHorizontalViewport(row.Cards, preferBackdrop: row.PreferBackdrop, scroller, row.CellWidth);
                break;
            }
        }
    }

    private void BrowseScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        EnsureBrowseRenderWindow();
        ScheduleBrowseViewport();
    }

    private void ScheduleHorizontalViewport(
        IReadOnlyList<MediaCardViewModel> cards,
        bool preferBackdrop,
        ScrollViewer scroller,
        double cellWidth)
    {
        if (_images is null || cards.Count == 0 || ViewportImageToken.IsCancellationRequested) return;

        var viewport = scroller.ViewportWidth > 1 ? scroller.ViewportWidth : Math.Max(700, ActualWidth - 310);
        var first = Math.Max(0, (int)Math.Floor(scroller.HorizontalOffset / cellWidth) - 1);
        var visibleCount = Math.Max(4, (int)Math.Ceiling(viewport / cellWidth) + 3);
        var visibleEnd = Math.Min(cards.Count, first + visibleCount);

        ScheduleCardRange(cards, preferBackdrop, first, visibleEnd - first, ImageRequestPriority.Visible);

        // Only warm one short look-ahead window. This keeps fast scrolling responsive without
        // allowing a 100-item library to flood the network queue.
        var prefetchStart = visibleEnd;
        var prefetchCount = Math.Min(6, cards.Count - prefetchStart);
        if (prefetchCount > 0)
            ScheduleCardRange(cards, preferBackdrop, prefetchStart, prefetchCount, ImageRequestPriority.Prefetch);
    }

    private void AppendBrowseCards(int count)
    {
        if (count <= 0 || _browseVisibleCards.Count >= _browseCards.Count) return;
        var end = Math.Min(_browseCards.Count, _browseVisibleCards.Count + count);
        for (var i = _browseVisibleCards.Count; i < end; i++)
            _browseVisibleCards.Add(_browseCards[i]);
    }

    private void EnsureBrowseRenderWindow()
    {
        if (BrowseScroll.Visibility != Visibility.Visible) return;
        if (BrowseScroll.ExtentHeight <= 0 || BrowseScroll.ViewportHeight <= 0) return;

        var cellHeight = _browseFlatLandscape ? LandscapeCellHeight : PosterCellHeight;
        var remaining = BrowseScroll.ExtentHeight - (BrowseScroll.VerticalOffset + BrowseScroll.ViewportHeight);
        if (remaining > cellHeight * 2.5) return;

        if (IsSearchBrowseActive)
        {
            if (_searchSections.Any(section => section.VisibleCards.Count < section.Cards.Count))
                AppendSearchCards(BrowseRenderBatchSize);
            return;
        }
        if (_browseVisibleCards.Count < _browseCards.Count)
            AppendBrowseCards(BrowseRenderBatchSize);
    }

    private void ScheduleBrowseViewport()
    {
        if (_images is null || _browseCards.Count == 0 || BrowseScroll.Visibility != Visibility.Visible || ViewportImageToken.IsCancellationRequested) return;

        if (IsSearchBrowseActive)
        {
            ScheduleSearchViewport();
            return;
        }

        var width = BrowseScroll.ViewportWidth > 1 ? BrowseScroll.ViewportWidth : Math.Max(700, ActualWidth - 310);
        var cellWidth = _browseFlatLandscape ? LandscapeCellWidth : PosterCellWidth;
        var cellHeight = _browseFlatLandscape ? LandscapeCellHeight : PosterCellHeight;
        var columns = Math.Max(1, (int)Math.Floor(width / cellWidth));
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(BrowseScroll.VerticalOffset / cellHeight) - 1);
        var visibleRows = Math.Max(2, (int)Math.Ceiling(Math.Max(400, BrowseScroll.ViewportHeight) / cellHeight) + 2);
        var availableCount = _browseVisibleCards.Count;
        var start = Math.Min(availableCount, firstVisibleRow * columns);
        var visibleCount = Math.Min(availableCount - start, visibleRows * columns);

        ScheduleCardRange(_browseCards, preferBackdrop: _browseFlatLandscape, start, visibleCount, ImageRequestPriority.Visible);

        var prefetchStart = start + visibleCount;
        var prefetchCount = Math.Min(columns * 2, availableCount - prefetchStart);
        if (prefetchCount > 0)
            ScheduleCardRange(_browseCards, preferBackdrop: _browseFlatLandscape, prefetchStart, prefetchCount, ImageRequestPriority.Prefetch);
    }

    private void ScheduleSearchViewport()
    {
        if (BrowseScroll.ViewportHeight <= 0) return;
        var viewportTop = BrowseScroll.VerticalOffset;
        var viewportBottom = viewportTop + BrowseScroll.ViewportHeight;
        var width = BrowseScroll.ViewportWidth > 1 ? BrowseScroll.ViewportWidth : Math.Max(700, ActualWidth - 310);

        foreach (var section in _searchSections)
        {
            var availableCount = section.VisibleCards.Count;
            if (availableCount == 0) continue;
            var cellWidth = section.Landscape ? LandscapeCellWidth : PosterCellWidth;
            var cellHeight = section.Landscape ? LandscapeCellHeight : PosterCellHeight;
            var columns = Math.Max(1, (int)Math.Floor(width / cellWidth));

            // Only sections whose WrapPanel is near the vertical viewport enqueue images.
            double sectionTop, sectionHeight;
            try
            {
                var point = section.ItemsControl.TranslatePoint(new Point(0, 0), BrowseScroll);
                sectionTop = point.Y + BrowseScroll.VerticalOffset;
                sectionHeight = Math.Max(120, section.ItemsControl.ActualHeight);
            }
            catch
            {
                continue;
            }

            const double margin = 520;
            if (sectionTop > viewportBottom + margin || sectionTop + sectionHeight < viewportTop - margin) continue;

            var firstRow = Math.Max(0, (int)Math.Floor((viewportTop - sectionTop) / cellHeight) - 1);
            var renderedRows = (int)Math.Ceiling((double)availableCount / columns) - 1;
            var lastRow = Math.Min(renderedRows, (int)Math.Floor((viewportBottom - sectionTop) / cellHeight) + 1);
            if (lastRow < firstRow) continue;

            var start = firstRow * columns;
            var count = Math.Min(availableCount - start, (lastRow - firstRow + 1) * columns);
            if (count > 0)
                ScheduleCardRange(section.VisibleCards, preferBackdrop: section.Landscape, start, count, ImageRequestPriority.Visible);
        }
    }

    private void ScheduleCardRange(
        IReadOnlyList<MediaCardViewModel> cards,
        bool preferBackdrop,
        int start,
        int count,
        ImageRequestPriority priority)
    {
        if (_images is null || count <= 0) return;
        var token = ViewportImageToken;
        var end = Math.Min(cards.Count, start + count);

        for (var i = Math.Max(0, start); i < end; i++)
        {
            var card = cards[i];
            if (!card.TryBeginImageLoad()) continue;
            _ = LoadCardImageAsync(card, preferBackdrop, priority, token);
        }
    }

    private async Task LoadCardImageAsync(
        MediaCardViewModel card,
        bool preferBackdrop,
        ImageRequestPriority priority,
        CancellationToken token)
    {
        var images = _images;
        if (images is null)
        {
            card.ResetImageLoad();
            return;
        }

        try
        {
            // Card-sized requests map to fixed cache buckets inside ImageCacheService:
            // poster 256px, landscape/episode still 512px. This maximizes reuse across pages.
            var isEpisode = string.Equals(card.Item.Type, "Episode", StringComparison.OrdinalIgnoreCase);
            var posterWidth = isEpisode ? 480 : 220;

            // Episodes keep their own 16:9 stills when the server has them; a landscape shelf
            // preference must not turn every episode into the parent Series/Season backdrop.
            // When an episode has no still (common for unscraped libraries), GetCardPosterAsync
            // falls back to series artwork so the tile is never blank.
            BitmapSource? image = null;
            if (isEpisode)
            {
                image = await images.GetCardPosterAsync(card.Item, posterWidth, token, priority).ConfigureAwait(false)
                        ?? await images.GetBackdropAsync(card.Item, 480, token, priority).ConfigureAwait(false);
            }
            else if (preferBackdrop)
            {
                image = await images.GetBackdropAsync(card.Item, 480, token, priority).ConfigureAwait(false)
                        ?? await images.GetCardPosterAsync(card.Item, 480, token, priority).ConfigureAwait(false);
            }
            else
            {
                image = await images.GetCardPosterAsync(card.Item, posterWidth, token, priority).ConfigureAwait(false)
                        ?? await images.GetBackdropAsync(card.Item, 480, token, priority).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested)
            {
                card.ResetImageLoad();
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (image is not null) card.CompleteImageLoad(image);
                else card.CompleteImageLoad(null);
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException)
        {
            card.ResetImageLoad();
        }
        catch (Exception ex)
        {
            card.CompleteImageLoad(null);
            LogService.Warning($"Card image load failed for {card.Item.Id}.", ex);
        }
    }

    private async Task ShowDetailsAsync(EmbyItem listItem)
    {
        if (_api is null || _images is null) return;

        var token = BeginOperation();
        ShowDetailSheet();
        ResetDetailPage(listItem);

        // Let WPF render the new page before any network continuation runs. The user
        // gets an immediate navigation response instead of waiting behind Item/Images.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        try
        {
            var itemTask = _api.GetItemAsync(listItem.Id, token);
            var quickBackdropTask = _images.GetBackdropAsync(listItem, 1600, token, ImageRequestPriority.Immediate);

            _detailItem = await itemTask.ConfigureAwait(true);
            ApplyDetailCore(_detailItem, null);
            PopulateMediaSources(_detailItem.MediaSources?.ToList() ?? []);

            // Parent enrichment, artwork and secondary shelves are independent. None
            // of them is allowed to hold the main detail page hostage.
            Task<EmbyItem?> parentTask = NeedsParentDetail(_detailItem)
                ? GetParentDetailAsync(_detailItem.SeriesId!, token)
                : Task.FromResult<EmbyItem?>(null);

            var heroTask = LoadDetailArtworkAsync(_detailItem, quickBackdropTask, parentTask, token);
            var peopleTask = LoadPeopleSectionAsync(_detailItem, parentTask, token);
            var episodesTask = LoadEpisodeShelfAsync(_detailItem, token);
            var similarTask = LoadSimilarShelfAsync(_detailItem, token);

            var parent = await parentTask.ConfigureAwait(true);
            if (parent is not null)
                ApplyDetailCore(_detailItem, parent);

            // Pre-negotiate playback after the detail shell has rendered. This makes the Play
            // button fast without blocking navigation. The task is canceled when the user leaves.
            _ = ScheduleDetailPlaybackPrefetchAsync(_detailItem, token);

            // These tasks update their own sections and swallow optional endpoint errors.
            _ = heroTask;
            _ = peopleTask;
            _ = episodesTask;
            _ = similarTask;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Navigation changed. The next page owns the UI now.
        }
        catch (Exception ex)
        {
            ShowDetailError(ex.Message);
            LogService.Warning($"Detail page failed for item {listItem.Id}.", ex);
        }
    }

    private void ResetDetailPage(EmbyItem seed)
    {
        _detailItem = seed;
        DetailScroll.ScrollToVerticalOffset(0);
        DetailBackdropImage.Source = null;
        DetailLogoImage.Source = null;
        DetailTitleText.Text = BuildDetailTitle(seed);
        DetailOriginalTitleText.Visibility = Visibility.Collapsed;
        DetailOriginalTitleText.Text = string.Empty;
        DetailMetaText.Text = BuildDetailMeta(seed, null);
        DetailUserStateText.Text = BuildUserState(seed);
        DetailOverviewText.Text = string.IsNullOrWhiteSpace(seed.Overview) ? "正在读取详细信息…" : seed.Overview!;
        DetailTaglineText.Text = string.Empty;
        DetailTaglineText.Visibility = Visibility.Collapsed;
        DetailErrorText.Visibility = Visibility.Collapsed;
        DetailProgressPanel.Visibility = Visibility.Collapsed;
        DetailProgressBar.Value = 0;
        DetailProgressText.Text = string.Empty;
        PlayButton.Content = "▶  播放";
        PlayButton.IsEnabled = false;

        MediaSourceComboBox.ItemsSource = null;
        DetailVideoText.Text = "正在读取媒体信息…";
        DetailTechnicalText.Text = "";
        DetailAudioItemsControl.ItemsSource = null;
        DetailSubtitleItemsControl.ItemsSource = null;

        DetailChaptersSection.Visibility = Visibility.Collapsed;
        DetailChaptersItemsControl.ItemsSource = null;
        DetailEpisodesSection.Visibility = Visibility.Collapsed;
        DetailEpisodesItemsControl.ItemsSource = null;
        DetailPeopleSection.Visibility = Visibility.Collapsed;
        DetailPeopleItemsControl.ItemsSource = null;
        DetailSimilarSection.Visibility = Visibility.Collapsed;
        DetailSimilarItemsControl.ItemsSource = null;
        DetailStudiosSection.Visibility = Visibility.Collapsed;
        DetailStudiosText.Text = string.Empty;
        DetailLocationsSection.Visibility = Visibility.Collapsed;
        DetailLocationsText.Text = string.Empty;
        DetailLinksSection.Visibility = Visibility.Collapsed;
        DetailLinksItemsControl.ItemsSource = null;
        DetailInfoSection.Visibility = Visibility.Collapsed;
        DetailInfoItemsControl.ItemsSource = null;

        _detailEpisodeCards = [];
        _detailSimilarCards = [];
        _detailPeople = [];
        _detailPlaybackInfoTask = null;
        _detailPlaybackInfo = null;
        _detailPlaybackInfoItemId = null;
        _detailPlaybackInfoAt = default;
        ApplyProgressState(seed);
    }

    private void ApplyDetailCore(EmbyItem item, EmbyItem? parent)
    {
        DetailTitleText.Text = BuildDetailTitle(item);

        var secondaryTitle = BuildSecondaryTitle(item);
        DetailOriginalTitleText.Text = secondaryTitle;
        DetailOriginalTitleText.Visibility = string.IsNullOrWhiteSpace(secondaryTitle) ? Visibility.Collapsed : Visibility.Visible;

        DetailMetaText.Text = BuildDetailMeta(item, parent);
        DetailUserStateText.Text = BuildUserState(item);
        DetailOverviewText.Text = BuildDetailOverview(item, parent);

        var tagline = !string.IsNullOrWhiteSpace(item.Tagline)
            ? item.Tagline
            : item.Taglines?.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
              ?? parent?.Tagline
              ?? parent?.Taglines?.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        DetailTaglineText.Text = tagline ?? string.Empty;
        DetailTaglineText.Visibility = string.IsNullOrWhiteSpace(tagline) ? Visibility.Collapsed : Visibility.Visible;

        ApplyProgressState(item);
        PopulateMetadataSections(item, parent);
        PopulateChapters(item);
    }

    private void ApplyProgressState(EmbyItem item)
    {
        var position = Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0);
        var runtime = Math.Max(0, item.RunTimeTicks ?? 0);
        if (position <= 0 || runtime <= 0)
        {
            DetailProgressPanel.Visibility = Visibility.Collapsed;
            PlayButton.Content = "▶  播放";
            return;
        }

        var percentage = Math.Clamp(position * 100d / runtime, 0, 100);
        DetailProgressBar.Value = percentage;
        DetailProgressText.Text = $"已观看 {FormatDuration(TimeSpan.FromTicks(position))} / {FormatDuration(TimeSpan.FromTicks(runtime))}";
        DetailProgressPanel.Visibility = Visibility.Visible;
        PlayButton.Content = $"▶  继续 {FormatClock(TimeSpan.FromTicks(position))}";
    }

    private void PopulateMetadataSections(EmbyItem item, EmbyItem? parent)
    {
        var studios = GetStudioNames(item.Studios);
        if (studios.Count == 0 && parent is not null) studios = GetStudioNames(parent.Studios);
        DetailStudiosText.Text = string.Join("   ", studios);
        DetailStudiosSection.Visibility = studios.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var locations = item.ProductionLocations is { Count: > 0 } ? item.ProductionLocations : parent?.ProductionLocations;
        DetailLocationsText.Text = locations is { Count: > 0 } ? string.Join("   ", locations) : string.Empty;
        DetailLocationsSection.Visibility = locations is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        var links = (item.ExternalUrls ?? [])
            .Where(link => !string.IsNullOrWhiteSpace(link.Name) && Uri.TryCreate(link.Url, UriKind.Absolute, out _))
            .Select(link => new DetailLinkViewModel(link.Name!, link.Url!))
            .ToList();
        if (links.Count == 0 && parent?.ExternalUrls is { Count: > 0 })
        {
            links = parent.ExternalUrls
                .Where(link => !string.IsNullOrWhiteSpace(link.Name) && Uri.TryCreate(link.Url, UriKind.Absolute, out _))
                .Select(link => new DetailLinkViewModel(link.Name!, link.Url!))
                .ToList();
        }
        DetailLinksItemsControl.ItemsSource = links;
        DetailLinksSection.Visibility = links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var infoRows = BuildDetailInfoRows(item, parent);
        DetailInfoItemsControl.ItemsSource = infoRows;
        DetailInfoSection.Visibility = infoRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<DetailInfoRow> BuildDetailInfoRows(EmbyItem item, EmbyItem? parent)
    {
        var rows = new List<DetailInfoRow>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new DetailInfoRow(label, value));
        }

        Add("媒体 ID", item.Id);
        Add("类型", string.Join(" / ", new[] { item.Type, item.MediaType }.Where(value => !string.IsNullOrWhiteSpace(value))));
        Add("服务器 ID", item.ServerId);
        Add("父级 ID", item.ParentId);
        Add("剧集 ID", item.SeriesId);
        Add("季 ID", item.SeasonId);
        if (item.DateCreated is not null) Add("入库时间", item.DateCreated.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (item.Width is > 0 && item.Height is > 0) Add("源尺寸", $"{item.Width} × {item.Height}");
        Add("容器", item.Container?.ToUpperInvariant());
        if (item.Size is > 0) Add("项目大小", FormatBytes(item.Size));
        if (item.Bitrate is > 0) Add("项目码率", FormatBitrate(item.Bitrate));
        if (item.PartCount is > 0) Add("分段数量", item.PartCount.Value.ToString());
        if (item.LocalTrailerCount is > 0) Add("本地预告", item.LocalTrailerCount.Value.ToString());
        Add("权限", $"下载 {(item.CanDownload ? "允许" : "否")} · 删除 {(item.CanDelete ? "允许" : "否")} · 元数据锁定 {(item.LockData ? "是" : "否")}");
        Add("文件名", item.FileName);
        Add("排序名", item.SortName);
        Add("强制排序名", item.ForcedSortName);
        if (item.ProviderIds is { Count: > 0 }) Add("Provider IDs", string.Join(" · ", item.ProviderIds.Select(pair => $"{pair.Key}: {pair.Value}")));
        Add("DisplayPreferences", item.DisplayPreferencesId);
        Add("Presentation Key", item.PresentationUniqueKey);
        Add("ETag", item.Etag);

        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) && parent is not null)
        {
            if (!string.IsNullOrWhiteSpace(parent.OriginalTitle)) Add("剧集原名", parent.OriginalTitle);
            if (parent.ProviderIds is { Count: > 0 }) Add("剧集 Provider IDs", string.Join(" · ", parent.ProviderIds.Select(pair => $"{pair.Key}: {pair.Value}")));
        }

        return rows;
    }

    private void PopulateChapters(EmbyItem item)
    {
        var chapters = (item.Chapters ?? [])
            .Where(chapter => chapter.StartPositionTicks >= 0)
            .Take(40)
            .Select((chapter, index) => new DetailChapterViewModel(
                string.IsNullOrWhiteSpace(chapter.Name) ? $"章节 {index + 1:00}" : chapter.Name!,
                FormatClock(TimeSpan.FromTicks(chapter.StartPositionTicks)),
                chapter.StartPositionTicks))
            .ToList();
        DetailChaptersItemsControl.ItemsSource = chapters;
        DetailChaptersSection.Visibility = chapters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PopulateMediaSources(List<MediaSourceInfo> sources)
    {
        MediaSourceComboBox.ItemsSource = sources.Select(source => new MediaSourceOption(source)).ToList();
        MediaSourceComboBox.SelectedIndex = sources.Count > 0 ? 0 : -1;
        PlayButton.IsEnabled = sources.Count > 0;
        if (sources.Count == 0)
        {
            DetailVideoText.Text = "服务器尚未返回媒体源。";
            DetailTechnicalText.Text = "播放时会继续请求 PlaybackInfo。";
            DetailAudioItemsControl.ItemsSource = null;
            DetailSubtitleItemsControl.ItemsSource = null;
        }
    }

    private async Task ScheduleDetailPlaybackPrefetchAsync(EmbyItem item, CancellationToken token)
    {
        try
        {
            // Give the detail page and hero image the first render budget. If the user clicks Play
            // immediately, PlayButton_Click simply joins the same in-flight task.
            await Task.Delay(450, token).ConfigureAwait(false);
            await EnsureDetailMediaSourcesAsync(item, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async void PlayButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_detailItem is null || _api is null) return;
        try { await EnsureDetailMediaSourcesAsync(_detailItem, CurrentToken); }
        catch { /* optional prefetch; Play click will surface a real error if needed */ }
    }

    private async Task<PlaybackInfoResponse> GetDetailPlaybackInfoAsync(EmbyItem item, CancellationToken token)
    {
        if (_api is null) throw new InvalidOperationException("Emby 客户端尚未初始化。");

        if (string.Equals(_detailPlaybackInfoItemId, item.Id, StringComparison.Ordinal)
            && _detailPlaybackInfo is not null
            && DateTimeOffset.UtcNow - _detailPlaybackInfoAt < TimeSpan.FromSeconds(90))
            return _detailPlaybackInfo;

        if (string.Equals(_detailPlaybackInfoItemId, item.Id, StringComparison.Ordinal)
            && _detailPlaybackInfoTask is { } existing
            && !existing.IsCanceled && !existing.IsFaulted)
            return await existing.WaitAsync(token).ConfigureAwait(false);

        var resumeTicks = Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0);
        var task = _api.GetPlaybackInfoAsync(item.Id, resumeTicks, token);
        _detailPlaybackInfoItemId = item.Id;
        _detailPlaybackInfoTask = task;
        try
        {
            var info = await task.ConfigureAwait(false);
            if (string.Equals(_detailPlaybackInfoItemId, item.Id, StringComparison.Ordinal))
            {
                _detailPlaybackInfo = info;
                _detailPlaybackInfoAt = DateTimeOffset.UtcNow;
            }
            return info;
        }
        catch
        {
            if (ReferenceEquals(_detailPlaybackInfoTask, task))
            {
                _detailPlaybackInfoTask = null;
                _detailPlaybackInfo = null;
                _detailPlaybackInfoAt = default;
            }
            throw;
        }
    }

    private async Task EnsureDetailMediaSourcesAsync(EmbyItem item, CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var playbackInfo = await GetDetailPlaybackInfoAsync(item, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_detailItem?.Id == item.Id)
                    PopulateMediaSources(playbackInfo.MediaSources.Count > 0 ? playbackInfo.MediaSources : item.MediaSources ?? []);
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // This is speculative work. Do not turn a prefetch failure into a visible page error;
            // PlayButton_Click can retry and present the error only if playback is actually requested.
            LogService.Warning($"PlaybackInfo prefetch failed for {item.Id}.", ex);
        }
    }

    private async Task LoadDetailArtworkAsync(
        EmbyItem item,
        Task<BitmapSource?> quickBackdropTask,
        Task<EmbyItem?> parentTask,
        CancellationToken token)
    {
        var images = _images;
        if (images is null) return;
        try
        {
            BitmapSource? backdrop = null;
            try { backdrop = await quickBackdropTask.ConfigureAwait(false); } catch (OperationCanceledException) { throw; } catch { }
            if (backdrop is null)
                backdrop = await images.GetBackdropAsync(item, 1600, token, ImageRequestPriority.Immediate).ConfigureAwait(false);

            var logoTask = string.IsNullOrWhiteSpace(item.EffectiveLogoTag)
                ? Task.FromResult<BitmapSource?>(null)
                : images.GetAsync(item.EffectiveLogoItemId, "Logo", item.EffectiveLogoTag, 512, token, ImageRequestPriority.Immediate);

            var parent = await parentTask.ConfigureAwait(false);
            if (backdrop is null && parent is not null)
                backdrop = await images.GetBackdropAsync(parent, 1600, token, ImageRequestPriority.Immediate).ConfigureAwait(false);
            backdrop ??= await images.GetPrimaryAsync(item, 1600, token, ImageRequestPriority.Immediate).ConfigureAwait(false);
            if (backdrop is null && !string.IsNullOrWhiteSpace(item.SeriesId))
                backdrop = await images.GetPrimaryByIdAsync(item.SeriesId!, item.SeriesPrimaryImageTag, 1200, token, ImageRequestPriority.Immediate).ConfigureAwait(false);
            if (backdrop is null && parent is not null)
                backdrop = await images.GetPrimaryAsync(parent, 1600, token, ImageRequestPriority.Immediate).ConfigureAwait(false);

            var logo = await logoTask.ConfigureAwait(false);
            if (logo is null && parent is not null && !string.IsNullOrWhiteSpace(parent.EffectiveLogoTag))
                logo = await images.GetAsync(parent.EffectiveLogoItemId, "Logo", parent.EffectiveLogoTag, 512, token, ImageRequestPriority.Immediate).ConfigureAwait(false);

            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                DetailBackdropImage.Source = backdrop;
                DetailLogoImage.Source = logo;
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Warning($"Detail artwork load failed for {item.Id}.", ex); }
    }

    private async Task LoadPeopleSectionAsync(EmbyItem item, Task<EmbyItem?> parentTask, CancellationToken token)
    {
        try
        {
            var parent = await parentTask.ConfigureAwait(false);
            var people = item.People is { Count: > 0 } ? item.People : parent?.People;
            if (people is not { Count: > 0 }) return;

            var cards = people
                .Where(person => !string.IsNullOrWhiteSpace(person.Name))
                .Take(24)
                .Select(person => new DetailPersonViewModel
                {
                    Id = person.Id ?? string.Empty,
                    Name = person.Name!,
                    Role = BuildPersonRole(person),
                    Type = person.Type ?? string.Empty,
                    ImageTag = person.PrimaryImageTag
                })
                .ToList();

            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                _detailPeople = cards;
                DetailPeopleItemsControl.ItemsSource = cards;
                DetailPeopleSection.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }, DispatcherPriority.Background, token);

            await Dispatcher.InvokeAsync(() => ScheduleDetailViewportImages(), DispatcherPriority.Loaded, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Warning($"People section load failed for {item.Id}.", ex); }
    }

    private async Task LoadPersonImageAsync(DetailPersonViewModel person, ImageCacheService images, ImageRequestPriority priority, CancellationToken token)
    {
        try
        {
            var image = await images.GetAsync(person.Id, "Primary", person.ImageTag, 256, token, priority).ConfigureAwait(false);
            if (image is null || token.IsCancellationRequested)
            {
                person.ResetImageLoad();
                return;
            }
            await Dispatcher.InvokeAsync(() => person.Image = image, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { person.ResetImageLoad(); }
        catch (Exception ex)
        {
            person.ResetImageLoad();
            LogService.Warning($"Person image load failed for {person.Id}.", ex);
        }
    }

    private async Task LoadEpisodeShelfAsync(EmbyItem item, CancellationToken token)
    {
        if (_api is null || string.IsNullOrWhiteSpace(item.SeasonId)) return;
        try
        {
            // Use the episode endpoint when SeriesId is available so each item carries
            // its own episode image metadata instead of relying on parent artwork.
            var episodes = !string.IsNullOrWhiteSpace(item.SeriesId)
                ? await _api.GetEpisodesAsync(item.SeriesId!, item.SeasonId, token).ConfigureAwait(false)
                : await _api.GetItemsAsync(item.SeasonId, token).ConfigureAwait(false);
            var cards = CreateCards(episodes.Where(episode => episode.CanPlay).Take(40).ToList());
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                _detailEpisodeCards = cards;
                DetailEpisodesItemsControl.ItemsSource = cards;
                DetailEpisodesTitle.Text = string.IsNullOrWhiteSpace(item.SeasonName) ? "本季剧集" : item.SeasonName!;
                DetailEpisodesSection.Visibility = cards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }, DispatcherPriority.Background, token);
            await Dispatcher.InvokeAsync(() => ScheduleDetailViewportImages(), DispatcherPriority.Loaded, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Warning($"Episode shelf load failed for {item.Id}.", ex); }
    }

    private async Task LoadSimilarShelfAsync(EmbyItem item, CancellationToken token)
    {
        if (_api is null) return;
        try
        {
            var similarId = string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.SeriesId)
                ? item.SeriesId!
                : item.Id;
            var similar = await _api.GetSimilarItemsAsync(similarId, 12, token).ConfigureAwait(false);
            var cards = CreateCards(similar.Where(candidate => candidate.Id != item.Id).Take(12).ToList());
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                _detailSimilarCards = cards;
                DetailSimilarItemsControl.ItemsSource = cards;
                DetailSimilarSection.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }, DispatcherPriority.Background, token);
            await Dispatcher.InvokeAsync(() => ScheduleDetailViewportImages(), DispatcherPriority.Loaded, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Warning($"Similar-items endpoint unavailable for {item.Id}.", ex); }
    }

    private void DetailScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleDetailViewportImages();
    private void DetailEpisodesScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleDetailViewportImages();
    private void DetailPeopleScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleDetailViewportImages();
    private void DetailSimilarScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleDetailViewportImages();

    private void ScheduleDetailViewportImages()
    {
        if (_images is null || DetailPage.Visibility != Visibility.Visible || CurrentToken.IsCancellationRequested) return;
        var token = CurrentToken;

        if (IsDetailSectionNearViewport(DetailEpisodesSection))
            ScheduleDetailCardViewport(_detailEpisodeCards, preferBackdrop: false, DetailEpisodesScroll, 272, token);
        if (IsDetailSectionNearViewport(DetailSimilarSection))
            ScheduleDetailCardViewport(_detailSimilarCards, preferBackdrop: false, DetailSimilarScroll, 162, token);
        if (IsDetailSectionNearViewport(DetailPeopleSection))
            ScheduleDetailPeopleViewport(token);
    }

    private bool IsDetailSectionNearViewport(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible || DetailScroll.ViewportHeight <= 0) return false;
        try
        {
            var point = element.TranslatePoint(new Point(0, 0), DetailScroll);
            var height = Math.Max(120, element.ActualHeight);
            const double margin = 520;
            return point.Y < DetailScroll.ViewportHeight + margin && point.Y + height > -margin;
        }
        catch
        {
            return false;
        }
    }

    private void ScheduleDetailCardViewport(
        IReadOnlyList<MediaCardViewModel> cards,
        bool preferBackdrop,
        ScrollViewer scroller,
        double cellWidth,
        CancellationToken token)
    {
        if (cards.Count == 0 || token.IsCancellationRequested) return;
        var viewport = scroller.ViewportWidth > 1 ? scroller.ViewportWidth : Math.Max(500, ActualWidth - 330);
        var first = Math.Max(0, (int)Math.Floor(scroller.HorizontalOffset / cellWidth) - 1);
        var count = Math.Max(4, (int)Math.Ceiling(viewport / cellWidth) + 3);
        var end = Math.Min(cards.Count, first + count + 4); // one small next-screen warm window
        for (var i = first; i < end; i++)
        {
            var card = cards[i];
            if (!card.TryBeginImageLoad()) continue;
            var priority = i < first + count ? ImageRequestPriority.Visible : ImageRequestPriority.Prefetch;
            _ = LoadCardImageAsync(card, preferBackdrop, priority, token);
        }
    }

    private void ScheduleDetailPeopleViewport(CancellationToken token)
    {
        var images = _images;
        if (images is null || _detailPeople.Count == 0 || token.IsCancellationRequested) return;
        const double cellWidth = 132;
        var viewport = DetailPeopleScroll.ViewportWidth > 1 ? DetailPeopleScroll.ViewportWidth : Math.Max(500, ActualWidth - 330);
        var first = Math.Max(0, (int)Math.Floor(DetailPeopleScroll.HorizontalOffset / cellWidth) - 1);
        var count = Math.Max(5, (int)Math.Ceiling(viewport / cellWidth) + 3);
        var end = Math.Min(_detailPeople.Count, first + count + 4);
        for (var i = first; i < end; i++)
        {
            var person = _detailPeople[i];
            if (string.IsNullOrWhiteSpace(person.Id) || string.IsNullOrWhiteSpace(person.ImageTag) || !person.TryBeginImageLoad()) continue;
            var priority = i < first + count ? ImageRequestPriority.Visible : ImageRequestPriority.Prefetch;
            _ = LoadPersonImageAsync(person, images, priority, token);
        }
    }

    private static bool NeedsParentDetail(EmbyItem item) =>
        string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(item.SeriesId);

    private async Task<EmbyItem?> GetParentDetailAsync(string seriesId, CancellationToken token)
    {
        if (_api is null || string.IsNullOrWhiteSpace(seriesId)) return null;
        if (_parentDetailCache.TryGetValue(seriesId, out var cached)) return cached;

        try
        {
            var parent = await _api.GetItemAsync(seriesId, token).ConfigureAwait(false);
            _parentDetailCache[seriesId] = parent;
            return parent;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Warning($"Parent series detail load failed for {seriesId}.", ex);
            return null;
        }
    }

    private static string BuildDetailTitle(EmbyItem item) =>
        string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.SeriesName)
            ? item.SeriesName!
            : item.DisplayName;

    private static string BuildSecondaryTitle(EmbyItem item)
    {
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            if (item.ParentIndexNumber is > 0) parts.Add($"第 {item.ParentIndexNumber} 季");
            if (item.IndexNumber is > 0) parts.Add($"第 {item.IndexNumber:00} 集");
            if (!string.IsNullOrWhiteSpace(item.Name) && !string.Equals(item.Name, $"第 {item.IndexNumber} 集", StringComparison.Ordinal))
                parts.Add(item.Name);
            return string.Join(" · ", parts);
        }
        return !string.IsNullOrWhiteSpace(item.OriginalTitle) && !string.Equals(item.OriginalTitle, item.Name, StringComparison.OrdinalIgnoreCase)
            ? item.OriginalTitle!
            : string.Empty;
    }

    private static string BuildDetailMeta(EmbyItem item, EmbyItem? parent)
    {
        var parts = new List<string>();
        var premiere = item.PremiereDate ?? parent?.PremiereDate;
        if (premiere is not null) parts.Add(premiere.Value.ToLocalTime().ToString("yyyy年M月d日"));
        else
        {
            var year = item.ProductionYear is > 0 ? item.ProductionYear : parent?.ProductionYear;
            if (year is > 0) parts.Add(year.Value.ToString());
        }

        var rating = item.CommunityRating is > 0 ? item.CommunityRating : parent?.CommunityRating;
        if (rating is > 0) parts.Add($"★ {rating:0.0}");

        if (item.RunTimeTicks is > 0)
            parts.Add(FormatDuration(TimeSpan.FromTicks(item.RunTimeTicks.Value)));

        var video = (item.MediaSources?.FirstOrDefault()?.MediaStreams ?? item.MediaStreams ?? [])
            .FirstOrDefault(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
        if (video?.Height is > 0)
        {
            var resolution = video.Height >= 2160 ? "4K" : video.Height >= 1440 ? "1440p" : video.Height >= 1080 ? "1080p" : $"{video.Height}p";
            if (!string.IsNullOrWhiteSpace(video.VideoRange) && !string.Equals(video.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase))
                resolution += $" {video.VideoRange}";
            else if (!string.IsNullOrWhiteSpace(video.VideoRange)) resolution += " SDR";
            parts.Add(resolution);
        }

        var genres = item.Genres is { Count: > 0 } ? item.Genres : parent?.Genres;
        if (genres is { Count: > 0 }) parts.Add(string.Join("  ", genres.Take(5)));
        return string.Join("   ·   ", parts);
    }

    private static string BuildDetailOverview(EmbyItem item, EmbyItem? parent)
    {
        if (!string.IsNullOrWhiteSpace(item.Overview)) return item.Overview!;
        if (!string.IsNullOrWhiteSpace(parent?.Overview)) return parent!.Overview!;
        return "暂无简介。";
    }

    private static string BuildUserState(EmbyItem item)
    {
        var values = new List<string>();
        if (item.UserData?.IsFavorite == true) values.Add("♥ 已收藏");
        if (item.UserData?.Played == true) values.Add("✓ 已看完");
        else if (item.UserData?.PlaybackPositionTicks is > 0) values.Add("继续观看");
        if (item.UserData?.PlayCount is > 0) values.Add($"播放 {item.UserData.PlayCount} 次");
        if (item.UserData?.LastPlayedDate is not null) values.Add($"上次 {item.UserData.LastPlayedDate.Value.ToLocalTime():M月d日 HH:mm}");
        return values.Count > 0 ? string.Join("   ·   ", values) : "未播放";
    }

    private static string BuildPersonRole(EmbyPerson person)
    {
        if (!string.IsNullOrWhiteSpace(person.Role)) return person.Role!;
        return person.Type switch
        {
            "Director" => "导演",
            "Writer" => "编剧",
            "Producer" => "制片",
            "Actor" => "演员",
            _ => person.Type ?? string.Empty
        };
    }

    private static List<string> GetStudioNames(JsonElement? studios)
    {
        var names = new List<string>();
        if (studios is null) return names;
        var value = studios.Value;
        if (value.ValueKind != JsonValueKind.Array) return names;
        foreach (var studio in value.EnumerateArray())
        {
            if (studio.ValueKind == JsonValueKind.String)
            {
                var name = studio.GetString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            else if (studio.ValueKind == JsonValueKind.Object && studio.TryGetProperty("Name", out var nameElement))
            {
                var name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        return names;
    }

    private static string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}小时{time.Minutes}分" : $"{Math.Max(1, time.Minutes)}分钟";

    private static string FormatClock(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null or <= 0) return "未知";
        var value = (double)bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatBitrate(long? bitsPerSecond)
    {
        if (bitsPerSecond is null or <= 0) return "未知";
        return bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond.Value / 1_000_000d:0.##} Mbps"
            : $"{bitsPerSecond.Value / 1000d:0.#} Kbps";
    }

    private void MediaSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaSourceComboBox.SelectedItem is not MediaSourceOption option)
        {
            DetailAudioItemsControl.ItemsSource = null;
            DetailSubtitleItemsControl.ItemsSource = null;
            return;
        }

        var source = option.Source;
        var video = source.MediaStreams.FirstOrDefault(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
        var audio = source.MediaStreams.Where(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase)).ToList();
        var subtitles = source.MediaStreams.Where(stream => string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)).ToList();

        var videoLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(video?.DisplayTitle)) videoLines.Add(video.DisplayTitle!);
        if (video?.Width is > 0 && video.Height is > 0) videoLines.Add($"{video.Width} × {video.Height}" + (!string.IsNullOrWhiteSpace(video.AspectRatio) ? $"   ·   {video.AspectRatio}" : ""));
        var codec = new List<string>();
        if (!string.IsNullOrWhiteSpace(video?.Codec)) codec.Add(video.Codec!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(video?.Profile)) codec.Add(video.Profile!);
        if (video?.BitDepth is > 0) codec.Add($"{video.BitDepth} bit");
        if (!string.IsNullOrWhiteSpace(video?.PixelFormat)) codec.Add(video.PixelFormat!);
        if (codec.Count > 0) videoLines.Add(string.Join("   ·   ", codec));
        if (video?.AverageFrameRate is > 0) videoLines.Add($"{video.AverageFrameRate:0.###} fps");
        if (!string.IsNullOrWhiteSpace(video?.VideoRange)) videoLines.Add(video.VideoRange!);
        var colorInfo = new[] { video?.ColorPrimaries, video?.ColorSpace, video?.ColorTransfer }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (colorInfo.Count > 0) videoLines.Add("色彩  " + string.Join(" · ", colorInfo));
        if (video?.IsInterlaced == true) videoLines.Add("隔行扫描");
        if (video?.IsAnamorphic == true) videoLines.Add("变形宽银幕");
        if (video?.BitRate is > 0) videoLines.Add(FormatBitrate(video.BitRate));
        DetailVideoText.Text = videoLines.Count > 0 ? string.Join(Environment.NewLine, videoLines) : "未返回视频流详情";

        var technical = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.Name)) technical.Add(source.Name!);
        if (!string.IsNullOrWhiteSpace(source.Container)) technical.Add($"容器  {source.Container!.ToUpperInvariant()}");
        if (source.Size is > 0) technical.Add($"大小  {FormatBytes(source.Size)}");
        if (source.Bitrate is > 0) technical.Add($"总码率  {FormatBitrate(source.Bitrate)}");
        if (!string.IsNullOrWhiteSpace(source.Protocol)) technical.Add($"协议  {source.Protocol}");
        if (!string.IsNullOrWhiteSpace(source.Type)) technical.Add($"源类型  {source.Type}");
        if (source.RunTimeTicks is > 0) technical.Add($"源时长  {FormatDuration(TimeSpan.FromTicks(source.RunTimeTicks.Value))}");
        if (source.DefaultAudioStreamIndex is not null) technical.Add($"默认音轨 Index  {source.DefaultAudioStreamIndex}");
        if (source.DefaultSubtitleStreamIndex is not null) technical.Add($"默认字幕 Index  {source.DefaultSubtitleStreamIndex}");
        technical.Add($"直链追加 API Key  {(source.AddApiKeyToDirectStreamUrl ? "是" : "否")}");
        technical.Add($"直接播放  {(source.SupportsDirectPlay ? "支持" : "不支持")}");
        technical.Add($"直接串流  {(source.SupportsDirectStream ? "支持" : "不支持")}");
        technical.Add($"转码  {(source.SupportsTranscoding ? "支持" : "不支持")}");
        if (_detailItem?.FileName is not null) technical.Add($"文件  {_detailItem.FileName}");
        if (source.Path is not null) technical.Add($"路径  {source.Path}");
        if (_detailItem?.ProviderIds is { Count: > 0 })
            technical.Add("ID  " + string.Join("   ", _detailItem.ProviderIds.Select(pair => $"{pair.Key}:{pair.Value}")));
        DetailTechnicalText.Text = string.Join(Environment.NewLine, technical);

        DetailAudioItemsControl.ItemsSource = audio.Select(stream => new DetailStreamViewModel(
            stream.DisplayTitle ?? stream.Title ?? $"音轨 {stream.Index}",
            BuildAudioSubtitle(stream))).ToList();
        DetailSubtitleItemsControl.ItemsSource = subtitles.Select(stream => new DetailStreamViewModel(
            stream.DisplayTitle ?? stream.Title ?? $"字幕 {stream.Index}",
            BuildSubtitleSubtitle(stream))).ToList();
    }

    private static string BuildAudioSubtitle(MediaStreamInfo stream)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stream.Language)) parts.Add(stream.Language!);
        if (!string.IsNullOrWhiteSpace(stream.Codec)) parts.Add(stream.Codec!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(stream.Profile)) parts.Add(stream.Profile!);
        if (stream.BitDepth is > 0) parts.Add($"{stream.BitDepth} bit");
        if (stream.Channels is > 0) parts.Add(stream.ChannelLayout ?? $"{stream.Channels}ch");
        if (stream.SampleRate is > 0) parts.Add($"{stream.SampleRate / 1000d:0.#} kHz");
        if (stream.BitRate is > 0) parts.Add(FormatBitrate(stream.BitRate));
        if (stream.IsDefault) parts.Add("默认");
        return string.Join(" · ", parts);
    }

    private static string BuildSubtitleSubtitle(MediaStreamInfo stream)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stream.Language)) parts.Add(stream.Language!);
        if (!string.IsNullOrWhiteSpace(stream.Codec)) parts.Add(stream.Codec!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(stream.DeliveryMethod)) parts.Add(stream.DeliveryMethod!);
        if (!string.IsNullOrWhiteSpace(stream.SubtitleLocationType)) parts.Add(stream.SubtitleLocationType!);
        parts.Add(stream.IsTextSubtitleStream ? "文本字幕" : "图形字幕");
        if (stream.SupportsExternalStream) parts.Add("可外置加载");
        if (stream.IsDefault) parts.Add("默认");
        if (stream.IsForced) parts.Add("强制");
        if (stream.IsHearingImpaired) parts.Add("听障");
        return string.Join(" · ", parts);
    }

    private async void DetailRelatedCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaCardViewModel card) return;
        if (card.Item.CanPlay)
        {
            await ShowDetailsAsync(card.Item);
            return;
        }

        if (card.Item.CanBrowse)
        {
            HideDetailSheet();
            await BrowseIntoAsync(card.Item);
        }
    }

    private void ExternalLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowDetailError($"无法打开链接：{ex.Message}");
        }
    }

    private async Task StartPlaybackAsync(EmbyItem item, string? sourceId = null, CancellationToken token = default)
    {
        if (_api is null) return;

        if (_playerWindow is { IsLoaded: true })
        {
            _playerWindow.Activate();
            if (_playerWindow.WindowState == WindowState.Minimized)
                _playerWindow.WindowState = WindowState.Normal;
            return;
        }

        // Hijack-proxy servers publish unscraped movies as "Folder"/"VideoFolder" shells with
        // no MediaSources of their own; PlaybackInfo on them answers 410 Gone. Resolve the
        // shell to the real playable item inside it before negotiating.
        if (!item.CanPlay)
        {
            var resolved = await ResolvePlayableItemAsync(item, token);
            if (resolved is null)
                throw new EmbyApiException("该条目没有可直接播放的视频内容。");
            item = resolved;
            sourceId = null;
        }

        PlaybackPlan plan;
        if (ReferenceEquals(item, _detailItem))
        {
            try
            {
                var playbackInfo = await GetDetailPlaybackInfoAsync(_detailItem, token);
                plan = _api.CreatePlaybackPlan(_detailItem, playbackInfo, sourceId);
            }
            catch (Exception prefetchError) when (prefetchError is not OperationCanceledException)
            {
                LogService.Warning("Prefetched PlaybackInfo could not be reused; retrying negotiation.", prefetchError);
                plan = await _api.CreatePlaybackPlanAsync(item, sourceId, token);
            }
        }
        else
        {
            plan = await _api.CreatePlaybackPlanAsync(item, sourceId, token);
        }

        var player = new PlayerWindow(_api, plan, _settings, SaveSettings)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _playerWindow = player;
        player.Closed += PlayerWindow_Closed;
        player.Show();
    }

    /// <summary>
    /// Resolves a non-playable item (Folder shell / boxset / series) to the first real video
    /// beneath it. Uses the recursive typed query that works on hijack proxies, then a plain
    /// children listing, then the detail-item hydration as last resorts.
    /// </summary>
    private async Task<EmbyItem?> ResolvePlayableItemAsync(EmbyItem item, CancellationToken token)
    {
        if (_api is null) return null;

        // 1. Recursive typed query — the only shape hijack proxies answer for folder shells.
        try
        {
            var found = await _api.GetPlayableChildrenAsync(item.Id, token);
            var pick = found.FirstOrDefault(i => i.CanPlay && i.MediaSources is { Count: > 0 })
                       ?? found.FirstOrDefault(i => i.CanPlay);
            if (pick is not null) return pick;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Warning($"Playable-children lookup failed for {item.Id}.", ex); }

        // 2. Hydrated detail item sometimes carries MediaSources even when the list did not.
        try
        {
            var hydrated = await _api.GetItemAsync(item.Id, token);
            if (hydrated.CanPlay && hydrated.MediaSources is { Count: > 0 }) return hydrated;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Warning($"Detail hydration failed for {item.Id}.", ex); }

        return null;
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _detailItem is null) return;
        PlayButton.IsEnabled = false;
        PlayButton.Content = "正在准备…";
        DetailErrorText.Visibility = Visibility.Collapsed;
        DetailPlayBusyText.Text = "正在获取播放信息…";
        DetailPlayBusyOverlay.Visibility = Visibility.Visible;
        try
        {
            var sourceId = (MediaSourceComboBox.SelectedItem as MediaSourceOption)?.Source.Id;
            DetailPlayBusyText.Text = "正在协商媒体流…";
            await StartPlaybackAsync(_detailItem, sourceId, CurrentToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_playerWindow is { IsLoaded: false } failedPlayer)
            {
                failedPlayer.Closed -= PlayerWindow_Closed;
                _playerWindow = null;
            }
            ShowDetailError(ex.Message);
        }
        finally
        {
            DetailPlayBusyOverlay.Visibility = Visibility.Collapsed;
            PlayButton.Content = "▶  播放";
            if (_detailItem is not null && DetailPage.Visibility == Visibility.Visible)
                PlayButton.IsEnabled = true;
        }
    }

    private void SaveSettings() => _profileStore.Save(_settings);

    private async void PlayerWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is PlayerWindow player)
            player.Closed -= PlayerWindow_Closed;
        var playedItemIds = (sender as PlayerWindow)?.PlayedItemIds?.ToArray()
            ?? (string.IsNullOrWhiteSpace(_detailItem?.Id) ? Array.Empty<string>() : new[] { _detailItem!.Id });
        _playerWindow = null;

        // Player is an owned child window. Closing/backing out should immediately return focus
        // to the existing detail/library window instead of making the user reopen it manually.
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();

        // Progress/UserData are the pieces that really changed. Invalidate only played items and
        // Resume; posters/library structure stay hot in memory and on disk.
        foreach (var playedItemId in playedItemIds)
            _api?.InvalidatePlaybackDerivedMetadata(playedItemId);
        if (AppPanel.Visibility == Visibility.Visible)
            await RefreshResumeSectionAsync();
    }

    private void CloseActivePlayer()
    {
        var player = _playerWindow;
        _playerWindow = null;
        if (player is null) return;
        player.Closed -= PlayerWindow_Closed;
        if (player.IsLoaded) player.Close();
    }

    private void ShowDetailSheet()
    {
        // Detail owns the network budget while visible. Cancelling the previous viewport
        // session prevents off-screen home/library thumbnails from competing with hero data.
        StopHeroRotation();
        SuspendViewportImageSession();
        MainPageContainer.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Visible;
        HideBusy();
    }

    private void HideDetailSheet()
    {
        DetailPage.Visibility = Visibility.Collapsed;
        MainPageContainer.Visibility = Visibility.Visible;
        DetailBackdropImage.Source = null;
        DetailLogoImage.Source = null;
    }

    private void DetailBackButton_Click(object sender, RoutedEventArgs e) => RestoreMainPageAfterDetail();

    private void RestoreMainPageAfterDetail()
    {
        CancelCurrentOperation();
        HideDetailSheet();
        BeginViewportImageSession();
        // If the user returns to a partially loaded home row, make sure visible
        // cards get another chance to enter the viewport queue.
        if (HomeScroll.Visibility == Visibility.Visible)
        {
            StartHeroRotation();
            ScheduleVisibleHomeRowsViewport();
        }
        else
        {
            ScheduleBrowseViewport();
        }
    }


    private void ShowDetailError(string message)
    {
        DetailErrorText.Text = message;
        DetailErrorText.Visibility = Visibility.Visible;
    }

    private void SwitchServerButton_Click(object sender, RoutedEventArgs e)
    {
        CloseActivePlayer();
        ShowServerPanel();
        ReplaceApi(null);
        _activeProfile = null;
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null) return;
        if (MessageBox.Show("退出当前账号并清除本机保存的访问令牌？", "退出账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CloseActivePlayer();
        try { if (_api is not null) await _api.LogoutAsync(); } catch { }
        _profileStore.ClearToken(_activeProfile);
        ReplaceApi(null);
        PrepareProfileLogin(_activeProfile, "已退出，请重新登录。");
    }

    private void ShowMainError(string title, Exception ex)
    {
        LogService.Warning(title, ex);
        ShowErrorToast(title, ex.Message);
    }

    private DispatcherTimer? _errorToastTimer;

    private void ShowErrorToast(string title, string message)
    {
        MainErrorTitle.Text = title;
        MainErrorText.Text = message;
        MainErrorToast.Visibility = Visibility.Visible;

        if (_errorToastTimer is null)
        {
            _errorToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            _errorToastTimer.Tick += (_, _) =>
            {
                _errorToastTimer.Stop();
                MainErrorToast.Visibility = Visibility.Collapsed;
            };
        }
        _errorToastTimer.Stop();
        _errorToastTimer.Start();
    }

    private void MainErrorToast_Click(object sender, MouseButtonEventArgs e)
    {
        _errorToastTimer?.Stop();
        MainErrorToast.Visibility = Visibility.Collapsed;
    }

    private CancellationToken BeginOperation()
    {
        CancelCurrentOperation();
        _operationCts = new CancellationTokenSource();
        return _operationCts.Token;
    }

    private CancellationToken CurrentToken => _operationCts?.Token ?? CancellationToken.None;

    private void CancelCurrentOperation()
    {
        try { _operationCts?.Cancel(); } catch { }
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        try { _busyDelayCts?.Cancel(); } catch { }
        _busyDelayCts?.Dispose();
        var cts = new CancellationTokenSource();
        _busyDelayCts = cts;

        // Cache hits commonly finish within a frame. Delaying the blocking overlay prevents
        // a distracting flash while still giving clear feedback for a real network wait.
        _ = ShowBusyAfterDelayAsync(cts);
    }

    private async Task ShowBusyAfterDelayAsync(CancellationTokenSource owner)
    {
        try
        {
            await Task.Delay(180, owner.Token);
            if (!owner.IsCancellationRequested && ReferenceEquals(_busyDelayCts, owner) && AppPanel.Visibility == Visibility.Visible)
                BusyOverlay.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
    }

    private void HideBusy()
    {
        try { _busyDelayCts?.Cancel(); } catch { }
        _busyDelayCts?.Dispose();
        _busyDelayCts = null;
        BusyOverlay.Visibility = Visibility.Collapsed;
    }

    private void Api_MetadataUpdated(string endpoint)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => _ = ApplyVisibleMetadataRefreshAsync(endpoint)), DispatcherPriority.Background);
    }

    private async Task ApplyVisibleMetadataRefreshAsync(string endpoint)
    {
        if (_api is null || AppPanel.Visibility != Visibility.Visible || HomeScroll.Visibility != Visibility.Visible || DetailPage.Visibility == Visibility.Visible)
            return;

        try
        {
            var token = CurrentToken;
            if (endpoint.Contains("Shows/NextUp", StringComparison.OrdinalIgnoreCase))
                await LoadNextUpHomeSectionAsync(token);
            else if (endpoint.Contains("/Items/Resume", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("Filters=IsResumable", StringComparison.OrdinalIgnoreCase))
                await LoadResumeHomeSectionAsync(token);
            else if (endpoint.Contains("/Items/Latest", StringComparison.OrdinalIgnoreCase) && endpoint.Contains("IncludeItemTypes=Movie", StringComparison.OrdinalIgnoreCase))
                await LoadLatestMoviesHomeSectionAsync(token);
            else if (endpoint.Contains("/Items/Latest", StringComparison.OrdinalIgnoreCase) && endpoint.Contains("IncludeItemTypes=Episode", StringComparison.OrdinalIgnoreCase))
                await LoadLatestEpisodesHomeSectionAsync(token);
            else if (endpoint.Contains("SortBy=CommunityRating", StringComparison.OrdinalIgnoreCase) && endpoint.Contains("MinCommunityRating=7", StringComparison.OrdinalIgnoreCase))
                await LoadTopRatedHomeSectionAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Warning("Unable to apply refreshed cached metadata to the visible home page.", ex); }
    }

    private void ReplaceApi(EmbyApiClient? api)
    {
        var old = _api;
        var oldImages = _images;
        if (old is not null) old.MetadataUpdated -= Api_MetadataUpdated;
        _api = api;
        if (_api is not null) _api.MetadataUpdated += Api_MetadataUpdated;
        _images = null;
        try { _viewportImageCts?.Cancel(); } catch { }
        _viewportImageCts?.Dispose();
        _viewportImageCts = null;
        _detailItem = null;
        _parentDetailCache.Clear();
        StopHeroRotation();
        _heroCandidates.Clear();
        _currentHeroItem = null;
        try { _heroCts?.Cancel(); } catch { }
        _heroCts?.Dispose();
        _heroCts = null;
        HeroPanel.Visibility = Visibility.Collapsed;
        _homeRows.Clear();
        _homeRowsByKey.Clear();
        _homeRowViewports.Clear();
        _browseCards = [];
        _browseVisibleCards.Clear();
        _browseFlatLandscape = false;
        ClearSearchSections();
        _detailEpisodeCards = [];
        _detailSimilarCards = [];
        _detailPeople = [];
        _prefetchedCardIds.Clear();
        _detailPlaybackInfoTask = null;
        _detailPlaybackInfo = null;
        _detailPlaybackInfoItemId = null;
        _detailPlaybackInfoAt = default;
        HideBusy();
        oldImages?.Dispose();
        old?.Dispose();
    }

    private void OpenDebugConsoleButton_Click(object sender, RoutedEventArgs e) => DebugConsoleWindow.ShowFor(this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var navigateBack = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 &&
                           (e.Key == Key.Left || e.SystemKey == Key.Left);
        if (navigateBack)
        {
            if (DetailPage.Visibility == Visibility.Visible)
                RestoreMainPageAfterDetail();
            else if (BrowseScroll.Visibility == Visibility.Visible && BrowseBackButton.Visibility == Visibility.Visible)
                BrowseBackButton_Click(BrowseBackButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (DetailPage.Visibility == Visibility.Visible)
            {
                RestoreMainPageAfterDetail();
                e.Handled = true;
            }
            else if (LoginPanel.Visibility == Visibility.Visible)
            {
                ShowServerPanel();
                e.Handled = true;
            }
            else if (BrowseScroll.Visibility == Visibility.Visible && BrowseBackButton.Visibility == Visibility.Visible)
            {
                BrowseBackButton_Click(BrowseBackButton, new RoutedEventArgs());
                e.Handled = true;
            }
            if (e.Handled) return;
        }

        if (e.Key == Key.F12)
        {
            DebugConsoleWindow.ShowFor(this);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.F || e.Key == Key.K))
        {
            FocusSearchBoxFromAnywhere();
            e.Handled = true;
        }
    }

    private void FocusSearchBoxFromAnywhere()
    {
        if (_api is null || AppPanel.Visibility != Visibility.Visible) return;
        if (DetailPage.Visibility == Visibility.Visible)
            RestoreMainPageAfterDetail();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopHeroRotation();
        CancelCurrentOperation();
        CloseActivePlayer();
        ReplaceApi(null);
    }

    private sealed record BrowseSnapshot(NavigationState State, IReadOnlyList<EmbyItem> Items, double VerticalOffset);

    private sealed class SearchCardSection(string title, List<MediaCardViewModel> cards, bool landscape)
    {
        public string Title { get; } = title;
        public List<MediaCardViewModel> Cards { get; } = cards;
        public bool Landscape { get; } = landscape;
        public ObservableCollection<MediaCardViewModel> VisibleCards { get; } = [];
        public ItemsControl ItemsControl { get; set; } = null!;
    }

    private sealed class SmoothScrollState
    {
        public double Target { get; set; }
        public bool Horizontal { get; set; }
        public bool Active { get; set; }
    }

}
