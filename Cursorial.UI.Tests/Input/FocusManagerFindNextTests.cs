using Cursorial.Tests.UI.InputMatrix;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Input;

/// <summary>
/// <see cref="FocusManager.FindNext"/> (design-doc punch 23 — the Label-targeting query): a pure
/// tab-order lookup anchored at any element, focusable or not. No matrix rows exist for it; these
/// are the API's unit tests.
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
}
