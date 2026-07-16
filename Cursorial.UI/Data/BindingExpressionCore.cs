using Cursorial.UI.Input;

namespace Cursorial.UI.Data;

/// <summary>
/// The shared binding-expression lifecycle (design doc §6.2–§6.9), extracted at P10/B2 so the
/// reflection lane (<see cref="ReflectionBindingExpression"/>) and the compiled lane
/// (<see cref="CompiledBindingExpression{TSource,TValue}"/>) share <em>one</em> implementation of
/// source anchoring (DataContext / Source / ElementName / RelativeSource), the forward value
/// pipeline, two-way / one-way-to-source write-back with echo suppression, triggers, cross-thread
/// coalescing, and eviction-aware disposal. The two lanes differ only in <b>value access</b>
/// (per-hop accessor vs whole-chain <c>Getter</c>) and <b>push typing</b> (boxed vs typed zero-box) —
/// the abstract hooks (<see cref="WireValueGraph"/> / <see cref="RewireFrom"/> /
/// <see cref="WriteSourceLeaf"/> / <see cref="DescribePath"/>) are the only seams (matrix B156 is the
/// equivalence proof; BD17 the compiled contract).
/// </summary>
internal abstract class BindingExpressionCore : BindingExpressionBase, IValueEvictionListener
{
    private protected readonly AnchoredBinding _binding;
    private protected readonly UIObject _target;
    private protected readonly UIProperty _targetProperty;
    private protected UIElement? _anchorElement; // mutable: a non-UIElement target re-anchors on its owner element late (BD13)
    private protected readonly ValueFrame? _hostFrame;
    private protected readonly Action<object?>? _watchCallback;
    private protected readonly BindingMode _effectiveMode;
    private protected readonly UpdateSourceTrigger _trigger;
    private protected readonly BindingPriority _installPriority; // LocalValue, or Template in a template scope (§20/PD24)

    private protected AnchorKind _anchorKind;
    private protected object? _root;            // the resolved source root object
    private protected BindingEntryBase? _entry; // null for watch-only / OneWayToSource-passive / DirectProperty
    private IDisposable? _targetObserverToken;  // the write-back target observer / OWS anchor write retarget
    private IDisposable? _anchorObserverToken;  // the DataContext observer (default-source rebind)
    private Action<UIElement>? _editCommitHandler;
    private bool _sourceDirty; // a pending LostFocus/Explicit write
    private protected bool _isPushingToTarget;
    private protected bool _isWritingToSource;
    private bool _lostFocusSubscribed;
    private bool _editCommitSubscribed;
    private bool _inheritanceParentSubscribed; // a non-UIElement target's late-anchor watch (BD13)
    private protected bool _watchModeDegraded;
    private protected object? _lastPushedValue = NoPushSentinel;
    private protected object? _lastProducedValue;
    private protected BindingFailureKind _lastFailure;
    private int _dirtyBitmask; // cross-thread coalescing (Interlocked.Or)
    private bool _drainQueued;
    private bool _readOnlyLeafWarned;
    private protected bool _sourceDirtyDuringWrite;

    private protected static readonly object NoPushSentinel = new();

    private protected BindingExpressionCore(AnchoredBinding binding, in BindingActivationContext context)
    {
        _binding = binding;
        _target = context.Target;
        _targetProperty = context.TargetProperty;
        _anchorElement = context.Anchor;
        _hostFrame = context.HostFrame;
        _watchCallback = context.WatchCallback;
        _installPriority = context.InstallPriority; // §20/PD24: captured at install (used when the entry materializes)

        _trigger = binding.UpdateSourceTrigger == UpdateSourceTrigger.Default
                       ? UpdateSourceTrigger.PropertyChanged
                       : binding.UpdateSourceTrigger;

        _effectiveMode = ResolveEffectiveMode(binding, _targetProperty, context.IsWatchOnly);
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

    public object? CurrentValue => LastProducedForDiagnostics;

    // ───────────────────────────── lane hooks (the only differences between lanes) ─────────────────────────────

    /// <summary>The lane's forward/back converter (reflection + compiled both carry one; not on the descriptor base).</summary>
    private protected abstract IValueConverter? Converter { get; }

    /// <summary>The lane's converter parameter.</summary>
    private protected abstract object? ConverterParameter { get; }

    /// <summary>The number of path hops (reflection: parsed segments; compiled: <c>Steps</c>).</summary>
    private protected abstract int SegmentCount { get; }

    /// <summary>The hop index to re-read from after a successful source write-back (reflection: the leaf; compiled: the whole chain).</summary>
    private protected abstract int PostWriteRereadIndex { get; }

    /// <summary>(Re)wires the value graph from <paramref name="fromIndex"/> and produces the leaf value into the target.</summary>
    private protected abstract void WireValueGraph(int fromIndex);

    /// <summary>A hop's <em>value</em> changed (instance unchanged): re-read it and rewire the tail below it.</summary>
    private protected abstract void RewireFrom(int index);

    /// <summary>Performs the target → source write (TwoWay / OneWayToSource); uses <see cref="WriteConvertedLeaf"/>.</summary>
    private protected abstract void WriteSourceLeaf();

    /// <summary>The diagnostics chain text for <see cref="Explain"/>.</summary>
    private protected abstract string DescribePath();

    /// <summary>Lane-specific disposal (unsubscribe the value-graph subscriptions).</summary>
    private protected abstract void DisposeLaneSpecific();

    /// <summary>The last produced value for diagnostics; the compiled typed lane overrides to box on demand (cold).</summary>
    private protected virtual object? LastProducedForDiagnostics => _lastProducedValue;

    /// <summary>Whether a value has been pushed (for async-echo suppression, BD8 step 2); the compiled typed lane
    /// overrides so it can keep the last-pushed value typed (no per-push boxing).</summary>
    private protected virtual bool HasPushedValue => !ReferenceEquals(_lastPushedValue, NoPushSentinel);

    /// <summary>The last value pushed to the target, for the async-echo comparison; the compiled typed lane
    /// overrides to box its typed value on demand (the echo check runs on external target writes, not the hot push).</summary>
    private protected virtual object? LastPushedForEcho => _lastPushedValue;

    /// <summary>
    /// Disarms the async-echo discriminator (BD8 step 2) — called on every target → source write-back.
    /// After a write-back the comparand no longer describes anything the SOURCE produced (its newest
    /// value came from the target), and a non-notifying source has no post-write re-read to re-stamp
    /// it — a stale comparand would swallow the next target edit that returns to the source's original
    /// value (the checkbox toggle-on/toggle-off case, B92b). Re-armed by the next forward push. The
    /// compiled typed lane overrides to clear its typed comparand too.
    /// </summary>
    private protected virtual void ClearEchoComparand() => _lastPushedValue = NoPushSentinel;

    /// <summary>Pushes the unset / fallback result; the compiled typed lane overrides to push through its typed entry.</summary>
    private protected virtual void ProduceUnsetOrFallback() => PushToTarget(UIProperty.UnsetValue);

    // ───────────────────────────── activation & anchoring ─────────────────────────────

    /// <summary>Activates the expression. Called by the subclass ctor <em>after</em> its lane fields are set.</summary>
    private protected void Activate()
    {
        _anchorKind = DetermineAnchorKind();
        BindingRegistry.GetOrCreate(_target).Register(this);

        BindingLeakTracker.Track(this, _target, _binding is Binding b ? b.Path.Path : DescribePath(),
                                 BindingRegistry.DescribeTarget(_target, _targetProperty));

        // A non-UIElement target (an InputBinding/KeyBinding whose Command="{Binding}") has no DataContext
        // of its own; it anchors on the nearest UIElement up its inheritance chain — the owner element set
        // via SetInheritanceParent (BD13). That owner is assigned AFTER the binding installs (the gesture is
        // bound, then added to its element's collection), so resolve it now if already present and watch for
        // it to arrive / change. UIElement targets anchor on themselves and never take this path.
        if (_target is not UIElement)
        {
            _anchorElement = NearestInheritanceElement();
            _target.InheritanceParentChanged += OnTargetInheritanceParentChanged;
            _inheritanceParentSubscribed = true;
        }

        WireAnchorObserver();
        ResolveRootAndWire();
    }

    // The nearest UIElement up a non-UIElement target's inheritance chain — its DataContext anchor.
    private UIElement? NearestInheritanceElement()
    {
        for (var node = _target.GetInheritanceParent(); node is not null; node = node.GetInheritanceParent())
        {
            if (node is UIElement element)
                return element;
        }

        return null;
    }

    // The target's inheritance parent changed (an InputBinding added to / removed from a collection, or
    // reparented): re-resolve the anchor element and re-wire against it. Tears down the old element's
    // subscriptions first. A null resolution (detached gesture) parks the binding SourceMissing.
    private void OnTargetInheritanceParentChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        var resolved = NearestInheritanceElement();

        if (ReferenceEquals(resolved, _anchorElement))
            return;

        _anchorObserverToken?.Dispose();
        _anchorObserverToken = null;
        UnsubscribeTreeEvents();

        _anchorElement = resolved;
        WireAnchorObserver();
        ResolveRootAndWire();
    }

    private BindingMode ResolveEffectiveMode(AnchoredBinding binding, UIProperty property, bool watchOnly)
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

        if (_binding.RelativeSource is {} rs)
        {
            return rs.Mode switch
                   {
                       RelativeSourceMode.Self            => AnchorKind.Self,
                       RelativeSourceMode.TemplatedParent => AnchorKind.TemplatedParent,
                       _                                  => AnchorKind.FindAncestor
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

        if (_anchorKind == AnchorKind.FindAncestor)
        {
            _anchorElement.AttachedToTree += OnAnchorTreeChanged;
            _anchorElement.DetachedFromTree += OnAnchorTreeChanged;
        }

        _anchorElement.AttachedToLogicalTree += OnAnchorLogicalTreeChanged;
        _anchorElement.DetachedFromLogicalTree += OnAnchorLogicalTreeChanged;
    }

    private void UnsubscribeTreeEvents()
    {
        if (_anchorElement is null)
            return;
        
        _anchorElement.AttachedToTree -= OnAnchorTreeChanged;
        _anchorElement.DetachedFromTree -= OnAnchorTreeChanged;
        _anchorElement.AttachedToLogicalTree -= OnAnchorLogicalTreeChanged;
        _anchorElement.DetachedFromLogicalTree -= OnAnchorLogicalTreeChanged;
        _anchorElement.TemplatedParentChanged -= OnAnchorTemplatedParentChanged;
    }

    private void OnAnchorTemplatedParentChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            ResolveRootAndWire();
    }

    private void OnAnchorLogicalTreeChanged(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        if (IsDisposed)
            return;

        ResolveRootAndWire();
    }

    private void OnAnchorTreeChanged(object? sender, TreeAttachmentEventArgs e)
    {
        if (IsDisposed)
            return;

        ResolveRootAndWire();
    }

    private protected void ResolveRootAndWire()
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

        // Re-install the parent-DataContext observer on the (possibly new) logical parent — on FAILURE
        // too. The common park is "parent exists, its DataContext hasn't arrived yet" (the loader builds
        // children and installs their bindings BEFORE the parent's context flows; a d:DataContext or a
        // late VM assignment arrives after attach), and this observer IS the wake-up signal: tree events
        // fire at parenting time and never again. Installing it only on success parked every
        // DataContext="{Binding …}" re-scope permanently (BD2).
        if (_anchorKind == AnchorKind.ParentDataContext && _anchorElement?.LogicalParent is {} anchorParent)
        {
            _anchorObserverToken = anchorParent.AddObserver(
                DataContextSupport.DataContextProperty, new AnchorObserver(this));
        }

        if (!resolved)
        {
            DisposeLaneSpecific();
            _root = null;
            Status = failure == BindingFailureKind.NameNotFound ? BindingStatus.PathError : BindingStatus.SourceMissing;

            // SourceMissing parks silently — it recovers on attach (a UIElement) or when the inheritance
            // parent arrives (a non-UIElement gesture target re-anchors on its owner element, BD13). Other
            // failures, and any unresolved watch-only binding, trace a warning.
            if (failure != BindingFailureKind.SourceMissing || _watchCallback is not null)
                MaybeTrace(failure, BindingTraceLevel.Warning, FailureMessage(failure));

            ProduceUnsetOrFallback();
            return;
        }

        _root = newRoot;
        Status = BindingStatus.Active;
        WireValueGraph(0);

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
                if (_anchorElement?.LogicalParent is not {} logicalParent)
                {
                    root = null;
                    failure = BindingFailureKind.SourceMissing;
                    return false;
                }

                root = logicalParent.GetValue(DataContextSupport.DataContextProperty);
                if (root is null) failure = BindingFailureKind.SourceMissing;
                return root is not null;

            case AnchorKind.Self:
                root = _anchorElement;
                return root is not null;

            case AnchorKind.TemplatedParent:
                root = _anchorElement?.TemplatedParent;
                if (root is null) failure = BindingFailureKind.SourceMissing;
                return root is not null;

            case AnchorKind.ElementName:
                // Name scopes live on the LOGICAL tree (FindEnclosing walks LogicalParent), so resolve
                // once the anchor is logically attached — not gated on visual attachment, which lands a
                // step later (AdoptChild raises AttachedToLogicalTree before AddVisualChild sets the
                // visual root). A forward reference during build (no logical parent yet) parks
                // SourceMissing without a trace; a name unresolved after attach traces NameNotFound (B123).
                if (_anchorElement?.LogicalParent is null)
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

        // No parent yet ⇒ park until attach (the walk re-runs on AttachedToLogicalTree).
        if ((_anchorElement.VisualParent ?? _anchorElement.UIParent) is null)
        {
            failure = BindingFailureKind.SourceMissing;
            return null;
        }

        var matches = 0;

        for (var node = _anchorElement.VisualParent ?? _anchorElement.UIParent;
             node is not null;
             node = node.VisualParent ?? node.UIParent)
        {
            if (rs.AncestorType!.IsInstanceOfType(node) && ++matches == rs.AncestorLevel)
                return node;
        }

        failure = BindingFailureKind.AncestorNotFound;
        return null;
    }

    // ───────────────────────────── source-change dispatch (cross-thread + write coalesce) ─────────────────────────────

    /// <summary>
    /// The shared source-change dispatch: cross-thread changes coalesce into one drain (BD20); a change
    /// raised during our own write coalesces into one post-write re-read (BD12); otherwise rewire from
    /// the changed hop. Lane subscription handlers call this <em>after</em> their member-name filter.
    /// </summary>
    private protected void DispatchSourceChange(int index)
    {
        if (IsDisposed)
            return;

        if (BindingDispatcher.Current is {} dispatcher && !dispatcher.CheckAccess())
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

        RewireFrom(index);
    }

    private void QueueCrossThread(int index)
    {
        Interlocked.Or(ref _dirtyBitmask, 1 << Math.Min(index, 30));

        if (BindingDispatcher.Current is not {} dispatcher)
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
        RewireFrom(lowest);
    }

    // ───────────────────────────── forward pipeline (boxed) ─────────────────────────────

    private protected void ProduceFromLeaf(object? rawLeaf) => PushToTarget(rawLeaf);

    private protected void PushToTarget(object? rawValue)
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

        if (Converter is {} converter)
        {
            try
            {
                value = converter.Convert(value, targetType, ConverterParameter, culture);
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

        if (_binding.StringFormat is {} format)
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
                     ? _target.BindUntyped(_targetProperty, _installPriority, this) // LocalValue, or Template in a template scope (§20)
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

    private protected void WireTargetObserver()
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
        if (!_editCommitSubscribed && UIApplication.Current?.InputDispatcher is {} dispatcher)
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

            if (HasPushedValue && _targetProperty.AreValuesEqualUntyped(_target.GetType(), current, LastPushedForEcho))
                return;
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

    private protected bool ValuesEqual(object? a, object? b)
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

        WireValueGraph(0);
    }

    // ReSharper disable once UnusedParameter.Local
    private void WriteToSource(bool force)
    {
        if (IsDisposed)
            return;

        if (_effectiveMode is not (BindingMode.TwoWay or BindingMode.OneWayToSource))
            return;

        WriteSourceLeaf();
    }

    /// <summary>
    /// The shared write-back tail (BD10/BD12): the read-only-leaf degrade, the ConvertBack ladder, the
    /// <c>_isWritingToSource</c> guard, and the post-write coalesced re-read. The lane supplies the
    /// leaf's writability + type and the actual set via <paramref name="applySet"/>.
    /// </summary>
    private protected void WriteConvertedLeaf(bool canWrite, Type leafType, Action<object?> applySet)
    {
        if (!canWrite)
        {
            DegradeToOneWayIfNeeded();
            return;
        }

        var targetValue = _target.GetValue(_targetProperty);

        if (!TryConvertBack(targetValue, leafType, out var sourceValue))
            return;

        _isWritingToSource = true;
        ClearEchoComparand(); // BD8 step 2: the comparand only ever describes source-produced values
        _sourceDirtyDuringWrite = false;

        try
        {
            applySet(sourceValue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            MaybeTrace(BindingFailureKind.SourceUpdateFailed, BindingTraceLevel.Warning,
                       $"the source write failed: {ex.Message}.");

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
            WireValueGraph(PostWriteRereadIndex);
        }
    }

    private bool TryConvertBack(object? targetValue, Type leafType, out object? sourceValue)
    {
        sourceValue = null;
        var culture = _binding.EffectiveCulture;

        // Converter present.
        if (Converter is {} converter)
        {
            object? result;

            try
            {
                result = converter.ConvertBack(targetValue, leafType, ConverterParameter, culture);
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
        if (_binding.StringFormat is {} format)
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

    private protected void DegradeToOneWayIfNeeded()
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
        DisposeLaneSpecific();
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

        if (_editCommitSubscribed && _editCommitHandler is not null && UIApplication.Current?.InputDispatcher is {} dispatcher)
        {
            dispatcher.EditCommitRequested -= _editCommitHandler;
            _editCommitSubscribed = false;
            _editCommitHandler = null;
        }

        if (_inheritanceParentSubscribed)
        {
            _target.InheritanceParentChanged -= OnTargetInheritanceParentChanged;
            _inheritanceParentSubscribed = false;
        }

        if (_entry is not null && !fromEviction)
            _entry.Dispose();

        _entry = null;

        BindingRegistry.Get(_target)?.Unregister(this);
        BindingLeakTracker.Forget(this); // the contract met — the tracker forgets a cleanly disposed expression
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    private protected void MaybeTrace(BindingFailureKind kind, BindingTraceLevel level, string message)
    {
        _lastFailure = kind != BindingFailureKind.None ? kind : _lastFailure;

        if (!BindingDiagnostics.ShouldConstruct(level, _binding.Trace))
            return;

        var description = BindingRegistry.DescribeTarget(_target, _targetProperty);

        BindingDiagnostics.Record(new BindingTraceEvent(
                                      level, kind, DescribePath(), description, message, Environment.TickCount64));
    }

    private string FailureMessage(BindingFailureKind kind) => kind switch
                                                              {
                                                                  BindingFailureKind.SourceMissing =>
                                                                      "the binding source (anchor) is unresolved; parked until attach.",
                                                                  BindingFailureKind.NameNotFound =>
                                                                      $"the element name '{_binding.ElementName}' was not found in scope.",
                                                                  BindingFailureKind.AncestorNotFound =>
                                                                      $"no ancestor of type '{_binding.RelativeSource?.AncestorType?.Name}' was found.",
                                                                  _ => "the binding source could not be resolved."
                                                              };

    internal override BindingExpressionExplanation Explain()
    {
        var chain = _anchorKind == AnchorKind.Source
                        ? $"Source({_binding.Source?.GetType().Name})"
                        : _anchorKind.ToString();

        var path = DescribePath();

        if (!string.IsNullOrEmpty(path))
            chain = $"{chain} → {path}";

        return new BindingExpressionExplanation(
            Lane, path, Status, EffectiveMode, chain, LastProducedForDiagnostics, _lastFailure);
    }

    // ───────────────────────────── shared observer types ─────────────────────────────

    private sealed class AnchorObserver(BindingExpressionCore owner) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => owner.ResolveRootAndWire();
    }

    private sealed class TargetObserver(BindingExpressionCore owner) : IUntypedValueObserver
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
            => owner.OnTargetValueChanged(priority);
    }
}