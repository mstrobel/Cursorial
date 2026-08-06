using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// P3 — Menu as a RETURNING focus scope (nav-subsystem decision ③). A Menu bar is a non-retaining focus scope (like
// the bars Toolbar): entered via Alt/F10, it RETURNS focus to the pre-menu origin when it closes (Escape whole-chain
// collapse, leaf invoke, light-dismiss). A ContextMenu is a focus scope but RETAINS focus (its Popup W4 restore
// returns focus to the right-click trigger). Item focus memory records on the menu, never clobbering the window root.
public sealed class MenuFocusScopeTests
{
    private sealed class Cmd : System.Windows.Input.ICommand
    {
        public int Runs;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Runs++;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    // A menu bar (row 0) above a focusable editor (row 1). The menu has File → [New → [Doc, Sheet], Save]. The editor
    // is the pre-menu origin. Activation auto-focuses the first tab stop (the menu's File); tests pin focus to the
    // editor first so the menu is not keyboard-active until they enter it.
    private sealed record Harness(UIHeadlessHost Host, StackPanel Root, Button Editor, Menu Menu,
                                  MenuItem File, MenuItem New, MenuItem Save, MenuItem Doc, Cmd SaveCmd)
    {
        public FocusManager Focus => Host.Application.FocusManager;
    }

    private static Harness Build()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });

        var doc = new MenuItem { Header = "Doc" };
        var sheet = new MenuItem { Header = "Sheet" };
        var @new = new MenuItem { Header = "New" };
        @new.Items.Add(doc);
        @new.Items.Add(sheet);
        var saveCmd = new Cmd();
        var save = new MenuItem { Header = "Save", Command = saveCmd };
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(@new);
        file.Items.Add(save);

        var menu = new Menu();
        menu.Items.Add(file);

        var editor = new Button { Content = "Editor", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(menu);    // row 0
        root.Children.Add(editor);  // row 1
        host.ShowRoot(root);
        host.RunUntilIdle();
        return new Harness(host, root, editor, menu, file, @new, save, doc, saveCmd);
    }

    [Fact] // a Menu bar is a NON-retaining focus scope (returns focus); a ContextMenu is a scope but RETAINS focus
    public void Menu_IsNonRetainingFocusScope_ContextMenu_Retains()
    {
        var h = Build();
        using var _ = h.Host;

        Assert.True(FocusManager.GetIsFocusScope(h.Menu));
        Assert.False(FocusManager.GetRetainsFocus(h.Menu)); // returns focus on close

        var ctx = new ContextMenu();
        Assert.True(FocusManager.GetIsFocusScope(ctx)); // isolates item-focus memory
        Assert.True(FocusManager.GetRetainsFocus(ctx)); // …but RETAINS (Popup W4 owns its return)
    }

    [Fact] // Escape from a focused top-level header with NO submenu open returns focus to the pre-menu origin (Path B)
    public void Escape_TopLevelHeader_NoSubmenu_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();
        ((IMainMenu) h.Menu).OnEnterMenuMode(); // Alt/F10 — focuses File via AccessKey, captures the editor origin
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsFocused);
        Assert.False(h.File.IsSubmenuOpen);

        h.Host.SendKey(Key.Escape); // no submenu → Menu.OnKeyDown → RestoreRetainedFocus
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    [Fact] // Escape from a DEEP submenu collapses the WHOLE chain (every level) and returns focus to the origin (Path A)
    public void Escape_DeepSubmenu_WholeChainCollapse_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        ((IMainMenu) h.Menu).OnEnterMenuMode();
        h.Host.RunUntilIdle();

        h.Host.SendKey(Key.DownArrow); // File header → open File submenu, focus New
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsSubmenuOpen);
        Assert.True(h.New.IsFocused);

        h.Host.SendKey(Key.RightArrow); // New header → open New submenu, focus Doc (2 levels deep)
        h.Host.RunUntilIdle();
        Assert.True(h.New.IsSubmenuOpen);
        Assert.True(h.Doc.IsFocused);

        h.Host.SendKey(Key.Escape); // whole-chain collapse + return
        h.Host.RunUntilIdle();
        Assert.False(h.New.IsSubmenuOpen); // BOTH levels closed, not just one
        Assert.False(h.File.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    private static void Click(UIHeadlessHost host, UIElement element)
    {
        var origin = element.TranslateToScreen(0, 0);
        host.SendClick(origin.Column + 1, origin.Row);
    }

    [Fact] // #134 (user-found): clicking a top-level header with the POINTER must acquire keyboard focus so directional
           // navigation works afterward, and arm the auto-return (Pointer method) so a leaf-invoke / Escape returns
           // focus to the pre-menu origin. Before the fix a pointer-open left focus on the origin, so arrows were dead.
    public void PointerClickHeader_AcquiresFocus_EnablesDirectionalNav()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement);

        Click(h.Host, h.File); // pointer-open the File submenu
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsSubmenuOpen);
        Assert.True(h.Menu.IsKeyboardFocusWithin, "a pointer click on a header must acquire keyboard focus");
        Assert.Same(h.File, h.Focus.FocusedElement);

        h.Host.SendKey(Key.DownArrow); // directional nav now works: descend into the submenu
        h.Host.RunUntilIdle();
        Assert.Same(h.New, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Escape); // whole-chain collapse + auto-return to the pre-menu origin
        h.Host.RunUntilIdle();
        Assert.False(h.File.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    [Fact] // #134: after a pointer-open, clicking a leaf invokes it and returns focus to the origin (the Pointer entry
           // armed the auto-return, so the leaf-invoke CloseMenuChain resolves the captured pre-menu origin).
    public void PointerClickHeaderThenLeaf_Invokes_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();

        Click(h.Host, h.File);      // pointer-open File
        h.Host.RunUntilIdle();
        Click(h.Host, h.Save);      // click the Save leaf
        h.Host.RunUntilIdle();

        Assert.Equal(1, h.SaveCmd.Runs);
        Assert.False(h.File.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement); // returned to the pre-menu origin
    }

    [Fact] // invoking a leaf runs its command, dismisses the whole menu, and returns focus to the origin
    public void LeafInvoke_RunsCommand_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        ((IMainMenu) h.Menu).OnEnterMenuMode();
        h.Host.SendKey(Key.DownArrow); // open File, focus New
        h.Host.RunUntilIdle();
        h.Host.SendKey(Key.DownArrow); // New → Save
        h.Host.RunUntilIdle();
        Assert.True(h.Save.IsFocused);

        h.Host.SendKey(Key.Enter); // invoke Save (leaf) → CloseMenuChain
        h.Host.RunUntilIdle();
        Assert.Equal(1, h.SaveCmd.Runs);
        Assert.False(h.File.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    [Fact] // an excursion into the menu does NOT clobber the window-root scope's focus memory (recorded on the Menu)
    public void MenuExcursion_DoesNotClobberWindowRootMemory()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, FocusManager.GetFocusedElement(h.Root)); // window-root memory = editor

        ((IMainMenu) h.Menu).OnEnterMenuMode();
        h.Host.SendKey(Key.DownArrow); // into the submenu
        h.Host.RunUntilIdle();
        Assert.True(h.New.IsFocused);

        Assert.Same(h.Editor, FocusManager.GetFocusedElement(h.Root)); // STILL editor — item focus recorded on the Menu
    }

    [Fact] // Left ascends exactly ONE level (CloseAndFocusParent), unchanged — Esc is the whole-chain exit, Left is one-level
    public void LeftArrow_AscendsOneLevel_NotWholeChain()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        ((IMainMenu) h.Menu).OnEnterMenuMode();
        h.Host.SendKey(Key.DownArrow);  // open File, focus New
        h.Host.RunUntilIdle();
        h.Host.SendKey(Key.RightArrow); // open New, focus Doc (2 levels)
        h.Host.RunUntilIdle();
        Assert.True(h.New.IsSubmenuOpen);

        h.Host.SendKey(Key.LeftArrow);  // ascend ONE level: close New's submenu, focus New — File stays open
        h.Host.RunUntilIdle();
        Assert.False(h.New.IsSubmenuOpen);
        Assert.True(h.File.IsSubmenuOpen); // the outer level is still open (not a whole-chain collapse)
        Assert.True(h.New.IsFocused);
    }

    [Fact] // the hover-gate: hovering a menu item does NOT steal keyboard focus unless the menu is already keyboard-active
    public void HoverGate_NoFocusStealWhenMenuNotKeyboardActive()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus(); // the menu is NOT keyboard-active
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement);

        var origin = h.File.TranslateToScreen(0, 0);
        h.Host.SendMouseMove(origin.Column + 1, origin.Row); // hover File
        h.Host.RunFrame();

        Assert.Same(h.Editor, h.Focus.FocusedElement); // focus NOT stolen (gate blocked the hover Focus())
        Assert.False(h.File.IsFocused);
    }

    [Fact] // a click-away (light-dismiss) collapses the whole menu and returns focus to the origin, like Escape
    public void LightDismiss_WholeChainCollapse_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        ((IMainMenu) h.Menu).OnEnterMenuMode();
        h.Host.SendKey(Key.DownArrow);  // open File, focus New
        h.Host.RunUntilIdle();
        h.Host.SendKey(Key.RightArrow); // open New, focus Doc
        h.Host.RunUntilIdle();
        Assert.True(h.New.IsSubmenuOpen);

        h.Host.SendClick(20, 12); // empty cell, outside every menu surface → light-dismiss
        h.Host.RunUntilIdle();

        Assert.False(h.File.IsSubmenuOpen);
        Assert.False(h.New.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    private static KeyEvent KeyEvt(Key key, KeyModifiers modifiers = KeyModifiers.None, KeyEventKind kind = KeyEventKind.Down)
        => new() { Key = key, Modifiers = modifiers, Kind = kind, Text = string.Empty.AsMemory(), Timestamp = DateTimeOffset.UnixEpoch };

    [Fact] // the two-Esc through the REAL AccessKeyManager cue: Esc#1 (cue up) clears the cue and KEEPS focus; Esc#2
           // (cue down) returns focus to the pre-menu origin. Decision ③ — "the first esc only clears the cues".
    public void TwoEsc_FirstClearsCueKeepsFocus_SecondReturnsToOrigin()
    {
        // Access-key caps so the sticky-cue Alt-tap machinery is live (the gate: DistinguishesKeyUpDown && ReportsRepeats).
        var caps = HeadlessCapabilities.KittyTruecolor with
        {
            Input = HeadlessCapabilities.KittyTruecolor.Input with
            {
                Keyboard = HeadlessCapabilities.KittyTruecolor.Input.Keyboard with { DistinguishesKeyUpDown = true, ReportsRepeats = true },
            },
        };
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16), Capabilities = caps });
        using var _ = host;

        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "New" });
        var menu = new Menu();
        menu.Items.Add(file);
        var editor = new Button { Content = "Editor", Width = 8, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(menu);
        root.Children.Add(editor);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        editor.Focus();
        host.RunUntilIdle();

        // Alt-tap (chordless Alt down→up) → menu mode: sticky cue up, first item focused.
        var d = host.Application.InputDispatcher;
        d.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt));       // Alt down
        d.ProcessEvent(KeyEvt(Key.LeftAlt, kind: KeyEventKind.Up));  // Alt up → the tap fires EnterMenuMode
        host.RunUntilIdle();
        Assert.True(host.Application.AccessKeys.IsCueActive);
        Assert.True(file.IsFocused);

        host.SendKey(Key.Escape); // Esc#1 — the AccessKeyManager consumes it: cue clears, focus STAYS on File
        host.RunUntilIdle();
        Assert.False(host.Application.AccessKeys.IsCueActive);
        Assert.Same(file, focus.FocusedElement); // focus retained (menu not exited)

        host.SendKey(Key.Escape); // Esc#2 — cue down → Menu.OnKeyDown → returns focus to the origin
        host.RunUntilIdle();
        Assert.Same(editor, focus.FocusedElement);
    }

    [Fact] // review fix (Path B): a top-level header focused WITH its submenu open (mouse-opened on a keyboard-focused
           // header) — Esc closes the submenu AND returns focus to the origin, leaving no stranded (zombie) popup
    public void Escape_TopLevelHeader_SubmenuOpen_ClosesSubmenuAndReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        ((IMainMenu) h.Menu).OnEnterMenuMode(); // File focused → menu keyboard-active
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsFocused);

        // Mouse-click the focused header to open its submenu WITHOUT moving focus off it (OnMouseDown → OpenSubmenu).
        var origin = h.File.TranslateToScreen(0, 0);
        h.Host.SendClick(origin.Column + 1, origin.Row);
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsFocused);      // focus stayed on the header
        Assert.True(h.File.IsSubmenuOpen);  // …with its submenu open (the zombie precondition)

        h.Host.SendKey(Key.Escape);
        h.Host.RunUntilIdle();
        Assert.False(h.File.IsSubmenuOpen);          // submenu closed — no stranded popup
        Assert.Same(h.Editor, h.Focus.FocusedElement); // and focus returned to the origin
    }

    [Fact] // coverage: a MOUSE-opened menu (no captured return scope — the hover-gate blocks focus) still returns focus
           // to the origin on a leaf invoke, via the null-returnScope → window-root-memory fallback
    public void MouseOpen_LeafInvoke_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();

        var fileOrigin = h.File.TranslateToScreen(0, 0);
        h.Host.SendClick(fileOrigin.Column + 1, fileOrigin.Row); // mouse-open File (menu NOT keyboard-active)
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsSubmenuOpen);

        var saveOrigin = h.Save.TranslateToScreen(0, 0);
        h.Host.SendClick(saveOrigin.Column + 1, saveOrigin.Row); // click the Save leaf
        h.Host.RunUntilIdle();

        Assert.Equal(1, h.SaveCmd.Runs);
        Assert.False(h.File.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement); // returned via the null-returnScope fallback
    }

    [Fact] // coverage: a MOUSE-opened menu returns focus to the origin on a click-away (light-dismiss)
    public void MouseOpen_LightDismiss_ReturnsToOrigin()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();

        var fileOrigin = h.File.TranslateToScreen(0, 0);
        h.Host.SendClick(fileOrigin.Column + 1, fileOrigin.Row); // mouse-open File
        h.Host.RunUntilIdle();
        Assert.True(h.File.IsSubmenuOpen);

        h.Host.SendClick(20, 12); // empty cell → light-dismiss
        h.Host.RunUntilIdle();
        Assert.False(h.File.IsSubmenuOpen);
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    [Fact] // hover-gate POSITIVE: when the menu IS keyboard-active, hovering a sibling header DOES move focus to it
    public void HoverGate_FocusesSiblingWhenMenuKeyboardActive()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;

        var file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "New" });
        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(new MenuItem { Header = "Copy" });
        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        host.ShowRoot(menu);
        host.RunUntilIdle();

        ((IMainMenu) menu).OnEnterMenuMode(); // File focused → the menu is keyboard-active
        host.RunUntilIdle();
        Assert.True(file.IsFocused);
        Assert.True(menu.IsKeyboardFocusWithin);

        var editOrigin = edit.TranslateToScreen(0, 0);
        host.SendMouseMove(editOrigin.Column + 1, editOrigin.Row); // hover the Edit sibling
        host.RunFrame();

        Assert.True(edit.IsFocused); // the gate ALLOWED the hover Focus() (menu keyboard-active)
    }
}
