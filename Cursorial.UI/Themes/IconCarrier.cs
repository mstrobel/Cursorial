using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Themes;

/// <summary>
/// An immutable carrier of an <see cref="Icon"/> descriptor that is safe to use as a theme resource.
/// </summary>
[TypeConverter(typeof(IconCarrierConverter))]
public record IconCarrier
{
    public IconCarrier() {}
    
    public IconCarrier(string? glyph = null,
                       int? glyphWidth = null,
                       Uri? imageUri = null,
                       string? emoji = null,
                       string? text = null,
                       IBrush? iconBrush = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
        Glyph = glyph;
        GlyphWidth = glyphWidth ?? 1;
        ImageUri = imageUri;
        Emoji = emoji;
        IconBrush =  iconBrush;
    }

    /// <inheritdoc cref="Icon.Glyph"/>
    public string? Glyph { get; init; }
    
    /// <inheritdoc cref="Icon.GlyphWidth"/>
    public int GlyphWidth { get; init; }

    /// <inheritdoc cref="Icon.ImageUri"/>
    public Uri? ImageUri { get; init; }

    /// <inheritdoc cref="Icon.Emoji"/>
    public string? Emoji { get; init; }

    /// <inheritdoc cref="Icon.Text"/>
    public string? Text { get; init; }

    /// <inheritdoc cref="Icon.IconBrush"/>
    public IBrush? IconBrush { get; init; }

    public void Deconstruct(out string? glyph, out int glyphWidth, out Uri? imageUri, out string? emoji, out string? text, out IBrush? iconBrush)
    {
        glyph = Glyph;
        glyphWidth = GlyphWidth;
        imageUri = ImageUri;
        emoji = Emoji;
        text = Text;
        iconBrush = IconBrush;
    }
}

public sealed class IconCarrierConverter : TypeConverter
{
    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
    {
        return typeof(UIElement).IsAssignableFrom(destinationType) ||
               base.CanConvertTo(context, destinationType);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is IconCarrier c && destinationType.IsAssignableFrom(typeof(Icon)))
        {
            var icon = new Icon
                            {
                                Glyph = c.Glyph,
                                GlyphWidth = c.GlyphWidth,
                                ImageUri = c.ImageUri,
                                Emoji = c.Emoji,
                                Text = c.Text
                            };

            if (c.IconBrush is {} iconBrush)
                Icon.SetIconBrush(icon, iconBrush);

            return icon;
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }
}