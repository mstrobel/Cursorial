using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The seal-time setter-value conversion ladder (style matrix SD9 — the non-XAML converter set;
/// conversion runs exactly once at <see cref="Style.Seal"/>, never at activation). The v1 ladder,
/// in order: <see cref="UIProperty.UnsetValue"/> → valueless entry; <see langword="null"/> for
/// reference/nullable property types; exact/assignable values pass through; enum targets accept
/// member names (ordinal) and integral values; <c>Cursorial.Output.Color</c> targets accept
/// <c>#RGB</c>/<c>#RRGGBB</c> hex strings; <c>IBrush</c> targets accept <c>Color</c> values and hex
/// strings (wrapped in <c>SolidColorBrush</c>); <see cref="IConvertible"/> primitives convert via
/// invariant-culture <see cref="Convert.ChangeType(object?, Type, IFormatProvider?)"/>. Anything
/// else is a seal error naming (style, rule index, property) — the §3.3 triple.
/// </summary>
internal static class StyleSetterConverter
{
    /// <summary>
    /// Converts and validates one setter value. Throws <see cref="InvalidOperationException"/>
    /// (the seal error) when the value cannot represent the property's type.
    /// </summary>
    internal static CompiledSetter Convert(Style style, int ruleIndex, Setter setter)
    {
        var property = setter.Property;

        if (property.IsReadOnly)
        {
            throw SealError(style, ruleIndex, property,
                            "the property is read-only — only its registration key may write it (SD10)");
        }

        var value = setter.Value;

        if (ReferenceEquals(value, UIProperty.UnsetValue))
            return new CompiledSetter(property, value: null, isUnset: true);

        var targetType = property.PropertyType;
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is null)
        {
            // Null is legal for reference types and Nullable<T> only (SD9).
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw SealError(style, ruleIndex, property,
                                $"null is not a legal value for the non-nullable value type '{targetType.Name}'");
            }

            return new CompiledSetter(property, value: null, isUnset: false);
        }

        if (targetType.IsInstanceOfType(value) || underlyingType.IsInstanceOfType(value))
            return new CompiledSetter(property, value, isUnset: false);

        try
        {
            if (underlyingType.IsEnum)
            {
                return new CompiledSetter(
                    property,
                    value switch
                    {
                        string name => Enum.Parse(underlyingType, name),
                        IConvertible => Enum.ToObject(underlyingType,
                                                      System.Convert.ChangeType(
                                                          value,
                                                          Enum.GetUnderlyingType(underlyingType),
                                                          CultureInfo.InvariantCulture)!),
                        _ => throw SealError(style, ruleIndex, property,
                                             $"cannot convert '{value.GetType().Name}' to enum type '{underlyingType.Name}'")
                    },
                    isUnset: false);
            }

            if (underlyingType == typeof(Color) && value is string colorText)
                return new CompiledSetter(property, Color.FromHex(colorText), isUnset: false);

            if (typeof(IBrush).IsAssignableFrom(underlyingType))
            {
                switch (value)
                {
                    case Color color:
                        return new CompiledSetter(property, new SolidColorBrush(color), isUnset: false);

                    case string brushText:
                        return new CompiledSetter(property, new SolidColorBrush(Color.FromHex(brushText)), isUnset: false);
                }
            }

            if (value is IConvertible && (underlyingType.IsPrimitive || underlyingType == typeof(decimal) || underlyingType == typeof(string)))
            {
                return new CompiledSetter(
                    property, System.Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture), isUnset: false);
            }
        }
        catch (Exception conversion) when (conversion is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw SealError(style, ruleIndex, property,
                            $"cannot convert '{value}' ({value.GetType().Name}) to '{targetType.Name}': {conversion.Message}");
        }

        throw SealError(style, ruleIndex, property,
                        $"cannot convert '{value}' ({value.GetType().Name}) to '{targetType.Name}'");
    }

    private static InvalidOperationException SealError(Style style, int ruleIndex, UIProperty property, string reason)
        => new($"Style '{style.IdentityForDiagnostics}': rule {ruleIndex}, setter for property '{property.Name}': {reason}.");
}