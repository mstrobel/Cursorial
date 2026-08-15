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
    /// Build a brush resolver even without a <see cref="DocumentBrush"/> — the declaration cases
    /// (run-carrier, block-style, document-default brushes) want the Drawing resolver installed so
    /// the production ladder, not just the painter's fold, is what the dump exercises.
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
                                            (PartialStyle.WithForeground(Amber)
                                                 with { Background = Color.FromRgb(16, 16, 32), UnderlineColor = Teal })
                                                .Weighing(TextWeight.Bold)
                                                .Underlining(UnderlineStyle.Curly))
                                        .Run("inherit me")
                                        .Build(),
            Columns = 16, PaintColumns = 16, PaintRows = 2
        },
        new()
        {
            Id = "document-default-style-fill-bounds",
            Description = "FillEntireBounds fills the whole rect with the document default's background "
                        + "(durable, owning blanks) and re-centres the document.",
            Document = static () => new RichTextBuilder(PartialStyle.WithBackground(Color.FromRgb(0, 32, 0)))
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
                                    .Run("styled tail that overflows", PartialStyle.WithForeground(Amber))
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
                                    .HorizontalRule("┈", PartialStyle.WithForeground(Teal))
                                    .Run("footer")
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 9
        },
        new()
        {
            Id = "blocks-rule-alignment",
            Description = "A rule painted into a rect wider than its own block size — the anchor-column rule.",
            Document = static () => new RichTextBuilder()
                                    .HorizontalRule("═", default, TextAlignment.Center)
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
            Description = "A document brush samples ONE continuous rect — the document's derived extent — so "
                        + "one gradient spans both blocks and nothing resets at the block boundary. The cells "
                        + "here cannot tell the extent from the old painted-docBounds rect: the prose fills "
                        + "the 12-column budget, so the two rects differ only in height and origin-matching "
                        + "width, and a horizontal brush never samples the row axis. The desc names the "
                        + "extent because the contract does; the freeze is axis-coincidence, not identity.",
            Document = static () => new RichTextBuilder().Run(Prose).EndParagraph().Run("second").Build(),
            Columns = 12, PaintColumns = 12, PaintRows = 7,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-document-scope-vertical",
            Description = "Top-to-bottom over wrapped lines: the derived document extent's height — the four "
                        + "rows the text inks, not the paint rect's five — is what the ramp spans.",
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
                                    .Run("aaaa bbbb cccc", new BrushedStyle { Foreground = LeftToRight() })
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
                                    .Run("aaaa bbbb cccc", new BrushedStyle { Foreground = LeftToRight() })
                                    .Build(),
            Columns = 9, PaintColumns = 12, PaintRows = 4, Brushed = true
        },
        new()
        {
            Id = "brush-run-inline-scope-wrapped-wide",
            Description = "Wrap invariance with wide glyphs — the logical-offset accounting and the painter's "
                        + "cursor advance must stay in step across the break.",
            Document = static () => new RichTextBuilder()
                                    .Run("字字字 字字字", new BrushedStyle { Foreground = LeftToRight() })
                                    .Build(),
            Columns = 7, PaintColumns = 10, PaintRows = 4, Brushed = true
        },
        new()
        {
            Id = "brush-block-style-scope",
            Description = "A gradient declared on the enclosing block's own style: every run that declares no "
                        + "foreground samples the block's 2-D rect, so a run's position within the block "
                        + "decides its colour. Replaces brush-run-block-scope (a Block-scoped ScopedBrush tag "
                        + "on one run), a construction scope inference removed.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(style: new BrushedStyle { Foreground = LeftToRight() })
                                    .Run("xxxxxxxxxx")
                                    .Run("AB")
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 3, Brushed = true
        },
        new()
        {
            Id = "brush-document-default-gradient",
            Description = "A gradient declared as the document default's foreground samples the document's "
                        + "derived extent — the ladder's document rung observed directly: the 12-wide text in "
                        + "the 24-wide budget ramps across its own 12 columns, and the section's bytes are the "
                        + "byte-twin of brush-block-style-scope's ramp (for a single Left block the document "
                        + "rung's rect and the block rung's rect coincide; the two sections differ only in "
                        + "which rung declared the brush). Replaces brush-run-document-scope (a "
                        + "Document-scoped ScopedBrush tag), a construction scope inference removed.",
            Document = static () => new RichTextBuilder(new BrushedStyle { Foreground = LeftToRight() })
                                    .Run("xxxxxxxxxx")
                                    .Run("AB")
                                    .Build(),
            Columns = 14, PaintColumns = 24, PaintRows = 3, Brushed = true
        },
        new()
        {
            Id = "brush-run-wins-over-document",
            Description = "A run's own brush beats the document brush; untagged runs keep the document's.",
            Document = static () => new RichTextBuilder()
                                    .Run("gg ")
                                    .Run("RR", new BrushedStyle { Foreground = new SolidColorBrush(Red) })
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 3,
            DocumentBrush = new SolidColorBrush(Teal), Brushed = true
        },
        new()
        {
            Id = "document-default-foreground-beats-preference-brush",
            Description = "A document default that STATES a foreground beats the caller's preference brush — "
                        + "the object model wins wherever it speaks; the preference colours only text no "
                        + "level declared for.",
            Document = static () => new RichTextBuilder(PartialStyle.WithForeground(Color.FromRgb(180, 180, 180)))
                                    .Run("aaaa bbbb")
                                    .Build(),
            Columns = 9, PaintColumns = 12, PaintRows = 4,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-explicit-foreground-wins",
            Description = "A run's OWN explicit foreground beats the brush — a stated CellStyle channel is a "
                        + "declaration at run scope, and the ladder colours only text no level declared for.",
            Document = static () => new RichTextBuilder()
                                    .Run("inherited ")
                                    .Run("explicit", PartialStyle.WithForeground(Amber))
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
                                    .Run("italic", PartialStyle.Postured(TextStyle.Italic))
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
                                            PartialStyle.WithForeground(Amber) with { Background = Teal })
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
                                               (PartialStyle.WithForeground(Amber)
                                                    with { Background = Color.FromRgb(64, 0, 0) })
                                                   .Weighing(TextWeight.Bold))
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
            Document = static () => new RichTextBuilder(PartialStyle.WithBackground(Color.FromRgb(0, 48, 96)))
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
                                    .Run("A", PartialStyle.WithForeground(Red))
                                    .Run("B", PartialStyle.WithBackground(Blue))
                                    .Run("C", PartialStyle.Weighted(TextWeight.Bold))
                                    .Run("D", PartialStyle.WithUnderline(UnderlineStyle.Dashed, Teal))
                                    .Run("E", PartialStyle.WithApplied(TextAttributes.Inverse))
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
                                         PartialStyle.WithForeground(Amber).Posturing(TextStyle.Italic))
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
                        + "FIGlet arm's two halves disagreeing. With no resolver the painter sees the stated "
                        + "background and asks for a box (the arm's resolver-null path in "
                        + "FormattedText.PaintParagraph); with one it hands the face a BrushedStyle and never "
                        + "asks at all (the resolver path — FigletFont's brushed overload has no GlyphPaint.Ink "
                        + "call, so no box is ever filled), so the "
                        + "glyph gaps stay unwritten and the stated background survives on the ink cells only. "
                        + "None of the first 73 cases combined a stated background with a brush, so the missing "
                        + "box was invisible. The foreground is left inherited so the "
                        + "brush leg is live too: this also pins per-cell gradient sampling over a run that "
                        + "states a background.",
            Document = static () => new RichTextBuilder()
                                    .Figlet("Hi", FigletFonts.Small, PartialStyle.WithBackground(Teal))
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
            Document = static () => new RichTextBuilder(PartialStyle.WithForeground(Color.Transparent)
                                                                with { Background = Color.Transparent, UnderlineColor = Color.Transparent })
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
            Document = static () => new RichTextBuilder(PartialStyle.WithForeground(Color.Transparent)
                                                                with { Background = Color.Transparent, UnderlineColor = Color.Transparent })
                                    .SizedText("Title", new TextSizing(Scale: 2))
                                    .Build(),
            Columns = 24, PaintColumns = 24, PaintRows = 4, EmitsFragments = true
        },
        new()
        {
            Id = "align-center-brushed-wider-than-budget",
            Description = "align-center-wider-than-budget, plus a horizontal document brush. Among the first 73 "
                        + "cases the align-* family carries no brush and no brushed case states an alignment, so "
                        + "nothing combined them. The document/preference rung samples the derived extent — "
                        + "(4,0,12,4), the block's REAL Center anchor in the 20-column rect — so the ramp spans "
                        + "exactly the centred ink, columns 4-15: defect 1's family finally sampling where the "
                        + "glyphs land. The Left-forced block SamplingRect this desc once deferred to is gone; "
                        + "the walk carries only the ink-anchored Extent, and the block rung samples it "
                        + "(witnessed by brush-block-style-scope-centred).",
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
                                         new BrushedStyle { Foreground = new SolidColorBrush(Red) })
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
                builder.Run("UP", new TextSizing(Scale: 2), new BrushedStyle { Foreground = new SolidColorBrush(Red) });
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
                        + "Two operations sharing one rect: the surround BACKGROUND FILL samples the whole box "
                        + "(its source ladder reads the preference Background, absent here — the document brush "
                        + "is a FOREGROUND preference with no background leg — then the document default's, the "
                        + "solid green), and the document then paints its own extent brushed. The surround is "
                        + "therefore flat while the text ramps. The fill also re-centres the document vertically, "
                        + "which is why the word lands on row 2 of 5; the derived extent the brush samples "
                        + "against moves down with it, but this brush is HORIZONTAL, so nothing here samples "
                        + "that axis and the moved row is not pinned by this section.",
            Document = static () => new RichTextBuilder(PartialStyle.WithBackground(Color.FromRgb(0, 32, 0)))
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
            Document = static () => TextMarkup.Parse("[fg=linear:#ff0000,#0000ff]aaaa bbbb cccc[/fg]",
                                                     BrushMarkup.Options()),
            Columns = 9, PaintColumns = 12, PaintRows = 4, Brushed = true
        },
        new()
        {
            Id = "brush-run-solid-equal-to-document-default",
            Description = "A run-declared SOLID equal to the document default's colour still wins at its own "
                        + "scope — declaration wins by policy, no value equality anywhere. Under a gradient "
                        + "document brush the declared solid is visibly flat; phase 6's restatement sentinel "
                        + "briefly inverted this, unpinned.",
            Document = static () => new RichTextBuilder(PartialStyle.WithForeground(Color.FromRgb(180, 180, 180)))
                                    .Run("aaaa ")
                                    .Run("SOLID", new BrushedStyle
                                                  {
                                                      Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
                                                  })
                                    .Build(),
            Columns = 14, PaintColumns = 14, PaintRows = 3,
            DocumentBrush = LeftToRight()
        },
        new()
        {
            Id = "brush-block-style-scope-centred",
            Description = "brush-block-style-scope with the paragraph CENTRED and the paint rect wider than "
                        + "the 12-column budget — the combination no earlier case has: brush-block-style-scope "
                        + "is Left-aligned (its old SamplingRect and its ink coincided) and "
                        + "align-center-brushed-wider-than-budget carries a document brush, not a block one. "
                        + "The block rung samples the walk's ink-anchored Extent, (4,0,12,1), so the ramp "
                        + "spans exactly the centred glyphs at columns 4-15 — the re-aim this case was "
                        + "appended to witness. As recorded one commit earlier it pinned the pre-re-aim "
                        + "bytes: the Left-forced SamplingRect covered columns 0-11, the leftmost glyph "
                        + "started 0.375 up the ramp and the last four clamped flat blue past its end. That "
                        + "rect is deleted; this section is the surviving half of defect 1, closed.",
            Document = static () => new RichTextBuilder()
                                    .Paragraph(alignment: TextAlignment.Center,
                                               style: new BrushedStyle { Foreground = LeftToRight() })
                                    .Run("xxxxxxxxxx")
                                    .Run("AB")
                                    .Build(),
            Columns = 12, PaintColumns = 20, PaintRows = 3, Brushed = true
        },
        new()
        {
            Id = "figlet-inline-run-brushed-gradient",
            Description = "figlet-inline-run-tagged-brush with the run's declared brush a GRADIENT instead of "
                        + "a solid — both tagged-brush cases are solids, which are rect-independent, so no "
                        + "earlier case can see the run leg's geometry on the FIGlet arm. Appended in 7b as "
                        + "the witness for the task-#15 fix, and re-recorded when the fix landed: a "
                        + "run-declared brush on a glyph run now samples the run's wrap-invariant "
                        + "reading-order strip (reading-order column, band top, TOTAL run width, band "
                        + "height — FormattedText.GlyphRunStrip), exactly as text runs do, so the declared "
                        + "gradient wins the run leg WITH its geometry: the face samples every glyph cell "
                        + "against the strip (3,0,6x4) and the Mini 'A' sweeps red-to-blue across its six "
                        + "columns. As first recorded this section pinned the pre-fix bytes — the context's "
                        + "inline scope was the 1×1 cell at the piece's anchor, every ink cell sat right of "
                        + "that rect and clamped past the ramp's end, and the whole glyph came out the flat "
                        + "END colour — and the resolver-null arm handed the face the PIECE rect, the two "
                        + "paths disagreeing about one document. The strip closed that disagreement; the "
                        + "wrapped pins appended alongside the fix witness the wrap-invariance the unwrapped "
                        + "strip here cannot (for an unwrapped run the strip and the piece rect coincide).",
            Document = static () => new RichTextBuilder()
                                    .Run("hi ")
                                    .Run("A", new GlyphSource(FigletFonts.Mini),
                                         new BrushedStyle { Foreground = LeftToRight() })
                                    .Run(" there")
                                    .Build(),
            Columns = 20, PaintColumns = 20, PaintRows = 5,
            DocumentBrush = new SolidColorBrush(Teal)
        },
        new()
        {
            Id = "figlet-run-brushed-gradient-wrapped",
            Description = "The task-#15 fix's central property, pinned: a WRAPPED figlet run with a "
                        + "run-declared horizontal gradient. 'A B' in Mini at an 8-column budget wraps into "
                        + "two 4-row bands — 'A' (width 6, logical 0-5) and 'B' (width 5, logical 8-12; the "
                        + "space, logical 6-7, is consumed by the wrap) — and the run's strip spans its "
                        + "TOTAL width (13), rebased per piece (band 1 samples (0,0,13x4), band 2 "
                        + "(-8,4,13x4)), so the ramp CONTINUES across the wrap in reading order instead of "
                        + "restarting per piece: band 2's ink picks up blue-dominant past band 1's "
                        + "red-dominant tail. Under piece-rect sampling band 2 would restart near red "
                        + "(t=0.5/5); under the pre-fix 1×1 anchor rect both bands were flat end-colour "
                        + "blue. The resolver path's half of the pair; its resolver-null twin below must "
                        + "hold byte-identical ink.",
            Document = static () => new RichTextBuilder()
                                    .Run("A B", new GlyphSource(FigletFonts.Mini),
                                         new BrushedStyle { Foreground = LeftToRight() })
                                    .Build(),
            Columns = 8, PaintColumns = 8, PaintRows = 8,
            DocumentBrush = new SolidColorBrush(Teal)
        },
        new()
        {
            Id = "figlet-run-brushed-gradient-wrapped-null-resolver",
            Description = "figlet-run-brushed-gradient-wrapped with NO resolver installed — the FIGlet arm's "
                        + "resolver-null path, which the task-#15 ruling changed to match (option (b)'s "
                        + "stated cost): the face is handed the run's strip, not the piece rect. The ink "
                        + "bytes here must be IDENTICAL to the resolver case above — the two paths agreeing "
                        + "about one document is the closure of the disagreement "
                        + "figlet-inline-run-brushed-gradient originally pinned. Before the fix this "
                        + "path sampled the PIECE rect, so band 2 restarted its ramp near red while the "
                        + "resolver path painted flat blue.",
            Document = static () => new RichTextBuilder()
                                    .Run("A B", new GlyphSource(FigletFonts.Mini),
                                         new BrushedStyle { Foreground = LeftToRight() })
                                    .Build(),
            Columns = 8, PaintColumns = 8, PaintRows = 8
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

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
        {
            // The delta resolves per painted cell over its bounds — the painter hands content ONE
            // centre-sampled value restated as a uniform carrier, so this reproduces that value
            // on every cell exactly as the CellStyle hand-off did.
            for (int i = 0; i < Math.Min(width, bounds.Columns); i++)
                buffer.Set(bounds.Column + i, bounds.Row, glyph,
                           style.Resolve(bounds.Column + i, bounds.Row, bounds).ApplyTo(CellStyle.Default));

            return new Rect(bounds.Column, bounds.Row, Math.Min(width, bounds.Columns), 1);
        }
    }
}
