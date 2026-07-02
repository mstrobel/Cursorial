using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// The Backstage (Ribbon P3e): the File tab's view — a TabControl-shaped host of BackstageItem destinations on a
// VERTICAL rail beside a swappable detail pane, a ◂ back, and a DisplayMode (FullScreen window vs File-anchored Menu
// popup, hosted by BackstageHost). It inherits TabControl's single-selection + selected-content host verbatim; only
// the rail orientation, the Up/Down navigation, and BackRequested differ.
public sealed class BackstageTests
{
    private const int H = 14;

    private static UITestHost NewHost(int w = 48, int h = H) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static BackstageItem Dest(string header, string detail, bool selectable = true)
        => new() { Header = header, Content = detail, IsSelectable = selectable };

    private static Backstage NewBackstage(params BackstageItem[] items)
    {
        var bs = new Backstage();
        foreach (var it in items)
            bs.Items.Add(it);
        return bs;
    }

    private static string AllRows(UITestHost host) =>
        string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));

    private static BackstageItem Container(Backstage bs, int index) =>
        (BackstageItem) bs.ItemContainerGenerator.ContainerFromIndex(index)!;

    // ───────────────────────────── Increment 1 — skeleton + selection ─────────────────────────────

    [Fact] // the rail generates a BackstageItem per destination and renders their headers + the ◂ back button
    public void Backstage_RailRendersDestinationsAndBack()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "Create"), Dest("Open", "Browse"), Dest("Save", "Persist"));
        host.ShowRoot(bs);
        host.RunUntilIdle();

        Assert.Equal(3, bs.ItemContainerGenerator.ContainerCount);
        var all = AllRows(host);
        Assert.Contains("New", all);
        Assert.Contains("Open", all);
        Assert.Contains("Save", all);
        Assert.Contains("◂", all); // the back button
    }

    [Fact] // the first destination auto-selects (TabControl parity) and the detail pane shows its Content
    public void Backstage_AutoSelectsFirstAndShowsDetail()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "Create-a-document"), Dest("Open", "Browse-files"));
        host.ShowRoot(bs);
        host.RunUntilIdle();

        Assert.Equal(0, bs.SelectedIndex);
        Assert.Equal("Create-a-document", bs.SelectedContent);
        Assert.Contains("Create-a-document", AllRows(host)); // the detail pane rendered it
    }

    [Fact] // selecting a destination swaps the detail pane to its Content (the rail persists)
    public void Backstage_SelectionSwapsDetailPane()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "New-pane"), Dest("Export", "Export-pane"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Assert.Contains("New-pane", AllRows(host));

        bs.SelectedIndex = 1;
        host.RunUntilIdle();

        Assert.Equal("Export-pane", bs.SelectedContent);
        Assert.Contains("Export-pane", AllRows(host));
        Assert.DoesNotContain("New-pane", AllRows(host)); // the old pane is gone (single content host)
    }

    [Fact] // a non-selectable rail row (a [separator] / section header) is skipped by auto-selection
    public void Backstage_NonSelectableRowSkippedByAutoSelect()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("---", "n/a", selectable: false), Dest("Open", "Browse"));
        host.ShowRoot(bs);
        host.RunUntilIdle();

        Assert.Equal(1, bs.SelectedIndex); // auto-selection skipped the non-selectable row 0
    }

    [Fact] // a BackstageItem added directly is its own container (IsItemItsOwnContainer)
    public void Backstage_ItemIsItsOwnContainer()
    {
        using var host = NewHost();
        var item = Dest("New", "Create");
        var bs = NewBackstage(item);
        host.ShowRoot(bs);
        host.RunUntilIdle();

        Assert.Same(item, bs.ItemContainerGenerator.ContainerFromIndex(0));
    }

    // ───────────────────────────── Increment 2 — rail keyboard nav + Escape ─────────────────────────────

    [Fact] // Down/Up move selection AND focus along the rail (selection follows focus)
    public void Backstage_DownUpNavigatesRail()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("Open", "o"), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();

        Container(bs, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, bs.SelectedIndex);
        Assert.True(Container(bs, 1).IsFocused);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(0, bs.SelectedIndex);
        Assert.True(Container(bs, 0).IsFocused);
    }

    [Fact] // Home/End jump to the first/last selectable destination
    public void Backstage_HomeEndJumpToEnds()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("Open", "o"), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Container(bs, 1).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(2, bs.SelectedIndex);

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, bs.SelectedIndex);
    }

    [Fact] // Down over a non-selectable row (a separator) skips it — lands on the next selectable destination
    public void Backstage_DownSkipsNonSelectable()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("---", "sep", selectable: false), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Container(bs, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.Equal(2, bs.SelectedIndex); // skipped the separator at index 1
        Assert.True(Container(bs, 2).IsFocused);
    }

    [Fact] // a rail-edge arrow (Down at the last row) is CONSUMED — it must not bubble out to switch surfaces
    public void Backstage_RailEdgeArrowConsumed()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        bs.SelectedIndex = 1;      // select + focus the last row (a consistent "focus at the edge" state)
        Container(bs, 1).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.Equal(1, bs.SelectedIndex);        // stayed at the last row (edge arrow had nowhere to go)
        Assert.True(Container(bs, 1).IsFocused);  // focus not lost
    }

    [Fact] // Escape raises BackRequested (the ◂ keyboard twin) so the host closes the surface
    public void Backstage_EscapeRaisesBackRequested()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("Save", "s"));
        var backs = 0;
        bs.BackRequested += (_, _) => backs++;
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Container(bs, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Escape);
        host.RunUntilIdle();

        Assert.Equal(1, backs);
    }

    [Fact] // clicking the ◂ back button raises BackRequested
    public void Backstage_BackButtonRaisesBackRequested()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"));
        var backs = 0;
        bs.BackRequested += (_, _) => backs++;
        host.ShowRoot(bs);
        host.RunUntilIdle();

        // The ◂ back button is the top-left rail cell (row 0). A preceding hover arms the release-click gate (the
        // RibbonGroup-launcher test precedent — a click without a prior mouse-move over the target does not fire Click).
        host.SendMouseMove(1, 0);
        host.RunFrame();
        host.SendClick(1, 0);
        host.RunUntilIdle();

        Assert.Equal(1, backs);
    }

    [Fact] // audit-3: Left/Right on the VERTICAL rail is consumed (a no-op), never handed to the base TabControl's
           // horizontal index nav — which skips only Collapsed, NOT IsSelectable=false rows, and would strand focus on
           // a separator (a dead row with no detail pane)
    public void Backstage_LeftRightDoesNotStrandOnSeparator()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("---", "sep", selectable: false), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Container(bs, 0).Focus(); // the "New" content row; the separator is index 1
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.True(Container(bs, 0).IsFocused);   // focus stayed on the content row
        Assert.False(Container(bs, 1).IsFocused);  // the separator did NOT steal focus
        Assert.Equal(0, bs.SelectedIndex);

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.True(Container(bs, 0).IsFocused);
        Assert.False(Container(bs, 1).IsFocused);
    }

    [Fact] // audit-4: a MODIFIED arrow (Ctrl+Down) is NOT claimed as plain rail nav — rail navigation is unmodified-only,
           // so a modified chord falls through to the base handler / an ancestor binding (base ignores it via `when !ctrl`)
    public void Backstage_ModifiedArrowNotConsumedAsRailNav()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Container(bs, 0).Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow, KeyModifiers.Control); // must NOT move rail selection
        host.RunUntilIdle();

        Assert.Equal(0, bs.SelectedIndex); // unchanged — the modified chord was not treated as rail nav
    }

    // ───────────────────────────── Increment 4 — DisplayMode compaction ─────────────────────────────

    [Fact] // FullScreen (default) shows the ◂ back button; Menu mode collapses it (:backstage-menu compaction)
    public void Backstage_MenuModeCollapsesBackButton()
    {
        using var host = NewHost();
        var bs = NewBackstage(Dest("New", "n"), Dest("Save", "s"));
        host.ShowRoot(bs);
        host.RunUntilIdle();
        Assert.Contains("◂", AllRows(host)); // FullScreen default: back button visible

        bs.DisplayMode = BackstageDisplayMode.Menu;
        host.RunUntilIdle();

        Assert.DoesNotContain("◂", AllRows(host)); // Menu: light-dismissed → back button collapsed
    }

    // ───────────────────────────── Increments 3/4 — BackstageHost ─────────────────────────────

    [Fact] // FullScreen host: ShowAsync opens a maximized modal window that AUTO-FOCUSES the rail (even though the
           // anchor behind it held focus), so Escape (no forced focus) closes it
    public async Task BackstageHost_FullScreen_OpensAutoFocusesRail_AndBackCloses()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus(); // focus sits on the owner BEHIND the modal (the case that used to strand focus)
        host.RunUntilIdle();
        Assert.True(anchor.IsFocused);

        var bs = NewBackstage(Dest("New", "New-detail"), Dest("Save", "Save-detail"));
        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();

        Assert.False(task.IsCompleted);
        Assert.Contains("New-detail", AllRows(host)); // the Backstage took over the window
        Assert.True(Container(bs, 0).IsFocused);       // focus auto-moved INTO the rail's first destination (the modal focus fix)…
        Assert.False(anchor.IsFocused);                // …and off the obscured owner behind it

        host.SendKey(Key.Escape); // ◂ keyboard twin (no forced focus) → BackRequested → the host closes the window
        host.RunUntilIdle();

        await task; // completes on dismissal (the continuation resumes on the UI thread the pump drives)
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact] // Menu host: ShowAsync opens a File-anchored popup that AUTO-FOCUSES the rail; Escape (no forced focus) dismisses it
    public async Task BackstageHost_Menu_OpensAutoFocusesRail_AndDismisses()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        var bs = NewBackstage(Dest("New", "New-detail"), Dest("Save", "Save-detail"));
        bs.DisplayMode = BackstageDisplayMode.Menu;
        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();

        Assert.False(task.IsCompleted);
        Assert.Contains("New", AllRows(host));   // the popup rail rendered
        Assert.True(Container(bs, 0).IsFocused);  // the menu auto-focused its rail (a Popup gets no window activation)

        host.SendKey(Key.Escape); // BackRequested → popup.Close() (works because focus is in the rail)
        host.RunUntilIdle();

        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }
}
