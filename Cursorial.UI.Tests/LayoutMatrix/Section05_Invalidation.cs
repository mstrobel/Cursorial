using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>L76: a custom element registering effects in its static ctor — one flagged property, one unflagged.</summary>
public class EffectsProbe : Probe
{
    public static readonly StyledProperty<int> FlaggedProperty =
        UIProperty.Register<EffectsProbe, int>("Section05Flagged");

    public static readonly StyledProperty<int> UnflaggedProperty =
        UIProperty.Register<EffectsProbe, int>("Section05Unflagged");

    static EffectsProbe()
    {
        AffectsMeasure<EffectsProbe>(FlaggedProperty);
    }

    public EffectsProbe(int columns, int rows) : base(columns, rows)
    {
    }
}

/// <summary>L81: a container type with an inheriting styled property registered <c>[AffectsMeasure]</c>.</summary>
public class InheritHost : Host
{
    public static readonly StyledProperty<int> InheritedProperty =
        UIProperty.Register<InheritHost, int>("Section05Inherited", inherits: true);

    static InheritHost()
    {
        AffectsMeasure<InheritHost>(InheritedProperty);
    }
}

/// <summary>Layout matrix §5 — invalidation routing &amp; <c>LayoutManager</c> (L71–L94).</summary>
public class Section05_Invalidation
{
    private static (LayoutManager Manager, Host Root, Host Mid, Probe Leaf) CleanThreeLevelTree()
    {
        var root = new Host { LogName = "root" };
        var mid = new Host { LogName = "mid" };
        var leaf = new Probe(4, 2) { LogName = "leaf" };
        root.Add(mid);
        mid.Add(leaf);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);
        return (manager, root, mid, leaf);
    }

    [Fact]
    public void L71_AffectsMeasure_OnLeaf_InvalidatesLeafAndEveryAncestor()
    {
        var (manager, root, mid, leaf) = CleanThreeLevelTree();

        leaf.Width = 6;

        Assert.False(leaf.IsMeasureValid);
        Assert.False(mid.IsMeasureValid);
        Assert.False(root.IsMeasureValid);
        Assert.True(manager.HasPendingWork);
    }

    [Fact]
    public void L72_SetToCurrentValue_EqualityGated_NothingInvalidated()
    {
        var (manager, root, _, leaf) = CleanThreeLevelTree();
        leaf.Width = 6;
        manager.Layout(20, 10);

        leaf.Width = 6; // current value — gated at the property engine

        Assert.True(leaf.IsMeasureValid);
        Assert.True(root.IsMeasureValid);
        Assert.False(manager.HasPendingWork);
    }

    [Fact]
    public void L73_AffectsArrange_SelfOnly_MeasureAndAncestorsUntouched()
    {
        var (manager, root, mid, leaf) = CleanThreeLevelTree();

        leaf.HorizontalAlignment = HorizontalAlignment.Right;

        Assert.False(leaf.IsArrangeValid);
        Assert.True(leaf.IsMeasureValid);
        Assert.True(mid.IsMeasureValid);
        Assert.True(mid.IsArrangeValid);
        Assert.True(root.IsArrangeValid);
        Assert.True(manager.HasPendingWork);
    }

    [Fact]
    public void L74_TwoDirtyLeaves_OneLayout_EachMeasureOverrideRunsAtMostOnce()
    {
        var root = new Host();
        var a = new Probe(4, 2);
        var b = new Probe(4, 2);
        root.Add(a);
        root.Add(b);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        a.InvalidateMeasure();
        b.InvalidateMeasure();
        var rootBefore = root.MeasureOverrideCount;
        var aBefore = a.MeasureOverrideCount;
        var bBefore = b.MeasureOverrideCount;

        manager.Layout(20, 10);

        Assert.Equal(rootBefore + 1, root.MeasureOverrideCount);
        Assert.Equal(aBefore + 1, a.MeasureOverrideCount);
        Assert.Equal(bBefore + 1, b.MeasureOverrideCount);
    }

    [Fact]
    public void L75_DoubleInvalidate_EnqueuedBitIdempotence_OneMeasure()
    {
        var (manager, _, _, leaf) = CleanThreeLevelTree();

        leaf.InvalidateMeasure();
        leaf.InvalidateMeasure();
        var before = leaf.MeasureOverrideCount;
        manager.Layout(20, 10);

        Assert.Equal(before + 1, leaf.MeasureOverrideCount);
    }

    [Fact]
    public void L76_AffectsSugar_FlaggedInvalidates_UnflaggedDoesNot()
    {
        var probe = new EffectsProbe(4, 2);
        var manager = LayoutFixture.CreateRoot(probe);
        manager.Layout(20, 10);

        probe.SetValue(EffectsProbe.UnflaggedProperty, 7);
        Assert.True(probe.IsMeasureValid);

        probe.SetValue(EffectsProbe.FlaggedProperty, 7);
        Assert.False(probe.IsMeasureValid);
    }

    [Fact]
    public void L77_SetDock_InvalidatesParentMeasure_ChildStaysValid()
    {
        var panel = new DockPanel();
        var child = new Probe(5, 3);
        var fill = new Probe(2, 2);
        panel.Children.Add(child);
        panel.Children.Add(fill);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        DockPanel.SetDock(child, Dock.Right);

        Assert.False(panel.IsMeasureValid);
        Assert.True(child.IsMeasureValid); // re-measures only because the parent hands a new constraint
    }

    [Fact]
    public void L78_AttachedEffects_RideTheGlobalLane_AfterHostTypeTableFroze()
    {
        // Freeze the effects resolution for the host type on an unrelated property FIRST — the
        // per-type lane for Probe can no longer change for that property; DockProperty's effects
        // still fire because GetEffects merges the property-global lane (doc §5.5 two-lane contract).
        _ = UIElement.WidthProperty.GetEffects(typeof(Probe));

        var panel = new DockPanel();
        var probe = new Probe(5, 3);
        var fill = new Probe(2, 2);
        panel.Children.Add(probe);
        panel.Children.Add(fill);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        DockPanel.SetDock(probe, Dock.Bottom);

        Assert.False(panel.IsMeasureValid); // without the global lane this write would invalidate nothing
        Assert.True(
            (DockPanel.DockProperty.GetEffects(typeof(Probe)) & PropertyEffects.AffectsParentMeasure) != 0);
    }

    [Fact]
    public void L79_CanvasSetLeft_ParentArrangeOnly_NoMeasureWorkAnywhere()
    {
        var canvas = new Canvas();
        var child = new Probe(5, 4);
        canvas.Children.Add(child);
        var manager = LayoutFixture.CreateRoot(canvas);
        manager.Layout(20, 10);

        Canvas.SetLeft(child, 7);

        Assert.False(canvas.IsArrangeValid);
        Assert.True(canvas.IsMeasureValid);
        Assert.True(child.IsMeasureValid);
        Assert.True(child.IsArrangeValid); // the child re-arranges only because the parent hands a new rect
    }

    [Fact]
    public void L80_SetDock_OnDetachedElement_NoThrow_NoInvalidation()
    {
        var element = new Probe(4, 2);
        DockPanel.SetDock(element, Dock.Top); // VisualParent?. routing
        Assert.Equal(Dock.Top, DockPanel.GetDock(element));
    }

    [Fact]
    public void L81_InheritingAffectsMeasure_RootWrite_InvalidatesEntrylessDescendants()
    {
        var root = new InheritHost();
        var mid = new InheritHost();
        var leaf = new InheritHost();
        root.Add(mid);
        mid.Add(leaf);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        root.SetValue(InheritHost.InheritedProperty, 42); // descendants are entry-less

        Assert.False(root.IsMeasureValid);
        Assert.False(mid.IsMeasureValid);  // via the OnInheritedPropertyChanged carrier
        Assert.False(leaf.IsMeasureValid);
    }

    [Fact]
    public void L82_ChildrenAdd_InvalidatesPanelMeasure_AttachesChild()
    {
        var panel = new StackPanel();
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        var probe = new Probe(4, 2);
        panel.Children.Add(probe);

        Assert.False(panel.IsMeasureValid);
        Assert.Same(panel, probe.VisualParent);
        Assert.Same(panel, probe.LogicalParent);
        Assert.True(probe.IsAttachedToTree);
    }

    [Fact]
    public void L83_ChildrenRemove_InvalidatesPanelMeasure_DetachesChild()
    {
        var panel = new StackPanel();
        var probe = new Probe(4, 2);
        panel.Children.Add(probe);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        panel.Children.Remove(probe);

        Assert.False(panel.IsMeasureValid);
        Assert.Null(probe.VisualParent);
        Assert.Null(probe.LogicalParent);
        Assert.False(probe.IsAttachedToTree);
    }

    [Fact]
    public void L84_UIElementCollection_Throws_OnNullDuplicateOrAttachedElsewhere()
    {
        var panel = new StackPanel();
        var other = new StackPanel();
        var child = new Probe(4, 2);
        other.Children.Add(child);

        Assert.Throws<ArgumentNullException>(() => panel.Children.Add(null!));
        Assert.Throws<InvalidOperationException>(() => panel.Children.Add(child)); // attached elsewhere

        var own = new Probe(4, 2);
        panel.Children.Add(own);
        Assert.Throws<InvalidOperationException>(() => panel.Children.Add(own)); // same child twice
    }

    [Fact]
    public void L85_ShallowestFirst_ParentMeasureOverrideRunsBeforeChild()
    {
        var log = new List<string>();
        var root = new Host { Log = log, LogName = "root" };
        var leaf = new Probe(4, 2) { Log = log, LogName = "leaf" };
        root.Add(leaf);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        leaf.InvalidateMeasure(); // dirties leaf and root
        log.Clear();
        manager.Layout(20, 10);

        var rootIndex = log.IndexOf("measure:root");
        var leafIndex = log.IndexOf("measure:leaf");
        Assert.True(rootIndex >= 0 && leafIndex >= 0 && rootIndex < leafIndex);
    }

    [Fact]
    public void L86_SiblingWriteDuringMeasure_SameTickFixpoint()
    {
        var root = new Host();
        var a = new Probe(4, 2);
        var b = new Probe(4, 2);
        root.Add(a);
        root.Add(b);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        var fired = false;
        a.OnMeasuring = _ =>
        {
            if (!fired)
            {
                fired = true;
                b.Width = 3; // one-shot sibling write
            }
        };
        a.InvalidateMeasure();
        var bBefore = b.MeasureOverrideCount;

        manager.Layout(20, 10);

        Assert.True(b.MeasureOverrideCount > bBefore); // B re-measured within the same pass
        Assert.Equal(3, b.DesiredSize.Columns);
        Assert.False(manager.HasPendingWork);
    }

    [Fact]
    public void L87_Oscillator_TerminatesAtPassCap_WithCycleDiagnostic()
    {
        var root = new Host();
        var oscillator = new Probe(4, 2) { OnMeasuring = static e => e.InvalidateMeasure() };
        root.Add(oscillator);
        var manager = LayoutFixture.CreateRoot(root);

        var diagnostics = LayoutFixture.CaptureDiagnostics(manager, () => manager.Layout(20, 10)); // must not hang

        Assert.True(manager.HasPendingWork); // residual work slips
#if DEBUG
        Assert.Contains(diagnostics, d => d.Kind == LayoutDiagnosticKind.LayoutCycle && d.Element is not null);
#endif
    }

    [Fact]
    public void L88_LayoutUpdated_RunsAfterThePass_WithLayoutValid()
    {
        var probe = new Probe(4, 2);
        var manager = LayoutFixture.CreateRoot(probe);

        var raised = 0;
        var pendingAtRaise = true;
        manager.LayoutUpdated += () =>
        {
            raised++;
            pendingAtRaise = manager.HasPendingWork;
        };

        manager.Layout(20, 10);

        Assert.True(raised >= 1);
        Assert.False(pendingAtRaise);
        Assert.True(probe.IsMeasureValid);
    }

    [Fact]
    public void L89_LayoutUpdated_DirtyingHandler_OneBoundedRerun_PerpetualDirtierSlips()
    {
        // First-raise-only dirtier: exactly one bounded same-tick re-run.
        var probe = new Probe(4, 2);
        var manager = LayoutFixture.CreateRoot(probe);
        manager.Layout(20, 10);

        var observations = new List<bool>();
        var first = true;
        manager.LayoutUpdated += () =>
        {
            observations.Add(probe.IsMeasureValid); // observes valid layout at each raise
            if (first)
            {
                first = false;
                probe.InvalidateMeasure();
            }
        };
        probe.InvalidateMeasure();
        manager.Layout(20, 10);

        Assert.Equal(2, observations.Count); // handler observed valid layout twice
        Assert.All(observations, Assert.True);
        Assert.False(manager.HasPendingWork);

        // Perpetual dirtier: degrades to one-frame lag with a diagnostic.
        var probe2 = new Probe(4, 2);
        var manager2 = LayoutFixture.CreateRoot(probe2);
        manager2.Layout(20, 10);
        manager2.LayoutUpdated += () => probe2.InvalidateMeasure();
        probe2.InvalidateMeasure();

        var diagnostics = LayoutFixture.CaptureDiagnostics(manager2, () => manager2.Layout(20, 10));

        Assert.True(manager2.HasPendingWork); // slips to the next tick
#if DEBUG
        Assert.Contains(diagnostics, d => d.Kind == LayoutDiagnosticKind.ResidualLayoutWork);
#endif
    }

    [Fact]
    public void L90_FullyValidTree_Layout_ZeroOverrideCalls()
    {
        var (manager, root, mid, leaf) = CleanThreeLevelTree();
        var counts = (root.MeasureOverrideCount, root.ArrangeOverrideCount,
                      mid.MeasureOverrideCount, mid.ArrangeOverrideCount,
                      leaf.MeasureOverrideCount, leaf.ArrangeOverrideCount);

        manager.Layout(20, 10);

        Assert.Equal(counts, (root.MeasureOverrideCount, root.ArrangeOverrideCount,
                              mid.MeasureOverrideCount, mid.ArrangeOverrideCount,
                              leaf.MeasureOverrideCount, leaf.ArrangeOverrideCount));
    }

    [Fact]
    public void L91_CleanLayoutPass_AllocatesZeroBytes()
    {
        var (manager, _, _, _) = CleanThreeLevelTree();
        var size = new Size(20, 10);

        // Warm: settle any lazy/tiering allocation on the exact code path.
        for (var i = 0; i < 5; i++)
            manager.RunLayoutPass(size);

        var before = GC.GetAllocatedBytesForCurrentThread();
        manager.RunLayoutPass(size);
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
    }

    [Fact]
    public void L92_LeafRemeasuresToSameDesired_NoParentCascade()
    {
        var (manager, root, mid, leaf) = CleanThreeLevelTree();
        var rootCounts = (root.MeasureOverrideCount, root.ArrangeOverrideCount);
        var midCounts = (mid.MeasureOverrideCount, mid.ArrangeOverrideCount);

        leaf.Measure(new Size(21, 10)); // constraint-busting knob; rigid Probe → same desired
        manager.Layout(20, 10);

        Assert.Equal(rootCounts, (root.MeasureOverrideCount, root.ArrangeOverrideCount));
        Assert.Equal(midCounts, (mid.MeasureOverrideCount, mid.ArrangeOverrideCount));
    }

    [Fact]
    public void L93_ArrangeInvalidOnly_RearrangedWithCachedRect_ParentArrangeNotRerun()
    {
        var (manager, root, mid, leaf) = CleanThreeLevelTree();
        leaf.HorizontalAlignment = HorizontalAlignment.Right; // L73's state: leaf arrange-invalid only
        var rootArrange = root.ArrangeOverrideCount;
        var midArrange = mid.ArrangeOverrideCount;

        manager.Layout(20, 10);

        Assert.True(leaf.IsArrangeValid);
        Assert.Equal(16, leaf.Bounds.Column); // re-arranged inside the cached 20-wide slot: 20 − 4
        Assert.Equal(rootArrange, root.ArrangeOverrideCount);
        Assert.Equal(midArrange, mid.ArrangeOverrideCount);
    }

    [Fact]
    public void L94_SingleRootContract_RootMeasuredAndArrangedToConstraint()
    {
        var root = new Probe(4, 2);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(40, 12);

        Assert.Equal(new Size(40, 12), Assert.Single(root.MeasureConstraints));
        Assert.Equal(new Rect(0, 0, 40, 12), root.Bounds); // Stretch root fills (0,0,W×H)
    }
}
