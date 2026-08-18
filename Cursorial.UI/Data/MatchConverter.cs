using System.Globalization;

namespace Cursorial.UI.Data;

public sealed class MatchConverter : IValueConverter
{
    /// <summary>The value to match.</summary>
    public object? ValueToMatch { get; set; }

    /// <summary>The conversion result when the input value matches.</summary>
    public object? IfMatched { get; set; }

    /// <summary>The conversion result when the input value does NOT match.</summary>
    public object? IfUnmatched { get; set; }

    /// <summary>The conversion result when the conversion between the input value and <see cref="ValueToMatch"/> fails.</summary>
    public object? FallbackValue { get; set; } = UIProperty.UnsetValue;

    private static object? TryResolveMatch(object? value, object? expected, CultureInfo culture)
    {
        return value switch
               {
                   null                            => null,
                   var o when expected is not null => ValueConversion.Convert(o, expected.GetType(), culture),
                   _                               => null
               };
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var expected = ValueToMatch;
        var conversionResult = TryResolveMatch(value, expected, culture);

        object? baseValue;

        if (conversionResult == ValueConversion.Failed)
            baseValue = FallbackValue;
        else if (Equals(conversionResult, expected))
            baseValue = IfMatched;
        else
            baseValue = IfUnmatched;

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