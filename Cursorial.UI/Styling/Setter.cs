// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One property assignment inside a <see cref="Style"/> (design doc §3.1). At P3 the value
/// vocabulary is <b>constants and <see cref="UIProperty.UnsetValue"/></b> (style matrix SD9):
/// constants are validated and converted against the property exactly once at
/// <see cref="Style.Seal"/> (exact/assignable values pass through; <see cref="IConvertible"/>
/// primitives convert with the invariant culture; enum names and integral values convert to enum
/// types; <c>#hex</c> strings convert to <c>Color</c>, and <c>Color</c>/<c>#hex</c> values to
/// solid brushes); an <see cref="UIProperty.UnsetValue"/> setter compiles to a valueless entry that
/// contributes nothing while the rule is active. Resource references and bindings join the
/// vocabulary at P5/P4.
/// </summary>
[Cursorial.Markup.ContentProperty("Value")]
public sealed class Setter
{
    /// <summary>Creates a setter for <paramref name="property"/> with the raw (unconverted) <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="property"/> is null.</exception>
    public Setter(UIProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property;
        Value = value;
    }

    /// <summary>The target property.</summary>
    public UIProperty Property { get; }

    /// <summary>The raw setter value as authored; conversion happens once at seal (SD9).</summary>
    public object? Value { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Property.Name} = {Value ?? "(null)"}";
}