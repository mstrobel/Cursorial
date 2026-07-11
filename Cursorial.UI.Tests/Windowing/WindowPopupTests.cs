using System.ComponentModel;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W4 — <see cref="Popup"/> as the light-dismiss primitive (design doc §8.4): opening hosts the
/// <c>Child</c> on a popup surface in the band above every window, placed relative to the target and clamped
/// to the screen; an outside press light-dismisses (a press inside keeps it; <c>StaysOpen</c> opts out);
/// Escape closes through the cross-tree route (the route builder hops the logical parent at the surface root);
/// <c>IsOpen</c> writes back on every close; a content swap while open re-hosts. Tooltip hit-transparency,
/// anchor tracking, and chain semantics are W4-b.
/// </summary>
public sealed class WindowPopupTests
{
    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });

    /// <summary>A root with a small placement-target button and a Popup wired to it (the popup is a logical
    /// child of the root panel so Escape routes back; the child is a focusable button so we can focus into it).</summary>
    private static (UIHeadlessHost Host, WindowManager Wm, Popup Popup, UIControls.Button Target, UIControls.Button Inner) Setup(bool staysOpen = false)
    {
        var host = NewHost();
        var target = new UIControls.Button { Width = 10, Height = 1, Content = "open" };
        var inner = new UIControls.Button { Width = 8, Height = 3, Content = "menu" };
        var popup = new Popup { Child = inner, PlacementTarget = target, StaysOpen = staysOpen };

        var root = new UIControls.StackPanel();
        root.Children.Add(target);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        return (host, host.Application.WindowManager!, popup, target, inner);
    }

    [Fact] // Open hosts the child on a popup surface in the band above the root
    public void Open_HostsChildSurfaceInPopupBand()
    {
        var (host, wm, popup, _, inner) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);
        Assert.Same(popup, Assert.Single(wm.Popups));
        Assert.Equal(2, wm.Surfaces.Count);            // root + popup
        var surface = wm.Surfaces[^1];                 // the popup is the top of the stack
        Assert.True(surface.IsPopup);
        Assert.Same(inner, surface.Root);
        Assert.Same(surface, popup.PopupSurface);
    }

    [Fact] // PlacementMode.Bottom positions the surface directly below the target, in screen space
    public void PlacementBottom_PositionsBelowTarget()
    {
        var (host, _, popup, target, _) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());

        var (tx, ty) = target.TranslateToWindow(0, 0); // the root surface sits at the origin
        Assert.Equal(tx, popup.PopupSurface!.Left);
        Assert.Equal(ty + target.Bounds.Rows, popup.PopupSurface!.Top);
    }

    [Fact] // an uncaptured press outside the popup light-dismisses it (Closed fires with LightDismiss)
    public void OutsidePress_LightDismisses()
    {
        var (host, wm, popup, _, _) = Setup();
        using var _ = host;

        PopupCloseReason? reason = null;
        popup.Closed += (_, e) => reason = e.Reason;

        popup.Open();
        Assert.True(host.RunUntilIdle());
        var surface = popup.PopupSurface!;

        host.SendClick(surface.Left, surface.Top + surface.Size.Rows + 5); // below the popup, still on the root
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);                    // two-way write-back
        Assert.Empty(wm.Popups);
        Assert.Single(wm.Surfaces);                    // just the root again
        Assert.Equal(PopupCloseReason.LightDismiss, reason);
    }

    [Fact] // a press inside the popup keeps it open (the pressed surface is excluded from dismissal)
    public void PressInsidePopup_KeepsOpen()
    {
        var (host, _, popup, _, _) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());
        var surface = popup.PopupSurface!;

        host.SendClick(surface.Left + 1, surface.Top + 1); // inside the popup content
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);
        Assert.Same(surface, popup.PopupSurface);
    }

    [Fact] // a StaysOpen popup is not light-dismissed by an outside press
    public void StaysOpen_SurvivesOutsidePress()
    {
        var (host, _, popup, _, _) = Setup(staysOpen: true);
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());
        var surface = popup.PopupSurface!;

        host.SendClick(surface.Left, surface.Top + surface.Size.Rows + 5);
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);
    }

    [Fact] // a press inside a NESTED popup must NOT light-dismiss its ancestor popup (the toolbar-overflow
           // dropdown bug: a dropdown opened from a button inside the overflow popup collapsed the overflow —
           // and with it the dropdown — on a pointer press, so the item's Click never fired; keyboard was fine).
    public void NestedPopup_PressInsideInner_KeepsAncestorOpen()
    {
        var host = NewHost();
        using var _ = host;

        var target = new UIControls.Button { Width = 10, Height = 1, Content = "open" };

        // The outer popup's content hosts an anchor that opens an INNER popup (the dropdown-in-overflow shape).
        var innerAnchor = new UIControls.Button { Width = 8, Height = 1, Content = "drop" };
        var item = new UIControls.Button { Width = 8, Height = 1, Content = "item" };
        var innerPopup = new Popup { Child = item, PlacementTarget = innerAnchor };

        var outerContent = new UIControls.StackPanel();
        outerContent.Children.Add(innerAnchor);
        outerContent.Children.Add(innerPopup); // the inner popup lives logically in the outer content

        var outerPopup = new Popup { Child = outerContent, PlacementTarget = target };

        var root = new UIControls.StackPanel();
        root.Children.Add(target);
        root.Children.Add(outerPopup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        outerPopup.Open();
        Assert.True(host.RunUntilIdle());
        innerPopup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(outerPopup.IsOpen);
        Assert.True(innerPopup.IsOpen);

        var innerSurface = innerPopup.PopupSurface!;
        host.SendClick(innerSurface.Left + 1, innerSurface.Top); // press the item INSIDE the inner popup
        Assert.True(host.RunUntilIdle());

        Assert.True(innerPopup.IsOpen);  // the hit popup survives (as before)
        Assert.True(outerPopup.IsOpen);  // and so must its ancestor — the fix
    }

    [Fact] // Escape closes the popup through the cross-tree route (focus is inside the popup child)
    public void Escape_ClosesViaCrossTreeRoute()
    {
        var (host, wm, popup, _, inner) = Setup();
        using var _ = host;

        PopupCloseReason? reason = null;
        popup.Closed += (_, e) => reason = e.Reason;

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(inner.Focus());                    // focus inside the popup surface
        Assert.True(host.RunUntilIdle());

        host.SendKey(Key.Escape);
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Empty(wm.Popups);
        Assert.Equal(PopupCloseReason.EscapeKey, reason);
    }

    [Fact] // CloseOnEscape=false leaves the popup open under Escape
    public void Escape_Ignored_WhenCloseOnEscapeFalse()
    {
        var (host, _, popup, _, inner) = Setup();
        using var _ = host;

        popup.CloseOnEscape = false;
        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(inner.Focus());
        Assert.True(host.RunUntilIdle());

        host.SendKey(Key.Escape);
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);
    }

    [Fact] // swapping Child while open re-hosts the new child on a fresh surface without a close round-trip
    public void ContentSwap_WhileOpen_RehostsChild()
    {
        var (host, wm, popup, _, _) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());

        var replacement = new UIControls.Button { Width = 6, Height = 2, Content = "new" };
        popup.Child = replacement;
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);
        Assert.Same(popup, Assert.Single(wm.Popups));
        Assert.Same(replacement, popup.PopupSurface!.Root);
    }

    [Fact] // closing a popup repaints the cells it occupied — the desktop shows through, no lingering artifact
    public void Close_RepaintsVacatedCells()
    {
        var desktop = Color.FromRgb(10, 20, 30);
        var popupFill = Color.FromRgb(200, 100, 50);

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var _ = host;
        var target = new UIControls.Button { Width = 10, Height = 1, Content = "t" };
        host.ShowRoot(new UIControls.Border { Background = new SolidColorBrush(desktop), Child = target });
        Assert.True(host.RunUntilIdle());

        var popup = new Popup
        {
            PlacementTarget = target,
            Child = new UIControls.Border { Background = new SolidColorBrush(popupFill), Width = 10, Height = 3 }
        };

        popup.Open();
        Assert.True(host.RunUntilIdle());
        var s = popup.PopupSurface!;
        var (col, row) = (s.Left + 1, s.Top + 1);            // a cell well inside the popup
        Assert.Equal(popupFill, host.GetCell(col, row).Style.Background);

        popup.Close();
        Assert.True(host.RunUntilIdle());                    // the close must force a render (the fix)

        Assert.Equal(desktop, host.GetCell(col, row).Style.Background); // vacated → desktop, not stale popup fill
    }

    [Fact] // open → close → reopen the same popup (the Child surface must fully detach so it can re-host)
    public void Reopen_AfterClose_RehostsTheSameChild()
    {
        var (host, wm, popup, _, inner) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.Same(popup, Assert.Single(wm.Popups));

        popup.Close();
        Assert.True(host.RunUntilIdle());
        Assert.Empty(wm.Popups);
        Assert.False(inner.IsAttachedToTree); // the child must be fully detached, not half-attached

        popup.Open(); // reopen the SAME popup with the SAME child
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);
        Assert.Same(popup, Assert.Single(wm.Popups));
        Assert.Same(inner, popup.PopupSurface!.Root);
        Assert.True(inner.IsAttachedToTree);
    }

    [Fact] // clearing Child while open closes the popup cleanly (write-back + Closed, nothing left to show)
    public void ChildClearedWhileOpen_ClosesCleanly()
    {
        var (host, wm, popup, _, _) = Setup();
        using var _ = host;

        PopupCloseReason? reason = null;
        popup.Closed += (_, e) => reason = e.Reason;

        popup.Open();
        Assert.True(host.RunUntilIdle());

        popup.Child = null;
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Empty(wm.Popups);
        Assert.Null(popup.PopupSurface);
        Assert.Equal(PopupCloseReason.Programmatic, reason);
    }

    [Fact] // a placement-target-only popup (no logical parent) routes keys to its owner via the UIParent bridge
    public void StandalonePopup_RoutesKeysToOwner()
    {
        var host = NewHost();
        using var _ = host;
        var owner = new UIControls.Button { Width = 10, Height = 1, Content = "owner" };
        var root = new UIControls.StackPanel();
        root.Children.Add(owner);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // The popup is NOT in the logical tree — its only link to the owner is PlacementTarget.
        var inner = new UIControls.Button { Width = 8, Height = 1, Content = "x" };
        var popup = new Popup { Child = inner, PlacementTarget = owner };

        var ownerSawKey = false;
        owner.AddHandler(UIElement.KeyDownEvent, (_, _) => ownerSawKey = true);

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(inner.Focus());
        Assert.True(host.RunUntilIdle());

        host.SendKey(Key.F1); // a plain key, left unhandled — bubbles the full route
        Assert.True(host.RunUntilIdle());

        Assert.True(ownerSawKey); // routed across the PlacementTarget bridge (EventRoute uses UIParent)
    }

    [Fact] // a placement-target-only popup's child inherits the owner's DataContext — initially and on live change
    public void StandalonePopup_ChildInheritsOwnerDataContext()
    {
        var host = NewHost();
        using var _ = host;
        var owner = new UIControls.Button { Width = 10, Height = 1, Content = "owner", DataContext = "ctx-1" };
        var root = new UIControls.StackPanel();
        root.Children.Add(owner);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var inner = new UIControls.Button { Width = 8, Height = 1, Content = "x" };
        var popup = new Popup { Child = inner, PlacementTarget = owner };

        popup.Open();
        Assert.True(host.RunUntilIdle());

        Assert.Equal("ctx-1", inner.DataContext);  // inherited across the bridge: inner → popup → owner

        owner.DataContext = "ctx-2";                // live eager-notify through the bidirectional inheritance graph
        Assert.True(host.RunUntilIdle());
        Assert.Equal("ctx-2", inner.DataContext);
    }

    [Fact] // closing via IsOpen = false tears the surface down and writes back through the property
    public void Close_RemovesSurface_AndWritesBack()
    {
        var (host, wm, popup, _, _) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.Single(wm.Popups);

        popup.IsOpen = false;
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Empty(wm.Popups);
        Assert.Null(popup.PopupSurface);
    }

    // ───────────────────────────── W4-b: on-close focus restore ─────────────────────────────

    [Fact] // closing a popup that HELD keyboard focus returns focus to the SPECIFIC trigger (Esc-in-submenu → parent, dismiss → opener)
    public void FocusRestore_OnClose_ReturnsToTrigger()
    {
        // The trigger is NOT the first tab stop (a decoy precedes it), so this is distinguishable from
        // plain detach focus-repair — which would land on the scope-root's first tab stop (the decoy), not
        // the trigger. Only the explicit restore lands on `target`.
        var host = NewHost();
        using var _ = host;

        var decoy = new UIControls.Button { Width = 10, Height = 1, Content = "decoy" }; // the first tab stop
        var target = new UIControls.Button { Width = 10, Height = 1, Content = "open" };  // the trigger (second)
        var inner = new UIControls.Button { Width = 8, Height = 3, Content = "menu" };
        var popup = new Popup { Child = inner, PlacementTarget = target };
        var root = new UIControls.StackPanel();
        root.Children.Add(decoy);
        root.Children.Add(target);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        target.Focus();
        Assert.True(host.RunUntilIdle());
        Assert.Same(target, host.Application.FocusManager.FocusedElement);

        popup.Open();
        Assert.True(host.RunUntilIdle());
        inner.Focus(); // the popup takes keyboard focus (a focused menu item)
        Assert.True(host.RunUntilIdle());
        Assert.Same(inner, host.Application.FocusManager.FocusedElement);

        popup.Close();
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        // Restored to the SPECIFIC trigger, not the scope-root first tab stop (the decoy) that repair would pick.
        Assert.Same(target, host.Application.FocusManager.FocusedElement);
    }

    // A sticky-open view model: when the two-way IsOpen binding writes back false, it snaps the source back to
    // true and re-notifies — re-asserting "open" to the popup. The mid-close re-entrancy the !_open guard handles.
    private sealed class StickyOpenViewModel : INotifyPropertyChanged
    {
        private bool _isOpen;
        public bool Reassert { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                _isOpen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
                if (!value && Reassert)
                {
                    _isOpen = true; // snap back open and re-notify → the binding pushes true to the popup
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
                }
            }
        }
    }

    [Fact] // W7 #6: a sticky two-way IsOpen source that re-asserts "open" on the close write-back is handled
            // cleanly — the close/reopen re-entrancy converges (popup ends open, no exception). The CloseCore
            // `&& !_open` restore guard is the forward-defense for the SYNCHRONOUS re-open variant of this race;
            // the binding's re-assert lands a turn later, so this asserts the robust convergence the guard backs.
    public void StickyOpenSource_ReopensOnCloseWriteBack_ConvergesCleanly()
    {
        var host = NewHost();
        using var _ = host;

        var trigger = new UIControls.Button { Width = 10, Height = 1, Content = "open" };
        var inner = new UIControls.Button { Width = 8, Height = 3, Content = "menu" };
        var popup = new Popup { Child = inner, PlacementTarget = trigger };
        var root = new UIControls.StackPanel();
        root.Children.Add(trigger);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var vm = new StickyOpenViewModel();
        popup.SetBinding(Popup.IsOpenProperty, new Binding { Source = vm, Path = "IsOpen", Mode = BindingMode.TwoWay });

        trigger.Focus(); // the trigger holds focus at open time (captured by OpenCore)
        Assert.True(host.RunUntilIdle());

        vm.IsOpen = true; // open through the binding
        Assert.True(host.RunUntilIdle());
        Assert.True(popup.IsOpen);
        inner.Focus(); // the popup takes keyboard focus
        Assert.True(host.RunUntilIdle());
        Assert.Same(inner, host.Application.FocusManager.FocusedElement);

        vm.Reassert = true; // arm the sticky source: the next close write-back re-asserts "open"

        // Light-dismiss: the WM calls CloseCore with IsOpen still true → the write-back round-trips the two-way
        // binding, which re-asserts true and re-opens the popup. The cycle must converge without throwing.
        host.SendClick(trigger.TranslateToWindow(0, 0).Column, trigger.TranslateToWindow(0, 0).Row + 8); // outside both
        Assert.True(host.RunUntilIdle());

        Assert.True(popup.IsOpen);                            // converged to the sticky source's "open" state
        Assert.NotNull(popup.PopupSurface);                   // re-hosted on a live surface, not stranded
        var focused = host.Application.FocusManager.FocusedElement;
        Assert.True(focused is null || focused.IsAttachedToTree); // focus left in a valid (attached) state
    }

    [Fact] // closing a popup that never held focus does NOT yank focus back (the IsKeyboardFocusWithin guard)
    public void FocusRestore_Skipped_WhenPopupNeverHeldFocus()
    {
        var host = NewHost();
        using var _ = host;

        var target = new UIControls.Button { Width = 10, Height = 1, Content = "open" };
        var other = new UIControls.Button { Width = 10, Height = 1, Content = "other" };
        var inner = new UIControls.Button { Width = 8, Height = 3, Content = "menu" };
        var popup = new Popup { Child = inner, PlacementTarget = target };
        var root = new UIControls.StackPanel();
        root.Children.Add(target);
        root.Children.Add(other);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        target.Focus();        // trigger focused at open time (captured)
        Assert.True(host.RunUntilIdle());
        popup.Open();          // opens, but focus stays out of the popup
        Assert.True(host.RunUntilIdle());
        other.Focus();         // focus deliberately moves elsewhere
        Assert.True(host.RunUntilIdle());
        Assert.Same(other, host.Application.FocusManager.FocusedElement);

        popup.Close();
        Assert.True(host.RunUntilIdle());

        Assert.Same(other, host.Application.FocusManager.FocusedElement); // NOT yanked back to the trigger
    }

    [Fact] // A CLOSED Popup sitting in a template's visual tree must contribute NO hit-test footprint: its only
           // job in the host tree is structural (logical parent of Child, placement anchor) — its real content
           // lives in a separate surface when open. Otherwise the invisible, full-bounds (Stretch-arranged) popup
           // steals hover/clicks from the content it is layered over (the ComboBox/DatePicker face).
    public void ClosedPopupInTemplateDoesNotStealHitTesting()
    {
        var host = NewHost();
        using var _ = host;

        var face = new UIControls.Border { Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        // The drop-toggle template pattern: a 0-DesiredSize Popup added AFTER the face (z-topmost) in a Grid cell.
        var popup = new Popup { Child = new UIControls.Border { Width = 6, Height = 3 } };

        var grid = new UIControls.Grid();
        grid.Children.Add(face);
        grid.Children.Add(popup);
        host.ShowRoot(grid);
        host.RunUntilIdle();
        Assert.False(popup.IsOpen);

        // The pure query (the inspector / ToolTipService / cursor-resolution path) resolves the face, not the popup.
        Assert.Same(face, host.Application.InputDispatcher.HitTest(new CellPosition(30, 10)));

        // And hover lands on the face — its :pointerover would otherwise never fire under the invisible popup.
        host.SendMouseMove(30, 10);
        host.RunUntilIdle();
        Assert.True(face.IsPointerOver);
        Assert.False(popup.IsPointerOver);
    }

    [Fact] // #129: an open popup self-closes when its anchor (placement target / logical owner) leaves the tree — the
           // Child roots a separate surface, so nothing else would close the orphan (a page navigation / tab switch).
    public void PopupSelfClosesWhenTargetDetaches()
    {
        var host = NewHost();
        using var _ = host;

        var target = new UIControls.Button { Content = "anchor" };
        var popup = new Popup { Child = new UIControls.Button { Content = "menu" }, PlacementTarget = target };
        var root = new UIControls.StackPanel();
        root.Children.Add(target);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(popup.IsOpen);
        Assert.Same(popup, Assert.Single(host.Application.WindowManager!.Popups));

        // The target leaves the tree while the popup is open → the popup self-closes (no orphaned surface).
        root.Children.Remove(target);
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Empty(host.Application.WindowManager!.Popups);
    }

    [Fact] // #129 (audit-1): the HEADLINE scenario — the popup element sits DEEP inside a page that is swapped out
           // wholesale (page navigation / tab switch). The nested anchor's logical-detach never fires (that event is
           // NOT recursive), so the anchor-watch alone would orphan the surface; the popup element's own recursive
           // OnDetachedFromTree is what self-closes it. This is the primary case in-template popups actually hit.
    public void PopupSelfClosesWhenNestedInsideSwappedOutPage()
    {
        var host = NewHost();
        using var _ = host;

        // A "page": a panel with a nested anchor and the popup itself nested beneath the page root (NOT a direct child
        // of the host root — the popup element is deep inside the subtree that gets swapped).
        var anchor = new UIControls.Button { Content = "anchor" };
        var popup = new Popup { Child = new UIControls.Button { Content = "menu" }, PlacementTarget = anchor };
        var page = new UIControls.StackPanel();
        var inner = new UIControls.StackPanel();
        inner.Children.Add(anchor);
        inner.Children.Add(popup); // nested one level below the page root
        page.Children.Add(inner);

        // Host the page as ContentControl.Content — navigating away removes the page ROOT logically, not the nested
        // anchor or popup (the real page-switch shape).
        var content = new UIControls.ContentControl { Content = page };
        host.ShowRoot(content);
        Assert.True(host.RunUntilIdle());

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(popup.IsOpen);
        Assert.Same(popup, Assert.Single(host.Application.WindowManager!.Popups));

        // Navigate away: swap the whole page out. The page root is removed; the nested popup never sees a
        // logical-detach, but its recursive OnDetachedFromTree fires → self-close.
        content.Content = new UIControls.Button { Content = "other page" };
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Empty(host.Application.WindowManager!.Popups);
    }

    [Fact] // #129 (audit-2): re-anchoring an OPEN popup (PlacementTarget swapped A→B) must move the detach watch to B —
           // removing B closes it, and removing the stale A must NOT.
    public void PopupReanchoredWhileOpen_WatchesNewTarget_NotStaleOne()
    {
        var host = NewHost();
        using var _ = host;

        var a = new UIControls.Button { Content = "A" };
        var b = new UIControls.Button { Content = "B" };
        var popup = new Popup { Child = new UIControls.Button { Content = "menu" }, PlacementTarget = a };
        var root = new UIControls.StackPanel();
        root.Children.Add(a);
        root.Children.Add(b);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(popup.IsOpen);

        // Re-anchor to B while open.
        popup.PlacementTarget = b;
        Assert.True(host.RunUntilIdle());

        // Removing the STALE anchor A must NOT close the popup (the watch moved to B).
        root.Children.Remove(a);
        Assert.True(host.RunUntilIdle());
        Assert.True(popup.IsOpen);

        // Removing the CURRENT anchor B closes it.
        root.Children.Remove(b);
        Assert.True(host.RunUntilIdle());
        Assert.False(popup.IsOpen);
        Assert.Empty(host.Application.WindowManager!.Popups);
    }

    [Fact] // the frame-converged popup re-fit: content that grows WHILE OPEN re-sizes the surface (a popup is sized
           // at open from that instant's desired size — late-materializing content, e.g. bound bar-command faces,
           // used to stay clipped behind the frozen surface forever)
    public void ContentGrowth_WhileOpen_RefitsTheSurface()
    {
        var (host, _, popup, _, inner) = Setup();
        using var _ = host;

        popup.Open();
        Assert.True(host.RunUntilIdle());

        Assert.Equal(8, popup.PopupSurface!.Size.Columns); // sized to the open-time desired

        inner.Width = 20; // the content grows while open (the late-face analog)
        Assert.True(host.RunUntilIdle());

        Assert.Equal(20, popup.PopupSurface!.Size.Columns); // the surface re-fit — nothing clips
        Assert.Equal(20, inner.Bounds.Columns);

        inner.Width = 5; // …and a shrink re-fits down (no oversized ghost surface)
        Assert.True(host.RunUntilIdle());

        Assert.Equal(5, popup.PopupSurface!.Size.Columns);
    }

    [Fact] // the re-fit keeps the surface on-screen: growth near the right edge re-clamps left instead of clipping
    public void ContentGrowth_WhileOpen_ReclampsIntoTheViewport()
    {
        var host = NewHost();
        using var _ = host;
        var target = new UIControls.Button { Width = 10, Height = 1, Content = "t", Margin = new Margins(45, 0, 0, 0) };
        var inner = new UIControls.Button { Width = 8, Height = 3, Content = "menu" };
        var popup = new Popup { Child = inner, PlacementTarget = target };
        var root = new UIControls.StackPanel();
        root.Children.Add(target);
        root.Children.Add(popup);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        popup.Open();
        Assert.True(host.RunUntilIdle());

        inner.Width = 30; // grows past the 60-wide viewport's right edge from its anchor at 45
        Assert.True(host.RunUntilIdle());

        Assert.Equal(30, popup.PopupSurface!.Size.Columns);
        Assert.Equal(30, popup.PopupSurface!.Left); // 60 − 30: shifted left to stay whole, not clipped
    }
}
