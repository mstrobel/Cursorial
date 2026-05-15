namespace Cursorial.Output;

/// <summary>
/// The full set of SGR-controlled styling applied to a run of text: foreground / background
/// color, attribute flags, underline shape, and (when supported) an independent underline color.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Style"/> is a value type. Equality is component-wise; <c>default(Style)</c>
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
/// hyperlink. The renderer emits hyperlink open/close brackets at run boundaries so adjacent
/// cells with the same hyperlink share one logical link target.
/// </param>
public readonly record struct Style(
    Color Foreground,
    Color Background,
    TextAttributes Attributes,
    UnderlineStyle UnderlineStyle,
    Color UnderlineColor,
    Hyperlink Hyperlink = default)
{
    /// <summary>The "no styling" sentinel — default colors and no attributes.</summary>
    public static Style Default => default;
    
    /// <summary>The default color for text shadows.</summary>
    public static Style DefaultShadow => Default.WithForeground(Color.FromRgba(0, 0, 0, 127));

    /// <summary>Replace the foreground color.</summary>
    public Style WithForeground(Color color) => this with { Foreground = color };

    /// <summary>Replace the background color.</summary>
    public Style WithBackground(Color color) => this with { Background = color };

    /// <summary>Replace the entire attribute set.</summary>
    public Style WithAttributes(TextAttributes attributes) => this with { Attributes = attributes };

    /// <summary>Add the given attribute bits to the existing set.</summary>
    public Style AddAttributes(TextAttributes attributes) => this with { Attributes = Attributes | attributes };

    /// <summary>Clear the given attribute bits from the existing set.</summary>
    public Style RemoveAttributes(TextAttributes attributes) => this with { Attributes = Attributes & ~attributes };

    /// <summary>Set the underline shape (does not toggle the <see cref="TextAttributes.Underline"/> flag).</summary>
    public Style WithUnderlineStyle(UnderlineStyle style) => this with { UnderlineStyle = style };

    /// <summary>Replace the underline color.</summary>
    public Style WithUnderlineColor(Color color) => this with { UnderlineColor = color };

    /// <summary>Replace the hyperlink — pass <see cref="Output.Hyperlink.None"/> (or <c>default</c>) to clear.</summary>
    public Style WithHyperlink(Hyperlink hyperlink) => this with { Hyperlink = hyperlink };

    /// <summary>Convenience: replace the hyperlink with the given URI and optional id.</summary>
    public Style WithHyperlink(string? uri, string? id = null)
        => this with { Hyperlink = new Hyperlink(uri, id) };

    /// <summary>True when no foreground, background, attribute, or hyperlink carries any non-default value.</summary>
    public bool IsDefault => Foreground.IsDefault &&
                             Background.IsDefault &&
                             Attributes == TextAttributes.None &&
                             UnderlineColor.IsDefault &&
                             Hyperlink.IsEmpty;

    public Style BlendOver(in Style backdrop, IBlendingMode? blendingMode = null)
    {
        var mode = blendingMode ?? BlendingModes.Default;

        return this with
               {
                   Foreground = Color.Composite(Foreground, backdrop.Background, mode),
                   Background = Background != Color.Default ? Color.Composite(Background, backdrop.Background, mode) : backdrop.Background,
                   UnderlineColor = Color.Composite(UnderlineColor, backdrop.UnderlineColor, mode)
               };
    }
}