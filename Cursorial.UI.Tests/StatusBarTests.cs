using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI;

/// <summary>
/// The <see cref="StatusBar"/> (#112): an ItemsControl that docks <see cref="StatusBarItem"/> containers along a
/// one-row bar (left-to-right by default, right-dockable per item); a <see cref="StatusBarItem"/>/<see cref="Separator"/>
/// is its own container.
/// </summary>
public class StatusBarTests
{
    private static (UITestHost Host, StatusBar Bar) Make(params object[] items)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 3) });
        var bar = new StatusBar { VerticalAlignment = VerticalAlignment.Top, ItemsSource = items };
        host.ShowRoot(bar);
        host.RunUntilIdle();
        return (host, bar);
    }

    [Fact]
    public void PlainItems_GetStatusBarItemContainers_AndRenderLeftToRight()
    {
        var (host, bar) = Make("Ready", "Line 1");
        using var _ = host;

        Assert.IsType<StatusBarItem>(bar.ItemContainerGenerator.ContainerFromIndex(0));

        var row = host.GetRowText(0);
        Assert.Contains("Ready", row);
        Assert.Contains("Line 1", row);
        Assert.True(row.IndexOf("Ready", StringComparison.Ordinal) < row.IndexOf("Line 1", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusBarItemAndSeparator_AreUsedAsOwnContainers()
    {
        var item = new StatusBarItem { Content = "X" };
        var separator = new Separator();
        var (host, bar) = Make(item, separator);
        using var _ = host;

        Assert.Same(item, bar.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Same(separator, bar.ItemContainerGenerator.ContainerFromIndex(1));
    }

    [Fact]
    public void RightDockedItem_AlignsToTheRightEdge()
    {
        var left = new StatusBarItem { Content = "L" };
        var right = new StatusBarItem { Content = "R" };
        DockPanel.SetDock(right, Dock.Right);
        var (host, bar) = Make(left, right);
        using var _ = host;

        var row = host.GetRowText(0);
        var li = row.IndexOf('L');
        var ri = row.IndexOf('R');
        Assert.True(li >= 0 && ri >= 0);
        Assert.True(li < ri);  // L at the left, R docked right
        Assert.True(ri > 30);  // R is near the 40-wide right edge
    }
}
