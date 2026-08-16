using System.Globalization;
using System.Windows.Data;

namespace EmbyRemotePlayer.ViewModels;

public sealed class StringVisibilityConverter : IValueConverter
{
    public static StringVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
