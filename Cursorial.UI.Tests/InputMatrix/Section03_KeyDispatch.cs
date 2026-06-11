using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>
/// Input matrix §3 — key dispatch order and TextInput synthesis (N29–N47). N48 (access-key-tail
/// precedence) opens with the I4 stage that introduces that tail.
/// </summary>
public class Section03_KeyDispatch
{
    [Fact]
    public void N029_FullKeyDispatchOrder_TunnelRootToTarget_BubbleTargetToRoot()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Root.AddLog(UIElement.PreviewKeyDownEvent, "Root.p1");
        fixture.A.AddLog(UIElement.PreviewKeyDownEvent, "A.p1");
        fixture.B.AddLog(UIElement.PreviewKeyDownEvent, "B.p1");
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1");
        fixture.A.AddLog(UIElement.KeyDownEvent, "A.h1");
        fixture.Root.AddLog(UIElement.KeyDownEvent, "Root.h1");

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.Equal(
            [
                "Root.OnPreviewKeyDown", "Root.p1",
                "A.OnPreviewKeyDown", "A.p1",
                "B.OnPreviewKeyDown", "B.p1",
                "B.OnKeyDown", "B.h1",
                "A.OnKeyDown", "A.h1",
                "Root.OnKeyDown", "Root.h1",
            ],
            fixture.Log);
    }

    [Fact]
    public void N030_NoFocus_ActiveRootFallback_SingleNodeRoute()
    {
        using var fixture = InputHost.CreateChain();
        fixture.Host.Application.FocusManager.ClearFocus(); // activation auto-focused B (N115) — this row wants none
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.Equal(["Root.OnPreviewKeyDown", "Root.OnKeyDown"], fixture.Log);
    }

    [Fact]
    public void N031_CharacterKeys_TextCarriesPerDispatchIdentity()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        var texts = new List<string>();
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e => texts.Add(e.Text.ToString()));

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "a"));
        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "b"));

        Assert.Equal(["a", "b"], texts); // (Key, Text) identity observable at the handler
    }

    [Fact]
    public void N032_RepeatMetadata_PassesThrough()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        (bool IsRepeat, int RepeatCount)? seen = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e => seen = (e.IsRepeat, e.RepeatCount));

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "a", isRepeat: true, repeatCount: 3));

        Assert.Equal((true, 3), seen);
    }

    [Fact]
    public void N033_KeyUp_RoutesPair_NoTailProcessing()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter, kind: KeyEventKind.Up));

        Assert.Equal(
            [
                "Root.OnPreviewKeyUp", "A.OnPreviewKeyUp", "B.OnPreviewKeyUp",
                "B.OnKeyUp", "A.OnKeyUp", "Root.OnKeyUp",
            ],
            fixture.Log); // ND9: no tails of any kind — nothing else in the log
    }

    [Fact]
    public void N034_UnhandledPrintable_SynthesizesTextInputPair()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        (string Text, bool FromPaste)? seen = null;
        fixture.B.AddLog(UIElement.TextInputEvent, "B.t1", action: e => seen = (e.Text.ToString(), e.FromPaste));

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "f"));

        Assert.Equal(
            [
                "Root.OnPreviewKeyDown", "A.OnPreviewKeyDown", "B.OnPreviewKeyDown",
                "B.OnKeyDown", "A.OnKeyDown", "Root.OnKeyDown",
                "Root.OnPreviewTextInput", "A.OnPreviewTextInput", "B.OnPreviewTextInput",
                "B.OnTextInput", "B.t1", "A.OnTextInput", "Root.OnTextInput",
            ],
            fixture.Log); // synthesis runs after the full KeyDown route
        Assert.Equal(("f", false), seen);
    }

    [Fact]
    public void N035_HandledKeyDown_NoTextInputSynthesized()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: static e => e.Handled = true);
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "f"));

        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("TextInput"));
    }

    [Fact]
    public void N036_ShiftedCharacter_SynthesizesTextInput()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        string? text = null;
        fixture.B.AddLog(UIElement.TextInputEvent, "B.t1", action: e => text = e.Text.ToString());

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Shift, "F"));

        Assert.Equal("F", text); // Shift is allowed through the chord mask
    }

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Super)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Hyper)]
    public void N037_ChordModifiers_SuppressTextInput_KeyDownStillRouted(KeyModifiers modifier)
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, modifier, "f"));

        Assert.Contains("B.OnKeyDown", fixture.Log); // the KeyDown route still ran
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("TextInput"));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    public void N038_NamedKeys_NeverSynthesizeTextInput(Key key)
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(key));

        Assert.Contains("B.OnKeyDown", fixture.Log);
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("TextInput"));
    }

    [Fact]
    public void N039_SpacebarWireBytes_DecodeAsCharacterSpace_TypeASpaceEndToEnd()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        (Key Key, string Text)? keySeen = null;
        string? textSeen = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e => keySeen = (e.Key, e.Text.ToString()));
        fixture.B.AddLog(UIElement.TextInputEvent, "B.t1", action: e => textSeen = e.Text.ToString());

        fixture.Host.SendBytes(" "u8); // raw 0x20 through the real VtInputDevice
#pragma warning disable xUnit1031
        fixture.Host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
#pragma warning restore xUnit1031
        fixture.Host.RunFrame();

        Assert.Equal((Key.Character, " "), keySeen); // ND10: spacebar identity is (Character, " ")
        Assert.Equal(" ", textSeen);                 // spacebar types a space end-to-end
    }

    [Fact]
    public void N040_EmptyText_NoTextInput_KeyDownRouted()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: ""));

        Assert.Contains("B.OnKeyDown", fixture.Log);
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("TextInput"));
    }

    [Fact]
    public void N041_Paste_OneTextInputPairAtFocused_NoKeyDownRoute()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        (string Text, bool FromPaste)? seen = null;
        fixture.B.AddLog(UIElement.TextInputEvent, "B.t1", action: e => seen = (e.Text.ToString(), e.FromPaste));

        fixture.Dispatcher.ProcessEvent(fixture.Paste("hello\nworld"));

        Assert.Equal(("hello\nworld", true), seen);
        Assert.Equal(1, fixture.Log.Count(static entry => entry == "B.OnTextInput")); // one pair
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("KeyDown"));
    }

    [Fact]
    public void N042_PasteWithNoFocus_FallsBackToActiveRoot()
    {
        using var fixture = InputHost.CreateChain();
        fixture.Host.Application.FocusManager.ClearFocus(); // activation auto-focused B (N115) — this row wants none
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Paste("x"));

        Assert.Contains("Root.OnTextInput", fixture.Log);
        Assert.DoesNotContain("B.OnTextInput", fixture.Log);
    }

    [Fact]
    public void N043_HandledTextInput_FeedsTheKeyEventResult()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.B.AddLog(UIElement.TextInputEvent, "B.t1", action: static e => e.Handled = true);

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "f"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
    }

    [Fact]
    public void N044_LockBits_VisibleOnlyOnExtendedModifiers()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        (KeyModifiers Modifiers, KeyModifiers Extended)? seen = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e => seen = (e.Modifiers, e.ExtendedModifiers));

        fixture.Dispatcher.ProcessEvent(
            fixture.Key(Key.Character, KeyModifiers.Control, "s", extendedModifiers: KeyModifiers.CapsLock));

        Assert.NotNull(seen);
        Assert.Equal(KeyModifiers.Control, seen!.Value.Modifiers); // exactly — lock-free
        Assert.True(seen.Value.Extended.HasFlag(KeyModifiers.CapsLock));
        Assert.True(seen.Value.Extended.HasFlag(KeyModifiers.Control)); // superset
    }

    [Fact]
    public void N045_StandaloneAltDown_RoutesNormally()
    {
        // The pre-stage Alt bracket itself is asserted by §12 (I4); this row pins that the Alt
        // key event ALSO routes normally — a handler may care about it.
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.LeftAlt));

        Assert.Contains("B.OnPreviewKeyDown", fixture.Log);
        Assert.Contains("B.OnKeyDown", fixture.Log);
    }

    [Fact]
    public void N046_SynthesizedAlt_RoutingStillOccurs()
    {
        // The pre-stage skips Synthesized key events (no bracket corruption — §12 asserts the
        // bracket side at I4); routing itself is unaffected by the Synthesized flag.
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.LeftAlt, synthesized: true));

        Assert.Contains("B.OnKeyDown", fixture.Log);
    }

    [Fact]
    public void N047_HandledArrow_NavigationTailNeverRuns()
    {
        // TextBox-style: the focused element handles arrows in step 4; the directional tail
        // (step 6) must never see them — handled wins (doc §7.5).
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var container = new CanvasProbe("Grid", log, 40, 20);
        root.Place(container, 0, 0);
        Cursorial.UI.Input.KeyboardNavigation.SetDirectionalNavigation(
            container, Cursorial.UI.Input.DirectionalNavigationMode.Contained);
        var b = new Btn("B", log);
        var below = new Btn("Below", log);
        container.Place(b, 10, 5);
        container.Place(below, 10, 8); // the would-be directional target
        host.ShowRoot(root);
        host.RunFrame();
        Assert.True(b.Focus());
        b.AddHandler(UIElement.KeyDownEvent, (_, e) => e.Handled = true); // arrow-eating control

        var result = host.Application.InputDispatcher.ProcessEvent(new KeyEvent
        {
            Key = Key.DownArrow,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = ReadOnlyMemory<char>.Empty,
            Timestamp = DateTimeOffset.UnixEpoch,
        });

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Same(b, host.Application.FocusManager.FocusedElement); // focus unmoved — tail never ran
        Assert.False(below.IsFocused);
    }

    [Fact]
    public void N048_AccessKeyTail_RunsBeforeNavigationAndTextInput()
    {
        // An active registration for 'f' (Alt held — the cue window is up) claims the unhandled
        // key in step 5, before navigation (step 6) and before TextInput (step 7).
        using var fixture = InputHost.CreateChain();
        var target = new AkTarget("T", fixture.Log);
        fixture.Root.AddChild(target);
        fixture.Host.Application.AccessKeys.Register('f', target);
        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.LeftAlt, modifiers: KeyModifiers.Alt)); // Alt Down — cue up
        fixture.Log.Clear();

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, text: "f"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.Contains("T.OnAccessKey(single)", fixture.Log);                                  // activation occurred
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("TextInput"));        // step 7 never ran
    }
}
