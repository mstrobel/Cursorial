using System.Collections.Immutable;

using Cursorial.Rendering.Media;

namespace Cursorial.Rendering.Text;

/// <summary>
/// An immutable rich-text document — an ordered sequence of <see cref="Block"/> values.
/// Construct via <see cref="RichTextBuilder"/> for fluent assembly, or
/// <see cref="TextMarkup.Parse(string)" /> for the markup-language sugar.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RichText"/> is the canonical model the formatter consumes. Empty documents are
/// valid and produce a zero-row <see cref="FormattedText"/> when formatted; a document with a
/// single empty <see cref="TextParagraph"/> produces a one-row blank line.
/// </para>
/// </remarks>
public sealed record RichText(ImmutableArray<Block> Blocks)
{
    /// <summary>An empty rich-text document with no blocks.</summary>
    public static RichText Empty { get; } = new(ImmutableArray<Block>.Empty);

    /// <summary>True when the document has no blocks. Empty paragraphs do NOT count as empty here — they still occupy a row.</summary>
    public bool IsEmpty => Blocks.IsDefaultOrEmpty;
    
    /// <summary>The default style applied to all text in the document.</summary>
    /// <remarks>The background of the default style is used for filling the entire document host when requested.</remarks>
    public BrushedStyle DefaultStyle { get; init; } = BrushedStyle.Identity;
}
