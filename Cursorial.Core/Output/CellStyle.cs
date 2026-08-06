using Cursorial.Media;
using Cursorial.Text;

namespace Cursorial.Output;

/// <summary>
/// The full set of SGR-controlled styling applied to a run of text: foreground / background
/// color, attribute flags, underline shape, and (when supported) an independent underline color.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CellStyle"/> is a value type. Equality is component-wise; <c>default(Style)</c>
/// describes "no styling" — both colors are <see cref="Color.Default"/>, no attributes set.
/// </para>
/// <para>
/// Use the <c>With…</c> methods for fluent composition:
/// <c>Style.Default.WithForeground(Color.FromPalette(1)).WithAttributes(TextAttributes.Bold)</c>.
/// They allocate nothing — each returns a new value with the requested fields replaced.
/// </para>
/// <para>
/// <see cref="UnderlineColor"/> applies only when <see cref="TextAttributes.Underline"/> is set
/// AND the terminal advertises <c>TextStylingCapabilities.ColoredUnderline</c>. <see cref="Color.Default"/>
/// means "use the foreground color for the underline too" — the same default the terminal
/// follows when SGR 59 is emitted.
/// </para>
/// </remarks>
/// <param name="Foreground">Glyph color. Defaults to <see cref="Color.Default"/>.</param>
/// <param name="Background">Background color behind the glyph. Defaults to <see cref="Color.Default"/>.</param>
/// <param name="Attributes">Bitset of independently toggleable attributes (bold, italic, …).</param>
/// <param name="UnderlineStyle">Shape of the underline; only emitted when <see cref="TextAttributes.Underline"/> is set.</param>
/// <param name="UnderlineColor">
/// Color of the underline (SGR 58). Only emitted when the colored-underline capability is
/// available; <see cref="Color.Default"/> means "follow the foreground."
/// </param>
/// <param name="Hyperlink">
/// OSC 8 hyperlink anchor for this cell. <see cref="Hyperlink.None"/> (the default) means no
/// hyperlink. The renderer emits hyperlink open/close brackets at run boundaries, so adjacent
/// cells with the same hyperlink share one logical link target.
/// </param>
public readonly record struct CellStyle(
    Color Foreground,
    Color Background,
    TextAttributes Attributes,
    UnderlineStyle UnderlineStyle,
    Color UnderlineColor,
    Hyperlink Hyperlink = default)
{
    /// <summary>The "no styling" sentinel — default colors and no attributes.</summary>
    public static CellStyle Default => default;

    /// <summary>
    /// The compositing identity: foreground, background, and underline color are all
    /// <see cref="Color.Transparent"/>. A cell carrying this style contributes no color when
    /// composited via <see cref="Color.Composite"/> (which short-circuits a transparent source to
    /// the backdrop), so it is the value a drawing-layer scene buffer is cleared to — unpainted
    /// cells then leave the composite target untouched. Distinct from <see cref="Default"/>, which
    /// paints the terminal's default colors <em>opaquely</em>. <see cref="Hyperlink"/> is left at
    /// its default (<see cref="Output.Hyperlink.None"/>).
    /// </summary>
    public static CellStyle Transparent { get; } = Default.WithForeground(Color.Transparent)
                                                      .WithBackground(Color.Transparent)
                                                      .WithUnderlineColor(Color.Transparent);

    /// <summary>The default style for text shadows.</summary>
    public static CellStyle DefaultShadow { get; } = Default.WithForeground(Color.FromRgba(0, 0, 0, 127))
                                                        .WithUnderlineColor(Color.FromRgba(0, 0, 0, 127))
                                                        .WithBackground(Color.Transparent);

    /// <summary>Replace the foreground color.</summary>
    public CellStyle WithForeground(Color color) => this with { Foreground = color };

    /// <summary>Replace the background color.</summary>
    public CellStyle WithBackground(Color color) => this with { Background = color };

    /// <summary>Replace the entire attribute set.</summary>
    public CellStyle WithAttributes(TextAttributes attributes) => this with { Attributes = attributes };

    /// <summary>Add the given attribute bits to the existing set.</summary>
    public CellStyle AddAttributes(TextAttributes attributes) => this with { Attributes = Attributes | attributes };

    /// <summary>Clear the given attribute bits from the existing set.</summary>
    public CellStyle RemoveAttributes(TextAttributes attributes) => this with { Attributes = Attributes & ~attributes };

    /// <summary>Set the underline shape (does not toggle the <see cref="TextAttributes.Underline"/> flag).</summary>
    public CellStyle WithUnderlineStyle(UnderlineStyle style) => this with { UnderlineStyle = style };

    /// <summary>Replace the underline color.</summary>
    public CellStyle WithUnderlineColor(Color color) => this with { UnderlineColor = color };

    /// <summary>Replace the hyperlink — pass <see cref="Output.Hyperlink.None"/> (or <c>default</c>) to clear.</summary>
    public CellStyle WithHyperlink(Hyperlink hyperlink) => this with { Hyperlink = hyperlink };

    /// <summary>Convenience: replace the hyperlink with the given URI and optional id.</summary>
    public CellStyle WithHyperlink(string? uri, string? id = null)
        => this with { Hyperlink = new Hyperlink(uri, id) };

    /// <summary>True when no foreground, background, attribute, or hyperlink carries any non-default value.</summary>
    public bool IsDefault => this == default;

    // return Foreground.IsDefault &&
    //        Background.IsDefault &&
    //        Attributes == TextAttributes.None &&
    //        UnderlineStyle == default &&
    //        UnderlineColor.IsDefault &&
    //        Hyperlink.IsEmpty;
    public CellStyle BlendOver(in CellStyle backdrop, IBlendingMode? blendingMode = null)
    {
        var mode = blendingMode ?? BlendingModes.Default;

        return this with
               {
                   Foreground = Color.Composite(Foreground, backdrop.Background, mode),
                   Background = Background != Color.Default
                                    ? Color.Composite(Background, backdrop.Background, mode)
                                    : backdrop.Background,
                   UnderlineColor = UnderlineColor != Color.Default || Attributes.HasFlag(TextAttributes.Underline)
                                        ? Color.Composite(UnderlineColor, backdrop.Background, mode)
                                        : backdrop.UnderlineColor
               };
    }
}