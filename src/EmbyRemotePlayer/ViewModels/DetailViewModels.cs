namespace EmbyRemotePlayer.ViewModels;

public sealed class DetailPersonViewModel : NotifyObject
{
    private ImageSource? _image;
    private int _imageLoadStarted;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? ImageTag { get; init; }

    public ImageSource? Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    public bool TryBeginImageLoad() => Interlocked.CompareExchange(ref _imageLoadStarted, 1, 0) == 0;
    public void ResetImageLoad() => Interlocked.Exchange(ref _imageLoadStarted, 0);
}

public sealed record DetailChapterViewModel(string Name, string TimeText, long StartPositionTicks);
public sealed record DetailLinkViewModel(string Name, string Url);
public sealed record DetailInfoRow(string Label, string Value);
public sealed record DetailStreamViewModel(string Title, string Subtitle);
