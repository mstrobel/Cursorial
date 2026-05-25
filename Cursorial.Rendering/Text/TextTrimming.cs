using Cursorial.Output.Capabilities;

namespace Cursorial.Rendering.Text;

/// <summary>
/// How <see cref="TextFormatter"/> shortens content that doesn't fit within the per-paragraph
/// width or the document-level row budget. Applies at two layers: a paragraph's own
/// <see cref="TextParagraph.Trim"/> setting governs how an individual paragraph trims its content
/// (most useful with <see cref="WrapMode.NoWrap"/> or <see cref="TextParagraph.MaxLines"/>);
/// the formatter's global <see cref="TextFormatter.Trim"/> governs the last visible line when
/// the document hits the <see cref="TextFormatter.Format(RichText, int, int?, OutputCapabilities?, bool)"/>
/// row budget.
/// </summary>
public enum TextTrimming
{
    /// <summary>No trimming — content past the limit is clipped without an ellipsis.</summary>
    None,

    /// <summary>
    /// Hard clip: content past the limit is dropped silently with no ellipsis. Equivalent to
    /// <see cref="None"/> when there's nothing past the limit; distinct only when paired with a
    /// limit that some content actually exceeds.
    /// </summary>
    ClipFromEnd,

    /// <summary>
    /// Truncate to the nearest grapheme cluster and append <see cref="TextFormatter.Ellipsis"/>
    /// (default <c>"…"</c>). Best for compact UIs where any visible character matters more than
    /// preserving word integrity.
    /// </summary>
    CharacterEllipsis,

    /// <summary>
    /// Truncate to the nearest word boundary and append <see cref="TextFormatter.Ellipsis"/>.
    /// Trailing whitespace before the ellipsis is trimmed. Best for prose where mid-word cuts
    /// look awkward.
    /// </summary>
    WordEllipsis,
}
