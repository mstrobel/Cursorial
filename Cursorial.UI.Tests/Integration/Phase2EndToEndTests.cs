// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// the SendBytes tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// Phase 2 integration: the design doc §14 P2 exit criteria proven end-to-end through
/// <see cref="UITestHost"/> — every scenario enters through the harness input surface
/// (<c>SendKey</c>/<c>SendInput</c>/<c>SendBytes</c>) and crosses the full spine (frame loop
/// input drain → <c>InputDispatcher.ProcessEvent</c> → routing/focus/hover/capture → render →
/// Phase-6 <c>UpdateHover</c>), not a dispatcher harness. Covers: (a) tab cycling + scope memory +
/// window-activation focus restore, (b) hover re-targeting under scroll with no mouse motion,
/// (c) mouse capture across a drag that leaves the captor's bounds, (d) one <see cref="KeyBinding"/>
/// fired from both wire encodings of Ctrl+S (legacy C0 and Kitty CSI-u), (e) the terminal
/// focus-out cluster, and (f) the access-key gate verdicts under scripted capability presets.
/// </summary>
public sealed class Phase2EndToEndTests
{
    // ───────────────────────────── local elements ─────────────────────────────
    // Self-contained on purpose: the InputMatrix and LayoutMatrix fixtures both define `Probe`,
    // so this file brings its own minimal leaves instead of importing either namespace.

    /// <summary>A rigid host that fills nothing on its own — the neutral root container.</summary>
    private sealed class PlainHost : UIElement
    {
        public void Add(UIElement child) => AddVisualChild(child);
    }

    /// <summary>A rigid, focusable leaf.</summary>
    private sealed class FocusLeaf : UIElement
    {
        private readonly int _columns;
        private readonly int _rows;

        public FocusLeaf(string name, int columns = 6, int rows = 1)
        {
            Name = name;
            _columns = columns;
            _rows = rows;
            Focusable = true;
        }

        public new string Name { get; }

        protected override Size MeasureOverride(Size availableSize) => new(_columns, _rows);
    }

    /// <summary>A rigid, non-focusable leaf.</summary>
    private sealed class Leaf(int columns, int rows) : UIElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
    }

    /// <summary>
    /// The ButtonBase press pattern minus ButtonBase (P5): arms <see cref="InteractionState.Pressed"/>
    /// through the sanctioned protected setter on mouse-down, clears on mouse-up.
    /// </summary>
    private sealed class PressableButton : UIElement
    {
        public PressableButton() => Focusable = true;

        protected override Size MeasureOverride(Size availableSize) => new(8, 2);

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            SetInteractionState(InteractionState.Pressed, true);
            e.Handled = true;
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            SetInteractionState(InteractionState.Pressed, false);
            e.Handled = true;
        }
    }

    /// <summary>An <c>ICommand</c> recording executions.</summary>
    private sealed class RecordingCommand : ICommand
    {
        public int Executions { get; private set; }

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Executions++;
    }

    private static MouseEvent Mouse(
        MouseEventKind kind,
        int column,
        int row,
        MouseButton button = MouseButton.None,
        MouseButtons buttonsHeld = MouseButtons.None,
        int wheelDeltaY = 0)
        => new()
        {
            Kind = kind,
            Position = new CellPosition(column, row),
            Button = button,
            ButtonsHeld = buttonsHeld,
            Modifiers = KeyModifiers.None,
            WheelDeltaY = wheelDeltaY,
            ClickCount = 1,
            Timestamp = default, // stamped by UITestHost.SendInput on the fake clock
        };

    // ───────────────────────────── (a) tab cycle + scope memory + restore ─────────────────────────────

    /// <summary>
    /// The focus-restore round trip through a scope (§7.7): a <c>Once</c>-navigation focus-scope
    /// panel remembers its last focused element across an excursion; Tab cycling wraps at the root;
    /// window deactivate/activate restores from the ROOT scope's memory (the inner-scope excursion
    /// does not overwrite it — the menus/toolbars semantics), while the inner scope's memory
    /// survives for the next entry.
    /// </summary>
    [Fact]
    public void TabCycle_OnceScopeMemory_WindowActivationRestore_RoundTrip()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel();
        var left = new FocusLeaf("L");
        var scope = new StackPanel();
        var s1 = new FocusLeaf("S1");
        var s2 = new FocusLeaf("S2");
        var right = new FocusLeaf("R");
        scope.Children.Add(s1);
        scope.Children.Add(s2);
        root.Children.Add(left);
        root.Children.Add(scope);
        root.Children.Add(right);

        FocusManager.SetIsFocusScope(scope, true);
        KeyboardNavigation.SetTabNavigation(scope, KeyboardNavigationMode.Once);

        FocusNavigationMethod? leftMethod = null;
        left.AddHandler(UIElement.GotFocusEvent, (_, e) => leftMethod = e.Method);

        host.ShowRoot(root);
        host.RunFrame();

        var focus = host.Application.FocusManager;
        Assert.Same(left, focus.FocusedElement);                  // activation auto-focus: first tab stop
        Assert.Equal(FocusNavigationMethod.Restore, leftMethod);  // via the Restore method

        // Tab enters the Once container: no memory yet → first eligible descendant.
        host.SendKey(Key.Tab);
        host.RunFrame();
        Assert.Same(s1, focus.FocusedElement);

        // Move within the scope — the nearest-scope memory records S2 (not the root's).
        Assert.True(s2.Focus());
        Assert.Same(s2, focus.FocusedElement);
        Assert.Same(s2, FocusManager.GetFocusedElement(scope));

        // Tab leaves the Once container (one stop), then wraps at the root cycle.
        host.SendKey(Key.Tab);
        host.RunFrame();
        Assert.Same(right, focus.FocusedElement);

        host.SendKey(Key.Tab);
        host.RunFrame();
        Assert.Same(left, focus.FocusedElement); // wrap: R → L

        // Re-entry consumes the scope memory: Tab into the scope lands on S2, not S1.
        host.SendKey(Key.Tab);
        host.RunFrame();
        Assert.Same(s2, focus.FocusedElement);

        // Shift+Tab from inside the scope exits backward to L; Shift+Tab again wraps to R.
        host.SendKey(Key.Tab, KeyModifiers.Shift);
        host.RunFrame();
        Assert.Same(left, focus.FocusedElement);

        host.SendKey(Key.Tab, KeyModifiers.Shift);
        host.RunFrame();
        Assert.Same(right, focus.FocusedElement); // backward wrap: L → R (root memory records R)

        // The restore leg: physical focus moves into the scope, then the window deactivates and
        // re-activates. Restore consults the ROOT scope's memory — R, not the inner-scope S2 —
        // and the inner scope's memory survives the round trip.
        host.SendKey(Key.Tab); // R → wraps to L
        host.RunFrame();
        host.SendKey(Key.Tab); // L → Once entry → S2
        host.RunFrame();
        Assert.Same(s2, focus.FocusedElement);

        focus.OnWindowDeactivated(root);
        Assert.Null(focus.ActiveRoot);
        Assert.Same(s2, focus.FocusedElement); // deactivation retains physical focus

        focus.OnWindowActivated(root);
        Assert.Same(root, focus.ActiveRoot);
        Assert.Same(left, focus.FocusedElement);                   // root memory = L (last root-scope stop)
        Assert.Same(s2, FocusManager.GetFocusedElement(scope));    // inner memory intact for the next entry
    }

    // ───────────────────────────── (b) hover under scroll ─────────────────────────────

    /// <summary>
    /// The <c>UpdateHover</c> proof (punch 10): with the pointer stationary, a wheel-driven scroll
    /// offset change re-targets hover to the element newly under the pointer in the SAME frame —
    /// no mouse motion is ever sent after the first establishing move.
    /// </summary>
    [Fact]
    public void HoverUnderScroll_WheelChangesOffset_HoverRetargetsWithoutMouseMotion()
    {
        using var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(20, 10),
        });

        var root = new PlainHost();
        var presenter = new ScrollContentPresenter();
        var stack = new StackPanel();
        var rows = new Leaf[40];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new Leaf(20, 1);
            stack.Children.Add(rows[i]);
        }

        presenter.Content = stack;
        root.Add(presenter);

        var log = new List<string>();
        for (var i = 0; i < rows.Length; i++)
        {
            var index = i;
            rows[i].AddHandler(UIElement.MouseEnterEvent, (_, _) => log.Add($"enter:{index}"));
            rows[i].AddHandler(UIElement.MouseLeaveEvent, (_, _) => log.Add($"leave:{index}"));
        }

        // The wheel consumer (ScrollViewer arrives at P5): two rows per notch event.
        presenter.AddHandler(UIElement.MouseWheelEvent, (_, e) =>
        {
            presenter.ScrollOffsetRow += 2;
            e.Handled = true;
        });

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // Establish hover: pointer at terminal (5, 3) ⇒ content row 3 at offset 0.
        host.SendMouseMove(5, 3);
        host.RunFrame();
        Assert.True(rows[3].IsPointerOver);
        Assert.Contains("enter:3", log);
        log.Clear();

        // The wheel event scrolls 2 rows; the SAME frame renders the slide and Phase 6 re-diffs
        // hover against the retained pointer position — content row 5 is now under (5, 3).
        host.SendInput(Mouse(MouseEventKind.Wheel, 5, 3, wheelDeltaY: -1));
        host.RunFrame();

        Assert.Equal(2, presenter.ScrollOffsetRow);
        Assert.False(rows[3].IsPointerOver);
        Assert.True(rows[5].IsPointerOver);
        Assert.Equal(["leave:3", "enter:5"], log);
        log.Clear();

        // A purely programmatic offset change re-targets the same way (no input at all).
        presenter.ScrollOffsetRow = 0;
        host.RunFrame();

        Assert.False(rows[5].IsPointerOver);
        Assert.True(rows[3].IsPointerOver);
        Assert.Equal(["leave:5", "enter:3"], log);
    }

    // ───────────────────────────── (c) capture across a drag ─────────────────────────────

    /// <summary>
    /// Mouse capture end-to-end: a press grants capture; a drag OUTSIDE the captor's bounds keeps
    /// routing to it (with hover tracking the actual hit underneath — N091 semantics); the release
    /// routes to the captor, drops capture, and subsequent events route by hit again.
    /// </summary>
    [Fact]
    public void Capture_DragOutsideBounds_KeepsRoutingToCaptor_ReleaseRestoresHitRouting()
    {
        using var host = UITestHost.Create();
        var root = new Canvas();
        var captor = new Leaf(10, 4);
        var other = new Leaf(10, 4);
        Canvas.SetLeft(captor, 10);
        Canvas.SetTop(captor, 5);
        Canvas.SetLeft(other, 30);
        Canvas.SetTop(other, 5);
        root.Children.Add(captor);
        root.Children.Add(other);

        var log = new List<string>();
        captor.AddHandler(UIElement.MouseDownEvent, (_, e) =>
        {
            captor.CaptureMouse();
            e.Handled = true;
        });
        captor.AddHandler(UIElement.MouseUpEvent, (_, e) =>
        {
            captor.ReleaseMouseCapture();
            e.Handled = true;
        });
        captor.AddHandler(UIElement.MouseMoveEvent, (_, _) => log.Add("captor.move"));
        captor.AddHandler(UIElement.LostMouseCaptureEvent, (_, _) => log.Add("captor.lost-capture"));
        other.AddHandler(UIElement.MouseMoveEvent, (_, _) => log.Add("other.move"));
        other.AddHandler(UIElement.MouseUpEvent, (_, _) => log.Add("other.up"));

        host.ShowRoot(root);
        host.RunFrame();

        var dispatcher = host.Application.InputDispatcher;

        // Press inside the captor: the MouseDown handler takes capture.
        host.SendInput(Mouse(MouseEventKind.ButtonDown, 12, 6, MouseButton.Left));
        host.RunFrame();
        Assert.Same(captor, dispatcher.MouseCaptureTarget);

        // Drag OUTSIDE the captor's bounds, over `other`: routing sticks to the captor while
        // hover tracks the element actually under the pointer.
        host.SendInput(Mouse(MouseEventKind.Drag, 32, 6, buttonsHeld: MouseButtons.Left));
        host.RunFrame();

        Assert.Contains("captor.move", log);
        Assert.DoesNotContain("other.move", log);
        Assert.True(other.IsPointerOver);   // hit testing runs under capture
        Assert.False(captor.IsPointerOver); // the pointer left the captor
        log.Clear();

        // Release outside: still routes to the captor (capture), which lets go.
        host.SendInput(Mouse(MouseEventKind.ButtonUp, 32, 6, MouseButton.Left));
        host.RunFrame();

        Assert.Null(dispatcher.MouseCaptureTarget);
        Assert.Contains("captor.lost-capture", log);
        Assert.DoesNotContain("other.up", log);
        log.Clear();

        // After the release: routing is by hit again and hover reflects the pointer truthfully.
        host.SendInput(Mouse(MouseEventKind.Move, 33, 6));
        host.RunFrame();

        Assert.Equal(["other.move"], log);
        Assert.True(other.IsPointerOver);
        Assert.False(captor.IsPointerOver);
    }

    // ───────────────────────────── (d) one KeyBinding, both wire encodings ─────────────────────────────

    /// <summary>
    /// The gesture contract across encodings (doc §7.3): raw DC3 (legacy C0 Ctrl+S) and the Kitty
    /// CSI-u form both decode to <c>(Character, "s", Control)</c> and fire the SAME
    /// <see cref="KeyBinding"/> — bytes through the real <c>VtInputDevice</c>, not injected records.
    /// </summary>
    [Fact]
    public void KeyBinding_CtrlS_FiresFromBothWireEncodings()
    {
        using var host = UITestHost.Create();
        var root = new Canvas();
        root.Children.Add(new Leaf(10, 2));

        var command = new RecordingCommand();
        root.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), command));

        host.ShowRoot(root);
        host.RunFrame();

        // Leg 1 — the legacy C0 wire: DC3 (0x13) decodes as (Character, "s", Control).
        host.SendBytes([0x13]);
        host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
        host.RunFrame();
        Assert.Equal(1, command.Executions);

        // Leg 2 — the Kitty CSI-u wire: CSI 115;5 u decodes to the same shape.
        host.SendBytes("\x1b[115;5u"u8);
        host.DrainParsedInputAsync().GetAwaiter().GetResult();
        host.RunFrame();
        Assert.Equal(2, command.Executions);
    }

    // ───────────────────────────── (e) terminal focus-out ─────────────────────────────

    /// <summary>
    /// The <c>FocusEvent{HasFocus:false}</c> cluster end-to-end (doc §13.2 / ND24): pressed state
    /// clears, <c>EditCommitRequested</c> fires for the focused element, keyboard focus is RETAINED
    /// (no <c>LostFocus</c>), and the held-Alt access-key cue clears.
    /// </summary>
    [Fact]
    public void TerminalFocusOut_PressedCleared_EditCommitFires_FocusRetained_AltCueCleared()
    {
        using var host = UITestHost.Create();
        var root = new Canvas();
        var button = new PressableButton();
        Canvas.SetLeft(button, 10);
        Canvas.SetTop(button, 5);
        root.Children.Add(button);

        host.ShowRoot(root);
        host.RunFrame();

        var focus = host.Application.FocusManager;
        Assert.Same(button, focus.FocusedElement); // activation auto-focus

        // Arm the cluster: a press held (no release) + Alt held (cue up under the Kitty preset).
        host.SendInput(Mouse(MouseEventKind.ButtonDown, 12, 6, MouseButton.Left));
        host.RunFrame();
        Assert.True((button.InteractionStateInternal & InteractionState.Pressed) != 0);

        host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
        host.RunFrame();
        Assert.True(host.Application.AccessKeys.IsCueActive);

        var dispatcher = host.Application.InputDispatcher;
        UIElement? editCommitTarget = null;
        bool? terminalFocus = null;
        var lostFocusRaises = 0;
        dispatcher.EditCommitRequested += element => editCommitTarget = element;
        dispatcher.TerminalFocusChanged += has => terminalFocus = has;
        button.AddHandler(UIElement.LostFocusEvent, (_, _) => lostFocusRaises++);

        // The terminal loses focus.
        host.SendInput(new FocusEvent { HasFocus = false, Timestamp = default });
        host.RunFrame();

        Assert.Equal(0u, (uint)(button.InteractionStateInternal & InteractionState.Pressed)); // pressed cleared
        Assert.Same(button, editCommitTarget);              // EditCommitRequested → the focused element
        Assert.Same(button, focus.FocusedElement);          // keyboard focus RETAINED
        Assert.Equal(0, lostFocusRaises);                   // …with no LostFocus raise
        Assert.False(host.Application.AccessKeys.IsCueActive); // Alt-held bracket cleared
        Assert.False(terminalFocus);                        // the app-level notification fired

        // Focus returns: the same element keeps composing without any focus churn.
        host.SendInput(new FocusEvent { HasFocus = true, Timestamp = default });
        host.RunFrame();
        Assert.True(terminalFocus);
        Assert.Same(button, focus.FocusedElement);
        Assert.Equal(0, lostFocusRaises);
    }

    // ───────────────────────────── (f) access-key gate verdicts ─────────────────────────────

    /// <summary>
    /// The punch-21 gate on scripted capability presets: a Kitty-class terminal (key up/down +
    /// repeats) gets the Alt-toggle UX (<see cref="AccessKeyMode.AltHeld"/> — cue on Alt down,
    /// sticky after a tap, second tap toggles off); a legacy 16-color terminal gets permanently
    /// visible cues (<see cref="AccessKeyMode.AlwaysVisible"/>).
    /// </summary>
    [Fact]
    public void AccessKeyGate_KittyAltToggle_LegacyPermanentCues()
    {
        // Kitty preset: DistinguishesKeyUpDown ∧ ReportsRepeats ⇒ AltHeld.
        using (var host = UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.KittyTruecolor }))
        {
            var root = new Canvas();
            root.Children.Add(new Leaf(10, 2));
            host.ShowRoot(root);
            host.RunFrame();

            var accessKeys = host.Application.AccessKeys;
            Assert.Equal(AccessKeyMode.AltHeld, accessKeys.Mode);
            Assert.False(accessKeys.IsCueActive); // hidden until Alt

            // Alt down ⇒ cue up; Alt tap (up with no intervening key) ⇒ sticky cue stays.
            host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
            host.RunFrame();
            Assert.True(accessKeys.IsCueActive);

            host.SendInput(new KeyEvent { Key = Key.LeftAlt, Kind = KeyEventKind.Up, Modifiers = KeyModifiers.None, Text = ReadOnlyMemory<char>.Empty, Timestamp = default });
            host.RunFrame();
            Assert.True(accessKeys.IsCueActive); // sticky after the tap

            // A second tap toggles the sticky cue off.
            host.SendKey(Key.LeftAlt, KeyModifiers.Alt);
            host.RunFrame();
            host.SendInput(new KeyEvent { Key = Key.LeftAlt, Kind = KeyEventKind.Up, Modifiers = KeyModifiers.None, Text = ReadOnlyMemory<char>.Empty, Timestamp = default });
            host.RunFrame();
            Assert.False(accessKeys.IsCueActive);
        }

        // Legacy preset: no key-up/repeat truth and no Win32 input mode ⇒ AlwaysVisible, cue
        // permanently on from the capability fan-out.
        using (var host = UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.Ansi16Legacy }))
        {
            var root = new Canvas();
            root.Children.Add(new Leaf(10, 2));
            host.ShowRoot(root);
            host.RunFrame();

            var accessKeys = host.Application.AccessKeys;
            Assert.Equal(AccessKeyMode.AlwaysVisible, accessKeys.Mode);
            Assert.True(accessKeys.IsCueActive); // permanent — no Alt choreography exists on this wire
        }
    }
}
