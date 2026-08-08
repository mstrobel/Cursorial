using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Bars;

// MiniToolbar: a right-click floating strip of bar controls, opened via the MiniToolbar.Bar attached property on a
// target (parallels ContextMenu, but horizontal, and it wins the right-click by marking the event handled).
public sealed class MiniToolbarTests
{
    private static UIHeadlessHost NewHost(int w = 40, int h = 12) =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(w, h), Capabilities = HeadlessCapabilities.KittyTruecolor });

    private static (UIHeadlessHost Host, StackPanel Root, Border Target, MiniToolbar Strip) Build()
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

    [Fact] // the target-watch tracks the open strip: armed on open, released on Close() and on light-dismiss
    public void MiniToolbar_TargetWatch_TracksOpenState()
    {
        var (host, _, target, strip) = Build();
        using var _h = host;

        host.SendClick(10, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.True(strip.IsOpen);
        Assert.Same(target, strip.WatchedTargetForTests); // watching while open

        strip.Close();
        host.RunUntilIdle();
        Assert.Null(strip.WatchedTargetForTests);          // released on Close()

        host.SendClick(10, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.Same(target, strip.WatchedTargetForTests);

        host.SendClick(38, 11); // light-dismiss
        host.RunUntilIdle();
        Assert.Null(strip.WatchedTargetForTests);          // released on light-dismiss (via OnPopupClosed)
    }

    [Fact] // Open() with no live WindowManager must not strand the target-watch: the popup never opens, so Closed never
           // fires and OnPopupClosed (the sole unwatch site) would never run — the fix skips the watch when not open
    public void MiniToolbar_OpenWithNoWindowManager_DoesNotLeakWatch()
    {
        Assert.Null(UIApplication.Current); // no host on this thread → Popup.OpenCore bails (no WindowManager)

        var strip = new MiniToolbar();
        strip.Items.Add(new BarButton { Content = "Cut" });
        var target = new Border { Width = 10, Height = 3 };
        MiniToolbar.SetBar(target, strip);

        strip.Open(target);

        Assert.False(strip.IsOpen);                  // OpenCore bailed
        Assert.Null(strip.WatchedTargetForTests);    // …and the DetachedFromLogicalTree subscription was NOT stranded
    }

    [Fact] // faces that materialize AFTER open (bound BarCommands / data-templated icon carriers) grow the strip —
           // the popup surface re-fits instead of clipping the tail at its open-time width (the gallery regression:
           // three groups of three, everything after the second separator invisible)
    public void MiniToolbar_LateFaces_GrowTheOpenStrip()
    {
        var host = NewHost(w: 80, h: 12);
        using var _h = host;

        var strip = new MiniToolbar();
        var buttons = new List<BarButton>();
        for (var g = 0; g < 3; g++)
        {
            if (g > 0)
                strip.Items.Add(new BarSeparator());
            for (var i = 0; i < 3; i++)
            {
                var b = new BarButton(); // faceless until the "command binding" delivers below
                buttons.Add(b);
                strip.Items.Add(b);
            }
        }

        var target = new Border { Width = 60, Height = 8, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        MiniToolbar.SetBar(target, strip);
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(target);
        host.ShowRoot(root);
        host.RunUntilIdle();

        host.SendClick(40, 4, MouseButton.Right);
        host.RunUntilIdle();
        Assert.True(strip.IsOpen);

        const string icons = "ABCDEFGHI";
        for (var i = 0; i < buttons.Count; i++)
            buttons[i].Icon = icons[i].ToString(); // the late arrival
        host.RunUntilIdle();

        Assert.Equal(strip.DesiredSize.Columns, strip.Bounds.Columns); // the surface grew with the strip

        var screen = string.Join("\n", Enumerable.Range(0, 12).Select(host.GetRowText));
        Assert.Contains("G", screen); // the third group is visible…
        Assert.Contains("I", screen); // …to its last button
    }
}
