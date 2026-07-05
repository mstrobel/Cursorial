using Cursorial.UI.Controls;

namespace Cursorial.UI.Themes;

/// <summary>
/// An immutable carrier of an <see cref="Icon"/> descriptor that is safe to use as a theme resource.
/// </summary>
public record IconCarrier
{
    public IconCarrier() {}
    
    public IconCarrier(string? glyph = null,
                       int? glyphWidth = null,
                       Uri? imageUri = null,
                       string? emoji = null,
                       string? text = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
        Glyph = glyph;
        GlyphWidth = glyphWidth ?? 1;
        ImageUri = imageUri;
        Emoji = emoji;
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

    public void Deconstruct(out string? glyph, out int glyphWidth, out Uri? imageUri, out string? emoji, out string? text)
    {
        glyph = Glyph;
        glyphWidth = GlyphWidth;
        imageUri = ImageUri;
        emoji = Emoji;
        text = Text;
    }
}