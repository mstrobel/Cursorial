using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §13 — direct properties / <c>SetAndRaise</c> (M213–M220). No store, no coercion, no
/// styling, no inheritance, no animation lane (ledger A24).
/// </summary>
public class Section13_Direct
{
    [Fact]
    public void M219_StyledSurfaces_RejectDirectProperties()
    {
        // BeginAnimation / typed Bind / typed frame entries take StyledProperty<T> — direct
        // properties are unrepresentable at compile time (A24). The untyped seams throw:
        var host = new Host();
        var frame = new TestValueFrame(10).WithUntyped(Pd);

        Assert.Throws<ArgumentException>(() => host.AddFrame(frame));      // A24: untyped frame entry
        Assert.Throws<NotSupportedException>(() => host.BindUntyped(Pd));  // A16 bridge: no entry kind for direct
    }

    [Fact]
    public void M213_SetAndRaise_RaisesObserverAndVirtualChannels()
    {
        var host = new RecordingHost();
        var untyped = new UntypedRecorder();
        host.AddObserver((UIProperty)Pd, untyped);
        var virtualLog = new List<BindingPriority>();
        host.VirtualHook += args =>
        {
            if (ReferenceEquals(args.Property, Pd))
                virtualLog.Add(args.Priority);
        };

        var changed = host.SetD(5); // SetAndRaise(Pd, ref _d, 5)

        Assert.True(changed);
        Assert.Equal(5, host.DValue);
        // Direct properties have no styled metadata, so there is structurally no metadata-Changed channel.
        Assert.Equal([((object?)0, (object?)5, BindingPriority.LocalValue)], untyped.Deliveries);
        Assert.Equal([BindingPriority.LocalValue], virtualLog);
    }

    [Fact]
    public void M214_SetAndRaise_EqualValue_GatedAndSilent()
    {
        var host = new RecordingHost();
        host.SetD(5);
        var untyped = new UntypedRecorder();
        host.AddObserver((UIProperty)Pd, untyped);

        var changed = host.SetD(5);

        Assert.False(changed); // EqualityComparer<T>.Default gate
        Assert.Empty(untyped.Deliveries);
    }

    [Fact]
    public void M215_UntypedGetValue_RoutesThroughGetter_NoStore()
    {
        var host = new Host();

        var boxed = host.GetValue((UIProperty)Pd);

        Assert.Equal(0, boxed);
        Assert.Null(host.DebugValueStore); // no store allocation
    }

    [Fact]
    public void M216_UntypedSetValue_RoutesThroughSetter_Raises()
    {
        var host = new RecordingHost();
        var untyped = new UntypedRecorder();
        host.AddObserver((UIProperty)Pd, untyped);

        host.SetValue((UIProperty)Pd, 7);

        Assert.Equal(7, host.DValue);
        Assert.Equal([((object?)0, (object?)7, BindingPriority.LocalValue)], untyped.Deliveries); // raises as M213
    }

    [Fact]
    public void M217_SetterlessDirect_UntypedSetValue_Throws()
    {
        var readOnlyDirect = UIProperty.RegisterDirect<Host, int>(UniqueName("M217"), static h => h.DValue);
        var host = new Host();

        Assert.Throws<InvalidOperationException>(() => host.SetValue((UIProperty)readOnlyDirect, 7));
    }

    [Fact]
    public void M218_UntypedUnsetValue_SetterReceivesRegisteredFallback()
    {
        var host = new Host();

        host.SetValue((UIProperty)Pd, UIProperty.UnsetValue);

        Assert.Equal(-1, host.DValue); // the descriptor's unsetValue contract
    }

    [Fact]
    public void M220_GetValueSource_AlwaysLocal_NeverCurrent()
    {
        var host = new Host();

        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource((UIProperty)Pd));

        host.SetD(5);
        Assert.Equal(new ValueSource(BindingPriority.LocalValue, false), host.GetValueSource((UIProperty)Pd)); // field semantics, no ladder
    }
}
