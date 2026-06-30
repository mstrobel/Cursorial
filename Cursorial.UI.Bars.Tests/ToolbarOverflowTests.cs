using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// Phase 1 spec for the Toolbar's discrete overflow (the Actipro live-control re-parent model): the ToolbarOverflowPanel
// folds the trailing items into the chevron popup by moving the LIVE container instances between the row band (the
// panel itself) and the overflow band (the popup's StackPanel), preserving the Toolbar as their logical parent. Band
// membership is asserted via the public VisualParent/LogicalParent (row = ToolbarOverflowPanel, overflow = StackPanel).
public sealed class ToolbarOverflowTests
{
    private static UITestHost NewHost(int width, int height = 6) => UITestHost.Create(new UITestHostOptions
    {
        InitialSize = new Size(width, height),
        Capabilities = TestCapabilities.KittyTruecolor,
    });

    // A toolbar that STRETCHES to the host width (so overflow tracks the available space — a Left-aligned toolbar
    // auto-sizes to its content and would never overflow, by design) and is pinned to the top so its row is row 0 and
    // the overflow popup drops onto rows 1+.
    private static Toolbar NewToolbar(params UIElement[] items)
    {
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        foreach (var item in items)
            toolbar.Items.Add(item);
        return toolbar;
    }

    private static BarButton Btn(string label) => new() { Content = label };

    private static bool OnRow(UIElement c) => c.VisualParent is ToolbarOverflowPanel;
    private static bool InOverflow(UIElement c) => c.VisualParent is StackPanel; // the popup band host

    [Fact] // everything fits → no overflow, no chevron, all items on the row band
    public void NoOverflow_WhenItemsFit()
    {
        using var host = NewHost(width: 60);
        var a = Btn("Cut");
        var b = Btn("Copy");
        var c = Btn("Paste");
        var toolbar = NewToolbar(a, b, c);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        Assert.False(toolbar.HasOverflow);
        Assert.Equal(0, toolbar.OverflowCount);
        Assert.True(OnRow(a) && OnRow(b) && OnRow(c));
        Assert.DoesNotContain("»", host.GetRowText(0));
        var row = host.GetRowText(0);
        Assert.Contains("Cut", row);
        Assert.Contains("Paste", row);
    }

    [Fact] // a narrow bar folds the trailing items into the popup; the chevron appears
    public void FoldsTrailingItems_WhenNarrow()
    {
        using var host = NewHost(width: 14);
        var a = Btn("Cut");
        var b = Btn("Copy");
        var c = Btn("Paste");
        var d = Btn("Delete");
        var toolbar = NewToolbar(a, b, c, d);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        Assert.True(toolbar.HasOverflow);
        Assert.True(toolbar.OverflowCount > 0);
        Assert.Contains("»", host.GetRowText(0));

        // The leading item stays on the row; the trailing item folds into the overflow band.
        Assert.True(OnRow(a), "first item should remain on the row");
        Assert.True(InOverflow(d), "last item should overflow into the popup band");
    }

    [Fact] // an overflowed control stays a LOGICAL child of the Toolbar (its command/inheritance is unbroken)
    public void OverflowedItem_StaysLogicalChildOfToolbar()
    {
        using var host = NewHost(width: 14);
        var ran = 0;
        var d = new BarButton { Content = "Delete", Command = new BarCommand(() => ran++) };
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), d);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        Assert.True(InOverflow(d));               // re-parented into the popup band (visual)
        Assert.Same(toolbar, d.LogicalParent);    // …but still a logical child of the Toolbar (inheritance intact)

        // Open the popup so the overflowed control is attached on the popup surface (an overflowed item is detached
        // while the popup is closed — by design), then activate it: the command resolves and runs, proving the
        // logical/inheritance/command chain survives the cross-surface re-parent.
        toolbar.IsOverflowOpen = true;
        host.RunUntilIdle();
        d.Focus();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(1, ran);
    }

    [Fact] // opening the chevron shows the SAME live controls in the popup; closing returns them to the row
    public void OpenChevron_ShowsLiveOverflowedControls()
    {
        using var host = NewHost(width: 14);
        var d = Btn("Delete");
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), d);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        Assert.True(InOverflow(d));

        toolbar.IsOverflowOpen = true;
        host.RunUntilIdle();
        // The popup band drops below the row; the overflowed label renders on a popup row (its instance is unchanged).
        var popupText = host.GetRowText(1) + host.GetRowText(2) + host.GetRowText(3);
        Assert.Contains("Delete", popupText);
        Assert.True(InOverflow(d)); // still the same instance in the popup band
    }

    [Fact] // widening the bar returns the items to the row AND force-closes the popup
    public void Widen_ReturnsItemsToRow_AndClosesPopup()
    {
        using var host = NewHost(width: 14);
        var d = Btn("Delete");
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), d);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        toolbar.IsOverflowOpen = true;
        host.RunUntilIdle();
        Assert.True(toolbar.HasOverflow);
        Assert.True(toolbar.IsOverflowOpen);

        host.SendResize(60, 6);
        host.RunUntilIdle();

        Assert.False(toolbar.HasOverflow);
        Assert.False(toolbar.IsOverflowOpen);     // un-overflow force-closes the popup
        Assert.True(OnRow(d));                    // returned to the row band
        Assert.DoesNotContain("»", host.GetRowText(0));
    }

    [Fact] // CD-P9-3: removing an item that is currently overflowed must not crash or strand a dangling visual parent
    public void RemoveOverflowedItem_NoCrash()
    {
        using var host = NewHost(width: 14);
        var d = Btn("Delete");
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), d);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        Assert.True(InOverflow(d));

        toolbar.Items.Remove(d);
        host.RunUntilIdle();

        Assert.Null(d.VisualParent);   // detached from the overflow band synchronously (no dangling visual parent)
        Assert.Null(d.LogicalParent);  // generator's FinishUnrealize ran the logical detach after the visual detach
    }

    [Fact] // the discrete fold moves a WHOLE control across the boundary (never splits one) — exact fit stays
    public void Fold_IsWholeControl_NotSplit()
    {
        using var host = NewHost(width: 14);
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), Btn("Delete"));
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        // Every container is in exactly one band (row XOR overflow) — none half-placed.
        foreach (var item in toolbar.Items)
        {
            var c = (UIElement) item!;
            Assert.True(OnRow(c) ^ InOverflow(c), $"{((BarButton) c).Content} must be in exactly one band");
        }
    }

    [Fact] // OverflowMode.Never pins an item to the row even when the bar is too narrow
    public void OverflowModeNever_PinsToRow()
    {
        using var host = NewHost(width: 14);
        var pinned = Btn("Cut");
        Toolbar.SetOverflowMode(pinned, ToolbarOverflowMode.Never);
        var toolbar = NewToolbar(pinned, Btn("Copy"), Btn("Paste"), Btn("Delete"));
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        Assert.True(OnRow(pinned)); // never overflows
    }

    [Fact] // OverflowMode.Always keeps an item in the popup and forces the chevron even when everything else fits
    public void OverflowModeAlways_StaysInPopup_AndForcesChevron()
    {
        using var host = NewHost(width: 60);
        var always = Btn("Settings");
        Toolbar.SetOverflowMode(always, ToolbarOverflowMode.Always);
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), always);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();

        Assert.True(toolbar.HasOverflow);   // forced by the Always item
        Assert.True(InOverflow(always));
        Assert.Contains("»", host.GetRowText(0));
    }

    [Fact] // audit fix: changing OverflowMode on an item currently in the popup band re-folds the row. The item's
           // VisualParent is the popup host (not the panel), so the changed handler must reach the panel via the
           // logical chain — invalidating VisualParent would silently miss the overflowed item.
    public void OverflowMode_ChangeOnOverflowedItem_RefoldsRow()
    {
        using var host = NewHost(width: 30);
        var settings = Btn("Settings");
        Toolbar.SetOverflowMode(settings, ToolbarOverflowMode.Always);
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), settings);
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        Assert.True(InOverflow(settings)); // Always ⇒ pinned to the popup band
        Assert.True(toolbar.HasOverflow);

        // Flip it to AsNeeded — at width 30 everything now fits, so it must return to the row and overflow clears.
        Toolbar.SetOverflowMode(settings, ToolbarOverflowMode.AsNeeded);
        host.RunUntilIdle();
        Assert.True(OnRow(settings));
        Assert.False(toolbar.HasOverflow);
    }

    [Fact] // audit fix: detaching the toolbar while the overflow popup is open closes it cleanly (no leaked surface)
    public void Detach_WhilePopupOpen_ClosesPopup()
    {
        using var host = NewHost(width: 14);
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), Btn("Delete"));
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        toolbar.IsOverflowOpen = true;
        host.RunUntilIdle();
        Assert.True(toolbar.IsOverflowOpen);

        // Replace the root → the toolbar detaches; OnDetachedFromTree must close the popup (no dangling surface, no crash).
        var ex = Record.Exception(() =>
        {
            host.ShowRoot(new BarButton { Content = "X" });
            host.RunUntilIdle();
        });
        Assert.Null(ex);
        Assert.False(toolbar.IsOverflowOpen);
    }

    [Fact] // audit fix: re-templating the toolbar while the popup is open closes the OLD popup (OnTemplateDetaching
           // leak guard — without it the old PART_OverflowPopup's TopLevelSurface leaks)
    public void Retemplate_WhilePopupOpen_ClosesOldPopup()
    {
        using var host = NewHost(width: 14);
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), Btn("Delete"));
        host.ShowRoot(toolbar);
        host.RunUntilIdle();
        toolbar.IsOverflowOpen = true;
        host.RunUntilIdle();
        Assert.True(toolbar.IsOverflowOpen);

        // Force a re-template by assigning a fresh theme (a new ControlTemplate instance) — OnTemplateDetaching fires.
        var ex = Record.Exception(() =>
        {
            toolbar.Theme = CursorialBarsTheme.ToolbarStyle();
            host.RunUntilIdle();
        });
        Assert.Null(ex);
        Assert.False(toolbar.IsOverflowOpen);
    }

    [Fact] // anti-thrash: the fold converges (no oscillation) at a boundary width
    public void Fold_ConvergesAtBoundaryWidth()
    {
        using var host = NewHost(width: 14);
        var toolbar = NewToolbar(Btn("Cut"), Btn("Copy"), Btn("Paste"), Btn("Delete"), Btn("Find"));
        host.ShowRoot(toolbar);
        // RunUntilIdle returns false if it never settles within the frame budget (oscillation would do that).
        Assert.True(host.RunUntilIdle(), "the fold must converge (no re-parent oscillation)");

        // Step the width down one cell at a time across the boundary; each step must settle.
        for (var w = 30; w >= 8; w--)
        {
            host.SendResize(w, 6);
            Assert.True(host.RunUntilIdle(), $"width {w} must converge");
        }
    }
}
