using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// The Ribbon Quick Access Toolbar (QAT): a caption-row Toolbar of high-frequency commands (the SAME BarCommands the
// ribbon groups bind) + a customize checklist dropdown + an above/below placement toggle. Reuses Toolbar; a ribbon
// that never populates the QAT shows no caption row (byte-compatible with the P2 ribbon).
public sealed class RibbonQuickAccessTests
{
    private const int H = 12;

    private static UITestHost NewHost(int w = 64, int h = H) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static string AllRows(UITestHost host) =>
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
        Assert.Equal(1, ribbon.ActiveQuickAccessToolbarForTests!.Items.Count);

        ribbon.QuickAccessCommands.Add(new BarCommand(() => { }) { Text = "Print" });
        host.RunUntilIdle();
        Assert.Equal(2, ribbon.ActiveQuickAccessToolbarForTests!.Items.Count);
        Assert.Contains("Print", AllRows(host));

        ribbon.QuickAccessCommands.Remove(save);
        host.RunUntilIdle();
        Assert.Equal(1, ribbon.ActiveQuickAccessToolbarForTests!.Items.Count);
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
        Assert.Equal(1, above.Items.Count);

        ribbon.QuickAccessPlacement = RibbonQuickAccessPlacement.BelowRibbon;
        host.RunUntilIdle();

        var below = ribbon.ActiveQuickAccessToolbarForTests!;
        Assert.NotSame(above, below);           // a different toolbar host
        Assert.Equal(1, below.Items.Count);      // the command moved to it
        Assert.Equal(0, above.Items.Count);      // and left the above host
        Assert.Contains("Save", AllRows(host));
    }
}
