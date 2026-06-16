using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C8 — ToolTip / ToolTipService (P9.4d): hover-driven, UITimer-paced, on S3's HoverChanged
// hook. The tooltip Popup is hit-test-transparent + never focused; it closes on leave/press/key/focus-out.
public sealed class Section21_ToolTip
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    // A centered Tip-bearing Border (fixed-size → centered in the 40×16 host; hittable via its Background).
    private static (UITestHost Host, Border Tip) RootWithTip(object tip)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 16) });
        var element = new Border { Width = 20, Height = 4, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        ToolTipService.SetTip(element, tip);
        host.ShowRoot(element);
        host.RunUntilIdle();
        return (host, element);
    }

    private static int Popups(UITestHost host) => host.Application.WindowManager!.Popups.Count;

    private static void Hover(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToWindow(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row + 1); // inside the element
        host.RunFrame();
    }

    private static void MoveAway(UITestHost host)
    {
        host.SendMouseMove(0, 0); // the far corner, off the centered element
        host.RunFrame();
    }

    // Hovers the element and lets the open delay elapse → the tooltip is shown.
    private static void Show(UITestHost host, UIElement element)
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
        Assert.True(surface!.IsHitTestTransparent);
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
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 16) });
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
}
