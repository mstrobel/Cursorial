using Cursorial.Tests.UI.InputMatrix;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Input;

/// <summary>
/// <see cref="FocusManager.FindNext"/> (design-doc punch 23 — the Label-targeting query): a pure lookup anchored at
/// any element, focusable or not. With no <c>direction</c> it is a tab-order walk (matrix N221/N226); with a
/// <see cref="FocusNavigationDirection"/> it routes through the geometric directional scorer (<c>NextDirectional</c>)
/// — the <c>BarLabel</c> mnemonic-forwarding path (N228). These are the API's unit tests.
/// </summary>
public class FocusManagerFindNextTests
{
    private static (UITestHost Host, FocusManager Focus, Probe Label, Btn F1, Btn F2) CreateForm()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var f1 = new Btn("F1", log);
        var label = new Probe("Label", log); // not focusable — the Label shape
        var f2 = new Btn("F2", log);
        root.AddChild(f1);
        root.AddChild(label);
        root.AddChild(f2);
        host.ShowRoot(root);
        return (host, host.Application.FocusManager, label, f1, f2);
    }

    [Fact]
    public void FindNext_FromNonFocusableAnchor_ReturnsFollowingTabStop()
    {
        var (host, focus, label, _, f2) = CreateForm();
        using var _ = host;

        Assert.Same(f2, focus.FindNext(label)); // document position anchors the search
    }

    [Fact]
    public void FindNext_FromTabStop_ReturnsNext_AndWraps()
    {
        var (host, focus, _, f1, f2) = CreateForm();
        using var _ = host;

        Assert.Same(f2, focus.FindNext(f1));
        Assert.Same(f1, focus.FindNext(f2)); // wraps within the container, like Tab
    }

    [Fact]
    public void FindNext_IsAPureQuery_NeverMovesFocus()
    {
        var (host, focus, label, f1, _) = CreateForm();
        using var _ = host;
        Assert.Same(f1, focus.FocusedElement); // activation auto-focus

        focus.FindNext(label);
        focus.FindNext(f1);

        Assert.Same(f1, focus.FocusedElement);
    }

    [Fact]
    public void FindNext_EntryResolvesOnceContainers()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var label = new Probe("Label", log);
        var list = new Probe("List", log);
        var item = new Btn("Item", log);
        root.AddChild(label);
        root.AddChild(list);
        list.AddChild(item);
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Once);
        host.ShowRoot(root);

        Assert.Same(item, host.Application.FocusManager.FindNext(label)); // ND16 entry resolution
    }

    [Fact]
    public void FindNext_NothingReachable_ReturnsNull()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var label = new Probe("Label", log);
        root.AddChild(label);
        host.ShowRoot(root);

        Assert.Null(host.Application.FocusManager.FindNext(label));
    }

    [Fact] // the directional overload routes through NextDirectional (geometry), distinct from the tab-order default
    public void FindNext_DirectionalRight_ReturnsRightwardNeighbor_AndNullMatchesTabForm()
    {
        using var host = UITestHost.Create();
        var b1 = new Button { Content = "B1", Width = 6, Height = 1 };
        var b2 = new Button { Content = "B2", Width = 6, Height = 1 };
        var b3 = new Button { Content = "B3", Width = 6, Height = 1 };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        KeyboardNavigation.SetDirectionalNavigation(row, DirectionalNavigationMode.Cycle);
        row.Children.Add(b1);
        row.Children.Add(b2);
        row.Children.Add(b3);
        host.ShowRoot(row);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        Assert.Same(b2, focus.FindNext(b1, FocusNavigationDirection.Right)); // geometry: the rightward neighbor
        Assert.Same(focus.FindNext(b1), focus.FindNext(b1, direction: null)); // null direction == the tab-order form
    }

    [Fact] // the fork: with NO DirectionalNavigation container the directional form returns null where tab finds a stop
    public void FindNext_DirectionalRight_NoContainer_ReturnsNull_WhileTabFindsStop()
    {
        using var host = UITestHost.Create();
        var b1 = new Button { Content = "B1", Width = 6, Height = 1 };
        var b2 = new Button { Content = "B2", Width = 6, Height = 1 };
        var row = new StackPanel { Orientation = Orientation.Horizontal }; // DirectionalNavigation defaults to None
        row.Children.Add(b1);
        row.Children.Add(b2);
        host.ShowRoot(row);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        Assert.Null(focus.FindNext(b1, FocusNavigationDirection.Right)); // no directional container ⇒ null (opt-in, N135)
        Assert.Same(b2, focus.FindNext(b1));                            // tab order still finds the next stop
    }
}
