using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Interactivity;

namespace Cursorial.Tests.UI.Interactivity;

/// <summary>
/// §9 — <c>ViewRegistration</c>: the attach-time self→VM handoff. The behavior registers its host with an
/// <see cref="IResourceRootSink"/> DataContext (present at attach OR arriving/swapping later) and
/// unregisters on detach/replacement — the VM gets a resource-resolution root without the element tree.
/// </summary>
public sealed class ViewRegistrationTests
{
    private sealed class SinkVm : IResourceRootSink
    {
        public readonly List<UIElement?> Received = [];

        public void SetResourceRoot(UIElement? root) => Received.Add(root);
    }

    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });

    [Fact] // the DataContext is already a sink at attach → registered immediately; detach unregisters (null)
    public void RegistersAtAttach_UnregistersAtDetach()
    {
        using var host = NewHost();
        var vm = new SinkVm();
        var view = new StackPanel { DataContext = vm };
        Interaction.GetBehaviors(view).Add(new ViewRegistration());

        Assert.Empty(vm.Received); // not attached yet — nothing registered

        host.ShowRoot(view);
        host.RunUntilIdle();
        Assert.Same(view, Assert.Single(vm.Received)); // registered with the view at attach

        Interaction.SetBehaviors(view, null); // behavior detaches
        Assert.Equal(2, vm.Received.Count);
        Assert.Null(vm.Received[1]); // …and unregistered — a dead view is never pinned
    }

    [Fact] // the DataContext ARRIVES after attach (the real MVVM order) → registration follows it
    public void RegistersWhenDataContextArrives_AndFollowsSwaps()
    {
        using var host = NewHost();
        var view = new StackPanel();
        Interaction.GetBehaviors(view).Add(new ViewRegistration());
        host.ShowRoot(view);
        host.RunUntilIdle();

        var first = new SinkVm();
        view.DataContext = first;
        Assert.Same(view, Assert.Single(first.Received)); // registered on arrival

        var second = new SinkVm();
        view.DataContext = second;                        // swap
        Assert.Equal(2, first.Received.Count);
        Assert.Null(first.Received[1]);                   // the old sink unregistered…
        Assert.Same(view, Assert.Single(second.Received)); // …the new one registered

        view.DataContext = "not a sink";                  // a non-sink context unregisters cleanly
        Assert.Equal(2, second.Received.Count);
        Assert.Null(second.Received[1]);
    }

    [Fact] // an inherited DataContext (set on an ancestor) registers the HOST element, not the ancestor
    public void InheritedDataContext_RegistersTheHost()
    {
        using var host = NewHost();
        var vm = new SinkVm();
        var inner = new Button();
        var root = new StackPanel { DataContext = vm };
        root.Children.Add(inner);
        Interaction.GetBehaviors(inner).Add(new ViewRegistration());
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Same(inner, Assert.Single(vm.Received)); // the behavior's host, via inheritance
    }
}
