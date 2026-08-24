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

    [Fact] // audit: the mid-detach ElementName re-anchor must not re-fire the OWTS activation write
    public void B195_ElementNameOwts_DetachReanchor_NoPhantomActivationWrite()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        var a = new BindWidget { Num = 111 };
        var b = new BindWidget();
        root.Children.Add(a);
        root.Children.Add(b);

        var scope = new NameScopeDictionary();
        scope.Register("editor", b);
        NameScope.SetNameScope(root, scope);

        host.ShowRoot(root);
        host.RunUntilIdle();

        BindingOperations.Install(a, BindWidget.NumProperty,
            new Binding("Num") { ElementName = "editor", Mode = BindingMode.OneWayToSource });
        Assert.Equal(111, b.Num);                // activation pushed target → named source

        b.Num = 222;                             // the source moves on (OWTS never flows source → target)

        host.ShowRoot(new StackPanel());         // swap the root out — the departing walk re-anchors
        host.RunUntilIdle();                     // ElementName through the still-intact LOGICAL tree

        Assert.Equal(222, b.Num);                // same-root re-anchor SKIPPED — no phantom 111 write
    }

    [Fact] // audit: a quiesce-swallowed OWTS activation write is DEFERRED, not dropped — re-host replays it
    public void B196_OwtsRehost_ActivationWriteReplaysOnReattach()
    {
        var vm1 = new ChooserVm();
        var vm2 = new ChooserVm();
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        var p1 = new StackPanel { DataContext = vm1 };
        var p2 = new StackPanel { DataContext = vm2 };
        root.Children.Add(p1);
        root.Children.Add(p2);

        var widget = new BindWidget();
        BindingOperations.Install(widget, BindWidget.TextProperty,
            new Binding("Selected") { Mode = BindingMode.OneWayToSource });
        widget.SetValue(BindWidget.TextProperty, "X");
        p1.Children.Add(widget);
        host.ShowRoot(root);
        host.RunUntilIdle();
        Assert.Equal("X", vm1.Selected);         // activation pushed into the first host's VM

        p1.Children.Remove(widget);              // depart → quiesce
        host.RunUntilIdle();
        widget.SetValue(BindWidget.TextProperty, "Y");
        Assert.Equal("X", vm1.Selected);         // no write while departed

        p2.Children.Add(widget);                 // re-host — the re-anchor may run at parenting time,
        host.RunUntilIdle();                     // BEFORE the attach walk's resume; the replay covers it

        Assert.Equal("Y", vm2.Selected);         // the re-hosted view activation-pushed into the NEW VM
        Assert.Equal("X", vm1.Selected);         // …and never wrote back into the old one
    }

    [Fact] // audit: a binding installed on a DEPARTED element is born quiesced — no write-back resurrection
    public void B197_InstallWhileDeparted_BornQuiesced_ReattachRearms()
    {
        var vm = new ChooserVm { Selected = "seed" };
        var widget = new BindWidget { DataContext = vm };
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        root.Children.Add(widget);
        host.ShowRoot(root);
        host.RunUntilIdle();

        root.Children.Remove(widget);            // departed (attach → detach)
        host.RunUntilIdle();

        BindingOperations.Install(widget, BindWidget.TextProperty,
            new Binding("Selected") { Mode = BindingMode.TwoWay });
        widget.SetValue(BindWidget.TextProperty, "fresh-while-detached");
        Assert.Equal("seed", vm.Selected);       // the fresh install inherited the departed quiesce

        BindingOperations.Install(widget, BindWidget.TextProperty,
            new Binding("Selected") { Mode = BindingMode.OneWayToSource });
        Assert.Equal("seed", vm.Selected);       // an OWTS install's activation write defers too

        root.Children.Add(widget);               // re-attach → resume replays the deferred activation
        host.RunUntilIdle();
        Assert.Equal("fresh-while-detached", vm.Selected);
    }

    [Fact] // audit: Clear's cross-sibling gap — an earlier-severed sibling must not phantom-write through a later one
    public void B198_ChildrenClear_CrossSiblingCascade_NoPhantomWrite()
    {
        var vm = new ChooserVm { Selected = "beta" };
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var panel = new StackPanel { DataContext = vm };
        var chooser = new ListBox();             // index 0 — the DEPENDENT sits below its provider,
        var provider = new Border();             // index 1 — so Clear's backward loop severs the provider first
        panel.Children.Add(chooser);
        panel.Children.Add(provider);
        BindingOperations.Install(chooser, ItemsControl.ItemsSourceProperty,
            new Binding("DataContext.Items") { Source = provider });
        BindingOperations.Install(chooser, SelectingItemsControl.SelectedItemProperty,
            new Binding("Selected") { Mode = BindingMode.TwoWay });

        host.ShowRoot(panel);
        host.RunUntilIdle();
        Assert.Equal("beta", chooser.SelectedItem); // forward sync sanity

        panel.Children.Clear();                  // batch removal: EVERY child is departing —
        host.RunUntilIdle();                     // the pre-pass quiesces all subtrees before the first disown

        Assert.Equal("beta", vm.Selected);       // the provider's severance never round-tripped through the chooser
    }

    [Fact] // audit: the documented teardown-then-detach PUBLIC pattern must not throw (PD21 tolerance)
    public void B199_TearDownThenDetach_PublicPattern_NoThrow()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new StackPanel();
        var list = new ListBox { ItemsSource = new List<string> { "alpha", "beta" } };
        root.Children.Add(list);                 // a real styled control (control themes install style frames)
        host.ShowRoot(root);
        host.RunUntilIdle();

        list.TearDown();                         // the documented order: permanent-discard sweep first…
        root.Children.Remove(list);              // …then detach — the walk must tolerate torn elements
        host.RunUntilIdle();

        Assert.False(list.IsAttachedToTree);     // the detach walk ran to completion
        Assert.Null(list.VisualParent);
    }
}
