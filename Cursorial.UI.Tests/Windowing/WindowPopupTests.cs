using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

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
    private static UITestHost NewHost() =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });

    /// <summary>A root with a small placement-target button and a Popup wired to it (the popup is a logical
    /// child of the root panel so Escape routes back; the child is a focusable button so we can focus into it).</summary>
    private static (UITestHost Host, WindowManager Wm, Popup Popup, UIControls.Button Target, UIControls.Button Inner) Setup(bool staysOpen = false)
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
        var (host, wm, popup, target, _) = Setup();
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

        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        using var _ = host;
        var target = new UIControls.Button { Width = 10, Height = 1, Content = "t" };
        host.ShowRoot(new UIControls.Border { Background = new SolidColorBrush(desktop), Child = target });
        Assert.True(host.RunUntilIdle());

        var popup = new Popup
        {
            PlacementTarget = target,
            Child = new UIControls.Border { Background = new SolidColorBrush(popupFill), Width = 10, Height = 3 },
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
        owner.AddHandler(UIElement.KeyDownEvent, (object? _, KeyEventArgs _) => ownerSawKey = true);

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
}
