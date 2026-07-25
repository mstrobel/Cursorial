using Cursorial.Output.Capabilities;

namespace Cursorial.Output;

/// <summary>
/// Vertical alignment of glyphs within a multicell block under the Kitty text-sizing protocol.
/// Applies when the cell-block is taller than the glyph (e.g. <c>s=2:n=1:d=2</c> renders text
/// at half-height inside a double-tall block).
/// </summary>
public enum TextSizingVerticalAlignment : byte
{
    /// <summary>Anchor at the top of the cell-block (the spec default, <c>v=0</c>).</summary>
    Top = 0,

    /// <summary>Anchor at the bottom (<c>v=1</c>).</summary>
    Bottom = 1,

    /// <summary>Center vertically within the cell-block (<c>v=2</c>).</summary>
    Center = 2,
}

/// <summary>Horizontal alignment of glyphs within a multicell block under the Kitty text-sizing protocol.</summary>
public enum TextSizingHorizontalAlignment : byte
{
    /// <summary>Anchor at the left of the cell-block (the spec default, <c>h=0</c>).</summary>
    Left = 0,

    /// <summary>Anchor at the right (<c>h=1</c>).</summary>
    Right = 1,

    /// <summary>Center horizontally within the cell-block (<c>h=2</c>).</summary>
    Center = 2,
}

/// <summary>
/// Parameters of a Kitty text-sizing OSC 66 emission. Default-constructed instance is "normal
/// text" (no scaling, auto width) and produces an empty metadata block on the wire.
/// </summary>
/// <param name="Scale">
/// Overall scale factor <c>s</c>. Range 1–7 per the spec. Default 1 (no scale). Combined with
/// <see cref="Width"/> the rendered block is <c>Scale × Width</c> cells wide by <c>Scale</c>
/// cells tall.
/// </param>
/// <param name="Width">
/// Width in cells <c>w</c>. Range 0–7; 0 means "auto-calculate from the glyph's natural width."
/// Combine with <see cref="Scale"/> to control the block footprint exactly.
/// </param>
/// <param name="Numerator">
/// Fractional-scale numerator <c>n</c>. Range 0–15. Used with <see cref="Denominator"/> to
/// render text at a fraction of its natural height (e.g. <c>n=1:d=2</c> = half-height inside the
/// <see cref="Scale"/>-tall block).
/// </param>
/// <param name="Denominator">
/// Fractional-scale denominator <c>d</c>. Range 0–15. MUST be greater than <see cref="Numerator"/>
/// when both are non-zero, per the spec.
/// </param>
/// <param name="Vertical">Vertical alignment within the block.</param>
/// <param name="Horizontal">Horizontal alignment within the block.</param>
public readonly record struct TextSizing(
    byte Scale = 1,
    byte Width = 0,
    byte Numerator = 0,
    byte Denominator = 0,
    TextSizingVerticalAlignment Vertical = TextSizingVerticalAlignment.Top,
    TextSizingHorizontalAlignment Horizontal = TextSizingHorizontalAlignment.Left)
{
    /// <summary>Spec defaults — no scaling, auto width, top-left alignment.</summary>
    public static TextSizing Normal => default;

    /// <summary>True when every parameter is at its spec-default and the metadata block would be empty.</summary>
    public bool IsNormal => Scale is 0 or 1 &&
                            Width is 0 &&
                            Numerator is 0 &&
                            Denominator is 0 &&
                            Vertical is TextSizingVerticalAlignment.Top &&
                            Horizontal is TextSizingHorizontalAlignment.Left;
    
    public bool IsSupported(OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // Scale > 1 requires the s-key; Width > 0 requires the w-key. A fragment whose sizing
        // is fully default would emit an empty metadata block, which any terminal will ignore —
        // but a no-op fragment isn't useful, so we still report unsupported in that case so
        // higher-level fallback fires.
        bool needsScale = Scale != 0 && Scale != 1 ||
                          Numerator != 0 ||
                          Denominator != 0;

        bool needsWidth = Width != 0;

        if (needsWidth && !capabilities.TextSizing.Width) return false;
        if (needsScale && !capabilities.TextSizing.Scale) return false;

        // If neither sub-feature is exercised, the fragment renders identically to plain text, and
        // a regular MonospaceFont would be the better choice.
        return needsScale || needsWidth;
    }
}