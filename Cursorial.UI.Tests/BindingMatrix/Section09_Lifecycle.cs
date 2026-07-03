using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// Binding matrix §9 — producer lifecycle, registry, eviction &amp; the teardown sweep (B101–B120).
/// </summary>
public class Section09_Lifecycle
{
    public Section09_Lifecycle()
    {
        BindingMatrixFixture.Ensure();
        BindingDiagnostics.ResetForTests();
    }

    [Fact]
    public void B101_LocalInstall_RegisteredAndProducing()
    {
        var vm = new Vm { Name = "n" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        Assert.Same(expr, BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));
        Assert.Equal(BindingStatus.Active, expr.Status);
        Assert.Equal("n", w.Text);
    }

    [Fact]
    public void B102_ReplaceAndDispose_FirstDisposedBeforeNewInstall()
    {
        var vm = new Vm { Name = "a", Age = 7 };
        var w = new BindWidget { DataContext = vm };
        var first = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        var second = w.SetBinding(BindWidget.TextProperty, new Binding("Age"));

        Assert.True(first.IsDisposed);
        Assert.Same(second, BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));

        // The first's source handler is gone — a Name change does not deliver.
        vm.Name = "changed";
        Assert.Equal("7", w.Text);
    }

    [Fact]
    public void B106_ClearValue_EvictsLocalBinding()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        w.ClearValue(BindWidget.TextProperty);
        Assert.True(expr.IsDisposed);
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));

        // Unsubscribed: a source change does not deliver.
        vm.Name = "x";
        Assert.NotEqual("x", w.Text);
    }

    [Fact]
    public void B106a_ClearBinding_DisposesExpression_AndClearsValue()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget();

        // SetBinding (the WPF-shaped static alias) installs at LocalValue.
        var expr = BindingOperations.SetBinding(w, BindWidget.TextProperty, new Binding("Name"));
        w.DataContext = vm;
        Assert.Equal("a", w.Text);

        // ClearBinding (the WPF-shaped surface) disposes the tracked expression and runs ClearValue.
        BindingOperations.ClearBinding(w, BindWidget.TextProperty);
        Assert.True(expr.IsDisposed);
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));

        // Unsubscribed: a source change does not deliver.
        vm.Name = "x";
        Assert.NotEqual("x", w.Text);

        // Idempotent on a property with no binding.
        var record = Record.Exception(() => BindingOperations.ClearBinding(w, BindWidget.NumProperty));
        Assert.Null(record);
    }

    [Fact]
    public void B107_SetValue_TransientOverride_BindingSurvives()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        w.Text = "lv"; // plain SetValue — does NOT evict
        Assert.False(expr.IsDisposed);
        Assert.Same(expr, BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));

        vm.Name = "re";
        Assert.Equal("re", w.Text); // re-wins on the next produce
    }

    [Fact]
    public void B108_TeardownSweep_DisposesSubtreeBindings_ClosesP1Gap()
    {
        var vm = new Vm { Name = "n" };
        var root = new BindWidget { DataContext = vm };
        var paneA = new BindWidget();
        var a = new BindWidget();
        root.AddChild(paneA);
        paneA.AddChild(a);
        var expr = a.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        Assert.Equal("n", a.Text);

        root.TearDown(); // permanent detach: ValueStore.TearDown → BindingOperations.TearDown

        Assert.True(expr.IsDisposed);
        // Every INPC handler removed from vm: a later change does not deliver.
        vm.Name = "after";
        Assert.NotEqual("after", a.Text);
    }

    [Fact]
    public void B108b_TeardownSweep_DisposesInputBindingCommandBinding()
    {
        // An InputBinding's Command="{Binding}" anchors on its owner element (BD13) but is neither a visual nor
        // a logical child, so TearDown reaches it via the dedicated InputBindings sweep leg — disposing the
        // expression, its owner-side DataContext observer, and the InheritanceParentChanged subscription.
        var cmd = new StubCommand();
        var owner = new BindWidget { DataContext = new CommandVm { Cmd = cmd } };
        var kb = new KeyBinding();
        owner.InputBindings.Add(kb);
        var expr = kb.SetBinding(InputBinding.CommandProperty, new Binding("Cmd"));
        Assert.Same(cmd, kb.Command);

        owner.TearDown();
        Assert.True(expr.IsDisposed);
    }

    [Fact]
    public void B110_DirectProperty_DisposedViaRegistryLeg()
    {
        var vm = new Vm { Age = 3 };
        var root = new BindWidget { DataContext = vm };
        var a = new BindWidget();
        root.AddChild(a);
        var expr = a.SetBinding(BindWidget.DirProperty, new Binding("Age"));
        Assert.Equal(3, a.Dir);

        root.TearDown();
        Assert.True(expr.IsDisposed);

        vm.Age = 9;
        Assert.NotEqual(9, a.Dir); // unhooked via the registry leg (no store entry exists)
    }

    [Fact]
    public void B112_NotDataBindable_Throws()
    {
        var w = new BindWidget();
        var ex = Assert.Throws<ArgumentException>(() =>
            w.SetBinding(BindWidget.NdbProperty, new Binding("Age") { Source = new Vm() }));
        Assert.Contains("NotDataBindable", ex.Message);
    }

#if DEBUG
    [Fact]
    public void B114_DebugLeakTracker_WeakTargetSweep_ReportsLeak_DisposedForgotten()
    {
        BindingLeakTracker.ResetForTests();
        var vm = new Vm { Name = "a" };
        var alive = new BindWidget { DataContext = vm };
        var disposedTarget = new BindWidget { DataContext = vm };

        // Two installs with site capture; one is disposed (the contract met), one is not (a leak).
        alive.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        var disposedExpr = disposedTarget.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        disposedExpr.Dispose(); // the contract met — the tracker forgets it

        var leaks = BindingLeakTracker.Sweep();

        // The undisposed one is reported (path + install site + target description); the disposed one is not.
        Assert.Contains(leaks, l => l.Path == "Name" && l.TargetDescription.Contains("BindWidget"));
        Assert.NotEqual(string.Empty, leaks[0].InstallSite);

        GC.KeepAlive(alive);
        BindingLeakTracker.ResetForTests();
    }
#endif

    [Fact]
    public void B115_GetBindingExpression_NoBinding_ReturnsNull()
    {
        var w = new BindWidget();
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));
    }

    [Fact]
    public void B117_SourceChangeOnDisposed_NoThrow()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));
        expr.Dispose();

        // A source change after dispose must be a no-op, not a throw.
        var record = Record.Exception(() => vm.Name = "b");
        Assert.Null(record);
    }

    [Fact]
    public void B118_DoubleDispose_Idempotent()
    {
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var expr = w.SetBinding(BindWidget.TextProperty, new Binding("Name"));

        expr.Dispose();
        var record = Record.Exception(expr.Dispose);
        Assert.Null(record);
        Assert.True(expr.IsDisposed);
    }

    [Fact]
    public void B119_BindingHostState_LazyAllocated()
    {
        var w = new BindWidget();
        Assert.Null(w.BindingHostState); // never bound ⇒ null slot

        w.SetBinding(BindWidget.TextProperty, new Binding("Name") { Source = new Vm() });
        Assert.NotNull(w.BindingHostState); // allocated on first install
    }

    [Fact]
    public void B103_FrameHosted_NotLocalValueLane()
    {
        var vm = new Vm { Name = "framed" };
        var w = new BindWidget { DataContext = vm };
        var frame = new TestFrame();
        w.AddFrame(frame);

        var expr = BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Name"), frame);
        Assert.Equal("framed", w.Text);
        // Frame-hosted ⇒ GetBindingExpression returns null (BD7); the lane is FrameHosted.
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty));
        Assert.Equal(BindingStatus.Active, expr.Status);
    }

    [Fact]
    public void B104_TwoFrameHosted_BothLive_FramesStack()
    {
        var vm = new Vm { Name = "low", Age = 9 };
        var w = new BindWidget { DataContext = vm };
        var weak = new TestFrame(sortKey: 1);
        var strong = new TestFrame(sortKey: 10);
        w.AddFrame(weak);
        w.AddFrame(strong);

        // Two frame-hosted bindings for the same property — exempt from replace-and-dispose.
        var weakExpr = BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Name"), weak);
        var strongExpr = BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Age"), strong);

        Assert.False(weakExpr.IsDisposed);
        Assert.False(strongExpr.IsDisposed); // both live (frames stack)
        Assert.Equal("9", w.Text);           // the stronger key wins in the store

        // Each evicts on its own frame's retraction.
        w.RemoveFrame(strong);
        Assert.True(strongExpr.IsDisposed);
        Assert.False(weakExpr.IsDisposed);
        Assert.Equal("low", w.Text); // the store promotes the surviving frame
    }

    [Fact]
    public void B109_TeardownSweep_BottomUpOrder()
    {
        var vm = new Vm { Name = "n" };
        var root = new BindWidget { DataContext = vm };
        var paneA = new BindWidget();
        var a = new BindWidget();
        root.AddChild(paneA);
        paneA.AddChild(a);

        var order = new List<string>();
        var rootExpr = new RecordingExpressionProbe(root, "root", order);
        var paneExpr = new RecordingExpressionProbe(paneA, "paneA", order);
        var leafExpr = new RecordingExpressionProbe(a, "a", order);
        GC.KeepAlive(rootExpr);
        GC.KeepAlive(paneExpr);
        GC.KeepAlive(leafExpr);

        root.TearDown(); // bottom-up per element (children before parents), mirroring store S155 order

        Assert.Equal(new[] { "a", "paneA", "root" }, order);
    }

    [Fact]
    public void B111_DirectProperty_NonElement_CallerOwned_NoSweep()
    {
        var vm = new Vm { Age = 3 };
        var holder = new DirectHolder();

        // A DirectProperty target on a non-element UIObject: no tree lifecycle exists, so no sweep
        // runs. The binding is caller-owned (must be disposed explicitly).
        var expr = BindingOperations.Install(holder, DirectHolder.DirProperty, new Binding("Age") { Source = vm });
        Assert.Equal(3, holder.Dir);
        Assert.Null(BindingOperations.GetBindingExpression(holder, DirectHolder.DirProperty)); // DirectProperty ⇒ null (BD7)

        // No automatic teardown — only an explicit dispose stops it.
        expr.Dispose();
        vm.Age = 9;
        Assert.NotEqual(9, holder.Dir);
    }

    [Fact]
    public void B113_ReadOnlyUIProperty_BindingProducer_RejectedByPD14()
    {
        // DEV-corrected (matrix M209): the producer mouth (Bind/BindInFrame/BeginAnimation) enforces
        // PD14 — a read-only property is writable ONLY through its UIPropertyKey, never through a
        // binding producer. The S1 entry-mouth admission is the oracle.
        var vm = new Vm { Age = 7 };
        var w = new BindWidget { DataContext = vm };

        Assert.Throws<InvalidOperationException>(() =>
            w.SetBinding(BindWidget.RoProperty, new Binding("Age") { Mode = BindingMode.OneWay }));
    }

    [Fact]
    public void B116_FrameEvictedDuringProduce_IdempotentEvictionDispose_NoThrow()
    {
        // A source change drives a push; if the host frame is evicted mid-flight, OnEvicted →
        // Dispose(fromEviction:true) must be idempotent and not double-dispose the entry or NRE.
        var vm = new Vm { Name = "a" };
        var w = new BindWidget { DataContext = vm };
        var frame = new TestFrame();
        w.AddFrame(frame);
        var expr = BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Name"), frame);

        // Evict during a source-change-driven produce: subscribe to the source's change and remove the
        // frame from within (simulating a reentrant retraction).
        vm.PropertyChanged += (_, _) =>
        {
            if (!expr.IsDisposed)
                w.RemoveFrame(frame);
        };

        var record = Record.Exception(() => vm.Name = "b");
        Assert.Null(record);
        Assert.True(expr.IsDisposed);
        // A double dispose is still a no-op.
        Assert.Null(Record.Exception(expr.Dispose));
    }

    [Fact]
    public void B105_FrameHosted_EvictedOnFrameRemoval()
    {
        var vm = new Vm { Name = "framed" };
        var w = new BindWidget { DataContext = vm };
        var frame = new TestFrame();
        w.AddFrame(frame);

        var expr = BindingOperations.Install(w, BindWidget.TextProperty, new Binding("Name"), frame);
        Assert.Equal("framed", w.Text);
        Assert.Null(BindingOperations.GetBindingExpression(w, BindWidget.TextProperty)); // frame-hosted ⇒ not LocalValue lane

        w.RemoveFrame(frame); // cookie retraction → eviction → dispose
        Assert.True(expr.IsDisposed);
        vm.Name = "after";
        Assert.NotEqual("after", w.Text);
    }
}

/// <summary>A minimal installable <see cref="ValueFrame"/> (no declared setters; binding entries only) for the frame-hosted rows.</summary>
public sealed class TestFrame(int sortKey = 0) : ValueFrame(new StyleSortKey((ulong)sortKey))
{
    public override int EntryCount => 0;

    public override IValueEntry GetEntry(int index) => throw new ArgumentOutOfRangeException(nameof(index));
}

/// <summary>
/// A <see cref="BindingExpressionBase"/> probe that records its disposal into a shared list, used to
/// observe the bottom-up teardown-sweep order (B109). Self-registers in its target's registry on
/// construction so the sweep finds it.
/// </summary>
public sealed class RecordingExpressionProbe : BindingExpressionBase
{
    private readonly UIObject _target;
    private readonly string _label;
    private readonly List<string> _order;

    public RecordingExpressionProbe(UIObject target, string label, List<string> order)
    {
        _target = target;
        _label = label;
        _order = order;
        Status = BindingStatus.Active;
        BindingRegistry.GetOrCreate(target).Register(this);
    }

    public override BindingBase ParentBinding => throw new NotSupportedException();

    public override UIObject Target => _target;

    public override UIProperty TargetProperty => UIProperty.UnsetTargetProperty;

    public override BindingMode EffectiveMode => BindingMode.OneWay;

    internal override BindingLane Lane => BindingLane.WatchOnly;

    public override void UpdateSource()
    {
    }

    public override void UpdateTarget()
    {
    }

    internal override BindingExpressionExplanation Explain() =>
        new(Lane, "probe", Status, EffectiveMode, _label, null, BindingFailureKind.None);

    private protected override void DisposeCore(bool fromEviction)
    {
        _order.Add(_label);
        BindingRegistry.Get(_target)?.Unregister(this);
    }
}

/// <summary>A non-element <see cref="UIObject"/> with a <see cref="DirectProperty{TOwner,T}"/> (B111).</summary>
public sealed class DirectHolder : UIObject
{
    private int _dir;

    public static readonly DirectProperty<DirectHolder, int> DirProperty =
        UIProperty.RegisterDirect<DirectHolder, int>("Dir", static h => h._dir, static (h, v) => h._dir = v);

    public int Dir
    {
        get => _dir;
        set => SetAndRaise(DirProperty, ref _dir, value);
    }
}
