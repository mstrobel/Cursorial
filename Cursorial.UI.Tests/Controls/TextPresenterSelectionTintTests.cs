// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// The one caller of <c>RenderContext.TintCells</c>: a glyph-face (FIGlet) <see cref="TextBox"/>
/// highlights its selection by RESTYLING the cells it already painted, so the face's geometry never
/// shifts. These assert the rendered frame, because the tint's contract lives in two places at once —
/// what the presenter asks for and what the drawing layer does with it — and only the frame sees both.
/// <para>
/// The migration from <c>CellStyle</c> to <c>PartialStyle</c> has to collapse two sentinel
/// conventions the old spelling relied on: an attribute set that meant "OR these on, having first
/// cleared Inverse" (so the same call was a SET on one path and a CLEAR on another), and a default
/// background that meant "said nothing".
/// </para>
/// </summary>
public sealed class TextPresenterSelectionTintTests
{
    private static readonly Color Selection = Color.FromRgb(180, 40, 200);

    private const int Columns = 24;
    private const int Rows = 8;

    /// <summary>A 2-row face, 3 columns per glyph, ink in every cell — so a selection rectangle over
    /// one glyph is exactly 3 × 2 cells and no cell of it is blank.</summary>
    private static readonly FigletFont Face =
        new("tint-tests", '$', 2, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["   ", "   "], '$'),
                ['A'] = new('A', ["/#\\", "|=|"], '$'),
                ['B'] = new('B', ["[#]", "|=|"], '$'),
            });

    private const int GlyphColumns = 3;
    private const int GlyphRows = 2;

    private static (UIHeadlessHost Host, TextBox Box) Shown(
        bool noColor, bool inverse, IBrush? selectionBrush, string text = "AB")
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(Columns, Rows),
            Capabilities = noColor ? HeadlessCapabilities.GenericVt : HeadlessCapabilities.KittyTruecolor,
        });

        var box = new TextBox
        {
            Text = text,
            Width = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SelectionFill = selectionBrush,
        };

        TextElement.SetFont(box, Face);

        // Set EITHER way, never conditionally: the default theme carries a
        // `:is(TextBox) RequiresCapabilities="NoColor"` rule that flips Inverse on, so `false` here is
        // a local value deliberately overriding it — which is the only way the noColor+not-inverse
        // leg of the tint is reachable at all.
        TextElement.SetInverse(box, inverse);

        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();
        return (host, box);
    }

    private static void SelectFirstGlyph(UIHeadlessHost host, TextBox box)
    {
        box.SelectionStart = 0;
        box.SelectionLength = 1;
        host.RunUntilIdle();
    }

    private static List<(int Column, int Row)> Cells(UIHeadlessHost host, Func<Cell, bool> match)
    {
        var found = new List<(int, int)>();
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
            if (match(host.GetCell(column, row)))
                found.Add((column, row));
        return found;
    }

    private static bool IsInverse(Cell cell) => cell.Style.Attributes.HasFlag(TextAttributes.Inverse);

    /// <summary>Renders <paramref name="host"/>'s frame as a per-cell map for assertion messages.</summary>
    private static string Map(UIHeadlessHost host, Func<Cell, bool> match)
    {
        var text = new System.Text.StringBuilder();
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
                text.Append(match(host.GetCell(column, row)) ? '#' : '.');
            text.Append('\n');
        }
        return text.ToString();
    }

    // ───────────────────────────── the colour path ─────────────────────────────

    [Fact]
    public void ColorSelection_PaintsExactlyTheSelectedGlyphsCellsWithTheSelectionBrush()
    {
        var (host, box) = Shown(noColor: false, inverse: false, new SolidColorBrush(Selection));
        using var _ = host;

        Assert.Empty(Cells(host, c => c.Style.Background == Selection)); // nothing selected yet

        SelectFirstGlyph(host, box);

        var tinted = Cells(host, c => c.Style.Background == Selection);
        Assert.Equal(GlyphColumns * GlyphRows, tinted.Count);

        // A contiguous rectangle, and the glyph is still drawn under it — the tint restyles, it does
        // not repaint, so no cell of the face was replaced by a blank.
        var columns = tinted.Select(p => p.Column).Distinct().Order().ToArray();
        Assert.Equal(GlyphColumns, columns.Length);
        Assert.Equal(columns[0] + GlyphColumns - 1, columns[^1]);
        Assert.All(tinted, p => Assert.False(string.IsNullOrEmpty(host.GetCell(p.Column, p.Row).Grapheme)));
    }

    /// <summary>
    /// TRAP 1. The old code cleared Inverse on EVERY path, including the colour one, where the caller
    /// passed no attributes at all — so the clear was invisible at the call site and easy to drop.
    /// With an inverse presenter the whole run is painted inverse first; the colour selection then
    /// reads as a coloured band because it un-inverts as well as recolouring. A migration to a bare
    /// <c>PartialStyle.WithBackground(colour)</c> leaves the band inverted, which flips the very
    /// foreground/background the brush was chosen to sit behind.
    /// </summary>
    [Fact]
    public void ColorSelection_ClearsInverseUnderTheTint()
    {
        var (host, box) = Shown(noColor: false, inverse: true, new SolidColorBrush(Selection));
        using var _ = host;

        SelectFirstGlyph(host, box);

        var tinted = Cells(host, c => c.Style.Background == Selection);
        Assert.Equal(GlyphColumns * GlyphRows, tinted.Count);
        Assert.All(tinted, p => Assert.False(IsInverse(host.GetCell(p.Column, p.Row)),
                                             $"({p.Column},{p.Row}) kept Inverse under the colour tint:\n" +
                                             Map(host, IsInverse)));

        // ...and the rest of the inverse run is untouched, so the tint is not just "clear everything".
        Assert.NotEmpty(Cells(host, IsInverse));
    }

    /// <summary>
    /// TRAP 3. <c>Brushes.Default</c> is a legal <see cref="TextBox.SelectionFill"/> and samples to
    /// <c>Color.Default</c> everywhere. The old code's <c>IsDefault</c> guard meant it stated no
    /// background — the tint fell back to un-inverting — and the guard has to survive the migration,
    /// because a present-but-default background is a real opinion to a <c>PartialStyle</c>.
    /// </summary>
    [Fact]
    public void ColorSelection_TreatsADefaultColoredBrushAsNoBackgroundAtAll()
    {
        var (host, box) = Shown(noColor: false, inverse: false, Brushes.Default);
        using var _ = host;

        var before = Cells(host, c => c.Style.Background.IsDefault).Count;

        SelectFirstGlyph(host, box);

        // No cell was tinted TO the terminal default: the guard suppressed the background entirely.
        Assert.Equal(before, Cells(host, c => c.Style.Background.IsDefault).Count);
    }

    // ───────────────────────────── the no-colour path ─────────────────────────────

    /// <summary>
    /// TRAP 2, first leg. On a monochrome terminal the selection IS the inversion, so the delta must
    /// force Inverse ON.
    /// </summary>
    [Fact]
    public void NoColorSelection_InvertsTheSelectedGlyph()
    {
        var (host, box) = Shown(noColor: true, inverse: false, selectionBrush: null);
        using var _ = host;

        // Differenced against the unselected frame: a monochrome TextBox uses Inverse for chrome of
        // its own, so the selection is what CHANGES, not the whole inverse population.
        var before = Cells(host, IsInverse);

        SelectFirstGlyph(host, box);

        var after = Cells(host, IsInverse);
        var gained = after.Except(before).ToList();

        Assert.True(gained.Count == GlyphColumns * GlyphRows,
                    $"expected {GlyphColumns * GlyphRows} newly-inverted cells, got {gained.Count}:\n" +
                    Map(host, IsInverse));
        Assert.Empty(before.Except(after)); // inverting only; nothing lost Inverse
    }

    /// <summary>
    /// TRAP 2, second leg — the same call site, the opposite operation. When the presenter is already
    /// inverse the run is painted inverse wholesale and the selection reads by UN-inverting, so the
    /// identical <c>CellStyle.Default.WithAttributes(...)</c> spelling that meant SET above meant
    /// CLEAR here. Only the <c>&amp; ~Inverse</c> buried in <c>TintCells</c> made that work.
    /// </summary>
    [Fact]
    public void NoColorSelection_UnInvertsWhenThePresenterIsAlreadyInverse()
    {
        var (host, box) = Shown(noColor: true, inverse: true, selectionBrush: null);
        using var _ = host;

        var before = Cells(host, IsInverse);
        Assert.NotEmpty(before); // the whole content band is inverted

        SelectFirstGlyph(host, box);

        var after = Cells(host, IsInverse);
        var cleared = before.Except(after).ToList();

        Assert.Equal(GlyphColumns * GlyphRows, cleared.Count);
        Assert.Empty(after.Except(before)); // un-inverting only; nothing gained Inverse
    }

    // ───────────────────────────── geometry ─────────────────────────────

    /// <summary>
    /// The reason a glyph face tints instead of re-painting: selecting must not move a single glyph
    /// cell. Pins that the migrated call still restyles in place.
    /// </summary>
    [Fact]
    public void Selection_DoesNotMoveAnyGlyphCell()
    {
        var (host, box) = Shown(noColor: false, inverse: false, new SolidColorBrush(Selection));
        using var _ = host;

        var before = new string?[Columns, Rows];
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
            before[column, row] = host.GetCell(column, row).Grapheme;

        SelectFirstGlyph(host, box);

        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
            Assert.Equal(before[column, row], host.GetCell(column, row).Grapheme);
    }
}
