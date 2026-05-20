namespace Cursorial.Output.Capabilities;

/// <summary>
/// Describes window- and surface-level facilities a terminal exposes — distinct from
/// glyph-level styling.
/// </summary>
/// <param name="TitleSet">OSC 0 / 2 — set the terminal window title.</param>
/// <param name="IconSet">OSC 1 — set the terminal icon name (X11 heritage; rare in modern terminals).</param>
/// <param name="SizeQueryInPixels">CSI 14 t / 16 t — window and cell size in pixels.</param>
/// <param name="AlternateScreenBuffer">DECSET 1049 — switch to and from the alternate screen.</param>
/// <param name="ScrollRegion">DECSTBM — set the vertical scrolling region.</param>
public sealed record WindowCapabilities(bool TitleSet,
                                        bool IconSet,
                                        bool SizeQueryInPixels,
                                        bool AlternateScreenBuffer,
                                        bool ScrollRegion,
                                        int? CellPixelWidth,
                                        int? CellPixelHeight)
{
    public static WindowCapabilities None { get; } = new(TitleSet: false,
                                                         IconSet: false,
                                                         SizeQueryInPixels: false,
                                                         AlternateScreenBuffer: false,
                                                         ScrollRegion: false,
                                                         CellPixelHeight: null,
                                                         CellPixelWidth: null);
}
