using System.Text;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// The block text painters under a <b>negative</b> push translate — the primitive-level form of the
/// <c>ClipToBounds</c> no-op property (<c>ClipToBoundsRenderEquivalenceTests</c> in
/// <c>Cursorial.UI.Tests</c>).
/// <para>
/// A negative translate makes <c>DrawingContext.MappedSurface</c> hand the painter a view re-based by
/// <see cref="CellBufferView.WithOrigin"/>: the same backing window, but the local origin moved left /
/// up. The addressable local range is then <c>[LocalColumnStart, LocalColumnEnd)</c> — for
/// <c>Dx = −n</c> that is <c>[n, n + Columns)</c>, so the run's leading cells are at
/// <em>non-negative</em> local coordinates that the view still rejects. Two ways to get this wrong,
/// both of which used to drop the entire run:
/// </para>
/// <list type="number">
/// <item>advancing the paint cursor by <c>Set</c>'s return — <c>0</c> for a rejected cell — which
/// stalls the cursor on the first clipped column forever;</item>
/// <item>testing the right edge against <c>Columns</c> rather than <c>LocalColumnEnd</c>, which
/// truncates the run <c>n</c> columns early.</item>
/// </list>
/// <para>
/// The contract each is measured against is the per-cell <c>DrawingContext.Set</c> leg, which has
/// always been negative-capable: identical inputs must produce identical cells.
/// </para>
/// </summary>
public class NegativeTranslateBlockTextTests
{
    private const string Run = "ABCDEFGHIJKLMNOPQRSTUVWX";   // 24 pairwise-distinct single-width cells

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3)]
    [InlineData(-6)]
    public void FormattedText_AtANegativeTranslate_PaintsTheVisibleTail(int dx)
    {
        var expected = PerCellReference(dx);

        var actual = Row(20, ctx =>
        {
            using (ctx.PushTranslate(dx, 0))
                ctx.DrawFormattedText(Format(Run), new Rect(0, 0, Run.Length, 1), OutputCapabilities.None);
        });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3)]
    [InlineData(-6)]
    public void MonospaceFont_AtANegativeTranslate_PaintsTheVisibleTail(int dx)
    {
        var expected = PerCellReference(dx);

        var actual = Row(20, ctx =>
        {
            using (ctx.PushTranslate(dx, 0))
                ctx.DrawGlyphText(MonospaceFont.Default, 0, 0, Run, default(PartialStyle));
        });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3)]
    [InlineData(-6)]
    public void FigletFont_AtANegativeTranslate_PaintsTheVisibleTail(int dx)
    {
        // Three glyphs × 3 columns = a 9×3 block of pairwise-distinct ink.
        var font = Face();
        var expected = Grid(12, 3, ctx =>
        {
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 9; column++)
                ctx.Set(column + dx, row, FigletCell(column, row), CellStyle.Default);
        });

        var actual = Grid(12, 3, ctx =>
        {
            using (ctx.PushTranslate(dx, 0))
                ctx.DrawGlyphText(font, 0, 0, "ABC", default(PartialStyle));
        });

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The vertical twin: a FIGlet block whose top rows are pushed above the surface still paints the
    /// rows that remain on it. The block used to be discarded whole, because a negative anchor row
    /// short-circuited <c>PaintCore</c> before any line was considered.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    public void FigletFont_AtANegativeRowTranslate_PaintsTheVisibleRows(int dy)
    {
        var font = Face();
        var expected = Grid(12, 3, ctx =>
        {
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 9; column++)
                ctx.Set(column, row + dy, FigletCell(column, row), CellStyle.Default);
        });

        var actual = Grid(12, 3, ctx =>
        {
            using (ctx.PushTranslate(0, dy))
                ctx.DrawGlyphText(font, 0, 0, "ABC", default(PartialStyle));
        });

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The right edge, measured against the <b>local</b> window rather than <c>Columns</c>: at
    /// <c>Dx = −6</c> the addressable range is <c>[6, 26)</c> on a 20-column surface, so a 24-cell run
    /// fills the surface end to end. Testing against <c>Columns</c> instead stops at local column 20
    /// and leaves the last 6 surface columns blank.
    /// </summary>
    [Theory]
    [InlineData("formatted")]
    [InlineData("monospace")]
    public void ARunLongerThanTheSurface_FillsItToTheRightEdge(string painter)
    {
        var actual = Row(20, ctx =>
        {
            using (ctx.PushTranslate(-6, 0))
            {
                if (painter == "formatted")
                    ctx.DrawFormattedText(Format(Run), new Rect(0, 0, Run.Length, 1), OutputCapabilities.None);
                else
                    ctx.DrawGlyphText(MonospaceFont.Default, 0, 0, Run, default(PartialStyle));
            }
        });

        Assert.Equal("GHIJKLMNOPQRSTUVWX..", actual);
    }

    /// <summary>
    /// The stall was width-independent, not a "wholly off-surface" early bail: <b>one</b> clipped
    /// leading cell used to swallow the whole run.
    /// </summary>
    [Fact]
    public void ASingleClippedLeadingCell_DoesNotSwallowTheRun()
    {
        var actual = Row(20, ctx =>
        {
            using (ctx.PushTranslate(-1, 0))
                ctx.DrawFormattedText(Format("ABCDEF"), new Rect(0, 0, 6, 1), OutputCapabilities.None);
        });

        Assert.Equal("BCDEF...............", actual);
    }

    // ───────────────────────────────── harness ─────────────────────────────────

    private static FormattedText Format(string text)
        => new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.None }
           .Format(new RichTextBuilder().Run(text, default(PartialStyle)).Build(), text.Length);

    /// <summary>The always-correct leg: the same cells placed one at a time through the translate.</summary>
    private static string PerCellReference(int dx)
        => Row(20, ctx =>
        {
            for (var column = 0; column < Run.Length; column++)
                ctx.Set(column + dx, 0, Run[column].ToString(), CellStyle.Default);
        });

    private static string Row(int columns, Action<DrawingContext> draw) => Grid(columns, 1, draw)[0..columns];

    private static string Grid(int columns, int rows, Action<DrawingContext> draw)
    {
        var buffer = DrawHarness.Render(columns, rows, draw);
        var text = new StringBuilder(columns * rows);
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var grapheme = buffer[column, row].Grapheme;
            text.Append(string.IsNullOrEmpty(grapheme) ? '.' : grapheme[0]);
        }

        return text.ToString();
    }

    private static string FigletCell(int column, int row) => Body(column / 3)[row][column % 3].ToString();

    private static string[] Body(int glyph) => glyph switch
    {
        0 => ["/#\\", "|=|", "*-*"],
        1 => ["<1>", "(2)", "[3]"],
        _ => ["{4}", "%5%", "~6~"]
    };

    private static FigletFont Face()
        => new("negative-translate", '$', 3, FigletLayoutMode.None,
               new Dictionary<uint, FigletGlyph>
               {
                   ['A'] = new('A', Body(0), '$'),
                   ['B'] = new('B', Body(1), '$'),
                   ['C'] = new('C', Body(2), '$')
               });
}
