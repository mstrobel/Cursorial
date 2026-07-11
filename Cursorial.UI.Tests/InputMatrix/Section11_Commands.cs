using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

#pragma warning disable xUnit1031

/// <summary>
/// Input matrix §11 — commands, <see cref="KeyGesture"/>, <see cref="KeyBinding"/> (N148–N165).
/// N153–N155 are the wire-truth encoding rows (<c>SendBytes</c> through the real
/// <c>VtInputDevice</c>); everything else injects records directly.
/// </summary>
public class Section11_Commands
{
    private static KeyEventArgs Args(
        Key key,
        string? text = null,
        KeyModifiers modifiers = KeyModifiers.None,
        KeyModifiers? extendedModifiers = null)
    {
        var element = new Probe("X", []);
        return new KeyEventArgs(UIElement.KeyDownEvent, element, new KeyEvent
        {
            Key = key,
            Modifiers = modifiers,
            ExtendedModifiers = extendedModifiers ?? modifiers,
            Kind = KeyEventKind.Down,
            Text = (text ?? string.Empty).AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch
        });
    }

    /// <summary>An <c>ICommand</c> logging executions into the shared ordered log.</summary>
    private sealed class LogCommand(List<string> log, string name, Func<bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => log.Add(name);
    }

    [Fact]
    public void N148_Parse_CtrlS_SingleCharToken_CharacterGesture()
    {
        var gesture = KeyGesture.Parse("Ctrl+S");

        Assert.Equal(Key.Character, gesture.Key);
        Assert.Equal(KeyModifiers.Control, gesture.Modifiers);
        Assert.Equal("S", gesture.Character);
    }

    [Theory]
    [InlineData("F5", Key.F5, KeyModifiers.None)]
    [InlineData("Alt+Enter", Key.Enter, KeyModifiers.Alt)]
    [InlineData("Ctrl+Shift+P", Key.Character, KeyModifiers.Control | KeyModifiers.Shift)]
    public void N149_Parse_NamedKeysAndChords(string text, Key expectedKey, KeyModifiers expectedModifiers)
    {
        var gesture = KeyGesture.Parse(text);

        Assert.Equal(expectedKey, gesture.Key);
        Assert.Equal(expectedModifiers, gesture.Modifiers);
        if (expectedKey == Key.Character)
            Assert.Equal("P", gesture.Character);
    }

    [Theory]
    [InlineData("ctrl+s", KeyModifiers.Control, "S")] // canonicalized upper-invariant (amended ND13)
    [InlineData("Control+S", KeyModifiers.Control, "S")]
    [InlineData("Win+K", KeyModifiers.Super, "K")]
    [InlineData("Cmd+K", KeyModifiers.Super, "K")]
    public void N150_Parse_CaseInsensitive_ModifierAliases(string text, KeyModifiers expectedModifiers, string expectedCharacter)
    {
        var gesture = KeyGesture.Parse(text);

        Assert.Equal(Key.Character, gesture.Key);
        Assert.Equal(expectedModifiers, gesture.Modifiers);
        Assert.Equal(expectedCharacter, gesture.Character);

        // The amendment's point: case variants are the same gesture with a stable ToString.
        Assert.Equal(KeyGesture.Parse(text.ToUpperInvariant()), gesture);
        Assert.Equal(KeyGesture.Parse(text.ToUpperInvariant()).ToString(), gesture.ToString());
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Bogus+X")]
    [InlineData("")]
    public void N151_Parse_Invalid_FormatException(string text)
        => Assert.Throws<FormatException>(() => KeyGesture.Parse(text));

    [Theory] // ToString is the canonical DISPLAY form (the wizard/menus show it) — never the flags-enum dump
    [InlineData("Ctrl+Shift+O", "Ctrl+Shift+O")]
    [InlineData("shift+ctrl+o", "Ctrl+Shift+O")] // modifier order canonicalizes: Ctrl, Alt, Shift, Super, Meta, Hyper
    [InlineData("Ctrl+L", "Ctrl+L")]
    [InlineData("F10", "F10")]
    public void N151a_ToString_CanonicalDisplayForm(string text, string expected)
        => Assert.Equal(expected, KeyGesture.Parse(text).ToString());

    [Fact] // Super renders platform-natively (Cmd on macOS, Win on Windows, Super elsewhere)
    public void N151b_ToString_SuperIsPlatformNative()
    {
        var super = OperatingSystem.IsMacOS() ? "Cmd" : OperatingSystem.IsWindows() ? "Win" : "Super";
        Assert.Equal($"Alt+{super}+K", KeyGesture.Parse("Win+Alt+K").ToString());
    }

    [Fact]
    public void N152_Constructor_CharacterRules_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new KeyGesture(Key.Character, KeyModifiers.Control));
        Assert.Throws<ArgumentException>(() => new KeyGesture(Key.F5, character: "x"));
    }

    [Fact]
    public void N153_LegacyC0_CtrlS_Matches()
    {
        // The legacy-C0 encoding row: raw DC3 (0x13) decodes as (Character, "s", Control).
        using var fixture = InputHost.CreateChain();
        var gesture = KeyGesture.Parse("Ctrl+S");

        bool? matched = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e =>
        {
            matched = gesture.Matches(e);
            e.Handled = true;
        });

        fixture.Host.SendBytes([0x13]);
        fixture.Host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
        fixture.Host.RunFrame();

        Assert.True(matched);
    }

    [Fact]
    public void N154_KittyCsiU_CtrlS_Matches_SameGestureBothWires()
    {
        // The Kitty encoding row: CSI 115;5 u decodes to the same (Character, "s", Control) shape.
        using var fixture = InputHost.CreateChain();
        var gesture = KeyGesture.Parse("Ctrl+S");

        bool? matched = null;
        (Key Key, string Text, KeyModifiers Modifiers)? shape = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e =>
        {
            matched = gesture.Matches(e);
            shape = (e.Key, e.Text.ToString(), e.Modifiers);
            e.Handled = true;
        });

        fixture.Host.SendBytes("\x1b[115;5u"u8);
        fixture.Host.DrainParsedInputAsync().GetAwaiter().GetResult();
        fixture.Host.RunFrame();

        Assert.Equal((Key.Character, "s", KeyModifiers.Control), shape);
        Assert.True(matched);
    }

    [Fact]
    public void N155_EscPrefixAlt_AltF_Matches()
    {
        // The legacy Alt observable: ESC-prefixed 'f' decodes as (Character, "f", Alt).
        using var fixture = InputHost.CreateChain();
        var gesture = KeyGesture.Parse("Alt+F");

        bool? matched = null;
        (Key Key, string Text, KeyModifiers Modifiers)? shape = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e =>
        {
            matched = gesture.Matches(e);
            shape = (e.Key, e.Text.ToString(), e.Modifiers);
            e.Handled = true;
        });

        fixture.Host.SendBytes([0x1b, (byte)'f']); // ESC 'f' — a "\x1bf" string literal greedily parses as U+01BF
        fixture.Host.DrainParsedInputAsync().GetAwaiter().GetResult();
        fixture.Host.RunFrame();

        Assert.Equal((Key.Character, "f", KeyModifiers.Alt), shape);
        Assert.True(matched);
    }

    [Fact]
    public void N156_LockBitsNeverConsulted()
    {
        var gesture = KeyGesture.Parse("Ctrl+S");
        var args = Args(
            Key.Character,
            "s",
            KeyModifiers.Control,
            extendedModifiers: KeyModifiers.Control | KeyModifiers.CapsLock | KeyModifiers.NumLock);

        Assert.True(gesture.Matches(args));
    }

    [Fact]
    public void N157_ExactModifiers_ShiftIsABit_CaseInsensitivityIsText()
    {
        var shifted = Args(Key.Character, "S", KeyModifiers.Control | KeyModifiers.Shift);

        Assert.False(KeyGesture.Parse("Ctrl+S").Matches(shifted));       // exact modifiers — Shift not subsumed
        Assert.True(KeyGesture.Parse("Ctrl+Shift+S").Matches(shifted));  // case-insensitivity is on the text
    }

    [Fact]
    public void N158_SpaceToken_CompilesToCharacterGesture()
    {
        var gesture = KeyGesture.Parse("Space");

        Assert.Equal(Key.Character, gesture.Key);
        Assert.Equal(" ", gesture.Character);
        Assert.True(gesture.Matches(Args(Key.Character, " ")));
    }

    [Fact]
    public void N159_SweepPosition_VirtualAndHandlersFirst_ThenBindingExecutes()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1");
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), new LogCommand(fixture.Log, "cmd")));
        fixture.Log.Clear();

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        var virtualIndex = fixture.Log.IndexOf("B.OnKeyDown");
        var handlerIndex = fixture.Log.IndexOf("B.h1");
        var commandIndex = fixture.Log.IndexOf("cmd");
        Assert.True(virtualIndex >= 0 && handlerIndex > virtualIndex && commandIndex > handlerIndex);
        Assert.DoesNotContain("A.OnKeyDown", fixture.Log); // Handled at B — bubble stops there
    }

    [Fact]
    public void N160_TwoBindingsSameGesture_FirstWins_SecondNeverConsulted()
    {
        using var fixture = InputHost.CreateChain();
        var first = new TestCommand();
        var second = new TestCommand();
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), first));
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), second));

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Single(first.Executions);
        Assert.Empty(second.Executions);
        Assert.Equal(0, second.CanExecuteCalls); // never consulted — ordering is the priority mechanism
    }

    [Fact]
    public void N161_CanExecuteFalse_SkippedWithoutConsuming_SecondExecutes()
    {
        using var fixture = InputHost.CreateChain();
        var first = new TestCommand { CanExecuteResult = false };
        var second = new TestCommand();
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), first));
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), second));

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Empty(first.Executions);             // ND15: skipped …
        Assert.True(first.CanExecuteCalls > 0);     // … after being consulted …
        Assert.Single(second.Executions);           // … without consuming
        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
    }

    [Fact]
    public void N162_FocusedElementWins_RootBindingOnlyWhenUnhandled()
    {
        using var fixture = InputHost.CreateChain();
        var rootCommand = new TestCommand();
        var innerCommand = new TestCommand();
        fixture.Root.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), rootCommand));
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), innerCommand));

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Single(innerCommand.Executions); // bubble order — focused-element-wins
        Assert.Empty(rootCommand.Executions);

        // The window-root default fires only when the inner route leaves the key unhandled.
        fixture.B.InputBindings.Clear();
        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Single(rootCommand.Executions);
        Assert.Single(innerCommand.Executions);
    }

    [Fact]
    public void N163_HandledBeforeBindingNode_SweepSkipped()
    {
        using var fixture = InputHost.CreateChain();
        var command = new TestCommand();
        fixture.B.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), command));
        fixture.B.AddHandler(UIElement.KeyDownEvent, (_, e) => e.Handled = true); // handler precedes the sweep

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Empty(command.Executions);
        Assert.Equal(0, command.CanExecuteCalls); // bindings are normal processing, not handledEventsToo
    }

    [Fact]
    public void N164_CanExecuteChanged_FlipsEffectiveEnabledAndDisabledState_BothDirections()
    {
        using var host = UIHeadlessHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var btn = new CommandBtn("B", log);
        root.AddChild(btn);
        host.ShowRoot(root);
        host.Application.FocusManager.ClearFocus(); // isolate the :disabled push from the ND28 focus repair

        var sink = new StateSink();
        host.Application.InteractionStateObserver = sink;

        var command = new TestCommand();
        btn.AttachCommand(command);
        Assert.True(btn.IsEffectivelyEnabled);

        command.CanExecuteResult = false;
        command.RaiseCanExecuteChanged();

        Assert.False(btn.IsEffectivelyEnabled);
        Assert.True((btn.InteractionStateInternal & InteractionState.Disabled) != 0);
        Assert.Single(sink.Notifications, n => ReferenceEquals(n.Element, btn) && (n.NewState & InteractionState.Disabled) != 0);

        command.CanExecuteResult = true;
        command.RaiseCanExecuteChanged();

        Assert.True(btn.IsEffectivelyEnabled);
        Assert.Equal(0u, (uint)(btn.InteractionStateInternal & InteractionState.Disabled));
    }

    [Fact]
    public void N165_BindingOnDisabledNode_NeverExecutes()
    {
        using var fixture = InputHost.CreateChain();
        var command = new TestCommand();
        fixture.Root.InputBindings.Add(new KeyBinding(KeyGesture.Parse("Ctrl+S"), command));

        // Disabling the root dooms every focus-repair candidate (ND28 repair fails → focus
        // clears), so keys target the active root: the disabled node IS in the route — exactly
        // the row's "in the focused chain only if focus repair failed" — and only the sweep's
        // effectively-disabled gate stands between the matching gesture and execution.
        fixture.Root.IsEnabled = false;
        Assert.Null(fixture.Host.Application.FocusManager.FocusedElement);

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Character, KeyModifiers.Control, "s"));

        Assert.Empty(command.Executions); // the sweep skips effectively-disabled nodes
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
    }
}

#pragma warning restore xUnit1031
