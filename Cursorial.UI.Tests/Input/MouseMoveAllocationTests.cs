using System.Diagnostics.CodeAnalysis;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Input;

/// <summary>
/// The I2 stage gate for the zero-steady-state-allocation contract on the Move/hover path (design
/// doc §7.6 — any-event motion is on by default, so the per-Move path must be allocation-free).
/// The formal matrix rows (N196/N197/N199, ND25 methodology incl. the probe-4 motion-storm CI
/// gate) land at I4 in <c>Section14</c>; this gate asserts the same contract at the stage that
/// introduces the path.
/// </summary>
[SuppressMessage("ReSharper", "UnusedTupleComponentInReturnValue")]
public class MouseMoveAllocationTests
{
    /// <summary>A non-logging rigid leaf — log-string allocation must not pollute the measurement.</summary>
    private sealed class RigidElement(int columns, int rows) : UIElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
    }

    private static (UITestHost Host, RigidElement A, RigidElement B) CreateTree()
    {
        var host = UITestHost.Create();
        var root = new Canvas();
        var a = new RigidElement(10, 4);
        var b = new RigidElement(10, 4);
        Canvas.SetLeft(a, 10);
        Canvas.SetTop(a, 5);
        Canvas.SetLeft(b, 30);
        Canvas.SetTop(b, 5);
        root.Children.Add(a);
        root.Children.Add(b);
        host.ShowRoot(root);
        host.RunFrame(); // settle layout + render — RenderTree.HitTest valid
        return (host, a, b);
    }

    private static MouseEvent Move(int column, int row)
        => new()
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(column, row),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    [Fact]
    public void SameLeafMove_SteadyState_AllocatesNothing()
    {
        var (host, _, _) = CreateTree();
        using var _ = host;
        var dispatcher = host.Application.InputDispatcher;

        // Two cells inside A — the unchanged-chain steady state (the N196 shape).
        var first = Move(12, 6);
        var second = Move(13, 6);

        for (var i = 0; i < 256; i++)
        {
            dispatcher.ProcessEvent(first);
            dispatcher.ProcessEvent(second);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            dispatcher.ProcessEvent(first);
            dispatcher.ProcessEvent(second);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AlternatingLeafMove_EnterLeavePerMove_AllocatesNothing()
    {
        var (host, _, _) = CreateTree();
        using var _ = host;
        var dispatcher = host.Application.InputDispatcher;

        // Alternating between the two leaves — enter/leave + pooled snapshots per move (the N197
        // shape; HoverChanged unsubscribed here, the subscribed variant is I4's formal row).
        var overA = Move(12, 6);
        var overB = Move(32, 6);

        for (var i = 0; i < 256; i++)
        {
            dispatcher.ProcessEvent(overA);
            dispatcher.ProcessEvent(overB);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            dispatcher.ProcessEvent(overA);
            dispatcher.ProcessEvent(overB);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
