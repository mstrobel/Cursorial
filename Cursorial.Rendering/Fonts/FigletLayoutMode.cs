namespace Cursorial.Rendering.Fonts;

/// <summary>
/// FIGlet 2a+ horizontal layout rules — the <c>fullLayout</c> header field. Controls how
/// adjacent glyphs are combined: pure spacing (none of the bits set), kerning (move closer
/// without overlap), or full smushing (overlap by one cell when the boundary characters
/// satisfy one of the smushing rules).
/// </summary>
/// <remarks>
/// The bit positions match the FIGlet spec verbatim — the parser can cast the integer value
/// from the header straight into this enum. Vertical-layout bits (rules 7–14 in the spec) are
/// intentionally omitted; Cursorial only paints horizontal text and a vertical-smushing
/// implementation would belong on a future renderer-side helper, not this enum.
/// </remarks>
[Flags]
public enum FigletLayoutMode
{
    /// <summary>No rules selected — characters render at full width with no kerning or smushing.</summary>
    None = 0,

    /// <summary>Rule 1: two identical characters smush into one.</summary>
    Equal = 0b0000_0001,

    /// <summary>Rule 2: underscore can be smushed under one of <c>| / \ [ ] { } ( ) &lt; &gt;</c>.</summary>
    Lowline = 0b0000_0010,

    /// <summary>Rule 3: hierarchy — bracket-class characters smush over higher-rank neighbours.</summary>
    Hierarchy = 0b0000_0100,

    /// <summary>Rule 4: opposite-bracket pairs smush into <c>|</c>.</summary>
    Pair = 0b0000_1000,

    /// <summary>Rule 5: <c>/\</c> becomes <c>|</c>, <c>\/</c> becomes <c>Y</c>, <c>&gt;&lt;</c> becomes <c>X</c>.</summary>
    BigX = 0b0001_0000,

    /// <summary>Rule 6: a hardblank can smush with anything (resolves to the non-hardblank).</summary>
    Hardblank = 0b0010_0000,

    /// <summary>Horizontal kerning enabled — characters move together but never overlap.</summary>
    Kern = 0b0100_0000,

    /// <summary>Horizontal smushing enabled — when set, the rule bits above are honored at the boundary.</summary>
    Smush = 0b1000_0000
}

/// <summary>
/// Legacy FIGlet layout field (the <c>oldLayout</c> header parameter, pre-2a). When a font's
/// <see cref="FigletLayoutMode"/> field is absent or negative, the parser derives the modern
/// layout from these bits.
/// </summary>
[Flags]
public enum FigletLegacyLayoutMode
{
    /// <summary>No legacy layout — full-width spacing.</summary>
    None = 0,

    /// <summary>Horizontal fitting: equal-character smushing only.</summary>
    HorizontalFitting = 0b0001,

    /// <summary>Horizontal kerning: characters move together but never overlap.</summary>
    HorizontalKerning = 0b0010,

    /// <summary>Vertical fitting (unused — Cursorial does not render vertical-smushed FIGlet).</summary>
    VerticalFitting = 0b0100,

    /// <summary>Vertical kerning (unused — Cursorial does not render vertical-smushed FIGlet).</summary>
    VerticalKerning = 0b1000
}
