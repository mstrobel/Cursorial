using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI;

/// <summary>
/// The <see cref="Thumb"/> draggable primitive (#109): a left-press captures the mouse and reports
/// DragStarted → DragDelta* → DragCompleted with cumulative cell deltas from the grab point (stable
/// terminal coords), and a non-release capture loss cancels the drag.
/// </summary>
public class ThumbTests
{
    private static (UITestHost Host, Thumb Thumb) Make()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(80, 24) });
        var thumb = new Thumb { Background = new SolidColorBrush(Color.FromRgb(80, 80, 80)) }; // hit-opaque + visible
        host.ShowRoot(thumb);
        host.RunUntilIdle();
        return (host, thumb);
    }

    // SendInput re-stamps Timestamp, but MouseEvent's required members must still be set in the initializer.
    private static MouseEvent Mouse(MouseEventKind kind, int c, int r, MouseButton button, MouseButtons held) => new()
    {
        Kind = kind,
        Position = new CellPosition(c, r),
        Button = button,
        ButtonsHeld = held,
        Modifiers = KeyModifiers.None,
        Timestamp = default,
    };

    private static void Down(UITestHost h, int c, int r)
    {
        h.SendInput(Mouse(MouseEventKind.ButtonDown, c, r, MouseButton.Left, MouseButtons.None));
        h.RunUntilIdle();
    }

    private static void Move(UITestHost h, int c, int r)
    {
        h.SendInput(Mouse(MouseEventKind.Move, c, r, MouseButton.None, MouseButtons.Left));
        h.RunUntilIdle();
    }

    private static void Up(UITestHost h, int c, int r)
    {
        h.SendInput(Mouse(MouseEventKind.ButtonUp, c, r, MouseButton.Left, MouseButtons.None));
        h.RunUntilIdle();
    }

    [Fact]
    public void Drag_ReportsCumulativeDeltas_AndLifecycle()
    {
        var (host, thumb) = Make();
        using var _ = host;

        var started = 0;
        var deltas = new List<(int H, int V)>();
        (int H, int V) completed = (int.MinValue, int.MinValue);
        thumb.DragStarted += (_, _) => started++;
        thumb.DragDelta += (_, e) => deltas.Add((e.HorizontalChange, e.VerticalChange));
        thumb.DragCompleted += (_, e) => completed = (e.HorizontalChange, e.VerticalChange);

        Down(host, 10, 5);
        Assert.Equal(1, started);
        Assert.True(thumb.IsDragging);

        Move(host, 15, 8); // +5, +3 from the grab (10,5)
        Move(host, 12, 5); // +2,  0 from the grab
        Assert.Equal([(5, 3), (2, 0)], deltas);

        Up(host, 12, 5);
        Assert.Equal((2, 0), completed); // final cumulative delta
        Assert.False(thumb.IsDragging);
    }

    [Fact]
    public void RightButton_DoesNotStartDrag()
    {
        var (host, thumb) = Make();
        using var _ = host;

        var started = 0;
        thumb.DragStarted += (_, _) => started++;

        host.SendInput(Mouse(MouseEventKind.ButtonDown, 10, 5, MouseButton.Right, MouseButtons.None));
        host.RunUntilIdle();

        Assert.Equal(0, started);
        Assert.False(thumb.IsDragging);
    }

    [Fact] // A non-release capture loss (e.g. detach / surface close) cancels the drag exactly once.
    public void CaptureLoss_CancelsDragOnce()
    {
        var (host, thumb) = Make();
        using var _ = host;

        var completed = 0;
        thumb.DragCompleted += (_, _) => completed++;

        Down(host, 10, 5);
        Assert.True(thumb.IsDragging);

        thumb.ReleaseMouseCapture();
        host.RunUntilIdle();

        Assert.False(thumb.IsDragging);
        Assert.Equal(1, completed);
    }
}
