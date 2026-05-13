using Cursorial.Output.Capabilities;

namespace Cursorial.Output;

/// <summary>
/// Independently toggleable text attributes a terminal applies via SGR commands. Combine with
/// bitwise OR. The presence of <see cref="Underline"/> here is a boolean — the shape of the
/// underline (single, double, curly, dotted, dashed) is the separate <see cref="UnderlineStyle"/>
/// enum, because those forms don't combine and can't fit in a flags layout.
/// </summary>
[Flags]
public enum TextAttributes : ushort
{
    None = 0,

    /// <summary>SGR 1.</summary>
    Bold = 1 << 0,

    /// <summary>SGR 2 — "faint" / dim.</summary>
    Faint = 1 << 1,

    /// <summary>SGR 3.</summary>
    Italic = 1 << 2,

    /// <summary>
    /// SGR 4 with shape determined by the accompanying <see cref="UnderlineStyle"/>. When this
    /// flag is clear the underline style is ignored.
    /// </summary>
    Underline = 1 << 3,

    /// <summary>SGR 5 — slow blink.</summary>
    Blink = 1 << 4,

    /// <summary>SGR 7 — swap foreground and background.</summary>
    Inverse = 1 << 5,

    /// <summary>SGR 8 — render in the background color (the glyph still occupies cells).</summary>
    Hidden = 1 << 6,

    /// <summary>SGR 9.</summary>
    Strikethrough = 1 << 7,

    /// <summary>SGR 53.</summary>
    Overline = 1 << 8
}

/// <summary>
/// Shape of an underline when <see cref="TextAttributes.Underline"/> is set. Mapped to SGR
/// <c>4:n</c> for the extended forms — terminals advertising
/// <see cref="TextStylingCapabilities.ExtendedUnderline"/> honor the four non-Single shapes;
/// others fall back to <see cref="Single"/> when emitted.
/// </summary>
public enum UnderlineStyle : byte
{
    /// <summary>SGR 4 — a single line under the glyph row.</summary>
    Single = 0,

    /// <summary>SGR 4:2 — two parallel lines.</summary>
    Double = 1,

    /// <summary>SGR 4:3 — wavy underline; used by editors to mark spelling errors.</summary>
    Curly = 2,

    /// <summary>SGR 4:4 — dotted underline.</summary>
    Dotted = 3,

    /// <summary>SGR 4:5 — dashed underline.</summary>
    Dashed = 4
}