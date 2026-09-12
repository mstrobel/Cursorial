using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Bars.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Bars;

// KeyTips FOLLOW the keyboard through menus (maintainer, 2026-09-12): with the overlay up, a submenu the arrow keys
// open gets its rows badged, a submenu the arrow keys close pops its level, and a sibling top-level menu reached by
// arrows gets badged too. Plus the QAT customize ▾: badge 00 drills into the checklist, whose check boxes toggle on
// activation.
public sealed class KeyTipMenuNavigationTests
{
    private static UIHeadlessHost NewHost(int w = 80, int h = 16) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static KeyEvent Key_(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null, KeyEventKind kind = KeyEventKind.Down)
        => new() { Key = key, Modifiers = modifiers, Kind = kind, Text = (text ?? string.Empty).AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };

    // Sticky mode (Alt tap): the realistic "Alt, letter, arrows" flow — plain keys afterwards.
    private static void AltTap(UIHeadlessHost host)
    {
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.Alt));
        host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.None, kind: KeyEventKind.Up));
    }

    private static void Type(UIHeadlessHost host, char c) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.None, c.ToString()));

    private static void Press(UIHeadlessHost host, Key key)
    {
        host.Application.InputDispatcher.ProcessEvent(Key_(key));
        host.RunUntilIdle();
    }

    private static void SettleLevel(UIHeadlessHost host, KeyTipController controller, int depth)
    {
        for (var i = 0; i < 8 && controller.LevelDepthForTests != depth; i++)
            host.RunFrame();
        Assert.Equal(depth, controller.LevelDepthForTests);
    }

    private static (Menu Menu, MenuItem File, MenuItem Recent, MenuItem RecentA, MenuItem Edit, MenuItem Undo) NewMenu()
    {
        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        var recent = new MenuItem { Header = "_Recent" };
        var recentA = new MenuItem { Header = "_Alpha.txt" };
        recent.Items.Add(recentA);
        file.Items.Add(new MenuItem { Header = "_New" });
        file.Items.Add(recent);
        var edit = new MenuItem { Header = "_Edit" };
        var undo = new MenuItem { Header = "_Undo" };
        edit.Items.Add(undo);
        menu.Items.Add(file);
        menu.Items.Add(edit);
        return (menu, file, recent, recentA, edit, undo);
    }

    [Fact] // Alt, F badges File's rows; Right on the Recent row opens its submenu BY KEYBOARD → its rows are badged; Left closes it → back to File's rows.
    public void ArrowKeys_OpenAndCloseANestedSubmenu_TheLevelFollows()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();
        var (menu, file, recent, recentA, _, _) = NewMenu();
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { menu, new TextBox() } });
        host.RunUntilIdle();

        AltTap(host);
        Type(host, 'F');                          // drill File (submenu opens, focus on its first row)
        SettleLevel(host, controller, 2);
        Assert.True(file.IsSubmenuOpen);

        Press(host, Key.DownArrow);               // New → Recent
        Assert.True(recent.IsFocused);
        Press(host, Key.RightArrow);              // opens Recent's submenu by keyboard
        Assert.True(recent.IsSubmenuOpen);
        SettleLevel(host, controller, 3);         // the overlay followed: a level over Recent's rows
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(recentA)!.Visibility);

        Press(host, Key.LeftArrow);               // closes Recent by keyboard
        Assert.False(recent.IsSubmenuOpen);
        SettleLevel(host, controller, 2);         // popped back to File's rows
        Assert.NotNull(controller.BadgeForTargetForTests(recent));
        Assert.True(controller.IsActive);

        var invoked = false;
        recentA.Click += (_, _) => invoked = true;
        Press(host, Key.RightArrow);              // re-open Recent…
        SettleLevel(host, controller, 3);
        Type(host, 'A');                          // …and its badge still activates
        host.RunUntilIdle();
        Assert.True(invoked);
        Assert.False(controller.IsActive);
    }

    [Fact] // Alt, F, then Left (back to the File header) and over to Edit with Down: the sibling menu's rows get badged.
    public void ArrowKeys_ReachASiblingTopLevelMenu_ItsRowsAreBadged()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();
        var (menu, file, _, _, edit, undo) = NewMenu();
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { menu, new TextBox() } });
        host.RunUntilIdle();

        AltTap(host);
        Type(host, 'F');
        SettleLevel(host, controller, 2);

        Press(host, Key.LeftArrow);               // on a File row: ascend — closes File, focuses its header
        Assert.False(file.IsSubmenuOpen);
        SettleLevel(host, controller, 1);         // File's level popped: the headers are badged again
        Assert.NotNull(controller.BadgeForTargetForTests(edit));

        Press(host, Key.RightArrow);              // File header → Edit header
        Assert.True(edit.IsFocused);
        Press(host, Key.DownArrow);               // opens Edit's submenu by keyboard
        Assert.True(edit.IsSubmenuOpen);
        SettleLevel(host, controller, 2);         // the overlay followed into the sibling menu
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(undo)!.Visibility);

        var invoked = false;
        undo.Click += (_, _) => invoked = true;
        Type(host, 'U');
        host.RunUntilIdle();
        Assert.True(invoked);
    }

    [Fact] // The QAT customize ▾ carries badge 00 and drills into the checklist; a check box row TOGGLES on activation.
    public void QatCustomize_Badge00_DrillsIntoTheChecklist_AndACheckBoxToggles()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        home.Groups.Add(new RibbonGroup { Header = "Clipboard", Items = { new BarButton { Content = "Paste" } } });
        ribbon.Items.Add(home);
        var save = new BarCommand(() => { }) { Text = "_Save" };
        var print = new BarCommand(() => { }) { Text = "_Print" };
        ribbon.QuickAccessCandidates.Add(save);
        ribbon.QuickAccessCandidates.Add(print);
        ribbon.QuickAccessCommands.Add(save);     // Save ON, Print OFF
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, new TextBox() } });
        host.RunUntilIdle();

        AltTap(host);
        var customize = ribbon.QatCustomizeForTests!;
        var badge = controller.BadgeForTargetForTests(customize);
        Assert.NotNull(badge);
        Assert.Equal("00", badge!.KeyTipText);

        Type(host, '0');
        Type(host, '0');                          // commits "00": the checklist opens
        SettleLevel(host, controller, 2);
        Assert.True(ribbon.QatPopupForTests!.IsOpen);
        var printRow = (CheckBox)ribbon.QatChecklistForTests!.Children[1];
        Assert.False(printRow.IsChecked == true);
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(printRow)!.Visibility);

        Type(host, 'P');                          // activate the Print row: it TOGGLES (not focus-only)
        host.RunUntilIdle();
        Assert.True(printRow.IsChecked == true);
        Assert.Contains(print, ribbon.QuickAccessCommands);
        Assert.False(controller.IsActive);
    }

    [Fact] // A checkable menu item keeps its menu open by design; a badge toggling it keeps the OVERLAY at that level too,
           // so the next badge can toggle another item — no exit, no menu teardown on the next Alt.
    public void CheckableMenuItem_BadgeToggles_MenuAndOverlayStay()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var menu = new Menu();
        var view = new MenuItem { Header = "_View" };
        var wrap = new MenuItem { Header = "_Wrap", IsCheckable = true };
        var ruler = new MenuItem { Header = "_Ruler", IsCheckable = true };
        var close = new MenuItem { Header = "_Close" };
        view.Items.Add(wrap);
        view.Items.Add(ruler);
        view.Items.Add(close);
        menu.Items.Add(view);
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { menu, new TextBox() } });
        host.RunUntilIdle();

        AltTap(host);
        Type(host, 'V');
        SettleLevel(host, controller, 2);

        Type(host, 'W');                          // toggle Wrap: the menu stays, the overlay stays at View's rows
        host.RunUntilIdle();
        Assert.True(wrap.IsChecked);
        Assert.True(view.IsSubmenuOpen);
        Assert.True(controller.IsActive);
        Assert.Equal(2, controller.LevelDepthForTests);
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(ruler)!.Visibility);

        Type(host, 'R');                          // and the next choice works straight away
        host.RunUntilIdle();
        Assert.True(ruler.IsChecked);
        Assert.True(controller.IsActive);

        var closed = false;
        close.Click += (_, _) => closed = true;
        Type(host, 'C');                          // a plain leaf still activates, dismisses the chain and exits
        host.RunUntilIdle();
        Assert.True(closed);
        Assert.False(view.IsSubmenuOpen);
        Assert.False(controller.IsActive);
    }
}
