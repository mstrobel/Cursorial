using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C6 — Menu / MenuItem / Separator (P9.4a: structural core — mouse open/invoke + theme).
public sealed class Section19_Menu
{
    private static UITestHost Host() => UITestHost.Create(new UITestHostOptions { InitialSize = new Size(36, 14) });

    // Click an element at its on-screen position (works for bar items and items inside an open popup surface).
    private static void Click(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToScreen(0, 0);
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

    [Fact] // C6.6b: PageDown/PageUp in an open submenu jump to the last/first item (a menu "page")
    public void C6_6b_Submenu_PageNav_LastFirst()
    {
        var file = new MenuItem { Header = "File" };
        var a = new MenuItem { Header = "New" };
        var b = new MenuItem { Header = "Open" };
        var c = new MenuItem { Header = "Save" };
        file.Items.Add(a);
        file.Items.Add(b);
        file.Items.Add(c);
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Click(host, file); // open the submenu
        host.RunUntilIdle();
        a.Focus(); // focus the first sub-item
        host.RunUntilIdle();

        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.True(c.IsFocused); // jumped to the last item

        host.SendKey(Key.PageUp);
        host.RunUntilIdle();
        Assert.True(a.IsFocused); // …and back to the first
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
        Assert.Single(host.Application.WindowManager!.Popups);

        root.Children.Remove(menu); // detach the whole menu while the submenu is open
        host.RunUntilIdle();
        Assert.False(file.IsSubmenuOpen);
        Assert.Empty(host.Application.WindowManager!.Popups); // the submenu Popup surface was released
    }

    private static MenuItem Sub(MenuItem item, int index) => (MenuItem)item.ItemContainerGenerator.ContainerFromIndex(index)!;

    // ── access-key drivers: end-to-end through the real AccessKeyManager (mirrors InputMatrix/Section12) ──
    // UITestHost's default KittyTruecolor caps gate the manager into AccessKeyMode.AltHeld, so an Alt-down
    // primes the cue window and a following Alt+<char> activates the folded mnemonic. These exercise the
    // whole registration spine (OnAttachedToTree→RegisterAccessKey, the Header literal fold, the manager's
    // registry + scope resolution + Invoke), not just the IAccessKeyTarget.OnAccessKey reaction body.
    private static KeyEvent KeyEvt(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null)
        => new()
        {
            Key = key,
            Modifiers = modifiers,
            Kind = KeyEventKind.Down,
            Text = (text ?? string.Empty).AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch
        };

    // Activate a folded mnemonic through the manager. A fresh Alt-down precedes each char so the cue window
    // is open (a bare char with Alt held would trip the manager's stale-Alt inference and close it).
    private static void ActivateAccessKey(UITestHost host, char mnemonic)
    {
        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt));                          // Alt down → cue up
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, mnemonic.ToString()));   // Alt+<char>
        host.RunUntilIdle();
    }

    [Fact] // C6.31: a folded Header access key opens the submenu header — end-to-end through the manager
    public void C6_31_AccessKeyOpensSubmenuHeader()
    {
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "New" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        ActivateAccessKey(host, 'f'); // Alt+F — registration ran on attach, folded "_File"→'f', manager dispatched
        Assert.True(file.IsSubmenuOpen);
    }

    [Fact] // C6.32: a folded Header access key invokes a leaf — end-to-end through the manager
    public void C6_32_AccessKeyInvokesLeaf()
    {
        var command = new TestCommand();
        var leaf = new MenuItem { Header = "_Quit", Command = command };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        ActivateAccessKey(host, 'q'); // Alt+Q — single match → focus + Invoke → command runs once
        Assert.Equal(1, command.Runs);
    }

    [Fact] // C6.33: two items sharing a mnemonic — the manager produces a multi-match → focus cycles, never invokes (ND18)
    public void C6_33_MultiMatchFocusesOnly()
    {
        var save = new TestCommand();
        var send = new TestCommand();
        var saveItem = new MenuItem { Header = "_Save", Command = save };
        var sendItem = new MenuItem { Header = "_Send", Command = send }; // both fold to 's' → a real collision
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(saveItem);
        menu.Items.Add(sendItem);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        saveItem.Focus(); // anchor focus on the first match so the cycle target is observable
        host.RunUntilIdle();

        ActivateAccessKey(host, 's'); // two eligible matches → cycle focus to the NEXT match, never invoke
        Assert.Equal(0, save.Runs);
        Assert.Equal(0, send.Runs);
        Assert.Same(sendItem, host.Application.FocusManager.FocusedElement); // cycled past the focused saveItem
    }

    [Fact] // C6.34: the Menu registers as the app main menu (IMainMenu)
    public void C6_34_RegistersAsMainMenu()
    {
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(new MenuItem { Header = "_File" });
        host.ShowRoot(menu);
        host.RunUntilIdle();
        Assert.Same(menu, host.Application.AccessKeys.MainMenu);
    }

    [Fact] // C6.35: menu-mode entry focuses the first top-level item
    public void C6_35_EnterMenuModeFocusesFirst()
    {
        var file = new MenuItem { Header = "_File" };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        ((IMainMenu)menu).OnEnterMenuMode();
        host.RunUntilIdle();
        Assert.True(file.IsFocused);
    }

    [Fact] // C6.36: a disabled MenuItem is not access-key eligible — and the manager actually skips it
    public void C6_36_DisabledNotEligible()
    {
        var command = new TestCommand { CanRun = false };
        var leaf = new MenuItem { Header = "_Quit", Command = command };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Assert.False(((IAccessKeyTarget)leaf).IsAccessKeyEligible);
        ActivateAccessKey(host, 'q'); // the manager's CollectEligibleMatches skips the ineligible target
        Assert.Equal(0, command.Runs); // never invoked — exclusion holds end-to-end
    }

    [Fact] // C6.37: changing Header while attached re-registers the NEW mnemonic and drops the OLD (OnHeaderChanged)
    public void C6_37_HeaderChangeReRegistersAccessKey()
    {
        var command = new TestCommand();
        var leaf = new MenuItem { Header = "_Quit", Command = command };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        leaf.Header = "_Exit"; // re-folds to 'e'; the old 'q' registration must drop
        host.RunUntilIdle();

        ActivateAccessKey(host, 'q'); // the stale mnemonic activates nothing
        Assert.Equal(0, command.Runs);

        ActivateAccessKey(host, 'e'); // the re-registered mnemonic invokes
        Assert.Equal(1, command.Runs);
    }

    [Fact] // C6.38: attach registers the mnemonic; removing the item from Items unregisters it (detach backstop)
    public void C6_38_AttachRegistersDetachUnregisters()
    {
        var command = new TestCommand();
        var leaf = new MenuItem { Header = "_Quit", Command = command };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        ActivateAccessKey(host, 'q'); // attach-time RegisterAccessKey ran with the folded 'q'
        Assert.Equal(1, command.Runs);

        menu.Items.Remove(leaf); // unrealize → detach → UnregisterAccessKey
        host.RunUntilIdle();
        ActivateAccessKey(host, 'q'); // the mnemonic is gone — no further invoke
        Assert.Equal(1, command.Runs);
    }

    [Fact] // C6.39: detaching the Menu releases its IMainMenu registration (symmetric clear, ReferenceEquals-guarded)
    public void C6_39_MenuDetachClearsMainMenu()
    {
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(new MenuItem { Header = "_File" });
        host.ShowRoot(menu);
        host.RunUntilIdle();
        Assert.Same(menu, host.Application.AccessKeys.MainMenu);

        host.ShowRoot(new StackPanel()); // swap the root out → the old menu detaches
        host.RunUntilIdle();
        Assert.Null(host.Application.AccessKeys.MainMenu); // released, not leaked
    }

    [Fact] // C6.40: detaching a non-owner menu leaves the last-wins MainMenu intact (the ReferenceEquals guard)
    public void C6_40_NonOwnerDetachKeepsMainMenu()
    {
        var first = new Menu();
        first.Items.Add(new MenuItem { Header = "_File" });
        var second = new Menu();
        second.Items.Add(new MenuItem { Header = "_Edit" });
        var root = new StackPanel();
        root.Children.Add(first);
        root.Children.Add(second);
        using var host = Host();
        host.ShowRoot(root);
        host.RunUntilIdle();
        Assert.Same(second, host.Application.AccessKeys.MainMenu); // last attach wins

        root.Children.Remove(first); // the NON-owner detaches — must NOT clear second's registration
        host.RunUntilIdle();
        Assert.Same(second, host.Application.AccessKeys.MainMenu);

        root.Children.Remove(second); // the owner detaches — now it clears
        host.RunUntilIdle();
        Assert.Null(host.Application.AccessKeys.MainMenu);
    }

    [Fact] // C6.41: an access key activates a checkable leaf — toggles IsChecked through Invoke's SetCurrentValue
    public void C6_41_AccessKeyTogglesCheckable()
    {
        var leaf = new MenuItem { Header = "_Wrap", IsCheckable = true };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        ActivateAccessKey(host, 'w');
        Assert.True(leaf.IsChecked);
        Assert.True(leaf.HasCustomPseudoClass(":checked"));
    }

    [Fact] // C6.42: the MenuItem HeaderProperty metadata override preserves the inherited AffectsMeasure effect
    public void C6_42_HeaderChangeStillInvalidatesMeasure()
    {
        var leaf = new MenuItem { Header = "Quit" };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(leaf);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        Assert.True(leaf.IsMeasureValid); // settled

        leaf.Header = "Exit"; // AffectsMeasure(HeaderProperty) must survive the OverrideMetadata merge
        Assert.False(leaf.IsMeasureValid);
    }

    [Fact] // C6.22: Down on a focused bar header opens its submenu + moves focus to the first sub-item
    public void C6_22_DownOpensAndFocusesFirst()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        file.Items.Add(new MenuItem { Header = "Open" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.True(file.IsSubmenuOpen);
        Assert.True(Sub(file, 0).IsFocused); // focus entered the submenu
    }

    [Fact] // C6.23: Down/Up move focus among sub-items (wrapping)
    public void C6_23_ArrowsMoveWithinSubmenu()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        file.Items.Add(new MenuItem { Header = "Open" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open + focus New (0)
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // → Open (1)
        host.RunUntilIdle();
        Assert.True(Sub(file, 1).IsFocused);
        host.SendKey(Key.UpArrow);   // → New (0)
        host.RunUntilIdle();
        Assert.True(Sub(file, 0).IsFocused);
    }

    [Fact] // C6.24: Enter invokes a focused leaf (and dismisses)
    public void C6_24_EnterInvokesLeaf()
    {
        var command = new TestCommand();
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New", Command = command });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open + focus New
        host.RunUntilIdle();

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(1, command.Runs);
        Assert.False(file.IsSubmenuOpen); // chain dismissed
    }

    [Fact] // C6.25: Right descends into a nested submenu; Left closes it + focuses the parent header
    public void C6_25_RightDescendsLeftAscends()
    {
        var recent = new MenuItem { Header = "Recent" };
        recent.Items.Add(new MenuItem { Header = "a.txt" });
        var file = new MenuItem { Header = "File" };
        file.Items.Add(recent);
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open File → focus Recent
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow); // descend into Recent
        host.RunUntilIdle();
        Assert.True(recent.IsSubmenuOpen);
        Assert.True(Sub(recent, 0).IsFocused);

        host.SendKey(Key.LeftArrow); // ascend
        host.RunUntilIdle();
        Assert.False(recent.IsSubmenuOpen);
        Assert.True(recent.IsFocused);
    }

    [Fact] // C6.26: Left/Right move between top-level bar headers
    public void C6_26_BarArrowsCycle()
    {
        var file = new MenuItem { Header = "File" };
        var edit = new MenuItem { Header = "Edit" };
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.True(edit.IsFocused);
        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.True(file.IsFocused);
    }

    [Fact] // C6.27: Escape closes the open submenu (focus is inside it after a keyboard open)
    public void C6_27_EscapeClosesSubmenu()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open + focus into submenu
        host.RunUntilIdle();

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(file.IsSubmenuOpen);
    }

    [Fact] // C6.28: a key the focused sub-item leaves unhandled (Right on a leaf) does NOT hijack an ancestor header
    public void C6_28_UnhandledKeyDoesNotHijackAncestor()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" }); // a leaf
        var edit = new MenuItem { Header = "Edit" };
        edit.Items.Add(new MenuItem { Header = "Copy" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open File, focus the leaf "New"
        host.RunUntilIdle();
        var leaf = Sub(file, 0);
        Assert.True(leaf.IsFocused);

        host.SendKey(Key.RightArrow); // the leaf doesn't handle Right — must NOT bubble to File and jump the bar
        host.RunUntilIdle();
        Assert.True(file.IsSubmenuOpen);  // File stays open (not stranded by a bar jump)
        Assert.False(edit.IsFocused);     // focus did NOT hijack to the Edit header
        Assert.True(leaf.IsFocused);      // focus stayed on the leaf
    }

    [Fact] // C6.29: highlight follows focus — the focused sub-item is :highlighted, others are not
    public void C6_29_HighlightFollowsFocus()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" });
        file.Items.Add(new MenuItem { Header = "Open" });
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // focus New (0)
        host.RunUntilIdle();
        Assert.True(Sub(file, 0).IsHighlighted);
        Assert.True(Sub(file, 0).HasCustomPseudoClass(":highlighted"));
        Assert.False(Sub(file, 1).IsHighlighted);

        host.SendKey(Key.DownArrow); // focus Open (1) — highlight moves
        host.RunUntilIdle();
        Assert.True(Sub(file, 1).IsHighlighted);
        Assert.False(Sub(file, 0).IsHighlighted);
    }

    [Fact] // C6.30: arrow navigation skips a non-focusable Separator (and wraps)
    public void C6_30_ArrowSkipsSeparator()
    {
        var file = new MenuItem { Header = "File" };
        file.Items.Add(new MenuItem { Header = "New" }); // 0
        file.Items.Add(new Separator());                  // 1 — not focusable
        file.Items.Add(new MenuItem { Header = "Open" }); // 2
        using var host = Host();
        var menu = new Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();
        file.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // open, focus New (0)
        host.RunUntilIdle();
        Assert.True(Sub(file, 0).IsFocused);

        host.SendKey(Key.DownArrow); // skip the Separator → Open (2)
        host.RunUntilIdle();
        Assert.True(Sub(file, 2).IsFocused);

        host.SendKey(Key.DownArrow); // wrap back to New (0)
        host.RunUntilIdle();
        Assert.True(Sub(file, 0).IsFocused);
    }

    private static void Hover(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToScreen(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row);
        host.RunFrame();
    }

    [Fact] // C6.16: hovering a submenu header opens it after the 250 ms hover delay
    public void C6_16_HoverOpensAfterDelay()
    {
        var file = new MenuItem { Header = "File" };
        var open = new MenuItem { Header = "Open", Items = { new MenuItem { Header = "Cloud"}, new MenuItem { Header = "Local"} }};
        file.Items.Add(open);
        using var host = Host();
        var menu = new Menu() { VerticalAlignment = VerticalAlignment.Top };
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Assert.False(file.IsSubmenuOpen);
        Click(host, file);
        host.RunUntilIdle();
        Assert.True(file.IsSubmenuOpen);
        Hover(host, open);
        host.RunUntilIdle();
        Assert.False(open.IsSubmenuOpen); // pending — the delay hasn't elapsed

        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.True(open.IsSubmenuOpen); // opened on the hover timer
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
        Assert.Empty(host.Application.WindowManager!.Popups);  // no popup surface materialized post-detach
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
        Assert.Empty(host.Application.WindowManager!.Popups);
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
        Assert.Single(host.Application.WindowManager!.Popups); // exactly one popup (no churn)
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

    [Fact] // W7 #2: MenuItem declares its submenu surface via [TemplatePart("PART_Popup", typeof(Popup))] —
            // a template that mis-types the part is rejected at apply (the attribute is wired, not decorative)
    public void C6_PartPopup_WrongType_Throws()
    {
        using var host = Host();
        var item = new MenuItem
        {
            Header = "File",
            Template = new ControlTemplate(ctx =>
            {
                var wrong = new Border();
                ctx.RegisterName("PART_Popup", wrong); // declared Popup, provided Border
                return wrong;
            }),
        };
        host.ShowRoot(item);

        var ex = Assert.Throws<InvalidOperationException>(host.RunFrame);
        Assert.Contains("PART_Popup", ex.Message);
        Assert.Contains("Popup", ex.Message);
        Assert.Contains("Border", ex.Message);
    }
    // ── MenuItem.IsIconTrayVisible — the per-popup icon-gutter fact ──

    [Fact] // any sibling icon flips the whole popup's tray; top-level bar items never show one
    public void C6_IconTray_GroupSemantics()
    {
        using var host = Host();
        var menu = new Menu();
        var file = new MenuItem { Header = "File", Icon = "F" }; // icon on a BAR item: still no tray
        var newItem = new MenuItem { Header = "New" };
        var openItem = new MenuItem { Header = "Open" };
        file.Items.Add(newItem);
        file.Items.Add(openItem);
        menu.Items.Add(file);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        Assert.False(file.IsIconTrayVisible); // top-level: never

        file.IsSubmenuOpen = true;
        host.RunUntilIdle();
        Assert.False(newItem.IsIconTrayVisible);  // iconless popup: no tray
        Assert.False(openItem.IsIconTrayVisible);

        newItem.Icon = "★"; // ONE icon flips the whole popup
        Assert.True(newItem.IsIconTrayVisible);
        Assert.True(openItem.IsIconTrayVisible);  // the iconless sibling aligns too

        newItem.Icon = null; // last icon leaves → tray collapses group-wide
        Assert.False(newItem.IsIconTrayVisible);
        Assert.False(openItem.IsIconTrayVisible);
    }

    [Fact] // the property is a live BINDING SOURCE: a sibling's icon change re-delivers through a Binding
    public void C6_IconTray_IsABindingSource()
    {
        using var host = Host();
        var menu = new Menu();
        var file = new MenuItem { Header = "File" };
        var newItem = new MenuItem { Header = "New" };
        var openItem = new MenuItem { Header = "Open" };
        file.Items.Add(newItem);
        file.Items.Add(openItem);
        menu.Items.Add(file);

        var probe = new TextBlock();
        var root = new DockPanel { Children = { menu, probe } };
        host.ShowRoot(root);
        host.RunUntilIdle();

        Cursorial.UI.Data.BindingOperations.SetBinding(
            probe, TextBlock.TextProperty,
            new Cursorial.UI.Data.Binding(nameof(MenuItem.IsIconTrayVisible)) { Source = openItem });
        file.IsSubmenuOpen = true;
        host.RunUntilIdle();
        Assert.Equal("False", probe.Text);

        newItem.Icon = "★"; // the SIBLING's icon — openItem's own property re-delivers
        host.RunUntilIdle();
        Assert.Equal("True", probe.Text);
    }

    [Fact] // items entering/leaving the popup re-evaluate the group (the icon may arrive or leave with them)
    public void C6_IconTray_TracksItemMembership()
    {
        using var host = Host();
        var menu = new Menu();
        var file = new MenuItem { Header = "File" };
        var openItem = new MenuItem { Header = "Open" };
        file.Items.Add(openItem);
        menu.Items.Add(file);
        host.ShowRoot(menu);
        file.IsSubmenuOpen = true;
        host.RunUntilIdle();

        Assert.False(openItem.IsIconTrayVisible);

        var iconed = new MenuItem { Header = "Save", Icon = "S" };
        file.Items.Add(iconed); // an iconed item ARRIVES
        host.RunUntilIdle();
        Assert.True(openItem.IsIconTrayVisible);

        file.Items.Remove(iconed); // …and LEAVES
        host.RunUntilIdle();
        Assert.False(openItem.IsIconTrayVisible);
    }

    [Fact] // standalone (no owning menu): the item's own icon decides — the tray must not hide a lone icon
    public void C6_IconTray_StandaloneFallsBackToOwnIcon()
    {
        var lone = new MenuItem { Header = "Lone" };
        Assert.False(lone.IsIconTrayVisible);

        lone.Icon = "★";
        Assert.True(lone.IsIconTrayVisible);
    }

}
