namespace Cursorial.Output.Capabilities;

/// <summary>
/// Describes inline-image and bitmap-graphics protocols a terminal accepts.
/// </summary>
/// <param name="Sixel">DEC Sixel raster graphics (CSI &#x3F; 80 h, DCS … q).</param>
/// <param name="KittyGraphics">The Kitty graphics protocol (APC G…).</param>
/// <param name="ITerm2InlineImages">iTerm2 / WezTerm inline images via OSC 1337 ; File=…</param>
public sealed record GraphicsCapabilities(bool Sixel,
                                          bool KittyGraphics,
                                          bool ITerm2InlineImages)
{
    public static GraphicsCapabilities None { get; } = new(Sixel: false,
                                                           KittyGraphics: false,
                                                           ITerm2InlineImages: false);
}
