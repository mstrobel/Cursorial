namespace Cursorial.Output.Capabilities;

/// <summary>
/// Describes the color fidelity and color-control facilities an output channel supports.
/// </summary>
/// <param name="Depth">The maximum color depth the terminal honors.</param>
/// <param name="TruecolorVerified">
/// True when truecolor support has been empirically verified — typically by setting an OSC
/// palette entry or background color and reading it back via OSC 11/12, confirming the value
/// round-trips intact. <see cref="Depth"/> may be <see cref="ColorDepth.Truecolor"/> while this
/// is false (terminals that advertise truecolor but quantize to 256 colors internally).
/// </param>
/// <param name="DefaultColorReset">
/// True when the terminal honors SGR 39 / 49 (reset to default foreground / background)
/// distinctly from SGR 0 (full reset).
/// </param>
/// <param name="OscPaletteSet">
/// True when the terminal accepts OSC 4 to redefine indexed palette colors, OSC 10 to set the
/// default foreground, and OSC 11 to set the default background.
/// </param>
public sealed record class ColorCapabilities(
    ColorDepth Depth,
    bool TruecolorVerified,
    bool DefaultColorReset,
    bool OscPaletteSet)
{
    public static ColorCapabilities None { get; } = new(
        Depth: ColorDepth.NoColor,
        TruecolorVerified: false,
        DefaultColorReset: false,
        OscPaletteSet: false);
}
