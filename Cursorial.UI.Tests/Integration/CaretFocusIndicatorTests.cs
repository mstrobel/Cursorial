// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// The <see cref="Caret"/> element (design doc §5.9) and its CheckBox/RadioButton focus-indicator
/// wiring: the caret publishes the real terminal cursor at its arranged origin while
/// <see cref="Caret.IsCaretShown"/> is set, and the BuiltIn toggle theme shows it inside the
/// <c>[ ]</c>/<c>( )</c> box under <c>:focus-visible</c> (keyboard focus only). The integration cases
/// also empirically pin that the <c>^:focus-visible /template/ Caret</c> rule re-evaluates on the
/// focus-visible flip.
/// </summary>
public sealed class CaretFocusIndicatorTests
{
    [Fact]
    public void Caret_PublishesWhenShown_ClearsWhenHidden_DefaultsToBlinkingUnderline()
    {
        using var host = UITestHost.Create();
        // Left/Top-pin so the zero-size caret sits at the origin (default Stretch would center it).
        var caret = new Caret { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.ShowRoot(new Border { Child = caret });
        Assert.True(host.RunUntilIdle());

        // Resting: no publication ⇒ the terminal cursor is hidden.
        Assert.False(host.FrameBuffer.CursorVisible);

        caret.IsCaretShown = true;
        host.RunFrame();
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(CursorShape.BlinkingUnderline, host.FrameBuffer.CursorShape); // the default shape
        Assert.Equal(0, host.FrameBuffer.CursorColumn);                            // published at its origin
        Assert.Equal(0, host.FrameBuffer.CursorRow);

        caret.IsCaretShown = false;
        host.RunFrame();
        Assert.False(host.FrameBuffer.CursorVisible);
    }

    [Fact]
    public void Caret_ShapeOverride_IsPublished()
    {
        using var host = UITestHost.Create();
        var caret = new Caret { CaretShape = CursorShape.SteadyBar, IsCaretShown = true };
        host.ShowRoot(new Border { Child = caret });
        Assert.True(host.RunUntilIdle());

        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(CursorShape.SteadyBar, host.FrameBuffer.CursorShape);
    }

    [Fact]
    public void CheckBox_KeyboardFocus_ShowsCaretInBox_AndClearsWhenFocusMovesAway()
    {
        using var host = UITestHost.Create();
        var check = new CheckBox { Content = "Enable" };
        var button = new Button { Content = "OK" };
        var stack = new StackPanel();
        stack.Children.Add(check);  // first tab stop — auto-focused on activation (Restore ⇒ focus-visible)
        stack.Children.Add(button);
        host.ShowRoot(stack);
        Assert.True(host.RunUntilIdle());

        // The CheckBox auto-focuses with Restore (focus-visible) ⇒ the :focus-visible rule shows the
        // caret inside its box (the space inside "[ ]" — column 1 of the glyph at the panel origin).
        Assert.Same(check, host.Application.FocusManager.FocusedElement);
        Assert.True(host.FrameBuffer.CursorVisible, "keyboard-focused CheckBox should show the box caret");
        Assert.Equal(CursorShape.BlinkingUnderline, host.FrameBuffer.CursorShape);
        Assert.Equal(1, host.FrameBuffer.CursorColumn);
        Assert.Equal(0, host.FrameBuffer.CursorRow);

        // Tab to the Button: the CheckBox loses :focus-visible ⇒ the rule retracts ⇒ the caret clears.
        host.SendKey(Key.Tab);
        Assert.True(host.RunUntilIdle());
        Assert.Same(button, host.Application.FocusManager.FocusedElement);
        Assert.False(host.FrameBuffer.CursorVisible, "the Button has no caret; the CheckBox caret must clear");
    }

    [Fact]
    public void CheckBox_PointerFocus_DoesNotShowCaret()
    {
        // Pointer focus is NOT focus-visible, so the box caret stays hidden (the indicator is for
        // keyboard navigation). A second control exists so activation auto-focus does not land the
        // keyboard caret on the CheckBox before the click.
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 4) });
        var button = new Button { Content = "OK" };
        var check = new CheckBox { Content = "Enable" };
        var stack = new StackPanel();
        stack.Children.Add(button); // first tab stop ⇒ takes the activation auto-focus
        stack.Children.Add(check);
        host.ShowRoot(stack);
        Assert.True(host.RunUntilIdle());
        Assert.False(host.FrameBuffer.CursorVisible); // button focused, no caret

        // Click the CheckBox's glyph — pointer focus, not focus-visible. The bordered Button is 3 rows
        // tall, so the CheckBox sits at row 3; its box is at column 1.
        host.SendClick(1, 3);
        Assert.True(host.RunUntilIdle());
        Assert.Same(check, host.Application.FocusManager.FocusedElement);
        Assert.False(host.FrameBuffer.CursorVisible, "pointer focus must not show the keyboard caret");
    }
}
