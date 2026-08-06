using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Text;

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
/// Overall scale factor <c>s</c>. Range 1–7 per the spec. Default 1 (no scale). Text splits
/// into cells as normal text would, each cell an <c>s×s</c> block — a cluster occupies its
/// natural width × <c>s</c> columns by <c>s</c> rows.
/// </param>
/// <param name="Width">
/// Width in cells <c>w</c>. Range 0–7. <b>Unsupported by decision</b> (2026-08-02): per the
/// OSC 66 spec this is the fixed width of the <i>entire sequence</i> (all text renders in
/// <c>s·w × s</c> cells), not a per-cluster advance, and the sub-cell layouts it enables
/// cannot be measured in the whole cells this framework's layout speaks — so the key is never
/// emitted (<see cref="TextSizingWriter"/>) and never measured. The parameter remains for wire
/// round-tripping only.
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

    public static TextSizing Double => new(Scale: 2);

    /// <summary>True when every parameter is at its spec-default and the metadata block would be empty.</summary>
    public bool IsNormal => Scale is 0 or 1 &&
                            Width is 0 &&
                            Numerator is 0 &&
                            Denominator is 0 &&
                            Vertical is TextSizingVerticalAlignment.Top &&
                            Horizontal is TextSizingHorizontalAlignment.Left;

    public string DisplayName => GetDisplayName();

    private string GetDisplayName()
    {
        if (IsNormal) return "Normal";

        if (Numerator > 0 && Denominator > 0)
        {
            if (Scale > 1) return $"Scaled {(Scale - 1) + (decimal)Numerator / Denominator:P0}";
            if (Vertical is TextSizingVerticalAlignment.Top) return $"{(decimal)Numerator/Denominator:P0} Superscript";
            if (Vertical is TextSizingVerticalAlignment.Bottom) return $"{(decimal)Numerator/Denominator:P0} Subscript";
            return $"Scaled {(decimal)Numerator/Denominator:P0}";
        }

        return $"Scaled {Scale:P0}";
    }

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

        if (needsScale && !capabilities.TextSizing.Scale) return false;

        // Width ('w') is unsupported by decision — never emitted, never measured (see
        // TextSizingWriter.WriteMetadata for the full rationale) — so it neither gates on a
        // capability nor makes an otherwise-default sizing worth a fragment. If only scale is
        // left unexercised, the fragment renders identically to plain text, and a regular
        // MonospaceFont would be the better choice.
        return needsScale;
    }

    public (int Columns, int Rows) GetGlyphSize()
    {
        // Cell footprint per the OSC 66 spec: with w=0 ("auto"), text splits into cells as
        // normal text would, and each cell becomes an s×s block — so a cluster occupies
        // (natural width × s, s). Scale=0 is the record-struct default; treat as 1.
        //
        // The FRACTIONAL scale (n/d) deliberately does not participate: per the spec, "the
        // fractional scale does not affect the number of cells the text occupies, instead it
        // just adjusts the rendered font size within those cells" — s=2:n=1:d=2 still occupies
        // 2×2 cells per cluster (half-size glyphs, the spacing makes up the difference).
        // (Maintainer-verified against kitty, 2026-08-02; the previous n/d division here was a
        // misreading that collapsed half-height sizing to a zero footprint.)

        int unitSize = Scale == 0 ? 1 : Scale;

        return (unitSize, unitSize);
    }
}