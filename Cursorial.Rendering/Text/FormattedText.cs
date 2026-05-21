using System.Collections.Immutable;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// The result of formatting a <see cref="RichText"/> document against a column budget. Immutable
/// — format once and paint many times; querying <see cref="Size"/> doesn't need a buffer.
/// </summary>
public sealed record FormattedText(ImmutableArray<FormattedBlock> Blocks, Size Size) : IContent
{
    /// <summary>Empty formatted document — zero blocks, zero size.</summary>
    public static FormattedText Empty { get; } = new(ImmutableArray<FormattedBlock>.Empty, Size.Empty);

    /// <summary>
    /// Paint the formatted document into <paramref name="buffer"/> at the supplied
    /// <paramref name="bounds"/>. Blocks stack top-to-bottom inside the rect, observing their
    /// <see cref="FormattedBlock.Margin"/> top spacing (the first block's top is suppressed —
    /// the document anchors flush to the bounds). Content is clipped to the rect; returns the
    /// rectangle actually painted.
    /// </summary>
    public Rect Paint(CellBuffer buffer, Rect bounds, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(capabilities);

        int row = bounds.Row;
        int rowsAvailable = bounds.Rows;
        bool first = true;
        int paintedWidth = 0;
        Margins lastBlockMargins = Margins.Zero;

        foreach (var block in Blocks)
        {
            if (rowsAvailable <= 0) break;

            int marginTop = first ? 0 : Math.Max(block.Margin.Top, lastBlockMargins.Bottom);
            row += marginTop;
            rowsAvailable -= marginTop;
            if (rowsAvailable <= 0) break;

            int blockHeight = Math.Min(block.Size.Rows, rowsAvailable);
            if (blockHeight > 0)
            {
                int anchorColumn = ComputeAnchorColumn(bounds, block);
                PaintBlock(block, buffer, anchorColumn, row, blockHeight, bounds.Columns, capabilities);
                paintedWidth = Math.Max(paintedWidth, block.Size.Columns);
            }

            row += blockHeight;
            rowsAvailable -= blockHeight;
            first = false;
            lastBlockMargins = block.Margin;
        }

        return new Rect(bounds.Column, bounds.Row,
                        Math.Min(paintedWidth, bounds.Columns),
                        row - bounds.Row);
    }

    private static int ComputeAnchorColumn(Rect bounds, FormattedBlock block)
    {
        var alignment = block switch
                        {
                            FormattedParagraph            => TextAlignment.Left, // paragraphs already align their own lines internally
                            FormattedHorizontalRule hr    => hr.Alignment,
                            FormattedFigletBlock fig      => fig.Alignment,
                            FormattedSizedTextBlock sized => sized.Alignment,
                            FormattedContentBlock content => content.Alignment,
                            _                             => TextAlignment.Left
                        };

        int slack = Math.Max(0, bounds.Columns - block.Size.Columns);

        return alignment switch
               {
                   TextAlignment.Right  => bounds.Column + slack,
                   TextAlignment.Center => bounds.Column + slack / 2,
                   _                    => bounds.Column
               };
    }

    private static void PaintBlock(
        FormattedBlock block, CellBuffer buffer, int column, int row, int maxRows, int boundsColumns,
        OutputCapabilities capabilities)
    {
        switch (block)
        {
            case FormattedParagraph paragraph:
                PaintParagraph(paragraph, buffer, column, row, maxRows, capabilities);
                break;
            case FormattedHorizontalRule rule:
                PaintHorizontalRule(rule, buffer, column, row, boundsColumns);
                break;
            case FormattedFigletBlock figlet:
                figlet.Face.Paint(buffer, column, row, figlet.Text, figlet.Style);
                break;
            case FormattedSizedTextBlock sized:
                PaintSizedText(sized, buffer, column, row, capabilities);
                break;
            case FormattedContentBlock content:
                content.Content.Paint(buffer, new Rect(column, row, block.Size.Columns, maxRows),
                                      style: default, capabilities);
                break;
        }
    }

    private static void PaintParagraph(
        FormattedParagraph paragraph, CellBuffer buffer, int column, int row, int maxRows,
        OutputCapabilities capabilities)
    {
        int linesToPaint = Math.Min(paragraph.Lines.Length, maxRows);
        for (int i = 0; i < linesToPaint; i++)
        {
            var line = paragraph.Lines[i];
            int cursor = column;
            foreach (var run in line.Runs)
            {
                switch (run)
                {
                    case FormattedTextRun text:
                    {
                        var enumerator = text.Text.GetGraphemeEnumerator();
                        while (enumerator.MoveNext())
                        {
                            var grapheme = enumerator.Current;
                            int width = buffer.Set(cursor, row + i, grapheme.ToString(), text.Style);
                            cursor += width;
                        }
                        break;
                    }
                    case FormattedContentRun content:
                    {
                        var bounds = new Rect(cursor, row + i, content.Width, 1);
                        content.Content.Paint(buffer, bounds, content.Style, capabilities);
                        cursor += content.Width;
                        break;
                    }
                }
            }
        }
    }

    private static void PaintHorizontalRule(FormattedHorizontalRule rule, CellBuffer buffer, int column, int row, int width)
    {
        int glyphWidth = GraphemeWidth.StringWidth(rule.Glyph);
        if (glyphWidth <= 0) return;

        int cursor = column;
        int end = column + width;
        while (cursor + glyphWidth <= end)
        {
            buffer.Set(cursor, row, rule.Glyph, rule.Style);
            cursor += glyphWidth;
        }
    }

    private static void PaintSizedText(
        FormattedSizedTextBlock sized, CellBuffer buffer, int column, int row, OutputCapabilities capabilities)
    {
        // Mirror ScaledText's protocol: try OSC 66 fragment when supported, else fall back to
        // the configured glyph font. ScaledText itself encapsulates the decision tree, so we
        // delegate to a transient instance.
        var scaled = new ScaledText(sized.Text, sized.Sizing, sized.Fallback);
        scaled.Paint(buffer, new Rect(column, row, sized.Size.Columns, sized.Size.Rows),
                     sized.Style, capabilities);
    }

    Size IContent.Measure(Size availableSpace, OutputCapabilities capabilities)
        => Size.ClampTo(availableSpace);

    Rect IContent.Paint(CellBuffer buffer, Rect bounds, in Style style, OutputCapabilities capabilities)
        => Paint(buffer, bounds, capabilities);
}

/// <summary>
/// Base type for an individually formatted block. Concrete subtypes mirror the
/// <see cref="Block"/> hierarchy. <see cref="Size"/> is the block's content footprint (excluding
/// <see cref="Margin"/>); <see cref="Margin"/> is the inter-block spacing the formatter requested.
/// </summary>
public abstract record FormattedBlock(Size Size)
{
    /// <summary>Inter-block top/bottom margin applied during stacking; horizontal margins are ignored at block level.</summary>
    public Margins Margin { get; init; }
}

/// <summary>
/// A formatted text paragraph: an ordered list of fully-laid-out, aligned lines. Each line's
/// runs have already had glyph maps applied and trimming / alignment padding inserted as
/// regular space runs, so paint can be a straight walk.
/// </summary>
public sealed record FormattedParagraph(ImmutableArray<FormattedLine> Lines, Size Size) : FormattedBlock(Size);

/// <summary>
/// A formatted horizontal rule. The painter repeats <see cref="Glyph"/> across the line, up to
/// <see cref="Size"/>.Columns, applying <see cref="Style"/> and respecting <see cref="Alignment"/>
/// when the rule is narrower than the available column budget (rare for HRs but supported).
/// </summary>
public sealed record FormattedHorizontalRule(
    string Glyph, Style Style, TextAlignment Alignment, Size Size) : FormattedBlock(Size);

/// <summary>
/// A formatted FIGlet headline. <see cref="Face"/> drives both measurement (already reflected
/// in <see cref="Size"/>) and paint; <see cref="Text"/> is the source string.
/// </summary>
public sealed record FormattedFigletBlock(
    string Text, IGlyphFont Face, Style Style, TextAlignment Alignment, Size Size) : FormattedBlock(Size);

/// <summary>
/// A formatted Kitty-OSC-66 sized-text headline. When the negotiated capabilities support OSC 66,
/// the painter attaches a <c>SizedTextFragment</c>; otherwise it paints via
/// <see cref="Fallback"/> as a FIGlet headline.
/// </summary>
public sealed record FormattedSizedTextBlock(
    string Text, TextSizing Sizing, Style Style, IGlyphFont? Fallback,
    TextAlignment Alignment, Size Size) : FormattedBlock(Size);

/// <summary>
/// A formatted block-level <see cref="IContent"/> embedding. The painter delegates to
/// <see cref="IContent.Paint"/> at the block's anchor with a rect of <see cref="Size"/>.
/// </summary>
public sealed record FormattedContentBlock(
    IContent Content, TextAlignment Alignment, Size Size) : FormattedBlock(Size);

/// <summary>
/// A single line of formatted content. <see cref="Columns"/> is the line's visible cell width
/// (after alignment padding); <see cref="Runs"/> is the ordered sequence of styled text
/// fragments that compose it. Lines never contain trailing whitespace.
/// </summary>
public sealed record FormattedLine(ImmutableArray<FormattedRun> Runs, int Columns);

/// <summary>
/// The atomic unit of paintable content inside a <see cref="FormattedLine"/>. Concrete
/// subtypes carry either visible text (<see cref="FormattedTextRun"/>) or an embedded
/// <see cref="IContent"/> placement (<see cref="FormattedContentRun"/>). The painter walks the
/// line's runs in order and dispatches per subtype.
/// </summary>
public abstract record FormattedRun
{
    /// <summary>The cell footprint this run occupies on its line.</summary>
    public abstract int CellWidth { get; }
}

/// <summary>
/// A text run — final visible text (post-glyph-map), an SGR style, and an optional OSC&#x202F;8
/// hyperlink target. The painter walks graphemes and writes cells through
/// <see cref="CellBuffer.Set"/>.
/// </summary>
public sealed record FormattedTextRun(string Text, Style Style, string? Hyperlink = null) : FormattedRun
{
    /// <inheritdoc/>
    public override int CellWidth => GraphemeWidth.StringWidth(Text);
}

/// <summary>
/// An inline <see cref="IContent"/> placement laid out atomically inside a paragraph flow.
/// Width is known at format time (from <see cref="IContent.Measure"/>); the painter calls
/// <see cref="IContent.Paint"/> with a rect of (<see cref="Width"/>, 1) anchored at the run's
/// cell position, with the captured <see cref="Style"/> as backdrop.
/// </summary>
public sealed record FormattedContentRun(IContent Content, int Width, Style Style = default) : FormattedRun
{
    /// <inheritdoc/>
    public override int CellWidth => Width;
}
