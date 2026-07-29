using Cursorial.UI.Xaml;

namespace Cursorial.UI.Data;

public sealed class BooleanConverter : ITypeConverter
{
    /// <summary>The conversion result when the input value is <see langword="true"/>.</summary>
    public object? TrueValue { get; set; }

    /// <summary>The conversion result when the input value is <see langword="false"/>.</summary>
    public object? FalseValue { get; set; }

    /// <inheritdoc/>
    public bool IsContextFree => false;

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
}