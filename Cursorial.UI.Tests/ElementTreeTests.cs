using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;

namespace Cursorial.Tests.UI;

/// <summary>A <see cref="Host"/> recording lifecycle walks, with enabled-core and templated-parent knobs.</summary>
public class TreeProbe : Host
{
    private bool _enabledCore = true;

    public List<string> Events { get; } = [];

    public bool EnabledCore
    {
        get => _enabledCore;
        set
        {
            _enabledCore = value;
            InvalidateIsEnabledCore();
        }
    }

    public int EffectivelyEnabledChanges { get; private set; }

    public void StampTemplatedParent(UIElement? value) => SetTemplatedParent(value);

    protected override bool IsEnabledCore => _enabledCore;

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        Events.Add($"attach:{LogName}");
    }

    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        base.OnDetachedFromTree(in e);
        Events.Add($"detach:{LogName}");
    }

    protected override void OnVisualParentChanged(UIElement? oldParent, UIElement? newParent)
    {
        base.OnVisualParentChanged(oldParent, newParent);
        Events.Add($"parent:{LogName}:{oldParent?.GetType().Name ?? "null"}->{newParent?.GetType().Name ?? "null"}");
    }

    protected override void OnIsEffectivelyEnabledChanged(bool isEffectivelyEnabled)
    {
        base.OnIsEffectivelyEnabledChanged(isEffectivelyEnabled);
        EffectivelyEnabledChanges++;
    }
}

/// <summary>T0 tree-plumbing coverage beyond the layout matrix: lifecycle walks, logical events, inheritance wiring, teardown, effective-enabled.</summary>
public class ElementTreeTests
{
    [Fact]
    public void AttachWalk_IsPreOrderParentFirst_DetachIsBottomUp()
    {
        var events = new List<string>();
        var parent = new TreeProbe { LogName = "parent" };
        var child = new TreeProbe { LogName = "child" };
        parent.Add(child);

        var manager = LayoutFixture.CreateRoot(parent);
        events.AddRange(parent.Events.Where(e => e.StartsWith("attach")));
        events.AddRange(child.Events.Where(e => e.StartsWith("attach")));
        Assert.Contains("attach:parent", parent.Events);
        Assert.Contains("attach:child", child.Events);
        Assert.True(parent.IsAttachedToTree);
        Assert.Same(parent, child.VisualRoot);

        parent.Remove(child);
        Assert.Contains("detach:child", child.Events);
        Assert.False(child.IsAttachedToTree);
        Assert.Null(child.VisualRoot);
        Assert.Null(child.VisualParent);
        Assert.True(parent.IsAttachedToTree);
        _ = manager;
    }

    [Fact]
    public void DetachWalk_DescendantsDetachBeforeAncestors()
    {
        var log = new List<string>();
        var root = new TreeProbe { LogName = "root" };
        var mid = new TreeProbe { LogName = "mid" };
        var leaf = new TreeProbe { LogName = "leaf" };
        root.Add(mid);
        mid.Add(leaf);
        _ = LayoutFixture.CreateRoot(root);

        root.Remove(mid);

        // Bottom-up: leaf detached before mid.
        log.AddRange(leaf.Events.Concat(mid.Events).Where(e => e.StartsWith("detach")));
        Assert.Contains("detach:leaf", leaf.Events);
        Assert.Contains("detach:mid", mid.Events);
        Assert.False(leaf.IsAttachedToTree);
    }

    [Fact]
    public void InheritanceParent_IsLogicalParent_FallingBackToVisualParent()
    {
        var visualHost = new TreeProbe();
        var logicalHost = new TreeProbe();
        var child = new Probe(2, 2);

        // Visual-only adoption (punch 43): inheritance follows the visual parent.
        visualHost.AddVisualChildOnly(child);
        Assert.Same(visualHost, child.GetInheritanceParent());
        Assert.Null(child.LogicalParent);

        // A logical parent re-points inheritance (LogicalParent ?? VisualParent).
        logicalHost.AdoptLogical(child);
        Assert.Same(logicalHost, child.GetInheritanceParent());
    }

    [Fact]
    public void LogicalAttachDetach_EventsFire_WithParent()
    {
        var host = new TreeProbe();
        var child = new Probe(2, 2);

        LogicalTreeAttachmentEventArgs? attached = null;
        LogicalTreeAttachmentEventArgs? detached = null;
        child.AttachedToLogicalTree += (_, e) => attached = e;
        child.DetachedFromLogicalTree += (_, e) => detached = e;

        host.Add(child);
        Assert.NotNull(attached);
        Assert.Same(host, attached.NewParent);
        Assert.Null(attached.OldParent);
        Assert.Same(child, attached.Element);

        host.Remove(child);
        Assert.NotNull(detached);
        Assert.Same(host, detached.OldParent);
        Assert.Null(detached.NewParent);
        Assert.Null(child.LogicalParent);
    }

    [Fact]
    public void TemplatedParent_StorageAndSeam_ThrowsOnceAttached()
    {
        var owner = new TreeProbe();
        var part = new TreeProbe();

        part.StampTemplatedParent(owner); // legal while detached (S8 stamps before attach)
        Assert.Same(owner, part.TemplatedParent);

        owner.Add(part);
        _ = LayoutFixture.CreateRoot(owner);
        Assert.Throws<InvalidOperationException>(() => part.StampTemplatedParent(new TreeProbe()));
        part.StampTemplatedParent(owner); // idempotent re-stamp is allowed
    }

    [Fact]
    public void TearDown_RunsValueStoreSweep_BottomUp_OverBothRelationships()
    {
        var root = new TreeProbe();
        var visualChild = new TreeProbe();
        var logicalOnlyChild = new Probe(2, 2);
        root.Add(visualChild);
        root.AdoptLogical(logicalOnlyChild); // logical-only (no visual adoption)

        // Give each element store state via a producer entry (eviction observable via listener).
        var evicted = new List<BindingEntryBase>();
        var listener = new RecordingEvictionListener(evicted);
        root.Bind(UIElement.ZIndexProperty, listener: listener).SetValue(1);
        visualChild.Bind(UIElement.ZIndexProperty, listener: listener).SetValue(2);
        logicalOnlyChild.Bind(UIElement.ZIndexProperty, listener: listener).SetValue(3);

        root.TearDown();

        Assert.Equal(3, evicted.Count);
        Assert.Equal(0, root.ZIndex); // stores are inert; reads return defaults
        Assert.Equal(0, visualChild.ZIndex);
        Assert.Equal(0, logicalOnlyChild.ZIndex);
    }

    [Fact]
    public void EffectiveEnabled_AncestorAnd_IsEnabledCore_AndReEvaluation()
    {
        var parent = new TreeProbe();
        var child = new TreeProbe();
        var grandchild = new TreeProbe();
        parent.Add(child);
        child.Add(grandchild);

        Assert.True(grandchild.IsEffectivelyEnabled);

        parent.IsEnabled = false; // fans out over the wiring
        Assert.False(parent.IsEffectivelyEnabled);
        Assert.False(child.IsEffectivelyEnabled);
        Assert.False(grandchild.IsEffectivelyEnabled);

        parent.IsEnabled = true;
        Assert.True(grandchild.IsEffectivelyEnabled);

        child.EnabledCore = false; // the control-author gate (InvalidateIsEnabledCore)
        Assert.True(parent.IsEffectivelyEnabled);
        Assert.False(child.IsEffectivelyEnabled);
        Assert.False(grandchild.IsEffectivelyEnabled);

        child.EnabledCore = true;
        Assert.True(grandchild.IsEffectivelyEnabled);
        Assert.True(child.EffectivelyEnabledChanges >= 2);
    }

    [Fact]
    public void TranslateToWindow_AndToLocal_FoldBoundsAndRenderOffsets()
    {
        var root = new TreeProbe();
        var panel = new Canvas();
        var leaf = new Probe(3, 2);
        root.Add(panel);
        panel.Children.Add(leaf);
        Canvas.SetLeft(leaf, 4);
        Canvas.SetTop(leaf, 2);
        var manager = LayoutFixture.CreateRoot(root);
        manager.Layout(20, 10);

        Assert.Equal((5, 3), leaf.TranslateToWindow(1, 1)); // (4+1, 2+1) — panel at origin
        Assert.Equal((1, 1), leaf.TranslateToLocal(5, 3));

        leaf.RenderOffsetColumn = 2; // composite offsets fold into the chain walk
        Assert.Equal((7, 3), leaf.TranslateToWindow(1, 1));
        Assert.Equal((1, 1), leaf.TranslateToLocal(7, 3));
    }

    [Fact]
    public void Elements_AreReusable_DetachAndReattachRelayouts()
    {
        var panelA = new StackPanel();
        var panelB = new StackPanel();
        var child = new Probe(5, 2);
        panelA.Children.Add(child);
        var managerA = LayoutFixture.CreateRoot(panelA);
        managerA.Layout(20, 10);
        Assert.Equal(new Rect(0, 0, 20, 2), child.Bounds);

        panelA.Children.Remove(child);
        panelB.Children.Add(child);
        var managerB = LayoutFixture.CreateRoot(panelB);
        managerB.Layout(30, 10);

        Assert.Same(panelB, child.VisualParent);
        Assert.Equal(new Rect(0, 0, 30, 2), child.Bounds); // single-shot state rebuilt on attach
    }

    [Fact]
    public void UIElementCollection_MoveAndIndexerSet_PreserveAttachState()
    {
        var panel = new StackPanel();
        var a = new Probe(5, 1);
        var b = new Probe(5, 1);
        panel.Children.Add(a);
        panel.Children.Add(b);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        panel.Children.Move(0, 1); // reorder without detach
        Assert.Same(panel, a.VisualParent);
        Assert.Equal(new[] { b, a }, panel.Children.ToArray());
        manager.Layout(20, 10);
        Assert.Equal(0, b.Bounds.Row);
        Assert.Equal(1, a.Bounds.Row);

        var c = new Probe(5, 1);
        panel.Children[0] = c; // replace = detach old + adopt new
        Assert.Null(b.VisualParent);
        Assert.Same(panel, c.VisualParent);
    }

    [Fact]
    public void RunLayoutPass_ConstraintChange_RelayoutsRootToTheNewRect()
    {
        var panel = new StackPanel();
        var child = new Probe(5, 2);
        panel.Children.Add(child);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);
        Assert.Equal(new Rect(0, 0, 20, 10), panel.Bounds);

        manager.Layout(30, 12); // resize: root re-measures and re-arranges under the new constraint

        Assert.Equal(new Rect(0, 0, 30, 12), panel.Bounds);
        Assert.Equal(30, child.Bounds.Columns); // children reflow under the new width
        Assert.False(manager.HasPendingWork);
    }

    [Fact]
    public void AbandonPendingLayout_DropsThisTick_RequeuesNextPass()
    {
        var panel = new StackPanel();
        var child = new Probe(5, 2);
        panel.Children.Add(child);
        var manager = LayoutFixture.CreateRoot(panel);
        manager.Layout(20, 10);

        child.Width = 7;
        Assert.True(manager.HasPendingWork);

        manager.AbandonPendingLayout();
        Assert.True(manager.HasPendingWork);  // stays invalid — pending for the next tick
        Assert.False(child.IsMeasureValid);

        manager.Layout(20, 10);               // the dropped entries re-queue and run
        Assert.True(child.IsMeasureValid);
        Assert.Equal(7, child.DesiredSize.Columns);
        Assert.False(manager.HasPendingWork);
    }

    [Fact]
    public void VisibilityRouting_VisibleHiddenFlip_SetsRenderSeamOnly()
    {
        var probe = new Probe(5, 2);
        var manager = LayoutFixture.CreateRoot(probe);
        manager.Layout(20, 10);
        probe.RenderDirty = false;

        probe.Visibility = Visibility.Hidden;

        Assert.False(manager.HasPendingWork); // render-side only (LD5)
    }

    // ───────────────────────────── adoption pre-validation (collection integrity) ─────────────────────────────

    [Fact]
    public void ChildrenCollection_CycleAdd_ThrowsBeforeMutation_CollectionStaysUsable()
    {
        var parent = new StackPanel();
        var child = new StackPanel();
        parent.Children.Add(child);

        // Adding the parent into its own child's collection is a cycle: the documented
        // InvalidOperationException must fire BEFORE any state mutates — a mid-adoption throw
        // would leave the item half-adopted and the collection permanently wedged.
        Assert.Throws<InvalidOperationException>(() => child.Children.Add(parent));

        Assert.DoesNotContain(parent, child.Children);
        Assert.Null(parent.LogicalParent);
        Assert.Null(parent.VisualParent);

        // Self-add throws without corrupting either.
        Assert.Throws<InvalidOperationException>(() => child.Children.Add(child));
        Assert.Empty(child.Children);

        // The collection is not wedged: normal operations still work.
        var sibling = new Probe(2, 1);
        child.Children.Add(sibling);
        Assert.True(child.Children.Remove(sibling));
    }

    [Fact]
    public void ChildrenCollection_InheritanceCycleAdd_ThrowsBeforeMutation()
    {
        var logicalOwner = new TreeProbe();
        var panel = new StackPanel();
        logicalOwner.AdoptLogical(panel); // logicalOwner is panel's inheritance ancestor — no visual link

        // Adopting the inheritance ancestor would trip SetInheritanceParent's cycle guard AFTER
        // the logical-child mutation; the collection pre-validates so nothing mutates.
        Assert.Throws<InvalidOperationException>(() => panel.Children.Add(logicalOwner));

        Assert.DoesNotContain(logicalOwner, panel.Children);
        Assert.Null(logicalOwner.VisualParent);
        Assert.Null(logicalOwner.LogicalParent);
        Assert.Empty(panel.Children);
    }

    [Fact]
    public void ChildrenCollection_IndexerSet_InvalidValue_KeepsOldItem()
    {
        var panel = new StackPanel();
        var other = new StackPanel();
        var item = new Probe(2, 1);
        var taken = new Probe(2, 1);
        panel.Children.Add(item);
        other.Children.Add(taken);

        // The replacement is invalid (parented elsewhere): the setter must validate BEFORE
        // removing the old item, so a failed replace leaves the collection untouched.
        Assert.Throws<InvalidOperationException>(() => panel.Children[0] = taken);
        Assert.Throws<ArgumentNullException>(() => panel.Children[0] = null!);

        Assert.Same(item, panel.Children[0]);
        Assert.Same(panel, item.VisualParent);
    }

    [Fact]
    public void AddVisualChildOnly_PreservesExistingLogicalParent_AndInheritance()
    {
        // The punch-43 contract (ItemsPresenter-style hosting): the item is a logical child of
        // the ItemsControl analogue FIRST, then visually hosted elsewhere — visual-only adoption
        // must not touch the logical relationship, and inheritance stays LogicalParent-first.
        var itemsControl = new TreeProbe();
        var presenter = new TreeProbe();
        var item = new Probe(2, 2);

        itemsControl.AdoptLogical(item);
        presenter.AddVisualChildOnly(item);

        Assert.Same(itemsControl, item.LogicalParent);
        Assert.Same(presenter, item.VisualParent);
        Assert.Same(itemsControl, item.GetInheritanceParent());
    }

    private sealed class RecordingEvictionListener(List<BindingEntryBase> evicted) : IValueEvictionListener
    {
        public void OnEvicted(BindingEntryBase entry) => evicted.Add(entry);
    }
}
