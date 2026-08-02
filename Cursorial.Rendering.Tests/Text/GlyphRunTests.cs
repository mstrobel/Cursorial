using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// Glyph runs (proposal-glyph-runs Phase 1): sized text and FIGlet as first-class styled runs.
/// A run's <see cref="GlyphSource"/> drives every advance through the shared pipeline — mixed
/// lines become bands (max run height), bands stack, runs align vertically within their band by
/// the paragraph rule, and non-cell runs paint per piece through the OSC-66-or-fallback tree.
/// </summary>
public class GlyphRunTests
{
    private static OutputCapabilities CapsWithScale()
        => OutputCapabilities.None with
           {
               TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
           };

    private static readonly GlyphSource Big2 = new(null, new TextSizing(Scale: 2));

    private static FormattedParagraph FormatOne(RichText rt, int columns, TextFormatter? tf = null)
    {
        // Scaled geometry requires a scale-capable terminal: sources RESOLVE against the
        // capabilities at layout time, so under OutputCapabilities.None a scaled run would
        // honestly fall back to its (bundled) face and measure at FACE metrics.
        var ft = (tf ?? new TextFormatter()).Format(rt, columns, capabilities: CapsWithScale());
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    // ---- Line geometry ----

    [Fact]
    public void MixedLine_IsOneBand_MaxOfRunHeights()
    {
        var rt = new RichTextBuilder().Paragraph().Run("tiny ").Run("BIG", Big2).Build();
        var p = FormatOne(rt, columns: 40);

        var line = Assert.Single(p.Lines);
        Assert.Equal(2, line.Rows);            // the band takes the tallest run
        Assert.Equal(2, p.Size.Rows);          // one band = two rows
        Assert.Equal(5 + 6, line.Columns);     // "tiny " at 1×, "BIG" at 2 cells/cluster
    }

    [Fact]
    public void WrappedMixedRuns_StackBands()
    {
        // "aaaa " (5 cells) + "XY" at scale 2 (4 cells) exceeds 8 columns — the sized word wraps
        // whole onto its own band. Line 1 is a 1-row band, line 2 a 2-row band; the paragraph is
        // their SUM, not the line count.
        var rt = new RichTextBuilder().Paragraph().Run("aaaa ").Run("XY", Big2).Build();
        var p = FormatOne(rt, columns: 8);

        Assert.Equal(2, p.Lines.Length);
        Assert.Equal(1, p.Lines[0].Rows);
        Assert.Equal(2, p.Lines[1].Rows);
        Assert.Equal(3, p.Size.Rows);
    }

    [Fact]
    public void SizedRun_WrapsAtItsOwnAdvances()
    {
        // A lone scaled word wider than the budget char-splits at SCALED cluster widths: 5
        // clusters × 2 cells = 10 > 6 → head takes 3 clusters (6 cells), tail wraps.
        var rt = new RichTextBuilder().Paragraph().Run("ABCDE", Big2).Build();
        var p = FormatOne(rt, columns: 6);

        Assert.Equal(2, p.Lines.Length);
        Assert.Equal(6, p.Lines[0].Columns);
        Assert.Equal(4, p.Lines[1].Columns);
        Assert.All(p.Lines, l => Assert.Equal(2, l.Rows));
    }

    [Fact]
    public void EllipsisJoiningASizedRun_MeasuresAtTheRunsScale()
    {
        // NoWrap + CharacterEllipsis: "ABCDE" at scale 2 is 10 cells in a 7-column budget. The
        // ellipsis joins the sized run, so it costs 2 cells (not 1): 7 − 2 = 5 → two whole
        // clusters (4 cells) survive, and the line lands at 6 cells, never 7+.
        var tf = new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.CharacterEllipsis };
        var rt = new RichTextBuilder().Paragraph(wrap: WrapMode.NoWrap, trim: TextTrimming.CharacterEllipsis)
                                      .Run("ABCDE", Big2).Build();
        var p = FormatOne(rt, columns: 7, tf);

        var line = Assert.Single(p.Lines);
        Assert.True(line.Trimmed);
        Assert.Equal(6, line.Columns);

        var ellipsis = Assert.IsType<FormattedTextRun>(line.Runs[^1]);
        Assert.Equal("…", ellipsis.Text);
        Assert.Same(Big2, ellipsis.Source); // measures AND paints at the joined run's source
    }

    // ---- Vertical alignment within the band ----

    private static string PaintRow(FormattedParagraph p, int columns, int row, VerticalTextAlignment _ = default)
    {
        var buffer = new CellBuffer(columns, p.Size.Rows);
        ((IContent)new FormattedText([p], p.Size, columns)).Paint(
            buffer.AsView(), new Rect(0, 0, columns, p.Size.Rows), default, CapsWithScale());

        var sb = new StringBuilder();
        for (int c = 0; c < columns; c++)
            sb.Append(buffer[c, row].Grapheme is { Length: > 0 } t ? t : " ");
        return sb.ToString();
    }

    private static FormattedParagraph FormatAligned(VerticalTextAlignment alignment)
    {
        var rt = new RichTextBuilder()
                .Paragraph()
                .Run("lo ").Run("HI", Big2)
                .Build();

        var paragraph = (TextParagraph)rt.Blocks[0] with { VerticalAlignment = alignment };
        var ft = new TextFormatter().Format(new RichText([paragraph]), 20, capabilities: CapsWithScale());
        return Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
    }

    [Fact]
    public void IdentityRuns_SitOnTheBottomRow_ByDefault()
    {
        var p = FormatAligned(default); // Bottom is the enum default by declaration order

        Assert.Equal(VerticalTextAlignment.Bottom, p.VerticalAlignment);
        Assert.StartsWith("lo ", PaintRow(p, 20, row: 1)); // bottom row of the 2-row band
        Assert.StartsWith("   ", PaintRow(p, 20, row: 0)); // top row empty under the identity run
    }

    [Fact]
    public void TopAlignment_PutsIdentityRunsOnTheTopRow()
    {
        var p = FormatAligned(VerticalTextAlignment.Top);

        Assert.StartsWith("lo ", PaintRow(p, 20, row: 0));
        Assert.StartsWith("   ", PaintRow(p, 20, row: 1));
    }

    // ---- Painting / emission ----

    [Fact]
    public void SizedRunPiece_EmitsItsOwnOsc66Fragment()
    {
        // On a scale-capable terminal, a sized run's painted piece attaches a SizedTextFragment
        // at the piece's own anchor — the renderer emits it after the cell pass.
        var rt = new RichTextBuilder().Paragraph().Run("at ").Run("BIG", Big2).Build();
        var ft = new TextFormatter().Format(rt, 20, capabilities: CapsWithScale());

        var buffer = new CellBuffer(20, ft.Size.Rows);
        ((IContent)ft).Paint(buffer.AsView(), new Rect(0, 0, 20, ft.Size.Rows), default, CapsWithScale());

        var w = new ArrayBufferWriter<byte>();
        new FrameRenderer(CapsWithScale()).Render(buffer, w);
        var output = Encoding.UTF8.GetString(w.WrittenSpan);

        Assert.Contains("at", output);              // the identity piece renders as cells
        Assert.Contains("\x1b]66;s=2;BIG", output); // the sized piece rides OSC 66
    }

    [Fact]
    public void ScaledSource_OnADumbTerminal_ResolvesToItsFallbackFace()
    {
        // Tier parity with the old block path: under capabilities without OSC 66, a scaled run
        // resolves AT LAYOUT TIME to its fallback face (bundled when none is given), so the
        // paragraph measures at face metrics — a figlet headline, not clipped monospace.
        var rt = new RichTextBuilder().Paragraph().Run("HI", Big2).Build();
        var ft = new TextFormatter().Format(rt, 40, capabilities: OutputCapabilities.None);

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var run = Assert.IsType<FormattedTextRun>(Assert.Single(Assert.Single(p.Lines).Runs));
        Assert.NotNull(run.Source.Font);              // resolved to a face
        Assert.True(run.Source.Sizing.IsNormal);      // the unsupported sizing is gone
        Assert.True(p.Lines[0].Rows >= 2);            // face-height band, not 1-row monospace
    }

    [Fact]
    public void DocumentRowCap_DropsWholeBands()
    {
        // maxRows 2 over [1-row band, 2-row band]: the second band cannot HALF-fit — it drops
        // whole, and the surviving line reports the trim.
        var rt = new RichTextBuilder().Paragraph().Run("top").LineBreak().Run("BIG", Big2).Build();
        var ft = new TextFormatter().Format(rt, 20, maxRows: 2, capabilities: CapsWithScale());

        var p = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        Assert.Single(p.Lines);
        Assert.Equal(1, p.Size.Rows);
        Assert.True(ft.HasTrimmedLines);
    }
}
