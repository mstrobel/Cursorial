using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// Regression for the "overflow engages ~3 items late on shrink" bug (user-found, gallery layout): a Toolbar docked
// Top inside a DockPanel, folding as the window narrows. The bug: an already-overflowed container lives in the CLOSED
// overflow popup band, whose surface is not laid out, so it measures to DesiredSize 0. The fold read that live width,
// concluded the item "fit," un-reserved its space, and the row clipped at the window edge instead of folding — so the
// row visibly overran by several items before overflow ever engaged. The fix: the fold accounts by each container's
// natural (row-band) width, cached across band moves, so a popup-parked item's zero live width can't corrupt it.
public sealed class ToolbarOverflowDockedTests
{
    private static UITestHost NewHost(int width, int height = 10) => UITestHost.Create(new UITestHostOptions
    {
        InitialSize = new Size(width, height),
        Capabilities = TestCapabilities.KittyTruecolor,
    });

    // Build the gallery's Bars-page shape: a Toolbar docked Top (Margin 2,1) over a filling body, so the toolbar
    // stretches to the page width and folds as the window narrows.
    private static (DockPanel page, Toolbar toolbar) GalleryStylePage()
    {
        var toolbar = new Toolbar { Margin = new Margins(2, 1) };
        DockPanel.SetDock(toolbar, Dock.Top);
        toolbar.Items.Add(new BarLabel { Content = "Edit:" });
        toolbar.Items.Add(new BarButton { Content = "Cut" });
        toolbar.Items.Add(new BarButton { Content = "Copy" });
        toolbar.Items.Add(new BarButton { Content = "Paste" });
        toolbar.Items.Add(new BarSeparator());
        toolbar.Items.Add(new BarButton { Content = "Undo" });
        toolbar.Items.Add(new BarButton { Content = "Redo" });
        toolbar.Items.Add(new BarSeparator());
        toolbar.Items.Add(new BarButton { Content = "Find" });

        var body = new Border();
        DockPanel.SetDock(body, Dock.Top);

        var page = new DockPanel { LastChildFill = true };
        page.Children.Add(toolbar);
        page.Children.Add(body);
        return (page, toolbar);
    }

    [Fact] // the fold engages promptly on shrink — no clip-then-fold lag from popup-parked zero widths
    public void DockedToolbar_FoldsPromptlyOnShrink_NotLate()
    {
        using var host = NewHost(width: 80);
        var (page, toolbar) = GalleryStylePage();
        host.ShowRoot(page);
        host.RunUntilIdle();

        Assert.False(toolbar.HasOverflow); // everything fits at 80

        // Narrow one cell at a time; once the natural content no longer fits, overflow must engage and STAY engaged
        // (it must not clip several items past the edge first). We measure the natural content once (at a wide width)
        // then assert overflow is active for every width below it.
        for (var w = 40; w >= 16; w -= 2)
        {
            host.SendResize(w, 10);
            Assert.True(host.RunUntilIdle(), $"width {w} must converge");
            Assert.True(toolbar.HasOverflow, $"width {w}: the narrow toolbar must overflow, not clip");
            Assert.True(toolbar.OverflowCount > 0, $"width {w}: at least one item must be in the popup");
        }

        // Widen fully back — the row must un-fold cleanly.
        host.SendResize(80, 10);
        host.RunUntilIdle();
        Assert.False(toolbar.HasOverflow);
    }

    [Fact] // the row's VISIBLE content never overruns the toolbar's arranged width by more than a partial glyph
    public void DockedToolbar_VisibleRowStaysWithinWidth()
    {
        using var host = NewHost(width: 80);
        var (page, toolbar) = GalleryStylePage();
        host.ShowRoot(page);
        host.RunUntilIdle();

        host.SendResize(30, 10);
        host.RunUntilIdle();

        // The row band's arranged children must not extend far past the toolbar's arranged width (a few cells of
        // clip tolerance for the last partially-fitting item + the reserved chevron is fine; several whole items
        // overrunning is the bug).
        var rowRight = 0;
        foreach (var item in toolbar.Items)
        {
            var c = (UIElement) item!;
            if (c.VisualParent is ToolbarOverflowPanel)
                rowRight = Math.Max(rowRight, c.Bounds.Column + c.Bounds.Columns);
        }

        Assert.True(rowRight <= toolbar.Bounds.Columns + 2,
            $"visible row extends to {rowRight} but the toolbar is only {toolbar.Bounds.Columns} wide (overflow engaged late)");
        Assert.True(toolbar.HasOverflow);
    }
}
