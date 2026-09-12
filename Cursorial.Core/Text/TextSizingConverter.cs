using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Cursorial.Text;

/// <summary>
/// Converts between <see cref="TextSizing"/> and its string form (XAML attribute values, options files,
/// diagnostics). Two spellings parse, mixed freely, case-insensitively, tokens separated by whitespace,
/// <c>:</c> or <c>,</c>:
/// <list type="bullet">
/// <item><b>Named:</b> <c>Normal</c>; <c>Double</c>, <c>Triple</c>, or <c>2x</c>…<c>7x</c> (the scale);
/// <c>Superscript</c> / <c>Subscript</c> (packed half-size, top / bottom), optionally with a fraction —
/// <c>Superscript 1/3</c>; a bare fraction <c>1/2</c> (the fractional scale); <c>Packed</c>; <c>Top</c>,
/// <c>Bottom</c>, <c>Center</c> (vertical) and <c>Left</c>, <c>Right</c>, <c>HCenter</c> (horizontal).</item>
/// <item><b>Metadata:</b> the OSC 66 keys — <c>s=2:n=1:d=2:v=2:h=1:w=1</c>, <c>v</c>/<c>h</c> taking the number
/// or the name (<c>v=bottom</c>), plus <c>packed</c> / <c>p=1</c>.</item>
/// </list>
/// <see cref="TextSizing.ToString"/> writes the canonical metadata form (<c>Normal</c> for the default), which
/// round-trips through <see cref="TextSizing.Parse"/>.
/// </summary>
public sealed class TextSizingConverter : TypeConverter
{
    /// <inheritdoc/>
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc/>
    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
        => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    /// <inheritdoc/>
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
            return TextSizing.Parse(text);

        throw GetConvertFromException(value);
    }

    /// <inheritdoc/>
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is TextSizing sizing && destinationType == typeof(string))
            return sizing.ToString();

        return base.ConvertTo(context, culture, value, destinationType);
    }
}
