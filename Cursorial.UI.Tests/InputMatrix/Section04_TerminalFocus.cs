using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Input matrix §4 — the FocusEvent{HasFocus:false} cluster (N49–N57): B focused, capture held by
/// C, hover over A, pressed-holders {B, C} (B keyboard-pressed without capture), Alt held (AltHeld
/// mode under the default KittyCaps).
/// </summary>
public class Section04_TerminalFocus
{
    private static (UIHeadlessHost Host, List<string> Log, Probe Root, Probe A, Btn B, Probe C, InputDispatcher Dispatcher)
        CreateCluster(bool focusB = true)
    {
        var host = UIHeadlessHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var a = new Probe("A", log, 4, 2);
        var b = new Btn("B", log);
        var c = new Probe("C", log, 4, 2);
        root.Place(a, 0, 0);
        root.Place(b, 10, 0);
        root.Place(c, 20, 0);
        host.ShowRoot(root);
        host.RunFrame(); // settle layout + render so hit testing is valid

        if (focusB)
            Assert.True(b.Focus());
        else
            host.Application.FocusManager.ClearFocus(); // activation auto-focused B (N115) — this cluster wants none

        // Hover over A (the I2 cluster leg) — established before the capture grant so the chain
        // reflects the pointer, then capture held by C.
        var dispatcher = host.Application.InputDispatcher;
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(1, 1),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        Assert.True((a.InteractionStateInternal & InteractionState.PointerOver) != 0);

        Assert.True(c.CaptureMouse());

        // Pressed-holders {B, C}: B keyboard-pressed without capture (the C8 coverage), C pressed
        // while holding capture.
        b.SetState(InteractionState.Pressed, true);
        c.SetState(InteractionState.Pressed, true);

        // Alt held (AltHeld mode under KittyCaps): the cue is up, a side bit is set.
        dispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.LeftAlt,
            Kind = KeyEventKind.Down,
            Modifiers = KeyModifiers.Alt,
            Text = ReadOnlyMemory<char>.Empty,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        Assert.True(host.Application.AccessKeys.IsCueActive);

        log.Clear();
        return (host, log, root, a, b, c, dispatcher);
    }

    private static FocusEvent FocusEvt(bool hasFocus)
        => new() { HasFocus = hasFocus, Timestamp = DateTimeOffset.UnixEpoch };

    [Fact]
    public void N049_TerminalFocusOut_FullClusterOrder_ND24()
    {
        var (host, log, _, _, _, _, dispatcher) = CreateCluster();
        using var _host = host;

        host.Application.InteractionStateObserver = new StateSink(log);
        dispatcher.EditCommitRequested += e => log.Add($"edit-commit:{(e as Probe)?.LogName}");
        dispatcher.TerminalFocusChanged += has => log.Add($"terminal-focus:{has}");

        dispatcher.ProcessEvent(FocusEvt(false));

        // ND24 markers: ① ak cue-clear (the first Root state flip — AccessKeyCue off) →
        // ② C.OnLostMouseCapture → ③ A.OnMouseLeave → ④ Pressed clears → ⑤ EditCommit(B) →
        // ⑥ TerminalFocusChanged(false).
        var akClear = log.FindIndex(static e => e.StartsWith("state:Root:", StringComparison.Ordinal));
        var capture = log.IndexOf("C.OnLostMouseCapture");
        var hover = log.IndexOf("A.OnMouseLeave");
        var pressedB = log.FindIndex(static e => e.StartsWith("state:B:", StringComparison.Ordinal) && e.Contains("Pressed"));
        var pressedC = log.FindIndex(static e => e.StartsWith("state:C:", StringComparison.Ordinal) && e.Contains("Pressed"));
        var commit = log.IndexOf("edit-commit:B");
        var terminal = log.IndexOf("terminal-focus:False");

        Assert.True(akClear >= 0 && log[akClear].Contains("AccessKeyCue"), $"ak-clear marker missing: {string.Join(", ", log)}");
        Assert.True(capture > akClear, "② capture release must follow ① the ak clear");
        Assert.True(hover > capture, "③ hover Leave must follow ② capture release");
        Assert.True(pressedB > hover && pressedC > hover, "④ pressed clears must follow ③ the hover clear");
        Assert.True(commit > pressedB && commit > pressedC, "⑤ EditCommitRequested must follow ④ pressed clears");
        Assert.True(terminal > commit, "⑥ TerminalFocusChanged must come last");
    }

    [Fact]
    public void N051_TerminalFocusOut_AllPressedHoldersCleared_IncludingCaptureless()
    {
        var (host, _, _, _, b, c, dispatcher) = CreateCluster();
        using var _host = host;

        Assert.True(dispatcher.IsPressedHolderInternal(b));
        Assert.True(dispatcher.IsPressedHolderInternal(c));

        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Equal(0u, (uint)(b.InteractionStateInternal & InteractionState.Pressed)); // C8: keyboard-held press, no capture
        Assert.Equal(0u, (uint)(c.InteractionStateInternal & InteractionState.Pressed));
        Assert.Equal(0, dispatcher.PressedHolderCountInternal);
    }

    [Fact]
    public void N050_TerminalFocusOut_KeyboardFocusRetained_NoLostFocus()
    {
        var (host, log, _, _, b, _, dispatcher) = CreateCluster();
        using var _host = host;

        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Same(b, host.Application.FocusManager.FocusedElement); // focus retained (doc §13.2)
        Assert.DoesNotContain(log, static entry => entry.Contains("LostFocus") && !entry.Contains("MouseCapture"));
    }

    [Fact]
    public void N052_TerminalFocusOut_CaptureForceReleased_DirectLostMouseCaptureOnce()
    {
        var (host, log, _, _, _, _, dispatcher) = CreateCluster();
        using var _host = host;

        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Null(dispatcher.MouseCaptureTarget);
        Assert.Equal(1, log.Count(static entry => entry == "C.OnLostMouseCapture")); // exactly once, Direct
        Assert.DoesNotContain("Root.OnLostMouseCapture", log);                       // no bubbling
    }

    [Fact]
    public void N053_TerminalFocusOut_HoverChainCleared_LeaveDeepestFirst()
    {
        var (host, log, root, a, _, _, dispatcher) = CreateCluster();
        using var _host = host;

        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Equal(0u, (uint)(a.InteractionStateInternal & InteractionState.PointerOver));
        Assert.Equal(0u, (uint)(root.InteractionStateInternal & InteractionState.PointerOver));
        var leaves = log.Where(static entry => entry.Contains("MouseLeave")).ToList();
        Assert.Equal(["A.OnMouseLeave", "Root.OnMouseLeave"], leaves); // deepest-first over the old chain
    }

    [Fact]
    public void N054_NoFocusedElement_EditCommitNotRaised_RestOfClusterProceeds()
    {
        var (host, _, _, _, _, _, dispatcher) = CreateCluster(focusB: false);
        using var _host = host;

        var commits = new List<UIElement>();
        var terminalFocus = new List<bool>();
        dispatcher.EditCommitRequested += commits.Add;
        dispatcher.TerminalFocusChanged += terminalFocus.Add;

        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Empty(commits);                          // ND24 ⑤: only with a focused element
        Assert.Equal([false], terminalFocus);           // everything else proceeds
        Assert.Null(dispatcher.MouseCaptureTarget);
    }

    [Fact]
    public void N055_FocusOutThenIn_TerminalFocusChangedBoth_FocusIntact_NoGotLostFocus()
    {
        var (host, log, _, _, b, _, dispatcher) = CreateCluster();
        using var _host = host;

        var terminalFocus = new List<bool>();
        dispatcher.TerminalFocusChanged += terminalFocus.Add;

        dispatcher.ProcessEvent(FocusEvt(false));
        dispatcher.ProcessEvent(FocusEvt(true));

        Assert.Equal([false, true], terminalFocus);
        Assert.Same(b, host.Application.FocusManager.FocusedElement); // intact across the round trip
        Assert.DoesNotContain(log, static entry => entry.Contains("GotFocus"));
        Assert.DoesNotContain(log, static entry => entry.Contains(".OnLostFocus"));
    }

    [Fact]
    public void N056_EditCommitRequested_OncePerFocusOutEvent_NoDedupe()
    {
        var (host, _, _, _, b, _, dispatcher) = CreateCluster();
        using var _host = host;

        var commits = new List<UIElement>();
        dispatcher.EditCommitRequested += commits.Add;

        dispatcher.ProcessEvent(FocusEvt(false));
        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Equal(2, commits.Count); // once per event — B still focused for the second
        Assert.All(commits, element => Assert.Same(b, element));
    }

    [Fact]
    public void N057_AfterTerminalFocusOut_KeysStillDispatchToRetainedFocus()
    {
        var (host, log, _, _, _, _, dispatcher) = CreateCluster();
        using var _host = host;

        dispatcher.ProcessEvent(FocusEvt(false));
        log.Clear();

        dispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.Character,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = "f".AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch
        });

        Assert.Contains("B.OnKeyDown", log); // terminal focus is not element focus
    }

    [Fact]
    public void N203_TerminalFocusOut_ButtonsHeldZeroed()
    {
        var (host, _, _, _, _, _, dispatcher) = CreateCluster();
        using var _host = host;

        // Buttons held: a Down with no matching Up — the Up will go to another window.
        dispatcher.ProcessEvent(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(1, 1),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        Assert.Equal(MouseButtons.Left, dispatcher.ButtonsHeld);

        dispatcher.ProcessEvent(FocusEvt(false));

        Assert.Equal(MouseButtons.None, dispatcher.ButtonsHeld); // ND24 ④ — no stale held mask survives a focus loss
    }
}
