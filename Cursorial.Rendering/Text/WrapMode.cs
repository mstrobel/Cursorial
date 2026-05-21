namespace Cursorial.Rendering.Text;

/// <summary>
/// How <see cref="TextFormatter"/> handles paragraph content that exceeds the available column
/// budget. Mirrors WPF's <c>TextWrapping</c> with an added <see cref="CharacterWrap"/> option
/// for CJK-dense or preformatted-data scenarios where word semantics don't apply.
/// </summary>
public enum WrapMode
{
    /// <summary>
    /// Single line; content past the right edge is clipped (or truncated when the paragraph's
    /// <see cref="TextTrimming"/> mode requests an ellipsis). The wrap loop never advances to a
    /// new row, regardless of break opportunities in the text.
    /// </summary>
    NoWrap,

    /// <summary>
    /// Break at word boundaries (ASCII whitespace + Unicode line-break opportunities). Words
    /// longer than the line are broken at any character to fit. Default behavior; matches
    /// WPF's <c>Wrap</c>.
    /// </summary>
    WordWrap,

    /// <summary>
    /// Break at word boundaries; words longer than the line are placed on their own row and
    /// allowed to overflow past the right edge. Useful when the column budget is unreliable
    /// (e.g., URLs in chat output) and clipping or mid-word breaks would lose information.
    /// Matches WPF's <c>WrapWithOverflow</c>.
    /// </summary>
    WordWrapOverflow,

    /// <summary>
    /// Break at any grapheme-cluster boundary. No word semantics — every character is a wrap
    /// opportunity. The natural mode for CJK text, dense numeric tables, or any input where
    /// "words" aren't meaningful.
    /// </summary>
    CharacterWrap,
}
