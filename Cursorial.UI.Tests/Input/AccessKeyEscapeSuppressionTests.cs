using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Terminal;
using Cursorial.Tests.UI.InputMatrix;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.Input;

/// <summary>
/// The post-Escape access-key suppression window (<see cref="AccessKeyManager.EscapeSuppressionWindow"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug these rows exist for.</b> A lone <c>ESC</c> byte is ambiguous on the wire — Escape, or
/// the prefix of an Alt chord (<c>ESC 'o'</c> ≡ <c>Alt+O</c>) — and S6's pump resolves it with a
/// 50 ms timing race (<see cref="VtInputDevice.DefaultEscapeAmbiguityTimeout"/>). A file dialog whose
/// Esc moves focus from the breadcrumb to a type-ahead file list therefore had a destructive failure
/// mode: press Esc, immediately type a letter, and the letter can arrive wearing an Alt bit it was
/// never typed with — <c>Alt+O</c> ≡ <i>Open</i>, committing the dialog on a keystroke the user
/// meant as type-ahead.
/// </para>
/// <para>
/// The guard is deliberately narrow, and these rows pin every edge of it: only <b>activation</b>, only
/// an <b>unbracketed</b> Alt chord (no real Alt Down observed), only inside the window, and never the
/// cue. Headless throughout, driving the real <see cref="InputDispatcher"/>; the window is stepped
/// with the host's <c>FakeTimeProvider</c>, which is the same clock domain the manager samples — no
/// wall-clock sleeps, no flake.
/// </para>
/// </remarks>
public sealed class AccessKeyEscapeSuppressionTests
{
    // The legacy (Ansi16) terminal is where the hazard actually lives: no Alt key events on the wire,
    // so ESC framing is the ONLY way an Alt chord can arrive — and the cue is AlwaysVisible, which
    // keeps the cue assertions independent of any Alt-bracket bookkeeping.
    private static (UIHeadlessHost Host, List<string> Log, CanvasProbe Root, AccessKeyManager Ak, InputDispatcher Dispatcher)
        CreateHost(TerminalCapabilities? capabilities = null)
    {
        var host = UIHeadlessHost.Create(capabilities is null ? null : new UIHeadlessHostOptions { Capabilities = capabilities });
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        host.ShowRoot(root);
        log.Clear();
        return (host, log, root, host.Application.AccessKeys, host.Application.InputDispatcher);
    }

    private static KeyEvent KeyEvt(Key key, KeyModifiers modifiers = KeyModifiers.None, string? text = null, KeyEventKind kind = KeyEventKind.Down)
        => new()
        {
            Key = key,
            Modifiers = modifiers,
            Kind = kind,
            Text = (text ?? string.Empty).AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch // deliberately constant: the guard must NOT key on producer timestamps
        };

    private static KeyEvent Escape() => KeyEvt(Key.Escape);

    private static KeyEvent AltO() => KeyEvt(Key.Character, KeyModifiers.Alt, "o");

    private static AkTarget RegisterOpen(CanvasProbe root, AccessKeyManager ak)
    {
        var target = new AkTarget("Open", root.Log);
        root.AddChild(target);
        ak.Register('o', target);
        return target;
    }

    private static bool HasCue(Probe element)
        => (element.InteractionStateInternal & InteractionState.AccessKeyCue) != 0;

    [Fact]
    public void Default_IsTheEscapeAmbiguityTimeout()
    {
        // The two constants are the same physical quantity from opposite ends of the pipe; they must
        // not drift apart silently.
        Assert.Equal(VtInputDevice.DefaultEscapeAmbiguityTimeout, AccessKeyManager.DefaultEscapeSuppressionWindow);

        var (host, _, _, ak, _) = CreateHost();
        using var _host = host;
        Assert.Equal(AccessKeyManager.DefaultEscapeSuppressionWindow, ak.EscapeSuppressionWindow);
    }

    [Fact]
    public void EscThenAltLetter_InsideWindow_DoesNotActivate()
    {
        var (host, log, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(Escape());
        Assert.True(ak.EscapeSuppressionActiveInternal);

        host.Time.Advance(TimeSpan.FromMilliseconds(10)); // the user's very next keystroke, mid-race
        log.Clear();

        var result = dispatcher.ProcessEvent(AltO());

        Assert.Empty(open.Activations);                                  // "Open" did NOT fire — the dialog survives
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.Contains("Root.OnKeyDown", log);                          // …but the key still ROUTED: only activation is suppressed
    }

    [Fact]
    public void EscThenAltLetter_AfterWindow_Activates()
    {
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(Escape());
        host.Time.Advance(AccessKeyManager.DefaultEscapeSuppressionWindow + TimeSpan.FromMilliseconds(1));

        Assert.False(ak.EscapeSuppressionActiveInternal); // the arm self-disarms on the first read past the window

        var result = dispatcher.ProcessEvent(AltO());

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Equal(('o', false), Assert.Single(open.Activations));
    }

    [Fact]
    public void EscThenAltLetter_ExactlyAtWindowEdge_Activates()
    {
        // The boundary is exclusive: elapsed == window is already OUTSIDE (the pump has committed
        // the Escape by then, so nothing is left to be re-framed).
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(Escape());
        host.Time.Advance(ak.EscapeSuppressionWindow);
        dispatcher.ProcessEvent(AltO());

        Assert.Single(open.Activations);
    }

    [Fact]
    public void AltLetter_WithNoPrecedingEscape_AlwaysActivates()
    {
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        // A single press with a frozen clock — the shape that would break if the guard were armed by
        // anything other than a real Escape.
        dispatcher.ProcessEvent(AltO());
        Assert.Single(open.Activations);

        // …and repeatedly, with no clock movement at all.
        dispatcher.ProcessEvent(AltO());
        Assert.Equal(2, open.Activations.Count);
    }

    [Fact]
    public void ModifiedEscape_DoesNotArm()
    {
        // Only a BARE Escape is the ambiguous ESC-framing shape; Alt+Esc / Ctrl+Esc arrive fully framed.
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(KeyEvt(Key.Escape, KeyModifiers.Control));

        Assert.False(ak.EscapeSuppressionActiveInternal);
        dispatcher.ProcessEvent(AltO());
        Assert.Single(open.Activations);
    }

    [Fact]
    public void BracketedAltChord_InsideWindow_StillActivates()
    {
        // A chord corroborated by a REAL Alt Down cannot be an ESC-framing artifact — the terminal
        // told us the Alt key is physically down — so it activates even inside the window.
        var (host, _, root, ak, dispatcher) = CreateHost(); // Kitty: Alt brackets on the wire
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(Escape());
        Assert.True(ak.EscapeSuppressionActiveInternal);

        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt)); // the bracket opens
        dispatcher.ProcessEvent(AltO());

        Assert.Single(open.Activations);
    }

    [Fact]
    public void MenuModeEscape_IsConsumed_AndNeverArms()
    {
        // Esc inside menu mode is consumed by the pre-stage (N177) and never reaches the tree, so it
        // must not arm the guard: the user is deliberately inside the access-key surface and their
        // next letter is intentional.
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt));
        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, kind: KeyEventKind.Up)); // Alt tap → sticky menu mode
        Assert.True(ak.StickyCueInternal);

        Assert.Equal(InputDispatchResult.DispatchedHandled, dispatcher.ProcessEvent(Escape())); // consumed
        Assert.False(ak.EscapeSuppressionActiveInternal);

        dispatcher.ProcessEvent(KeyEvt(Key.F10));                   // back into menu mode…
        dispatcher.ProcessEvent(KeyEvt(Key.Character, text: "o"));  // …an UNMODIFIED letter still activates

        Assert.Single(open.Activations);
    }

    [Fact]
    public void Suppression_LeavesTheCueUntouched()
    {
        // AlwaysVisible mode: the cue is permanently stamped and a suppressed chord must not disturb
        // it — not the roots, not the sticky flag, and not the chord-flash latch (which would
        // otherwise read the untrusted chord as proof the terminal never delivers Alt brackets).
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        Assert.True(ak.IsCueActive);
        Assert.True(HasCue(root));

        dispatcher.ProcessEvent(Escape());
        host.Time.Advance(TimeSpan.FromMilliseconds(10));
        dispatcher.ProcessEvent(AltO());

        Assert.Empty(open.Activations);
        Assert.True(ak.IsCueActive);
        Assert.True(HasCue(root));
        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.BracketUnobservedInternal);
    }

    [Fact]
    public void CueMachineryStillRuns_InsideTheWindow()
    {
        // AltHeld mode: the cue's Alt-held state machine (stamp on Down, sticky on a chordless tap)
        // is completely unaffected by an armed window — only activation is gated.
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;

        dispatcher.ProcessEvent(Escape());
        Assert.True(ak.EscapeSuppressionActiveInternal);
        Assert.False(ak.IsCueActive);

        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, KeyModifiers.Alt));
        Assert.True(ak.IsCueActive);
        Assert.True(HasCue(root)); // the cue displays normally mid-window

        dispatcher.ProcessEvent(KeyEvt(Key.LeftAlt, kind: KeyEventKind.Up));
        Assert.True(ak.StickyCueInternal); // …and the Alt tap still enters menu mode
        Assert.True(ak.IsCueActive);
        Assert.True(ak.EscapeSuppressionActiveInternal); // the window is untouched by cue traffic
    }

    [Fact]
    public void ChordFlashSelfCorrection_IsNotTrippedByASuppressedChord()
    {
        // AltHeld mode with no bracket ever observed: an unbracketed chord normally latches the cue ON
        // and STICKY (N187). A SUPPRESSED chord must not — latching sticky here would turn every
        // following plain letter into an activation, amplifying the very hazard we are guarding.
        var (host, _, root, ak, dispatcher) = CreateHost();
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(Escape());
        dispatcher.ProcessEvent(AltO());

        Assert.Empty(open.Activations);
        Assert.False(ak.BracketUnobservedInternal);
        Assert.False(ak.StickyCueInternal);
        Assert.False(ak.IsCueActive);

        // Past the window the same chord behaves exactly as it always did: flash + activate.
        host.Time.Advance(AccessKeyManager.DefaultEscapeSuppressionWindow);
        dispatcher.ProcessEvent(AltO());

        Assert.Single(open.Activations);
        Assert.True(ak.BracketUnobservedInternal);
    }

    [Fact]
    public void TerminalFocusOut_DisarmsTheWindow()
    {
        // The next key belongs to a fresh focus session — there is no in-flight ESC framing to distrust.
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        dispatcher.ProcessEvent(Escape());
        dispatcher.ProcessEvent(new FocusEvent { HasFocus = false, Timestamp = DateTimeOffset.UnixEpoch });

        Assert.False(ak.EscapeSuppressionActiveInternal);
        dispatcher.ProcessEvent(AltO());
        Assert.Single(open.Activations);
    }

    [Fact]
    public void WindowIsAdjustable_AndZeroDisablesTheGuard()
    {
        var (host, _, root, ak, dispatcher) = CreateHost(HeadlessCapabilities.Ansi16Legacy);
        using var _host = host;
        var open = RegisterOpen(root, ak);

        ak.EscapeSuppressionWindow = TimeSpan.FromMilliseconds(200);
        dispatcher.ProcessEvent(Escape());
        host.Time.Advance(TimeSpan.FromMilliseconds(150)); // past the default, inside the widened window
        dispatcher.ProcessEvent(AltO());
        Assert.Empty(open.Activations);

        ak.EscapeSuppressionWindow = TimeSpan.Zero; // opt out entirely
        dispatcher.ProcessEvent(Escape());
        dispatcher.ProcessEvent(AltO());
        Assert.Single(open.Activations);

        Assert.Throws<ArgumentOutOfRangeException>(() => ak.EscapeSuppressionWindow = TimeSpan.FromMilliseconds(-1));
    }
}
