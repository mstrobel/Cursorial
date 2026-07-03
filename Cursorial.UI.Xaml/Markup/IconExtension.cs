using Cursorial.UI.Controls;

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

    /// <summary>The emoji / Unicode floor — see <see cref="Icon.Text"/>.</summary>
    public string? Text { get; set; }

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Icon { Glyph = Glyph, GlyphWidth = GlyphWidth, ImageUri = ImageUri, Text = Text };
}
