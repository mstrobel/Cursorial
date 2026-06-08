using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

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
public sealed record FormattedText(ImmutableArray<FormattedBlock> Blocks, Size Size, in Style DefaultStyle = default, bool FillEntireBounds = false) : IContent
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
    public Rect Paint(in CellBufferView buffer, in Rect bounds, OutputCapabilities capabilities,
                      BrushedTextResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);

        int row = bounds.Row;
        int rowsAvailable = bounds.Rows;
        bool first = true;
        int paintedWidth = 0;
        Margins lastBlockMargins = Margins.Zero;

        var fillEntireBounds = FillEntireBounds;

        if (fillEntireBounds/* && DefaultStyle.Background.IsDefault is false*/)
        {
            buffer.ClearCells(bounds, DefaultStyle);
            row = bounds.Row + (bounds.Rows - Size.Rows) / 2;
        }

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
                PaintBlock(block, buffer, anchorColumn, row, blockHeight, bounds.Columns, capabilities, resolver);
                paintedWidth = Math.Max(paintedWidth, block.Size.Columns);
            }

            row += blockHeight;
            rowsAvailable -= blockHeight;
            first = false;
            lastBlockMargins = block.Margin;
        }

        if (fillEntireBounds)
            return bounds;

        return new Rect(bounds.Column, bounds.Row,
                        Math.Min(paintedWidth, bounds.Columns),
                        row - bounds.Row);
    }

    private static int ComputeAnchorColumn(in Rect bounds, FormattedBlock block)
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
        FormattedBlock block, in CellBufferView buffer, int column, int row, int maxRows, int boundsColumns,
        OutputCapabilities capabilities, BrushedTextResolver? resolver)
    {
        // The block's 2-D rect — the sampling bounds for a block/document-scoped brush. Text and rules sample
        // the resolver per cell; single-Style elements (figlet, sized text, block content) sample one color at
        // the block's center and hand it to their own painter (so a glyph an image/icon degrades to picks up
        // the brush — the fallback-glyph gradient). Clamped to ≥1 so a degenerate block can't throw.
        var blockRect = new Rect(column, row, Math.Max(1, block.Size.Columns), Math.Max(1, maxRows));
        int centerColumn = column + block.Size.Columns / 2;
        int centerRow = row + maxRows / 2;

        switch (block)
        {
            case FormattedParagraph paragraph:
                PaintParagraph(paragraph, buffer, column, row, maxRows, capabilities, resolver);
                break;
            case FormattedHorizontalRule rule:
                PaintHorizontalRule(rule, buffer, column, row, boundsColumns, resolver);
                break;
            case FormattedFigletBlock figlet:
                figlet.Face.Paint(buffer, column, row, figlet.Text,
                                  ResolveStyle(resolver, figlet.Style, centerColumn, centerRow, blockRect));
                break;
            case FormattedSizedTextBlock sized:
                PaintSizedText(sized, buffer, column, row, capabilities,
                               ResolveStyle(resolver, sized.Style, centerColumn, centerRow, blockRect));
                break;
            case FormattedContentBlock content:
                content.Content.Paint(buffer, new Rect(column, row, block.Size.Columns, maxRows),
                                      ResolveStyle(resolver, default, centerColumn, centerRow, blockRect), capabilities);
                break;
        }
    }

    /// <summary>Resolve one cell's style via the optional brush resolver (or the flat <paramref name="baseStyle"/>).</summary>
    private static Style ResolveStyle(BrushedTextResolver? resolver, in Style baseStyle, int column, int row, in Rect block)
        => resolver is null ? baseStyle : resolver(new BrushedTextContext(baseStyle, column, row, block));

    private static void PaintParagraph(
        FormattedParagraph paragraph, in CellBufferView buffer, int column, int row, int maxRows,
        OutputCapabilities capabilities, BrushedTextResolver? resolver)
    {
        int linesToPaint = Math.Min(paragraph.Lines.Length, maxRows);

        // The block's 2-D rect — the sampling bounds for a block/document-scoped brush (6a.1). Built once
        // per paragraph; clamped to ≥1 so a degenerate (zero-width/height) paragraph can't throw.
        var blockRect = new Rect(column, row, Math.Max(1, paragraph.Size.Columns), Math.Max(1, linesToPaint));

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
                            // Resolver (when present) recolors per cell — block-scoped 2-D sampling. Width is
                            // grapheme-driven, so a substituted style is layout-safe.
                            var style = ResolveStyle(resolver, text.Style, cursor, row + i, blockRect);
                            int width = buffer.Set(cursor, row + i, grapheme.ToString(), style);
                            cursor += width;
                        }
                        break;
                    }
                    case FormattedContentRun content:
                    {
                        var bounds = new Rect(cursor, row + i, content.Width, 1);
                        // Inline content samples one color at its center against the block rect — so a fallback
                        // glyph (when no graphics protocol) is brush-colored; a real image ignores the style.
                        var style = ResolveStyle(resolver, content.Style, cursor + content.Width / 2, row + i, blockRect);
                        content.Content.Paint(buffer, bounds, style, capabilities);
                        cursor += content.Width;
                        break;
                    }
                }
            }
        }
    }

    private static void PaintHorizontalRule(
        FormattedHorizontalRule rule, in CellBufferView buffer, int column, int row, int width,
        BrushedTextResolver? resolver)
    {
        int glyphWidth = GraphemeWidth.StringWidth(rule.Glyph);
        if (glyphWidth <= 0) return;

        var ruleRect = new Rect(column, row, Math.Max(1, width), 1);   // gradient spans the rule's painted extent
        int cursor = column;
        int end = column + width;
        while (cursor + glyphWidth <= end)
        {
            buffer.Set(cursor, row, rule.Glyph, ResolveStyle(resolver, rule.Style, cursor, row, ruleRect));
            cursor += glyphWidth;
        }
    }

    private static void PaintSizedText(
        FormattedSizedTextBlock sized, in CellBufferView buffer, int column, int row,
        OutputCapabilities capabilities, in Style style)
    {
        // Mirror ScaledText's protocol: try OSC 66 fragment when supported, else fall back to
        // the configured glyph font. ScaledText itself encapsulates the decision tree, so we
        // delegate to a transient instance. The style (already brush-resolved by the caller) colors the
        // OSC-66 backdrop / the FIGlet fallback glyphs.
        var scaled = new ScaledText(sized.Text, sized.Sizing, sized.Fallback);
        scaled.Paint(buffer, new Rect(column, row, sized.Size.Columns, sized.Size.Rows),
                     style, capabilities);
    }

    Size IContent.Measure(Size availableSpace, OutputCapabilities capabilities)
        => Size.ClampTo(availableSpace);

    Rect IContent.Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
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
/// <see cref="CellBufferView.Set"/>.
/// </summary>
public sealed record FormattedTextRun : FormattedRun
{
    /// <summary>
    /// A text run — final visible text (post-glyph-map), an SGR style, and an optional OSC&#x202F;8
    /// hyperlink target. The painter walks graphemes and writes cells through
    /// <see cref="CellBufferView.Set"/>.
    /// </summary>
    [SetsRequiredMembers]
    public FormattedTextRun(string Text, Style Style, string? Hyperlink = null)
    {
        this.Text = Text;
        this.Style = Style;
        this.Hyperlink = Hyperlink;
        
        if (this.Hyperlink is {} link)
            this.Style = this.Style.WithHyperlink(link);
    }

    /// <inheritdoc/>
    public override int CellWidth => GraphemeWidth.StringWidth(Text);

    public required string Text { get; init; }
    public required Style Style { get; init; }

    public string? Hyperlink { get; init; }

    public void Deconstruct(out string Text, out Style Style, out string? Hyperlink)
    {
        Text = this.Text;
        Style = this.Style;
        Hyperlink = this.Hyperlink;
    }
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
