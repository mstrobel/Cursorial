// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Terminal;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// ONE selection rule across the presenter's three painting lanes (plain cells, sized OSC 66 runs,
/// glyph-face tints): <b>no usable selection brush → TOGGLE <see cref="TextAttributes.Inverse"/>;
/// a usable brush → paint its background and CLEAR it.</b> Each lane used to hand-roll the rule and
/// they drifted — the sized lane's spelling left the selection INVISIBLE on a colour tier whose
/// theme resolves no <c>SelectionBrush</c>, which is the asymmetry these pin.
/// <para>
/// Plus the band fill's attribute allowlist: the whole-area <c>Inverse</c> fill an inverse glyph
/// editor paints under its face used to write <c>Inverse</c> ALONE, and <c>CellBuffer.Set</c>
/// rescues the grapheme but not the attributes — so every other axis was lost on the inked cells.
/// </para>
/// <para>
/// Every assertion reads the RENDERED frame. This suite renders through the real default theme, so
/// "the fixture sets no X" is not evidence that X is absent from the cells; the fixtures assert
/// their own reachability first.
/// </para>
/// </summary>
public sealed class TextPresenterSelectionRuleTests
{
    private static readonly Color Selection = Color.FromRgb(180, 40, 200);

    private const int Columns = 40;
    private const int Rows = 10;

    /// <summary>A 2-row face, 3 columns per glyph. 'A'/'B' ink every cell; ' ' inks none — so a
    /// selection spanning a space covers 3 inked columns, 3 GAP columns, 3 inked columns.</summary>
    private static readonly FigletFont Face =
        new("rule-tests", '$', 2, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["   ", "   "], '$'),
                ['A'] = new('A', ["/#\\", "|=|"], '$'),
                ['B'] = new('B', ["[#]", "|=|"], '$'),
            });

    private const int GlyphColumns = 3;
    private const int GlyphRows = 2;

    /// <summary>The Kitty preset with colour negotiated away — the presenter reads <c>noColor</c>
    /// from the RENDER capabilities, so only the capability snapshot moves both it and the theme.</summary>
    private static TerminalCapabilities NoColorTerminal { get; } = HeadlessCapabilities.KittyTruecolor with
    {
        Output = HeadlessCapabilities.KittyTruecolor.Output with
        {
            Color = ColorCapabilities.None with { Depth = ColorDepth.NoColor },
        },
    };

    /// <summary>A colour tier that also advertises OSC 66 — the lane where <c>PaintsAsCells</c> is false.</summary>
    private static TerminalCapabilities SizedKitty { get; } = HeadlessCapabilities.KittyTruecolor with
    {
        Output = HeadlessCapabilities.KittyTruecolor.Output with
        {
            TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
        },
    };

    // ───────────────────────────── fixtures ─────────────────────────────

    private static (UIHeadlessHost Host, TextBox Box) Shown(
        TerminalCapabilities capabilities, string text, Action<TextBox>? configure = null,
        IBrush? selectionBrush = null, bool setSelectionBrush = false)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(Columns, Rows),
            Capabilities = capabilities,
        });

        var box = new TextBox
        {
            Text = text,
            Width = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (setSelectionBrush)
            box.SelectionBrush = selectionBrush;

        configure?.Invoke(box);

        host.ShowRoot(box);
        host.RunUntilIdle();
        box.Focus();
        host.RunUntilIdle();
        return (host, box);
    }

    /// <summary>A sentinel that is deliberately NOT an <see cref="IBrush"/> — the presenter's
    /// <c>ResolveBrush</c> tests the resolved value's type, so this is exactly what a theme that
    /// never defined the key looks like from inside the presenter.</summary>
    private static readonly object Unresolved = new();

    /// <summary>
    /// Removes every route to a selection brush, the way a theme with no <c>SelectionBrush</c>
    /// would: the box's own property back to null (the default theme SETS it from
    /// <c>Theme.InputSelection*</c>), and the fallback resource keys shadowed at the application
    /// scope — which the alias chase re-resolves through, so the aliases die with them.
    /// </summary>
    private static void StripSelectionBrush(UIHeadlessHost host, TextBox box)
    {
        host.Application.Resources[ThemeKeys.SelectionBrush] = Unresolved;
        host.Application.Resources[ThemeKeys.SelectionInactiveBrush] = Unresolved;
        box.SelectionBrush = null;
        host.RunUntilIdle();

        // Reachability, asserted rather than assumed: BOTH of the presenter's routes are dead.
        Assert.Null(box.SelectionBrush);
        var presenter = Presenter(box);
        Assert.False(presenter.TryFindResource(ThemeKeys.SelectionBrush, out var active) && active is IBrush,
                     "the presenter still resolves Theme.SelectionBrush");
        Assert.False(presenter.TryFindResource(ThemeKeys.SelectionInactiveBrush, out var idle) && idle is IBrush,
                     "the presenter still resolves Theme.SelectionInactiveBrush");
    }

    private static TextPresenter Presenter(TextBox box)
    {
        var presenter = FindDescendant<TextPresenter>(box);
        Assert.NotNull(presenter);
        return presenter!;
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        for (var i = 0; i < root.VisualChildrenCount; i++)
        {
            var child = root.GetVisualChild(i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } found)
                return found;
        }

        return null;
    }

    private static bool IsInverse(Cell cell) => (cell.Style.Attributes & TextAttributes.Inverse) != 0;

    private static List<(int Column, int Row)> Cells(UIHeadlessHost host, Func<Cell, bool> match)
    {
        var found = new List<(int, int)>();
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
            if (match(host.GetCell(column, row)))
                found.Add((column, row));
        return found;
    }

    private static string Map(UIHeadlessHost host, Func<Cell, bool> match)
    {
        var text = new System.Text.StringBuilder();
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
                text.Append(match(host.GetCell(column, row)) ? '#' : '.');
            text.Append('\n');
        }
        return text.ToString();
    }

    private static void Select(UIHeadlessHost host, TextBox box, int start, int length)
    {
        box.SelectionStart = start;
        box.SelectionLength = length;
        host.RunUntilIdle();
    }

    // ───────────── all three lanes toggle when there is no usable brush ─────────────

    /// <summary>
    /// PLAIN lane. The precedent leg (monochrome) already toggled; the colour tier with no resolved
    /// brush handed <c>DrawText</c> a null background and the run's own style — a selection that
    /// painted exactly like the text around it.
    /// </summary>
    [Fact]
    public void PlainLane_TogglesInverse_WhenTheColourTierHasNoUsableBrush()
    {
        var (host, box) = Shown(HeadlessCapabilities.KittyTruecolor, "hello");
        using var _ = host;
        StripSelectionBrush(host, box);

        Assert.True(Presenter(box).EditingSource.PaintsAsCells); // the cell walk, not a fragment

        var before = Cells(host, IsInverse);
        Select(host, box, 0, 2); // "he"
        var after = Cells(host, IsInverse);

        var gained = after.Except(before).ToList();
        Assert.True(gained.Count == 2,
                    $"expected the two selected cells to invert, got {gained.Count}:\n" + Map(host, IsInverse));
        Assert.Empty(before.Except(after));
    }

    /// <summary>
    /// SIZED lane — the silently invisible case. The old spelling's <c>: baseStyle.Attributes</c>
    /// arm is NOT dead code: the outer gate is <c>noColor || brush is null</c>, so it ran on every
    /// colour tier with no brush, and it was a no-op (the backdrop already WAS <c>baseStyle</c>).
    /// </summary>
    [Fact]
    public void SizedLane_TogglesInverse_WhenTheColourTierHasNoUsableBrush()
    {
        var (host, box) = Shown(SizedKitty, "ABCD",
                                b => TextElement.SetSizing(b, new TextSizing(Scale: 2)));
        using var _ = host;
        StripSelectionBrush(host, box);

        Assert.False(Presenter(box).EditingSource.PaintsAsCells); // the fragment lane

        Select(host, box, 1, 2); // "BC"

        var pieces = Fragments(host);
        Assert.True(pieces.ContainsKey("BC"), $"no selected fragment; emitted: {string.Join(", ", pieces.Keys)}");
        Assert.True((pieces["BC"].Attributes & TextAttributes.Inverse) != 0,
                    "the selected sized run did not invert — the selection is invisible");
        Assert.All(pieces.Where(p => p.Key != "BC"),
                   p => Assert.True((p.Value.Attributes & TextAttributes.Inverse) == 0,
                                    $"the unselected run '{p.Key}' inverted too"));
    }

    /// <summary>FIGLET lane — the local precedent for "no brush ⇒ the selection IS the inversion".</summary>
    [Fact]
    public void FigletLane_TogglesInverse_WhenTheColourTierHasNoUsableBrush()
    {
        var (host, box) = Shown(HeadlessCapabilities.KittyTruecolor, "AB", b => TextElement.SetFont(b, Face));
        using var _ = host;
        StripSelectionBrush(host, box);

        var before = Cells(host, IsInverse);
        Select(host, box, 0, 1);
        var after = Cells(host, IsInverse);

        var gained = after.Except(before).ToList();
        Assert.True(gained.Count == GlyphColumns * GlyphRows,
                    $"expected {GlyphColumns * GlyphRows} newly-inverted cells, got {gained.Count}:\n" +
                    Map(host, IsInverse));
    }

    // ───────────── the brush path: paint the background, CLEAR Inverse ─────────────

    /// <summary>
    /// Why the clear is not decoration: if <c>Inverse</c> survives, the terminal swaps channels at
    /// render, so the brush's BACKGROUND comes out as the visible foreground and the band never
    /// appears — while the visible foreground comes from the cell's background channel, which is
    /// <c>Color.Transparent</c> here, so the text renders as the backdrop.
    /// </summary>
    [Fact]
    public void PlainLane_BrushPath_PaintsTheBackgroundAndClearsInverse()
    {
        var (host, box) = Shown(HeadlessCapabilities.KittyTruecolor, "hello",
                                b => TextElement.SetInverse(b, true),
                                new SolidColorBrush(Selection), setSelectionBrush: true);
        using var _ = host;

        Assert.True(TextElement.GetInverse(Presenter(box))); // the surrounding run IS inverse
        Select(host, box, 0, 2);

        var painted = Cells(host, c => c.Style.Background == Selection);
        Assert.True(painted.Count == 2, $"expected 2 brushed cells, got {painted.Count}");
        Assert.All(painted, p => Assert.False(IsInverse(host.GetCell(p.Column, p.Row)),
                                              $"({p.Column},{p.Row}) kept Inverse under the brush"));
        Assert.NotEmpty(Cells(host, IsInverse)); // the rest of the inverse run is untouched
    }

    /// <summary>The same rule on the fragment lane: the selected piece's SGR backdrop.</summary>
    [Fact]
    public void SizedLane_BrushPath_PaintsTheBackgroundAndClearsInverse()
    {
        var (host, box) = Shown(SizedKitty, "ABCD",
                                b =>
                                {
                                    TextElement.SetSizing(b, new TextSizing(Scale: 2));
                                    TextElement.SetInverse(b, true);
                                },
                                new SolidColorBrush(Selection), setSelectionBrush: true);
        using var _ = host;

        Select(host, box, 1, 2);

        var pieces = Fragments(host);
        Assert.True(pieces.ContainsKey("BC"), $"no selected fragment; emitted: {string.Join(", ", pieces.Keys)}");
        Assert.Equal(Selection, pieces["BC"].Background);
        Assert.True((pieces["BC"].Attributes & TextAttributes.Inverse) == 0, "the brushed run kept Inverse");
        Assert.All(pieces.Where(p => p.Key != "BC"),
                   p => Assert.True((p.Value.Attributes & TextAttributes.Inverse) != 0,
                                    $"the unselected run '{p.Key}' lost the presenter's Inverse"));
    }

    /// <summary>
    /// <c>Brushes.Default</c> is a legal <c>SelectionBrush</c> and samples to <c>Color.Default</c>
    /// everywhere. It is not a USABLE brush — a default background is indistinguishable from no
    /// selection — so the rule falls through to the toggle rather than painting it.
    /// </summary>
    [Fact]
    public void ADefaultSamplingBrush_IsNotUsable_SoTheSelectionToggles()
    {
        var (host, box) = Shown(HeadlessCapabilities.KittyTruecolor, "AB",
                                b => TextElement.SetFont(b, Face),
                                Brushes.Default, setSelectionBrush: true);
        using var _ = host;

        Assert.Same(Brushes.Default, box.SelectionBrush); // the local value survived the theme's setter

        var defaultBackgrounds = Cells(host, c => c.Style.Background.IsDefault).Count;
        var before = Cells(host, IsInverse);

        Select(host, box, 0, 1);

        // The guard held: nothing was tinted TO the terminal default...
        Assert.Equal(defaultBackgrounds, Cells(host, c => c.Style.Background.IsDefault).Count);

        // ...and the selection is still visible, because an unusable brush toggles.
        var gained = Cells(host, IsInverse).Except(before).ToList();
        Assert.True(gained.Count == GlyphColumns * GlyphRows,
                    $"expected {GlyphColumns * GlyphRows} newly-inverted cells, got {gained.Count}:\n" +
                    Map(host, IsInverse));
    }

    // ───────────── toggle semantics ─────────────

    /// <summary>A band that is NOT inverse inverts.</summary>
    [Fact]
    public void Toggle_InvertsABandThatIsNotInverse()
    {
        var (host, box) = Shown(NoColorTerminal, "AB",
                                b =>
                                {
                                    TextElement.SetFont(b, Face);
                                    TextElement.SetInverse(b, false); // override the theme's caps-nocolor rule
                                });
        using var _ = host;

        Assert.False(TextElement.GetInverse(Presenter(box)));

        var before = Cells(host, IsInverse);
        Select(host, box, 0, 1);
        var gained = Cells(host, IsInverse).Except(before).ToList();

        Assert.Equal(GlyphColumns * GlyphRows, gained.Count);
        Assert.Empty(before.Except(Cells(host, IsInverse)));
    }

    /// <summary>...and one that IS inverse un-inverts. Same call site, opposite operation.</summary>
    [Fact]
    public void Toggle_UnInvertsABandThatIsAlreadyInverse()
    {
        var (host, box) = Shown(NoColorTerminal, "AB",
                                b =>
                                {
                                    TextElement.SetFont(b, Face);
                                    TextElement.SetInverse(b, true);
                                });
        using var _ = host;

        var before = Cells(host, IsInverse);
        Assert.NotEmpty(before);

        Select(host, box, 0, 1);
        var cleared = before.Except(Cells(host, IsInverse)).ToList();

        Assert.Equal(GlyphColumns * GlyphRows, cleared.Count);
        Assert.Empty(Cells(host, IsInverse).Except(before));
    }

    /// <summary>
    /// A multi-glyph selection SPANNING A SPACE: gap cells and inked cells are one rectangle, and
    /// the toggle reads each cell's own state rather than the presenter's bool — so a band the fill
    /// inverted un-inverts across ink and gap alike.
    /// </summary>
    [Fact]
    public void MultiGlyphSelection_SpanningASpace_TreatsGapAndInkAlike()
    {
        var (host, box) = Shown(NoColorTerminal, "A B",
                                b =>
                                {
                                    TextElement.SetFont(b, Face);
                                    TextElement.SetInverse(b, true);
                                });
        using var _ = host;

        var before = Cells(host, IsInverse);
        Select(host, box, 0, 3); // "A B" — glyph, gap, glyph

        var cleared = before.Except(Cells(host, IsInverse)).ToList();
        Assert.True(cleared.Count == 3 * GlyphColumns * GlyphRows,
                    $"expected {3 * GlyphColumns * GlyphRows} un-inverted cells (3 glyph widths × {GlyphRows} rows), " +
                    $"got {cleared.Count}:\n" + Map(host, IsInverse));

        // And the middle three columns really are GAP — no glyph ink there, so the assertion above
        // is about the rectangle, not about the face happening to fill it.
        var columns = cleared.Select(p => p.Column).Distinct().Order().ToArray();
        Assert.Equal(3 * GlyphColumns, columns.Length);
        var gaps = cleared.Where(p => p.Column >= columns[0] + GlyphColumns &&
                                      p.Column < columns[0] + 2 * GlyphColumns).ToList();
        Assert.Equal(GlyphColumns * GlyphRows, gaps.Count);
        Assert.All(gaps, p => Assert.True(IsBlank(host.GetCell(p.Column, p.Row)),
                                          $"({p.Column},{p.Row}) was expected to be a gap cell"));
    }

    private static bool IsBlank(Cell cell) => string.IsNullOrEmpty(cell.Grapheme) || cell.Grapheme.IsWhiteSpace();

    // ───────────── the band fill's attribute allowlist ─────────────

    /// <summary>
    /// The whole-area <c>Inverse</c> fill an inverse glyph editor paints under its face wrote
    /// <c>Inverse</c> ALONE — <c>CellStyle.Default.WithAttributes(Inverse)</c>, not the line's own
    /// style. <c>FillOpaque</c> writes space-bearing cells and <c>CellBuffer.Set</c> rescues the
    /// grapheme and composites the foreground back, but NOT the attributes: so every other axis the
    /// fold delivered was lost on exactly the cells the face had inked.
    /// </summary>
    [Fact]
    public void BandFill_KeepsTheAllowedAttributes_OnInkedCells()
    {
        var (host, box) = Shown(HeadlessCapabilities.KittyTruecolor, "AB",
                                b =>
                                {
                                    TextElement.SetFont(b, Face);
                                    TextElement.SetInverse(b, true);
                                    TextElement.SetTextWeight(b, TextWeight.Bold);
                                });
        using var _ = host;

        var presenter = Presenter(box);
        Assert.True(TextElement.GetInverse(presenter));
        Assert.Equal(TextAttributes.Bold | TextAttributes.Inverse, TextElement.ComposeAttributes(presenter).Flags);

        var inked = Cells(host, c => c.Grapheme is "#" or "/" or "=");
        Assert.NotEmpty(inked); // the face really did ink cells inside the band

        Assert.All(inked, p =>
        {
            var style = host.GetCell(p.Column, p.Row).Style;
            Assert.True((style.Attributes & TextAttributes.Inverse) != 0, $"({p.Column},{p.Row}) lost Inverse");
            Assert.True((style.Attributes & TextAttributes.Bold) != 0,
                        $"({p.Column},{p.Row}) lost Bold to the band fill — the fill wrote Inverse alone");
        });
    }

    /// <summary>
    /// The allowlist's discriminating claim, on the cells the presenter did NOT ink. Bold/Faint/
    /// Italic are invisible on a blank, so spreading them is free and keeps the SGR state uniform
    /// across the band; Underline and Blink are VISIBLE on a blank, so spreading them would rule
    /// every row of the band and strobe the whole content rect respectively.
    /// </summary>
    [Fact]
    public void BandFill_SpreadsOnlyTheAllowedAttributes_ToGapCells()
    {
        var (host, box) = Shown(HeadlessCapabilities.KittyTruecolor, "A B",
                                b =>
                                {
                                    TextElement.SetFont(b, Face);
                                    TextElement.SetInverse(b, true);
                                    TextElement.SetTextWeight(b, TextWeight.Bold);
                                    TextElement.SetUnderline(b, UnderlineStyle.Single);
                                    TextElement.SetBlink(b, true);
                                });
        using var _ = host;

        var presenter = Presenter(box);
        var folded = TextElement.ComposeAttributes(presenter).Flags;
        Assert.Equal(TextAttributes.Bold | TextAttributes.Inverse | TextAttributes.Underline | TextAttributes.Blink,
                     folded); // reachability: all four axes reached the fold

        // The gap columns of "A B": the middle glyph cell of the band — inside the rectangle the
        // fill wrote, never inked. Located from the PRESENTER's origin, not from the inverse
        // population: the box's own chrome is inverse too, so the frame-wide extent is not the band.
        var origin = presenter.TranslateToWindow(0, 0);
        var gaps = (from row in Enumerable.Range(origin.Row, GlyphRows)
                    from column in Enumerable.Range(origin.Column + GlyphColumns, GlyphColumns)
                    select (Column: column, Row: row)).ToList();

        Assert.All(gaps, p => Assert.True(IsBlank(host.GetCell(p.Column, p.Row)),
                                          $"({p.Column},{p.Row}) is inked — not a gap cell:\n" + Map(host, IsInverse)));

        Assert.All(gaps, p =>
        {
            var attributes = host.GetCell(p.Column, p.Row).Style.Attributes;
            Assert.True((attributes & TextAttributes.Inverse) != 0, $"({p.Column},{p.Row}) lost Inverse");
            Assert.True((attributes & TextAttributes.Bold) != 0,
                        $"({p.Column},{p.Row}) did not receive Bold — the fill dropped an allowed attribute");
            Assert.True((attributes & TextAttributes.Underline) == 0,
                        $"({p.Column},{p.Row}) received Underline — the fill would rule every row of the band");
            Assert.True((attributes & TextAttributes.Blink) == 0,
                        $"({p.Column},{p.Row}) received Blink — the fill would strobe the whole content rect");
        });
    }

    // ───────────── helpers ─────────────

    /// <summary>The frame's sized-text fragments, keyed by their text — the split selection pieces.</summary>
    private static Dictionary<string, CellStyle> Fragments(UIHeadlessHost host)
    {
        var pieces = new Dictionary<string, CellStyle>();
        foreach (var entry in host.FrameBuffer.Fragments)
            if (entry.Value.Fragment is SizedTextFragment sized)
                pieces[sized.Text] = sized.Style;
        return pieces;
    }
}
