using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Cursorial.UI.Input;

// ReSharper disable UnusedParameter.Local

namespace Cursorial.UI.Data;

/// <summary>
/// The reflection-lane binding expression (design doc §6.2–§6.9) — the runtime machinery for one
/// armed <see cref="Binding"/> on one target: source anchoring (DataContext / Source / ElementName /
/// RelativeSource), the source-notification ladder (INPC / <c>[Name]Changed</c> event /
/// parent-change degradation) + INCC, the forward value pipeline, two-way / one-way-to-source
/// write-back with echo suppression, triggers, cross-thread coalescing, and eviction-aware disposal.
/// </summary>
internal sealed class ReflectionBindingExpression : BindingExpressionBase, IValueEvictionListener
{
    private readonly Binding _binding;
    private readonly BindingPath _path;
    private readonly UIObject _target;
    private readonly UIProperty _targetProperty;
    private readonly UIElement? _anchorElement;
    private readonly ValueFrame? _hostFrame;
    private readonly Action<object?>? _watchCallback;
    private readonly BindingMode _effectiveMode;
    private readonly UpdateSourceTrigger _trigger;

    private AnchorKind _anchorKind;
    private object? _root;                       // the resolved source root object
    private Node[] _nodes;                        // one per path segment
    private int _wiredCount;                      // number of nodes currently wired
    private BindingEntryBase? _entry;             // null for watch-only / OneWayToSource-passive / DirectProperty
    private IDisposable? _targetObserverToken;    // the write-back target observer / OWS anchor write retarget
    private IDisposable? _anchorObserverToken;    // the DataContext observer (default-source rebind)
    private Action<UIElement>? _editCommitHandler;
    private bool _sourceDirty;                    // a pending LostFocus/Explicit write
    private bool _isPushingToTarget;
    private bool _isWritingToSource;
    private bool _lostFocusSubscribed;
    private bool _editCommitSubscribed;
    private bool _watchModeDegraded;
    private object? _lastPushedValue = NoPushSentinel;
    private object? _lastProducedValue;
    private BindingFailureKind _lastFailure;
    private int _dirtyBitmask;                    // cross-thread coalescing (Interlocked.Or)
    private bool _drainQueued;
    private bool _readOnlyLeafWarned;
    private bool _rung3Warned;

    private static readonly object NoPushSentinel = new();

    public ReflectionBindingExpression(Binding binding, in BindingActivationContext context)
    {
        _binding = binding;
        _path = binding.GetPath();
        _target = context.Target;
        _targetProperty = context.TargetProperty;
        _anchorElement = context.Anchor;
        _hostFrame = context.HostFrame;
        _watchCallback = context.WatchCallback;
        _trigger = binding.UpdateSourceTrigger == UpdateSourceTrigger.Default
            ? UpdateSourceTrigger.PropertyChanged
            : binding.UpdateSourceTrigger;
        _nodes = _path.SegmentCount == 0 ? [] : new Node[_path.SegmentCount];

        _effectiveMode = ResolveEffectiveMode(binding, _targetProperty, context.IsWatchOnly);

        Activate();
    }

    public override BindingBase ParentBinding => _binding;

    public override UIObject Target => _target;

    public override UIProperty TargetProperty => _targetProperty;

    public override BindingMode EffectiveMode => _watchModeDegraded ? BindingMode.OneWay : _effectiveMode;

    internal override BindingLane Lane => _watchCallback is not null
        ? BindingLane.WatchOnly
        : _targetProperty.IsDirect
            ? BindingLane.DirectProperty
            : _hostFrame is not null
                ? BindingLane.FrameHosted
                : BindingLane.LocalValue;

    public object? CurrentValue => _lastProducedValue;

    // ───────────────────────────── activation & anchoring ─────────────────────────────

    private void Activate()
    {
        _anchorKind = DetermineAnchorKind();
        BindingRegistry.GetOrCreate(_target).Register(this);
        BindingLeakTracker.Track(this, _target, _binding.Path, BindingRegistry.DescribeTarget(_target, _targetProperty));

        WireAnchorObserver();
        ResolveRootAndWire();
    }

    private BindingMode ResolveEffectiveMode(Binding binding, UIProperty property, bool watchOnly)
    {
        if (watchOnly)
            return BindingMode.OneWay;
        if (binding.Mode != BindingMode.Default)
            return binding.Mode;
        var effects = property.IsDirect ? PropertyEffects.None : property.GetEffects(_target.GetType());
        return (effects & PropertyEffects.BindsTwoWayByDefault) != 0 ? BindingMode.TwoWay : BindingMode.OneWay;
    }

    private AnchorKind DetermineAnchorKind()
    {
        if (_binding.Source is not null)
            return AnchorKind.Source;
        if (_binding.ElementName is not null)
            return AnchorKind.ElementName;
        if (_binding.RelativeSource is { } rs)
        {
            return rs.Mode switch
            {
                RelativeSourceMode.Self => AnchorKind.Self,
                RelativeSourceMode.TemplatedParent => AnchorKind.TemplatedParent,
                _ => AnchorKind.FindAncestor
            };
        }

        // Default source. The DataContext-as-target special case anchors on the LOGICAL PARENT's
        // DataContext (BD2). Watch-only anchors on its anchor's DataContext like a normal binding.
        if (!_targetProperty.IsDirect && ReferenceEquals(_targetProperty, DataContextSupport.DataContextProperty) && _watchCallback is null)
            return AnchorKind.ParentDataContext;

        return AnchorKind.DataContext;
    }

    private void WireAnchorObserver()
    {
        switch (_anchorKind)
        {
            case AnchorKind.DataContext when _anchorElement is not null:
                _anchorObserverToken = _anchorElement.AddObserver(
                    DataContextSupport.DataContextProperty, new AnchorObserver(this));
                break;
            case AnchorKind.ParentDataContext:
                // Re-anchored on attach/detach; the observer is (re)installed in ResolveRootAndWire.
                SubscribeTreeEvents();
                break;
            case AnchorKind.ElementName:
            case AnchorKind.FindAncestor:
                SubscribeTreeEvents();
                break;
            case AnchorKind.TemplatedParent:
                // A TemplateBinding / RelativeSource.TemplatedParent binding installed BEFORE the part
                // is stamped (the build delegate runs before StampTemplatedParent) parks SourceMissing;
                // it re-resolves when the stamp arrives (template parts attach visually, not logically,
                // so AttachedToLogicalTree never fires for them).
                if (_anchorElement is not null)
                    _anchorElement.TemplatedParentChanged += OnAnchorTemplatedParentChanged;
                break;
        }
    }

    private void SubscribeTreeEvents()
    {
        if (_anchorElement is null)
            return;
        _anchorElement.AttachedToLogicalTree += OnAnchorTreeChanged;
        _anchorElement.DetachedFromLogicalTree += OnAnchorTreeChanged;
    }

    private void UnsubscribeTreeEvents()
    {
        if (_anchorElement is null)
            return;
        _anchorElement.AttachedToLogicalTree -= OnAnchorTreeChanged;
        _anchorElement.DetachedFromLogicalTree -= OnAnchorTreeChanged;
        _anchorElement.TemplatedParentChanged -= OnAnchorTemplatedParentChanged;
    }

    private void OnAnchorTemplatedParentChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            ResolveRootAndWire();
    }

    private void OnAnchorTreeChanged(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        if (IsDisposed)
            return;
        ResolveRootAndWire();
    }

    private void ResolveRootAndWire()
    {
        if (IsDisposed)
            return;

        // Tear down any prior parent-DataContext observer before re-resolving.
        if (_anchorKind == AnchorKind.ParentDataContext)
        {
            _anchorObserverToken?.Dispose();
            _anchorObserverToken = null;
        }

        var resolved = ResolveRoot(out var newRoot, out var failure);
        if (!resolved)
        {
            UnwireFrom(0);
            _root = null;
            Status = failure == BindingFailureKind.NameNotFound ? BindingStatus.PathError : BindingStatus.SourceMissing;

            // SourceMissing normally parks silently (it recovers on attach). The default-source /
            // non-UIElement-target case (B44 / BD13) can NEVER recover — there is no element to carry
            // a DataContext — so trace it as an install-time error with a tailored message.
            var permanentNoDataContext = failure == BindingFailureKind.SourceMissing
                && _anchorElement is null
                && _anchorKind is AnchorKind.DataContext or AnchorKind.ParentDataContext;
            if (failure != BindingFailureKind.SourceMissing || permanentNoDataContext || _watchCallback is not null)
                MaybeTrace(failure, BindingTraceLevel.Warning, FailureMessage(failure));
            ProduceFallbackOrUnset();
            return;
        }

        // Re-install the parent-DataContext observer on the (possibly new) logical parent.
        if (_anchorKind == AnchorKind.ParentDataContext && _anchorElement?.LogicalParent is { } parent)
        {
            _anchorObserverToken = parent.AddObserver(
                DataContextSupport.DataContextProperty, new AnchorObserver(this));
        }

        _root = newRoot;
        Status = BindingStatus.Active;
        WireFrom(0);

        // OneWayToSource: keep the target observer (it never produces into the target, BD11) and push
        // target → source at activation.
        if (_effectiveMode == BindingMode.OneWayToSource && !IsDisposed)
        {
            WireTargetObserver();
            WriteToSource(force: true);
        }
    }

    private bool ResolveRoot(out object? root, out BindingFailureKind failure)
    {
        failure = BindingFailureKind.None;
        switch (_anchorKind)
        {
            case AnchorKind.Source:
                root = _binding.Source;
                return root is not null;

            case AnchorKind.DataContext:
                if (_anchorElement is null)
                {
                    root = null;
                    failure = BindingFailureKind.SourceMissing;
                    return false;
                }

                root = _anchorElement.GetValue(DataContextSupport.DataContextProperty);
                return root is not null;

            case AnchorKind.ParentDataContext:
                var logicalParent = _anchorElement?.LogicalParent;
                if (logicalParent is null)
                {
                    root = null;
                    failure = BindingFailureKind.SourceMissing;
                    return false;
                }

                root = logicalParent.GetValue(DataContextSupport.DataContextProperty);
                return root is not null;

            case AnchorKind.Self:
                root = _anchorElement;
                return root is not null;

            case AnchorKind.TemplatedParent:
                root = _anchorElement?.TemplatedParent;
                if (root is null)
                    failure = BindingFailureKind.SourceMissing;
                return root is not null;

            case AnchorKind.ElementName:
                // Name scopes live on the LOGICAL tree (FindEnclosing walks LogicalParent), so resolve
                // once the anchor is logically attached — not gated on visual attachment, which lands a
                // step later (AdoptChild raises AttachedToLogicalTree before AddVisualChild sets the
                // visual root). A forward reference during build (no logical parent yet) parks
                // SourceMissing without a trace; a name unresolved after attach traces NameNotFound (B123).
                if (_anchorElement is null || _anchorElement.LogicalParent is null)
                {
                    root = null;
                    failure = BindingFailureKind.SourceMissing;
                    return false;
                }

                root = NameScope.FindEnclosing(_anchorElement)?.Find(_binding.ElementName!);
                if (root is null)
                    failure = BindingFailureKind.NameNotFound;
                return root is not null;

            case AnchorKind.FindAncestor:
                root = ResolveAncestor(out failure);
                return root is not null;

            default:
                root = null;
                failure = BindingFailureKind.SourceMissing;
                return false;
        }
    }

    private object? ResolveAncestor(out BindingFailureKind failure)
    {
        failure = BindingFailureKind.None;
        var rs = _binding.RelativeSource!;
        if (_anchorElement is null)
        {
            failure = BindingFailureKind.SourceMissing;
            return null;
        }

        // No logical parent yet ⇒ park until attach (the walk re-runs on AttachedToLogicalTree).
        if (_anchorElement.LogicalParent is null)
        {
            failure = BindingFailureKind.SourceMissing;
            return null;
        }

        var matches = 0;
        for (var node = _anchorElement.LogicalParent; node is not null; node = node.LogicalParent)
        {
            if (rs.AncestorType!.IsInstanceOfType(node) && ++matches == rs.AncestorLevel)
                return node;
        }

        failure = BindingFailureKind.AncestorNotFound;
        return null;
    }

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
                NotifyCollectionChangedEventHandler handler = (_, _) => OnHopChanged(index);
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
        EventHandler plain = (_, _) => OnHopChanged(index);
        changedEvent.AddEventHandler(instance, plain);
        return plain;
    }

    private EventHandler<TArgs> MakeGenericChangedHandler<TArgs>(int index) where TArgs : EventArgs
        => (_, _) => OnHopChanged(index);

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

        // Apply the property-name filter BEFORE the cross-thread branch too: an unrelated property
        // change on the same INPC source should not wake a drain (the accessor's MemberName for an
        // already-wired node is stable — set on the UI thread before this subscription attached).
        var memberName = _nodes[index].Accessor?.MemberName;
        var matches = string.IsNullOrEmpty(e.PropertyName) ||
            string.Equals(e.PropertyName, memberName, StringComparison.Ordinal) ||
            (IsIndexerNode(index) && string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal));
        if (!matches)
            return;

        if (BindingDispatcher.Current is { } dispatcher && !dispatcher.CheckAccess())
        {
            QueueCrossThread(index);
            return;
        }

        OnHopChanged(index);
    }

    private bool IsIndexerNode(int index)
        => _nodes[index].Accessor is ListIndexerAccessor or ReflectionIndexerAccessor or DictionaryAccessor;

    private void OnHopChanged(int index)
    {
        if (IsDisposed)
            return;

        if (BindingDispatcher.Current is { } dispatcher && !dispatcher.CheckAccess())
        {
            QueueCrossThread(index);
            return;
        }

        if (_isWritingToSource)
        {
            // A source INPC raised during our write — coalesce into one post-write re-read (BD12).
            _sourceDirtyDuringWrite = true;
            return;
        }

        RewireFromChangedHop(index);
    }

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

    private bool _sourceDirtyDuringWrite;

    private void QueueCrossThread(int index)
    {
        Interlocked.Or(ref _dirtyBitmask, 1 << Math.Min(index, 30));
        if (BindingDispatcher.Current is not { } dispatcher)
            return;

        // Post one coalesced drain; the bitmask OR collapses N changes (BD20).
        lock (this)
        {
            if (_drainQueued)
                return;
            _drainQueued = true;
        }

        dispatcher.Post(DrainCrossThread);
    }

    private void DrainCrossThread()
    {
        lock (this)
            _drainQueued = false;

        if (IsDisposed)
            return;

        var mask = Interlocked.Exchange(ref _dirtyBitmask, 0);
        if (mask == 0)
            return;

        // Rewire from the lowest set bit (BD20): re-read that hop, rewire the tail.
        var lowest = System.Numerics.BitOperations.TrailingZeroCount(mask);
        RewireFromChangedHop(lowest);
    }

    // ───────────────────────────── forward pipeline ─────────────────────────────

    private void ProduceFromLeaf(object? rawLeaf) => PushToTarget(rawLeaf);

    private void ProduceFallbackOrUnset() => PushToTarget(UIProperty.UnsetValue);

    private void PushToTarget(object? rawValue)
    {
        if (IsDisposed)
            return;

        var result = RunPipeline(rawValue, out var isUnset);
        _lastProducedValue = isUnset ? UIProperty.UnsetValue : result;

        if (_watchCallback is not null)
        {
            _watchCallback(isUnset ? UIProperty.UnsetValue : result);
            return;
        }

        if (_targetProperty.IsDirect)
        {
            PushToDirectProperty(result, isUnset);
            return;
        }

        EnsureEntry();
        if (_entry is null)
            return;

        _isPushingToTarget = true;
        try
        {
            if (isUnset)
            {
                _entry.SetUnset();
                _lastPushedValue = NoPushSentinel;
            }
            else
            {
                _entry.SetValue(result);
                _lastPushedValue = result;
            }
        }
        finally
        {
            _isPushingToTarget = false;
        }
    }

    private object? RunPipeline(object? rawValue, out bool isUnset)
    {
        isUnset = false;

        if (ReferenceEquals(rawValue, UIProperty.UnsetValue))
            return FallbackOrUnset(out isUnset);

        var value = rawValue;
        var culture = _binding.EffectiveCulture;
        var targetType = _targetProperty.PropertyType;

        if (_binding.Converter is { } converter)
        {
            try
            {
                value = converter.Convert(value, targetType, _binding.ConverterParameter, culture);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                MaybeTrace(BindingFailureKind.ConversionFailed, BindingTraceLevel.Warning,
                    $"converter '{converter.GetType().Name}' threw: {ex.Message}.");
                return FallbackOrUnset(out isUnset);
            }

            if (ReferenceEquals(value, UIProperty.UnsetValue))
                return FallbackOrUnset(out isUnset);
        }

        if (value is null && _binding.HasTargetNullValue)
            value = _binding.TargetNullValue;

        if (_binding.StringFormat is { } format)
        {
            if (targetType == typeof(string) || targetType == typeof(object))
            {
                value = string.Format(culture, format, value);
            }
            else
            {
                MaybeTrace(BindingFailureKind.None, BindingTraceLevel.Warning,
                    $"StringFormat '{format}' ignored: target type '{targetType.Name}' is not string or object.");
            }
        }

        return CoerceToTargetType(value, out isUnset);
    }

    private object? CoerceToTargetType(object? value, out bool isUnset)
    {
        isUnset = false;
        var targetType = _targetProperty.PropertyType;
        var converted = ValueConversion.Convert(value, targetType, _binding.EffectiveCulture);
        if (ReferenceEquals(converted, ValueConversion.Failed))
        {
            MaybeTrace(BindingFailureKind.TypeMismatch, BindingTraceLevel.Warning,
                $"value '{value}' could not be converted to target type '{targetType.Name}'.");
            return FallbackOrUnset(out isUnset);
        }

        return converted;
    }

    private object? FallbackOrUnset(out bool isUnset)
    {
        if (_binding.HasFallbackValue)
        {
            var converted = ValueConversion.Convert(_binding.FallbackValue, _targetProperty.PropertyType, _binding.EffectiveCulture);
            if (!ReferenceEquals(converted, ValueConversion.Failed))
            {
                isUnset = false;
                return converted;
            }

            MaybeTrace(BindingFailureKind.TypeMismatch, BindingTraceLevel.Warning,
                $"the fallback value '{_binding.FallbackValue}' could not be converted to target type '{_targetProperty.PropertyType.Name}'.");
        }

        isUnset = true;
        return null;
    }

    private void EnsureEntry()
    {
        if (_entry is not null || _effectiveMode == BindingMode.OneWayToSource)
            return;

        _entry = _hostFrame is null
            ? _target.BindUntyped(_targetProperty, BindingPriority.LocalValue, this)
            : _target.BindInFrameUntyped(_targetProperty, _hostFrame, this);

        WireTargetObserver();
    }

    private void PushToDirectProperty(object? result, bool isUnset)
    {
        WireTargetObserver();
        if (isUnset)
        {
            _target.SetValue(_targetProperty, UIProperty.UnsetValue);
            _lastPushedValue = NoPushSentinel;
        }
        else
        {
            _isPushingToTarget = true;
            try
            {
                _target.SetValue(_targetProperty, result);
                _lastPushedValue = result;
            }
            finally
            {
                _isPushingToTarget = false;
            }
        }
    }

    // ───────────────────────────── target → source write-back ─────────────────────────────

    private void WireTargetObserver()
    {
        if (_targetObserverToken is not null)
            return;
        if (_effectiveMode is not (BindingMode.TwoWay or BindingMode.OneWayToSource))
            return;

        _targetObserverToken = _target.AddObserver(_targetProperty, new TargetObserver(this));

        if (_trigger == UpdateSourceTrigger.LostFocus)
            SubscribeLostFocus();
    }

    private void SubscribeLostFocus()
    {
        if (_lostFocusSubscribed || _anchorElement is null)
            return;

        _anchorElement.AddHandler(UIElement.LostFocusEvent, OnLostFocus);
        _lostFocusSubscribed = true;

        // The terminal-focus-out edit-commit pulse is a second, distinct flush source (B133).
        if (!_editCommitSubscribed && UIApplication.Current?.InputDispatcher is { } dispatcher)
        {
            _editCommitHandler = OnEditCommitRequested;
            dispatcher.EditCommitRequested += _editCommitHandler;
            _editCommitSubscribed = true;
        }
    }

    private void OnLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (IsDisposed || !_sourceDirty)
            return;
        FlushSource();
    }

    private void OnEditCommitRequested(UIElement focused)
    {
        if (IsDisposed || !_sourceDirty)
            return;
        if (!ReferenceEquals(focused, _anchorElement))
            return;
        FlushSource();
    }

    private void OnTargetValueChanged(BindingPriority priority)
    {
        if (IsDisposed || _isPushingToTarget)
            return; // BD8 step 1: synchronous self-echo.

        // BD8 step 3: animated values never round-trip (the args carry the replaced lane's priority).
        if (priority == BindingPriority.Animation)
            return;

        // BD8 step 2: skip if the new value equals the last pushed value (asynchronous echo). The
        // discriminator uses the TARGET PROPERTY's effective comparer (design doc §6.6 / spec §3.6
        // step 2), not object.Equals — a custom comparer (e.g. OrdinalIgnoreCase) must not let a
        // case-only resurfaced own value round-trip to the source.
        if (_effectiveMode != BindingMode.OneWayToSource)
        {
            var current = _target.GetValue(_targetProperty);
            if (!ReferenceEquals(_lastPushedValue, NoPushSentinel) &&
                _targetProperty.AreValuesEqualUntyped(_target.GetType(), current, _lastPushedValue))
            {
                return;
            }
        }

        switch (_trigger)
        {
            case UpdateSourceTrigger.PropertyChanged:
                WriteToSource(force: false);
                break;
            case UpdateSourceTrigger.LostFocus:
            case UpdateSourceTrigger.Explicit:
                _sourceDirty = true;
                break;
        }
    }

    private bool ValuesEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        return a.Equals(b);
    }

    private void FlushSource()
    {
        _sourceDirty = false;
        WriteToSource(force: false);
    }

    public override void UpdateSource()
    {
        _target.VerifyAccess();
        if (IsDisposed)
            return;
        _sourceDirty = false;
        WriteToSource(force: true);
    }

    public override void UpdateTarget()
    {
        _target.VerifyAccess();
        if (IsDisposed)
            return;
        WireFrom(0);
    }

    private void WriteToSource(bool force)
    {
        if (IsDisposed)
            return;
        if (_effectiveMode is not (BindingMode.TwoWay or BindingMode.OneWayToSource))
            return;

        // OneWayToSource re-resolves the chain from the anchor on every write (BD11).
        if (_effectiveMode == BindingMode.OneWayToSource)
        {
            if (!ResolveOneWayToSourceChain(out var leafAccessor, out var leafInstance))
                return;
            WriteLeaf(leafAccessor, leafInstance);
            return;
        }

        // TwoWay: write to the live leaf node.
        if (_wiredCount < _path.SegmentCount || _path.SegmentCount == 0)
            return;

        ref var leaf = ref _nodes[_path.SegmentCount - 1];
        if (leaf.Accessor is null || leaf.Instance is null)
            return;

        WriteLeaf(leaf.Accessor, leaf.Instance);
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

    private void WriteLeaf(PropertyAccessor leafAccessor, object leafInstance)
    {
        if (!leafAccessor.CanWrite)
        {
            DegradeToOneWayIfNeeded();
            return;
        }

        var targetValue = _target.GetValue(_targetProperty);
        if (!TryConvertBack(targetValue, leafAccessor.ValueType ?? typeof(object), out var sourceValue))
            return;

        _isWritingToSource = true;
        _sourceDirtyDuringWrite = false;
        try
        {
            leafAccessor.SetValue(leafInstance, sourceValue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            MaybeTrace(BindingFailureKind.SourceUpdateFailed, BindingTraceLevel.Warning,
                $"the source write to '{leafAccessor.MemberName}' failed: {ex.Message}.");
            return;
        }
        finally
        {
            _isWritingToSource = false;
        }

        // A source INPC raised during the write coalesces into one post-write re-read (BD12).
        if (_sourceDirtyDuringWrite && _effectiveMode == BindingMode.TwoWay && !IsDisposed)
        {
            _sourceDirtyDuringWrite = false;
            WireFrom(_path.SegmentCount - 1);
        }
    }

    private bool TryConvertBack(object? targetValue, Type leafType, out object? sourceValue)
    {
        sourceValue = null;
        var culture = _binding.EffectiveCulture;

        // Converter present.
        if (_binding.Converter is { } converter)
        {
            object? result;
            try
            {
                result = converter.ConvertBack(targetValue, leafType, _binding.ConverterParameter, culture);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                MaybeTrace(BindingFailureKind.ConvertBackFailed, BindingTraceLevel.Warning,
                    $"converter '{converter.GetType().Name}'.ConvertBack threw: {ex.Message}.");
                return false;
            }

            if (ReferenceEquals(result, UIProperty.UnsetValue))
            {
                MaybeTrace(BindingFailureKind.ConvertBackFailed, BindingTraceLevel.Warning,
                    "ConvertBack returned UnsetValue; no write.");
                return false;
            }

            sourceValue = result;
            return true;
        }

        // TargetNullValue reverse mapping.
        if (_binding.HasTargetNullValue && ValuesEqual(targetValue, _binding.TargetNullValue))
        {
            sourceValue = null;
            return true;
        }

        // StringFormat reverse parse: only when exactly "{0}".
        if (_binding.StringFormat is { } format)
        {
            if (format != "{0}")
            {
                MaybeTrace(BindingFailureKind.ConvertBackFailed, BindingTraceLevel.Warning,
                    $"the composite StringFormat '{format}' cannot be reverse-parsed; no write.");
                return false;
            }
            // exactly "{0}" → fall through to the type-conversion ladder.
        }

        // No converter, type gap → the conversion ladder.
        var converted = ValueConversion.Convert(targetValue, leafType, culture);
        if (ReferenceEquals(converted, ValueConversion.Failed))
        {
            MaybeTrace(BindingFailureKind.SourceUpdateFailed, BindingTraceLevel.Warning,
                $"the target value '{targetValue}' could not be converted to source type '{leafType.Name}'; no write.");
            return false;
        }

        sourceValue = converted;
        return true;
    }

    private void DegradeToOneWayIfNeeded()
    {
        if (_readOnlyLeafWarned)
            return;
        _readOnlyLeafWarned = true;
        _watchModeDegraded = true; // EffectiveMode reports OneWay; re-evaluated per rewire.
        MaybeTrace(BindingFailureKind.None, BindingTraceLevel.Warning,
            "the source leaf is read-only; the binding degraded to OneWay (BD10).");
    }

    // ───────────────────────────── eviction & disposal ─────────────────────────────

    public void OnEvicted(BindingEntryBase entry)
    {
        if (ReferenceEquals(entry, _entry))
            Dispose(fromEviction: true);
    }

    private protected override void DisposeCore(bool fromEviction)
    {
        UnwireFrom(0);
        _anchorObserverToken?.Dispose();
        _anchorObserverToken = null;
        _targetObserverToken?.Dispose();
        _targetObserverToken = null;
        UnsubscribeTreeEvents();

        if (_lostFocusSubscribed && _anchorElement is not null)
        {
            _anchorElement.RemoveHandler(UIElement.LostFocusEvent, OnLostFocus);
            _lostFocusSubscribed = false;
        }

        if (_editCommitSubscribed && _editCommitHandler is not null && UIApplication.Current?.InputDispatcher is { } dispatcher)
        {
            dispatcher.EditCommitRequested -= _editCommitHandler;
            _editCommitSubscribed = false;
            _editCommitHandler = null;
        }

        if (_entry is not null && !fromEviction)
            _entry.Dispose();
        _entry = null;

        BindingRegistry.Get(_target)?.Unregister(this);
        BindingLeakTracker.Forget(this); // the contract met — the tracker forgets a cleanly disposed expression
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    private void MaybeTrace(BindingFailureKind kind, BindingTraceLevel level, string message)
    {
        _lastFailure = kind != BindingFailureKind.None ? kind : _lastFailure;
        if (!BindingDiagnostics.ShouldConstruct(level, _binding.Trace))
            return;

        var description = BindingRegistry.DescribeTarget(_target, _targetProperty);
        BindingDiagnostics.Record(new BindingTraceEvent(
            level, kind, _binding.Path, description, message, Environment.TickCount64));
    }

    private string FailureMessage(BindingFailureKind kind) => kind switch
    {
        // A default-source binding on a non-UIElement target has no DataContext to anchor on (B44).
        BindingFailureKind.SourceMissing when _anchorElement is null
            && _anchorKind is AnchorKind.DataContext or AnchorKind.ParentDataContext =>
            "non-UIElement target has no DataContext; use Source.",
        BindingFailureKind.SourceMissing => "the binding source (anchor) is unresolved; parked until attach.",
        BindingFailureKind.NameNotFound => $"the element name '{_binding.ElementName}' was not found in scope.",
        BindingFailureKind.AncestorNotFound => $"no ancestor of type '{_binding.RelativeSource?.AncestorType?.Name}' was found.",
        _ => "the binding source could not be resolved."
    };

    internal override BindingExpressionExplanation Explain()
    {
        var chain = _anchorKind == AnchorKind.Source
            ? $"Source({_binding.Source?.GetType().Name})"
            : _anchorKind.ToString();
        if (!_path.IsEmpty)
            chain = $"{chain} → {_path}";

        return new BindingExpressionExplanation(
            Lane, _binding.Path, Status, EffectiveMode, chain, _lastProducedValue, _lastFailure);
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
            => owner.OnHopChanged(index);
    }

    private sealed class AnchorObserver(ReflectionBindingExpression owner) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => owner.ResolveRootAndWire();
    }

    private sealed class TargetObserver(ReflectionBindingExpression owner) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => owner.OnTargetValueChanged(priority);
    }
}
