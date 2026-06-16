using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C7 — ContextMenu (P9.4d): the popup-rooted vertical menu + the right-click / Key.Menu
// router default. Reuses MenuItem (containers, invoke, submenus) — covered in Section19; here we test the
// ContextMenu host, the attached MenuProperty, and the dispatcher's router-default open/dismiss.
public sealed class Section20_ContextMenu
{
    private static UITestHost Host() => UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 16) });

    private sealed class TestCommand : ICommand
    {
        public bool CanRun = true;
        public int Runs;
        public bool CanExecute(object? parameter) => CanRun;
        public void Execute(object? parameter) => Runs++;
        public event EventHandler? CanExecuteChanged;
    }

    // A root element bearing a ContextMenu, sized to fill the host so a click anywhere hits it.
    private static (UITestHost Host, Border Owner, ContextMenu Menu) RootWithMenu(ContextMenu menu)
    {
        var host = Host();
        var owner = new Border { Width = 30, Height = 10 };
        ContextMenu.SetMenu(owner, menu);
        host.ShowRoot(owner);
        host.RunUntilIdle();
        return (host, owner, menu);
    }

    private static int Popups(UITestHost host) => host.Application.WindowManager!.Popups.Count;

    [Fact] // C7.1: a ContextMenu generates MenuItem containers; a MenuItem item is its own container
    public void C7_1_Containers()
    {
        var ownContainer = new MenuItem { Header = "Cut" };
        var menu = new ContextMenu();
        menu.Items.Add(ownContainer);
        menu.Items.Add("Copy"); // a data item → wrapped in a MenuItem container

        using var host = Host();
        var owner = new Border { Width = 20, Height = 6 };
        ContextMenu.SetMenu(owner, menu);
        host.ShowRoot(owner);
        host.RunUntilIdle();

        menu.Open(owner);
        host.RunUntilIdle();

        Assert.Same(ownContainer, menu.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.IsType<MenuItem>(menu.ItemContainerGenerator.ContainerFromIndex(1)); // data item wrapped
    }

    [Fact] // C7.2: the attached ContextMenu.Menu property round-trips
    public void C7_2_AttachedPropertyRoundTrip()
    {
        var menu = new ContextMenu();
        var element = new Border();
        Assert.Null(ContextMenu.GetMenu(element));
        ContextMenu.SetMenu(element, menu);
        Assert.Same(menu, ContextMenu.GetMenu(element));
    }

    [Fact] // C7.3: a right-click over an element carrying ContextMenu.Menu opens it (the router default)
    public void C7_3_RightClickOpens()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Cut" });
        var (host, _, _) = RootWithMenu(menu);
        using var _h = host;

        Assert.False(menu.IsOpen);
        host.SendClick(5, 4, MouseButton.Right);
        host.RunUntilIdle();

        Assert.True(menu.IsOpen);
        Assert.Equal(1, Popups(host));
    }

    [Fact] // C7.4: a right-click over an element with NO ContextMenu opens nothing
    public void C7_4_RightClickNoMenuNoOp()
    {
        using var host = Host();
        host.ShowRoot(new Border { Width = 30, Height = 10 });
        host.RunUntilIdle();

        host.SendClick(5, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.Equal(0, Popups(host));
    }

    [Fact] // C7.5: the Key.Menu context key opens the focused element's ContextMenu
    public void C7_5_MenuKeyOpens()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Cut" });
        var host = Host();
        using var _h = host;
        var owner = new Button { Content = "target" }; // focusable so Key.Menu has a focused target
        ContextMenu.SetMenu(owner, menu);
        host.ShowRoot(owner);
        host.RunUntilIdle();
        owner.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Menu);
        host.RunUntilIdle();
        Assert.True(menu.IsOpen);
    }

    [Fact] // C7.6: opening focuses the first item; Down moves focus to the next (keyboard nav is live)
    public void C7_6_OpenFocusesFirst_DownMoves()
    {
        var first = new MenuItem { Header = "Cut" };
        var second = new MenuItem { Header = "Copy" };
        var menu = new ContextMenu();
        menu.Items.Add(first);
        menu.Items.Add(second);
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner);
        host.RunUntilIdle();
        Assert.True(first.IsFocused);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.True(second.IsFocused);
    }

    [Fact] // C7.7: Enter on a focused leaf invokes its command and dismisses the menu (CloseMenuChain → ContextMenu.Close)
    public void C7_7_LeafInvokeDismisses()
    {
        var command = new TestCommand();
        var leaf = new MenuItem { Header = "Cut", Command = command };
        var menu = new ContextMenu();
        menu.Items.Add(leaf);
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner);
        host.RunUntilIdle();
        Assert.True(leaf.IsFocused);

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(1, command.Runs);
        Assert.False(menu.IsOpen);          // the leaf invoke dismissed the whole menu
        Assert.Equal(0, Popups(host));      // the popup surface was released
    }

    [Fact] // C7.8: Escape closes the open menu
    public void C7_8_EscapeCloses()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Cut" });
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner);
        host.RunUntilIdle();
        Assert.True(menu.IsOpen);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(menu.IsOpen);
    }

    [Fact] // C7.9: a click outside the open menu light-dismisses it
    public void C7_9_OutsideClickDismisses()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Cut" });
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner, new CellPosition(0, 0)); // drop below the owner so the far corner is outside
        host.RunUntilIdle();
        Assert.True(menu.IsOpen);

        host.SendClick(29, 9); // far from the menu → light-dismiss
        host.RunUntilIdle();
        Assert.False(menu.IsOpen);
    }

    [Fact] // C7.10: re-opening an open menu keeps exactly one popup surface (relocate, not stack)
    public void C7_10_ReopenRelocates()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Cut" });
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner, new CellPosition(0, 0));
        host.RunUntilIdle();
        Assert.Equal(1, Popups(host));

        menu.Open(owner, new CellPosition(2, 2)); // re-open elsewhere
        host.RunUntilIdle();
        Assert.True(menu.IsOpen);
        Assert.Equal(1, Popups(host)); // not two
    }

    [Fact] // C7.11: a submenu header inside the context menu opens its own submenu (nested popup)
    public void C7_11_NestedSubmenu()
    {
        var child = new MenuItem { Header = "a.txt" };
        var more = new MenuItem { Header = "Recent" };
        more.Items.Add(child);
        var menu = new ContextMenu();
        menu.Items.Add(more);
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner);
        host.RunUntilIdle();

        more.IsSubmenuOpen = true; // open the nested submenu
        host.RunUntilIdle();
        Assert.True(more.IsSubmenuOpen);
        Assert.True(more.ItemContainerGenerator.ContainerFromIndex(0)!.IsAttachedToTree); // grandchild hosted
        Assert.Equal(2, Popups(host)); // the context menu + the nested submenu
    }

    [Fact] // C7.12: Close() closes a programmatically-opened menu
    public void C7_12_CloseCloses()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Cut" });
        var (host, owner, _) = RootWithMenu(menu);
        using var _h = host;

        menu.Open(owner);
        host.RunUntilIdle();
        Assert.True(menu.IsOpen);

        menu.Close();
        host.RunUntilIdle();
        Assert.False(menu.IsOpen);
        Assert.Equal(0, Popups(host));
    }
}
