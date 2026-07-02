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

    [Fact] // invoking a command button inside a destination's DETAIL PANE closes the Backstage (raises BackRequested)
           // — the Office "act and return to the document" model; the command still runs (deferred close)
    public void Backstage_DetailPaneButtonInvoke_ClosesBackstage()
    {
        using var host = NewHost();
        var ran = 0;
        var action = new BarButton { Content = "Save Now", Command = new BarCommand(() => ran++) };
        var pane = new StackPanel { Orientation = Orientation.Vertical };
        pane.Children.Add(new TextBlock { Text = "Save the document." });
        pane.Children.Add(action);
        var bs = new Backstage();
        bs.Items.Add(new BackstageItem { Header = "Save", Content = pane });
        var backs = 0;
        bs.BackRequested += (_, _) => backs++;
        host.ShowRoot(bs);
        host.RunUntilIdle();

        // click the detail-pane button (it renders in the content host since "Save" is auto-selected)
        var origin = action.TranslateToScreen(1, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(1, ran);   // the command ran…
        Assert.Equal(1, backs); // …and the Backstage asked to close (BackRequested)
    }

    // repro (live path): the detail-pane button close must fire through the REAL hosted surface (a modal Window /
    // Popup opened by BackstageHost), not only when the Backstage is the app root. The gallery reported it failing
    // in the terminal while ShowRoot-based tests passed — the untested gap is the hosted-surface path below.
    [Fact]
    public async Task BackstageHost_FullScreen_DetailPaneButtonInvoke_ClosesWindow()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        var ran = 0;
        var action = new BarButton { Content = "◆ Save", Command = new BarCommand(() => ran++) };
        var pane = new StackPanel { Orientation = Orientation.Vertical };
        pane.Children.Add(new TextBlock { Text = "Save the document." });
        pane.Children.Add(action);
        var bs = new Backstage();
        bs.Items.Add(new BackstageItem { Header = "Save", Content = pane });

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();
        Assert.False(task.IsCompleted); // the modal is up

        var origin = action.TranslateToScreen(1, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(1, ran);            // the command ran…
        await task;                      // …and the modal window closed (the host task completed)
        Assert.True(task.IsCompletedSuccessfully);

        // The surface must be visually GONE too (a logical close that never re-renders looks exactly like
        // "it won't close" to the user — the gap the ShowRoot/task-only tests missed).
        host.RunUntilIdle();
        Assert.DoesNotContain("Save the document.", AllRows(host));
        Assert.Contains("File", AllRows(host)); // the document (the anchor) is visible again
    }

    [Fact]
    public async Task BackstageHost_Menu_DetailPaneButtonInvoke_ClosesPopup()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        var ran = 0;
        var action = new BarButton { Content = "◆ Save", Command = new BarCommand(() => ran++) };
        var pane = new StackPanel { Orientation = Orientation.Vertical };
        pane.Children.Add(new TextBlock { Text = "Save the document." });
        pane.Children.Add(action);
        var bs = new Backstage { DisplayMode = BackstageDisplayMode.Menu };
        bs.Items.Add(new BackstageItem { Header = "Save", Content = pane });

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();
        Assert.False(task.IsCompleted);

        var origin = action.TranslateToScreen(1, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(1, ran);
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact] // keyboard invoke (Enter on a focused detail-pane button) must close the hosted modal window
    public async Task BackstageHost_FullScreen_DetailPaneKeyboardInvoke_ClosesWindow()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        var ran = 0;
        var action = new BarButton { Content = "◆ Save", Command = new BarCommand(() => ran++) };
        var pane = new StackPanel { Orientation = Orientation.Vertical };
        pane.Children.Add(new TextBlock { Text = "Save the document." });
        pane.Children.Add(action);
        var bs = new Backstage();
        bs.Items.Add(new BackstageItem { Header = "Save", Content = pane });

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();

        action.Focus();
        host.RunUntilIdle();
        Assert.True(action.IsFocused);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(1, ran);
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact] // keyboard invoke (Enter on a focused detail-pane button) must close the hosted menu popup
    public async Task BackstageHost_Menu_DetailPaneKeyboardInvoke_ClosesPopup()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        var ran = 0;
        var action = new BarButton { Content = "◆ Save", Command = new BarCommand(() => ran++) };
        var pane = new StackPanel { Orientation = Orientation.Vertical };
        pane.Children.Add(new TextBlock { Text = "Save the document." });
        pane.Children.Add(action);
        var bs = new Backstage { DisplayMode = BackstageDisplayMode.Menu };
        bs.Items.Add(new BackstageItem { Header = "Save", Content = pane });

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();

        action.Focus();
        host.RunUntilIdle();
        Assert.True(action.IsFocused);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(1, ran);
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact] // after navigating the rail to a NON-first destination, clicking THAT pane's button must still close
           // (the detail pane swapped content in the persistent PART_ContentHost — the close handler must survive the swap)
    public async Task BackstageHost_Menu_SwappedDestinationButtonInvoke_ClosesPopup()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        BarButton MakePane(string name, out BackstageItem item)
        {
            var action = new BarButton { Content = $"◆ {name}" };
            var pane = new StackPanel { Orientation = Orientation.Vertical };
            pane.Children.Add(new TextBlock { Text = $"{name} detail." });
            pane.Children.Add(action);
            item = new BackstageItem { Header = name, Content = pane };
            return action;
        }

        var savedRan = 0;
        _ = MakePane("New", out var newItem);
        var saveButton = MakePane("Save", out var saveItem);
        saveButton.Command = new BarCommand(() => savedRan++);

        var bs = new Backstage { DisplayMode = BackstageDisplayMode.Menu };
        bs.Items.Add(newItem);
        bs.Items.Add(saveItem);

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();
        Assert.Equal(0, bs.SelectedIndex); // "New" auto-selected

        bs.SelectedIndex = 1;              // navigate to "Save" — the detail pane swaps
        host.RunUntilIdle();

        var origin = saveButton.TranslateToScreen(1, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.Equal(1, savedRan);
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact] // GHOSTING repro: keyboard rail-nav in a POPUP-hosted Backstage must fully repaint the detail zone —
           // the previous destination's detail text must be ERASED, not left ghosting under the new pane (the live
           // symptom: keyboard-select renders garbled while mouse-select is clean, because no pointer motion forces
           // a hover-driven zone re-render).
    public async Task BackstageHost_Menu_KeyboardRailNav_RepaintsDetailZone_NoGhost()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        var bs = new Backstage { DisplayMode = BackstageDisplayMode.Menu };
        bs.Items.Add(new BackstageItem { Header = "New", Content = "Create-a-brand-new-document." });
        bs.Items.Add(new BackstageItem { Header = "Save", Content = "Save-the-current-document." });

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();
        Assert.Equal(0, bs.SelectedIndex);
        Assert.Contains("Create-a-brand-new-document.", AllRows(host));

        // Navigate the rail by KEYBOARD (Down) — selection follows focus, the detail pane swaps to "Save".
        Container(bs, 0).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.Equal(1, bs.SelectedIndex);
        Assert.Contains("Save-the-current-document.", AllRows(host));           // the new pane rendered…
        Assert.DoesNotContain("Create-a-brand-new-document.", AllRows(host));   // …and the OLD pane was erased (no ghost)

        bs.BackRequested += (_, _) => { };
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        await task;
    }

    [Fact] // GHOSTING repro with the gallery's RICH detail pane (StackPanel: title + body + action button) — a
           // multi-child pane in a popup zone is where a partial repaint would hide. Keyboard rail-nav must erase
           // the whole previous pane, not leave any line ghosting.
    public async Task BackstageHost_Menu_KeyboardRailNav_RichPane_NoGhost()
    {
        using var host = NewHost();
        var anchor = new BarButton { Content = "File" };
        host.ShowRoot(anchor);
        anchor.Focus();
        host.RunUntilIdle();

        BackstageItem Dest2(string name, string body)
        {
            var pane = new StackPanel { Orientation = Orientation.Vertical, Margin = new Margins(1, 0) };
            pane.Children.Add(new TextBlock { Text = $"TITLE-{name}", Margin = new Margins(0, 0, 0, 1) });
            pane.Children.Add(new TextBlock { Text = body });
            pane.Children.Add(new BarButton { Content = $"ACT-{name}", Margin = new Margins(0, 1, 0, 0) });
            return new BackstageItem { Header = name, Content = pane };
        }

        var bs = new Backstage { DisplayMode = BackstageDisplayMode.Menu };
        bs.Items.Add(Dest2("New", "Body-New-create-empty."));
        bs.Items.Add(Dest2("Save", "Body-Save-persist-now."));

        var task = BackstageHost.ShowAsync(bs, anchor);
        host.RunUntilIdle();
        Assert.Contains("TITLE-New", AllRows(host));
        Assert.Contains("Body-New-create-empty.", AllRows(host));
        Assert.Contains("ACT-New", AllRows(host));

        Container(bs, 0).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // keyboard rail nav → detail pane swaps New → Save
        host.RunUntilIdle();

        // The new pane is fully present…
        Assert.Contains("TITLE-Save", AllRows(host));
        Assert.Contains("Body-Save-persist-now.", AllRows(host));
        Assert.Contains("ACT-Save", AllRows(host));
        // …and NO line of the old pane ghosts.
        Assert.DoesNotContain("TITLE-New", AllRows(host));
        Assert.DoesNotContain("Body-New-create-empty.", AllRows(host));
        Assert.DoesNotContain("ACT-New", AllRows(host));

        bs.BackRequested += (_, _) => { };
        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        await task;
    }

    [Fact] // REGRESSION: the detail-pane content must inherit its foreground from PART_ContentHost (the visual host,
           // TextBrush), NOT from its logical owner the BackstageItem (whose state-dependent rail-label ink is dark and
           // invisible on the detail surface — and flips with the rail row's :focus). WPF parity: hosted content inherits
           // through the visual tree. Was: body inherited the rail item's ink (rgb 23,26,38); now: the host's TextBrush.
    public void Backstage_DetailContent_InheritsForegroundFromContentHost_NotRailItem()
    {
        using var host = NewHost();
        var body = new TextBlock { Text = "BODY-TEXT" };
        var pane = new StackPanel { Orientation = Orientation.Vertical };
        pane.Children.Add(body);
        var bs = new Backstage();
        bs.Items.Add(new BackstageItem { Header = "New", Content = pane });
        host.ShowRoot(bs);
        host.RunUntilIdle();

        var contentHost = pane.VisualParent!;   // PART_ContentHost (ContentPresenter, foreground = TextBrush)
        var railItem = pane.LogicalParent!;      // BackstageItem (foreground = dark rail-label ink)

        var bodyFg = TextElement.GetForeground(body);
        Assert.NotNull(bodyFg);
        Assert.Same(TextElement.GetForeground(contentHost), bodyFg); // inherits the HOST's brush…
        Assert.NotSame(TextElement.GetForeground(railItem), bodyFg);  // …not the rail item's

        // And it stays correct when the rail row is focused (the state that used to darken the detail text).
        Container(bs, 0).Focus(FocusNavigationMethod.Directional);
        host.RunUntilIdle();
        Assert.Same(TextElement.GetForeground(contentHost), TextElement.GetForeground(body));
    }

    [Fact] // GUARD for the inheritance-redirect fix: directly-set TabItem content must still inherit DataContext
           // (the redirect re-points ALL inherited properties at the content host — but the host's chain reaches the
           // same TabControl DataContext, so a {Binding} in tab content still resolves).
    public void TabControl_DirectlySetContent_StillInheritsDataContext()
    {
        using var host = NewHost();
        var tc = new Cursorial.UI.Controls.TabControl { DataContext = "HELLO-CTX" };
        var leaf = new TextBlock();
        var pane = new StackPanel();
        pane.Children.Add(leaf);
        tc.Items.Add(new Cursorial.UI.Controls.TabItem { Header = "A", Content = pane });
        host.ShowRoot(tc);
        host.RunUntilIdle();

        // DataContext still inherits down through the content host into the directly-set content (the redirect
        // re-points inheritance at the presenter, whose chain reaches the same TabControl DataContext).
        Assert.Equal("HELLO-CTX", pane.GetValue(UIElement.DataContextProperty));
        Assert.Equal("HELLO-CTX", leaf.GetValue(UIElement.DataContextProperty));
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
