using System.Globalization;

using Cursorial.UI.Xaml;

namespace Cursorial.UI.Data;

public sealed class NullValueConverter : IValueConverter
{
    public object? IfNull { get; set; } = UIProperty.UnsetValue;

    public object? Else { get; set; } = UIProperty.UnsetValue;

    internal Func<Type, ITypeConverter?>? XamlConverter { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var baseResult = value is null ? IfNull : Else;

        if (ReferenceEquals(baseResult, UIProperty.UnsetValue)) return baseResult;
        
        var convertedResult = ValueConversion.Convert(baseResult, targetType, culture);

        if (ReferenceEquals(convertedResult, ValueConversion.Failed))
        {
            if (baseResult is string s && XamlConverter?.Invoke(targetType) is { IsContextFree: true } xamlConverter)
            {
                var context = new XamlValueContext(culture, null, targetType, null, 0, 0);
                return xamlConverter.ConvertFromString(s, in context);
            }
            return UIProperty.UnsetValue;
        }

        return convertedResult;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return UIProperty.UnsetValue;
    }
}