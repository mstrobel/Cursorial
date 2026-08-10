using Cursorial.Rendering.Content;
using Cursorial.Rendering.Media;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Base type for the contents of a <see cref="TextParagraph"/>. Inlines are the leaves of a
/// rich-text document: styled text runs, hard line breaks, and embedded single-row
/// <see cref="IContent"/> instances. The formatter walks the inline sequence to perform
/// wrapping, measurement, alignment, and trimming.
/// </summary>
/// <remarks>
/// <para>
/// Inlines are immutable records. Mutation goes through <see cref="RichTextBuilder"/> at
/// construction time; the formatter consumes the finished sequence read-only and may cache
/// derived state (measurements, wrap candidates) across paints.
/// </para>
/// </remarks>
public abstract record Inline;

/// <summary>
/// A styled text run — the atomic unit of paragraph content. Carries the raw text, a
/// <see cref="BrushedStyle"/> carrier (channels the run states; anything it declines to state is
/// inherited), an optional <see cref="IGlyphMap"/> for per-grapheme substitution
/// (Fullwidth, SmallCaps, custom icon set, …), and an optional OSC&#x202F;8 hyperlink target
/// that the renderer wraps around the run's visible cells.
/// </summary>
/// <remarks>
/// <para>
/// When a run is split across multiple lines by the formatter, the renderer re-emits the OSC&#x202F;8
/// open/close pair on each fragment so the link remains clickable on continuation lines. This is
/// the convention iTerm2, foot, kitty, and modern xterm all expect; emitting the open once and
/// closing once at the end of the original run causes the link target to be lost on the second
/// line.
/// </para>
/// </remarks>
public sealed record TextRun(
    string Text,
    BrushedStyle Style = default,
    IGlyphMap? Map = null,
    string? Hyperlink = null) : Inline
{
    /// <summary>
    /// The run's glyph source (proposal-glyph-runs): how its clusters measure and paint.
    /// <see langword="null"/> = the monospace identity. A sized or FIGlet source makes the run a
    /// first-class participant in the paragraph flow — it wraps, trims, and aligns through the
    /// same pipeline as plain text, at its own per-cluster cell advances.
    /// </summary>
    public Fonts.GlyphSource? Source { get; init; }
}

/// <summary>
/// A hard line break inside a paragraph. Ends the current line; subsequent inlines flow onto
/// the next row. Portable: source code constructing a <see cref="RichText"/> doesn't need to
/// decide between <c>\n</c> and <c>\r\n</c>. When the formatter is asked to serialize to a
/// raw string in a future iteration, <see cref="LineBreak"/> maps to <see cref="Environment.NewLine"/>.
/// </summary>
/// <remarks>
/// A line ending at a <see cref="LineBreak"/> is treated as "last in the paragraph" for
/// alignment purposes — a line that would normally be justified stays in its natural alignment
/// instead.
/// </remarks>
public sealed record LineBreak : Inline
{
    /// <summary>Singleton instance — <see cref="LineBreak"/> carries no state.</summary>
    public static LineBreak Instance { get; } = new();
}

/// <summary>
/// An embedded <see cref="IContent"/> placed inside the paragraph flow as a single atomic unit.
/// Treated by the wrap loop as an indivisible "word" of width
/// <c>Content.Measure((availableColumns, 1), capabilities).Columns</c> — never broken across
/// lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-row contract.</b> Inline content is measured with a row budget of 1; multi-row
/// content (full <c>Image</c>, <c>ScaledText</c> with scale &gt; 1, multi-line FIGlet output)
/// belongs at the block level via <see cref="BlockContent"/> instead. The formatter will clip
/// content that reports a height greater than 1.
/// </para>
/// </remarks>
public sealed record InlineContent(IContent Content) : Inline;
