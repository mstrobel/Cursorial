using System.Collections.Immutable;

using Cursorial.Output;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Base type for top-level elements in a <see cref="RichText"/> document. Blocks stack
/// vertically; the formatter measures each block under the document's column budget, applies
/// the block's <see cref="Alignment"/> within its row(s), and stacks them with optional inter-
/// block <see cref="Margin"/>.
/// </summary>
/// <remarks>
/// <para>
/// The block hierarchy is closed by convention: the formatter pattern-matches each known
/// subtype. Adding new built-in block types (Heading, UnorderedList, OrderedList, BlockQuote,
/// CodeBlock, Table) is purely additive — touch the formatter switch + add a record. A future
/// open extension point (e.g., a <c>CustomBlock</c> abstract record with a virtual
/// <c>Format</c> method) can be added when consumer demand justifies the surface.
/// </para>
/// </remarks>
public abstract record Block
{
    /// <summary>Horizontal alignment of the block's content within the column budget.</summary>
    public TextAlignment? Alignment { get; init; }

    /// <summary>
    /// Inter-block spacing applied above and below this block when stacked in a document.
    /// Horizontal margins are ignored at block-level — they're not meaningful when blocks
    /// occupy the full column budget.
    /// </summary>
    public Margins Margin { get; init; }
}

/// <summary>
/// A paragraph of styled text. The formatter walks <see cref="Inlines"/> and wraps according
/// to <see cref="Wrap"/>, applies <see cref="Trim"/> on lines that exceed the column budget
/// (or the optional <see cref="MaxLines"/> cap), and aligns each line per
/// <see cref="Block.Alignment"/>.
/// </summary>
public sealed record TextParagraph(ImmutableArray<Inline> Inlines) : Block
{
    public static readonly Margins DefaultMargins = new(0, 1);
    
    /// <summary>Wrap behavior for content exceeding the column budget.</summary>
    public WrapMode Wrap { get; init; } = WrapMode.WordWrap;

    /// <summary>How lines that don't fit are shortened.</summary>
    public TextTrimming Trim { get; init; } = TextTrimming.None;

    /// <summary>Optional per-paragraph row cap. Content past this many wrapped lines is dropped (with the active <see cref="Trim"/> applied to the final visible line).</summary>
    public int? MaxLines { get; init; }
}

/// <summary>
/// A horizontal rule rendered by repeating <see cref="Glyph"/> across the column budget.
/// Common box-drawing styles are exposed as static factories; supply any single-cell grapheme
/// for custom rules.
/// </summary>
public sealed record HorizontalRule(string Glyph, Style Style = default) : Block
{
    public static Margins DefaultMargins { get; } = new(0, 1);

    /// <summary>Light horizontal box-drawing rule (U+2500 ─).</summary>
    public static HorizontalRule Light  { get; } = new("─") { Margin = DefaultMargins };

    /// <summary>Heavy horizontal box-drawing rule (U+2501 ━).</summary>
    public static HorizontalRule Heavy  { get; } = new("━") { Margin = DefaultMargins };

    /// <summary>Double horizontal box-drawing rule (U+2550 ═).</summary>
    public static HorizontalRule Double { get; } = new("═") { Margin = DefaultMargins };

    /// <summary>Triple-dash horizontal box-drawing rule (U+2504 ┄).</summary>
    public static HorizontalRule Dashed { get; } = new("┄") { Margin = DefaultMargins };

    /// <summary>Quadruple-dot horizontal box-drawing rule (U+2508 ┈).</summary>
    public static HorizontalRule Dotted { get; } = new("┈") { Margin = DefaultMargins };
}

/// <summary>
/// A FIGlet-rendered text headline. The font's <see cref="IGlyphFont.Measure(ReadOnlySpan{char})"/>
/// determines the block's footprint; the formatter centers / left-anchors / right-anchors it
/// within the column budget per <see cref="Block.Alignment"/> and clips on overflow.
/// </summary>
public sealed record FigletBlock(string Text, IGlyphFont Face, Style Style = default) : Block;

/// <summary>
/// Kitty OSC 66 sized-text headline. The formatter computes the cell footprint from the
/// terminal's reported cell-pixel metrics; on capability misses it falls back to
/// <see cref="Fallback"/> (typically a FIGlet face derived from the <see cref="TextSizing"/>
/// scale, the same way <c>ScaledText</c> handles it).
/// </summary>
public sealed record SizedTextBlock(string Text, TextSizing Sizing, Style Style = default) : Block
{
    /// <summary>FIGlet face used when the terminal doesn't honor OSC 66.</summary>
    public IGlyphFont? Fallback { get; init; }
}

/// <summary>
/// A block-level <see cref="IContent"/> embedding — full Images, ScaledText, and other
/// multi-row content stacked between text blocks. Measured with a row budget derived from the
/// content's natural height; the formatter respects whatever the content reports.
/// </summary>
public sealed record BlockContent(IContent Content) : Block;
