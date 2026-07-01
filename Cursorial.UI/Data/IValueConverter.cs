using System.Globalization;

namespace Cursorial.UI.Data;

/// <summary>
/// A bidirectional value converter (design doc §6.1). Return <see cref="UIProperty.UnsetValue"/> to
/// mean "no value" — the pipeline then falls to <c>FallbackValue</c> or <c>SetUnset</c> (BD1). A
/// one-way converter may throw <see cref="NotSupportedException"/> from <see cref="ConvertBack"/>;
/// the engine treats that as a binding error (<c>ConvertBackFailed</c>), not a crash.
/// </summary>
public interface IValueConverter
{
    /// <summary>
    /// Converts the value provided into a target type using the specified parameter and culture.
    /// </summary>
    /// <param name="value">The value to be converted.</param>
    /// <param name="targetType">The type to which the value is being converted.</param>
    /// <param name="parameter">An optional parameter to be used during the conversion.</param>
    /// <param name="culture">The culture to be used during the conversion.</param>
    /// <returns>The converted value, or null if conversion is not possible.</returns>
    object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    /// <summary>
    /// Converts a value back from a target type to the source type.
    /// </summary>
    /// <param name="value">The value that is being converted back to its source type.</param>
    /// <param name="targetType">The type to which the data is being converted back.</param>
    /// <param name="parameter">An optional parameter to use during the conversion process.</param>
    /// <param name="culture">The culture to use during the conversion process.</param>
    /// <return>
    /// Returns the converted value, or a specific representation of an <see cref="UIProperty.UnsetValue">
    /// unset value</see> if no conversion logic is provided or conversion failed.
    /// </return>
    object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture);
}
