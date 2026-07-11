using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

/// <summary>
/// §10 — ButtonBase + Button (C0/C1; rows C181–C198). Click event + ClickMode + command, mouse
/// capture + <c>:pressed</c> + cleanup, keyboard down-activation + <c>IsEnabledCore</c>/CanExecute
/// discipline. One test per row, namespace per the §17 contract.
/// </summary>
public sealed class Section10_ButtonBase
{
    // ───────────────────────────── helpers ─────────────────────────────

    private static UIHeadlessHost Show(UIElement root)
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(root);
        host.RunFrame();
        return host;
    }

    /// <summary>A button anchored top-left so it arranges at (0,0) — clicks land at known cells.</summary>
    private static Button TopLeft(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Top;
        return button;
    }

    private static MouseEvent MouseDown(int column, int row) => new()
    {
        Kind = MouseEventKind.ButtonDown,
        Position = new CellPosition(column, row),
        Button = MouseButton.Left,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static MouseEvent MouseUp(int column, int row) => new()
    {
        Kind = MouseEventKind.ButtonUp,
        Position = new CellPosition(column, row),
        Button = MouseButton.Left,
        ButtonsHeld = MouseButtons.None,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static MouseEvent MouseMoveHeld(int column, int row) => new()
    {
        Kind = MouseEventKind.Move,
        Position = new CellPosition(column, row),
        Button = MouseButton.None,
        ButtonsHeld = MouseButtons.Left,
        Modifiers = KeyModifiers.None,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    // Establishes pointer-over on the target cell (the hover that precedes a real click), applied via
    // the frame loop's UpdateHover. Use before a MouseDown so IsPointerOver is true at up time.
    private static void HoverOver(UIHeadlessHost host, int column, int row)
    {
        host.SendMouseMove(column, row);
        host.RunFrame();
    }

    /// <summary>A scriptable <see cref="System.Windows.Input.ICommand"/> recording executions.</summary>
    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        public bool CanExecuteResult { get; set; } = true;
        public int Executions { get; private set; }
        public object? LastParameter { get; private set; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => CanExecuteResult;
        public void Execute(object? parameter) { Executions++; LastParameter = parameter; }
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── 10.1 Click + ClickMode + command ─────────────────────────────

    [Fact] // C181
    public void C181_ButtonPropertyMetadata()
    {
        var button = new Button();
        Assert.Equal(ClickMode.Release, button.ClickMode);
        Assert.Null(button.Command);
        Assert.False(button.IsPressed);
        Assert.True(ButtonBase.IsPressedProperty.IsReadOnly);
        Assert.Equal(RoutingStrategy.Bubble, ButtonBase.ClickEvent.Strategy);

        // The CLR Click sugar wires the routed event.
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var host = Show(button);
        using (host)
        {
            button.Focus();
            host.SendKey(Key.Enter);
            host.RunFrame();
            Assert.Equal(1, clicks);
        }
    }

    [Fact] // C182
    public void C182_OnClick_RaisesThenExecutesWhenCanExecute()
    {
        var command = new RelayCommand { CanExecuteResult = true };
        var button = new Button { Command = command, CommandParameter = "p" };
        var clicked = false;
        button.Click += (_, _) => clicked = true;

        using var host = Show(button);
        button.Focus();
        host.SendKey(Key.Enter);
        host.RunFrame();

        Assert.True(clicked);                 // Click raised
        Assert.Equal(1, command.Executions);  // then Execute, because CanExecute
        Assert.Equal("p", command.LastParameter);
    }

    [Fact] // C183
    public void C183_CanExecuteFalse_ClickRaisesButNoExecute()
    {
        var command = new RelayCommand { CanExecuteResult = false };
        var button = new Button { Command = command };
        var clicked = false;
        button.Click += (_, _) => clicked = true;

        using var host = Show(button);
        button.Focus();
        host.SendKey(Key.Enter);
        host.RunFrame();

        Assert.True(clicked);                 // Click still raises
        Assert.Equal(0, command.Executions);  // Execute NOT called
        Assert.False(button.IsEffectivelyEnabled); // effectively disabled via IsEnabledCore (C25)
    }

    [Fact] // C184
    public void C184_ClickMode_PressVsRelease()
    {
        var pressButton = TopLeft(new Button { ClickMode = ClickMode.Press, Width = 10, Height = 1 });
        var pressClicks = 0;
        pressButton.Click += (_, _) => pressClicks++;
        using (var host = Show(pressButton))
        {
            var dispatcher = host.Application.InputDispatcher;
            dispatcher.ProcessEvent(MouseDown(2, 0));
            host.RunFrame();
            Assert.Equal(1, pressClicks); // clicks on DOWN
            dispatcher.ProcessEvent(MouseUp(2, 0));
        }

        var releaseButton = TopLeft(new Button { ClickMode = ClickMode.Release, Width = 10, Height = 1 });
        var releaseClicks = 0;
        releaseButton.Click += (_, _) => releaseClicks++;
        using (var host = Show(releaseButton))
        {
            var dispatcher = host.Application.InputDispatcher;
            dispatcher.ProcessEvent(MouseDown(2, 0));
            host.RunFrame();
            Assert.Equal(0, releaseClicks); // no click yet
            dispatcher.ProcessEvent(MouseUp(2, 0));
            host.RunFrame();
            Assert.Equal(1, releaseClicks); // up-over ⇒ click
        }
    }

    // ───────────────────────────── 10.2 Mouse capture + :pressed + cleanup ─────────────────────────────

    [Fact] // C185
    public void C185_MouseDown_CapturesAndPresses()
    {
        var button = TopLeft(new Button { Width = 10, Height = 1 });
        using var host = Show(button);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(MouseDown(2, 0));
        host.RunFrame();

        Assert.True(button.IsPressed);                          // :pressed via SetInteractionState (CD24)
        Assert.True((button.InteractionStateInternal & InteractionState.Pressed) != 0);

        dispatcher.ProcessEvent(MouseUp(2, 0));
    }

    [Fact] // C186
    public void C186_PressedTracksPointerOverWhileCaptured()
    {
        var button = TopLeft(new Button { Width = 10, Height = 1 });
        using var host = Show(button);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(MouseDown(2, 0));
        host.RunFrame();
        Assert.True(button.IsPressed);

        // Drag off-self (captured): pressed clears; back on-self: pressed returns.
        dispatcher.ProcessEvent(MouseMoveHeld(60, 20));
        host.RunFrame();
        Assert.False(button.IsPressed);

        dispatcher.ProcessEvent(MouseMoveHeld(2, 0));
        host.RunFrame();
        Assert.True(button.IsPressed);

        dispatcher.ProcessEvent(MouseUp(2, 0));
    }

    [Fact] // C187
    public void C187_UpOverSelf_Clicks()
    {
        var button = TopLeft(new Button { ClickMode = ClickMode.Release, Width = 10, Height = 1 });
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        var dispatcher = host.Application.InputDispatcher;

        HoverOver(host, 2, 0); // pointer over the button (the hover that precedes a real click)
        dispatcher.ProcessEvent(MouseDown(2, 0));
        dispatcher.ProcessEvent(MouseUp(2, 0)); // up over self ⇒ click
        host.RunFrame();

        Assert.Equal(1, clicks);
        Assert.False(button.IsPressed);
    }

    [Fact] // C188
    public void C188_UpOffSelf_NoClick()
    {
        var button = TopLeft(new Button { ClickMode = ClickMode.Release, Width = 10, Height = 1 });
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(MouseDown(2, 0));
        dispatcher.ProcessEvent(MouseMoveHeld(60, 20)); // drag off
        dispatcher.ProcessEvent(MouseUp(60, 20));       // up off self ⇒ no click
        host.RunFrame();

        Assert.Equal(0, clicks);
        Assert.False(button.IsPressed);
    }

    [Fact] // C189
    public void C189_LostMouseCapture_ClearsPressedNoClick()
    {
        var button = TopLeft(new Button { Width = 10, Height = 1 });
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(MouseDown(2, 0));
        host.RunFrame();
        Assert.True(button.IsPressed);

        button.ReleaseMouseCapture(); // capture stolen ⇒ OnLostMouseCapture
        host.RunFrame();

        Assert.False(button.IsPressed); // unpressed (C189)
        Assert.Equal(0, clicks);        // no click
    }

    [Fact] // C190
    public void C190_TerminalFocusOut_ClearsPressedWindowWide()
    {
        var button = TopLeft(new Button { Width = 10, Height = 1 });
        using var host = Show(button);
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(MouseDown(2, 0));
        host.RunFrame();
        Assert.True(button.IsPressed);

        // Terminal focus-out clears the pressed-holder set window-wide (ND12/CD24).
        dispatcher.ProcessEvent(new FocusEvent { HasFocus = false, Timestamp = DateTimeOffset.UnixEpoch });
        host.RunFrame();

        Assert.False(button.IsPressed);
    }

    // ───────────────────────────── 10.3 Keyboard down-activation + CanExecute ─────────────────────────────

    [Fact] // C191
    public void C191_SpaceDown_ActivatesAndLatches()
    {
        var button = new Button { Width = 10, Height = 1 };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        button.Focus();

        host.SendKey(Key.Space); // down only
        host.RunFrame();

        Assert.Equal(1, clicks);        // activates on Down (CD23)
        Assert.True(button.IsPressed);  // pressed latch (Up reported)

        host.SendKey(Key.Space, withRelease: false);
    }

    [Fact] // C192
    public void C192_SpaceRepeat_DoesNotReactivate()
    {
        var button = new Button { Width = 10, Height = 1 };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        button.Focus();

        host.SendKey(Key.Space);
        host.RunFrame();
        Assert.Equal(1, clicks);

        // An auto-repeat (IsRepeat) does not re-activate (CD23/C192).
        host.SendInput(new KeyEvent { Key = Key.Space, Kind = KeyEventKind.Down, Modifiers = KeyModifiers.None, IsRepeat = true, Timestamp = DateTimeOffset.UnixEpoch });
        host.RunFrame();
        Assert.Equal(1, clicks);
    }

    [Fact] // C193
    public void C193_Enter_ImmediateClickNoLatch()
    {
        var button = new Button { Width = 10, Height = 1 };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        button.Focus();

        host.SendKey(Key.Enter);
        host.RunFrame();

        Assert.Equal(1, clicks);
        Assert.False(button.IsPressed); // no latch for Enter (C193)
    }

    [Fact] // C194
    public void C194_SpaceLatchClearsOnLostFocus_NoClick()
    {
        var sib = new Button { Width = 10, Height = 1 };
        var button = new Button { Width = 10, Height = 1 };
        var panel = new StackPanel();
        panel.Children.Add(button);
        panel.Children.Add(sib);
        using var host = Show(panel);

        button.Focus();
        var clicksBefore = 0;
        button.Click += (_, _) => clicksBefore++;

        host.SendKey(Key.Space); // activates once on down + latches
        host.RunFrame();
        var afterSpace = clicksBefore;
        Assert.True(button.IsPressed);

        sib.Focus(); // button loses focus → latch clears
        host.RunFrame();

        Assert.False(button.IsPressed);
        Assert.Equal(afterSpace, clicksBefore); // no further click from the focus loss
    }

    [Fact] // C195
    public void C195_IsEnabledCore_IncludesCanExecute()
    {
        var command = new RelayCommand { CanExecuteResult = false };
        var button = new Button { Command = command };
        using var host = Show(button);

        Assert.False(button.IsEffectivelyEnabled);
        Assert.True((button.InteractionStateInternal & InteractionState.Disabled) != 0); // :disabled

        command.CanExecuteResult = true;
        command.Raise();
        host.RunFrame();
        Assert.True(button.IsEffectivelyEnabled);
    }

    [Fact] // C196
    public void C196_CanExecuteChanged_RecomputesEffectiveEnabled()
    {
        var command = new RelayCommand { CanExecuteResult = true };
        var button = new Button { Command = command };
        using var host = Show(button);
        Assert.True(button.IsEffectivelyEnabled);

        command.CanExecuteResult = false;
        command.Raise(); // → InvalidateIsEnabledCore → effective recompute
        host.RunFrame();

        Assert.False(button.IsEffectivelyEnabled);
        Assert.True((button.InteractionStateInternal & InteractionState.Disabled) != 0);
    }

    [Fact] // C197
    public void C197_CanExecuteChanged_SubscriptionDiscipline()
    {
        var first = new RelayCommand { CanExecuteResult = true };
        var second = new RelayCommand { CanExecuteResult = true };
        var button = new Button { Command = first };
        using var host = Show(button);

        // Swapping Command unsubscribes the old: a stale CanExecuteChanged must not move the button.
        button.Command = second;
        host.RunFrame();

        first.CanExecuteResult = false;
        first.Raise(); // the OLD command — must NOT affect the button
        host.RunFrame();
        Assert.True(button.IsEffectivelyEnabled);

        second.CanExecuteResult = false;
        second.Raise(); // the live command — DOES affect it
        host.RunFrame();
        Assert.False(button.IsEffectivelyEnabled);
    }

    [Fact] // C198
    public void C198_IsDefault_InstallsEnterBinding()
    {
        var defaultBtn = new Button { IsDefault = true, Width = 10, Height = 1 };
        // A focusable non-button leaf: it does NOT handle Enter, so Enter bubbles unhandled to the
        // surface-root KeyBinding the default button installs (focused-element-wins is bubble order).
        var other = new FocusableLeaf { Width = 10, Height = 1 };
        var panel = new StackPanel();
        panel.Children.Add(other);
        panel.Children.Add(defaultBtn);
        using var host = Show(panel);

        var defaultClicks = 0;
        defaultBtn.Click += (_, _) => defaultClicks++;

        Assert.True(other.Focus());
        host.SendKey(Key.Enter);
        host.RunFrame();
        Assert.True(defaultClicks >= 1);

        Assert.True(defaultBtn.HasCustomPseudoClass(":default")); // :default pseudo-class on IsDefault

        // Removing IsDefault removes the binding.
        defaultBtn.IsDefault = false;
        host.RunFrame();
        var before = defaultClicks;
        other.Focus();
        host.SendKey(Key.Enter);
        host.RunFrame();
        Assert.Equal(before, defaultClicks); // no longer activated by Enter from elsewhere
    }

    // ───────────────────────────── 10.4 real-spacebar activation (ND10 regression) ─────────────────────────────

    [Fact] // C199 — the real spacebar (Key.Character + " ") activates, per input-matrix ND10
    public void C199_RealSpacebar_ActivatesAndToggles()
    {
        // ND10: a real spacebar arrives as (Key.Character, Text == " ") on EVERY protocol path
        // (VT 0x20, Kitty codepoint 32, Win32 VK_SPACE). Key.Space is emitted only for NUL→Ctrl+Space.
        // UITestHost.SendText(" ") reproduces the production wire (Key.Character + " ").

        // (a) A Button activates (Click) on a real-spacebar DOWN.
        var button = new Button { Width = 10, Height = 1 };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using (var host = Show(button))
        {
            button.Focus();
            host.SendText(" "); // Key.Character + " " — the real spacebar
            host.RunFrame();
            Assert.Equal(1, clicks);       // activates on Down (CD23), even though Key != Key.Space
            Assert.True(button.IsPressed); // pressed latch
        }

        // (b) A CheckBox toggles IsChecked on a real-spacebar DOWN (routes through ButtonBase).
        var check = new CheckBox();
        using (var host = Show(check))
        {
            check.Focus();
            Assert.Equal(false, check.IsChecked);

            host.SendText(" ");
            host.RunFrame();
            Assert.Equal(true, check.IsChecked); // toggled by the real spacebar (was the bug)

            host.SendText(" ");
            host.RunFrame();
            Assert.Equal(false, check.IsChecked); // toggles back
        }

        // (c) A RadioButton (also via ButtonBase) checks on a real-spacebar DOWN.
        var radio = new RadioButton();
        using (var host = Show(radio))
        {
            radio.Focus();
            host.SendText(" ");
            host.RunFrame();
            Assert.Equal(true, radio.IsChecked);
        }
    }

    [Fact] // C200 — the real-spacebar latch clears on Up / focus loss (Key.Character identity at all sites)
    public void C200_RealSpacebar_LatchClearsOnUpAndFocusLoss()
    {
        // The pressed-latch identity must use the same (Key.Character, " ") match as activation, or a
        // real-spacebar Up would never clear the latch and the button would stay :pressed.
        var button = new Button { Width = 10, Height = 1 };
        using (var host = Show(button))
        {
            button.Focus();
            host.SendKey(Key.Character, text: " "); // down only
            host.RunFrame();
            Assert.True(button.IsPressed);

            host.SendKey(Key.Character, text: " "); // a real-spacebar Up is a Character event too
            host.SendInput(new KeyEvent { Key = Key.Character, Kind = KeyEventKind.Up, Text = " ".AsMemory(), Modifiers = KeyModifiers.None, Timestamp = DateTimeOffset.UnixEpoch });
            host.RunFrame();
            Assert.False(button.IsPressed); // Up cleared the latch (C200)
        }

        // Focus loss clears the latch too (no further click).
        var sib = new Button { Width = 10, Height = 1 };
        var latched = new Button { Width = 10, Height = 1 };
        var panel = new StackPanel();
        panel.Children.Add(latched);
        panel.Children.Add(sib);
        using (var host = Show(panel))
        {
            latched.Focus();
            var clicks = 0;
            latched.Click += (_, _) => clicks++;

            host.SendKey(Key.Character, text: " "); // activates + latches
            host.RunFrame();
            Assert.True(latched.IsPressed);
            var afterSpace = clicks;

            sib.Focus(); // focus loss → latch clears, no extra click
            host.RunFrame();
            Assert.False(latched.IsPressed);
            Assert.Equal(afterSpace, clicks);
        }
    }

    [Fact] // C201 — Ctrl+Space (NUL → Key.Space + Control) must NOT activate (ND10 / N158 — P0-1 regression)
    public void C201_CtrlSpace_DoesNotActivate()
    {
        // ND10: Key.Space is emitted ONLY for NUL→Ctrl+Space, and VtInputInterpreter ALWAYS stamps it
        // with KeyModifiers.Control. The activation gesture is the modifier-free (Character, " ") form
        // (N158: (Character, " ", None)). A modified-Space chord — Ctrl+Space being the only real wire
        // that produces Key.Space at all — must never click the button. This was the P0-1 defect:
        // the unguarded `e.Key == Key.Space` clause activated on Ctrl+Space.
        var button = new Button { Width = 10, Height = 1 };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        using var host = Show(button);
        button.Focus();

        // (a) The exact CtrlSpace() wire shape (Key.Space + Control) does not activate or latch.
        host.SendInput(new KeyEvent
        {
            Key = Key.Space,
            Modifiers = KeyModifiers.Control,
            Kind = KeyEventKind.Down,
            Timestamp = DateTimeOffset.UnixEpoch
        });
        host.RunFrame();
        Assert.Equal(0, clicks);         // Ctrl+Space did NOT click (P0-1)
        Assert.False(button.IsPressed);  // and did NOT latch :pressed

        // (b) A modified character-form Space (Alt+Space) is likewise inert — the modifier guard
        //     covers the character clause too, aligning with N158's (Character, " ", None) oracle.
        host.SendKey(Key.Character, modifiers: KeyModifiers.Alt, text: " ");
        host.RunFrame();
        Assert.Equal(0, clicks);
        Assert.False(button.IsPressed);

        // (c) Sanity: the modifier-free spacebar still activates (the guard didn't over-reach).
        host.SendText(" ");
        host.RunFrame();
        Assert.Equal(1, clicks);
        Assert.True(button.IsPressed);
    }

    /// <summary>A focusable leaf that handles no keys — Enter bubbles past it (the C198 fixture).</summary>
    private sealed class FocusableLeaf : UIElement
    {
        public FocusableLeaf() => Focusable = true;
        protected override Size MeasureOverride(Size availableSize) => new(Width ?? 0, Height ?? 0);
        protected override Size ArrangeOverride(Size finalSize) => finalSize;
    }
}
