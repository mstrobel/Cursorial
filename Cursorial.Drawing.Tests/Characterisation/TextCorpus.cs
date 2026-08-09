// ---------------------------------------------------------------------------------------------------
// MIGRATION SCAFFOLDING — see Characterisation/README-scaffolding.md.
//
// The corpus of rich-text documents the characterisation harness pins. Delete together with the rest of
// Cursorial.Drawing.Tests/Characterisation once the FormattedTextRun style-carrier migration (resolved
// CellStyle -> BrushedStyle + sampling frame) has landed and its own tests cover the behaviour.
// ---------------------------------------------------------------------------------------------------

using System.Collections.Immutable;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing.Characterisation;

/// <summary>
/// One corpus entry: a document, the budget it is laid out against, and the rectangle it is painted into.
/// </summary>
/// <remarks>
/// <see cref="Document"/> is a factory rather than a value so each tier lays out from a clean document —
/// <see cref="GlyphSource"/> caches resolved metrics and <see cref="ScaledText"/> caches a realized
/// placeholder, and a shared instance would let one tier's capabilities leak into the next.
/// </remarks>
internal sealed record TextCase
{
    /// <summary>Stable identifier — the dump's section header and the diff report's pointer. Never reuse.</summary>
    public required string Id { get; init; }

    /// <summary>Why the case is in the corpus: what the migration could break here.</summary>
    public required string Description { get; init; }

    /// <summary>Builds the document afresh.</summary>
    public required Func<RichText> Document { get; init; }

    /// <summary>Column budget handed to <see cref="TextFormatter.Format"/>.</summary>
    public required int Columns { get; init; }

    /// <summary>Optional document-level row cap.</summary>
    public int? MaxRows { get; init; }

    /// <summary>Width of the paint rectangle (and of the cell buffer allocated for it).</summary>
    public required int PaintColumns { get; init; }

    /// <summary>Height of the paint rectangle (and of the cell buffer allocated for it).</summary>
    public required int PaintRows { get; init; }

    /// <summary>Formatter factory — defaults to a stock <see cref="TextFormatter"/>.</summary>
    public Func<TextFormatter> Formatter { get; init; } = static () => new TextFormatter();

    /// <summary>Passed through to <see cref="TextFormatter.Format"/>.</summary>
    public bool FillEntireBounds { get; init; }

    /// <summary>Document-wide brush, or null for none.</summary>
    public IBrush? DocumentBrush { get; init; }

    /// <summary>
    /// Build a brush resolver even without a <see cref="DocumentBrush"/> — the per-run
    /// <see cref="ScopedBrush"/> cases need the Drawing resolver installed to read their run tags.
    /// </summary>
    public bool Brushed { get; init; }

    /// <summary>Inherited element attributes union-merged onto every painted cell by the resolver.</summary>
    public TextAttributes BaseAttributes { get; init; }

    /// <summary>Inherited underline shape, honoured only when <see cref="BaseAttributes"/> carries Underline.</summary>
    public UnderlineStyle BaseUnderlineShape { get; init; } = UnderlineStyle.Single;

    /// <summary>Whether tier 3 (fragment / VT-byte emission) should exercise this case.</summary>
    public bool EmitsFragments { get; init; }

    /// <summary>True when the case wants the Drawing brush resolver installed at paint.</summary>
    public bool UsesResolver => Brushed || DocumentBrush is not null;
}

/// <summary>The corpus. Order is hand-written and load-bearing — it is the order of the baseline files.</summary>
internal static class TextCorpus
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Amber = Color.FromRgb(255, 176, 0);
    private static readonly Color Teal = Color.FromRgb(0, 128, 128);

    /// <summary>Prose with several break opportunities, reused so wrapping cases differ only in budget.</summary>
    private const string Prose = "the quick brown fox jumps over the lazy dog";

    private static LinearGradientBrush LeftToRight() =>
        new(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);

    private static LinearGradientBrush TopToBottom() =>
        new(Red, Blue, startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom);

    public static ImmutableArray<TextCase> All { get; } =
    [
        // ---- Plain, empty, whitespace, and non-trivial document defaults ---------------------------

        new()
        {
            Id = "plain-single-line",
            Description = "One unwrapped run. The floor: if this moves, everything moved.",
            Document = static () => new RichTextBuilder().Run("Hello, world.").Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 3
        },
        new()
        {
            Id = "empty-document",
            Description = "RichText.Empty formats to FormattedText.Empty — zero blocks, zero size.",
            Document = static () => RichText.Empty,
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "empty-paragraph",
            Description = "A paragraph with no inlines still occupies a row.",
            Document = static () => new RichTextBuilder().Paragraph().Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "whitespace-only",
            Description = "Whitespace-only content: the packer drops leading whitespace, so this is the empty line.",
            Document = static () => new RichTextBuilder().Run("     ").Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "whitespace-with-tabs",
            Description = "Tab expansion at format time (TabWidth = 4) around real glyphs.",
            Document = static () => new RichTextBuilder().Run("a\tb\tc").Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 2
        },
        new()
        {
            Id = "document-default-style-nontrivial",
            Description = "A document whose DefaultStyle sets fg + bg + attributes + a curly coloured underline; "
                        + "every run inherits it, which is exactly what a delta carrier must keep reproducing.",
            Document = static () => new RichTextBuilder(
                                            CellStyle.Default
                                                     .WithForeground(Amber)
                                                     .WithBackground(Color.FromRgb(16, 16, 32))
                                                     .WithAttributes(TextAttributes.Bold | TextAttributes.Underline)
                                                     .WithUnderlineStyle(UnderlineStyle.Curly)
                                                     .WithUnderlineColor(Teal))
                                        .Run("inherit me")
                                        .Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 2
        },
        new()
        {
            Id = "document-default-style-fill-bounds",
            Description = "FillEntireBounds clears the whole rect to DefaultStyle and re-centres the document.",
            Document = static () => new RichTextBuilder(CellStyle.Default.WithBackground(Color.FromRgb(0, 32, 0)))
                                    .Run("filled")
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 4, FillEntireBounds = true
        },

        // ---- Wrapping at several widths, and every wrap mode ----------------------------------------

        new()
        {
            Id = "wrap-word-w8",
            Description = "Word wrap at a budget narrower than several words.",
            Document = static () => new RichTextBuilder().Run(Prose).Build(),
            Columns = 8, PaintColumns = 8, PaintRows = 8
        },
        new()
        {
            Id = "wrap-word-w12",
            Description = "Same prose, wider budget — the wrap points must move and nothing else.",
            Document = static () => new RichTextBuilder().Run(Prose).Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 6
        },
        new()
        {
            Id = "wrap-word-w20",
            Description = "Same prose again at 20.",
            Document = static () => new RichTextBuilder().Run(Prose).Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 4
        },
        new()
        {
            Id = "wrap-word-w44",
            Description = "Same prose at a budget that fits it whole — the no-wrap control.",
            Document = static () => new RichTextBuilder().Run(Prose).Build(),
            Columns = 44, PaintColumns = 44, PaintRows = 2
        },
        new()
        {
            Id = "wrap-character",
            Description = "CharacterWrap breaks mid-word at any cluster boundary.",
            Document = static () => new RichTextBuilder().Paragraph(wrap: WrapMode.CharacterWrap).Run(Prose).Build(),
            Columns = 10, PaintColumns = 10, PaintRows = 6
        },
        new()
        {
            Id = "wrap-nowrap-clips",
            Description = "NoWrap with no trimming: one line, clipped at the budget.",
            Document = static () => new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap).Run(Prose).Build(),
            Columns = 10, PaintColumns = 10, PaintRows = 2
        },
        new()
        {
            Id = "wrap-overflow-long-token",
            Description = "WordWrapOverflow lets an unbreakable token run past the right edge — the painter's "
                        + "clip, not the packer's, is what stops it.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(wrap: WrapMode.WordWrapOverflow)
                                    .Run("ok https://example.com/a/very/long/path end")
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 5
        },
        new()
        {
            Id = "wrap-soft-hyphen",
            Description = "A soft hyphen is a break opportunity that materialises a visible hyphen when used.",
            Document = static () => new RichTextBuilder().Run("extra­ordinarily long").Build(),
            Columns = 8, PaintColumns = 8, PaintRows = 5
        },
        new()
        {
            Id = "wrap-hard-linebreak",
            Description = "A LineBreak inline ends the line early; the following run flows onto the next row.",
            Document = static () => new RichTextBuilder().Run("first").LineBreak().Run("second").Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 3
        },
        new()
        {
            Id = "wrap-wide-glyphs",
            Description = "CJK: layout advance is 2 cells per cluster, and the painter's cursor must agree "
                        + "with the packer's arithmetic or every downstream column slides.",
            Document = static () => new RichTextBuilder().Run("字字字 字字字 字字").Build(),
            Columns = 7, PaintColumns = 7, PaintRows = 5
        },

        // ---- Every TextAlignment ---------------------------------------------------------------------

        new()
        {
            Id = "align-left",
            Description = "TextAlignment.Left over wrapped prose.",
            Document = static () => new RichTextBuilder().Paragraph(alignment: TextAlignment.Left).Run(Prose).Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 4
        },
        new()
        {
            Id = "align-center",
            Description = "TextAlignment.Center — excess slack goes right.",
            Document = static () => new RichTextBuilder().Paragraph(alignment: TextAlignment.Center).Run(Prose).Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 4
        },
        new()
        {
            Id = "align-right",
            Description = "TextAlignment.Right.",
            Document = static () => new RichTextBuilder().Paragraph(alignment: TextAlignment.Right).Run(Prose).Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 4
        },
        new()
        {
            Id = "align-justify",
            Description = "TextAlignment.Justify distributes slack into inter-word gaps as synthesized space "
                        + "runs — extra runs on the line, which a run-carrier change can disturb.",
            Document = static () => new RichTextBuilder().Paragraph(alignment: TextAlignment.Justify).Run(Prose).Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 4
        },
        new()
        {
            Id = "align-center-wider-than-budget",
            Description = "Painting a 12-column document into a 20-column rect: the block anchor, not the "
                        + "paragraph's own line alignment, does the centring.",
            Document = static () => new RichTextBuilder().Paragraph(alignment: TextAlignment.Center).Run(Prose).Build(),
            Columns = 12, PaintColumns = 20, PaintRows = 6
        },

        // ---- Trimming and the IsTrimmed / HasTrimmedLines flags ---------------------------------------

        new()
        {
            Id = "trim-character-ellipsis",
            Description = "NoWrap + CharacterEllipsis: truncate at a cluster and append U+2026.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                    .Run(Prose)
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "trim-word-ellipsis",
            Description = "NoWrap + WordEllipsis: back off to a word boundary, drop trailing space, append U+2026.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.WordEllipsis)
                                    .Run(Prose)
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "trim-clip-from-end",
            Description = "ClipFromEnd drops the overflow silently — no ellipsis, but HasTrimmedLines still set.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.ClipFromEnd)
                                    .Run(Prose)
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "trim-maxlines",
            Description = "Per-paragraph MaxLines with WordEllipsis on the last visible line.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(trim: TextTrimming.WordEllipsis, maxLines: 2)
                                    .Run(Prose)
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 4
        },
        new()
        {
            Id = "trim-document-rowcap",
            Description = "The document-level row cap drops whole blocks — HasTrimmedLines is the tooltip's hinge.",
            Document = static () => new RichTextBuilder()
                                    .Run(Prose).EndParagraph()
                                    .HorizontalRule(Cursorial.Rendering.Text.HorizontalRule.Double)
                                    .Run("second paragraph that will not be shown")
                                    .Build(),
            Columns = 12, MaxRows = 3, PaintColumns = 12, PaintRows = 3,
            Formatter = static () => new TextFormatter { Trim = TextTrimming.CharacterEllipsis }
        },
        new()
        {
            Id = "trim-ellipsis-in-a-styled-run",
            Description = "The trim indicator is synthesized — which style does it inherit?",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                    .Run("plain ")
                                    .Run("styled tail that overflows", CellStyle.Default.WithForeground(Amber))
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 2
        },

        // ---- Nested blocks with margins ----------------------------------------------------------------

        new()
        {
            Id = "blocks-margins-stacked",
            Description = "Three blocks with differing margins: adjacent margins collapse to the max, and the "
                        + "first block's top margin is suppressed.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(margin: new Margins(0, 2)).Run("top block")
                                    .Paragraph(margin: new Margins(0, 1)).Run("middle block")
                                    .Paragraph(margin: new Margins(0, 3)).Run("bottom block")
                                    .Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 10
        },
        new()
        {
            Id = "blocks-rules-and-paragraphs",
            Description = "Horizontal rules between paragraphs, each rule repeating its glyph across the budget.",
            Document = static () => new RichTextBuilder()
                                    .Run("header").EndParagraph()
                                    .HorizontalRule(Cursorial.Rendering.Text.HorizontalRule.Heavy)
                                    .Run("body").EndParagraph()
                                    .HorizontalRule("┈", CellStyle.Default.WithForeground(Teal))
                                    .Run("footer")
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 9
        },
        new()
        {
            Id = "blocks-rule-alignment",
            Description = "A rule painted into a rect wider than its own block size — the anchor-column rule.",
            Document = static () => new RichTextBuilder()
                                    .HorizontalRule("═", CellStyle.Default, TextAlignment.Center)
                                    .Build(),
            Columns = 8, PaintColumns = 16, PaintRows = 3
        },
        new()
        {
            Id = "blocks-inline-content",
            Description = "An InlineContent embedding is an indivisible word in the flow; a block-level one "
                        + "stacks. Both take one sampled style at their centre.",
            Document = static () => new RichTextBuilder()
                                    .Run("before ")
                                    .InlineContent(new GlyphContent("▣", 2))
                                    .Run(" after")
                                    .EndParagraph()
                                    .Content(new GlyphContent("█", 4), TextAlignment.Center)
                                    .Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 4
        },

        // ---- Markup: named, indexed and hex colours; attributes; maps; links ----------------------------

        new()
        {
            Id = "markup-named-colors",
            Description = "[fg=named] / [bg=named] resolve through MarkupColor's palette table.",
            Document = static () => TextMarkup.Parse("[fg=red]red[/fg] [bg=blue]on blue[/bg] [fg=brightcyan]bc[/fg]"),
            Columns = 24, PaintColumns = 24, PaintRows = 3
        },
        new()
        {
            Id = "markup-indexed-colors",
            Description = "[fg=NNN] palette indices — a Palette-kind Color, which is NOT an RGB triple.",
            Document = static () => TextMarkup.Parse("[fg=196]196[/fg] [bg=17]bg17[/bg] [fg=0]zero[/fg]"),
            Columns = 24, PaintColumns = 24, PaintRows = 3
        },
        new()
        {
            Id = "markup-hex-colors",
            Description = "[fg=#rrggbb] and the three-digit [fg=#rgb] short form.",
            Document = static () => TextMarkup.Parse("[fg=#ff8800]long[/fg] [fg=#0f0]short[/fg] [bg=#123456]bg[/bg]"),
            Columns = 24, PaintColumns = 24, PaintRows = 3
        },
        new()
        {
            Id = "markup-attributes",
            Description = "Nested [b][i][u][s] — attribute composition through the builder's style stack.",
            Document = static () => TextMarkup.Parse("[b]b[i]bi[u]biu[s]bius[/s][/u][/i][/b] plain"),
            Columns = 24, PaintColumns = 24, PaintRows = 3
        },
        new()
        {
            Id = "markup-mixed-runs-one-paragraph",
            Description = "Colour, attribute, glyph-map, hyperlink and break tags interleaved in one paragraph — "
                        + "many small runs on a line, wrapping between them.",
            Document = static () => TextMarkup.Parse(
                "[fg=red]red[/fg] plain [b]bold[/b] [font=fullwidth]FW[/font] "
              + "[link=https://example.com]link[/link][br/]after the break [bg=#204060]tail[/bg]"),
            Columns = 16, PaintColumns = 16, PaintRows = 6
        },
        new()
        {
            Id = "markup-paragraph-attributes",
            Description = "[p] attributes drive wrap / align / trim / maxlines from markup rather than the builder.",
            Document = static () => TextMarkup.Parse(
                "[p align=right wrap=word trim=word maxlines=2]" + Prose + "[/p]"),
            Columns = 14, PaintColumns = 14, PaintRows = 4
        },
        new()
        {
            Id = "markup-hr-and-escapes",
            Description = "[hr=style/] plus escaped brackets — the parser's literal path.",
            Document = static () => TextMarkup.Parse("before[hr=dashed/]after \\[not a tag\\] \\\\ done"),
            Columns = 16, PaintColumns = 16, PaintRows = 6
        },

        // ---- Brush scope: inline vs block vs document, and the wrap-invariance property -----------------

        new()
        {
            Id = "brush-document-scope-horizontal",
            Description = "A document brush is sampled against each BLOCK's rect — the gradient spans the block "
                        + "across wrapped lines, and resets between blocks.",
            Document = static () => new RichTextBuilder().Run(Prose).EndParagraph().Run("second").Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 7,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-document-scope-vertical",
            Description = "Top-to-bottom over wrapped lines: the block rect's height is the sampling extent.",
            Document = static () => new RichTextBuilder().Run(Prose).Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 5,
            DocumentBrush = TopToBottom()
        },
        new()
        {
            Id = "brush-run-inline-scope-unwrapped",
            Description = "A per-run Inline-scoped gradient laid out WITHOUT a wrap. Paired with the wrapped "
                        + "case below: the same grapheme must take the same colour in both.",
            Document = static () => new RichTextBuilder()
                                    .BrushedRun("aaaa bbbb cccc", new ScopedBrush(LeftToRight()))
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 3, Brushed = true
        },
        new()
        {
            Id = "brush-run-inline-scope-wrapped",
            Description = "THE wrap-invariance case (cf. FormattedText_InlineScopeIsWrapInvariant). The same "
                        + "Inline-scoped run wrapped at 9: the gradient must flow across the break as one "
                        + "reading-order strip rather than restarting per line-piece. This is the property "
                        + "most likely to break silently under a carrier migration.",
            Document = static () => new RichTextBuilder()
                                    .BrushedRun("aaaa bbbb cccc", new ScopedBrush(LeftToRight()))
                                    .Build(),
            Columns = 9, PaintColumns = 12, PaintRows = 4, Brushed = true
        },
        new()
        {
            Id = "brush-run-inline-scope-wrapped-wide",
            Description = "Wrap invariance with wide glyphs — the logical-offset accounting and the painter's "
                        + "cursor advance must stay in step across the break.",
            Document = static () => new RichTextBuilder()
                                    .BrushedRun("字字字 字字字", new ScopedBrush(LeftToRight()))
                                    .Build(),
            Columns = 7, PaintColumns = 10, PaintRows = 4, Brushed = true
        },
        new()
        {
            Id = "brush-run-block-scope",
            Description = "DeclarationScope.Block: the run samples the enclosing block's 2-D rect, so its "
                        + "position within the block decides its colour.",
            Document = static () => new RichTextBuilder()
                                    .Run("xxxxxxxxxx")
                                    .BrushedRun("AB", new ScopedBrush(LeftToRight(), DeclarationScope.Block))
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 3, Brushed = true
        },
        new()
        {
            Id = "brush-run-document-scope",
            Description = "DeclarationScope.Document samples the whole painted bounds, which is wider than the block.",
            Document = static () => new RichTextBuilder()
                                    .Run("xxxxxxxxxx")
                                    .BrushedRun("AB", new ScopedBrush(LeftToRight(), DeclarationScope.Document))
                                    .Build(),
            Columns = 14, PaintColumns = 24, PaintRows = 3, Brushed = true
        },
        new()
        {
            Id = "brush-run-wins-over-document",
            Description = "A run's own brush beats the document brush; untagged runs keep the document's.",
            Document = static () => new RichTextBuilder()
                                    .Run("gg ")
                                    .BrushedRun("RR", new ScopedBrush(new SolidColorBrush(Red)))
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 3,
            DocumentBrush = new SolidColorBrush(Teal), Brushed = true
        },
        new()
        {
            Id = "brush-over-document-default-foreground",
            Description = "An INHERITED foreground is the brush's to colour; the document default must not win.",
            Document = static () => new RichTextBuilder(CellStyle.Default.WithForeground(Color.FromRgb(180, 180, 180)))
                                    .Run("aaaa bbbb")
                                    .Build(),
            Columns = 9, PaintColumns = 12, PaintRows = 4,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-explicit-foreground-wins",
            Description = "A run's OWN explicit foreground beats the brush — the inheritance test the resolver "
                        + "reads BaseStyle for.",
            Document = static () => new RichTextBuilder()
                                    .Run("inherited ")
                                    .Run("explicit", CellStyle.Default.WithForeground(Amber))
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 3,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-horizontal-rule",
            Description = "A rule is coloured per cell against its own painted extent.",
            Document = static () => new RichTextBuilder().HorizontalRule().Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 2,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-inherited-attributes",
            Description = "The element-attribute leg of the resolver: inherited Bold|Underline plus a non-default "
                        + "underline shape, union-merged onto every cell without a re-format.",
            Document = static () => new RichTextBuilder()
                                    .Run("plain ")
                                    .Run("italic", CellStyle.Default.WithAttributes(TextAttributes.Italic))
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 3,
            Brushed = true,
            BaseAttributes = TextAttributes.Bold | TextAttributes.Underline,
            BaseUnderlineShape = UnderlineStyle.Curly
        },
        new()
        {
            Id = "brush-inline-content-fallback-glyph",
            Description = "Inline content samples one colour at its centre, so an image/icon degrading to a glyph "
                        + "still picks up the brush.",
            Document = static () => new RichTextBuilder()
                                    .Run("ab ")
                                    .InlineContent(new GlyphContent("█", 3))
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 3,
            DocumentBrush = LeftToRight()
        },

        // ---- FIGlet ---------------------------------------------------------------------------------

        new()
        {
            Id = "figlet-small-block",
            Description = "A FIGlet block is sugar over a one-run paragraph; the face paints directly at the "
                        + "piece rect, so a multi-cell glyph samples per cell.",
            Document = static () => new RichTextBuilder().Figlet("Hi", FigletFonts.Small).Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 6
        },
        new()
        {
            Id = "figlet-mini-centered",
            Description = "Block alignment applied to a FIGlet headline.",
            Document = static () => new RichTextBuilder()
                                    .Figlet("ab", FigletFonts.Mini, alignment: TextAlignment.Center)
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 5
        },
        new()
        {
            Id = "figlet-wraps-and-trims",
            Description = "A FIGlet run participates in the shared packer: it wraps at the budget, and the trim "
                        + "indicator falls back to '...' when the face cannot draw U+2026.",
            Document = static () => new RichTextBuilder()
                                    .Figlet("abcdef", FigletFonts.Mini)
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 8,
            Formatter = static () => new TextFormatter { Trim = TextTrimming.CharacterEllipsis }
        },
        new()
        {
            Id = "figlet-inline-run-mixed-band",
            Description = "A FIGlet-sourced run beside plain text in ONE paragraph: the band is as tall as the "
                        + "face, and the one-row run places itself by the paragraph's vertical alignment.",
            Document = static () => new RichTextBuilder()
                                    .Run("hi ")
                                    .Run("A", new GlyphSource(FigletFonts.Mini))
                                    .Run(" there")
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 5
        },
        new()
        {
            Id = "figlet-inline-run-baseline-aligned",
            Description = "Same mixed band under VerticalTextAlignment.Baseline — the run drops by the baseline "
                        + "difference rather than to the band's bottom row.",
            Document = static () =>
            {
                var builder = new RichTextBuilder();
                builder.Run("hi ").Run("A", new GlyphSource(FigletFonts.Small)).Run(" there");
                var document = builder.Build();
                var paragraph = (TextParagraph) document.Blocks[0];
                return document with
                       {
                           Blocks = [paragraph with { VerticalAlignment = VerticalTextAlignment.Baseline }]
                       };
            },
            Columns = 20, PaintColumns = 20, PaintRows = 6
        },
        new()
        {
            Id = "figlet-brushed-per-cell",
            Description = "The FIGlet arm hands the UNSAMPLED template to the face so a multi-cell glyph samples "
                        + "per cell. If a carrier migration collapses that to one colour per character, this "
                        + "dump goes flat.",
            Document = static () => new RichTextBuilder().Figlet("Hi", FigletFonts.Small).Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 6,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "figlet-explicit-background-boxes",
            Description = "A stated background BOXES the glyphs; Color.Default lets the face STAMP. The one "
                        + "place a run's whole-CellStyle carrier is load-bearing today.",
            Document = static () => new RichTextBuilder()
                                    .Figlet("Hi", FigletFonts.Small,
                                            CellStyle.Default.WithForeground(Amber).WithBackground(Teal))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 6
        },

        // ---- Sized text (OSC 66). Tiers 1 and 2 see the fallback arm; tier 3 sees the fragment. ---------

        new()
        {
            Id = "sized-block-scale2",
            Description = "SizedTextBlock at scale 2. Cells show the fallback face; the fragment is tier 3's.",
            Document = static () => new RichTextBuilder().SizedText("Title", new TextSizing(Scale: 2)).Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 4, EmitsFragments = true
        },
        new()
        {
            Id = "sized-block-scale3-centered",
            Description = "Scale 3, centred — the anchor column the fragment is emitted at moves with alignment.",
            Document = static () => new RichTextBuilder()
                                    .SizedText("Big", new TextSizing(Scale: 3), alignment: TextAlignment.Center)
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 5, EmitsFragments = true
        },
        new()
        {
            Id = "sized-block-styled",
            Description = "Sized text carrying an explicit fg + bg + attributes. That style becomes the OSC 66 "
                        + "backdrop SGR — historically the fragile part.",
            Document = static () => new RichTextBuilder()
                                    .SizedText("Hot", new TextSizing(Scale: 2),
                                               CellStyle.Default
                                                        .WithForeground(Amber)
                                                        .WithBackground(Color.FromRgb(64, 0, 0))
                                                        .WithAttributes(TextAttributes.Bold))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 4, EmitsFragments = true
        },
        new()
        {
            Id = "sized-block-fractional",
            Description = "A footprint-identity fractional sizing (s=1:n=1:d=2) still emits OSC 66 — it must not "
                        + "be optimised into the cell walk.",
            Document = static () => new RichTextBuilder()
                                    .SizedText("half", new TextSizing(Scale: 1, Numerator: 1, Denominator: 2))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 3, EmitsFragments = true
        },
        new()
        {
            Id = "sized-block-multiline",
            Description = "Author line breaks inside block text: one OSC 66 sequence per line, with explicit CUP "
                        + "between them.",
            Document = static () => new RichTextBuilder().SizedText("ab\ncde", new TextSizing(Scale: 2)).Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 6, EmitsFragments = true
        },
        new()
        {
            Id = "sized-block-alignment-keys",
            Description = "Non-default vertical/horizontal alignment keys in the OSC 66 metadata block.",
            Document = static () => new RichTextBuilder()
                                    .SizedText("v", new TextSizing(Scale: 2,
                                                                   Vertical: TextSizingVerticalAlignment.Center,
                                                                   Horizontal: TextSizingHorizontalAlignment.Right))
                                    .Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 4, EmitsFragments = true
        },
        new()
        {
            Id = "sized-inline-run-mixed",
            Description = "A sized RUN inline with plain text — sized and cell text sharing one line band.",
            Document = static () => new RichTextBuilder()
                                    .Run("go ")
                                    .Run("UP", new TextSizing(Scale: 2))
                                    .Run(" now")
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 4, EmitsFragments = true
        },
        new()
        {
            Id = "sized-over-painted-background",
            Description = "Sized text whose anchor cell carries a panel background. FrameRenderer hands the "
                        + "fragment the anchor cell's style, so the backdrop SGR must name that colour rather "
                        + "than the terminal default — the seam commit 2137e58b closed.",
            Document = static () => new RichTextBuilder(CellStyle.Default.WithBackground(Color.FromRgb(0, 48, 96)))
                                    .SizedText("Panel", new TextSizing(Scale: 2))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 4,
            FillEntireBounds = true, EmitsFragments = true
        },
        new()
        {
            Id = "sized-brushed-block-scope",
            Description = "A brushed sized run takes ONE colour sampled at its centre — the single-style arm. "
                        + "That sampled colour is what reaches the OSC 66 backdrop SGR.",
            Document = static () => new RichTextBuilder().SizedText("Grad", new TextSizing(Scale: 2)).Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 4,
            DocumentBrush = LeftToRight(), EmitsFragments = true
        },
        new()
        {
            Id = "sized-explicit-figlet-fallback",
            Description = "An explicit fallback face pins which arm a non-supporting terminal takes.",
            Document = static () => new RichTextBuilder()
                                    .SizedText("Fb", new TextSizing(Scale: 2), fallback: FigletFonts.Mini)
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 5, EmitsFragments = true
        },

        // ---- Mixed runs in one paragraph ---------------------------------------------------------------

        new()
        {
            Id = "mixed-runs-styles-in-one-line",
            Description = "Adjacent runs differing in each style axis in turn — the per-run carrier's whole job, "
                        + "on one line.",
            Document = static () => new RichTextBuilder()
                                    .Run("A", CellStyle.Default.WithForeground(Red))
                                    .Run("B", CellStyle.Default.WithBackground(Blue))
                                    .Run("C", CellStyle.Default.WithAttributes(TextAttributes.Bold))
                                    .Run("D", CellStyle.Default
                                                       .WithAttributes(TextAttributes.Underline)
                                                       .WithUnderlineStyle(UnderlineStyle.Dashed)
                                                       .WithUnderlineColor(Teal))
                                    .Run("E", CellStyle.Default.WithAttributes(TextAttributes.Inverse))
                                    .Run("F")
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 2
        },
        new()
        {
            Id = "mixed-runs-wrapping-mid-run",
            Description = "A styled run split by a wrap: both pieces must keep the run's style and its logical "
                        + "offset accounting.",
            Document = static () => new RichTextBuilder()
                                    .Run("lead ")
                                    .Run("styled tail across the break",
                                         CellStyle.Default.WithForeground(Amber).WithAttributes(TextAttributes.Italic))
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 5
        },
        new()
        {
            Id = "mixed-runs-hyperlink",
            Description = "A hyperlink run split by a wrap — the OSC 8 target rides the CellStyle and must "
                        + "survive on the continuation piece.",
            Document = static () => new RichTextBuilder()
                                    .Hyperlink("a linked phrase that wraps", "https://example.com/x")
                                    .Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 4
        },
        new()
        {
            Id = "mixed-runs-glyph-map",
            Description = "A glyph map substitutes per grapheme at format time, changing cell widths as it goes.",
            Document = static () => TextMarkup.Parse("ab [font=fullwidth]cd[/font] ef"),
            Columns = 12, PaintColumns = 12, PaintRows = 4
        }
    ];

    /// <summary>
    /// A minimal <see cref="IContent"/> that paints one repeated glyph with whatever style it is handed —
    /// stands in for an image/icon's glyph fallback without dragging real image decoding (and its
    /// resource loading) into a characterisation dump.
    /// </summary>
    private sealed class GlyphContent(string glyph, int width) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities)
            => new(Math.Min(width, Math.Max(1, availableSpace.Columns)), 1);

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in CellStyle style, OutputCapabilities capabilities)
        {
            for (int i = 0; i < Math.Min(width, bounds.Columns); i++)
                buffer.Set(bounds.Column + i, bounds.Row, glyph, style);

            return new Rect(bounds.Column, bounds.Row, Math.Min(width, bounds.Columns), 1);
        }
    }
}
