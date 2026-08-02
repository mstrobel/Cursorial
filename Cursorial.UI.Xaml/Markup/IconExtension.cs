using Cursorial.Drawing.Media;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Xaml.Markup;

/// <summary>
/// The <c>{Icon …}</c> markup extension — the concise way to declare a tiered <see cref="Icon"/> inline as a
/// control's content (e.g. <c>Content="{Icon Glyph=… Text=📁}"</c>), the terse twin of the
/// <c>&lt;Icon …/&gt;</c> element form. <see cref="ProvideValue"/> returns a fresh <see cref="Icon"/> populated
/// from the string/URI tiers; the icon resolves the rendered tier from the terminal's capabilities at runtime
/// (see <see cref="Icon"/>). The raw-bytes <see cref="Icon.Image"/> tier is element/code-only — markup declares
/// the image via <see cref="ImageUri"/>.
/// </summary>
public sealed class IconExtension : MarkupExtension
{
    /// <summary>The Nerd Font glyph tier — see <see cref="Icon.Glyph"/>.</summary>
    public string? Glyph { get; set; }
    
    /// <summary>The Nerd Font glyph width — see <see cref="Icon.GlyphWidth"/>.</summary>
    public int GlyphWidth { get; set; } = 1;

    /// <summary>The image tier as a URI — see <see cref="Icon.ImageUri"/>.</summary>
    public Uri? ImageUri { get; set; }

    /// <summary>The emoji tier — see <see cref="Icon.Emoji"/>.</summary>
    public string? Emoji { get; set; }

    /// <summary>The Unicode floor — see <see cref="Icon.Text"/>.</summary>
    public string? Text { get; set; }

    /// <summary>The foreground brush with which to render the glyph/emoji/text tiers.</summary>
    public IBrush? IconBrush { get; init; }

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt)
        {
            if (pvt is { TargetObject: {} o })
            {
                if (o is IXamlDeferredContentFactory)
                    return this;

                if (o is IDeferredResourceEntry or ResourceDictionary)
                {
                    return new IconCarrier
                           {
                               Glyph = Glyph,
                               GlyphWidth = GlyphWidth,
                               ImageUri = ImageUri,
                               Emoji = Emoji,
                               Text = Text,
                               IconBrush = IconBrush
                           };
                }
            }

            var type = pvt.TargetProperty switch
                       {
                           UIProperty { PropertyType: {} uiType }                     => uiType,
                           XamlMember { ValueType.UnderlyingSystemType: {} xamlType } => xamlType,
                           Type clrType                                               => clrType, // full-lowering passes the CLR target's runtime type directly
                           _                                                          => typeof(object)
                       };

            if (type.IsAssignableFrom(typeof(Icon)) is false &&
                type.IsAssignableFrom(typeof(IconCarrier)) is true)
            {
                return new IconCarrier
                       {
                           Glyph = Glyph,
                           GlyphWidth = GlyphWidth,
                           ImageUri = ImageUri,
                           Emoji = Emoji,
                           Text = Text,
                           IconBrush = IconBrush
                       };
            }
        }

        var icon = new Icon
                   {
                       Glyph = Glyph,
                       GlyphWidth = GlyphWidth,
                       ImageUri = ImageUri,
                       Emoji = Emoji,
                       Text = Text
                   };

        if (IconBrush is {} iconBrush)
            Icon.SetIconBrush(icon, iconBrush);

        return icon;
    }
}