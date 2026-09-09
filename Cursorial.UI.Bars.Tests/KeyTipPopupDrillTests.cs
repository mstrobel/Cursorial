using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Bars.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Bars;

// KeyTip drills INTO popups (keytips-design §6's deferred v2 leg, built 2026-09-09 at the maintainer's ask): a menu
// header opens its submenu and badges the rows; a dropdown-bearing bar control opens its dropdown and badges its
// controls; the toolbar's overflow chevron (badge 0) opens the overflow and badges the pocketed controls. The overlay
// re-stacks directly ABOVE the popup it annotates; Esc pops the level and closes the popup it opened.
public sealed class KeyTipPopupDrillTests
{
    private static UIHeadlessHost NewHost(int w = 80, int h = 16) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static KeyEvent Key_(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null, KeyEventKind kind = KeyEventKind.Down)
        => new() { Key = key, Modifiers = modifiers, Kind = kind, Text = (text ?? string.Empty).AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };

    private static void AltDown(UIHeadlessHost host) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.LeftAlt, KeyModifiers.Alt));
    private static void TypeKeyTip(UIHeadlessHost host, char c) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Character, KeyModifiers.Alt, c.ToString()));
    private static void Escape(UIHeadlessHost host) => host.Application.InputDispatcher.ProcessEvent(Key_(Key.Escape));

    // Runs frames until the parked level lands (bounded).
    private static void SettleLevel(UIHeadlessHost host, KeyTipController controller, int depth)
    {
        for (var i = 0; i < 8 && controller.LevelDepthForTests < depth; i++)
            host.RunFrame();
        Assert.Equal(depth, controller.LevelDepthForTests);
    }

    // The topmost popup surface's index (the drilled popup is the last one opened).
    private static int TopPopupIndex(UIHeadlessHost host)
    {
        var surfaces = host.Application.WindowManager!.Surfaces;
        for (var i = surfaces.Count - 1; i >= 0; i--)
            if (surfaces[i].IsPopup)
                return i;
        return -1;
    }

    private static int OverlayIndex(UIHeadlessHost host)
    {
        var surfaces = host.Application.WindowManager!.Surfaces;
        for (var i = 0; i < surfaces.Count; i++)
            if (surfaces[i].IsHitTestTransparent)
                return i;
        return -1;
    }

    [Fact] // Menu: Alt → F (opens File, badges its rows) → N activates New; the level's badges sit ABOVE the submenu popup.
    public void Menu_HeaderDrillsIntoSubmenu_RowActivates()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        var newItem = new MenuItem { Header = "_New" };
        var openItem = new MenuItem { Header = "_Open" };
        file.Items.Add(newItem);
        file.Items.Add(openItem);
        menu.Items.Add(file);
        menu.Items.Add(new MenuItem { Header = "_Edit" });
        var invoked = false;
        newItem.Click += (_, _) => invoked = true;

        var root = new StackPanel { Orientation = Orientation.Vertical, Children = { menu, new TextBox() } };
        host.ShowRoot(root);
        host.RunUntilIdle();

        AltDown(host);
        Assert.Equal(1, controller.LevelDepthForTests);
        TypeKeyTip(host, 'F');                    // drill File: the submenu opens
        SettleLevel(host, controller, 2);
        Assert.True(file.IsSubmenuOpen);

        var badge = controller.BadgeForTargetForTests(newItem);
        Assert.NotNull(badge);
        Assert.Equal(Visibility.Visible, badge!.Visibility);
        Assert.True(TopPopupIndex(host) >= 0 && OverlayIndex(host) > TopPopupIndex(host), "the badge overlay stacks above the submenu popup");

        TypeKeyTip(host, 'N');                    // activate New
        host.RunUntilIdle();
        Assert.True(invoked);
        Assert.False(controller.IsActive);
    }

    [Fact] // Menu: Esc inside the submenu level pops it AND closes the submenu; the header level is matchable again.
    public void Menu_EscInSubmenuLevel_ClosesTheSubmenu_AndReDrills()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var menu = new Menu();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "_New" });
        var edit = new MenuItem { Header = "_Edit" };
        var undo = new MenuItem { Header = "_Undo" };
        edit.Items.Add(undo);
        menu.Items.Add(file);
        menu.Items.Add(edit);
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { menu, new TextBox() } });
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'F');
        SettleLevel(host, controller, 2);
        Assert.True(file.IsSubmenuOpen);

        Escape(host);                             // pop: back to the headers, File closed
        host.RunUntilIdle();
        Assert.Equal(1, controller.LevelDepthForTests);
        Assert.False(file.IsSubmenuOpen);
        Assert.True(controller.IsActive);

        TypeKeyTip(host, 'E');                    // a sibling header drills fine afterwards
        SettleLevel(host, controller, 2);
        Assert.True(edit.IsSubmenuOpen);
        Assert.NotNull(controller.BadgeForTargetForTests(undo));
    }

    [Fact] // Ribbon: Alt → tab → group → a dropdown control's letter OPENS its dropdown and badges the dropdown's controls.
    public void Ribbon_DropDownControl_DrillsIntoItsDropdown()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var pasteSpecial = new BarButton { Content = "_Special" };
        var pasteValues = new BarButton { Content = "_Values" };
        var dropContent = new StackPanel { Orientation = Orientation.Vertical, Children = { pasteSpecial, pasteValues } };
        var paste = new BarPopupButton { Content = "_Paste", DropDownContent = dropContent };
        var ran = false;
        pasteValues.Click += (_, _) => ran = true;

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        var clipboard = new RibbonGroup { Header = "Clipboard" };
        clipboard.Items.Add(paste);
        home.Groups.Add(clipboard);
        ribbon.Items.Add(home);
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { ribbon, new TextBox() } });
        host.RunUntilIdle();

        AltDown(host);
        TypeKeyTip(host, 'H');
        SettleLevel(host, controller, 2);
        TypeKeyTip(host, 'C');
        SettleLevel(host, controller, 3);
        TypeKeyTip(host, 'P');                    // the dropdown opens, its controls get badges
        SettleLevel(host, controller, 4);
        Assert.True(paste.IsDropDownOpen);
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(pasteValues)!.Visibility);
        Assert.True(TopPopupIndex(host) >= 0 && OverlayIndex(host) > TopPopupIndex(host), "the badge overlay stacks above the dropdown popup");

        TypeKeyTip(host, 'V');
        host.RunUntilIdle();
        Assert.True(ran);
        Assert.False(controller.IsActive);
    }

    [Fact] // Toolbar: the overflow chevron carries badge 0; it opens the overflow and the pocketed control gets its badge.
    public void Toolbar_OverflowChevron_DrillsIntoTheOverflow()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var visible = new BarButton { Content = "Xut" };
        var overflowed = new BarButton { Content = "Zettings" };
        Toolbar.SetOverflowMode(overflowed, ToolbarOverflowMode.Always);
        var toolbar = new Toolbar();
        toolbar.Items.Add(visible);
        toolbar.Items.Add(overflowed);
        var ran = false;
        overflowed.Click += (_, _) => ran = true;
        host.ShowRoot(new StackPanel { Orientation = Orientation.Vertical, Children = { toolbar, new TextBox() } });
        host.RunUntilIdle();
        Assert.True(toolbar.HasOverflow);

        AltDown(host);
        Assert.NotNull(controller.BadgeForTargetForTests(toolbar.OverflowToggleForTests!));
        TypeKeyTip(host, '0');                    // open the overflow
        SettleLevel(host, controller, 2);
        Assert.True(toolbar.IsOverflowOpen);
        Assert.Equal(Visibility.Visible, controller.BadgeForTargetForTests(overflowed)!.Visibility);

        TypeKeyTip(host, 'Z');
        host.RunUntilIdle();
        Assert.True(ran);
        Assert.False(controller.IsActive);
    }

    [Fact] // Scoping: with a window ACTIVE over the root ribbon, Alt badges the window's bar, not the ribbon behind it.
    public void ActiveWindow_ScopesTheOverlay_ToItsOwnBars()
    {
        using var host = NewHost();
        var controller = host.Application.EnableKeyTips();

        var ribbon = new Ribbon();
        var home = new RibbonTab { Header = "Home" };
        home.Groups.Add(new RibbonGroup { Header = "Font", Items = { new Button { Content = "Bold" } } });
        ribbon.Items.Add(home);
        host.ShowRoot(ribbon);
        host.RunUntilIdle();

        var dialogButton = new BarButton { Content = "Xut" };
        var window = new Window { Content = new StackPanel { Orientation = Orientation.Vertical, Children = { new Toolbar { Items = { dialogButton } }, new TextBox() } } };
        window.Show(host.Application.WindowManager!);
        host.RunUntilIdle();

        AltDown(host);
        Assert.True(controller.IsActive);
        Assert.NotNull(controller.BadgeForTargetForTests(dialogButton));                       // the dialog's bar is badged…
        Assert.Null(controller.BadgeForTargetForTests(home));                                  // …the ribbon behind it is not
    }
}
