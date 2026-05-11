namespace GlowTerm.Core.Output;

/// <summary>
/// Describes which non-color text styling attributes a terminal honors when emitted via SGR
/// or OSC sequences.
/// </summary>
/// <param name="Italic">SGR 3 italic.</param>
/// <param name="Underline">SGR 4 single underline.</param>
/// <param name="ExtendedUnderline">
/// SGR 4:2 (double), 4:3 (curly), 4:4 (dotted), 4:5 (dashed). Independent from
/// <see cref="ColoredUnderline"/>; a terminal may support extended styles without colored
/// underlines or vice versa.
/// </param>
/// <param name="ColoredUnderline">SGR 58 / 59 — colored underlines independent of glyph color.</param>
/// <param name="Strikethrough">SGR 9.</param>
/// <param name="Overline">SGR 53.</param>
/// <param name="Hyperlinks">OSC 8 — hyperlink anchors with URI and optional id.</param>
public sealed record class TextStylingCapabilities(
    bool Italic,
    bool Underline,
    bool ExtendedUnderline,
    bool ColoredUnderline,
    bool Strikethrough,
    bool Overline,
    bool Hyperlinks)
{
    public static TextStylingCapabilities None { get; } = new(
        Italic: false,
        Underline: false,
        ExtendedUnderline: false,
        ColoredUnderline: false,
        Strikethrough: false,
        Overline: false,
        Hyperlinks: false);
}
