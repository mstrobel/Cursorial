using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Input;

// FocusManager.FocusedElementChanged — the app-level focus-changed signal (added with the bars nav work). It must
// fire EXACTLY ONCE per committed focus change: the raise belongs solely to RaiseFocusedElementChanged (after the
// routed Lost/GotFocus), NOT embedded in the routed-event raiser (which fired it a second + third time per change).
public sealed class FocusManagerEventTests
{
    [Fact]
    public void FocusedElementChanged_FiresExactlyOncePerChange()
    {
        using var host = UIHeadlessHost.Create();
        var a = new Button { Content = "A", Width = 4, Height = 1 };
        var b = new Button { Content = "B", Width = 4, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(a);
        root.Children.Add(b);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        a.Focus();
        host.RunUntilIdle();

        var count = 0;
        focus.FocusedElementChanged += (_, _) => count++;

        b.Focus(); // one A→B change
        host.RunUntilIdle();
        Assert.Equal(1, count);

        a.Focus(); // one B→A change
        host.RunUntilIdle();
        Assert.Equal(2, count);
    }

    [Fact] // a no-op re-focus of the already-focused element raises nothing
    public void FocusedElementChanged_SameElement_DoesNotRaise()
    {
        using var host = UIHeadlessHost.Create();
        var a = new Button { Content = "A", Width = 4, Height = 1 };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(a);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        a.Focus();
        host.RunUntilIdle();

        var count = 0;
        focus.FocusedElementChanged += (_, _) => count++;

        a.Focus(); // already focused — no transition
        host.RunUntilIdle();
        Assert.Equal(0, count);
    }
}
