// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// <b>The <c>ClipToBounds</c> no-op property.</b> An element whose content exactly fits its bounds
/// must render <em>identically</em> with and without <see cref="UIElement.ClipToBounds"/> —
/// clipping something that does not overflow is by definition a no-op.
/// <para>
/// The property has teeth because the flag switches paint <b>paths</b>: setting it promotes the
/// element to a render boundary, so it rasters into its <em>own</em> zone scene at origin
/// <c>(0, 0)</c> and reaches the window through <c>SceneCompositor</c>'s
/// <c>CompositeParameters.OffsetColumn</c>; clearing it leaves the element inline in its parent's
/// zone, painting through a <c>PushTranslate</c> that re-bases the surface origin
/// (<c>CellBufferView.WithOrigin</c>). A negatively-margined element makes both of those offsets
/// negative, and the two legs must still agree cell for cell.
/// </para>
/// <para>
/// Pinned across five paint primitives so the property is independent of any one painter, and at
/// four placements — including one where the element genuinely starts left of / above the window
/// (a negative <em>window</em> offset, where losing the overhang is correct in <b>both</b> legs)
/// and one where the element is wider than the zone it paints into.
/// </para>
/// </summary>
public sealed class ClipToBoundsRenderEquivalenceTests
{
    private const int WindowColumns = 28;
    private const int WindowRows = 10;

    private static readonly Color Ink = Color.FromRgb(220, 224, 240);

    /// <summary>Four rows of pairwise-distinct glyphs — every lost cell is identifiable.</summary>
    private static readonly string[] Content =
    [
        "ABCDEFGHIJKL",
        "abcdefghijkl",
        "0123456789+-",
        "MNOPQRSTUVWX"
    ];

    public enum Paint
    {
        /// <summary>Per-cell <c>RenderContext.Set</c> — the control leg (known negative-capable).</summary>
        Cells,

        /// <summary><c>RenderContext.DrawText</c> — the immediate single-row text primitive.</summary>
        Text,

        /// <summary><c>RenderContext.DrawFormattedText</c> — the laid-out document block (TextBlock's path).</summary>
        Formatted,

        /// <summary><c>DrawGlyphText</c> through the identity face — <c>MonospaceFont</c>.</summary>
        Monospace,

        /// <summary><c>DrawGlyphText</c> through a FIGlet face — the multi-row block painter.</summary>
        Figlet
    }

    /// <summary>
    /// (slotColumn, slotRow, marginLeft, marginTop) — where the element's slot sits and how far its
    /// margin pulls it out of that slot. The element's window offset is the sum.
    /// </summary>
    public static TheoryData<Paint, int, int, int, int> Placements()
    {
        var data = new TheoryData<Paint, int, int, int, int>();
        foreach (var paint in Enum.GetValues<Paint>())
        {
            data.Add(paint, 0, 0, 0, 0);      // control: no pull at all
            data.Add(paint, 10, 3, -6, 0);    // pulled left, still well inside the window (offset +4)
            data.Add(paint, 10, 3, -6, -2);   // pulled left AND up (offset (+4, +1))
            data.Add(paint, 0, 0, -6, 0);     // pulled off the window's LEFT edge (offset -6)
            data.Add(paint, 0, 0, -6, -2);    // pulled off the left AND top edges (offset (-6, -2))
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void ClipToBounds_OnAnElementThatFitsItsBounds_ChangesNothing(
        Paint paint, int slotColumn, int slotRow, int marginLeft, int marginTop)
    {
        var plain = Render(paint, slotColumn, slotRow, marginLeft, marginTop, clipToBounds: false);
        var clipped = Render(paint, slotColumn, slotRow, marginLeft, marginTop, clipToBounds: true);

        AssertGridsEqual(plain, clipped);
    }

    /// <summary>
    /// The same property where the element is <b>wider than the zone it paints into</b>: pulled 6
    /// columns off the window's left edge, its right end still reaches past the zone's right edge.
    /// A painter that treats <c>CellBufferView.Columns</c> as its local right edge truncates here,
    /// because a re-based view's addressable range is <c>[Columns − 6, 2 · Columns − 6)</c>, not
    /// <c>[0, Columns)</c>.
    /// </summary>
    [Theory]
    [InlineData(Paint.Cells)]
    [InlineData(Paint.Text)]
    [InlineData(Paint.Formatted)]
    [InlineData(Paint.Monospace)]
    public void ClipToBounds_OnAnElementWiderThanItsZone_ChangesNothing(Paint paint)
    {
        // 30 columns of content in a 28-column window, pulled 6 columns left: element-local columns
        // 6..29 are on screen (window columns 0..23) and 24..29 sit past the ZONE's right edge.
        var plain = Render(paint, 0, 0, -6, 0, clipToBounds: false, columns: 30);
        var clipped = Render(paint, 0, 0, -6, 0, clipToBounds: true, columns: 30);

        AssertGridsEqual(plain, clipped);
    }

    /// <summary>
    /// The guard that gives the equivalence its teeth: the two arms really do take the two different
    /// paint paths. Without it a regression that stopped promoting on <c>ClipToBounds</c> would make
    /// every assertion above vacuous.
    /// </summary>
    [Fact]
    public void ClipToBounds_PromotesToARenderBoundary_SoTheTwoArmsTakeDifferentPaths()
    {
        var (plainProbe, _) = Build(Paint.Cells, 0, 0, -6, 0, clipToBounds: false, columns: 12);
        var (clippedProbe, _) = Build(Paint.Cells, 0, 0, -6, 0, clipToBounds: true, columns: 12);

        using (var host = UIHeadlessHost.Create(Options()))
        {
            host.ShowRoot((UIElement) plainProbe.VisualParent!);
            Assert.True(host.RunUntilIdle());
            Assert.False(ReferenceEquals(plainProbe.ZoneRoot, plainProbe));   // inline, in its parent's zone
        }

        using (var host = UIHeadlessHost.Create(Options()))
        {
            host.ShowRoot((UIElement) clippedProbe.VisualParent!);
            Assert.True(host.RunUntilIdle());
            Assert.True(ReferenceEquals(clippedProbe.ZoneRoot, clippedProbe)); // its own zone
        }
    }

    /// <summary>
    /// The arranged geometry the equivalence rests on, asserted rather than assumed: a negative
    /// margin moves the element's <see cref="Rect"/> origin, and at slot 0 that origin —
    /// and therefore the accumulated <em>window</em> offset — is genuinely negative.
    /// </summary>
    [Theory]
    [InlineData(0, 0, -6, 0, -6, 0)]
    [InlineData(10, 3, -6, 0, 4, 3)]
    [InlineData(10, 3, -6, -2, 4, 1)]
    public void NegativeMargin_MovesTheArrangedOrigin(
        int slotColumn, int slotRow, int marginLeft, int marginTop, int expectedColumn, int expectedRow)
    {
        var (probe, root) = Build(Paint.Cells, slotColumn, slotRow, marginLeft, marginTop, false, 12);

        using var host = UIHeadlessHost.Create(Options());
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(expectedColumn, probe.Bounds.Column);
        Assert.Equal(expectedRow, probe.Bounds.Row);
    }

    // ───────────────────────────────── harness ─────────────────────────────────

    private static UIHeadlessHostOptions Options()
        => new() { InitialSize = new Size(WindowColumns, WindowRows) };

    private static (Probe Probe, UIElement Root) Build(
        Paint paint, int slotColumn, int slotRow, int marginLeft, int marginTop, bool clipToBounds, int columns)
    {
        var rows = paint is Paint.Figlet ? FigletRows : Content.Length;

        var probe = new Probe(columns, rows)
                    {
                        Margin = new Margins(marginLeft, marginTop, 0, 0),
                        ClipToBounds = clipToBounds,
                        OnRender = (_, context) => PaintContent(paint, context, columns, rows)
                    };

        var host = new SlotHost(probe)
                   {
                       SlotRect = new Rect(slotColumn, slotRow,
                                           WindowColumns - slotColumn, WindowRows - slotRow)
                   };

        return (probe, host);
    }

    private static string[] Render(
        Paint paint, int slotColumn, int slotRow, int marginLeft, int marginTop, bool clipToBounds,
        int columns = 12)
    {
        var (_, root) = Build(paint, slotColumn, slotRow, marginLeft, marginTop, clipToBounds, columns);

        using var host = UIHeadlessHost.Create(Options());
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var grid = new string[WindowRows];
        for (var row = 0; row < WindowRows; row++)
        {
            var line = new StringBuilder(WindowColumns);
            for (var column = 0; column < WindowColumns; column++)
            {
                var grapheme = host.GetCell(column, row).Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) ? '.' : grapheme[0]);
            }

            grid[row] = line.ToString();
        }

        return grid;
    }

    private static void PaintContent(Paint paint, RenderContext context, int columns, int rows)
    {
        switch (paint)
        {
            case Paint.Cells:
                for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                    context.Set(column, row, Glyph(row, column), CellStyle.Transparent.WithForeground(Ink));
                break;

            case Paint.Text:
                for (var row = 0; row < rows; row++)
                    context.DrawText(0, row, Line(row, columns), Ink);
                break;

            case Paint.Formatted:
                context.DrawFormattedText(FormatBlock(rows, columns), context.Bounds);
                break;

            case Paint.Monospace:
                for (var row = 0; row < rows; row++)
                    context.DrawGlyphText(MonospaceFont.Default, 0, row, Line(row, columns),
                                          foreground: null, CellStyle.Default.WithForeground(Ink), default);
                break;

            case Paint.Figlet:
                context.DrawGlyphText(Figlet, 0, 0, new string('A', columns / FigletWidth),
                                      foreground: null, CellStyle.Default.WithForeground(Ink), default);
                break;
        }
    }

    private static string Glyph(int row, int column) => Content[row % Content.Length][column % 12].ToString();

    private static string Line(int row, int columns)
    {
        var source = Content[row % Content.Length];
        var line = new StringBuilder(columns);
        for (var column = 0; column < columns; column++) line.Append(source[column % source.Length]);
        return line.ToString();
    }

    private static FormattedText FormatBlock(int rows, int columns)
    {
        var builder = new RichTextBuilder();
        for (var row = 0; row < rows; row++)
        {
            if (row > 0) builder.LineBreak();
            builder.Run(Line(row, columns), CellStyle.Default.WithForeground(Ink));
        }

        return new TextFormatter { Wrap = WrapMode.NoWrap, Trim = TextTrimming.None }
               .Format(builder.Build(), columns);
    }

    // A 3-row FIGlet face whose glyph body is all-distinct so nothing can be lost silently.
    private const int FigletWidth = 3;

    private const int FigletRows = 3;

    private static readonly FigletFont Figlet =
        new("equivalence", '$', FigletRows, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["   ", "   ", "   "], '$'),
                ['A'] = new('A', ["/#\\", "|=|", "*-*"], '$')
            });

    private static void AssertGridsEqual(string[] plain, string[] clipped)
    {
        if (plain.SequenceEqual(clipped)) return;

        var message = new StringBuilder();
        message.AppendLine("ClipToBounds changed the rendered cells — it must be a no-op here.");
        message.AppendLine();
        message.AppendLine("     no ClipToBounds              ClipToBounds = true");
        for (var row = 0; row < plain.Length; row++)
        {
            message.Append(plain[row] == clipped[row] ? "   " : " ≠ ")
                   .Append(row.ToString().PadLeft(2))
                   .Append(' ')
                   .Append(plain[row])
                   .Append(" │ ")
                   .Append(clipped[row])
                   .AppendLine();
        }

        Assert.Fail(message.ToString());
    }
}
