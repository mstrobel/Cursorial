namespace Cursorial.Input.Events;

/// <summary>
/// Identifies the kind of <see cref="DeviceResponseEvent"/>. <see cref="Unknown"/> is used
/// when the response was recognized as a query reply but its specific kind could not be
/// classified — consumers may still inspect <see cref="DeviceResponseEvent.Payload"/>.
/// </summary>
/// <remarks>
/// Names mirror the corresponding terminal-protocol identifier (the report name, the OSC
/// number, or the xterm extension name). The enum itself is the "response kind" qualifier;
/// individual members do not repeat <c>Response</c> in their name.
/// </remarks>
public enum DeviceResponseKind
{
    Unknown = 0,

    /// <summary>Response to DA1 (<c>CSI c</c>) — primary device attributes.</summary>
    PrimaryDeviceAttributes,

    /// <summary>Response to DA2 (<c>CSI &gt; c</c>) — secondary device attributes.</summary>
    SecondaryDeviceAttributes,

    /// <summary>Response to DA3 (<c>CSI = c</c>) — tertiary device attributes (DECRPTUI).</summary>
    TertiaryDeviceAttributes,

    /// <summary>Response to DSR-CPR (<c>CSI 6 n</c>) — cursor position report.</summary>
    CursorPositionReport,

    /// <summary>Response to DSR (<c>CSI 5 n</c>) — generic device status.</summary>
    DeviceStatusReport,

    /// <summary>Response to a terminal-name query, when supported.</summary>
    TerminalName,

    /// <summary>Response to a terminal-version query, when supported.</summary>
    TerminalVersion,

    /// <summary>Response to <c>XTVERSION</c> (CSI &gt; q) — terminal program identification.</summary>
    XtVersion,

    /// <summary>Response to OSC 10 (foreground color query).</summary>
    ForegroundColor,

    /// <summary>Response to OSC 11 (background color query).</summary>
    BackgroundColor,

    /// <summary>Response to OSC 12 (cursor color query).</summary>
    CursorColor,

    /// <summary>Response to OSC 4 (palette color query) for one or more palette indices.</summary>
    PaletteColor,

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

    /// <summary>
    /// Response to a DECRQSS (Request Status String) query — <c>DCS &lt;valid&gt; $ r &lt;data&gt; ST</c>.
    /// The interpreter does not surface the leading <c>valid</c> parameter directly; an empty
    /// <see cref="DeviceResponseEvent.Payload"/> indicates the terminal rejected the request
    /// (wire form <c>DCS 0 $ r ST</c>) and a non-empty payload carries the answer bytes. The
    /// wire-level distinction between "rejected" and "honored with empty data" is therefore
    /// not recoverable; in practice DECRQSS responses with valid data are never empty.
    /// </summary>
    DecRqss,

    /// <summary>
    /// Response to an XTGETTCAP (Get Termcap) query — <c>DCS &lt;valid&gt; + r &lt;hexname&gt;=&lt;hexvalue&gt; ST</c>.
    /// As with <see cref="DecRqss"/>, an empty <see cref="DeviceResponseEvent.Payload"/>
    /// indicates the requested termcap entry was not recognized; otherwise the payload carries
    /// the (still hex-encoded) name=value pairs.
    /// </summary>
    XtGetTcap,

    /// <summary>
    /// Response to a DECRQM (Request Mode) query — <c>CSI ? &lt;mode&gt; ; &lt;status&gt; $ y</c>
    /// for DEC private modes (1006, 1004, 2004, 2026, …). The
    /// <see cref="DeviceResponseEvent.Payload"/> carries the raw parameter run
    /// "<c>&lt;mode&gt;;&lt;status&gt;</c>" as ASCII bytes; consumers re-parse to extract the
    /// mode-number and status code (0 = not recognized, 1 = set, 2 = reset, 3 = permanently set,
    /// 4 = permanently reset).
    /// </summary>
    DecRqmPrivate
}