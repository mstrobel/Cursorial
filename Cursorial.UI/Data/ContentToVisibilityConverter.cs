using System.Globalization;

namespace Cursorial.UI.Data;

public sealed class ContentToVisibilityConverter : IValueConverter
{
    public static ContentToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null or "" || value is string s && string.IsNullOrWhiteSpace(s))
            return Visibility.Collapsed;

        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}