using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §10 — inheritance: lazy-read, eager-notify, shadowing, reparent (M179–M196).
/// Propagated deliveries assert the PD22 channel set via <see cref="InheritedProbe{T}"/>.
/// </summary>
public class Section10_Inheritance
{
    private const ulong K1 = 10;

    [Fact]
    public void M179_LazyRead_WalkUp_NoLeafStoreEntry()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);

        Assert.Equal(5, leaf.GetValue(Pi));
        Assert.Null(leaf.DebugValueStore); // lazy-read: reads create nothing on the descendant
    }

    [Fact]
    public void M180_InheritedRead_ReportsInheritedSource()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);

        Assert.Equal(new ValueSource(BindingPriority.Inherited, false), leaf.GetValueSource(Pi));
    }

    [Fact]
    public void M181_FreshTree_NoContributingAncestor_Default()
    {
        var (_, _, leaf) = Chain();

        Assert.Equal(0, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), leaf.GetValueSource(Pi));
    }

    [Fact]
    public void M182_EagerNotify_EveryEntrylessDescendant_ExactlyOnce()
    {
        var (root, mid, leaf) = Chain();
        var rootProbe = Probe<int>.Attach(root, Pi);
        var midProbe = InheritedProbe<int>.Attach(mid, Pi);
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 5);

        rootProbe.AssertSingleNotify(0, 5, BindingPriority.LocalValue); // the origin's ordinary notify
        midProbe.AssertSinglePropagated(0, 5, BindingPriority.Inherited); // A3 + A4, exactly once each
        leafProbe.AssertSinglePropagated(0, 5, BindingPriority.Inherited);
    }

    [Fact]
    public void M183_Shadowing_PropagationStopsAtShadowingSubtreeRoot()
    {
        var (root, mid, leaf) = Chain();
        mid.SetValue(Pi, 7); // shadow
        var midProbe = InheritedProbe<int>.Attach(mid, Pi);
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        root.SetValue(Pi, 5);

        midProbe.AssertSilent();
        leafProbe.AssertSilent();
        Assert.Equal(7, leaf.GetValue(Pi)); // leaf reads the shadow
    }

    [Fact]
    public void M184_ShadowRemoval_ReExposesAncestorValue()
    {
        var (root, mid, leaf) = Chain();
        mid.SetValue(Pi, 7);
        root.SetValue(Pi, 5);
        var midProbe = Probe<int>.Attach(mid, Pi); // origin-site op on mid: full channels
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        mid.ClearValue(Pi);

        midProbe.AssertSingleNotify(7, 5, BindingPriority.Inherited);
        leafProbe.AssertSinglePropagated(7, 5, BindingPriority.Inherited); // via A3/A4
    }

    [Fact]
    public void M185_Reparent_RePullsDiff()
    {
        var root = new RecordingHost();
        var root2 = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        root.SetValue(Pi, 5);
        root2.SetValue(Pi, 8);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.SetInheritanceParent(root2);

        Assert.Equal(8, leaf.GetValue(Pi));
        probe.AssertSinglePropagated(5, 8, BindingPriority.Inherited);
    }

    [Fact]
    public void M186_ReparentToNonContributing_FallsToDefault()
    {
        var root = new RecordingHost();
        var root2 = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        root.SetValue(Pi, 5);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.SetInheritanceParent(root2);

        Assert.Equal(0, leaf.GetValue(Pi));
        probe.AssertSinglePropagated(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void M187_EqualValueReparent_Silent()
    {
        var root = new RecordingHost();
        var root2 = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        root.SetValue(Pi, 5);
        root2.SetValue(Pi, 5);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        leaf.SetInheritanceParent(root2);

        probe.AssertSilent(); // equal-value reparent diff
        Assert.Equal(5, leaf.GetValue(Pi));
    }

    [Fact]
    public void M188_DescendantsInheritTheAnimatedEffective()
    {
        var (root, _, leaf) = Chain();
        var handle = root.BeginAnimation(Pi);
        handle.SetValue(9);

        Assert.Equal(9, leaf.GetValue(Pi)); // the ancestor's effective — animated included
    }

    [Fact]
    public void M189_AnimatedAncestor_NotifiesPerFrame_EqualityGated()
    {
        var (root, _, leaf) = Chain();
        var handle = root.BeginAnimation(Pi);
        handle.SetValue(9);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        handle.SetValue(10);
        probe.AssertSinglePropagated(9, 10, BindingPriority.Inherited);

        handle.SetValue(10); // equality gate still applies
        probe.AssertSilent();
    }

    [Fact]
    public void M190_InheritedReads_SkipReCoercion()
    {
        var pic = UIProperty.Register<Host, int>(
            UniqueName("Pic"), inherits: true, coerce: static (_, v) => Math.Clamp(v, 0, 100));
        var (root, _, leaf) = Chain();

        root.SetValue(pic, 250);

        Assert.Equal(100, root.GetValue(pic)); // coerced at the set site
        Assert.Equal(100, leaf.GetValue(pic)); // walked, not re-coerced
    }

    [Fact]
    public void M191_StyleShadowsInherited()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        leaf.AddFrame(new TestValueFrame(K1).With(Pi, 9));

        Assert.Equal(9, leaf.GetValue(Pi));
        Assert.Equal(new ValueSource(BindingPriority.Style, false), leaf.GetValueSource(Pi));

        var probe = InheritedProbe<int>.Attach(leaf, Pi);
        root.SetValue(Pi, 6);
        probe.AssertSilent(); // style shadows the walk
    }

    [Fact]
    public void M192_InheritedContribution_DoesNotCountAsIsSet()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);

        Assert.False(leaf.IsSet(Pi)); // PD11
    }

    [Fact]
    public void M193_NonInheritingProperty_NoWalk()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(P, 5);

        Assert.Equal(0, leaf.GetValue(P));
    }

    [Fact]
    public void M194_DeferOnOrigin_CoalescesThePropagation()
    {
        var (root, _, leaf) = Chain();
        root.SetValue(Pi, 5);
        var probe = InheritedProbe<int>.Attach(leaf, Pi);

        using (root.DeferNotifications())
        {
            root.SetValue(Pi, 6);
            root.SetValue(Pi, 7);
            probe.AssertSilent(); // nothing escapes mid-scope
        }

        probe.AssertSinglePropagated(5, 7, BindingPriority.Inherited); // one coalesced delivery
    }

    [Fact]
    public void M195_DeepChain_WalkAtDepth_ReparentRePullsSubtreeOnly()
    {
        var root = new RecordingHost();
        var a = new RecordingHost();
        var b = new RecordingHost();
        var c = new RecordingHost();
        var leaf = new RecordingHost();
        a.SetInheritanceParent(root);
        b.SetInheritanceParent(a);
        c.SetInheritanceParent(b);
        leaf.SetInheritanceParent(c);

        root.SetValue(Pi, 5);
        Assert.Equal(5, leaf.GetValue(Pi)); // walk length ≥ 4

        var root2 = new RecordingHost();
        root2.SetValue(Pi, 8);
        var aProbe = InheritedProbe<int>.Attach(a, Pi);
        var bProbe = InheritedProbe<int>.Attach(b, Pi);
        var cProbe = InheritedProbe<int>.Attach(c, Pi);
        var leafProbe = InheritedProbe<int>.Attach(leaf, Pi);

        b.SetInheritanceParent(root2);

        aProbe.AssertSilent(); // re-pull covers b..leaf only
        bProbe.AssertSinglePropagated(5, 8, BindingPriority.Inherited);
        cProbe.AssertSinglePropagated(5, 8, BindingPriority.Inherited);
        leafProbe.AssertSinglePropagated(5, 8, BindingPriority.Inherited);
    }

    [Fact]
    public void M196_DataContextRebindShape_ObserverFires_NoLeafEntry()
    {
        var pdc = UIProperty.Register<Host, object?>(UniqueName("Pdc"), inherits: true);
        var (root, _, leaf) = Chain();
        var oldVm = new object();
        var newVm = new object();
        root.SetValue(pdc, oldVm);

        var recorder = new TypedRecorder<object?>();
        leaf.AddObserver(pdc, recorder);

        root.SetValue(pdc, newVm);

        Assert.Equal([(oldVm, (object?)newVm, BindingPriority.Inherited)], recorder.Deliveries); // the S2 rebind hook
        Assert.NotNull(leaf.DebugValueStore); // the observer table lives in the store…
        Assert.Null(leaf.DebugValueStore!.TryGetEntry(pdc.Id)); // …but observation creates no value entry (A22)
    }
}
