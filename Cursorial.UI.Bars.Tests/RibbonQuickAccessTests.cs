using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Bars;

// The Ribbon Quick Access Toolbar (QAT): a caption-row Toolbar of high-frequency commands (the SAME BarCommands the
// ribbon groups bind) + a customize checklist dropdown + an above/below placement toggle. Reuses Toolbar; a ribbon
// that never populates the QAT shows no caption row (byte-compatible with the P2 ribbon).
public sealed class RibbonQuickAccessTests
{
    private const int H = 12;

    private static UIHeadlessHost NewHost(int w = 64, int h = H) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static string AllRows(UIHeadlessHost host) =>
        string.Join("\n", Enumerable.Range(0, H).Select(host.GetRowText));

    private static RibbonTab Tab(string header, params (string name, string[] items)[] groups)
    {
        var tab = new RibbonTab { Header = header };
        foreach (var (name, items) in groups)
        {
            var group = new RibbonGroup { Header = name };
            foreach (var item in items)
                group.Items.Add(new BarButton { Content = item });
            tab.Groups.Add(group);
        }
        return tab;
    }

    private static Ribbon NewRibbon()
    {
        var ribbon = new Ribbon();
        ribbon.Items.Add(Tab("Home", ("Clipboard", ["Paste", "Cut"])));
        ribbon.Items.Add(Tab("Insert", ("Tables", ["Table"])));
        return ribbon;
    }

    [Fact] // a ribbon that never uses the QAT renders WITHOUT a caption row (the customize ▾ is absent) — P2 parity
    public void Ribbon_WithoutQuickAccess_HasNoCaptionRow()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.DoesNotContain("▾", AllRows(host)); // no caption row → no customize glyph
        Assert.Contains("Home", AllRows(host));    // the strip still renders
    }

    [Fact] // populating QuickAccessCommands shows the caption row with the QAT commands + the customize ▾
    public void QuickAccess_RendersCommandsInCaption()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Undo" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var all = AllRows(host);
        Assert.Contains("Save", all);
        Assert.Contains("Undo", all);
        Assert.Contains("▾", all); // the customize opener
        Assert.Equal(2, ribbon.ActiveQuickAccessToolbarForTests!.Items.Count);
    }

    [Fact] // adding/removing a QuickAccessCommand reflects live into the QAT toolbar
    public void QuickAccess_MembershipReflectsLive()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        var save = new BarCommand(() => { }) { Text = "Save" };
        ribbon.QuickAccessCommands.Add(save);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.Single(ribbon.ActiveQuickAccessToolbarForTests!.Items);

        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Print" });
        host.RunUntilIdle();
        Assert.Equal(2, ribbon.ActiveQuickAccessToolbarForTests!.Items.Count);
        Assert.Contains("Print", AllRows(host));

        ribbon.QuickAccessCommands.Remove(save);
        host.RunUntilIdle();
        Assert.Single(ribbon.ActiveQuickAccessToolbarForTests!.Items);
        Assert.DoesNotContain("Save", AllRows(host));
    }

    [Fact] // a checkable BarCommand generates a BarToggleButton (not a BarButton)
    public void QuickAccess_CheckableCommand_BecomesToggleButton()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(_ => { }) { Text = "Bold", IsCheckable = true });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.IsType<BarToggleButton>(ribbon.ActiveQuickAccessToolbarForTests!.Items[0]);
    }

    [Fact] // the customize checklist has one row per candidate, checked = currently on the QAT
    public void QuickAccess_CustomizeChecklist_OpensWithCheckedStates()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        var save = new BarCommand(() => { }) { Text = "Save" };
        var print = new BarCommand(() => { }) { Text = "Print" };
        ribbon.QuickAccessCandidates.Add(save);
        ribbon.QuickAccessCandidates.Add(print);
        ribbon.QuickAccessCommands.Add(save); // Save is ON, Print is OFF
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.QatPopupForTests!.IsOpen = true; // the ▾ toggles this; here we open it directly (its own test covers the click)
        host.RunUntilIdle();

        var checklist = ribbon.QatChecklistForTests!;
        Assert.IsType<CheckBox>(checklist.Children[0]);
        Assert.True(((CheckBox) checklist.Children[0]).IsChecked == true);   // Save is on the QAT
        Assert.False(((CheckBox) checklist.Children[1]).IsChecked == true);  // Print is off
    }

    [Fact] // keyboard entry: opening the customize checklist moves focus INTO the first row, and Up/Down navigate rows
    public void QuickAccess_CustomizeChecklist_KeyboardEntry_FocusesFirstRow_AndArrowsNavigate()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" });
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Print" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.QatPopupForTests!.IsOpen = true; // the ▾ toggles this (its own test covers the click path)
        host.RunUntilIdle();

        var checklist = ribbon.QatChecklistForTests!;
        Assert.True(((CheckBox) checklist.Children[0]).IsFocused); // focus moved INTO the checklist on open

        host.SendKey(Cursorial.Input.Key.DownArrow);               // Contained directional container ⇒ Down moves to the next row
        host.RunUntilIdle();
        Assert.True(((CheckBox) checklist.Children[1]).IsFocused);
    }

    [Fact] // keyboard entry with NO candidate rows must NOT park focus on the "More Commands…" dismiss action (a first
           // Enter would then close the menu); with only commands (:has-qat) the checklist opens navigable but unfocused
    public void QuickAccess_CustomizeChecklist_KeyboardEntry_NoCandidates_DoesNotFocusDismissAction()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" }); // :has-qat, but zero CANDIDATES
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.QatPopupForTests!.IsOpen = true;
        host.RunUntilIdle();

        // children are exactly [BarSeparator][BarButton "More Commands…"][CheckBox "Show Below"] — no candidate rows.
        var checklist = ribbon.QatChecklistForTests!;
        var more = checklist.Children.OfType<BarButton>().Single();
        Assert.False(more.IsFocused);                                          // did NOT auto-focus the dismiss action
        Assert.DoesNotContain(checklist.Children.OfType<CheckBox>(), c => c.IsFocused); // nor the placement toggle
    }

    [Fact] // tab order: the trailing QAT is reached AFTER the tabs — tabbing into the ribbon lands on a tab, not the QAT
    public void QuickAccess_TabOrder_TabsPrecedeQat()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        var outside = new BarButton { Content = "Outside" };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(outside);
        root.Children.Add(ribbon);
        host.ShowRoot(root);
        host.RunUntilIdle();

        outside.Focus();
        host.RunUntilIdle();
        host.SendKey(Cursorial.Input.Key.Tab); // into the ribbon
        host.RunUntilIdle();

        var focused = host.Application.FocusManager.FocusedElement;
        Assert.True(IsWithinRibbonTab(focused), $"expected the first ribbon stop to be a tab, got {focused?.GetType().Name}");
    }

    private static bool IsWithinRibbonTab(UIElement? element)
    {
        for (var node = element; node is not null; node = node.VisualParent)
        {
            if (node is RibbonTab)
                return true;
        }

        return false;
    }

    [Fact] // the customize checklist's rule is a HORIZONTAL separator (a vertical-list divider), not the default upright │
    public void QuickAccess_ChecklistSeparator_IsHorizontal()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var separator = ribbon.QatChecklistForTests!.Children.OfType<BarSeparator>().Single();
        Assert.Equal(Orientation.Horizontal, separator.Orientation);
    }

    [Fact] // clicking the ▾ opens the checklist popup (the customize opener owns the toggle)
    public void QuickAccess_CustomizeButton_OpensChecklist()
    {
        using var host = NewHost(w: 40);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var origin = ribbon.QatCustomizeForTests!.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column, origin.Row); // arm the release-click gate
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row);
        host.RunUntilIdle();

        Assert.True(ribbon.QatPopupForTests!.IsOpen);
    }

    [Fact] // toggling a checklist row adds/removes the command from the QAT (membership two-way)
    public void QuickAccess_ToggleChecklistRow_AddsAndRemovesMembership()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        var print = new BarCommand(() => { }) { Text = "Print" };
        ribbon.QuickAccessCandidates.Add(print);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.QatPopupForTests!.IsOpen = true;
        host.RunUntilIdle();
        var printRow = (CheckBox) ribbon.QatChecklistForTests!.Children[0];
        printRow.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Enter); // activate the focused row → toggle ON
        host.RunUntilIdle();
        Assert.Contains(print, ribbon.QuickAccessCommands);

        host.SendKey(Key.Enter); // toggle OFF
        host.RunUntilIdle();
        Assert.DoesNotContain(print, ribbon.QuickAccessCommands);
    }

    [Fact] // the "More Commands…" row raises QuickAccessMoreCommandsRequested and closes the dropdown
    public void QuickAccess_MoreCommands_RaisesEvent()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" });
        var raised = 0;
        ribbon.QuickAccessMoreCommandsRequested += (_, _) => raised++;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.QatPopupForTests!.IsOpen = true;
        host.RunUntilIdle();
        // children: [Save CheckBox][BarSeparator][More Commands… BarButton][Show Below CheckBox]
        var more = (BarButton) ribbon.QatChecklistForTests!.Children[2];
        more.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Enter); // activate More Commands…
        host.RunUntilIdle();

        Assert.Equal(1, raised);
        Assert.False(ribbon.QatPopupForTests!.IsOpen);
    }

    [Fact] // placing the QAT below the ribbon moves the commands into the below-band host
    public void QuickAccess_PlacementBelow_MovesToBelowHost()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        var above = ribbon.ActiveQuickAccessToolbarForTests!;
        Assert.Single(above.Items);

        ribbon.QuickAccessPlacement = RibbonQuickAccessPlacement.BelowRibbon;
        host.RunUntilIdle();

        var below = ribbon.ActiveQuickAccessToolbarForTests!;
        Assert.NotSame(above, below);        // a different toolbar host
        Assert.Single(below.Items);          // the command moved to it
        Assert.Empty(above.Items);           // and left the above host
        Assert.Contains("Save", AllRows(host));
    }

    // ───────────────────────────── audit follow-ups ─────────────────────────────

    [Fact] // audit-3: the checklist renders on FIRST open (rows built eagerly, before the surface is placed/sized)
    public void QuickAccess_ChecklistRendersOnFirstOpen()
    {
        using var host = NewHost(w: 40);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "SaveDoc" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        ribbon.QatPopupForTests!.IsOpen = true; // first open
        host.RunUntilIdle();

        var all = AllRows(host);
        Assert.Contains("SaveDoc", all);       // the candidate row renders (not a clipped sliver)
        Assert.Contains("More", all);          // the standing "More Commands…" row renders too
    }

    [Fact] // audit-5: clicking the ▾ a second time CLOSES the checklist (KeepOpenOnAnchorPress defeats the reopen race)
    public void QuickAccess_CustomizeButton_SecondClickCloses()
    {
        using var host = NewHost(w: 40);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var origin = ribbon.QatCustomizeForTests!.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row); // open
        host.RunUntilIdle();
        Assert.True(ribbon.QatPopupForTests!.IsOpen);

        host.SendMouseMove(origin.Column, origin.Row);
        host.RunFrame();
        host.SendClick(origin.Column, origin.Row); // close (must NOT reopen)
        host.RunUntilIdle();
        Assert.False(ribbon.QatPopupForTests!.IsOpen);
    }

    [Fact] // audit-2: a checkable QAT toggle's checked state survives an above↔below placement flip (instances moved)
    public void QuickAccess_CheckableToggleState_SurvivesPlacementFlip()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(_ => { }) { Text = "Bold", IsCheckable = true });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var toggle = Assert.IsType<BarToggleButton>(ribbon.ActiveQuickAccessToolbarForTests!.Items[0]);
        toggle.IsChecked = true;
        host.RunUntilIdle();

        ribbon.QuickAccessPlacement = RibbonQuickAccessPlacement.BelowRibbon;
        host.RunUntilIdle();

        var moved = Assert.IsType<BarToggleButton>(ribbon.ActiveQuickAccessToolbarForTests!.Items[0]);
        Assert.Same(toggle, moved);            // the SAME instance moved hosts (not rebuilt)
        Assert.True(moved.IsChecked == true);  // …so its checked state survived
    }

    [Fact] // audit-1: detaching the ribbon with the customize popup open closes it (no stranded WM surface) — no crash
    public void QuickAccess_PopupClosesOnRibbonDetach()
    {
        using var host = NewHost();
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        var popup = ribbon.QatPopupForTests!;
        popup.IsOpen = true;
        host.RunUntilIdle();
        Assert.True(popup.IsOpen);

        host.ShowRoot(new StackPanel()); // detach the ribbon
        host.RunUntilIdle();

        Assert.False(popup.IsOpen); // the popup closed on detach (surface released)
    }

    // ───────────────────────── collapse-first overflow (bars guide §"QAT placement in the compact ribbon") ─────────

    [Fact] // wide strip: the inline QAT shows beside the tabs — not collapsed, the ⋯▾ button hidden
    public void QuickAccess_WideStrip_InlineNotCollapsed()
    {
        using var host = NewHost(w: 80);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(ribbon.IsQuickAccessCollapsedForTests);              // inline fits at 80 cells
        Assert.False(ribbon.QatCollapsedButtonForTests!.IsEffectivelyVisible); // ⋯▾ hidden
        Assert.Empty(ribbon.QatCollapsedBarForTests!.Items);             // commands NOT in the collapsed bar
    }

    [Fact] // tight strip: the QAT collapses FIRST (before the tabs) to the ⋯▾ popup button; the commands re-host into
           // its mini-bar and the inline cluster hides — the design's degraded tier
    public void QuickAccess_TightStrip_CollapsesToPopupButton()
    {
        using var host = NewHost(w: 80);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.False(ribbon.IsQuickAccessCollapsedForTests);

        host.SendResize(16, 12); // tight — the inline QAT can't fit beside File/Home/Insert
        host.RunUntilIdle();

        Assert.True(ribbon.IsQuickAccessCollapsedForTests);                  // collapsed
        Assert.True(ribbon.QatCollapsedButtonForTests!.IsEffectivelyVisible); // ⋯▾ shown
        Assert.Same(ribbon.QatCollapsedBarForTests, ribbon.ActiveQuickAccessToolbarForTests); // commands re-hosted…
        Assert.Single(ribbon.QatCollapsedBarForTests!.Items);               // …into the ⋯▾ popup mini-bar
    }

    [Fact] // widening back un-collapses: the commands return to the inline cluster and the ⋯▾ hides again
    public void QuickAccess_CollapsedThenWidened_UnCollapses()
    {
        using var host = NewHost(w: 16);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        host.ShowRoot(ribbon);
        host.RunUntilIdle();
        Assert.True(ribbon.IsQuickAccessCollapsedForTests); // starts tight ⇒ collapsed

        host.SendResize(80, 12);
        host.RunUntilIdle();

        Assert.False(ribbon.IsQuickAccessCollapsedForTests);                   // un-collapsed
        Assert.False(ribbon.QatCollapsedButtonForTests!.IsEffectivelyVisible); // ⋯▾ hidden
        Assert.Empty(ribbon.QatCollapsedBarForTests!.Items);                   // commands left the popup mini-bar
    }

    [Fact] // a ribbon with NO QAT never collapses (the ⋯▾ never appears), even at a tight width
    public void QuickAccess_NoQat_NeverCollapses()
    {
        using var host = NewHost(w: 12);
        var ribbon = NewRibbon(); // no QuickAccessCommands / Candidates
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(ribbon.IsQuickAccessCollapsedForTests);
        Assert.False(ribbon.QatCollapsedButtonForTests!.IsEffectivelyVisible);
    }

    [Fact] // below-ribbon placement has its own full-width row — collapse never applies (no ⋯▾) even when tight
    public void QuickAccess_BelowPlacement_NeverCollapses()
    {
        using var host = NewHost(w: 16);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        ribbon.QuickAccessPlacement = RibbonQuickAccessPlacement.BelowRibbon;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(ribbon.IsQuickAccessCollapsedForTests);
        Assert.False(ribbon.QatCollapsedButtonForTests!.IsEffectivelyVisible);
        Assert.NotSame(ribbon.QatCollapsedBarForTests, ribbon.ActiveQuickAccessToolbarForTests); // hosted below, NOT in ⋯▾
    }

    [Fact] // regression: below-ribbon placement keeps the customize ▾ accessible (only the COMMANDS relocate to the
           // below band; the ▾ stays at its stable strip-trailing home) — :qat-below must not strand the whole cluster
    public void QuickAccess_BelowPlacement_CustomizeButtonStaysAccessible()
    {
        using var host = NewHost(w: 80);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        ribbon.QuickAccessPlacement = RibbonQuickAccessPlacement.BelowRibbon;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.True(ribbon.QatCustomizeForTests!.IsEffectivelyVisible); // the ▾ did NOT vanish in below mode
        Assert.Contains("▾", AllRows(host));                           // …and it renders

        ribbon.QatPopupForTests!.IsOpen = true; // its checklist popup still opens
        host.RunUntilIdle();
        Assert.True(ribbon.QatPopupForTests!.IsOpen);
    }

    [Fact] // audit: below mode collapses the now-EMPTY above toolbar (an empty Toolbar otherwise reserves ~3 cells for
           // its Hidden overflow chevron and paints a discolored box left of the ▾) — the ▾ stays, the box is gone
    public void QuickAccess_BelowPlacement_EmptyAboveToolbarCollapsed()
    {
        using var host = NewHost(w: 80);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Save" });
        ribbon.QuickAccessPlacement = RibbonQuickAccessPlacement.BelowRibbon;
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(ribbon.QatAboveForTests!.IsEffectivelyVisible); // the emptied above toolbar collapsed (no box)
        Assert.Equal(0, ribbon.QatAboveForTests!.DesiredSize.Columns);
        Assert.True(ribbon.QatCustomizeForTests!.IsEffectivelyVisible); // …the customize ▾ still shows
    }

    [Fact] // audit: a CANDIDATES-only ribbon (nothing pinned) must NOT collapse into an EMPTY ⋯▾ popup — collapse is
           // gated on real commands (what the popup hosts); the customize ▾ stays inline instead
    public void QuickAccess_CandidatesOnly_TightWidth_DoesNotCollapse()
    {
        using var host = NewHost(w: 16);
        var ribbon = NewRibbon();
        ribbon.QuickAccessCandidates.Add(new BarCommand(() => { }) { Text = "Save" }); // a candidate, NOT a command
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        Assert.False(ribbon.IsQuickAccessCollapsedForTests);                   // no commands ⇒ never collapses
        Assert.False(ribbon.QatCollapsedButtonForTests!.IsEffectivelyVisible); // ⋯▾ hidden (would be empty)
        Assert.True(ribbon.QatCustomizeForTests!.IsEffectivelyVisible);        // customize ▾ stays inline
    }
}
