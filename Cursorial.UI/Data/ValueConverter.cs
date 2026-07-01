using System.Globalization;

namespace Cursorial.UI.Data;

public static class ValueConverter
{
    private sealed class AnonymousValueConverter : IValueConverter
    {
        private readonly ConvertCallback _convert;
        private readonly ConvertBackCallback? _convertBack;

        public AnonymousValueConverter(ConvertCallback convert, ConvertBackCallback? convertBack = null)
        {
            ArgumentNullException.ThrowIfNull(convert);

            _convert = convert;
            _convertBack = convertBack;
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return _convert(value, targetType, parameter, culture);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return _convertBack?.Invoke(value, targetType, parameter, culture) ?? UIProperty.UnsetValue;
        }
    }
}