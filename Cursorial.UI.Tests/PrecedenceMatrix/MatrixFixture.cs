using Cursorial.UI;

namespace Cursorial.Tests.UI.PrecedenceMatrix;

/// <summary>The matrix host fixture (§0.1): <c>Host</c> is the default property host.</summary>
public class Host : UIObject
{
    private int _d;

    /// <summary>The backing field of the fixture's direct property <c>Pd</c>.</summary>
    public int DValue => _d;

    /// <summary>Writes <c>Pd</c> via <c>SetAndRaise</c> (the canonical direct-property idiom).</summary>
    public bool SetD(int value) => SetAndRaise(MatrixFixture.Pd, ref _d, value);
}

/// <summary>§0.1: <c>DerivedHost : Host</c> (metadata-override and runtime-type rows).</summary>
public class DerivedHost : Host;

/// <summary>§0.1: <c>OtherHost : UIObject</c> (the <c>AddOwner</c> target; not a <c>Host</c>).</summary>
public class OtherHost : UIObject;

/// <summary>§0.1: the attached property's declaring type — deliberately NOT a <c>UIObject</c>.</summary>
public sealed class Tab
{
    private Tab() { }
}

/// <summary>
/// A <see cref="Host"/> exposing the virtual <c>OnPropertyChanged</c> channel to tests via a hook.
/// Hook handlers run inside the synchronous notification window, so <c>GetOldValue&lt;T&gt;</c> /
/// <c>GetNewValue&lt;T&gt;</c> are valid during the callback.
/// </summary>
public class RecordingHost : Host
{
    /// <summary>Invoked from the virtual channel with a copy of the args.</summary>
    public event Action<UIPropertyChangedEventArgs>? VirtualHook;

    /// <summary>Invoked from the inherited-change virtual (ledger A3) with a copy of the args.</summary>
    public event Action<InheritedPropertyChangedEventArgs>? InheritedHook;

    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);
        VirtualHook?.Invoke(args);
    }

    protected internal override void OnInheritedPropertyChanged(in InheritedPropertyChangedEventArgs args)
    {
        base.OnInheritedPropertyChanged(in args);
        InheritedHook?.Invoke(args);
    }
}

/// <summary>
/// The §0.1 fixture registrations, shared by every matrix section file. Static initialization makes
/// registration idempotent across test classes (dense ids are process-global). Rows whose Setup
/// mutates metadata register row-local properties via <see cref="UniqueName"/> instead — except the
/// fixture-pinned overrides applied here (M5/M6's DerivedHost default, M12's OtherHost default),
/// which by contract run before any instance of those types touches <see cref="P"/>.
/// </summary>
public static class MatrixFixture
{
    private static int _uniqueCounter;

    /// <summary>`P` — int on Host, default 0, no coerce/validate/changed.</summary>
    public static readonly StyledProperty<int> P = UIProperty.Register<Host, int>("P");

    /// <summary>`Pc` — default 0, coerce = clamp to [0,100].</summary>
    public static readonly StyledProperty<int> Pc = UIProperty.Register<Host, int>(
        "Pc", coerce: static (_, v) => Math.Clamp(v, 0, 100));

    /// <summary>`Pc2` — default 500, coerce = clamp to [0,100] (the default-not-coerced row M10).</summary>
    public static readonly StyledProperty<int> Pc2 = UIProperty.Register<Host, int>(
        "Pc2", defaultValue: 500, coerce: static (_, v) => Math.Clamp(v, 0, 100));

    /// <summary>`Pv` — default 0, validate = v ≥ 0.</summary>
    public static readonly StyledProperty<int> Pv = UIProperty.Register<Host, int>(
        "Pv", validate: static v => v >= 0);

    /// <summary>`Pcv` — validate = v ≠ 13 ∧ v ≤ 150, coerce = clamp to [0,100] (order-of-operations rows).</summary>
    public static readonly StyledProperty<int> Pcv = UIProperty.Register<Host, int>(
        "Pcv", validate: static v => v != 13 && v <= 150, coerce: static (_, v) => Math.Clamp(v, 0, 100));

    /// <summary>`Pi` — default 0, inherits: true.</summary>
    public static readonly StyledProperty<int> Pi = UIProperty.Register<Host, int>("Pi", inherits: true);

    /// <summary>`Pcmp` — string?, default null, metadata Comparer = OrdinalIgnoreCase.</summary>
    public static readonly StyledProperty<string?> Pcmp = UIProperty.Register<Host, string?>(
        "Pcmp", new PropertyMetadata<string?>(Comparer: StringComparer.OrdinalIgnoreCase));

    /// <summary>`Pa` — RegisterAttached&lt;Tab, Host, int&gt;("Index"), default 0.</summary>
    public static readonly AttachedProperty<int> Pa = UIProperty.RegisterAttached<Tab, Host, int>("Index");

    /// <summary>`Kro` — the write key for the read-only `Pro`.</summary>
    public static readonly UIPropertyKey<int> Kro = UIProperty.RegisterReadOnly<Host, int>("Pro");

    /// <summary>`Pro` — the read-only styled property behind <see cref="Kro"/>.</summary>
    public static readonly StyledProperty<int> Pro = Kro.Property;

    /// <summary>`Pd` — DirectProperty&lt;Host,int&gt; over <c>Host._d</c>, setter present, unsetValue −1.</summary>
    public static readonly DirectProperty<Host, int> Pd = UIProperty.RegisterDirect<Host, int>(
        "Pd", static h => h.DValue, static (h, v) => h.SetD(v), unsetValue: -1);

    /// <summary>`P2` — the M11 AddOwner alias (same instance as <see cref="P"/> by contract).</summary>
    public static readonly StyledProperty<int> P2;

    static MatrixFixture()
    {
        // M11/M12: AddOwner once, then the OtherHost default override (pre-touch by construction).
        P2 = P.AddOwner<OtherHost>();
        P.OverrideDefaultValue<OtherHost>(7);

        // M5/M6: the DerivedHost default override, applied fixture-time so it precedes any
        // DerivedHost touch of P. No other implemented row reads P on a DerivedHost instance.
        P.OverrideDefaultValue<DerivedHost>(9);
    }

    /// <summary>A process-unique property name for row-local registrations.</summary>
    public static string UniqueName(string prefix) => $"{prefix}_{Interlocked.Increment(ref _uniqueCounter)}";

    /// <summary>The §0.1 inheritance chain: three hosts wired <c>root → mid → leaf</c>.</summary>
    public static (RecordingHost Root, RecordingHost Mid, RecordingHost Leaf) Chain()
    {
        var root = new RecordingHost();
        var mid = new RecordingHost();
        var leaf = new RecordingHost();
        mid.SetInheritanceParent(root);
        leaf.SetInheritanceParent(mid);
        return (root, mid, leaf);
    }

    /// <summary>
    /// The M112 full-ladder stack on <see cref="Pi"/>: <c>root.L(Pi,2)</c>; leaf:
    /// <c>F(k1){Pi=5}</c>, <c>L(7)</c>, <c>H.Set(9)</c> (leaf's parent is root).
    /// </summary>
    public static (RecordingHost Root, RecordingHost Leaf, TestValueFrame Frame, AnimatedValueHandle<int> Handle) BuildM112Stack()
    {
        var root = new RecordingHost();
        var leaf = new RecordingHost();
        leaf.SetInheritanceParent(root);
        root.SetValue(Pi, 2);
        var frame = new TestValueFrame(10).With(Pi, 5);
        leaf.AddFrame(frame);
        leaf.SetValue(Pi, 7);
        var handle = leaf.BeginAnimation(Pi);
        handle.SetValue(9);
        return (root, leaf, frame, handle);
    }
}

/// <summary>
/// The propagated-delivery probe (matrix PD22): attaches a typed observer, an untyped observer,
/// the A3 inherited-virtual hook, <b>and</b> the ordinary-virtual hook (which must stay silent for
/// propagated changes) over one property on a <see cref="RecordingHost"/>.
/// </summary>
public sealed class InheritedProbe<T>
{
    /// <summary>Typed-channel deliveries (A4).</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> Typed = [];

    /// <summary>Untyped-channel deliveries (A10 rides A4).</summary>
    public readonly List<(object? Old, object? New, BindingPriority Priority)> Untyped = [];

    /// <summary>A3 inherited-virtual deliveries (values extracted inside the notification window).</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> Inherited = [];

    /// <summary>Ordinary-virtual deliveries — must stay empty for propagated changes (PD22).</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> OrdinaryVirtual = [];

    private InheritedProbe()
    {
    }

    /// <summary>Attaches all four channels.</summary>
    public static InheritedProbe<T> Attach(RecordingHost host, StyledProperty<T> property)
    {
        var probe = new InheritedProbe<T>();
        host.AddObserver(property, new TypedSink(probe));
        host.AddObserver(property, new UntypedSink(probe));
        host.InheritedHook += args =>
        {
            if (ReferenceEquals(args.Property, property))
                probe.Inherited.Add((args.GetOldValue<T>(), args.GetNewValue<T>(), args.Priority));
        };
        host.VirtualHook += args =>
        {
            if (ReferenceEquals(args.Property, property))
                probe.OrdinaryVirtual.Add((args.GetOldValue<T>(), args.GetNewValue<T>(), args.Priority));
        };
        return probe;
    }

    /// <summary>
    /// Asserts exactly one propagated delivery (<paramref name="oldValue"/> →
    /// <paramref name="newValue"/>, <paramref name="priority"/>) on the typed, untyped, and A3
    /// channels, with the ordinary virtual silent (PD22), then clears.
    /// </summary>
    public void AssertSinglePropagated(T oldValue, T newValue, BindingPriority priority)
    {
        var expected = (oldValue, newValue, priority);
        Assert.Equal([expected], Typed);
        Assert.Equal([((object?)oldValue, (object?)newValue, priority)], Untyped);
        Assert.Equal([expected], Inherited);
        Assert.Empty(OrdinaryVirtual);
        Clear();
    }

    /// <summary>Asserts zero deliveries on every channel.</summary>
    public void AssertSilent()
    {
        Assert.Empty(Typed);
        Assert.Empty(Untyped);
        Assert.Empty(Inherited);
        Assert.Empty(OrdinaryVirtual);
    }

    /// <summary>Clears all recorded deliveries.</summary>
    public void Clear()
    {
        Typed.Clear();
        Untyped.Clear();
        Inherited.Clear();
        OrdinaryVirtual.Clear();
    }

    private sealed class TypedSink(InheritedProbe<T> probe) : IValueObserver<T>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
            => probe.Typed.Add((oldValue, newValue, priority));
    }

    private sealed class UntypedSink(InheritedProbe<T> probe) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => probe.Untyped.Add((oldValue, newValue, priority));
    }
}

/// <summary>A recording typed observer with an optional reentrancy callback (invoked after the delivery is recorded, so sequence logs reflect delivery order).</summary>
public sealed class TypedRecorder<T> : IValueObserver<T>
{
    /// <summary>Deliveries in order.</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> Deliveries = [];

    /// <summary>Optional reentrancy hook, invoked after the delivery is recorded.</summary>
    public Action<T, T>? Callback;

    /// <summary>Optional shared sequence log entry prefix; appends "prefix(old->new,priority)".</summary>
    public List<string>? SequenceLog;

    /// <summary>The label used in <see cref="SequenceLog"/> entries.</summary>
    public string Label = "typed";

    public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
    {
        Deliveries.Add((oldValue, newValue, priority));
        SequenceLog?.Add($"{Label}({oldValue}->{newValue},{priority})");
        Callback?.Invoke(oldValue, newValue);
    }
}

/// <summary>A recording untyped observer (ledger A10 channel).</summary>
public sealed class UntypedRecorder : IUntypedValueObserver
{
    /// <summary>Deliveries in order.</summary>
    public readonly List<(object? Old, object? New, BindingPriority Priority)> Deliveries = [];

    /// <summary>Optional shared sequence log.</summary>
    public List<string>? SequenceLog;

    /// <summary>The label used in <see cref="SequenceLog"/> entries.</summary>
    public string Label = "untyped";

    public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
    {
        Deliveries.Add((oldValue, newValue, priority));
        SequenceLog?.Add($"{Label}({oldValue}->{newValue},{priority})");
    }
}

/// <summary>
/// The §0.2 "notify asserts all four ordinary channels" probe: attaches a typed observer, an
/// untyped observer, and a virtual-channel hook over one property on a <see cref="RecordingHost"/>.
/// (The metadata <c>Changed</c> channel is registration-time configuration; rows exercising it
/// register row-local properties with recording callbacks.)
/// </summary>
public sealed class Probe<T>
{
    /// <summary>Typed-channel deliveries.</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> Typed = [];

    /// <summary>Untyped-channel deliveries.</summary>
    public readonly List<(object? Old, object? New, BindingPriority Priority)> Untyped = [];

    /// <summary>Virtual-channel deliveries (values extracted inside the notification window).</summary>
    public readonly List<(T Old, T New, BindingPriority Priority)> Virtual = [];

    private Probe()
    {
    }

    /// <summary>Attaches all three observable channels.</summary>
    public static Probe<T> Attach(RecordingHost host, StyledProperty<T> property)
    {
        var probe = new Probe<T>();
        host.AddObserver(property, new TypedSink(probe));
        host.AddObserver(property, new UntypedSink(probe));
        host.VirtualHook += args =>
        {
            if (ReferenceEquals(args.Property, property))
                probe.Virtual.Add((args.GetOldValue<T>(), args.GetNewValue<T>(), args.Priority));
        };
        return probe;
    }

    /// <summary>Asserts exactly one delivery of (<paramref name="oldValue"/> → <paramref name="newValue"/>, <paramref name="priority"/>) on every channel, then clears.</summary>
    public void AssertSingleNotify(T oldValue, T newValue, BindingPriority priority)
    {
        var expected = (oldValue, newValue, priority);
        Assert.Equal([expected], Typed);
        Assert.Equal([((object?)oldValue, (object?)newValue, priority)], Untyped);
        Assert.Equal([expected], Virtual);
        Clear();
    }

    /// <summary>Asserts zero deliveries on every channel.</summary>
    public void AssertSilent()
    {
        Assert.Empty(Typed);
        Assert.Empty(Untyped);
        Assert.Empty(Virtual);
    }

    /// <summary>Clears all recorded deliveries.</summary>
    public void Clear()
    {
        Typed.Clear();
        Untyped.Clear();
        Virtual.Clear();
    }

    private sealed class TypedSink(Probe<T> probe) : IValueObserver<T>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, T oldValue, T newValue, BindingPriority priority)
            => probe.Typed.Add((oldValue, newValue, priority));
    }

    private sealed class UntypedSink(Probe<T> probe) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => probe.Untyped.Add((oldValue, newValue, priority));
    }
}
