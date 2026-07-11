using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Bars;

// The toolbar focus-scope behavior (FocusManager.RetainsFocus): true (the default) keeps focus where it lands;
// Toolbar sets it FALSE so the bar RETURNS focus to where it came from. The auto-return is gated by HOW focus
// ENTERED the bar — Pointer/AccessKey entries return on invoke (even after arrowing), Tab/Programmatic entries hold
// focus for navigation and only Escape returns. "editor"/"other" are focusable Buttons standing in for content.
public sealed class ToolbarFocusTests
{
    private sealed record Harness(UIHeadlessHost Host, Button Editor, Button Other, BarButton Cut, BarButton Copy, Toolbar Toolbar)
    {
        public FocusManager Focus => Host.Application.FocusManager;
    }

    private static Harness Build()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 6),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });

        var cut = new BarButton { Content = "Cut" };
        var copy = new BarButton { Content = "Copy" };
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(cut);
        toolbar.Items.Add(copy);

        var editor = new Button { Content = "Editor" };
        var other = new Button { Content = "Other" }; // a second root-level control (NOT in the bar)

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar);  // row 0
        root.Children.Add(editor);   // row 1
        root.Children.Add(other);    // row 2
        host.ShowRoot(root);
        host.RunUntilIdle();
        return new Harness(host, editor, other, cut, copy, toolbar);
    }

    [Fact] // the Toolbar opts OUT of focus retention (returns focus) and is a Tab-Once, arrow-navigable scope
    public void Toolbar_Defaults_AreReturningScopeWithArrowNav()
    {
        var h = Build();
        using var _ = h.Host;
        Assert.False(FocusManager.GetRetainsFocus(h.Toolbar)); // does NOT retain — returns focus
        Assert.True(FocusManager.GetRetainsFocus(h.Editor));   // a normal control retains (the default)
        Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(h.Toolbar));
        Assert.Equal(DirectionalNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(h.Toolbar));
    }

    [Fact] // arrow keys navigate between bar controls regardless of the retention bits
    public void ArrowKey_NavigatesWithinBar()
    {
        var h = Build();
        using var _ = h.Host;

        h.Cut.Focus();
        h.Host.RunUntilIdle();
        Assert.Same(h.Cut, h.Focus.FocusedElement);

        h.Host.SendKey(Key.RightArrow);
        h.Host.RunUntilIdle();
        Assert.Same(h.Copy, h.Focus.FocusedElement); // moved to the next bar control (DirectionalNavigation)
    }

    [Fact] // Tab into the bar (no auto-return) then keyboard-activate: focus STAYS for navigation
    public void TabEntry_KeyboardActivation_StaysInBar()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Cut.Focus(FocusNavigationMethod.Tab); // tab into the bar — return remembered, but NOT auto-returnable
        h.Host.RunUntilIdle();
        Assert.Same(h.Cut, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Enter);
        h.Host.RunUntilIdle();
        Assert.Same(h.Cut, h.Focus.FocusedElement); // stays — a Tab entry holds focus for navigation
    }

    [Fact] // Escape from a Tab entry returns focus to where it came from (the explicit return ignores the auto gate)
    public void TabEntry_Escape_RestoresPriorFocus()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement);

        h.Cut.Focus(FocusNavigationMethod.Tab);
        h.Host.RunUntilIdle();
        Assert.Same(h.Cut, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Escape);
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement); // restored
    }

    [Fact] // the edge case: AccessKey into the bar, arrow to a button, activate — focus returns to the original element
    public void AccessKeyEntry_ArrowThenActivate_ReturnsToPrior()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Cut.Focus(FocusNavigationMethod.AccessKey); // Alt+Tools → first item; auto-returnable, return = editor
        h.Host.RunUntilIdle();
        Assert.Same(h.Cut, h.Focus.FocusedElement);

        h.Host.SendKey(Key.RightArrow);   // arrow to another button — the original return target is preserved
        h.Host.RunUntilIdle();
        Assert.Same(h.Copy, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Enter);   // activate — returns to the editor despite the arrow navigation
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement);
    }

    [Fact] // a pointer click on a bar control runs it and returns focus to the prior element (click, keep editing)
    public void PointerInvoke_RestoresPriorFocus()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement);

        // "Cut" sits at the row's left edge (row 0); hover then click (hover establishes IsPointerOver for the
        // release-click gate).
        h.Host.SendMouseMove(2, 0);
        h.Host.RunFrame();
        h.Host.SendClick(2, 0);
        h.Host.RunUntilIdle();

        Assert.Same(h.Editor, h.Focus.FocusedElement); // pointer invoke restored focus to the editor
    }

    [Fact] // a retaining focus scope inside the bar (the overflow chevron's opt-out) is exempt from the return:
           // invoking it keeps focus on it (a drop-opener should not yank focus back to the editor on open)
    public void RetainingFocusScopeChild_IsExemptFromReturn()
    {
        var h = Build();
        using var _ = h.Host;

        // The chevron uses exactly this opt-out (FocusManager.SetIsFocusScope(chevron, true) in the bars theme):
        // a focus scope is a return barrier, so FindReturningScope stops at it instead of reaching the Toolbar.
        var opener = new BarButton { Content = "More" };
        FocusManager.SetIsFocusScope(opener, true);
        h.Toolbar.Items.Add(opener);
        h.Host.RunUntilIdle();

        h.Editor.Focus();
        opener.Focus(FocusNavigationMethod.Pointer); // enter the bar by pointer onto the drop-opener
        h.Host.RunUntilIdle();
        Assert.Same(opener, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Enter); // a normal bar button would return to the editor here; the opener must NOT
        h.Host.RunUntilIdle();
        Assert.Same(opener, h.Focus.FocusedElement); // exempt: focus stays on the opener
    }

    [Fact] // the root is immune: an access-key invoke on a control OUTSIDE any non-retaining scope keeps focus there
    public void Root_IsImmune_NormalAccessKeyInvokeStays()
    {
        var h = Build();
        using var _ = h.Host;

        h.Editor.Focus();
        h.Other.Focus(FocusNavigationMethod.AccessKey); // a root-level control — no non-retaining scope above it
        h.Host.RunUntilIdle();
        Assert.Same(h.Other, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Enter);
        h.Host.RunUntilIdle();
        Assert.Same(h.Other, h.Focus.FocusedElement); // stays — the root retains; no return fires
    }

    [Fact] // regression (P1): focus in a NESTED focus scope (a ListBox items host) is the return target, not the
           // enclosing scope's logical focus — the items host records the focused item, so the return must resolve
           // the scope that actually held focus, not the surface root's (stale) memory.
    public void NestedFocusScope_PointerInvoke_ReturnsToTheItemItCameFrom()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(40, 12),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;

        var cut = new BarButton { Content = "Cut" };
        var toolbar = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        toolbar.Items.Add(cut);

        var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
        var other = new Button { Content = "Other" }; // a root-level control — the (wrong) stale-memory candidate

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(toolbar); // row 0
        root.Children.Add(list);
        root.Children.Add(other);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        other.Focus();           // seed the surface-root scope memory with the WRONG element
        host.RunUntilIdle();

        var item1 = (ListBoxItem) list.ItemContainerGenerator.ContainerFromIndex(1)!;
        item1.Focus();           // focus a list item — memory records on the items-host panel scope, not the root
        host.RunUntilIdle();
        Assert.Same(item1, focus.FocusedElement);

        // Click "Cut" at the row's left edge (hover then release-click).
        host.SendMouseMove(2, 0);
        host.RunFrame();
        host.SendClick(2, 0);
        host.RunUntilIdle();

        Assert.Same(item1, focus.FocusedElement); // returned to the list item, NOT `other` (the root's stale memory)
    }

    [Fact] // bar→bar (audit): Tab into one toolbar, then invoke a button in ANOTHER toolbar by pointer — focus returns
           // to the original CONTENT, not the intermediate bar (MarkReturnableEntry chains the return scope through it).
    public void TwoToolbars_PointerInvokeInSecond_ReturnsToContent_NotFirstBar()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
        {
            InitialSize = new Size(48, 8),
            Capabilities = HeadlessCapabilities.KittyTruecolor,
        });
        using var _ = host;

        var aCut = new BarButton { Content = "ACut" };
        var barA = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        barA.Items.Add(aCut);

        var bCut = new BarButton { Content = "BCut" };
        var barB = new Toolbar { VerticalAlignment = VerticalAlignment.Top };
        barB.Items.Add(bCut);

        var editor = new Button { Content = "Editor" };

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(barA);   // row 0
        root.Children.Add(barB);   // row 1
        root.Children.Add(editor); // row 2
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        editor.Focus();                          // the true origin (outer-scope memory = editor)
        host.RunUntilIdle();

        aCut.Focus(FocusNavigationMethod.Tab);   // Tab INTO barA — no auto-return; barA captures editor's scope (root)
        host.RunUntilIdle();
        Assert.Same(aCut, focus.FocusedElement);

        // Pointer-click BCut in barB (row 1) — without ever returning out of barA first.
        host.SendMouseMove(2, 1);
        host.RunFrame();
        host.SendClick(2, 1);
        host.RunUntilIdle();

        Assert.Same(editor, focus.FocusedElement); // returns to content, NOT aCut (the intermediate bar)
    }

    [Fact] // P2 (#124): Escape from a retaining-scope-barrier child (the overflow chevron's shape) STILL returns focus
           // — the toolbar resolves the return through ITSELF, not the focused barrier child whose own scope blocks it.
    public void Escape_FromRetainingScopeBarrierChild_StillReturns()
    {
        var h = Build();
        using var _ = h.Host;

        // The chevron uses exactly this opt-out (a retaining focus scope = a FindReturningScope barrier). Escape from
        // it would, walking from the child, stop at the barrier and never return — the toolbar must resolve the return.
        var opener = new BarButton { Content = "More" };
        FocusManager.SetIsFocusScope(opener, true);
        h.Toolbar.Items.Add(opener);
        h.Host.RunUntilIdle();

        h.Editor.Focus();
        opener.Focus(FocusNavigationMethod.Pointer); // enter the bar onto the barrier child
        h.Host.RunUntilIdle();
        Assert.Same(opener, h.Focus.FocusedElement);

        h.Host.SendKey(Key.Escape);
        h.Host.RunUntilIdle();
        Assert.Same(h.Editor, h.Focus.FocusedElement); // returned to the editor (the #7 fix) — was stuck on the chevron
    }
}
