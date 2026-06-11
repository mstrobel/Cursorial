using Cursorial.Output;
using Cursorial.UI;
using static Cursorial.Tests.UI.PrecedenceMatrix.MatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>
/// Matrix §19 — allocation assertions (M265–M269). Deterministic
/// <c>GC.GetAllocatedBytesForCurrentThread</c> deltas after warm-up, single-threaded —
/// <c>[Fact]</c>s per the repo norm, not BenchmarkDotNet.
/// </summary>
public class Section19_Allocation
{
    /// <summary>The M265 record-struct-valued property (<c>Cursorial.Output.Color</c> — the styling vocabulary shape).</summary>
    private static readonly StyledProperty<Color> PColor = UIProperty.Register<Host, Color>(UniqueName("M265Color"));

    /// <summary>An observer that records nothing — pure delivery-path coverage with zero allocation.</summary>
    private sealed class CountingObserver<T> : IValueObserver<T>
    {
        public int Count;

        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
            => Count++;
    }

    private static long Measure(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void M265_SteadyStateAnimatedWrite_ZeroBytesPerWrite()
    {
        var host = new RecordingHost(); // virtual-channel override present — realistic dispatch shape
        host.SetValue(P, 3);
        host.SetValue(PColor, Color.FromRgb(10, 10, 10));
        var intHandle = host.BeginAnimation(P);
        var colorHandle = host.BeginAnimation(PColor);
        var intObserver = new CountingObserver<int>();
        var colorObserver = new CountingObserver<Color>();
        host.AddObserver(P, intObserver);
        host.AddObserver(PColor, colorObserver);

        // Warm-up: first writes create entries, resolve metadata, prime the carrier pool.
        intHandle.SetValue(100);
        colorHandle.SetValue(Color.FromRgb(0, 0, 1));

        var delta = Measure(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                intHandle.SetValue(i);                                 // changing values — full dispatch each write
                colorHandle.SetValue(Color.FromRgb(0, (byte)(i & 0xFF), (byte)(i >> 8)));
            }
        });

        Assert.Equal(0, delta); // in-place mutate + struct carrier args: 0 bytes/write after the first
        Assert.Equal(1_001, intObserver.Count);
    }

    [Fact]
    public void M268_EqualityGatedNoOps_ZeroBytes()
    {
        var host = new RecordingHost();
        host.SetValue(P, 5);
        var handle = host.BeginAnimation(P);
        handle.SetValue(9);

        var delta = Measure(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                host.SetValue(P, 5);   // gated local write
                handle.SetValue(9);    // gated animated write
            }
        });

        Assert.Equal(0, delta);
    }

    [Fact]
    public void M266_GetValueHotPath_AllocatesNothing()
    {
        var host = new Host();
        host.SetValue(P, 5);

        // Warm-up: resolve metadata caches for both the set and the default-valued read.
        _ = host.GetValue(P);
        _ = host.GetValue(Pc2);

        var delta = Measure(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                _ = host.GetValue(P);   // set property
                _ = host.GetValue(Pc2); // default-valued property
            }
        });

        Assert.Equal(0, delta);
    }

    [Fact]
    public void M267_UntypedGetValue_ZeroAfterFirstBox()
    {
        var host = new Host();
        host.SetValue(P, 5);
        _ = host.GetValue((UIProperty)P); // the first read pays the one box (then interned)
        _ = host.GetValue((UIProperty)Pc2); // metadata-default box is cached on the metadata instance

        var delta = Measure(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                _ = host.GetValue((UIProperty)P);
                _ = host.GetValue((UIProperty)Pc2);
            }
        });

        Assert.Equal(0, delta);
    }

    [Fact]
    public void M269_OneTimeCostsBounded_SecondWriteAllocatesZero()
    {
        var host = new RecordingHost(); // virtual-channel override present — realistic dispatch shape
        var observer = new CountingObserver<int>();

        // First write: bounded, not zero — allocates the store + one EffectiveValue<T>.
        var firstWrite = Measure(() => host.SetValue(P, 1));
        Assert.True(firstWrite > 0);

        // Observer add: bounded per add (COW array slot + subscription token), never zero.
        var observerAdd = Measure(() => host.AddObserver(P, observer));
        Assert.True(observerAdd > 0);

        // Warm the full dispatch path (carrier pool, observer arrays), then the pinned assertion:
        // the second write allocates 0 with observers subscribed.
        host.SetValue(P, 2);
        var secondWrite = Measure(() =>
        {
            for (var i = 0; i < 1_000; i++)
                host.SetValue(P, 3 + i);
        });

        Assert.Equal(0, secondWrite);
        Assert.Equal(1_001, observer.Count);
    }
}
