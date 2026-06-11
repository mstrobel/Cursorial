using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>Input matrix §2 — <c>ProcessEvent</c> classification and dispatch results (N19–N28).</summary>
public class Section02_Classification
{
    [Theory]
    [InlineData("resize")]
    [InlineData("deviceResponse")]
    [InlineData("unknown")]
    [InlineData("pointer")]
    public void N019_NonUIInput_ReturnsNotUIInput_ZeroRoutingZeroState(string kind)
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        InputEvent inputEvent = kind switch
        {
            "resize" => new ResizeEvent { Columns = 100, Rows = 30, Timestamp = DateTimeOffset.UnixEpoch },
            "deviceResponse" => new DeviceResponseEvent
            {
                Kind = DeviceResponseKind.PrimaryDeviceAttributes,
                Payload = new byte[] { 0x1b },
                Timestamp = DateTimeOffset.UnixEpoch,
            },
            "unknown" => new UnknownEvent { RawBytes = new byte[] { 0x1b, 0x5b }, Timestamp = DateTimeOffset.UnixEpoch },
            _ => new PointerEvent
            {
                PointerKind = PointerKind.Pen,
                EventKind = PointerEventKind.Move,
                Position = new CellPosition(3, 3),
                PointerId = 1,
                Timestamp = DateTimeOffset.UnixEpoch,
            },
        };

        var result = fixture.Dispatcher.ProcessEvent(inputEvent);

        Assert.Equal(InputDispatchResult.NotUIInput, result);
        Assert.Empty(fixture.Log);                                       // zero routing
        Assert.Null(fixture.Dispatcher.LastPointerPosition);             // zero state change
        Assert.Equal(InputModality.Keyboard, fixture.Dispatcher.LastModality);
    }

    [Fact]
    public void N020_UnclaimedKey_DispatchedUnhandled_RouteRan()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.NotEmpty(fixture.Log); // the route ran
    }

    [Fact]
    public void N021_HandledKey_DispatchedHandled()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: static e => e.Handled = true);

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("paste")]
    public void N022_NoFocusNoActiveRoot_KeyAndPasteDropped(string kind)
    {
        using var fixture = InputHost.CreateUnshown(); // root never shown — no ActiveRoot

        var result = kind == "key"
            ? fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter))
            : fixture.Dispatcher.ProcessEvent(fixture.Paste("x"));

        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.Empty(fixture.Log); // empty route — never routed to "topmost"
    }

    [Fact]
    public void N023_UnhandledCtrlC_DefaultGestureRequestsShutdown()
    {
        using var fixture = InputHost.CreateChain();
        fixture.Host.SendKey(Key.Character, KeyModifiers.Control, "c");
        fixture.Host.RunFrame();

        Assert.True(fixture.Host.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void N024_HandledCtrlC_SuppressesDefaultGesture()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: static e =>
        {
            if (e.Key == Key.Character && (e.Modifiers & KeyModifiers.Control) != 0)
                e.Handled = true; // a TextBox can claim copy
        });

        fixture.Host.SendKey(Key.Character, KeyModifiers.Control, "c");
        fixture.Host.RunFrame();

        Assert.False(fixture.Host.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void N025_SynthesizedClick_DefensiveNoOp()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

#if DEBUG
        var diagnostics = new List<string>();
        Action<string> sink = diagnostics.Add;
        InputDiagnostics.ContractViolation += sink;
        try
        {
#endif
            var result = fixture.Dispatcher.ProcessEvent(
                fixture.Mouse(MouseEventKind.Click, 5, 5, MouseButton.Left, synthesized: true));

            Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
            Assert.Empty(fixture.Log);                           // zero routing
            Assert.Null(fixture.Dispatcher.LastPointerPosition); // no state touched (ND4)
#if DEBUG
            Assert.Single(diagnostics);                          // the DEBUG diagnostic
        }
        finally
        {
            InputDiagnostics.ContractViolation -= sink;
        }
#endif
    }

    [Fact]
    public void N026_ModalityBookkeeping_PointerThenKeyboard_PositionRetained()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        fixture.Dispatcher.ProcessEvent(fixture.Mouse(MouseEventKind.Move, 5, 5));
        Assert.Equal(InputModality.Pointer, fixture.Dispatcher.LastModality);
        Assert.Equal(new CellPosition(5, 5), fixture.Dispatcher.LastPointerPosition);

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));
        Assert.Equal(InputModality.Keyboard, fixture.Dispatcher.LastModality);
        Assert.Equal(new CellPosition(5, 5), fixture.Dispatcher.LastPointerPosition); // retained
    }

    [Fact]
    public void N027_FreshDispatcher_NullPointerPosition_KeyboardModality()
    {
        using var fixture = InputHost.CreateChain();

        Assert.Null(fixture.Dispatcher.LastPointerPosition);                  // null until the first mouse event
        Assert.Equal(InputModality.Keyboard, fixture.Dispatcher.LastModality); // ND8
    }

    [Fact]
    public void N028_TerminalFocusRegained_RaisesTerminalFocusChangedOnly()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        var raised = new List<bool>();
        fixture.Dispatcher.TerminalFocusChanged += raised.Add;

        fixture.Dispatcher.ProcessEvent(fixture.Focus(true));

        Assert.Equal([true], raised);
        Assert.Same(fixture.B, fixture.Host.Application.FocusManager.FocusedElement); // no element focus change
        Assert.Empty(fixture.Log); // no other state touched — nothing routed
    }
}
