using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Input matrix §12 — <see cref="AccessKeyManager"/> core: the capability gate, Alt brackets,
/// chord-flash, registry, scopes, cycling, and the menu-mode entries (N166–N189). AltHeld-mode
/// rows use the default KittyCaps; legacy rows <see cref="TestCapabilities.Ansi16Legacy"/>.
/// </summary>
public class Section12_AccessKeys
{
    private static (UITestHost Host, List<string> Log, CanvasProbe Root, AccessKeyManager Ak, InputDispatcher Dispatcher)
        CreateHost(TerminalCapabilities? capabilities = null)
    {
        var host = UITestHost.Create(capabilities is null ? null : new UITestHostOptions { Capabilities = capabilities });
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        host.ShowRoot(root);
        log.Clear();
        return (host, log, root, host.Application.AccessKeys, host.Application.InputDispatcher);
    }

    private static TerminalCapabilities Caps(bool upDown, bool repeats, bool win32)
        => TestCapabilities.KittyTruecolor with
        {
            Input = TestCapabilities.KittyTruecolor.Input with
            {
                Keyboard = TestCapabilities.KittyTruecolor.Input.Keyboard with
                {
                    DistinguishesKeyUpDown = upDown,
                    ReportsRepeats = repeats,
                },
                Protocol = TestCapabilities.KittyTruecolor.Input.Protocol with { Win32InputMode = win32 },
            },
        };

    private static KeyEvent KeyEvt(
        Key key,
        KeyModifiers modifiers = KeyModifiers.None,
        string? text = null,
        KeyEventKind kind = KeyEventKind.Down,
        bool synthesized = false)
        => new()
        {
            Key = key,
            Modifiers = modifiers,
            Kind = kind,
            Text = (text ?? string.Empty).AsMemory(),
            Synthesized = synthesized,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private static void AltDown(InputDispatcher dispatcher, Key side = Key.LeftAlt)
        => dispatcher.ProcessEvent(KeyEvt(side, KeyModifiers.Alt));

    private static void AltUp(InputDispatcher dispatcher, Key side = Key.LeftAlt)
        => dispatcher.ProcessEvent(KeyEvt(side, kind: KeyEventKind.Up));

    private static bool HasCue(Probe element)
        => (element.InteractionStateInternal & InteractionState.AccessKeyCue) != 0;

    [Theory]
    [InlineData(true, true, false, AccessKeyMode.AltHeld)]
    [InlineData(true, false, false, AccessKeyMode.AlwaysVisible)]
    [InlineData(false, false, false, AccessKeyMode.AlwaysVisible)]
    [InlineData(false, false, true, AccessKeyMode.AltHeld)]
    [InlineData(true, true, true, AccessKeyMode.AltHeld)]
    public void N166_GateTruthTable(bool upDown, bool repeats, bool win32, AccessKeyMode expected)
    {
        var (host, _, _, ak, _) = CreateHost();
        using var _host = host;

        ak.OnCapabilitiesChanged(Caps(upDown, repeats, win32));

        Assert.Equal(expected, ak.Mode);
    }

    [Fact]
    public void N167_GateReadsUndecoratedSnapshot()
    {
        // Host level: the fan-out passes the NEGOTIATED session snapshot — a LegacyCaps host is
        // AlwaysVisible regardless of what a pipeline decorator might claim.
        var (host, _, _, ak, _) = CreateHost(TestCapabilities.Ansi16Legacy);
        using var _host = host;
        Assert.Equal(AccessKeyMode.AlwaysVisible, ak.Mode);

        // Unit level: the sourcing is what protects the gate — a decorator-shaped snapshot
        // (DistinguishesKeyUpDown + ReportsRepeats claimed) WOULD qualify, so it must never be
        // what S6 passes (ND23: dispatcher/ak fan-out reads session capabilities only).
        ak.OnCapabilitiesChanged(Caps(upDown: true, repeats: true, win32: false));
        Assert.Equal(AccessKeyMode.AltHeld, ak.Mode);

        ak.OnCapabilitiesChanged(host.Application.Capabilities); // the negotiated snapshot
        Assert.Equal(AccessKeyMode.AlwaysVisible, ak.Mode);
    }

    [Fact]
    public void N168_Renegotiation_UnconditionalClears_NoStaleBracket()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var target = new AkTarget("T", root.Log);
        root.AddChild(target);
        ak.Register('f', target);

        AltDown(dispatcher); // cue on, side bit set
        Assert.True(ak.IsCueActive);

        ak.OnCapabilitiesChanged(TestCapabilities.KittyTruecolor); // renegotiation, same caps

        Assert.Equal(AccessKeyMode.AltHeld, ak.Mode);
        Assert.False(ak.IsCueActive);
        Assert.False(HasCue(root));
        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.BracketUnobservedInternal);

        // The side bit is gone: an unmodified key no longer enters the activation path.
        dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "f"));
        Assert.Empty(target.Activations);
    }

    [Fact]
    public void N169_ModeFlip_AlwaysVisibleSetsCuePermanently_ReverseClears()
    {
        var (host, _, root, ak, _) = CreateHost();
        using var _host = host;

        ak.OnCapabilitiesChanged(TestCapabilities.Ansi16Legacy);

        Assert.Equal(AccessKeyMode.AlwaysVisible, ak.Mode);
        Assert.True(ak.IsCueActive);
        Assert.True(HasCue(root)); // permanently visible (requirement 6 fallback)

        ak.OnCapabilitiesChanged(TestCapabilities.KittyTruecolor);

        Assert.Equal(AccessKeyMode.AltHeld, ak.Mode);
        Assert.False(ak.IsCueActive); // pending real Alt
        Assert.False(HasCue(root));
    }

    [Fact]
    public void N170_AltDown_CueOn_ActiveScopeAndWindowRoot_OneBatch()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var sink = new StateSink();
        host.Application.InteractionStateObserver = sink;

        AltDown(dispatcher);

        Assert.True(ak.IsCueActive);
        Assert.True(HasCue(root)); // active scope root and window root — both Root at P2
        var notification = Assert.Single(sink.Notifications, n => ReferenceEquals(n.Element, root));
        Assert.True((notification.NewState & InteractionState.AccessKeyCue) != 0); // one batch — one notification
    }

    [Fact]
    public void N171_AltTap_StickyCue_EnterMenuModeOnce()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var menuEntries = 0;
        ak.EnterMenuMode += () => menuEntries++;

        AltDown(dispatcher);
        AltUp(dispatcher); // no intervening key — a tap

        Assert.True(ak.IsCueActive); // sticky
        Assert.True(HasCue(root));
        Assert.True(ak.StickyCueInternal);
        Assert.Equal(1, menuEntries);
    }

    [Fact]
    public void N172_ChordedBracket_CueOffAtUp_NoSticky_NoMenuMode()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var menuEntries = 0;
        ak.EnterMenuMode += () => menuEntries++;

        AltDown(dispatcher);
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f")); // the chord
        AltUp(dispatcher);

        Assert.False(ak.IsCueActive);
        Assert.False(HasCue(root));
        Assert.False(ak.StickyCueInternal);
        Assert.Equal(0, menuEntries);
    }

    [Fact]
    public void N173_BothAltsHeld_CueStaysUntilBothUp()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;

        AltDown(dispatcher);
        AltDown(dispatcher, Key.RightAlt);
        AltUp(dispatcher); // release one side only

        Assert.True(ak.IsCueActive);
        Assert.True(HasCue(root));
    }

    [Fact]
    public void N174_StaleBracketInference_SideBitsCleared_KeyDispatchesNormally()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;

        AltDown(dispatcher); // bit set, cue on
        log.Clear();

        dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "x")); // no Alt bit — the Up was lost

        Assert.False(ak.IsCueActive); // cue off (no sticky)
        Assert.False(HasCue(root));
        Assert.Contains("Root.OnKeyDown", log); // the key then dispatches normally
    }

    [Fact]
    public void N175_SynthesizedAlt_BracketUntouched_RoutingStillOccurs()
    {
        var (host, log, _, ak, dispatcher) = CreateHost();
        using var _host = host;

        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt, synthesized: true));
        Assert.False(ak.IsCueActive); // pre-stage skipped — no cue
        Assert.Contains("Root.OnKeyDown", log); // …but the event still routed

        log.Clear();
        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, kind: KeyEventKind.Up, synthesized: true));
        Assert.False(ak.StickyCueInternal); // no tap inferred from synthetic brackets
        Assert.Contains("Root.OnKeyUp", log);
    }

    [Fact]
    public void N176_TerminalFocusOut_ClearsSideBitsStickyCue_Unconditionally()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var target = new AkTarget("T", root.Log);
        root.AddChild(target);
        ak.Register('f', target);

        AltDown(dispatcher);
        AltUp(dispatcher);   // tap — sticky
        AltDown(dispatcher); // …and held again
        Assert.True(ak.StickyCueInternal);
        Assert.True(ak.IsCueActive);

        dispatcher.ProcessEvent(new FocusEvent { HasFocus = false, Timestamp = DateTimeOffset.UnixEpoch });

        Assert.False(ak.IsCueActive);
        Assert.False(HasCue(root));
        Assert.False(ak.StickyCueInternal);

        // Side bits gone too: an unmodified key no longer activates.
        dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "f"));
        Assert.Empty(target.Activations);
    }

    [Fact]
    public void N177_StickyCue_EscConsumedInPreStage_FocusedElementNeverSeesIt()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var b = new Btn("B", log);
        root.Place(b, 5, 5);
        host.RunFrame();
        Assert.True(b.Focus());
        b.AddHandler(UIElement.KeyDownEvent, (_, e) => e.Handled = true); // would handle Esc in step 4

        AltDown(dispatcher);
        AltUp(dispatcher); // sticky
        log.Clear();

        var result = dispatcher.ProcessEvent(KeyEvt(Key.Escape));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.IsCueActive);
        Assert.DoesNotContain("B.OnPreviewKeyDown", log); // consumed — never routed (menu mode is modal to Esc)
        Assert.DoesNotContain("B.OnKeyDown", log);
    }

    [Fact]
    public void N178_SecondAltTap_TogglesStickyAndCueOff()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;

        AltDown(dispatcher);
        AltUp(dispatcher); // tap 1 — sticky on
        AltDown(dispatcher);
        AltUp(dispatcher); // tap 2 — toggle off

        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.IsCueActive);
        Assert.False(HasCue(root));
    }

    [Fact]
    public void N179_PointerDrivenFocusChange_ClearsSticky()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var b = new Btn("B", log);
        root.Place(b, 5, 5);
        host.RunFrame();

        AltDown(dispatcher);
        AltUp(dispatcher); // sticky
        Assert.True(ak.StickyCueInternal);

        Assert.True(b.Focus(FocusNavigationMethod.Pointer)); // the mouse-down path's focus call

        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.IsCueActive);
    }

    [Fact]
    public void N180_Registry_CaseFolded_UnregisterOne_DetachBackstop()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var t1 = new AkTarget("T1", log);
        var t2 = new AkTarget("T2", log);
        root.Place(t1, 5, 5);
        root.Place(t2, 15, 5);
        host.RunFrame();
        ak.Register('f', t1);
        ak.Register('F', t2); // case-folded — same bucket

        AltDown(dispatcher); // hold Alt: clean activation window, no chord-flash noise
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        // Both registered under one key ⇒ multi-match cycling (focus-only).
        Assert.Single(t1.Activations.Concat(t2.Activations));
        Assert.True(t1.Activations.Concat(t2.Activations).All(static a => a.IsMultiMatch));

        ak.Unregister('F', t2);
        t1.Activations.Clear();
        t2.Activations.Clear();

        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));
        Assert.Equal([('f', false)], t1.Activations); // unique now — invokes
        Assert.Empty(t2.Activations);

        root.RemoveChild(t1); // detach backstop unregisters the other
        t1.Activations.Clear();

        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));
        Assert.Empty(t1.Activations);
    }

    [Fact]
    public void N181_LegacyChordActivation_NoBracketEverObserved()
    {
        var (host, log, root, ak, dispatcher) = CreateHost(TestCapabilities.Ansi16Legacy);
        using var _host = host;
        var target = new AkTarget("T", log);
        root.Place(target, 5, 5);
        host.RunFrame();
        ak.Register('f', target);

        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal([('f', false)], target.Activations);
    }

    [Fact]
    public void N182_AltHeld_UnmodifiedKey_Activates()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var target = new AkTarget("T", log);
        root.Place(target, 5, 5);
        host.RunFrame();
        ak.Register('f', target);

        AltDown(dispatcher);
        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "f")); // unmodified while held

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal([('f', false)], target.Activations);
    }

    [Fact]
    public void N183_FocusedTextControlKeepsPlainKeys_AltChordStillReachesManager()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var textBox = new Btn("TB", log); // the TextBox stand-in
        root.Place(textBox, 5, 5);
        var target = new AkTarget("T", log);
        root.Place(target, 15, 5);
        host.RunFrame();
        Assert.True(textBox.Focus());
        textBox.AddHandler(UIElement.KeyDownEvent, (_, e) =>
        {
            if (e is { Key: Key.Character, Modifiers: KeyModifiers.None })
                e.Handled = true; // text controls claim plain characters, never Alt-modified keys
        });
        ak.Register('f', target);

        dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "f")); // plain f — handled in step 4
        Assert.Empty(target.Activations);                          // access keys never consulted

        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f")); // Alt+F — the unhandled tail
        Assert.Equal([('f', false)], target.Activations);                      // coexistence
    }

    [Fact]
    public void N184_MultiMatch_FocusOnlyCycling_WrapAround_CueStays()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var t1 = new AkTarget("T1", log);
        var t2 = new AkTarget("T2", log);
        root.Place(t1, 5, 5);
        root.Place(t2, 15, 5);
        host.RunFrame();
        ak.Register('f', t1);
        ak.Register('f', t2);
        Assert.True(t1.Focus());

        AltDown(dispatcher);
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Same(t2, host.Application.FocusManager.FocusedElement); // first match after the focused one
        Assert.Equal([('f', true)], t2.Activations);                   // focus-only — never invokes
        Assert.Empty(t1.Activations);
        Assert.True((t2.InteractionStateInternal & InteractionState.FocusVisible) != 0); // method = AccessKey

        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Same(t1, host.Application.FocusManager.FocusedElement); // wraps
        Assert.Equal([('f', true)], t1.Activations);
        Assert.True(ak.IsCueActive); // the cue stays up for cycling
    }

    [Fact]
    public void N184b_SingleMatch_FocusableTarget_MovesFocusThenInvokes()
    {
        // A single (non-colliding) access key both INVOKES the target and moves focus to it when the
        // target is focusable — parity with multi-match cycling, the plain-element fallback, and
        // WPF/Avalonia. The manager owns the focus move (the fixture target only records OnAccessKey).
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var other = new Btn("Other", log);
        var target = new AkTarget("T", log);
        root.Place(other, 2, 2);
        root.Place(target, 15, 5);
        host.RunFrame();
        Assert.True(other.Focus()); // focus starts elsewhere, so a move is observable
        ak.Register('f', target);

        AltDown(dispatcher);
        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal([('f', false)], target.Activations);                       // invoked (single-match)
        Assert.Same(target, host.Application.FocusManager.FocusedElement);      // …and focus moved to it
        Assert.True((target.InteractionStateInternal & InteractionState.FocusVisible) != 0); // method = AccessKey
    }

    [Fact]
    public void N184c_SingleMatch_NonFocusableTarget_InvokesWithoutGrabbingFocus()
    {
        // The Focusable guard: a non-focusable target (e.g. a Label, which forwards focus to its own
        // Target inside OnAccessKey) is invoked but the manager does NOT pull focus onto it.
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var other = new Btn("Other", log);
        var target = new AkTarget("T", log) { Focusable = false };
        root.Place(other, 2, 2);
        root.Place(target, 15, 5);
        host.RunFrame();
        Assert.True(other.Focus());
        ak.Register('f', target);

        AltDown(dispatcher);
        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal([('f', false)], target.Activations);                  // still invoked
        Assert.Same(other, host.Application.FocusManager.FocusedElement);  // focus stayed put
    }

    public enum IneligibleKind
    {
        Disabled,
        Detached,
        Collapsed,
    }

    [Theory]
    [InlineData(IneligibleKind.Disabled)]
    [InlineData(IneligibleKind.Detached)]
    [InlineData(IneligibleKind.Collapsed)]
    public void N185_IneligibleTargets_Excluded(IneligibleKind kind)
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var target = new AkTarget("T", log);
        root.Place(target, 5, 5);
        host.RunFrame();
        ak.Register('f', target);

        switch (kind)
        {
            case IneligibleKind.Disabled:
                target.IsEnabled = false;
                break;
            case IneligibleKind.Detached:
                root.RemoveChild(target);
                break;
            case IneligibleKind.Collapsed:
                target.Visibility = Visibility.Collapsed;
                break;
        }

        AltDown(dispatcher);
        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "f"));

        Assert.Empty(target.Activations); // excluded; 0 eligible falls through (no sticky ⇒ unhandled tail)
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
    }

    [Fact]
    public void N186_StickyCue_UnmatchedKeySwallowed_NoTextInput()
    {
        var (host, log, _, ak, dispatcher) = CreateHost();
        using var _host = host;

        AltDown(dispatcher);
        AltUp(dispatcher); // sticky cue up
        log.Clear();

        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "x")); // no match for 'x'

        Assert.Equal(InputDispatchResult.DispatchedHandled, result); // swallowed (WPF bonk)
        Assert.True(ak.IsCueActive);                                 // cue stays
        Assert.DoesNotContain(log, static e => e.Contains("TextInput"));

        // Without sticky: unhandled, TextInput proceeds.
        AltDown(dispatcher);
        AltUp(dispatcher); // second tap clears sticky
        log.Clear();

        result = dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "x"));

        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.Contains("Root.OnTextInput", log);
    }

    [Fact]
    public void N187_ChordFlashSelfCorrection_LatchClearedByRealAlt()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var target = new AkTarget("T", log);
        root.Place(target, 5, 5);
        host.RunFrame();
        ak.Register('f', target);

        // AltHeld mode, but no bracket has ever been observed — the chord presumes it missing.
        var result = dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.True(ak.BracketUnobservedInternal);        // latched
        Assert.Equal([('f', false)], target.Activations); // single match still activates
        Assert.False(ak.IsCueActive);                     // …and clears per activation rules (no physical Alt)

        AltDown(dispatcher); // a later real Alt Down clears the latch

        Assert.False(ak.BracketUnobservedInternal);
    }

    [Fact]
    public void N188_ScopeStack_ActivationTimeResolution()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var pane = new CanvasProbe("Pane", log, 40, 10);
        root.Place(pane, 0, 0);
        var t1 = new AkTarget("T1", log);
        pane.Place(t1, 2, 2); // inside Pane
        var t2 = new AkTarget("T2", log);
        root.Place(t2, 50, 5); // outside, under Root
        host.RunFrame();
        ak.Register('f', t1);
        ak.Register('f', t2);

        AltDown(dispatcher); // a real bracket — no chord-flash noise on the chords below
        ak.PushScope(pane);
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Equal([('f', false)], t1.Activations); // only t1 resolves to the live stack top
        Assert.Empty(t2.Activations);

        ak.PopScope(pane);
        ak.Unregister('f', t1); // isolate the second leg
        t1.Activations.Clear();

        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));

        Assert.Equal([('f', false)], t2.Activations); // t2 matches again — no registration-time capture
        Assert.Empty(t1.Activations);
    }

    [Fact]
    public void N189_F10_StickyCueAndMenuMode_FocusedHandlerWins()
    {
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var menuEntries = 0;
        ak.EnterMenuMode += () => menuEntries++;

        var result = dispatcher.ProcessEvent(KeyEvt(Key.F10));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal(1, menuEntries);
        Assert.True(ak.IsCueActive);
        Assert.True(ak.StickyCueInternal);

        // A focused element handling F10 in step 4 wins — the tail never runs.
        root.AddHandler(UIElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F10)
                e.Handled = true;
        });

        result = dispatcher.ProcessEvent(KeyEvt(Key.F10));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal(1, menuEntries); // not raised again
    }

    [Fact]
    public void N212_AltRepeat_NeverReArmsTheTap_ND32()
    {
        var (host, log, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var target = new AkTarget("T", log);
        root.AddChild(target);
        ak.Register('f', target);

        var menuEntries = 0;
        ak.EnterMenuMode += () => menuEntries++;

        // ⓐ Alt held → Alt+F chord (activates) → Alt REPEAT Down → Alt Up: stays chorded — a
        // repeat never re-arms the chordless flag (ND32); no sticky cue, no EnterMenuMode.
        AltDown(dispatcher);
        dispatcher.ProcessEvent(KeyEvt(Key.Character, KeyModifiers.Alt, "f"));
        Assert.Single(target.Activations);

        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt) with { IsRepeat = true });
        AltUp(dispatcher);

        Assert.Equal(0, menuEntries);
        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.IsCueActive);

        // ⓑ a genuine chordless tap with intervening repeats is still a tap: sticky cue +
        // EnterMenuMode exactly once (repeats don't break the bracket).
        AltDown(dispatcher);
        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt) with { IsRepeat = true });
        AltUp(dispatcher);

        Assert.Equal(1, menuEntries);
        Assert.True(ak.StickyCueInternal);
        Assert.True(ak.IsCueActive);
    }
}
