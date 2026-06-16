using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable RedundantCast
// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>Matrix §14 — the untyped lane (M221–M229).</summary>
public class Section14_Untyped
{
    [Theory]
    [InlineData(BindingPriority.Animation, 9)]
    [InlineData(BindingPriority.LocalValue, 7)]
    [InlineData(BindingPriority.Style, 5)]
    [InlineData(BindingPriority.Inherited, 2)]
    [InlineData(BindingPriority.Default, 0)]
    public void M227_UntypedMaxPriorityProbes_TypedParity_BoxedResults(BindingPriority maxPriority, int expected)
    {
        var (_, leaf, _, _) = BuildM112Stack();

        Assert.Equal(expected, leaf.GetValue((UIProperty)Pi, maxPriority));
    }

    [Fact]
    public void M224_UntypedUnsetValueWrite_IsClearValueInFull_IncludingEviction()
    {
        var host = new RecordingHost();
        var listener = new EvictionRecorder();
        var entry = host.Bind(P, listener: listener);
        host.SetValue(P, 5);
        var probe = Probe<int>.Attach(host, P);

        host.SetValue(P, UIProperty.UnsetValue, BindingPriority.LocalValue); // PD5

        Assert.Equal([entry], listener.Evictions); // A9 eviction included
        Assert.Equal(0, host.GetValue(P));
        probe.AssertSingleNotify(5, 0, BindingPriority.Default);
    }

    private enum SampleEnum
    {
        // ReSharper disable once UnusedMember.Local
        None = 0,
        Red = 1
    }

    [Fact]
    public void M221_UntypedSetValue_IdenticalToTypedLocal()
    {
        var host = new RecordingHost();
        var probe = Probe<int>.Attach(host, P);

        host.SetValue((UIProperty)P, 5);

        Assert.Equal(5, host.GetValue(P)); // typed read, unboxed
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource(P));
        probe.AssertSingleNotify(0, 5, BindingPriority.LocalValue);
    }

    [Fact]
    public void M222_UntypedSetValue_WrongType_Throws()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue((UIProperty)P, "wrong-type")); // no silent conversion
    }

    [Fact]
    public void M223_UntypedSetValue_Null_ThrowsForNonNullable_LegalForNullable()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue((UIProperty)P, null)); // int

        host.SetValue((UIProperty)Pcmp, null); // string? — legal value (equals the default: silent lane flip)
        Assert.True(host.IsSet(Pcmp));
        Assert.Null(host.GetValue(Pcmp));
    }

    [Fact]
    public void M225_UntypedGetValue_ReturnsBoxedValue()
    {
        var host = new Host();
        host.SetValue(P, 5);

        Assert.Equal(5, host.GetValue((UIProperty)P)); // semantic assertion is Equals only (M267 owns identity)
    }

    [Fact]
    public void M226_UntypedObserver_BoxedCopy_AfterTypedObservers()
    {
        var order = new List<string>();
        var host = new Host();
        host.AddObserver(P, new TypedRecorder<int> { SequenceLog = order, Label = "typed" });
        var untyped = new UntypedRecorder { SequenceLog = order, Label = "untyped" };
        host.AddObserver((UIProperty)P, untyped);

        host.SetValue(P, 5);

        var delivery = Assert.Single(untyped.Deliveries);
        Assert.Equal(5, delivery.New);
        Assert.Equal(BindingPriority.LocalValue, delivery.Priority); // A10
        Assert.Equal(["typed(0->5,LocalValue)", "untyped(0->5,LocalValue)"], order); // M16 order
    }

    [Fact]
    public void M228_EnumTypedProperty_UntypedRoundTrips()
    {
        var property = UIProperty.Register<Host, SampleEnum>(UniqueName("M228"));
        var host = new Host();

        host.SetValue((UIProperty)property, SampleEnum.Red);

        Assert.Equal(SampleEnum.Red, host.GetValue(property));
        Assert.Equal(SampleEnum.Red, host.GetValue((UIProperty)property)); // boxed round-trip; interning is allocation behavior only
    }

    [Fact]
    public void M229_UntypedSetValue_StylePriority_Throws()
    {
        var host = new Host();

        Assert.Throws<ArgumentException>(() => host.SetValue((UIProperty)P, 5, BindingPriority.Style)); // PD1 at the untyped mouth
    }
}
