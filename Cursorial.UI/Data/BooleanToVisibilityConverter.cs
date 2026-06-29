using System.Globalization;

namespace Cursorial.UI.Data;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public static BooleanToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
           {
               Visibility.Visible => true,
               _                  => false
           };
}