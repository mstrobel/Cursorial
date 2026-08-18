using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;

// ReSharper disable UnusedParameter.Local

namespace Cursorial.UI.Data;

/// <summary>
/// The reflection-lane binding expression (design doc §6.2–§6.9) — value access is per-hop
/// (<see cref="PropertyAccessor"/> over the parsed <see cref="BindingPath"/>) and the push is boxed.
/// The lifecycle (anchoring, the source-notification ladder dispatch, write-back, echo suppression,
/// cross-thread coalescing, eviction-aware disposal) is shared with the compiled lane via
/// <see cref="BindingExpressionCore"/>; this class supplies only the per-hop wiring + leaf write.
/// </summary>
internal sealed class ReflectionBindingExpression : BindingExpressionCore
{
    private readonly Binding _reflectionBinding;
    private readonly BindingPath _path;
    private Node[] _nodes;                         // one per path segment
    private int _wiredCount;                       // number of nodes currently wired
    private bool _rung3Warned;

    public ReflectionBindingExpression(Binding binding, in BindingActivationContext context)
        : base(binding, in context)
    {
        _reflectionBinding = binding;
        _path = binding.GetPath();
        _nodes = _path.SegmentCount == 0 ? [] : new Node[_path.SegmentCount];
        Activate(); // after the lane fields are set (the base ctor sets no lane state and makes no virtual call)
    }

    // ───────────────────────────── lane hooks ─────────────────────────────

    private protected override IValueConverter? Converter => _reflectionBinding.Converter;

    private protected override object? ConverterParameter => _reflectionBinding.ConverterParameter;

    private protected override int SegmentCount => _path.SegmentCount;

    private protected override int PostWriteRereadIndex => _path.SegmentCount - 1; // re-read from the leaf

    private protected override void WireValueGraph(int fromIndex) => WireFrom(fromIndex);

    private protected override void RewireFrom(int index) => RewireFromChangedHop(index);

    private protected override string DescribePath() => _path.IsEmpty ? string.Empty : _path.ToString();

    private protected override void DisposeLaneSpecific() => UnwireFrom(0);

    // ───────────────────────────── wiring (per-node subscription) ─────────────────────────────

    private void WireFrom(int index)
    {
        if (_path.SegmentCount == 0)
        {
            // Empty path — the source itself.
            PushToTarget(_root);
            return;
        }

        UnwireFrom(index);

        var current = index == 0 ? _root : _nodes[index - 1].Value;
        for (var i = index; i < _path.SegmentCount; i++)
        {
            if (current is null)
            {
                _wiredCount = i;
                ProduceFromLeaf(UIProperty.UnsetValue);
                return;
            }

            ref var node = ref _nodes[i];
            node.Instance = current;
            node.Accessor = ResolveAccessor(current, in _path.Segments[i]);

            if (node.Accessor is UnresolvableAccessor)
            {
                _wiredCount = i;
                Status = BindingStatus.PathError;
                MaybeTrace(BindingFailureKind.PathError, BindingTraceLevel.Warning,
                    $"path step '{node.Accessor.MemberName}' could not be resolved on '{current.GetType().Name}'.");
                ProduceFromLeaf(UIProperty.UnsetValue);
                return;
            }

            var accessor = node.Accessor!;
            SubscribeNode(ref node, i);
            node.Value = accessor.GetValue(current);
            current = node.Value;
        }

        _wiredCount = _path.SegmentCount;
        ReevaluateLeafWritability();
        ProduceFromLeaf(_nodes[_path.SegmentCount - 1].Value);
    }

    /// <summary>
    /// Re-evaluates leaf writability after a (re)wire (BD10): the leaf's declaring type can change on
    /// an intermediate identity swap, so a TwoWay binding that degraded against a read-only leaf
    /// re-enables write-back when a rewired leaf proves writable, and vice-versa.
    /// </summary>
    private void ReevaluateLeafWritability()
    {
        if (_effectiveMode != BindingMode.TwoWay || _path.SegmentCount == 0)
            return;

        ref var leaf = ref _nodes[_path.SegmentCount - 1];
        if (leaf.Accessor is null)
            return;

        _watchModeDegraded = !leaf.Accessor.CanWrite;
    }

    private PropertyAccessor ResolveAccessor(object instance, in PathSegment segment)
        => segment.Kind switch
        {
            PathSegmentKind.IntIndexer => AccessorCache.ResolveIntIndexer(instance, segment.IntIndex),
            PathSegmentKind.StringIndexer => AccessorCache.ResolveStringIndexer(instance, segment.Name!) ?? new UnresolvableAccessor($"['{segment.Name}']"),
            _ => AccessorCache.ResolveProperty(instance, in segment)
        };

    private void SubscribeNode(ref Node node, int index)
    {
        // OneTime / OneWayToSource skip path subscriptions entirely (BD11).
        if (_effectiveMode is BindingMode.OneTime or BindingMode.OneWayToSource)
            return;

        var instance = node.Instance!;
        var accessor = node.Accessor!;

        // UIObject hop → AddObserver (no reflection, no INPC).
        if (accessor is UIPropertyAccessor uiAccessor && instance is UIObject uiObject)
        {
            node.Subscription = SubscriptionKind.UIObserver;
            node.Token = uiObject.AddObserver(uiAccessor.Property, new HopObserver(this, index));
            return;
        }

        // Indexer hop → INotifyCollectionChanged + INPC "Item[]".
        if (accessor is ListIndexerAccessor or ReflectionIndexerAccessor or DictionaryAccessor)
        {
            if (instance is INotifyCollectionChanged incc)
            {
                node.Subscription = SubscriptionKind.Incc;
                NotifyCollectionChangedEventHandler handler = (_, _) => DispatchSourceChange(index);
                incc.CollectionChanged += handler;
                node.InccHandler = handler;
            }

            if (instance is INotifyPropertyChanged inpcCollection)
            {
                // Honor the "Item[]" convention as a second subscription on the same instance.
                node.Subscription = node.Subscription == SubscriptionKind.Incc ? SubscriptionKind.InccAndInpc : SubscriptionKind.Inpc;
                inpcCollection.PropertyChanged += node.InpcHandler = (sender, e) => OnInpcChanged(index, sender, e);
            }

            return;
        }

        // CLR property hop → the source ladder.
        switch (accessor.Rung)
        {
            case NotificationRung.Inpc when instance is INotifyPropertyChanged inpc:
                node.Subscription = SubscriptionKind.Inpc;
                inpc.PropertyChanged += node.InpcHandler = (sender, e) => OnInpcChanged(index, sender, e);
                break;

            case NotificationRung.ChangedEvent when accessor.ChangedEvent is { } changedEvent:
                node.Subscription = SubscriptionKind.ChangedEvent;
                node.ChangedEventDelegate = SubscribeChangedEvent(changedEvent, instance, index);
                node.ChangedEventInfo = changedEvent;
                break;

            case NotificationRung.ParentChangeOnly:
                // Observed-on-parent-change only: a ONE-TIME Info diagnostic (matrix B31). The flag
                // keeps a repeated parent swap (B32) from re-emitting it on every rewire.
                node.Subscription = SubscriptionKind.None;
                if (!_rung3Warned)
                {
                    _rung3Warned = true;
                    MaybeTrace(BindingFailureKind.None, BindingTraceLevel.Verbose,
                        $"hop '{accessor.MemberName}' on '{instance.GetType().Name}' is not observable " +
                        "(no INotifyPropertyChanged, no [Name]Changed event); it re-reads only on a parent change.");
                }

                break;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "TArgs is the changed-event's EventArgs-derived parameter — always a reference type, and reference-type generic instantiations run on shared code that is always available under AOT.")]
    private Delegate SubscribeChangedEvent(EventInfo changedEvent, object instance, int index)
    {
        // The discovered event is EventHandler or EventHandler<EventArgs>-compatible (2-arg
        // (object, EventArgs-derived) Invoke). Build a matching delegate that re-reads on raise.
        var handlerType = changedEvent.EventHandlerType!;
        var invoke = handlerType.GetMethod("Invoke")!;
        var argsType = invoke.GetParameters()[1].ParameterType;

        // EventHandler<TArgs> path.
        if (handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(EventHandler<>))
        {
            var open = typeof(ReflectionBindingExpression).GetMethod(nameof(MakeGenericChangedHandler), BindingFlags.NonPublic | BindingFlags.Instance)!;
            var closed = open.MakeGenericMethod(argsType);
            var del = (Delegate)closed.Invoke(this, [index])!;
            changedEvent.AddEventHandler(instance, del);
            return del;
        }

        // Plain EventHandler.
        EventHandler plain = (_, _) => DispatchSourceChange(index);
        changedEvent.AddEventHandler(instance, plain);
        return plain;
    }

    private EventHandler<TArgs> MakeGenericChangedHandler<TArgs>(int index) where TArgs : EventArgs
        => (_, _) => DispatchSourceChange(index);

    private void UnwireFrom(int index)
    {
        for (var i = _wiredCount - 1; i >= index; i--)
        {
            ref var node = ref _nodes[i];
            UnsubscribeNode(ref node);
            node = default;
        }

        if (index < _wiredCount)
            _wiredCount = index;
    }

    private void UnsubscribeNode(ref Node node)
    {
        switch (node.Subscription)
        {
            case SubscriptionKind.UIObserver:
                node.Token?.Dispose();
                break;
            case SubscriptionKind.Inpc when node.Instance is INotifyPropertyChanged inpc && node.InpcHandler is { } h:
                inpc.PropertyChanged -= h;
                break;
            case SubscriptionKind.Incc when node.Instance is INotifyCollectionChanged incc && node.InccHandler is { } ih:
                incc.CollectionChanged -= ih;
                break;
            case SubscriptionKind.InccAndInpc:
                if (node.Instance is INotifyCollectionChanged incc2 && node.InccHandler is { } ih2)
                    incc2.CollectionChanged -= ih2;
                if (node.Instance is INotifyPropertyChanged inpc2 && node.InpcHandler is { } h2)
                    inpc2.PropertyChanged -= h2;
                break;
            case SubscriptionKind.ChangedEvent when node.ChangedEventInfo is { } info && node.ChangedEventDelegate is { } del && node.Instance is { } inst:
                info.RemoveEventHandler(inst, del);
                break;
        }

        node.Token = null;
        node.InpcHandler = null;
        node.InccHandler = null;
        node.ChangedEventDelegate = null;
        node.ChangedEventInfo = null;
        node.Subscription = SubscriptionKind.None;
    }

    // ───────────────────────────── source change handlers ─────────────────────────────

    private void OnInpcChanged(int index, object? sender, PropertyChangedEventArgs e)
    {
        if (IsDisposed)
            return;

        // Apply the property-name filter (the accessor's MemberName for an already-wired node is stable —
        // set on the UI thread before this subscription attached) before the shared dispatch.
        var memberName = _nodes[index].Accessor?.MemberName;
        var matches = string.IsNullOrEmpty(e.PropertyName) ||
            string.Equals(e.PropertyName, memberName, StringComparison.Ordinal) ||
            (IsIndexerNode(index) && string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal));
        if (!matches)
            return;

        DispatchSourceChange(index);
    }

    private bool IsIndexerNode(int index)
        => _nodes[index].Accessor is ListIndexerAccessor or ReflectionIndexerAccessor or DictionaryAccessor;

    /// <summary>
    /// Hop <paramref name="index"/> notified: its <em>value</em> changed but its instance is
    /// unchanged. Re-read node <paramref name="index"/> from its existing instance/accessor, then
    /// rewire the tail below it (spec §3.3 "re-read hop i, rewire below, push").
    /// </summary>
    private void RewireFromChangedHop(int index)
    {
        if (index >= _wiredCount || _nodes[index].Accessor is null || _nodes[index].Instance is null)
        {
            // The notifying node is no longer wired (a parent swap already rewired it) — re-wire the
            // whole chain from this hop to be safe.
            WireFrom(index);
            return;
        }

        _nodes[index].Value = _nodes[index].Accessor!.GetValue(_nodes[index].Instance!);
        WireFrom(index + 1);
    }

    // ───────────────────────────── target → source write-back ─────────────────────────────

    private protected override void WriteSourceLeaf()
    {
        // OneWayToSource re-resolves the chain from the anchor on every write (BD11).
        if (_effectiveMode == BindingMode.OneWayToSource)
        {
            if (!ResolveOneWayToSourceChain(out var leafAccessor, out var leafInstance))
                return;
            WriteConvertedLeaf(leafAccessor.CanWrite, leafAccessor.ValueType ?? typeof(object),
                v => leafAccessor.SetValue(leafInstance, v));
            return;
        }

        // TwoWay: write to the live leaf node.
        if (_wiredCount < _path.SegmentCount || _path.SegmentCount == 0)
            return;

        var accessor = _nodes[_path.SegmentCount - 1].Accessor;
        var instance = _nodes[_path.SegmentCount - 1].Instance;
        if (accessor is null || instance is null)
            return;

        WriteConvertedLeaf(accessor.CanWrite, accessor.ValueType ?? typeof(object),
            v => accessor.SetValue(instance, v));
    }

    private bool ResolveOneWayToSourceChain(out PropertyAccessor leafAccessor, out object leafInstance)
    {
        leafAccessor = null!;
        leafInstance = null!;
        if (_root is null || _path.SegmentCount == 0)
            return false;

        object? current = _root;
        for (var i = 0; i < _path.SegmentCount - 1; i++)
        {
            if (current is null)
                return false;
            var accessor = ResolveAccessor(current, in _path.Segments[i]);
            current = accessor.GetValue(current);
        }

        if (current is null)
            return false;

        leafInstance = current;
        leafAccessor = ResolveAccessor(current, in _path.Segments[^1]);
        return leafAccessor is not UnresolvableAccessor;
    }

    // ───────────────────────────── node + observer types ─────────────────────────────

    private enum SubscriptionKind : byte
    {
        None,
        UIObserver,
        Inpc,
        Incc,
        InccAndInpc,
        ChangedEvent
    }

    private struct Node
    {
        public object? Instance;
        public object? Value;
        public PropertyAccessor? Accessor;
        public SubscriptionKind Subscription;
        public IDisposable? Token;
        public PropertyChangedEventHandler? InpcHandler;
        public NotifyCollectionChangedEventHandler? InccHandler;
        public Delegate? ChangedEventDelegate;
        public EventInfo? ChangedEventInfo;
    }

    private sealed class HopObserver(ReflectionBindingExpression owner, int index) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => owner.DispatchSourceChange(index);
    }
}
