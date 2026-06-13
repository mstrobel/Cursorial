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
    /// <summary>Source → target. <paramref name="targetType"/> is the target property's CLR type.</summary>
    object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    /// <summary>Target → source. <paramref name="targetType"/> is the source leaf's CLR type.</summary>
    object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture);
}
