// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts.
#pragma warning disable xUnit1031

using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Terminal;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// The <see cref="TextPresenter"/>'s LINE BASE — the ground state every run and every fill on a
/// visual line is a delta against — carries a <b>brush</b>, not a resolved colour. A
/// <c>CellStyle</c> base could only ever hold the latter, so a gradient foreground had to travel
/// beside it as a parallel parameter; these pin the capability itself, on the frame.
/// <para>
/// Every assertion reads the RENDERED frame. This suite renders through the real default theme, so
/// "the fixture sets no X" is not evidence that X is absent from the cells — each fixture asserts
/// its own reachability first, and the gradient assertions compare the FIRST cell against the LAST
/// rather than an adjacent one (adjacent samples of a short ramp can quantize identically).
/// </para>
/// </summary>
public sealed class TextPresenterLineBaseTests
{
    private const int Columns = 40;
    private const int Rows = 10;

    /// <summary>Twelve cells of plain text — one <c>DrawText</c> run, so the brush samples across all of it.</summary>
    private const string PlainText = "ABCDEFGHIJKL";

    /// <summary>Four glyphs × 3 columns = the SAME 12-column extent, which is what lets the two lanes
    /// be compared cell for cell: a brush's sample depends on the run's extent, so a comparison across
    /// lanes of different widths would report a difference the brush is entitled to.</summary>
    private const string FaceText = "ABCD";

    private const int GlyphColumns = 3;
    private const int GlyphRows = 2;

    /// <summary>A 2-row face, 3 columns per glyph, EVERY cell inked — so the brush is readable at
    /// every column of the band, and a gap cell can never be mistaken for an unbrushed one.</summary>
    private static readonly FigletFont Face =
        new("line-base-tests", '$', GlyphRows, FigletLayoutMode.None,
            new Dictionary<uint, FigletGlyph>
            {
                [' '] = new(' ', ["   ", "   "], '$'),
                ['A'] = new('A', ["###", "###"], '$'),
                ['B'] = new('B', ["###", "###"], '$'),
                ['C'] = new('C', ["###", "###"], '$'),
                ['D'] = new('D', ["###", "###"], '$'),
            });

    /// <summary>A horizontal black→white ramp: the sampled colour is a direct readout of the column.</summary>
    private static LinearGradientBrush Ramp() => new(Colors.TrueBlack, Colors.TrueWhite);

    private static readonly Color Solid = Color.FromRgb(30, 200, 90);
    private static readonly Color Selection = Color.FromRgb(180, 40, 200);

    /// <summary>The underline INK — distinct from <see cref="Solid"/> (the foreground) so a cell
    /// carrying it can only have got it from the underline brush the fixture handed in.</summary>
    private static readonly Color UnderlineInk = Color.FromRgb(220, 60, 20);

    // ───────────────────────────── fixtures ─────────────────────────────

    private static (UIHeadlessHost Host, TextBox Box) Shown(string text, IBrush? foreground,
                                                            Action<TextBox>? configure = null,
                                                            bool focus = false)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(Columns, Rows),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var box = new TextBox
        {
            Text = text,
            Width = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (foreground is not null)
            box.Foreground = foreground;

        configure?.Invoke(box);

        host.ShowRoot(box);
        host.RunUntilIdle();

        if (focus)
        {
            box.Focus();
            host.RunUntilIdle();
        }

        // Reachability: the theme's own setter did NOT beat the local value, so the cells below are
        // coloured by the brush this fixture handed in.
        if (foreground is not null)
            Assert.Same(foreground, box.Foreground);

        return (host, box);
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

    /// <summary>The frame cell at a PRESENTER-local coordinate — the space the line base paints into.</summary>
    private static Cell At(UIHeadlessHost host, TextBox box, int column, int row)
    {
        var origin = Presenter(box).TranslateToWindow(0, 0);
        return host.GetCell(origin.Column + column, origin.Row + row);
    }

    /// <summary>
    /// Puts an underline BRUSH on the presenter and re-renders. It goes on the PRESENTER rather than
    /// the box because <see cref="TextElement.UnderlineBrushProperty"/> is not one of the eight
    /// <c>AllAxisProperties</c> the <c>TextBox</c> template forwards — a value set on the box never
    /// reaches the part that paints the line. <see cref="UIElement.InvalidateVisual"/> is explicit for
    /// the neighbouring reason: the property carries no <c>AffectsRender</c> registration, so setting
    /// it schedules no repaint of its own.
    /// </summary>
    private static void SetUnderlineBrush(UIHeadlessHost host, TextBox box, IBrush brush)
    {
        var presenter = Presenter(box);
        TextElement.SetUnderlineBrush(presenter, brush);
        presenter.InvalidateVisual();
        host.RunUntilIdle();

        // Reachability: nothing beat the value this fixture set, so the cells below are underlined
        // by the brush handed in — or by nothing.
        Assert.Same(brush, TextElement.GetUnderlineBrush(presenter));
    }

    private static string Graphemes(UIHeadlessHost host, TextBox box, int row, int count)
    {
        var text = new System.Text.StringBuilder();
        for (var column = 0; column < count; column++)
            text.Append(At(host, box, column, row).Grapheme);
        return text.ToString();
    }

    // ───────────── the capability: a BRUSHED base, in both lanes ─────────────

    /// <summary>
    /// PLAIN lane. The line base carries the gradient, so the run's cells are coloured per COLUMN —
    /// the thing a resolved <c>CellStyle</c> base cannot express at all.
    /// </summary>
    [Fact]
    public void PlainLane_GradientBase_ColoursCellsByColumn()
    {
        var (host, box) = Shown(PlainText, Ramp());
        using var _ = host;

        Assert.True(Presenter(box).EditingSource.PaintsAsCells);   // the cell walk, not a fragment
        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length)); // the cells really hold the text

        var first = At(host, box, 0, 0).Style.Foreground;
        var last = At(host, box, PlainText.Length - 1, 0).Style.Foreground;

        Assert.NotEqual(first, last);
        Assert.True(last.Red > first.Red,
                    $"a black→white ramp should lighten toward the end: first={first}, last={last}");
    }

    /// <summary>
    /// FIGLET lane. Same claim, through the glyph-face painter — a single word, so the brush bounds
    /// are the whole run and the ramp traverses all twelve columns.
    /// </summary>
    [Fact]
    public void FaceLane_GradientBase_ColoursCellsByColumn()
    {
        var (host, box) = Shown(FaceText, Ramp(), b => TextElement.SetFont(b, Face));
        using var _ = host;

        var width = FaceText.Length * GlyphColumns;
        Assert.Equal(new string('#', width), Graphemes(host, box, 0, width)); // the face really inked the band

        var first = At(host, box, 0, 0).Style.Foreground;
        var last = At(host, box, width - 1, 0).Style.Foreground;

        Assert.NotEqual(first, last);
        Assert.True(last.Red > first.Red,
                    $"a black→white ramp should lighten toward the end: first={first}, last={last}");
    }

    /// <summary>
    /// ...and the ramp is traversed MONOTONICALLY across the run rather than restarting — the
    /// discriminating half of the two above, which "everything broken the same way" could satisfy.
    /// </summary>
    [Fact]
    public void PlainLane_GradientBase_IsSampledAcrossTheWholeRun()
    {
        var (host, box) = Shown(PlainText, Ramp());
        using var _ = host;

        var reds = Enumerable.Range(0, PlainText.Length)
                             .Select(c => (int) At(host, box, c, 0).Style.Foreground.Red)
                             .ToArray();

        Assert.Equal(reds.OrderBy(r => r), reds);                     // non-decreasing left to right
        Assert.True(reds.Distinct().Count() >= 4,
                    $"the ramp barely moved across the run: [{string.Join(", ", reds)}]");
    }

    // ───────────── the common path: a SOLID base still renders flat ─────────────

    /// <summary>A solid base colours every cell of the run identically — no behavioural change for
    /// the path every ordinary editor takes.</summary>
    [Fact]
    public void PlainLane_SolidBase_ColoursEveryCellTheSame()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid));
        using var _ = host;

        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));
        Assert.All(Enumerable.Range(0, PlainText.Length),
                   c => Assert.Equal(Solid, At(host, box, c, 0).Style.Foreground));
    }

    /// <inheritdoc cref="PlainLane_SolidBase_ColoursEveryCellTheSame"/>
    [Fact]
    public void FaceLane_SolidBase_ColoursEveryCellTheSame()
    {
        var (host, box) = Shown(FaceText, new SolidColorBrush(Solid), b => TextElement.SetFont(b, Face));
        using var _ = host;

        var width = FaceText.Length * GlyphColumns;
        Assert.Equal(new string('#', width), Graphemes(host, box, 0, width));
        Assert.All(Enumerable.Range(0, width),
                   c => Assert.Equal(Solid, At(host, box, c, 0).Style.Foreground));
    }

    // ───────────── ONE base: both lanes resolve the same ground state ─────────────

    /// <summary>
    /// The base is resolved TWICE — once per painting lane — and the two must build the SAME thing.
    /// Divergence between the presenter's lanes is exactly the class of defect that produced three
    /// separate invisible-selection bugs in this file, so it is pinned rather than assumed: the same
    /// element state, read back off both lanes' cells, column for column.
    /// </summary>
    /// <remarks>
    /// The axes are Bold and Blink deliberately. Italic, Underline, Overline and Strikethrough are
    /// <c>FigletFont.ForbiddenAttributes</c> — the FACE strips them from whatever it is handed, per
    /// cell — so a lane comparison over those would report a difference that belongs to the font, not
    /// to the base. (Observed, not assumed: the first spelling of this test used Underline and
    /// Strikethrough and failed on exactly that.)
    /// </remarks>
    [Fact]
    public void BothLanes_ResolveTheSameLineBase()
    {
        static void Configure(TextBox box)
        {
            TextElement.SetTextWeight(box, TextWeight.Bold);
            TextElement.SetBlink(box, true);
        }

        var (plainHost, plainBox) = Shown(PlainText, new SolidColorBrush(Solid), Configure);
        using var _ = plainHost;

        var (faceHost, faceBox) = Shown(FaceText, new SolidColorBrush(Solid),
                                        b => { Configure(b); TextElement.SetFont(b, Face); });
        using var __ = faceHost;

        // Reachability: both axes reached the fold, on both boxes.
        var expected = TextAttributes.Bold | TextAttributes.Blink;
        Assert.Equal(expected, TextElement.ComposeAttributes(Presenter(plainBox)).Flags);
        Assert.Equal(expected, TextElement.ComposeAttributes(Presenter(faceBox)).Flags);

        var width = FaceText.Length * GlyphColumns;
        Assert.Equal(PlainText.Length, width);   // the two lanes really do cover the same extent
        Assert.Equal(PlainText, Graphemes(plainHost, plainBox, 0, width));
        Assert.Equal(new string('#', width), Graphemes(faceHost, faceBox, 0, width));

        for (var column = 0; column < width; column++)
        {
            var plain = At(plainHost, plainBox, column, 0).Style;
            var face = At(faceHost, faceBox, column, 0).Style;

            Assert.Equal(expected, plain.Attributes & expected);
            Assert.Equal(plain.Attributes & expected, face.Attributes & expected);
            Assert.Equal(Solid, plain.Foreground);
            Assert.Equal(plain.Foreground, face.Foreground);
        }
    }

    /// <summary>
    /// ...and both lanes SAMPLE the base's brush the same way: over the same 12-column extent, cell
    /// for cell. This is the assertion a base that carried its colour resolved could not even be
    /// asked to satisfy — there would be one colour per lane, not twelve.
    /// </summary>
    [Fact]
    public void BothLanes_SampleTheGradientBaseIdentically()
    {
        var (plainHost, plainBox) = Shown(PlainText, Ramp());
        using var _ = plainHost;

        var (faceHost, faceBox) = Shown(FaceText, Ramp(), b => TextElement.SetFont(b, Face));
        using var __ = faceHost;

        var width = FaceText.Length * GlyphColumns;
        var plain = Enumerable.Range(0, width).Select(c => At(plainHost, plainBox, c, 0).Style.Foreground).ToArray();
        var face = Enumerable.Range(0, width).Select(c => At(faceHost, faceBox, c, 0).Style.Foreground).ToArray();

        Assert.Equal(plain, face);

        // ...and not vacuously: the ramp really is traversed, so "equal" is twelve agreeing colours
        // rather than one repeated.
        Assert.NotEqual(plain[0], plain[^1]);
        Assert.True(plain.Distinct().Count() >= 4,
                    $"the ramp barely moved: [{string.Join(", ", plain.Select(c => c.Red))}]");
    }

    // ───────────── the paths that must NOT change ─────────────

    /// <summary>
    /// The PLACEHOLDER is not the line base and must not start riding it: it resolves its own muted
    /// brush, so a gradient <c>Foreground</c> on the box does not reach it — every placeholder cell
    /// is one colour. (The gradient is the discriminator: routing the placeholder through the base
    /// would ramp these cells.)
    /// </summary>
    [Fact]
    public void Placeholder_DoesNotRideTheLineBase()
    {
        var (host, box) = Shown(string.Empty, Ramp(), b => b.Placeholder = "PLACEHOLDER");
        using var _ = host;

        Assert.Equal("PLACEHOLDER", Graphemes(host, box, 0, "PLACEHOLDER".Length));

        var colours = Enumerable.Range(0, "PLACEHOLDER".Length)
                                .Select(c => At(host, box, c, 0).Style.Foreground)
                                .Distinct()
                                .ToList();

        Assert.True(colours.Count == 1,
                    $"the placeholder took {colours.Count} colours — it is riding the line base's brush");

        // ...and it is genuinely NOT the box's foreground brush's head, which the ramp starts at.
        Assert.NotEqual(Colors.TrueBlack, colours[0]);
    }

    /// <summary>
    /// The SELECTION is a delta over the base, not a replacement of it: a selected span takes the
    /// selection background and still carries the base's per-column foreground.
    /// </summary>
    [Fact]
    public void Selection_TintsOverAGradientBase_WithoutFlatteningIt()
    {
        var (host, box) = Shown(PlainText, Ramp(),
                                b => b.SelectionBrush = new SolidColorBrush(Selection), focus: true);
        using var _ = host;

        box.SelectionStart = 2;
        box.SelectionLength = 4;   // "CDEF"
        host.RunUntilIdle();

        var selected = Enumerable.Range(2, 4).Select(c => At(host, box, c, 0)).ToArray();

        Assert.All(selected, cell => Assert.Equal(Selection, cell.Style.Background));
        Assert.All(selected, cell => Assert.True((cell.Style.Attributes & TextAttributes.Inverse) == 0,
                                                 "the brushed selection kept Inverse"));

        // The base still ramps UNDER the tint — the selection did not flatten the foreground.
        Assert.NotEqual(selected[0].Style.Foreground, selected[^1].Style.Foreground);
        Assert.True(selected[^1].Style.Foreground.Red > selected[0].Style.Foreground.Red,
                    $"the selected span lost the ramp: {selected[0].Style.Foreground} → {selected[^1].Style.Foreground}");
    }

    /// <summary>
    /// The fourth invisible selection, and the one the parallel brush parameter caused directly. With
    /// no resolvable foreground brush the paint used to take a second route — the COLOUR overload,
    /// <c>DrawText(…, Color.Default, null, style)</c> — and that route dropped the
    /// <c>background</c> argument on the floor. <c>DrawText</c> writes its background brush over the
    /// base style's, so a null there overwrote the selection colour with <c>Transparent</c>: a
    /// selected span that painted exactly like the text around it.
    /// </summary>
    /// <remarks>
    /// The two-way branch existed because the base was a <c>CellStyle</c> and the brush travelled
    /// beside it, so "is there a brush?" had to be re-decided at the paint site. A base that CARRIES
    /// its brush answers it once, and an absent brush is just <c>Brushes.Default</c> — the same "no
    /// opinion" the colour overload substitutes, said in the type that keeps the background.
    /// </remarks>
    [Fact]
    public void Selection_SurvivesAnUnresolvableForegroundBrush()
    {
        var (host, box) = Shown(PlainText, foreground: null,
                                b => b.SelectionBrush = new SolidColorBrush(Selection), focus: true);
        using var _ = host;

        // Kill BOTH of the presenter's routes to a foreground brush: the box's own property, and the
        // theme key it falls back to (shadowed with a non-IBrush, which is what a theme that never
        // defined the key looks like from inside the presenter).
        host.Application.Resources[ThemeKeys.TextBrush] = new object();
        box.Foreground = null;
        host.RunUntilIdle();

        var presenter = Presenter(box);
        Assert.Null(box.Foreground);
        Assert.False(presenter.TryFindResource(ThemeKeys.TextBrush, out var themed) && themed is IBrush,
                     "the presenter still resolves Theme.TextBrush — the no-brush leg is not reachable");

        box.SelectionStart = 2;
        box.SelectionLength = 4;   // "CDEF"
        host.RunUntilIdle();

        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length)); // the run really painted

        var selected = Enumerable.Range(2, 4).Select(c => At(host, box, c, 0)).ToArray();
        Assert.All(selected, cell => Assert.Equal(Selection, cell.Style.Background));

        // ...and the surrounding text did NOT take it, so the assertion above is about the selection.
        Assert.NotEqual(Selection, At(host, box, 0, 0).Style.Background);
        Assert.NotEqual(Selection, At(host, box, PlainText.Length - 1, 0).Style.Background);
    }

    // ───────── the cell lane reaches the primitive as a TEMPLATE, not as brush + value ─────────

    /// <summary>
    /// The base's non-colour channels reach the cell lane's cells. They used to be pre-folded into a
    /// resolved <c>CellStyle</c> before the primitive saw them; they now ride the delta the primitive
    /// resolves per cell, and the underline SHAPE is the discriminating one — it is the channel a
    /// brush pair had nowhere to carry, so a lane that dropped the delta would still get the colours
    /// right and lose this.
    /// </summary>
    [Fact]
    public void PlainLane_LineBaseAttributesAndUnderlineShape_ReachTheCells()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid), b =>
        {
            TextElement.SetTextWeight(b, TextWeight.Bold);
            TextElement.SetUnderline(b, UnderlineStyle.Curly);
            TextElement.SetStrikethrough(b, true);
        });
        using var _ = host;

        Assert.True(Presenter(box).EditingSource.PaintsAsCells);
        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));

        for (var column = 0; column < PlainText.Length; column++)
        {
            var style = At(host, box, column, 0).Style;
            Assert.True(style.Attributes.HasFlag(TextAttributes.Bold), $"column {column}: {style.Attributes}");
            Assert.True(style.Attributes.HasFlag(TextAttributes.Underline), $"column {column}: {style.Attributes}");
            Assert.True(style.Attributes.HasFlag(TextAttributes.Strikethrough), $"column {column}: {style.Attributes}");
            Assert.Equal(UnderlineStyle.Curly, style.UnderlineStyle);
        }
    }

    /// <summary>
    /// ...and so does the underline's COLOUR. The shape and the colour are two channels of one
    /// decoration and they travel separately — the shape as an enum on the template, the colour as a
    /// BRUSH — so a base that carries the first is no evidence at all that it carries the second.
    /// Here it must: an element stating <c>TextElement.UnderlineBrush</c> underlines in that colour.
    /// </summary>
    [Fact]
    public void PlainLane_LineBaseUnderlineBrush_ColoursTheUnderline()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid),
                                b => TextElement.SetUnderline(b, UnderlineStyle.Curly));
        using var _ = host;

        SetUnderlineBrush(host, box, new SolidColorBrush(UnderlineInk));

        Assert.True(Presenter(box).EditingSource.PaintsAsCells);
        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));

        for (var column = 0; column < PlainText.Length; column++)
        {
            var style = At(host, box, column, 0).Style;
            Assert.True(style.Attributes.HasFlag(TextAttributes.Underline), $"column {column}: {style.Attributes}");
            Assert.Equal(UnderlineStyle.Curly, style.UnderlineStyle);
            Assert.Equal(UnderlineInk, style.UnderlineColor);
        }
    }

    /// <summary>
    /// ...and it does NOT drag an underline along with it. The asymmetry is deliberate and stated at
    /// the bottom of the stack by <c>PartialStyleTests.WithUnderlineColor_DoesNotForceAnUnderline</c>:
    /// a colour says "whatever underline is drawn, draw it in this", which a theme wants (error
    /// underlines are red) without asserting that everything is underlined. The base states the colour
    /// UNCONDITIONALLY — unlike the shape, which is gated on the flag — so this is the assertion that
    /// keeps the two apart.
    /// </summary>
    [Fact]
    public void PlainLane_LineBaseUnderlineBrush_DoesNotForceAnUnderline()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid));
        using var _ = host;

        SetUnderlineBrush(host, box, new SolidColorBrush(UnderlineInk));

        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));

        // Reachability, the other way round: the fold really did NOT deliver the flag, so a cell that
        // carries it got it from the colour.
        Assert.False(TextElement.ComposeAttributes(Presenter(box)).Flags.HasFlag(TextAttributes.Underline));

        for (var column = 0; column < PlainText.Length; column++)
        {
            var style = At(host, box, column, 0).Style;
            Assert.False(style.Attributes.HasFlag(TextAttributes.Underline), $"column {column}: {style.Attributes}");
        }
    }

    /// <summary>
    /// The SHAPE path stands where it was: an underlined base with no brush of its own states no
    /// colour, and the cells keep the terminal's default rather than acquiring one. The discriminator
    /// against a fix that reached for a foreground brush, or a theme default, when the element stated
    /// nothing.
    /// </summary>
    [Fact]
    public void PlainLane_UnderlineWithoutABrush_StatesNoColour()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid),
                                b => TextElement.SetUnderline(b, UnderlineStyle.Curly));
        using var _ = host;

        Assert.Null(TextElement.GetUnderlineBrush(Presenter(box)));
        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));

        for (var column = 0; column < PlainText.Length; column++)
        {
            var style = At(host, box, column, 0).Style;
            Assert.True(style.Attributes.HasFlag(TextAttributes.Underline), $"column {column}: {style.Attributes}");
            Assert.Equal(UnderlineStyle.Curly, style.UnderlineStyle);
            Assert.True(style.UnderlineColor.IsDefault, $"column {column}: {style.UnderlineColor}");
        }
    }

    /// <summary>
    /// <see cref="TextAttributes.Inverse"/> is the PRESENTER's axis and now rides the base style the
    /// delta falls through to, rather than being OR-ed onto a pre-folded value. The two compose to the
    /// same word — that is the claim, and this is what would catch it if they did not: the base's own
    /// axes must survive alongside it, in both directions.
    /// </summary>
    [Fact]
    public void PlainLane_InverseComposesWithTheBasesOwnAttributes()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid), b =>
        {
            TextElement.SetInverse(b, true);
            TextElement.SetTextWeight(b, TextWeight.Bold);
            TextElement.SetBlink(b, true);
        });
        using var _ = host;

        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));

        var expected = TextAttributes.Bold | TextAttributes.Blink | TextAttributes.Inverse;

        for (var column = 0; column < PlainText.Length; column++)
        {
            var style = At(host, box, column, 0).Style;
            Assert.Equal(expected, style.Attributes & expected);
        }
    }

    /// <summary>
    /// The trap the migration had to walk past, at the presenter: the cell lane still paints a
    /// <b>transparent</b> background, so the field's own chrome shows under every glyph. On a template
    /// an absent background means "no opinion" — which would leave the base's instead — so the lane
    /// states transparent rather than inheriting it from whichever overload it reaches.
    /// </summary>
    /// <remarks>
    /// The evidence is a cell the RUN did not paint, in the same row: the chrome painted it, and the
    /// text cells must match it exactly. Asserting a literal colour would only pin the theme.
    /// </remarks>
    [Fact]
    public void PlainLane_UnselectedRun_LetsTheFieldsChromeShowUnderTheGlyphs()
    {
        var (host, box) = Shown(PlainText, new SolidColorBrush(Solid));
        using var _ = host;

        Assert.Equal(PlainText, Graphemes(host, box, 0, PlainText.Length));

        // A cell inside the field but past the text — chrome only, no run.
        var chrome = At(host, box, PlainText.Length + 5, 0);
        Assert.True(string.IsNullOrWhiteSpace(chrome.Grapheme), $"the run reached this cell: '{chrome.Grapheme}'");
        Assert.False(chrome.Style.Background.IsDefault,
                     "the field painted no background — this fixture cannot tell transparent from absent");

        for (var column = 0; column < PlainText.Length; column++)
            Assert.Equal(chrome.Style.Background, At(host, box, column, 0).Style.Background);
    }
}
