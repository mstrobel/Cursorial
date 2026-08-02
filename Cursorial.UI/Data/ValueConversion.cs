using System.ComponentModel;
using System.Globalization;

namespace Cursorial.UI.Data;

/// <summary>
/// The no-converter type-conversion ladder (design doc §6.6): exact/assignable → <c>IConvertible</c>
/// / enum-parse fast paths → a <c>TypeConverter</c> fallback (the v1 stand-in for Fork C's
/// <c>XamlConverters.For(targetType)</c>). Used by both the forward pipeline and the reverse lane.
/// </summary>
internal static class ValueConversion
{
    /// <summary>The sentinel meaning "conversion failed" (distinct from a legitimately produced null).</summary>
    public static readonly object Failed = new();

    /// <summary>
    /// Converts <paramref name="value"/> to <paramref name="targetType"/>. Returns
    /// <see cref="Failed"/> when no conversion succeeds (the caller traces and unsets / drops the write).
    /// </summary>
    public static object? Convert(object? value, Type targetType, CultureInfo culture)
    {
        // Null: legal for reference / nullable target types only.
        if (value is null)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
                return null;
            return Failed;
        }

        // Exact / assignable.
        if (targetType.IsInstanceOfType(value))
            return value;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsInstanceOfType(value))
            return value;

        // Enum parse from a string or an integral value.
        if (underlying.IsEnum)
        {
            if (value is string enumText)
                return Enum.TryParse(underlying, enumText, ignoreCase: true, out var parsed) ? parsed : Failed;

            try
            {
                if (value.GetType().IsIntegralType())
                    return Enum.ToObject(underlying, value);
            }
            catch (ArgumentException)
            {
                return Failed;
            }
        }

        // IConvertible fast path (string ⇄ numeric/bool/date).
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(underlying))
        {
            try
            {
                return System.Convert.ChangeType(value, underlying, culture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Fall through to the TypeConverter ladder.
            }
        }

        // TypeConverter ladder (the v1 stand-in for XamlConverters.For).
        var converter = TypeDescriptor.GetConverter(underlying);
        if (value is string sourceText && converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                return converter.ConvertFromString(null, culture, sourceText);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
            {
                return Failed;
            }
        }

        if (converter.CanConvertFrom(value.GetType()))
        {
            try
            {
                return converter.ConvertFrom(null, culture, value);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
            {
                return Failed;
            }
        }

        return Failed;
    }
}
