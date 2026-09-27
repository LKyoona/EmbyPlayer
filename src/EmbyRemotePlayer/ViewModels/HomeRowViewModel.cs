using System.Collections.ObjectModel;

namespace EmbyRemotePlayer.ViewModels;

/// <summary>
/// One dynamic content row on the home page (接着看 / 继续观看 / 最新电影 / 为你推荐 …).
/// Rows are virtual: the home engine shows a row only when its data arrived non-empty.
/// </summary>
public sealed class HomeRowViewModel(string key, int rank, string title, string subtitle, string badgeText, bool isLandscape) : NotifyObject
{
    /// <summary>Stable identity used to update the row across refreshes (e.g. "nextup", "resume").</summary>
    public string Key { get; } = key;

    /// <summary>Deterministic sorting order on the home page, regardless of async arrival timing.</summary>
    public int Rank { get; } = rank;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    public string BadgeText { get; } = badgeText;

    /// <summary>Landscape rows use 16:9 still cards; poster rows use 2:3 poster cards.</summary>
    public bool IsLandscape { get; } = isLandscape;

    /// <summary>
    /// Footprint width in DIPs (Card Width + MarginRight) matching MainWindow.xaml:
    /// Landscape: 380 + 18 = 398, Poster: 190 + 18 = 208.
    /// </summary>
    public double CellWidth => IsLandscape ? 398 : 208;

    public bool PreferBackdrop => IsLandscape;

    public ObservableCollection<MediaCardViewModel> Cards { get; } = [];
}

/// <summary>Chooses the row chrome whose inner card template matches the row orientation.</summary>
public sealed class HomeRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PosterRowTemplate { get; set; }
    public DataTemplate? LandscapeRowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is HomeRowViewModel { IsLandscape: true })
            return LandscapeRowTemplate ?? PosterRowTemplate;
        return PosterRowTemplate;
    }
}
