namespace Cursorial.Rendering.Text;

/// <summary>
/// Horizontal alignment of a block's content within its available column budget. Applies to
/// <see cref="TextParagraph"/> lines, <see cref="HorizontalRule"/> glyph repetition,
/// <see cref="FigletBlock"/> + <see cref="SizedTextBlock"/> placement, and
/// <see cref="BlockContent"/> content positioning.
/// </summary>
public enum TextAlignment
{
    /// <summary>Content anchors at column 0; trailing space is unfilled.</summary>
    Left,

    /// <summary>Content is centered; equal slack on both sides (excess slack goes to the right).</summary>
    Center,

    /// <summary>Content anchors at the right edge; leading space is unfilled.</summary>
    Right,

    /// <summary>
    /// Distribute extra cells across inter-word gaps so every line — except the last in the
    /// paragraph and any line ending at a <see cref="LineBreak"/> — fills the column budget
    /// exactly. Lines with no inter-word gaps fall back to <see cref="Left"/>. Cell-aware:
    /// mixed CJK + Latin content justifies cleanly.
    /// </summary>
    Justify,
}
