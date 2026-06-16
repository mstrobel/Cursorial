using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C6 — Menu / MenuItem / Separator (P9.4a: structural core — mouse open/invoke + theme).
public sealed class Section19_Menu
{
    private static UITestHost Host() => UITestHost.Create(new UITestHostOptions { InitialSize = new Size(36, 14) });

    // Click an element at its on-screen position (works for bar items and items inside an open popup surface).
    private static void Click(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToWindow(0, 0);
        host.SendClick(origin.Column + 1, origin.Row);
        host.RunUntilIdle();
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                if (FindDescendant<T>(child) is { } found)
                    return found;
        return null;
    }

    private sealed class TestCommand : ICommand
    {
        public bool CanRun = true;
        public int Runs;
        public object? LastParameter;
        public bool CanExecute(object? parameter) => CanRun;
        public void Execute(object? parameter) { Runs++; LastParameter = parameter; }
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    [Fact] // C6.1: a MenuItem item is its own container; a data item would be wrapped in a MenuItem container
    public void C6_1_OwnContainers()
    {
        using var host = Host();
        var menu = new Menu();
        var file = new MenuItem { Header = "File" };
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        Assert.Same(file, menu.ItemContainerGenerator.ContainerFromIndex(0));
    }

    [Fact] // C6.2: a Separator is its own container
    public void C6_2_SeparatorOwnContainer()
    {
        using var host = Host();
        var menu = new Menu();
        var item = new MenuItem { Header = "File" };
        item.Items.Add(new MenuItem { Header = "New" });
        var sep = new Separator();
        item.Items.Add(sep);
        menu.Items.Add(item);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        Assert.Same(sep, item.ItemContainerGenerator.ContainerFromIndex(1));
        Assert.False(sep.Focusable);
    }

    [Fact] // C6.3: HasItems distinguishes a submenu header from a leaf
    public void C6_3_HasItems()
    {
        var header = new MenuItem { Header = "File" };
        header.Items.Add(new MenuItem { Header = "New" });
        var leaf = new MenuItem { Header = "Quit" };

        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(header);
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Assert.True(header.HasItems);
        Assert.False(leaf.HasItems);
    }

    [Fact] // C6.4: clicking a leaf raises Click + executes Command
    public void C6_4_LeafInvokes()
    {
        var command = new TestCommand();
        var leaf = new MenuItem { Header = "Quit", Command = command, CommandParameter = "p" };
        var clicks = 0;
        leaf.Click += (_, _) => clicks++;

        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, leaf);
        Assert.Equal(1, clicks);
        Assert.Equal(1, command.Runs);
        Assert.Equal("p", command.LastParameter);
    }

    [Fact] // C6.5: clicking a checkable leaf toggles IsChecked + :checked
    public void C6_5_CheckableToggles()
    {
        var leaf = new MenuItem { Header = "Word Wrap", IsCheckable = true };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, leaf);
        Assert.True(leaf.IsChecked);
        Assert.True(leaf.HasCustomPseudoClass(":checked"));

        Click(host, leaf);
        Assert.False(leaf.IsChecked);
        Assert.False(leaf.HasCustomPseudoClass(":checked")); // :checked cleared on the second toggle
    }

    [Fact] // C6.6: clicking a submenu header opens its submenu (Popup) + hosts the sub-items + :open
    public void C6_6_SubmenuOpens()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        file.Items.Add(new MenuItem { Header = "Open" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, file);
        Assert.True(file.IsSubmenuOpen);
        Assert.True(file.HasCustomPseudoClass(":open"));
        Assert.True(file.ItemContainerGenerator.ContainerFromIndex(0)!.IsAttachedToTree); // sub-item hosted in the popup
    }

    [Fact] // C6.7: clicking outside the menu light-dismisses the open submenu
    public void C6_7_OutsideClickDismisses()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, file);
        Assert.True(file.IsSubmenuOpen);

        host.SendClick(30, 13); // far outside the bar + popup → light-dismiss
        host.RunUntilIdle();
        Assert.False(file.IsSubmenuOpen);
        Assert.False(file.HasCustomPseudoClass(":open"));
    }

    [Fact] // C6.8: hovering a MenuItem highlights it (:highlighted)
    public void C6_8_HoverHighlights()
    {
        var file = new MenuItem { Header = "File" };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        var origin = file.TranslateToWindow(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row);
        host.RunUntilIdle();
        Assert.True(file.IsHighlighted);
        Assert.True(file.HasCustomPseudoClass(":highlighted"));
    }

    [Fact] // C6.10: a Command that can't execute disables the item — Invoke is never entered (no Click, no check toggle)
    public void C6_10_DisabledByCommand()
    {
        var command = new TestCommand { CanRun = false };
        var leaf = new MenuItem { Header = "Quit", Command = command, IsCheckable = true };
        var clicks = 0;
        leaf.Click += (_, _) => clicks++;
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Assert.False(leaf.IsEffectivelyEnabled);
        Click(host, leaf);
        // Invoke() was never entered (independent of Execute's own CanExecute re-check): no Click, no check toggle.
        Assert.Equal(0, clicks);
        Assert.False(leaf.IsChecked);
        Assert.Equal(0, command.Runs);
    }

    [Fact] // C6.13: a CanExecuteChanged pulse re-gates IsEnabledCore (the command coupling is live, not one-shot)
    public void C6_13_CanExecuteChangedReEnables()
    {
        var command = new TestCommand { CanRun = false };
        var leaf = new MenuItem { Header = "Quit", Command = command };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        Assert.False(leaf.IsEffectivelyEnabled);

        command.CanRun = true;
        command.RaiseCanExecuteChanged();
        host.RunUntilIdle();
        Assert.True(leaf.IsEffectivelyEnabled); // re-enabled — the item is subscribed to CanExecuteChanged
    }

    [Fact] // C6.14: detaching the menu while a submenu is open closes the submenu (no leaked Popup surface)
    public void C6_14_DetachClosesSubmenu()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        var menu = new Menu();
        menu.Items.Add(file);
        var root = new StackPanel();
        root.Children.Add(menu);
        using var host = Host();
        host.ShowRoot(root);
        host.RunUntilIdle();

        Click(host, file);
        Assert.Equal(1, host.Application.WindowManager!.Popups.Count);

        root.Children.Remove(menu); // detach the whole menu while the submenu is open
        host.RunUntilIdle();
        Assert.False(file.IsSubmenuOpen);
        Assert.Equal(0, host.Application.WindowManager!.Popups.Count); // the submenu Popup surface was released
    }

    private static void Hover(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToWindow(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row);
        host.RunFrame();
    }

    [Fact] // C6.16: hovering a submenu header opens it after the 250 ms hover delay
    public void C6_16_HoverOpensAfterDelay()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Hover(host, file);
        Assert.False(file.IsSubmenuOpen); // pending — the delay hasn't elapsed

        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.True(file.IsSubmenuOpen); // opened on the hover timer
    }

    [Fact] // C6.17: leaving before the delay cancels the pending hover-open
    public void C6_17_HoverLeaveCancels()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Hover(host, file);
        host.SendMouseMove(30, 12); // leave File before the delay elapses
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.False(file.IsSubmenuOpen); // the timer was cancelled
    }

    [Fact] // C6.18: once a menu is active, hovering a sibling header switches immediately (closing the open one)
    public void C6_18_HoverSwitchesSiblingsImmediately()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        var edit = new MenuItem { Header = "Edit" };
        edit.Items.Add(new MenuItem { Header = "Copy" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, file); // open File's submenu (menu is now active)
        Assert.True(file.IsSubmenuOpen);

        Hover(host, edit); // immediate switch — no delay
        Assert.True(edit.IsSubmenuOpen);
        Assert.False(file.IsSubmenuOpen);
    }

    [Fact] // C6.19: detaching the menu while a hover-open timer is pending stops it (no fire-after-detach)
    public void C6_19_DetachStopsPendingHoverTimer()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        var menu = new Menu();
        menu.Items.Add(file);
        var root = new StackPanel();
        root.Children.Add(menu);
        using var host = Host();
        host.ShowRoot(root);
        host.RunUntilIdle();

        Hover(host, file);                // arm the 250 ms hover-open timer
        Assert.False(file.IsSubmenuOpen); // pending
        root.Children.Remove(menu);       // detach before it fires → StopHoverTimer
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.False(file.IsSubmenuOpen);                               // the parked timer was stopped — never fired
        Assert.Equal(0, host.Application.WindowManager!.Popups.Count);  // no popup surface materialized post-detach
    }

    [Fact] // C6.20: hovering a leaf (no sub-items) arms no timer + opens nothing
    public void C6_20_LeafHoverNoOpen()
    {
        var leaf = new MenuItem { Header = "Quit" };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Hover(host, leaf);
        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.False(leaf.IsSubmenuOpen);
        Assert.Equal(0, host.Application.WindowManager!.Popups.Count);
    }

    [Fact] // C6.21: re-hovering an already-open header doesn't churn it (early-return on _isSubmenuOpen)
    public void C6_21_ReHoverOpenHeaderNoOp()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, file); // open
        Assert.True(file.IsSubmenuOpen);
        Hover(host, file); // re-enter the already-open header
        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.True(file.IsSubmenuOpen); // stays open, no flicker
        Assert.Equal(1, host.Application.WindowManager!.Popups.Count); // exactly one popup (no churn)
    }

    [Fact] // C6.15: a nested (2-level) submenu opens + hosts its grandchild items
    public void C6_15_NestedSubmenuHosts()
    {
        var grandchild = new MenuItem { Header = "a.txt" };
        var recent = new MenuItem { Header = "Recent" };
        recent.Items.Add(grandchild);
        var file = new MenuItem { Header = "File" };
        file.Items.Add(recent);
        var menu = new Menu();
        menu.Items.Add(file);
        using var host = Host();
        host.ShowRoot(menu);
        host.RunUntilIdle();

        file.IsSubmenuOpen = true;   // open File's submenu (realizes + templates Recent)
        host.RunUntilIdle();
        recent.IsSubmenuOpen = true; // open Recent's submenu
        host.RunUntilIdle();

        Assert.True(recent.IsSubmenuOpen);
        Assert.True(recent.ItemContainerGenerator.ContainerFromIndex(0)!.IsAttachedToTree); // grandchild hosted
    }

    [Fact] // C6.12: submenu placement — top-level opens downward, nested opens to the right
    public void C6_12_SubmenuPlacement()
    {
        var nested = new MenuItem { Header = "Recent" };
        nested.Items.Add(new MenuItem { Header = "a.txt" });
        var file = new MenuItem { Header = "File" };
        file.Items.Add(nested);
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, file); // realize/template the nested item
        Assert.Equal(PlacementMode.Bottom, FindDescendant<Popup>(file)!.Placement); // top-level → down
        Assert.Equal(PlacementMode.Right, FindDescendant<Popup>(nested)!.Placement);  // nested → right
    }
}
