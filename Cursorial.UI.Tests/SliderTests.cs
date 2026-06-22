using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI;

/// <summary>
/// The <see cref="Slider"/> (#110): a <see cref="RangeBase"/> whose <see cref="Thumb"/> rides a rail at the value
/// fraction. Covers thumb positioning by Value, keyboard adjustment, rail-click-to-position, and the thumb-drag
/// integration with the Thumb primitive.
/// </summary>
public class SliderTests
{
    private const int Length = 20; // travel = Length - 1 = 19

    private static (UITestHost Host, Slider Slider, Thumb Thumb) Make(Orientation orientation = Orientation.Horizontal, double value = 0)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 12) });
        var slider = new Slider
        {
            Orientation = orientation,
            Minimum = 0, Maximum = 100, Value = value, SmallChange = 1, LargeChange = 10,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        if (orientation == Orientation.Horizontal)
            slider.Width = Length;
        else
            slider.Height = Length;

        host.ShowRoot(slider);
        host.RunUntilIdle();
        var thumb = (Thumb)slider.TemplateInstance!.NameScope.Find("PART_Thumb")!;
        return (host, slider, thumb);
    }

    private static MouseEvent Mouse(MouseEventKind kind, int c, int r, MouseButton button, MouseButtons held) => new()
    {
        Kind = kind, Position = new CellPosition(c, r), Button = button, ButtonsHeld = held,
        Modifiers = KeyModifiers.None, Timestamp = default,
    };

    [Fact]
    public void Horizontal_ThumbPosition_TracksValue()
    {
        var (host, slider, thumb) = Make(value: 0);
        using var _ = host;

        Assert.Equal(0, thumb.Bounds.Column); // Minimum → left edge

        slider.Value = 100;
        host.RunUntilIdle();
        Assert.Equal(Length - 1, thumb.Bounds.Column); // Maximum → right edge (travel = 19)

        slider.Value = 50;
        host.RunUntilIdle();
        Assert.InRange(thumb.Bounds.Column, 9, 10); // mid-rail
    }

    [Fact]
    public void Vertical_ThumbPosition_MaximumAtTop()
    {
        var (host, slider, thumb) = Make(Orientation.Vertical, value: 100);
        using var _ = host;
        Assert.Equal(0, thumb.Bounds.Row); // Maximum → top

        slider.Value = 0;
        host.RunUntilIdle();
        Assert.Equal(Length - 1, thumb.Bounds.Row); // Minimum → bottom
    }

    [Fact]
    public void Keyboard_AdjustsValue()
    {
        var (host, slider, _) = Make(value: 50);
        using var _ = host;
        slider.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(51, slider.Value); // +SmallChange

        host.SendKey(Key.PageUp);
        host.RunUntilIdle();
        Assert.Equal(61, slider.Value); // +LargeChange

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, slider.Value);

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(100, slider.Value);
    }

    [Fact]
    public void RailClick_JumpsValueToThatEnd()
    {
        var (host, slider, _) = Make(value: 0);
        using var _ = host;

        // The thumb is at column 0; click the far right of the rail (column 19) ⇒ fraction 1 ⇒ Maximum.
        var (col, row) = slider.TranslateToWindow(Length - 1, 0);
        host.SendInput(Mouse(MouseEventKind.ButtonDown, col, row, MouseButton.Left, MouseButtons.None));
        host.RunUntilIdle();
        host.SendInput(Mouse(MouseEventKind.ButtonUp, col, row, MouseButton.Left, MouseButtons.None));
        host.RunUntilIdle();

        Assert.Equal(100, slider.Value);
    }

    [Fact] // Audit F1: the thumb's accent fill resolves (was null — an invisible thumb — when wired via a nested
           // ^/template/Thumb control-theme rule that doesn't match a cross-template part; now set on the part directly).
    public void Thumb_Background_ResolvesToAccentFill()
    {
        var (host, _, thumb) = Make(value: 50);
        using var _ = host;
        Assert.NotNull(thumb.Background);
    }

    [Fact]
    public void ThumbDrag_ChangesValue()
    {
        var (host, slider, thumb) = Make(value: 0);
        using var _ = host;

        // Grab the thumb (at column 0) and drag right 10 cells: delta 10 / travel 19 × range 100 ≈ 52.
        var (col, row) = thumb.TranslateToWindow(0, 0);
        host.SendInput(Mouse(MouseEventKind.ButtonDown, col, row, MouseButton.Left, MouseButtons.None));
        host.RunUntilIdle();
        host.SendInput(Mouse(MouseEventKind.Move, col + 10, row, MouseButton.None, MouseButtons.Left));
        host.RunUntilIdle();
        host.SendInput(Mouse(MouseEventKind.ButtonUp, col + 10, row, MouseButton.Left, MouseButtons.None));
        host.RunUntilIdle();

        Assert.InRange(slider.Value, 45, 60);
    }
}
