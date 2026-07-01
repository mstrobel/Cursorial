using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Bars;

// MiniToolbar: a right-click floating strip of bar controls, opened via the MiniToolbar.Bar attached property on a
// target (parallels ContextMenu, but horizontal, and it wins the right-click by marking the event handled).
public sealed class MiniToolbarTests
{
    private static UITestHost NewHost(int w = 40, int h = 12) =>
        UITestHost.Create(new UITestHostOptions { InitialSize = new Size(w, h), Capabilities = TestCapabilities.KittyTruecolor });

    private static (UITestHost Host, StackPanel Root, Border Target, MiniToolbar Strip) Build()
    {
        var host = NewHost();
        var strip = new MiniToolbar();
        strip.Items.Add(new BarButton { Content = "Cut" });
        strip.Items.Add(new BarButton { Content = "Copy" });
        var target = new Border { Width = 30, Height = 8, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        MiniToolbar.SetBar(target, strip);
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(target);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, root, target, strip);
    }

    [Fact] // a right-click over an element carrying a MiniToolbar.Bar opens the strip
    public void MiniToolbar_RightClickOpens()
    {
        var (host, _, _, strip) = Build();
        using var _h = host;

        host.SendClick(10, 4, MouseButton.Right);
        host.RunUntilIdle();

        Assert.True(strip.IsOpen);
    }

    [Fact] // clicking far outside light-dismisses the strip
    public void MiniToolbar_LightDismissCloses()
    {
        var (host, _, _, strip) = Build();
        using var _h = host;

        host.SendClick(10, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.True(strip.IsOpen);

        host.SendClick(38, 11); // an outside press → light-dismiss
        host.RunUntilIdle();
        Assert.False(strip.IsOpen);
    }

    [Fact] // Close() closes the strip; detaching the target closes it too (no stranded surface)
    public void MiniToolbar_CloseAndDetach()
    {
        var (host, root, target, strip) = Build();
        using var _h = host;

        host.SendClick(10, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.True(strip.IsOpen);

        strip.Close();
        host.RunUntilIdle();
        Assert.False(strip.IsOpen);

        host.SendClick(10, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.True(strip.IsOpen);

        root.Children.Remove(target); // detach the anchor while the strip is open → the target-watch closes it
        host.RunUntilIdle();
        Assert.False(strip.IsOpen);
    }
}
