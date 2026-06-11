using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §17 — reentrancy (M252–M258; M258 needs the animation lane).</summary>
public class Section17_Reentrancy
{
    [Fact]
    public void M252_CopiedArgs_SurviveNestedWrite_NestedCompletesFirst()
    {
        var sequence = new List<string>();
        var host = new Host();

        var observer1 = new TypedRecorder<int> { SequenceLog = sequence, Label = "o1" };
        observer1.Callback = (_, newValue) =>
        {
            if (newValue == 2)
                host.SetValue(P, 3);
        };
        host.AddObserver(P, observer1);
        var observer2 = new TypedRecorder<int> { SequenceLog = sequence, Label = "o2" };
        host.AddObserver(P, observer2);

        host.SetValue(P, 2);

        // Copied-value args stay uncorrupted: the outer change is exactly (0→2), the nested (2→3);
        // the nested dispatch completes before the outer finishes delivering (PD18 inversion).
        Assert.Equal(
            ["o1(0->2,LocalValue)", "o1(2->3,LocalValue)", "o2(2->3,LocalValue)", "o2(0->2,LocalValue)"],
            sequence);
        Assert.Equal([(0, 2, BindingPriority.LocalValue), (2, 3, BindingPriority.LocalValue)], observer1.Deliveries);
        Assert.Equal([(2, 3, BindingPriority.LocalValue), (0, 2, BindingPriority.LocalValue)], observer2.Deliveries);
        Assert.Equal(3, host.GetValue(P));
    }

    [Fact]
    public void M253_NestedSameValueWrite_EqualityGated_Terminates()
    {
        var host = new Host();
        var observer = new TypedRecorder<int>();
        observer.Callback = (_, newValue) =>
        {
            if (newValue == 2)
                host.SetValue(P, 2); // sets the same value it just observed
        };
        host.AddObserver(P, observer);

        host.SetValue(P, 2);

        Assert.Equal([(0, 2, BindingPriority.LocalValue)], observer.Deliveries); // no second dispatch
    }

    [Fact]
    public void M254_ConvergentCycle_TerminatesViaEqualityGate()
    {
        var propertyA = UIProperty.Register<Host, int>(UniqueName("M254A"));
        var propertyB = UIProperty.Register<Host, int>(UniqueName("M254B"));
        var host = new Host();

        var observerA = new TypedRecorder<int>();
        observerA.Callback = (_, newValue) => host.SetValue(propertyB, newValue);
        host.AddObserver(propertyA, observerA);

        var observerB = new TypedRecorder<int>();
        observerB.Callback = (_, newValue) => host.SetValue(propertyA, newValue); // converges: re-set of an equal value gates
        host.AddObserver(propertyB, observerB);

        host.SetValue(propertyA, 1);

        Assert.Equal([(0, 1, BindingPriority.LocalValue)], observerA.Deliveries); // every intermediate change exactly once
        Assert.Equal([(0, 1, BindingPriority.LocalValue)], observerB.Deliveries);
        Assert.Equal(1, host.GetValue(propertyA));
        Assert.Equal(1, host.GetValue(propertyB));
    }

    [Fact]
    public void M255_DivergentCycle_DebugDepthAssertTripsAt64()
    {
        var property = UIProperty.Register<Host, int>(UniqueName("M255"));
        var host = new Host();
        var observer = new TypedRecorder<int>();
#if DEBUG
        observer.Callback = (_, newValue) => host.SetValue(property, newValue + 1); // diverges
        host.AddObserver(property, observer);

        Assert.Throws<InvalidOperationException>(() => host.SetValue(property, 1)); // fail-fast at depth 64
#else
        // Release is unbounded by design (a consumer bug); cap the handler to verify no engine check fires.
        observer.Callback = (_, newValue) =>
        {
            if (newValue < 100)
                host.SetValue(property, newValue + 1);
        };
        host.AddObserver(property, observer);

        host.SetValue(property, 1);
        Assert.Equal(100, host.GetValue(property));
#endif
    }

    [Fact]
    public void M256_MetadataCallback_SetsSameProperty_NestedDispatchCompletesFirst()
    {
        var sequence = new List<string>();
        Host host = null!;
        StyledProperty<int> property = null!;
        property = UIProperty.Register<Host, int>(
            UniqueName("M256"),
            changed: (_, oldValue, newValue) =>
            {
                sequence.Add($"cb({oldValue}->{newValue})");
                if (newValue == 2)
                    host.SetValue(property, 5);
            });
        host = new RecordingHost();
        host.AddObserver(property, new TypedRecorder<int> { SequenceLog = sequence, Label = "obs" });
        ((RecordingHost)host).VirtualHook += args =>
        {
            if (ReferenceEquals(args.Property, property))
                sequence.Add($"virt({args.GetOldValue<int>()}->{args.GetNewValue<int>()})");
        };

        host.SetValue(property, 2);

        // Amended M256: cb before observers within each dispatch (M16); the nested dispatch
        // completes first (PD18), so observers receive (2→5) and then (0→2), each exactly once.
        Assert.Equal(
            [
                "cb(0->2)",
                "cb(2->5)", "obs(2->5,LocalValue)", "virt(2->5)",
                "obs(0->2,LocalValue)", "virt(0->2)",
            ],
            sequence);
        Assert.Equal(5, host.GetValue(property));
    }

    [Fact]
    public void M257_NestedClearValue_OuterArgsUncorrupted()
    {
        var sequence = new List<string>();
        var host = new Host();

        var observer1 = new TypedRecorder<int> { SequenceLog = sequence, Label = "o1" };
        observer1.Callback = (oldValue, newValue) =>
        {
            if (oldValue == 0 && newValue == 2)
                host.ClearValue(P);
        };
        host.AddObserver(P, observer1);
        var observer2 = new TypedRecorder<int> { SequenceLog = sequence, Label = "o2" };
        host.AddObserver(P, observer2);

        host.SetValue(P, 2);

        // The nested clear dispatches (2→0, Default) synchronously; the outer delivery then
        // continues with uncorrupted (0→2) args.
        Assert.Equal(
            ["o1(0->2,LocalValue)", "o1(2->0,Default)", "o2(2->0,Default)", "o2(0->2,LocalValue)"],
            sequence);
        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
    }

    [Fact]
    public void M258_HandleDisposedFromInsideAnimationDelivery_Legal()
    {
        var sequence = new List<string>();
        var host = new Host();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);

        var observer1 = new TypedRecorder<int> { SequenceLog = sequence, Label = "o1" };
        observer1.Callback = (_, newValue) =>
        {
            if (newValue == 9)
                handle.Dispose(); // dispose the producer from inside its own delivery
        };
        host.AddObserver(P, observer1);
        var observer2 = new TypedRecorder<int> { SequenceLog = sequence, Label = "o2" };
        host.AddObserver(P, observer2);

        handle.SetValue(9);

        // The nested promotion (9→3, Local) delivers synchronously (PD18); copied args keep the
        // outer (3→9, Animation) delivery uncorrupted; no double-dispose effects.
        Assert.Equal(
            ["o1(3->9,Animation)", "o1(9->3,LocalValue)", "o2(9->3,LocalValue)", "o2(3->9,Animation)"],
            sequence);
        Assert.Equal(3, host.GetValue(P));
        Assert.True(handle.IsDetached);
    }
}
