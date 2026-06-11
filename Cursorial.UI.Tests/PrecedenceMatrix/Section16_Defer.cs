using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §16 — <c>DeferNotifications</c> batching (M244–M251).</summary>
public class Section16_Defer
{
    [Fact]
    public void M246_Defer_FlushesInFirstChangeOrder_OncePerProperty()
    {
        var host = new RecordingHost();
        var sequence = new List<string>();
        host.AddObserver(P, new TypedRecorder<int> { SequenceLog = sequence, Label = "P" });
        host.AddObserver(Pc, new TypedRecorder<int> { SequenceLog = sequence, Label = "Pc" });

        using (host.DeferNotifications())
        {
            host.SetValue(P, 1);
            host.AddFrame(new TestValueFrame(10).With(Pc, 120));
            Assert.Empty(sequence);
        }

        Assert.Equal(["P(0->1,LocalValue)", "Pc(0->100,Style)"], sequence); // PD15: first-change order
    }

    [Fact]
    public void M248_Defer_EvictionFiresImmediately_PromotionDeferred()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder();
        var entry = host.Bind(P, listener: listener);
        entry.SetValue(5);
        var probe = Probe<int>.Attach(host, P);

        using (host.DeferNotifications())
        {
            host.ClearValue(P);
            Assert.Equal([entry], listener.Evictions); // PD15: lifecycle is immediate
            probe.AssertSilent(); // the promotion's change notification is deferred
        }

        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    [Fact]
    public void M249_Defer_AnimatedWritesCoalesce()
    {
        var host = new RecordingHost();
        host.SetValue(P, 3);
        var handle = host.BeginAnimation(P);
        var probe = Probe<int>.Attach(host, P);

        using (host.DeferNotifications())
        {
            handle.SetValue(9);
            probe.AssertSilent();
        }

        probe.AssertSingleNotify(3, 9, BindingPriority.Animation);
    }

    [Fact]
    public void M250_Defer_SetCurrentValuePriorityPreserved()
    {
        var host = new RecordingHost();
        host.AddFrame(new TestValueFrame(10).With(P, 5));
        var probe = Probe<int>.Attach(host, P);

        using (host.DeferNotifications())
        {
            host.SetCurrentValue(P, 6);
        }

        probe.AssertSingleNotify(5, 6, BindingPriority.Style); // A11 priority rides the coalesced flush
    }

    [Fact]
    public void M244_Defer_CoalescesFirstOldLastNew_ReadsStayLive()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        using (host.DeferNotifications())
        {
            host.SetValue(P, 1);
            Assert.Equal(1, host.GetValue(P)); // reads are live
            host.SetValue(P, 2);
            Assert.Equal(2, host.GetValue(P));
            host.SetValue(P, 3);
            Assert.Equal(3, host.GetValue(P));
            probe.AssertSilent(); // inside: silent
        }

        probe.AssertSingleNotify(0, 3, BindingPriority.LocalValue); // first old, last new
    }

    [Fact]
    public void M245_Defer_RoundTripChanges_CancelOut()
    {
        var host = new RecordingHost();
        host.SetValue(P, 5);
        var probe = Probe<int>.Attach(host, P);

        using (host.DeferNotifications())
        {
            host.SetValue(P, 9);
            host.SetValue(P, 5);
        }

        probe.AssertSilent(); // first old == last new, equality-gated at flush
    }

    [Fact]
    public void M247_NestedDefer_FlushesAtOutermostDisposeOnly()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        using (host.DeferNotifications())
        {
            using (host.DeferNotifications())
            {
                host.SetValue(P, 1);
            }

            probe.AssertSilent(); // inner dispose does not flush
            host.SetValue(P, 2);
        }

        probe.AssertSingleNotify(0, 2, BindingPriority.LocalValue);
    }

    [Fact]
    public void M251_WriteDuringFlush_DispatchesSynchronouslyOutsideTheScope()
    {
        var propertyB = UIProperty.Register<Host, int>(UniqueName("M251B"));
        var sequence = new List<string>();
        var host = new Host();

        var observerA = new TypedRecorder<int> { SequenceLog = sequence, Label = "A" };
        observerA.Callback = (_, newValue) =>
        {
            if (newValue == 1)
                host.SetValue(propertyB, 2); // written after the dispose began — outside any defer
        };
        host.AddObserver(P, observerA);
        host.AddObserver(propertyB, new TypedRecorder<int> { SequenceLog = sequence, Label = "B" });

        using (host.DeferNotifications())
        {
            host.SetValue(P, 1);
        }

        // B's notification dispatched synchronously (nested, PD18) during A's flush delivery —
        // it was not captured by the already-disposing scope.
        Assert.Equal(["A(0->1,LocalValue)", "B(0->2,LocalValue)"], sequence);
    }
}
