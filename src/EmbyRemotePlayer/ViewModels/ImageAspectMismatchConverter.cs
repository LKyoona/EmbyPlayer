using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EmbyRemotePlayer.ViewModels;

/// <summary>
/// Returns Visible only when the bound image's aspect ratio does NOT match the card tile,
/// so the blurred backdrop fill layer renders solely for mismatched fallback artwork
/// (16:9 backdrops in 2:3 poster tiles, series posters in 16:9 episode tiles).
/// Converter parameter: the tile aspect ratio as a plain invariant double (e.g. 0.695).
/// </summary>
public sealed class ImageAspectMismatchConverter : IValueConverter
{
    public static readonly ImageAspectMismatchConverter Instance = new();

    private const double AspectTolerance = 0.06;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            return Visibility.Collapsed;

        if (!double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tileAspect) ||
            tileAspect <= 0)
            return Visibility.Collapsed;

        var imageAspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        return Math.Abs(imageAspect - tileAspect) > AspectTolerance ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
