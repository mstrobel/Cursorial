using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §11 — RepeatButton + ToggleButton (C1; rows C199–C206). RepeatButton's UITimer-driven repeat
/// (delay/interval, pressed+over gate, cancel on release/move-off) and ToggleButton's 2-/3-state
/// cycle + <c>:checked</c>/<c>:indeterminate</c> + access-key/Space/click parity. One test per row.
/// </summary>
public sealed class Section11_RepeatToggle
{
    // ───────────────────────────── helpers ─────────────────────────────

    private static UITestHost Show(UIElement root)
    {
        var host = UITestHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    private static T TopLeft<T>(T element) where T : UIElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        return element;
    }

    private static MouseEvent Mouse(MouseEventKind kind, int column, int row, MouseButtons held = MouseButtons.None) => new()
    {
        Kind = kind,
        Position = new CellPosition(column, row),
        Button = kind is MouseEventKind.ButtonDown or MouseEventKind.ButtonUp ? MouseButton.Left : MouseButton.None,
        ButtonsHeld = held,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private static void HoverOver(UITestHost host, int column, int row)
    {
        host.SendMouseMove(column, row);
        host.RunFrame();
    }

    // ───────────────────────────── RepeatButton (C199–C202) ─────────────────────────────

    [Fact] // C199
    public void C199_RepeatButtonDefaults()
    {
        var rb = new RepeatButton();
        Assert.Equal(400, rb.Delay);
        Assert.Equal(60, rb.Interval);
        Assert.Equal(ClickMode.Press, rb.ClickMode); // RepeatButton presses (CD29)
    }

    [Fact] // C200
    public void C200_PressedRepeatsAfterDelayThenInterval()
    {
        var rb = TopLeft(new RepeatButton { Delay = 400, Interval = 60, Width = 10, Height = 1 });
        var clicks = 0;
        rb.Click += (_, _) => clicks++;
        using var host = Show(rb);
        var dispatcher = host.Application.InputDispatcher;

        HoverOver(host, 2, 0);
        dispatcher.ProcessEvent(Mouse(MouseEventKind.ButtonDown, 2, 0));
        host.RunFrame();
        Assert.Equal(1, clicks); // the press-mode down click (CD29 — repeats begin from the press)

        // Before the delay elapses: no repeat.
        host.AdvanceTime(TimeSpan.FromMilliseconds(200));
        host.RunFrame();
        Assert.Equal(1, clicks);

        // After the delay: the first repeat.
        host.AdvanceTime(TimeSpan.FromMilliseconds(200));
        host.RunFrame();
        Assert.True(clicks >= 2);

        // Then one per interval while held.
        var afterDelay = clicks;
        host.AdvanceTime(TimeSpan.FromMilliseconds(60));
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(60));
        host.RunFrame();
        Assert.True(clicks > afterDelay);

        dispatcher.ProcessEvent(Mouse(MouseEventKind.ButtonUp, 2, 0));
    }

    [Fact] // C201
    public void C201_RepeatStopsOnRelease()
    {
        var rb = TopLeft(new RepeatButton { Delay = 100, Interval = 50, Width = 10, Height = 1 });
        var clicks = 0;
        rb.Click += (_, _) => clicks++;
        using var host = Show(rb);
        var dispatcher = host.Application.InputDispatcher;

        HoverOver(host, 2, 0);
        dispatcher.ProcessEvent(Mouse(MouseEventKind.ButtonDown, 2, 0));
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(100));
        host.RunFrame();
        Assert.True(clicks >= 2);

        dispatcher.ProcessEvent(Mouse(MouseEventKind.ButtonUp, 2, 0));
        host.RunFrame();
        var afterRelease = clicks;

        // No further repeats after release (the timer is canceled + unhooked, CD29/C201).
        host.AdvanceTime(TimeSpan.FromMilliseconds(500));
        host.RunFrame();
        Assert.Equal(afterRelease, clicks);
    }

    [Fact] // C202
    public void C202_RepeatPausesWhenPointerLeavesResumesOnReturn()
    {
        var rb = TopLeft(new RepeatButton { Delay = 100, Interval = 50, Width = 10, Height = 1 });
        var clicks = 0;
        rb.Click += (_, _) => clicks++;
        using var host = Show(rb);
        var dispatcher = host.Application.InputDispatcher;

        HoverOver(host, 2, 0);
        dispatcher.ProcessEvent(Mouse(MouseEventKind.ButtonDown, 2, 0));
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(100));
        host.RunFrame();
        Assert.True(clicks >= 2);

        // Drag off-self (still pressed but not over): repeats pause.
        dispatcher.ProcessEvent(Mouse(MouseEventKind.Move, 60, 20, MouseButtons.Left));
        host.RunFrame();
        var paused = clicks;
        host.AdvanceTime(TimeSpan.FromMilliseconds(300));
        host.RunFrame();
        Assert.Equal(paused, clicks); // paused (pressed tracks pointer-over)

        // Back over self: repeats resume.
        dispatcher.ProcessEvent(Mouse(MouseEventKind.Move, 2, 0, MouseButtons.Left));
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(150));
        host.RunFrame();
        Assert.True(clicks > paused);

        dispatcher.ProcessEvent(Mouse(MouseEventKind.ButtonUp, 2, 0));
    }

    // ───────────────────────────── ToggleButton (C203–C206) ─────────────────────────────

    [Fact] // C203
    public void C203_ToggleButtonProperties()
    {
        var toggle = new ToggleButton();
        Assert.False(toggle.IsChecked); // default false (bool?)
        Assert.False(toggle.IsThreeState);

        // :checked (true) / :indeterminate (null) via PseudoClassMapping multi-class projection.
        using var host = Show(toggle);
        toggle.IsChecked = true;
        host.RunFrame();
        Assert.True(toggle.HasCustomPseudoClass(":checked"));
        Assert.False(toggle.HasCustomPseudoClass(":indeterminate"));

        toggle.IsThreeState = true;
        toggle.IsChecked = null;
        host.RunFrame();
        Assert.True(toggle.HasCustomPseudoClass(":indeterminate"));
        Assert.False(toggle.HasCustomPseudoClass(":checked"));
    }

    [Fact] // C204
    public void C204_TwoStateCycle()
    {
        var toggle = new ToggleButton { IsThreeState = false };
        using var host = Show(toggle);
        toggle.Focus();

        var states = new List<bool?>();
        for (var i = 0; i < 3; i++)
        {
            host.SendKey(Key.Space);
            host.RunFrame();
            states.Add(toggle.IsChecked);
        }

        Assert.Equal(new bool?[] { true, false, true }, states); // false→true→false→true
    }

    [Fact] // C205
    public void C205_ThreeStateCycle()
    {
        var toggle = new ToggleButton { IsThreeState = true };
        using var host = Show(toggle);
        toggle.Focus();

        var states = new List<bool?>();
        for (var i = 0; i < 4; i++)
        {
            host.SendKey(Key.Space);
            host.RunFrame();
            states.Add(toggle.IsChecked);
        }

        // false → true → null → false → true (WPF 3-state order, CD26).
        Assert.Equal(new bool?[] { true, null, false, true }, states);
    }

    [Fact] // C206
    public void C206_AccessKeySpaceClickAllToggle()
    {
        // Space toggles.
        var spaceToggle = new ToggleButton();
        using (var host = Show(spaceToggle))
        {
            spaceToggle.Focus();
            host.SendKey(Key.Space);
            host.RunFrame();
            Assert.Equal(true, spaceToggle.IsChecked);
        }

        // Enter toggles (rides OnClick → OnToggle).
        var enterToggle = new ToggleButton();
        using (var host = Show(enterToggle))
        {
            enterToggle.Focus();
            host.SendKey(Key.Enter);
            host.RunFrame();
            Assert.Equal(true, enterToggle.IsChecked);
        }

        // Access key toggles (OnAccessKey → OnClick → OnToggle).
        var akToggle = new ToggleButton { Content = "_Toggle" };
        using (var host = Show(akToggle))
        {
            ((IAccessKeyTarget)akToggle).OnAccessKey(new AccessKeyEventArgs('T', isMultiMatch: false, akToggle));
            host.RunFrame();
            Assert.Equal(true, akToggle.IsChecked);
        }
    }
}
