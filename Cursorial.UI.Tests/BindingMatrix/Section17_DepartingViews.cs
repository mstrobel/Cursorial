using System.ComponentModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §17 — departing views: "a departing view must not write to its source." The detach walk
/// quiesces every expression's reverse lane (target → source) BEFORE inheritance severance, so the cascade
/// (DataContext loss → items sources clear → selections clear) never round-trips into a view-model as a
/// phantom edit — the curio chooser / dialog-picker selection-loss bug. Re-attach re-arms. App dispose
/// additionally sweeps every still-alive formerly-mounted root (the weak-list backstop): past dispose the
/// dispatcher dies and the content is permanently unusable, so an un-torn swapped-out root is by
/// definition a leak.
/// </summary>
public class Section17_DepartingViews
{
    public Section17_DepartingViews()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    private sealed class ChooserVm : INotifyPropertyChanged
    {
        private object? _selected;

        public List<string> Items { get; } = ["alpha", "beta", "gamma"];

        public object? Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;
    }

    private static ListBox BoundChooser(ChooserVm vm)
    {
        var list = new ListBox { DataContext = vm };
        BindingOperations.Install(list, ItemsControl.ItemsSourceProperty, new Binding("Items"));
        BindingOperations.Install(list, SelectingItemsControl.SelectedItemProperty,
            new Binding("Selected") { Mode = BindingMode.TwoWay });
        return list;
    }

    [Fact] // the curio bug: app dispose (teardown + detach) must NOT null the VM's selection
    public void B190_AppDispose_SelectionSurvives_SubscriptionsReleased()
    {
        var vm = new ChooserVm { Selected = "beta" };
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        root.Children.Add(BoundChooser(vm));
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal("beta", ((ListBox)root.Children[0]).SelectedItem); // forward sync sanity

        host.Dispose(); // the canonical app teardown (1b: TearDown before surface detach)

        Assert.Equal("beta", vm.Selected);       // the selection SURVIVED — no phantom cancel
        Assert.Equal(0, vm.SubscriberCount);     // …and the teardown sweep released the VM
    }

    [Fact] // the dialog-picker bug: window CLOSE (detach-first by design) must not null the VM's selection
    public void B191_WindowClose_SelectionSurvives_SubscriptionsReleased()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var _ = host;
        host.ShowRoot(new StackPanel());
        host.RunUntilIdle();
        var wm = host.Application.WindowManager!;

        var vm = new ChooserVm { Selected = "beta" };
        var window = host.NewWindow(content: BoundChooser(vm), width: 30, height: 10);
        window.Show(wm);
        host.RunUntilIdle();

        Assert.Equal("beta", ((ListBox)window.Content!).SelectedItem); // forward sync sanity

        window.Close(); // reversible detach → Closed → terminal sweep (the order the escape hatch needs)
        host.RunUntilIdle();

        Assert.Equal("beta", vm.Selected);       // the quiesce covered the close's detach cascade
        Assert.Equal(0, vm.SubscriberCount);     // the terminal sweep released the VM
    }

    [Fact] // the quiesce/resume pair: detached writes don't reach the source; re-attach re-arms
    public void B192_DetachQuiesces_ReattachRearms()
    {
        var vm = new ChooserVm();
        var widget = new BindWidget { DataContext = vm };
        BindingOperations.Install(widget, BindWidget.TextProperty,
            new Binding("Selected") { Mode = BindingMode.TwoWay });

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        root.Children.Add(widget);
        host.ShowRoot(root);
        host.RunUntilIdle();

        widget.SetValue(BindWidget.TextProperty, "attached");
        Assert.Equal("attached", vm.Selected);   // live: write-back flows

        root.Children.Remove(widget);            // detach → quiesce
        host.RunUntilIdle();
        widget.SetValue(BindWidget.TextProperty, "departed");
        Assert.Equal("attached", vm.Selected);   // quiesced: the departing write never reached the VM

        root.Children.Add(widget);               // re-attach → re-arm
        host.RunUntilIdle();
        widget.SetValue(BindWidget.TextProperty, "rehosted");
        Assert.Equal("rehosted", vm.Selected);   // a re-hosted view binds two-way again
    }

    [Fact] // an EXPLICIT flush on a departed view is equally a phantom edit — gated while quiesced
    public void B193_Detached_ExplicitUpdateSource_NoOps()
    {
        var vm = new ChooserVm();
        var widget = new BindWidget { DataContext = vm };
        BindingOperations.Install(widget, BindWidget.TextProperty,
            new Binding("Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        root.Children.Add(widget);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var expression = BindingOperations.GetBindingExpression(widget, BindWidget.TextProperty)!;
        widget.SetValue(BindWidget.TextProperty, "committed");
        expression.UpdateSource();
        Assert.Equal("committed", vm.Selected);  // explicit flush while attached works

        widget.SetValue(BindWidget.TextProperty, "pending");
        root.Children.Remove(widget);            // detach → quiesce discards the pending edit
        host.RunUntilIdle();
        expression.UpdateSource();               // an explicit flush on a departed view…
        Assert.Equal("committed", vm.Selected);  // …is a no-op, never a phantom edit
    }

    [Fact] // the backstop: a swapped-out root left un-torn is swept at dispose (the content is unusable past it)
    public void B194_FormerRoot_SweptAtDispose()
    {
        var vm = new ChooserVm { Selected = "beta" };
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var first = new StackPanel();
        first.Children.Add(BoundChooser(vm));
        host.ShowRoot(first);
        host.RunUntilIdle();

        host.ShowRoot(new StackPanel());         // swap — REVERSIBLE: nothing torn here
        host.RunUntilIdle();
        Assert.True(vm.SubscriberCount > 0);     // the un-torn former root still pins the VM (the guide says tear eagerly)

        host.Dispose();                          // the 1c backstop sweeps still-alive former roots

        Assert.Equal("beta", vm.Selected);       // swept without phantom writes…
        Assert.Equal(0, vm.SubscriberCount);     // …and the leak is closed
    }
}
