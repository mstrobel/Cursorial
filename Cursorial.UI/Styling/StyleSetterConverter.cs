using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The seal-time setter-value conversion ladder (style matrix SD9 — the non-XAML converter set;
/// conversion runs exactly once at <see cref="Style.Seal"/>, never at activation). The v1 ladder,
/// in order: <see cref="UIProperty.UnsetValue"/> → valueless entry; the two <b>descriptors</b>
/// (<c>ResourceReference</c> B10, <c>BindingBase</c> B15) pass through unconverted for the frame to
/// resolve per element; <see langword="null"/> for
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

        // A DynamicResource setter (the R2 palette spine, design doc §11.5 / B10): the value is a
        // resolution marker, not the property's value type. It passes through seal-conversion verbatim
        // and the frame resolves it per-element against the owner's resource chain (re-resolving on every
        // variant flip / chain shadowing pulse). The entry installs valueless until the first resolve.
        if (value is ResourceReference)
            return new CompiledSetter(property, value, isUnset: false);

        // A binding-valued setter (B15): like a DynamicResource the value is a descriptor, not the
        // property's value type, so it passes through seal verbatim and the frame installs it per element
        // (StyleRuleFrame.OnInstalled → BindingOperations.Install with the frame as host).
        //
        // This MUST sit above the assignability ladder below. BindingBase is assignable to an
        // object?-typed property — DataContext, ContentPresenter.Content, ButtonBase.CommandParameter —
        // so the IsInstanceOfType early-out would otherwise take the descriptor as the LITERAL value and
        // set the property to a Binding object: no error, no evaluation, just a wrong value. Ordering the
        // check here is what makes "a Binding in a setter is always a binding" true for every property
        // type, which is also the WPF rule.
        if (value is Data.BindingBase)
        {
            // A direct property is not frame-hostable: BindingExpressionCore.Lane tests IsDirect BEFORE
            // the host frame, so the expression takes the DirectProperty lane and PushToDirectProperty
            // writes the value with no entry at all — leaving frame retraction nothing to evict, and the
            // value stranded on the element after the rule stops matching. Refuse at seal, where the
            // (style, rule, property) triple can still be named, rather than shipping a leak (A24).
            if (property.IsDirect)
            {
                throw SealError(style, ruleIndex, property,
                                "a direct property has no store ladder and cannot host a binding-valued setter (ledger A24)");
            }

            return new CompiledSetter(property, value, isUnset: false);
        }

        var targetType = property.PropertyType;
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is null)
        {
            // Null is legal for reference types and Nullable<T> only (SD9).
            if (targetType.IsNullableType() is false)
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
                                                          CultureInfo.InvariantCulture)),
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