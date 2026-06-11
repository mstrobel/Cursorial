using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable RedundantCast
// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §2 — the local lane (M15–M32; M31 needs binding entries and lands with them).</summary>
public class Section02_LocalLane
{
    [Fact]
    public void M015_LocalWrite_NotifiesAtLocal()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 1);

        Assert.Equal(1, host.GetValue(P));
        Assert.Equal(1, host.GetBaseValue(P));
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        Assert.True(host.IsSet(P));
        probe.AssertSingleNotify(0, 1, BindingPriority.LocalValue);
    }

    [Fact]
    public void M016_ChannelOrder_MetadataTypedUntypedVirtual()
    {
        var order = new List<string>();
        var property = UIProperty.Register<Host, int>(
            UniqueName("M16"), changed: (_, _, _) => order.Add("metadata"));

        var host = new RecordingHost();
        host.AddObserver(property, new TypedRecorder<int> { SequenceLog = order, Label = "typed" });
        host.AddObserver(property, new UntypedRecorder { SequenceLog = order, Label = "untyped" });
        host.VirtualHook += args =>
        {
            if (ReferenceEquals(args.Property, property))
                order.Add("virtual");
        };

        host.SetValue(property, 1);

        Assert.Equal(["metadata", "typed(0->1,LocalValue)", "untyped(0->1,LocalValue)", "virtual"], order);
    }

    [Fact]
    public void M017_EqualLocalRewrite_IsSilent()
    {
        var host = new RecordingHost();
        host.SetValue(P, 1);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 1);

        probe.AssertSilent();
    }

    [Fact]
    public void M018_SequentialWrites_NotifyEach()
    {
        var host = new RecordingHost();
        host.SetValue(P, 1);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, 2);
        probe.AssertSingleNotify(1, 2, BindingPriority.LocalValue);

        host.SetValue(P, 3);
        probe.AssertSingleNotify(2, 3, BindingPriority.LocalValue);
    }

    [Fact]
    public void M019_ComparerEqualWrite_Silent_FirstStoredSurvives()
    {
        var host = new RecordingHost();
        host.SetValue(Pcmp, "abc");
        var probe = Probe<string?>.Attach(host, Pcmp);

        host.SetValue(Pcmp, "ABC");

        probe.AssertSilent();
        Assert.Equal("abc", host.GetValue(Pcmp)); // PD20
    }

    [Fact]
    public void M020_ClearValue_PromotesToDefault()
    {
        var host = new RecordingHost();
        host.SetValue(P, 1);
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        Assert.Equal(0, host.GetValue(P));
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        Assert.False(host.IsSet(P));
        probe.AssertSingleNotify(1, 0, BindingPriority.Default); // PD10
    }

    [Fact]
    public void M021_ClearValue_OnFresh_IsNoOp()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.ClearValue(P);

        probe.AssertSilent();
        Assert.Null(host.DebugValueStore!.TryGetEntry(P.Id)); // observers exist, value entries don't
    }

    [Fact]
    public void M022_SetValue_StylePriority_Throws()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue(P, 1, BindingPriority.Style));
    }

    [Fact]
    public void M023_SetValue_AnimationPriority_Throws()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue(P, 1, BindingPriority.Animation));
    }

    [Theory]
    [InlineData(BindingPriority.Inherited)]
    [InlineData(BindingPriority.Default)]
    [InlineData(BindingPriority.Unset)]
    public void M024_SetValue_ResolutionOnlyTiers_Throw(BindingPriority priority)
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue(P, 1, priority));
    }

    [Fact]
    public void M025_ObserverArgs_CarryLocalPriority()
    {
        var host = new Host();
        var typed = new TypedRecorder<int>();
        var untyped = new UntypedRecorder();
        host.AddObserver(P, typed);
        host.AddObserver((UIProperty)P, untyped);

        host.SetValue(P, 1);

        Assert.Equal(BindingPriority.LocalValue, Assert.Single(typed.Deliveries).Priority);
        Assert.Equal(BindingPriority.LocalValue, Assert.Single(untyped.Deliveries).Priority);
    }

    [Fact]
    public void M026_Subscription_DoesNotReplay()
    {
        var host = new Host();
        var recorder = new TypedRecorder<int>();

        host.AddObserver(P, recorder);

        Assert.Empty(recorder.Deliveries); // PD19
        Assert.Equal(new ValueSource(BindingPriority.Default, false), host.GetValueSource(P));
        Assert.False(host.IsSet(P));
    }

    [Fact]
    public void M027_DisposedSubscription_ReceivesNothing()
    {
        var host = new Host();
        var recorder = new TypedRecorder<int>();
        var subscription = host.AddObserver(P, recorder);

        subscription.Dispose();
        host.SetValue(P, 1);

        Assert.Empty(recorder.Deliveries);
    }

    [Fact]
    public void M028_SubscribeDuringRaise_NotInvokedForInFlightChange()
    {
        var host = new Host();
        var late = new TypedRecorder<int>();
        var subscribed = false;

        var first = new TypedRecorder<int>
                    {
                        Callback = (_, _) =>
                                   {
                                       if (!subscribed)
                                       {
                                           subscribed = true;
                                           host.AddObserver(P, late);
                                       }
                                   }
                    };

        host.AddObserver(P, first);

        host.SetValue(P, 1);
        Assert.Empty(late.Deliveries); // snapshot semantics — not invoked for the in-flight change

        host.SetValue(P, 2);
        Assert.Equal([(1, 2, BindingPriority.LocalValue)], late.Deliveries);
    }

    [Fact]
    public void M029_DisposeDuringRaise_InFlightDeliveryStillArrives()
    {
        var host = new Host();
        var second = new TypedRecorder<int>();
        IDisposable? secondSubscription = null;

        var first = new TypedRecorder<int>
                    {
                        // ReSharper disable AccessToModifiedClosure
                        Callback = (_, _) => secondSubscription?.Dispose()
                    };

        // ReSharper restore AccessToModifiedClosure

        host.AddObserver(P, first);
        secondSubscription = host.AddObserver(P, second);

        host.SetValue(P, 1);
        Assert.Equal([(0, 1, BindingPriority.LocalValue)], second.Deliveries); // snapshot semantics

        host.SetValue(P, 2);
        Assert.Single(second.Deliveries); // unsubscribed for subsequent changes
    }

    [Fact]
    public void M030_ChangeArgs_TypedAccess_WrongTypeThrows()
    {
        var host = new RecordingHost();
        var asserted = false;
        host.VirtualHook += args =>
        {
            if (!ReferenceEquals(args.Property, P))
                return;
            Assert.Equal(0, args.GetOldValue<int>());
            Assert.Equal(1, args.GetNewValue<int>());
            Assert.Throws<InvalidCastException>(args.GetNewValue<string>);
            asserted = true;
        };

        host.SetValue(P, 1);

        Assert.True(asserted);
    }

    [Fact]
    public void M031_LastWriterWinsWithinTheLane_BindingSurvivesSetValue()
    {
        var host = new RecordingHost();
        host.SetValue(P, 1);
        var entry = host.Bind(P);
        var probe = Probe<int>.Attach(host, P);

        entry.SetValue(2);
        probe.AssertSingleNotify(1, 2, BindingPriority.LocalValue);

        host.SetValue(P, 3); // A9: a transient override — never kills the binding
        probe.AssertSingleNotify(2, 3, BindingPriority.LocalValue);

        entry.SetValue(4); // the binding's next push wins again
        probe.AssertSingleNotify(3, 4, BindingPriority.LocalValue);
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
    }

    [Fact]
    public void M032_AfterClear_IsSetIsFalse()
    {
        var host = new Host();
        host.SetValue(P, 1);
        host.ClearValue(P);

        Assert.False(host.IsSet(P));
    }
}
