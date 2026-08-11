// xUnit1031 disabled: UITestHost is single-thread-affine (see FrameLoopTests).
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Controls;

/// <summary>
/// Maintainer ruling 2026-08-11 (4), resolving D2: document-default attributes IMPOSE AT PAINT —
/// "impose-at-paint seems the safest bet to avoid staleness." RichTextPresenter and
/// FigletPresenter stop baking the composed element attributes (and their underline shape) into
/// the parse-time document/block default; the composed word reaches every painted cell through the
/// paint preference's <see cref="BrushedStyle.Imposing"/> — the exact shape
/// <see cref="TextBlock"/> always used. The staleness defect dies BY CONSTRUCTION: the attribute
/// axis properties ride the global AffectsRender lane and never invalidate the parse/layout
/// caches, so under baking an attribute REMOVED after the parse survived in the sticky document
/// forever (the add direction was always masked — the paint-side imposition, present alongside the
/// bake, supplied a freshly-added attribute on repaint).
/// </summary>
/// <remarks>
/// The flattened underline COLOUR deliberately stays parse-side (the ruling delegates the choice
/// with behaviour-preservation as the criterion): the resolver ladder has no underline-colour leg
/// for the preference, and the unbaked document default still states the Transparent compositing
/// identity, so the parse-time flatten of the element foreground is the only spelling that keeps
/// current inputs byte-identical (see
/// <c>FigletPresenterFreshnessTests.FigletPresenter_ReParsesOnThemeVariantFlip…</c>, which pins
/// that colour as the parse-freshness witness).
/// </remarks>
public sealed class DocumentAttributeImposeAtPaintTests
{
    private static (UIHeadlessHost Host, SlotHost Slot) Show(UIElement child, int columns, int rows)
    {
        var slot = new SlotHost(child) { SlotRect = new Rect(0, 0, columns, rows) };
        var host = UIHeadlessHost.Create();
        host.ShowRoot(slot);
        Assert.True(host.RunUntilIdle());
        return (host, slot);
    }

    // ───────────────────────── RichTextPresenter: the staleness probe ─────────────────────────

    /// <summary>
    /// The remove direction — the OBSERVABLE staleness at the pre-ruling HEAD: Inverse is ON when
    /// the string source parses (baked into the sticky document default), then flipped OFF. The
    /// flip is AffectsRender only (no cache invalidation, no key term), so the repaint serves the
    /// cached parse+layout — under baking the stale Inverse survived forever. Impose-at-paint
    /// repaints correctly WITHOUT a reparse: the format counter must not move.
    /// </summary>
    [Fact]
    public void RichTextPresenter_InverseClearedAfterParse_RepaintsCleanWithoutReformat()
    {
        var presenter = new RichTextPresenter
                        {
                            Source = "Hello",
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };
        TextElement.SetInverse(presenter, true);

        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        Assert.Equal("H", host.GetCell(0, 0).Grapheme);
        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));

        var formatsBefore = presenter.Cache.FullFormatCount;

        TextElement.SetInverse(presenter, false);
        host.RunFrame();

        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));
        Assert.Equal(formatsBefore, presenter.Cache.FullFormatCount); // a repaint, not a reformat/reparse
    }

    /// <summary>
    /// The add direction — always MASKED at the pre-ruling HEAD (which imposed at paint alongside
    /// the bake): an attribute set AFTER the parse reaches the frame fresh through the preference.
    /// Pinned so the lane stays proven under impose-at-paint alone.
    /// </summary>
    [Fact]
    public void RichTextPresenter_InverseSetAfterParse_ReachesThePaintFresh()
    {
        var presenter = new RichTextPresenter
                        {
                            Source = "Hello",
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };

        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        Assert.False(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));

        var formatsBefore = presenter.Cache.FullFormatCount;

        TextElement.SetInverse(presenter, true);
        host.RunFrame();

        Assert.True(host.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse));
        Assert.Equal(formatsBefore, presenter.Cache.FullFormatCount);
    }

    // ───────────────────────── FigletPresenter: the staleness probe ─────────────────────────

    /// <summary>The figlet sibling of the remove-direction probe: Bold baked into the figlet
    /// block's carrier at parse survived a post-parse clear forever. (Bold, not Underline —
    /// the figlet face strips Underline from cell attributes.)</summary>
    [Fact]
    public void FigletPresenter_BoldClearedAfterParse_RepaintsCleanWithoutReformat()
    {
        var presenter = new FigletPresenter
                        {
                            Text = "H",
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };
        TextElement.SetTextWeight(presenter, TextWeight.Bold);

        var (host, _) = Show(presenter, columns: 20, rows: 8);
        using var _1 = host;

        Assert.True(FindInkCell(host).Style.Attributes.HasFlag(TextAttributes.Bold));

        var formatsBefore = presenter.Cache.FullFormatCount;

        TextElement.SetTextWeight(presenter, TextWeight.Normal);
        host.RunFrame();

        Assert.False(FindInkCell(host).Style.Attributes.HasFlag(TextAttributes.Bold));
        Assert.Equal(formatsBefore, presenter.Cache.FullFormatCount); // a repaint, not a reformat/reparse
    }

    /// <summary>The figlet add direction — masked at the pre-ruling HEAD, pinned for the
    /// impose-at-paint lane (the resolver's attribute leg folds onto the face's cells).</summary>
    [Fact]
    public void FigletPresenter_BoldSetAfterParse_ReachesThePaintFresh()
    {
        var presenter = new FigletPresenter
                        {
                            Text = "H",
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };

        var (host, _) = Show(presenter, columns: 20, rows: 8);
        using var _1 = host;

        Assert.False(FindInkCell(host).Style.Attributes.HasFlag(TextAttributes.Bold));

        var formatsBefore = presenter.Cache.FullFormatCount;

        TextElement.SetTextWeight(presenter, TextWeight.Bold);
        host.RunFrame();

        Assert.True(FindInkCell(host).Style.Attributes.HasFlag(TextAttributes.Bold));
        Assert.Equal(formatsBefore, presenter.Cache.FullFormatCount);
    }

    // ───────────────────── behaviour-preservation pins (green before AND after) ─────────────────────

    /// <summary>
    /// The flattened underline COLOUR stays parse-side (the ruling's delegated choice, criterion:
    /// behaviour-preservation for current inputs): element underline + a solid element foreground
    /// still paints the underline in the flattened foreground colour, while the FLAG and SHAPE ride
    /// the paint preference.
    /// </summary>
    [Fact]
    public void RichTextPresenter_UnderlineWithSolidForeground_KeepsTheFlattenedUnderlineColour()
    {
        var red = Color.FromRgb(220, 30, 30);

        var presenter = new RichTextPresenter
                        {
                            Source = "Hi",
                            Foreground = new SolidColorBrush(red),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };
        TextElement.SetUnderline(presenter, UnderlineStyle.Single);

        var (host, _) = Show(presenter, columns: 12, rows: 1);
        using var _1 = host;

        var style = host.GetCell(0, 0).Style;
        Assert.True(style.Attributes.HasFlag(TextAttributes.Underline));
        Assert.Equal(UnderlineStyle.Single, style.UnderlineStyle);
        Assert.Equal(red, style.UnderlineColor); // the parse-side flatten of the element foreground
    }

    /// <summary>
    /// Composition-order reconciliation (the scoping's UNSURE): the element weight reaches the
    /// cells through the preference's per-axis <see cref="BrushedStyle.Imposing"/> fold, which
    /// IMPOSES over a run-declared weight (the shared SGR 22 reset — an unrenderable pair is
    /// never produced). Identical under baking (the imposition also ran) and under
    /// impose-at-paint, so nothing distinguishes the two mechanisms on a fresh parse.
    /// </summary>
    [Fact]
    public void RichTextPresenter_ElementFaintOverMarkupBold_TheImposedAxisWins()
    {
        var presenter = new RichTextPresenter
                        {
                            Source = "[b]X[/b]",
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top
                        };
        TextElement.SetTextWeight(presenter, TextWeight.Faint);

        var (host, _) = Show(presenter, columns: 4, rows: 1);
        using var _1 = host;

        var style = host.GetCell(0, 0).Style;
        Assert.True(style.Attributes.HasFlag(TextAttributes.Faint));  // the element's imposed weight
        Assert.False(style.Attributes.HasFlag(TextAttributes.Bold));  // the run's Bold cleared, not unioned
    }

    private static Cell FindInkCell(UIHeadlessHost host)
    {
        for (var r = 0; r < host.FrameBuffer.Rows; r++)
            for (var c = 0; c < host.FrameBuffer.Columns; c++)
            {
                var cell = host.GetCell(c, r);
                if (cell.Grapheme is { Length: > 0 } g && g != " ")
                    return cell;
            }

        Assert.Fail("No ink cell found in the frame.");
        return default;
    }
}
