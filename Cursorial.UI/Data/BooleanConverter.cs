using System.Globalization;

using Cursorial.UI.Xaml;

namespace Cursorial.UI.Data;

public sealed class BooleanConverter : ITypeConverter, IValueConverter
{
    /// <summary>The conversion result when the input value is <see langword="true"/>.</summary>
    public object? TrueValue { get; set; }

    /// <summary>The conversion result when the input value is <see langword="false"/>.</summary>
    public object? FalseValue { get; set; }

    /// <summary>The conversion result when the input value not a <see cref="Boolean"/>.</summary>
    public object? FallbackValue { get; set; } = UIProperty.UnsetValue;

    /// <inheritdoc/>
    public bool IsContextFree => true;

    /// <inheritdoc/>
    public object? ConvertFromString(string text, in XamlValueContext context)
    {
        if (bool.TryParse(text, out var input) is false)
            return UIProperty.UnsetValue;

        var value = input ? TrueValue : FalseValue;
        var result = ValueConversion.Convert(value, context.TargetType, context.Culture);

        if (ReferenceEquals(result, ValueConversion.Failed))
            return UIProperty.UnsetValue;
        
        return result;
    }

    private static bool? TryResolveBoolean(object? value, CultureInfo culture)
    {
        return value switch
               {
                   bool b => b,
                   string s when bool.TryParse(s, out var r) => r,
                   object o => ValueConversion.Convert(o, typeof(bool), culture) as bool?,
                   _ => null
               };
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var boolValue = TryResolveBoolean(value, culture);

        var baseValue = boolValue switch
                        {
                            true  => TrueValue,
                            false => FalseValue,
                            _     => FallbackValue
                        };

        var result = ValueConversion.Convert(baseValue, targetType, culture);

        if (result == ValueConversion.Failed)
            return UIProperty.UnsetValue;

        return result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}