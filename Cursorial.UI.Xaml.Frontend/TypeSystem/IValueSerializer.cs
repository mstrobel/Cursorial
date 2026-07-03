// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Serializes a value to and from a string — the XAML <c>ValueSerializer</c> contract a type/member declares
/// via <c>Cursorial.Markup.[ValueSerializer]</c>. The <see cref="ConvertFromString"/> (deserialize) leg is used
/// at LOAD and, following WPF's <c>ValueSerializer.GetSerializerFor</c> precedence, is preferred over an
/// <see cref="ITypeConverter"/> when both are declared. <see cref="ConvertToString"/> (serialize) is the
/// save-side leg, present for contract completeness (Cursorial has no XAML save path yet, so it is unused).
/// </summary>
public interface IValueSerializer
{
    /// <summary>
    /// Deserializes <paramref name="text"/> to a value. Throws <see cref="XamlParseException"/> (a
    /// <c>CUR2401</c>-class diagnostic) on a malformed value. The load-side leg.
    /// </summary>
    object? ConvertFromString(string text, in XamlValueContext context);

    /// <summary>Serializes <paramref name="value"/> to its string form (the save-side leg; unused until a save path lands).</summary>
    string ConvertToString(object? value, in XamlValueContext context);

    /// <summary>
    /// True when deserialization depends only on the input text (and culture) — the constant-folding eligibility
    /// (matrix XD3), mirroring <see cref="ITypeConverter.IsContextFree"/>.
    /// </summary>
    bool IsContextFree { get; }
}
