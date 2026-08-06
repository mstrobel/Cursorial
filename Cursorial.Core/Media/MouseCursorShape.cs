using Cursorial.Output;

namespace Cursorial.Media;

/// <summary>
/// Pointer / mouse-cursor shape values understood by Kitty's OSC 22 pointer-shape protocol,
/// mirroring CSS's <c>cursor</c> property. Use with <see cref="MouseCursorWriter"/> to tell the
/// terminal emulator which GUI cursor to render while the pointer is over your application —
/// useful for I-beams over text inputs, hand cursors over clickable elements, resize affordances
/// over draggable borders, etc.
/// </summary>
/// <remarks>
/// <para>
/// Capability gating belongs to the caller — check
/// <see cref="Output.Capabilities.OutputProtocolCapabilities.MouseCursorShape"/> on the negotiated
/// <see cref="Output.Capabilities.OutputCapabilities"/>. Emitting OSC 22 to a non-supporting
/// terminal is benign (OSC strings the terminal doesn't recognize are typically stripped) but
/// the cursor won't change. Today the protocol is honored by Kitty, Ghostty, and Foot.
/// </para>
/// <para>
/// Names map to CSS cursor identifiers verbatim (e.g., <see cref="NotAllowed"/> is sent as
/// <c>not-allowed</c>, <see cref="EwResize"/> as <c>ew-resize</c>). The conversion happens
/// inside <see cref="MouseCursorWriter"/>; consumers shouldn't have to spell CSS strings.
/// </para>
/// </remarks>
public enum MouseCursorShape
{
    /// <summary>The terminal-default pointer (typically an arrow).</summary>
    Default,

    /// <summary>Hide the pointer entirely.</summary>
    None,

    /// <summary>Context-menu indicator (typically arrow with a small menu glyph).</summary>
    ContextMenu,

    /// <summary>Help indicator (typically arrow with a question mark).</summary>
    Help,

    /// <summary>Pointer / hand — used over clickable targets.</summary>
    Pointer,

    /// <summary>Progress indicator while remaining interactive (typically arrow with spinner).</summary>
    Progress,

    /// <summary>Busy / non-interactive wait (typically an hourglass or spinner alone).</summary>
    Wait,

    /// <summary>Spreadsheet-style cell selector.</summary>
    Cell,

    /// <summary>Crosshair, for precise targeting.</summary>
    Crosshair,

    /// <summary>I-beam — text selection / text input.</summary>
    Text,

    /// <summary>I-beam rotated 90°, for vertical text layouts.</summary>
    VerticalText,

    /// <summary>Drag-and-drop alias indicator (arrow with shortcut overlay).</summary>
    Alias,

    /// <summary>Drag-and-drop copy indicator (arrow with plus overlay).</summary>
    Copy,

    /// <summary>Move indicator (four-way arrow on most platforms).</summary>
    Move,

    /// <summary>"No drop" — drag target rejects this drop.</summary>
    NoDrop,

    /// <summary>The operation is not allowed (typically a circle with a slash).</summary>
    NotAllowed,

    /// <summary>Open hand — indicates the element under the pointer is draggable.</summary>
    Grab,

    /// <summary>Closed hand — indicates a drag is in progress.</summary>
    Grabbing,

    /// <summary>East-edge resize.</summary>
    EResize,

    /// <summary>North-edge resize.</summary>
    NResize,

    /// <summary>Northeast-corner resize.</summary>
    NeResize,

    /// <summary>Northwest-corner resize.</summary>
    NwResize,

    /// <summary>South-edge resize.</summary>
    SResize,

    /// <summary>Southeast-corner resize.</summary>
    SeResize,

    /// <summary>Southwest-corner resize.</summary>
    SwResize,

    /// <summary>West-edge resize.</summary>
    WResize,

    /// <summary>Bidirectional east-west resize (horizontal split splitter).</summary>
    EwResize,

    /// <summary>Bidirectional north-south resize (vertical split splitter).</summary>
    NsResize,

    /// <summary>Bidirectional northeast-southwest resize (the "/" diagonal).</summary>
    NeswResize,

    /// <summary>Bidirectional northwest-southeast resize (the "\" diagonal).</summary>
    NwseResize,

    /// <summary>Column resize (between two columns of a table).</summary>
    ColResize,

    /// <summary>Row resize (between two rows of a table).</summary>
    RowResize,

    /// <summary>All-direction scroll indicator (typically a four-way arrow over a dot).</summary>
    AllScroll,

    /// <summary>Zoom-in (typically a magnifying glass with +).</summary>
    ZoomIn,

    /// <summary>Zoom-out (typically a magnifying glass with -).</summary>
    ZoomOut,
}
