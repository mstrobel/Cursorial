namespace GlowTerm.Core.Input;

/// <summary>
/// Identifies the kind of <see cref="DeviceResponseEvent"/>. <see cref="Unknown"/> is used
/// when the response was recognized as a query reply but its specific kind could not be
/// classified — consumers may still inspect <see cref="DeviceResponseEvent.Payload"/>.
/// </summary>
public enum DeviceResponseKind
{
    Unknown = 0,
    PrimaryDeviceAttributes,
    SecondaryDeviceAttributes,
    TertiaryDeviceAttributes,
    CursorPositionReport,
    DeviceStatusReport,
    TerminalNameQuery,
    TerminalVersionQuery,

    /// <summary>Response to <c>XTVERSION</c> (CSI &gt; q) — terminal program identification.</summary>
    XtVersionResponse,

    /// <summary>Response to OSC 10 (foreground color query).</summary>
    ForegroundColorQuery,

    /// <summary>Response to OSC 11 (background color query).</summary>
    BackgroundColorQuery,

    /// <summary>Response to OSC 12 (cursor color query).</summary>
    CursorColorQuery,

    /// <summary>Response to OSC 4 (palette color query) for one or more palette indices.</summary>
    PaletteColorQuery,

    /// <summary>
    /// Response to <c>CSI 14 t</c> — terminal window size in pixels. Distinct from a SIGWINCH-driven
    /// <see cref="ResizeEvent"/>; this is delivered only when the application explicitly queried.
    /// </summary>
    WindowSizeInPixels,

    /// <summary>
    /// Response to <c>CSI 16 t</c> — single character cell size in pixels. Required by callers
    /// that emit Sixel/Kitty graphics or otherwise need to translate between cell and pixel units.
    /// </summary>
    CellSizeInPixels,
}
