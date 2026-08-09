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
        },

        // ---- APPENDED: holes the first 73 cases left open ------------------------------------------------
        //
        // Appended rather than filed into their families ON PURPOSE. Sections are emitted in array order, so
        // inserting mid-array re-flows every later section and buries real movement in the churn. Each case
        // below names the case it is the A/B partner of, so a reader can diff two adjacent-in-meaning
        // sections even though they are far apart in the file.

        new()
        {
            Id = "figlet-brushed-explicit-background",
            Description = "figlet-explicit-background-boxes, plus a document brush — the pair that shows the "
                        + "FIGlet arm's two halves disagreeing. With no resolver the painter reads the run's "
                        + "background sentinel and asks for a box (FormattedText.cs:240-243); with one it hands "
                        + "the face a BrushedStyle and never asks at all (:249-251 — and FigletFont's brushed "
                        + "overload, :234-252, has no GlyphPaint.Ink call, so no box is ever filled), so the "
                        + "glyph gaps stay unwritten and the stated background survives on the ink cells only. "
                        + "None of the first 73 cases combined a stated background with a brush, so the missing "
                        + "box was invisible. The foreground is left inherited so the "
                        + "brush leg is live too: this also pins per-cell gradient sampling over a run that "
                        + "states a background.",
            Document = static () => new RichTextBuilder()
                                    .Figlet("Hi", FigletFonts.Small, CellStyle.Default.WithBackground(Teal))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 6,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "figlet-transparent-document-default",
            Description = "figlet-small-block on CellStyle.Transparent instead of CellStyle.Default — which is "
                        + "what production actually builds (RichTextPresenter.cs:433, TextBlock.cs:293) and what "
                        + "none of the first 73 cases used (sized-transparent-document-default, appended "
                        + "alongside this one, is the other). Transparent is Rgb(0,0,0,0), NOT Default, so "
                        + "Background.IsDefault is false and the face takes the BOX arm (FormattedText.cs:240-243). "
                        + "This section does NOT pin that arm, and it is not the only case that takes it: "
                        + "figlet-explicit-background-boxes states an opaque Teal, so it takes BOX too, and it is "
                        + "the case where the arm IS observable — its gap cells carry the Teal the box filled. "
                        + "Here the fill is TRANSPARENT and the blank underneath is opaque, so the box composites "
                        + "straight back to it and the gap cells come out byte-identical to figlet-small-block's. "
                        + "Read the INK cells instead: what this section actually demonstrates is a transparent "
                        + "foreground and underline colour resolving to the backdrop rather than staying "
                        + "Color.Default. A stationary section is therefore NOT evidence the box arm is intact. "
                        + "Seeing a transparent box would take a surface whose blank is itself transparent (a "
                        + "Scene); nothing in the repo puts this face's box arm on one today.",
            Document = static () => new RichTextBuilder(CellStyle.Transparent)
                                    .Figlet("Hi", FigletFonts.Small)
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 6
        },
        new()
        {
            Id = "sized-transparent-document-default",
            Description = "sized-block-scale2 on CellStyle.Transparent — the same production default, carried "
                        + "all the way into the OSC 66 backdrop SGR, where it stops being invisible. A fragment "
                        + "is a paint path that cannot composite transparency away — sized text is never written "
                        + "into cells, so there is no back buffer to blend against — which is why "
                        + "FrameRenderer.EmitFragmentBytes resolves all three colour channels itself before the "
                        + "absolute SGR goes out, and why the three do not resolve alike. BACKGROUND takes the "
                        + "anchor cell's own, the surface the fragment is drawn over, so a transparent one "
                        + "reaches the wire at neither tier. FOREGROUND and UNDERLINE COLOUR have no ink "
                        + "underneath to inherit and are reset to Color.Default, so the emission gains a bare 39 "
                        + "— a foreground RESET — and states no underline colour, which is also what "
                        + "StyleQuantizer produces for a transparent colour at every depth below truecolor. "
                        + "Authored in phase 0a, when the substitution covered background only: the transparent "
                        + "foreground and underline colour then survived quantization at TRUECOLOR and escaped "
                        + "as an explicit BLACK (38;2;0;0;0;58:2::0:0:0) while ANSI256 emitted the 39, the two "
                        + "tiers disagreeing about one document. Note the background outcome is over-determined "
                        + "— WriteDelta also declines to emit anything for a transparent background "
                        + "(FrameRenderer.cs:741-744) — so this section pins the bytes without isolating which "
                        + "rule produced them.",
            Document = static () => new RichTextBuilder(CellStyle.Transparent)
                                    .SizedText("Title", new TextSizing(Scale: 2))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 4, EmitsFragments = true
        },
        new()
        {
            Id = "align-center-brushed-wider-than-budget",
            Description = "align-center-wider-than-budget, plus a horizontal document brush. Among the first 73 "
                        + "cases the align-* family carries no brush and no brushed case states an alignment, so "
                        + "nothing combined them. The combination makes the two anchoring computations visible at "
                        + "once: the block "
                        + "rect a brush samples against is built from the BLOCK anchor, which is Left for a "
                        + "paragraph (FormattedText.cs:175), while the lines are laid out at the paragraph's own "
                        + "Center anchor (:187). Painted 20 wide from a 12-wide budget, the sampling rect spans "
                        + "columns 0-11 while the glyphs sit further right, so the ramp's tail clamps.",
            Document = static () => new RichTextBuilder().Paragraph(alignment: TextAlignment.Center).Run(Prose).Build(),
            Columns = 12, PaintColumns = 20, PaintRows = 6,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "figlet-inline-run-tagged-brush",
            Description = "figlet-inline-run-mixed-band with a per-run ScopedBrush on the FIGlet run and a "
                        + "contrasting solid document brush underneath. brush-run-wins-over-document pins the "
                        + "tag winning on a plain run; this pins what the same construction does on the FIGlet "
                        + "arm, which passes the run's tag into the resolver context (FormattedText.Brushed). "
                        + "So the run's declared red reaches the face, and the document's teal colours the "
                        + "untagged runs around it. Authored in phase 0a while that context was hard-coded to "
                        + "tag: null — the declared brush was dropped and the teal coloured the glyphs too, "
                        + "which is the behaviour the phase-0b diff moved off.",
            Document = static () => new RichTextBuilder()
                                    .Run("hi ")
                                    .Run("A", new GlyphSource(FigletFonts.Mini),
                                         tag: new ScopedBrush(new SolidColorBrush(Red)))
                                    .Run(" there")
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 5,
            DocumentBrush = new SolidColorBrush(Teal)
        },
        new()
        {
            Id = "sized-inline-run-tagged-brush",
            Description = "The same tag reaching the SIZED arm, authored the way the sized overload allows it — "
                        + "a PushTag scope the run inherits (RichTextBuilder.cs:183-184 omits the tag argument, "
                        + "so it defaults to null, and :174 falls back to CurrentTag). The sized arm passes the "
                        + "run's tag into FormattedText.ResolveStyle just as the FIGlet arm does, so the "
                        + "declared red is what reaches the sampled style — and, at tier 3, the OSC 66 backdrop "
                        + "SGR. Authored in phase 0a while both arms hard-coded tag: null and the document's "
                        + "teal reached the style instead.",
            Document = static () =>
            {
                var builder = new RichTextBuilder();
                builder.Run("go ");

                using (builder.PushTag(new ScopedBrush(new SolidColorBrush(Red))))
                    builder.Run("UP", new TextSizing(Scale: 2));

                builder.Run(" now");
                return builder.Build();
            },
            Columns = 20, PaintColumns = 20, PaintRows = 4,
            DocumentBrush = new SolidColorBrush(Teal), EmitsFragments = true
        },
        new()
        {
            Id = "brush-fill-entire-bounds",
            Description = "FillEntireBounds with a document brush — the other two cases that set the flag "
                        + "(document-default-style-fill-bounds, sized-over-painted-background) carry no brush. "
                        + "Two operations sharing one rect: ClearCells fills the whole box with the document's "
                        + "DefaultStyle, a CellStyle with no way to name a brush (FormattedText.cs:60-64), and "
                        + "the document then paints its own extent brushed. The surround is therefore flat while "
                        + "the text ramps. The clear also re-centres the document vertically (:63), which is why "
                        + "the word lands on row 2 of 5; the block rect the brush samples against moves down with "
                        + "it, but this brush is HORIZONTAL, so nothing here samples that axis and the moved row "
                        + "is not pinned by this section.",
            Document = static () => new RichTextBuilder(CellStyle.Default.WithBackground(Color.FromRgb(0, 32, 0)))
                                    .Run("filled")
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 5,
            FillEntireBounds = true, DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "markup-brush-run-wrapped",
            Description = "brush-run-inline-scope-wrapped's geometry reached through MARKUP instead of "
                        + "BrushedRun: same text, same budget, same paint rect, so the two sections are meant "
                        + "to be read against each other. Both wrap-invariance guards reach the brush through "
                        + "the BUILDER — brush-run-inline-scope-wrapped via BrushedRun, and "
                        + "FormattedText_InlineScopeIsWrapInvariant via a bare run plus a hand-written resolver "
                        + "— and [brush=…] arrives by a different route: the parser resolves "
                        + "the value to a ScopedBrush and pushes it as a tag SCOPE (TextMarkup.cs:294-295) rather "
                        + "than attaching it to one run — so a markup brush that stopped flowing across a wrap "
                        + "would not move any existing baseline.",
            Document = static () => TextMarkup.Parse("[brush=linear:#ff0000,#0000ff]aaaa bbbb cccc[/brush]",
                                                     BrushMarkup.Options()),
            Columns = 9, PaintColumns = 12, PaintRows = 4, Brushed = true
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
