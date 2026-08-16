using EmbyRemotePlayer.Models;

namespace EmbyRemotePlayer.ViewModels;

public sealed class MediaCardViewModel : NotifyObject
{
    private ImageSource? _image;
    private int _imageLoadState;

    public MediaCardViewModel(EmbyItem item)
    {
        Item = item;
        Title = BuildTitle(item);
        Subtitle = BuildSubtitle(item);
        Badge = BuildBadge(item);
        ProgressPercent = Math.Clamp(item.UserData?.PlayedPercentage ?? 0, 0, 100);
    }

    public EmbyItem Item { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Badge { get; }
    public double ProgressPercent { get; }
    public bool HasProgress => ProgressPercent > 0.5 && ProgressPercent < 99.5;
    public bool IsFavorite => Item.UserData?.IsFavorite == true;
    public bool IsFolder => Item.CanBrowse;

    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (SetProperty(ref _image, value))
            {
                if (value is not null) Interlocked.Exchange(ref _imageLoadState, 2);
                RaisePropertyChanged(nameof(ShowImageLoading));
            }
        }
    }

    private bool _isImageLoading;

    /// <summary>True while a network image load for this card is in flight.</summary>
    public bool IsImageLoading
    {
        get => _isImageLoading;
        private set
        {
            if (SetProperty(ref _isImageLoading, value))
                RaisePropertyChanged(nameof(ShowImageLoading));
        }
    }

    public bool ShowImageLoading => _isImageLoading && _image is null;

    public bool TryBeginImageLoad()
    {
        var began = Interlocked.CompareExchange(ref _imageLoadState, 1, 0) == 0;
        if (began) IsImageLoading = true;
        return began;
    }

    public void CompleteImageLoad(ImageSource? image)
    {
        IsImageLoading = false;
        if (image is not null) Image = image;
        else Interlocked.Exchange(ref _imageLoadState, 2);
    }

    public void ResetImageLoad()
    {
        IsImageLoading = false;
        if (_image is null) Interlocked.Exchange(ref _imageLoadState, 0);
    }

    private static string BuildTitle(EmbyItem item)
    {
        if (item.Type == "Episode" && !string.IsNullOrWhiteSpace(item.SeriesName)) return item.SeriesName;
        return string.IsNullOrWhiteSpace(item.Name) ? "未命名媒体" : item.Name;
    }

    private static string BuildSubtitle(EmbyItem item)
    {
        if (item.Type == "Episode")
        {
            var episode = item.ParentIndexNumber is not null && item.IndexNumber is not null
                ? $"S{item.ParentIndexNumber:00}E{item.IndexNumber:00}"
                : "单集";
            return string.IsNullOrWhiteSpace(item.Name) ? episode : $"{episode} · {item.Name}";
        }

        var parts = new List<string>();
        if (item.ProductionYear is > 0) parts.Add(item.ProductionYear.Value.ToString());
        if (item.RunTimeTicks is > 0)
        {
            var runtime = TimeSpan.FromTicks(item.RunTimeTicks.Value);
            parts.Add(runtime.TotalHours >= 1 ? $"{(int)runtime.TotalHours}小时{runtime.Minutes}分" : $"{runtime.Minutes}分钟");
        }
        if (parts.Count == 0 && item.ChildCount is > 0) parts.Add($"{item.ChildCount} 项");
        return string.Join(" · ", parts);
    }

    private static string BuildBadge(EmbyItem item)
    {
        if (item.UserData?.UnplayedItemCount is > 0) return item.UserData.UnplayedItemCount.Value.ToString();
        return string.Empty;
    }
}
