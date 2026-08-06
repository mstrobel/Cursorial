using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// The trim indicator must be something the PAINTING FACE can actually draw (maintainer report,
/// 2026-08). Trimming appended <see cref="TextFormatter.DefaultEllipsis"/> unconditionally; FIGlet
/// fonts have no U+2026 glyph and their missing-glyph rule is "blank gap", so a trimmed headline
/// rendered its truncation signal as NOTHING — truncated text was visually identical to complete
/// text. The formatter now asks the face (<see cref="IGlyphFont.HasGlyph"/>) and falls back to
/// <see cref="TextFormatter.DerivedEllipsis"/>, whose '.' is inside the ASCII range every FIGlet
/// font must define.
/// </summary>
/// <remarks>
/// Every "this face lacks U+2026" assertion here uses a HAND-BUILT face on purpose: the bundled
/// faces carry authored ellipsis art (an optimization over the derived form), so pinning the
/// fallback against them would pin the art instead. The derived path has to hold for
/// user-supplied fonts this repo will never see.
/// </remarks>
public class TrimIndicatorTests
{
    private const uint HorizontalEllipsis = 0x2026;

    /// <summary>
    /// A 3-row face shaped like every real FIGlet font: '.' present (required ASCII), U+2026
    /// absent. Unkerned, so widths are plain sums — '.' is 2 cells, a letter 3, the space glyph 2.
    /// </summary>
    private static FigletFont BuildAsciiOnlyFace()
    {
        var glyphs = new Dictionary<uint, FigletGlyph>
                     {
                         [' '] = new(' ', ["  ", "  ", "  "], '$'),
                         ['.'] = new('.', ["  ", "  ", ".."], '$'),
                         ['A'] = new('A', [" A ", "/_\\", "A A"], '$'),
                         ['B'] = new('B', ["BBB", "B B", "BBB"], '$'),
                     };

        return new FigletFont("ascii-only", '$', 3, FigletLayoutMode.None, glyphs);
    }

    /// <summary>A decorative face with neither U+2026 nor '.' — no candidate is renderable.</summary>
    private static FigletFont BuildRepertoirelessFace()
    {
        var glyphs = new Dictionary<uint, FigletGlyph>
                     {
                         [' '] = new(' ', ["  ", "  "], '$'),
                         ['A'] = new('A', ["AA", "AA"], '$'),
                         ['B'] = new('B', ["BB", "BB"], '$'),
                     };

        return new FigletFont("repertoireless", '$', 2, FigletLayoutMode.None, glyphs);
    }

    // ---- IGlyphFont.HasGlyph ----

    [Fact]
    public void Monospace_ClaimsEveryCodepoint()
    {
        // The pass-through face hands the cluster to the terminal — the terminal's font decides,
        // so "yes" is the honest answer here.
        Assert.True(MonospaceFont.Default.HasGlyph(HorizontalEllipsis));
        Assert.True(MonospaceFont.Default.HasGlyph('A'));
    }

    /// <summary>An external face written before <see cref="IGlyphFont.HasGlyph"/> existed —
    /// implements only the members that were required then.</summary>
    private sealed class LegacyExternalFace : IGlyphFont
    {
        public string DisplayName => "Legacy";
        public CellStyle EnsureCompatibleStyle(in CellStyle style) => style;
        public Size Measure(ReadOnlySpan<char> text) => new(text.Length, 1);

        public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style)
            => Measure(text);
    }

    [Fact]
    public void InterfaceDefault_KeepsExternalImplementorsSourceCompatible()
    {
        // The member is DEFAULTED so a face that never heard of it still compiles — and answers
        // optimistically, which is the behavior it had by omission.
        IGlyphFont legacy = new LegacyExternalFace();

        Assert.True(legacy.HasGlyph(HorizontalEllipsis));
    }

    [Fact]
    public void FigletFace_ReportsItsOwnRepertoire()
    {
        var face = BuildAsciiOnlyFace();

        Assert.False(face.HasGlyph(HorizontalEllipsis));
        Assert.True(face.HasGlyph('.'));
        Assert.False(face.HasGlyph('Z'));
    }

    [Fact]
    public void ShadowedFont_ReportsTheInnerRepertoire_NotTheOptimisticDefault()
    {
        // THE DECORATOR TRAP: a wrapper that inherits `=> true` claims every codepoint while
        // wrapping a face that has none of them, and the invisible-indicator bug returns with the
        // fix apparently in place.
        var inner = BuildAsciiOnlyFace();
        var shadowed = new ShadowedFont(inner);

        Assert.False(shadowed.HasGlyph(HorizontalEllipsis));
        Assert.True(shadowed.HasGlyph('.'));
        Assert.True(shadowed.HasGlyph('A'));
        Assert.False(shadowed.HasGlyph('Z'));
    }

    [Fact]
    public void DecoratedFont_ReportsTheInnerRepertoire_NotTheOptimisticDefault()
    {
        var inner = BuildAsciiOnlyFace();
        var decorated = new DecoratedFont("Underlined", inner, new Rune('▔'));

        Assert.False(decorated.HasGlyph(HorizontalEllipsis));
        Assert.True(decorated.HasGlyph('.'));
        Assert.False(decorated.HasGlyph('Z'));
    }

    [Fact]
    public void NestedDecorators_ForwardAllTheWayDown()
    {
        var stack = new ShadowedFont(new DecoratedFont("d", BuildAsciiOnlyFace(), new Rune('_')));

        Assert.False(stack.HasGlyph(HorizontalEllipsis));
        Assert.True(stack.HasGlyph('.'));
    }

    [Fact]
    public void DecoratorsOverMonospace_StillClaimEverything()
    {
        // Forwarding must not make the common path pessimistic — the identity's answer flows through.
        Assert.True(new ShadowedFont(MonospaceFont.Default).HasGlyph(HorizontalEllipsis));
        Assert.True(DecoratedFont.HalfBlockUnderline.HasGlyph(HorizontalEllipsis));
    }

    // ---- Selection through the formatter ----

    private static FormattedParagraph FormatFiglet(IGlyphFont face, string text, int columns,
                                                   TextFormatter? formatter = null)
    {
        var tf = formatter ?? new TextFormatter
                              {
                                  Wrap = WrapMode.NoWrap,
                                  Trim = TextTrimming.CharacterEllipsis
                              };

        var rt = new RichTextBuilder().Figlet(text, face).Build();
        var ft = tf.Format(rt, columns, capabilities: OutputCapabilities.None);
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    private static FormattedTextRun LastRun(FormattedLine line)
        => Assert.IsType<FormattedTextRun>(line.Runs[^1]);

    [Fact]
    public void TrimmedFiglet_PicksTheDerivedEllipsis()
    {
        var face = BuildAsciiOnlyFace();
        var p = FormatFiglet(face, "AAAAAA", columns: 12);

        var line = Assert.Single(p.Lines);
        Assert.True(line.Trimmed);

        var indicator = LastRun(line);
        Assert.Equal(TextFormatter.DerivedEllipsis, indicator.Text);
        Assert.Same(face, indicator.Source.Font); // painted BY THE FACE, at the face's height
    }

    [Fact]
    public void TrimmedFiglet_PaintsVisibleInk_ForTheIndicator()
    {
        // The bug in cell terms: the trimmed line's tail columns were all blank, so a truncated
        // headline was indistinguishable from a complete one. Assert PAINTED CELLS, not strings.
        var face = BuildAsciiOnlyFace();
        const int columns = 12;

        // The defect, reproduced directly — the face draws U+2026 as nothing at all.
        var naive = new CellBuffer(columns, face.Height);
        face.Paint(naive, 0, 0, "…", CellStyle.Default);
        Assert.Equal(0, InkCount(naive, 0, columns, face.Height));

        var p = FormatFiglet(face, "AAAAAA", columns);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);
        int indicatorStart = line.Columns - indicator.CellWidth;

        var buffer = Paint(p, columns);

        Assert.True(InkCount(buffer, indicatorStart, line.Columns, p.Size.Rows) > 0,
                    "the trim indicator painted no ink — truncation is invisible again");
    }

    [Fact]
    public void TrimmedFiglet_BudgetsTheChosenIndicator_NotTheConfiguredOne()
    {
        // "..." is WIDER than the un-renderable "…" (which measures as the space-glyph fallback).
        // A budget computed from the un-chosen candidate overflows the line by the difference.
        var face = BuildAsciiOnlyFace();
        const int columns = 12;

        Assert.Equal(6, face.Measure(TextFormatter.DerivedEllipsis).Columns);
        Assert.Equal(2, face.Measure(TextFormatter.DefaultEllipsis).Columns); // the blank fallback

        var p = FormatFiglet(face, "AAAAAA", columns);
        var line = Assert.Single(p.Lines);

        // 12 − 6 leaves room for two 3-cell letters. Budgeting the un-renderable "…" at 2 cells
        // would have kept three letters and landed the line at 9 + 6 = 15.
        Assert.Equal("AA", ((FormattedTextRun) line.Runs[0]).Text);
        Assert.Equal(columns, line.Columns);
        Assert.Equal(6, LastRun(line).CellWidth);
    }

    [Fact]
    public void TrimmedFiglet_NeverPaintsPastTheBudget()
    {
        var face = BuildAsciiOnlyFace();
        const int columns = 13;

        var p = FormatFiglet(face, "AAAAAAAA", columns);
        var line = Assert.Single(p.Lines);
        Assert.True(line.Columns <= columns, $"line is {line.Columns} cells in a {columns}-column budget");

        var buffer = Paint(p, columns + 8);
        Assert.Equal(0, InkCount(buffer, columns, columns + 8, p.Size.Rows));
    }

    [Fact]
    public void WordEllipsisTrim_AlsoPicksARenderableIndicator()
    {
        var face = BuildAsciiOnlyFace();
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.WordEllipsis };

        var p = FormatFiglet(face, "AA BB AA", columns: 14, tf);
        var line = Assert.Single(p.Lines);

        Assert.True(line.Trimmed);
        Assert.Equal(TextFormatter.DerivedEllipsis, LastRun(line).Text);
        Assert.True(line.Columns <= 14, $"line is {line.Columns} cells in a 14-column budget");
    }

    [Fact]
    public void CustomEllipsis_GetsTheSameTreatment()
    {
        // Ellipsis is author-settable; an un-renderable custom value derives too.
        var face = BuildAsciiOnlyFace();
        Assert.False(face.HasGlyph('»'));

        var tf = new TextFormatter
                 {
                     Wrap = WrapMode.NoWrap,
                     Trim = TextTrimming.CharacterEllipsis,
                     Ellipsis = "»"
                 };

        var p = FormatFiglet(face, "AAAAAA", columns: 12, tf);
        Assert.Equal(TextFormatter.DerivedEllipsis, LastRun(Assert.Single(p.Lines)).Text);
    }

    [Fact]
    public void CustomEllipsis_TheFaceCanDraw_IsKept()
    {
        // The rule is "first candidate the face can render", not "always derive": a custom value
        // inside the face's repertoire wins over the fallback, at its own width.
        var face = BuildAsciiOnlyFace();
        Assert.True(face.HasGlyph('B'));

        var tf = new TextFormatter
                 {
                     Wrap = WrapMode.NoWrap,
                     Trim = TextTrimming.CharacterEllipsis,
                     Ellipsis = "B"
                 };

        var p = FormatFiglet(face, "AAAAAA", columns: 12, tf);
        var line = Assert.Single(p.Lines);

        Assert.Equal("B", LastRun(line).Text);
        Assert.Equal(face.Measure("B").Columns, LastRun(line).CellWidth);
    }

    [Fact]
    public void EmptyEllipsis_IsRespectedAsAnExplicitRequestForNoIndicator()
    {
        var tf = new TextFormatter
                 {
                     Wrap = WrapMode.NoWrap,
                     Trim = TextTrimming.CharacterEllipsis,
                     Ellipsis = ""
                 };

        var p = FormatFiglet(BuildAsciiOnlyFace(), "AAAAAA", columns: 12, tf);
        var line = Assert.Single(p.Lines);

        Assert.True(line.Trimmed);
        Assert.Equal("", LastRun(line).Text);
    }

    // ---- Last resort: a face with NO renderable candidate ----

    [Fact]
    public void FaceWithoutAnyCandidate_FallsBackToTheMonospaceIdentity()
    {
        var face = BuildRepertoirelessFace();
        Assert.False(face.HasGlyph(HorizontalEllipsis));
        Assert.False(face.HasGlyph('.'));

        var p = FormatFiglet(face, "AAAAAAAA", columns: 9);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);

        Assert.True(line.Trimmed);
        Assert.Equal(TextFormatter.DefaultEllipsis, indicator.Text);
        Assert.Null(indicator.Source.Font);   // painted by the TERMINAL's font, one cell
        Assert.Equal(1, indicator.CellWidth);
        Assert.True(line.Columns <= 9, $"line is {line.Columns} cells in a 9-column budget");
    }

    [Fact]
    public void FaceWithoutAnyCandidate_StillPaintsVisibleInk()
    {
        // "Never render nothing": the last resort must produce cells, not an invisible run.
        var face = BuildRepertoirelessFace();
        const int columns = 9;

        var p = FormatFiglet(face, "AAAAAAAA", columns);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);

        var buffer = Paint(p, columns);
        int start = line.Columns - indicator.CellWidth;

        Assert.True(InkCount(buffer, start, line.Columns, p.Size.Rows) > 0,
                    "the last-resort indicator painted no ink");
    }

    // ---- Every bundled face, end to end ----

    public static TheoryData<string> BundledFaceNames =>
    [
        "Standard", "Slant", "Small", "Big", "Mini", "AnsiShadow", "Hp2640LargeType",
        "MiniWi", "CGA", "LCDMatrix", "LED", "Roman", "SmallSlant"
    ];

    private static FigletFont BundledFace(string name)
        => (FigletFont) typeof(FigletFonts).GetProperty(name)!.GetValue(null)!;

    [Theory]
    [MemberData(nameof(BundledFaceNames))]
    public void EveryBundledFace_TrimsToAVisibleIndicator(string name)
    {
        // Whether the face draws authored U+2026 art or the derived "...", the outcome the user
        // sees is the same contract: trimmed text is visibly marked as trimmed, inside the budget.
        var face = BundledFace(name);
        const int columns = 40;

        var p = FormatFiglet(face, new string('M', 80), columns);
        var line = Assert.Single(p.Lines);
        var indicator = LastRun(line);

        Assert.True(line.Trimmed);
        Assert.True(line.Columns <= columns, $"{name}: line is {line.Columns} cells in a {columns}-column budget");
        Assert.NotEqual(0, indicator.CellWidth);

        var buffer = Paint(p, columns);
        int start = line.Columns - indicator.CellWidth;
        Assert.True(InkCount(buffer, start, line.Columns, p.Size.Rows) > 0,
                    $"{name}: the trim indicator painted no ink");
    }

    // ---- The common path must be untouched ----

    [Fact]
    public void MonospaceTrimming_IsUnchanged()
    {
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                      .Run("abcdefghij").Build();

        var ft = tf.Format(rt, 5, capabilities: OutputCapabilities.None);
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var line = Assert.Single(p.Lines);

        Assert.True(line.Trimmed);
        Assert.Equal(5, line.Columns);
        Assert.Equal("abcd", ((FormattedTextRun) line.Runs[0]).Text);
        Assert.Equal(TextFormatter.DefaultEllipsis, LastRun(line).Text); // still U+2026, still one cell
        Assert.Equal(1, LastRun(line).CellWidth);
        Assert.True(LastRun(line).Source.PaintsAsCells);
    }

    [Fact]
    public void MonospaceWordEllipsisTrimming_IsUnchanged()
    {
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.WordEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.WordEllipsis)
                                      .Run("alpha beta gamma").Build();

        var ft = tf.Format(rt, 8, capabilities: OutputCapabilities.None);
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var line = Assert.Single(p.Lines);

        Assert.Equal("alpha", ((FormattedTextRun) line.Runs[0]).Text);
        Assert.Equal(TextFormatter.DefaultEllipsis, LastRun(line).Text);
        Assert.Equal(6, line.Columns);
    }

    [Fact]
    public void ScaledTextTrimming_IsUnchanged()
    {
        // OSC 66 cells are drawn by the TERMINAL's font, so a sized run never derives — it keeps
        // U+2026 at its own scale.
        var caps = OutputCapabilities.None with { TextSizing = new TextSizingCapabilities(Width: true, Scale: true) };
        var big = new GlyphSource(null, new TextSizing(Scale: 2));
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                      .Run("ABCDE", big).Build();

        var ft = tf.Format(rt, 7, capabilities: caps);
        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var line = Assert.Single(p.Lines);

        Assert.Equal(TextFormatter.DefaultEllipsis, LastRun(line).Text);
        Assert.Equal(2, LastRun(line).CellWidth);
        Assert.Equal(6, line.Columns);
    }

    // ---- Helpers ----

    private static CellBuffer Paint(FormattedParagraph p, int columns)
    {
        int rows = Math.Max(1, p.Size.Rows);
        var buffer = new CellBuffer(columns, rows);

        ((IContent) new FormattedText([p], p.Size, columns)).Paint(
            buffer.AsView(), new Rect(0, 0, columns, rows), default, OutputCapabilities.None);

        return buffer;
    }

    /// <summary>Cells carrying visible (non-blank) ink in the half-open column range.</summary>
    private static int InkCount(CellBuffer buffer, int startColumn, int endColumn, int rows)
    {
        int count = 0;

        for (int r = 0; r < rows && r < buffer.Rows; r++)
        for (int c = Math.Max(0, startColumn); c < endColumn && c < buffer.Columns; c++)
        {
            if (buffer[c, r].Grapheme is { Length: > 0 } g && g.Trim().Length > 0)
                count++;
        }

        return count;
    }
}
