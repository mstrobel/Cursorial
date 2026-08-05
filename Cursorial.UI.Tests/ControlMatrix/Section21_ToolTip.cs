using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C8 — ToolTip / ToolTipService (P9.4d): hover-driven, UITimer-paced, on S3's HoverChanged
// hook. The tooltip Popup is hit-test-transparent + never focused; it closes on leave/press/key/focus-out.
public sealed class Section21_ToolTip
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    // A centered Tip-bearing Border (fixed-size → centered in the 40×16 host; hittable via its Background).
    private static (UIHeadlessHost Host, Border Tip) RootWithTip(object tip)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        var element = new Border { Width = 20, Height = 4, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        ToolTipService.SetTip(element, tip);
        host.ShowRoot(element);
        host.RunUntilIdle();
        return (host, element);
    }

    private static int Popups(UIHeadlessHost host) => host.Application.WindowManager!.Popups.Count;

    private static void Hover(UIHeadlessHost host, UIElement element)
    {
        var origin = element.TranslateToWindow(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row + 1); // inside the element
        host.RunFrame();
    }

    private static void MoveAway(UIHeadlessHost host)
    {
        host.SendMouseMove(0, 0); // the far corner, off the centered element
        host.RunFrame();
    }

    // Hovers the element and lets the open delay elapse → the tooltip is shown.
    private static void Show(UIHeadlessHost host, UIElement element)
    {
        Hover(host, element);
        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();
    }

    [Fact] // C8.1: hovering a Tip-bearing element shows the tooltip after the delay (not before)
    public void C8_1_HoverShowsAfterDelay()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Hover(host, tip);
        Assert.Equal(0, Popups(host)); // the delay hasn't elapsed

        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();
        Assert.Equal(1, Popups(host)); // shown on the open timer
    }

    [Fact] // C8.2: leaving before the delay cancels the pending open
    public void C8_2_LeaveBeforeDelayCancels()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Hover(host, tip);
        MoveAway(host); // leave before the delay
        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();
        Assert.Equal(0, Popups(host)); // the timer was cancelled
    }

    [Fact] // C8.3: leaving after the tooltip is shown closes it
    public void C8_3_LeaveAfterShownCloses()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        Assert.Equal(1, Popups(host));

        MoveAway(host);
        Assert.Equal(0, Popups(host)); // hover-leave closed it
    }

    [Fact] // C8.4: a mouse button press closes the shown tooltip (DismissTransients)
    public void C8_4_ButtonPressCloses()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        Assert.Equal(1, Popups(host));

        host.SendClick(2, 2); // any button press
        host.RunFrame();
        Assert.Equal(0, Popups(host));
    }

    [Fact] // C8.5: a non-modifier key press closes the shown tooltip
    public void C8_5_KeyPressCloses()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        Assert.Equal(1, Popups(host));

        host.SendKey(Key.Enter);
        host.RunFrame();
        Assert.Equal(0, Popups(host));
    }

    [Fact] // C8.6: a bare modifier key press does NOT close the tooltip (Kitty reports Shift as a discrete event)
    public void C8_6_BareModifierKeepsTooltip()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        Assert.Equal(1, Popups(host));

        host.SendKey(Key.LeftShift);
        host.RunFrame();
        Assert.Equal(1, Popups(host)); // still shown
    }

    [Fact] // C8.7: quick-show — a re-hover within 100 ms of a close shows immediately (no delay)
    public void C8_7_QuickShow()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        MoveAway(host); // close (arms the 100 ms quick-show window)
        Assert.Equal(0, Popups(host));

        Hover(host, tip);  // re-hover within the window → delay 0
        host.RunFrame();   // no AdvanceTime — the open timer is due immediately
        Assert.Equal(1, Popups(host));
    }

    [Fact] // C8.8: the tooltip's popup surface is hit-test-transparent (never steals hover/clicks)
    public void C8_8_PopupHitTestTransparent()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        var surface = host.Application.WindowManager!.Popups[0].PopupSurface;
        Assert.NotNull(surface);
        Assert.True(surface.IsHitTestTransparent);
    }

    [Fact] // C8.9: terminal focus-out closes the tooltip (it must not outlive the focused terminal)
    public void C8_9_FocusOutCloses()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        Assert.Equal(1, Popups(host));

        host.SendInput(new Cursorial.Input.Events.FocusEvent { HasFocus = false, Timestamp = DateTimeOffset.UnixEpoch });
        host.RunFrame();
        Assert.Equal(0, Popups(host));
    }

    [Fact] // C8.10: the attached properties round-trip with their defaults
    public void C8_10_AttachedPropertyDefaults()
    {
        var element = new Border();
        Assert.Null(ToolTipService.GetTip(element));
        Assert.Equal(TimeSpan.FromMilliseconds(500), ToolTipService.GetInitialDelay(element)); // default 500 ms
        Assert.Null(ToolTipService.GetShowOnFocus(element));                                    // null = auto

        ToolTipService.SetTip(element, "x");
        ToolTipService.SetInitialDelay(element, TimeSpan.FromMilliseconds(50));
        ToolTipService.SetShowOnFocus(element, true);
        Assert.Equal("x", ToolTipService.GetTip(element));
        Assert.Equal(TimeSpan.FromMilliseconds(50), ToolTipService.GetInitialDelay(element));
        Assert.True(ToolTipService.GetShowOnFocus(element));
    }

    [Fact] // C8.11: a ToolTip is never a focus stop and never hit-tested
    public void C8_11_ToolTipNotInteractive()
    {
        var toolTip = new ToolTip();
        Assert.False(toolTip.Focusable);
        Assert.False(toolTip.IsHitTestVisible);
    }

    [Fact] // C8.12: a custom InitialDelay is honored (shorter than the default)
    public void C8_12_CustomDelayHonored()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var element = new Border { Width = 20, Height = 4, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        ToolTipService.SetTip(element, "hello");
        ToolTipService.SetInitialDelay(element, TimeSpan.FromMilliseconds(100));
        host.ShowRoot(element);
        host.RunUntilIdle();

        Hover(host, element);
        host.AdvanceTime(TimeSpan.FromMilliseconds(120)); // past the custom 100 ms, well short of 500 ms
        host.RunFrame();
        Assert.Equal(1, Popups(host));
    }

    [Fact] // C8.13: clearing the Tip while the open timer is pending shows nothing (no empty tooltip)
    public void C8_13_TipClearedWhilePendingNoShow()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Hover(host, tip);                 // arm the open timer
        ToolTipService.SetTip(tip, null); // clear the tip before the timer fires
        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();
        Assert.Equal(0, Popups(host));    // Show() bails when the tip is gone
    }

    [Fact] // C8.14: with nested tip-bearing elements, the innermost wins (the popup anchors to it)
    public void C8_14_InnermostTipWins()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var inner = new Border { Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) }; // fills outer, hittable
        ToolTipService.SetTip(inner, "inner");
        var outer = new Border { Width = 24, Height = 8, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Child = inner };
        ToolTipService.SetTip(outer, "outer");
        host.ShowRoot(outer);
        host.RunUntilIdle();

        Show(host, inner); // hovering the inner hits inner (on top); the chain carries both tips
        Assert.Equal(1, Popups(host));
        Assert.Same(inner, host.Application.WindowManager!.Popups[0].PlacementTarget); // innermost, not outer
    }

    [Fact] // C8.15: entering a no-tip child of the tip element does NOT reset the open timer (intra-element move)
    public void C8_15_IntraElementMoveDoesNotResetTimer()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var child = new Border // a no-tip child pinned top-left, smaller than the parent (leaves bare parent area)
        {
            Width = 8, Height = 2,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(70, 70, 70))
        };
        var parent = new Border { Width = 24, Height = 8, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Child = child };
        ToolTipService.SetTip(parent, "p");
        host.ShowRoot(parent);
        host.RunUntilIdle();

        var bare = parent.TranslateToWindow(0, 0);
        host.SendMouseMove(bare.Column + 20, bare.Row + 6); // a parent-only cell (beyond the top-left child)
        host.RunFrame();                                    // arm the open timer for the parent
        host.AdvanceTime(TimeSpan.FromMilliseconds(300));   // partway through the 500 ms delay

        var childOrigin = child.TranslateToWindow(0, 0);
        host.SendMouseMove(childOrigin.Column + 1, childOrigin.Row + 1); // move INTO the no-tip child (still inside the parent)
        host.RunFrame();                                                 // must NOT reset the parent's timer

        host.AdvanceTime(TimeSpan.FromMilliseconds(250)); // total ~550 ms — past the ORIGINAL 500 ms deadline
        host.RunFrame();
        Assert.Equal(1, Popups(host)); // shown on the un-reset timer (a reset would push it to ~800 ms)
    }

    [Fact] // C8.16: the quick-show window EXPIRES — re-hovering after 100 ms requires the full delay again
    public void C8_16_QuickShowWindowExpires()
    {
        var (host, tip) = RootWithTip("hello");
        using var _ = host;

        Show(host, tip);
        MoveAway(host); // close → arms the 100 ms quick-show window
        host.AdvanceTime(TimeSpan.FromMilliseconds(150)); // let the window expire
        host.RunFrame();

        Hover(host, tip); // re-hover after expiry
        host.RunFrame();
        Assert.Equal(0, Popups(host)); // NOT immediate — the quick-show window closed

        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();
        Assert.Equal(1, Popups(host)); // shows only after the full delay
    }

    [Fact] // C8.17: one controller per application — two tip-bearing elements never stack duplicate tooltips
    public void C8_17_OneControllerPerApp()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var a = new Border { Width = 16, Height = 3, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        var b = new Border { Width = 16, Height = 3, Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)) };
        ToolTipService.SetTip(a, "a"); // each SetTip calls ToolTipController.Ensure — must be idempotent
        ToolTipService.SetTip(b, "b");
        var panel = new StackPanel();
        panel.Children.Add(a);
        panel.Children.Add(b);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        Show(host, a);
        Assert.Equal(1, Popups(host)); // exactly one popup — a duplicate controller would show two
    }

    [Fact] // C8.20: a press-dismissed tooltip stays dismissed until the pointer leaves and returns
    public void C8_20_DismissedTooltip_DoesNotResurrectOnATipChange()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var element = new Border { Width = 20, Height = 4, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        ToolTipService.SetTip(element, "one");
        host.ShowRoot(element);
        host.RunUntilIdle();

        Show(host, element);
        Assert.Equal(1, Popups(host));

        var inside = element.TranslateToWindow(1, 1);
        host.SendMouseDown(inside.Column, inside.Row);   // §12.7: a press dismisses
        host.RunFrame();
        Assert.Equal(0, Popups(host));

        // An owner that republishes its tip as the pointer moves (a chart tracking a series) must
        // NOT bring the dismissed tooltip back — the pointer never left.
        ToolTipService.SetTip(element, "two");
        host.RunFrame();
        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();
        Assert.Equal(0, Popups(host));

        // Leaving and returning is the re-arming edge, and it works again.
        MoveAway(host);
        Show(host, element);
        Assert.Equal(1, Popups(host));
    }

    // ── C8.18/C8.19: a tip ARMED WHILE ALREADY HOVERING (no hover-enter edge to arm on — the
    // ChartPresenter case, whose tip only appears once the pointer reaches plotted ink) ──

    [Fact] // C8.18: the tip appearing under a stationary pointer arms its OWN owner
    public void C8_18_TipSetWhileHovering_ArmsItsOwner()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var element = new Border { Width = 20, Height = 4, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        host.ShowRoot(element);
        host.RunUntilIdle();

        Hover(host, element);                       // hover FIRST, while it has no tip
        Assert.Equal(0, Popups(host));

        ToolTipService.SetTip(element, "armed");    // the tip appears with the pointer already inside
        host.RunFrame();
        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();

        Assert.Equal(1, Popups(host));
    }

    [Fact] // C8.19: another element's tip change must NOT arm whatever sits under the pointer
    public void C8_19_UnrelatedTipChange_DoesNotArmTheHoveredElement()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        using var _ = host;
        var hovered = new Border { Width = 16, Height = 3, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        var elsewhere = new Border { Width = 16, Height = 3, Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)) };
        ToolTipService.SetTip(hovered, "hovered");  // it HAS a tip — but the pointer never entered it
        var panel = new StackPanel();
        panel.Children.Add(hovered);
        panel.Children.Add(elsewhere);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        // Park the pointer INSIDE `hovered` with the controller's target cleared: hover-enter arms
        // it, then a press at that same cell dismisses (DismissTransients → Reset) without moving
        // the pointer. `hovered` is still what the pointer is over, and it still has a tip.
        var inside = hovered.TranslateToWindow(1, 1);
        Hover(host, hovered);
        host.SendMouseDown(inside.Column, inside.Row);   // dismiss; pointer stays on `hovered`
        host.RunFrame();
        Assert.Equal(0, Popups(host));

        // A DIFFERENT element's tip changes. Nothing is under the pointer but `hovered`, whose tip
        // did not change — so no tooltip may arm.
        ToolTipService.SetTip(elsewhere, "elsewhere");
        host.RunFrame();
        host.AdvanceTime(Delay + TimeSpan.FromMilliseconds(50));
        host.RunFrame();

        Assert.Equal(0, Popups(host));
    }
}
