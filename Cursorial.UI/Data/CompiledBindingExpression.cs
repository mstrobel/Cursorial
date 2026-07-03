using System.Collections.Specialized;
using System.ComponentModel;

namespace Cursorial.UI.Data;

/// <summary>
/// The compiled-lane binding expression (design doc §6.7, BD17) — value access is a single typed
/// whole-chain <see cref="CompiledBinding{TSource,TValue}.Getter"/> call (the <c>Steps</c> drive only
/// INPC/INCC subscription rewiring), and the push is <b>typed and zero-box</b> when the target is a
/// <see cref="StyledProperty{TValue}"/> with no converter/StringFormat/null-or-fallback coercion
/// (B147; the binding analog of <c>AnimatedValueHandle&lt;T&gt;</c>). Everything else — anchoring, the
/// source-change dispatch, write-back, echo suppression, cross-thread coalescing, eviction — is shared
/// with the reflection lane via <see cref="BindingExpressionCore"/> (B156 is the equivalence proof).
/// </summary>
internal sealed class CompiledBindingExpression<TSource, TValue> : BindingExpressionCore
{
    private readonly CompiledBinding<TSource, TValue> _compiled;
    private readonly StyledProperty<TValue>? _typedProperty;
    private readonly bool _typedPath; // committed at construction: typed zero-box vs the boxed pipeline (no mid-life swap)

    private StepSub[] _subs; // one per Step — the live INPC/INCC subscriptions, parallel to _compiled.Steps
    private BindingEntry<TValue>? _typedEntry; // the typed zero-box entry (also stored in the core's _entry)
    private TValue _lastTyped = default!;
    private bool _hasLastTyped;

    public CompiledBindingExpression(CompiledBinding<TSource, TValue> binding, in BindingActivationContext context)
        : base(binding, in context)
    {
        _compiled = binding;
        _subs = binding.Steps.Length == 0 ? [] : new StepSub[binding.Steps.Length];

        // The typed zero-box predicate is decided ONCE from construction-immutable facts (BD17): the
        // target must be StyledProperty<TValue> and there must be no value coercion in the pipeline.
        _typedProperty = _targetProperty as StyledProperty<TValue>;
        _typedPath = _typedProperty is not null
            && context.WatchCallback is null
            && !_targetProperty.IsDirect
            && _effectiveMode != BindingMode.OneWayToSource
            && binding.Converter is null
            && binding.StringFormat is null
            && !binding.HasTargetNullValue
            && !binding.HasFallbackValue;

        Activate(); // after the lane fields are set
    }

    // ───────────────────────────── lane hooks ─────────────────────────────

    private protected override IValueConverter? Converter => _compiled.Converter;

    private protected override object? ConverterParameter => _compiled.ConverterParameter;

    private protected override int SegmentCount => _compiled.Steps.Length;

    private protected override int PostWriteRereadIndex => 0; // the whole chain re-reads via Getter

    private protected override string DescribePath() => _compiled.PathText;

    private protected override void DisposeLaneSpecific() => UnsubscribeFrom(0);

    // CurrentValue / Explain read the typed value lazily (cold path) without boxing on the hot push.
    private protected override object? LastProducedForDiagnostics => _hasLastTyped ? _lastTyped : _lastProducedValue;

    // Async-echo suppression keeps the last-pushed value TYPED on the typed path — no per-push boxing
    // (the box happens only here, on an external target write, not on the hot source→target push).
    private protected override bool HasPushedValue => _typedPath ? _hasLastTyped : base.HasPushedValue;

    private protected override object? LastPushedForEcho => _typedPath ? _lastTyped : base.LastPushedForEcho;

    // ───────────────────────────── value graph (whole-chain Getter + Steps subscription) ─────────────────────────────

    /// <summary>
    /// (Re)wires the compiled chain. The compiled lane always re-wires from the root: the value is a
    /// single whole-chain <c>Getter</c> call regardless, and re-subscribing the unchanged upper hops is
    /// cheap relative to correctness — so <paramref name="fromIndex"/> is honored as "wire the whole
    /// chain" (every caller passes 0 / <see cref="PostWriteRereadIndex"/> = 0).
    /// </summary>
    private protected override void WireValueGraph(int fromIndex)
    {
        if (IsDisposed)
            return;

        // Typed root check (B153): the anchor resolved (root != null) but is not assignable to TSource.
        if (_root is not TSource source)
        {
            UnsubscribeFrom(0);
            Status = BindingStatus.PathError;
            MaybeTrace(BindingFailureKind.SourceTypeMismatch, BindingTraceLevel.Warning,
                $"the compiled binding source '{_root?.GetType().Name}' is not assignable to '{typeof(TSource).Name}'.");
            ProduceUnsetOrFallback();
            return;
        }

        // A read-only leaf (null Setter) used TwoWay degrades to OneWay at wire (BD10/B152); the Setter is
        // construction-immutable, so unlike the reflection lane this never re-enables. Idempotent (one warning).
        if (_effectiveMode == BindingMode.TwoWay && _compiled.Setter is null)
            DegradeToOneWayIfNeeded();

        // Re-subscribe the chain for INPC/INCC rewiring (the Steps materialize intermediates; the typed
        // Getter does the value read). A null intermediate stops subscription — the Getter handles it.
        UnsubscribeFrom(0);
        object? instance = source;
        var steps = _compiled.Steps.Span;
        for (var i = 0; i < steps.Length && instance is not null; i++)
        {
            SubscribeStep(i, instance, in steps[i]);
            instance = steps[i].GetStep(instance);
        }

        PushValue(_compiled.Getter(source)); // BD17: one whole-chain typed read (struct intermediates copy)
    }

    /// <summary>
    /// A hop's <em>value</em> changed (its source instance is unchanged): re-subscribe only the tail below
    /// it and re-read the value via the whole-chain <c>Getter</c>. For a single-hop binding this re-subscribes
    /// nothing — so the steady-state source→target push allocates nothing (B147).
    /// </summary>
    private protected override void RewireFrom(int index)
    {
        if (IsDisposed)
            return;
        if (_root is not TSource source)
        {
            WireValueGraph(0);
            return;
        }

        var steps = _compiled.Steps.Span;
        if (index >= steps.Length || _subs[index].Instance is null)
        {
            WireValueGraph(0); // defensive: the changed hop is no longer wired — re-wire the whole chain
            return;
        }

        // Hop `index`'s source is unchanged; the value it produces changed → re-materialize the tail only.
        // (No tail ⇒ skip GetStep entirely; for a single-hop binding the steady-state push allocates nothing.)
        UnsubscribeFrom(index + 1);
        if (index + 1 < steps.Length)
        {
            object? instance = steps[index].GetStep(_subs[index].Instance);
            for (var i = index + 1; i < steps.Length && instance is not null; i++)
            {
                SubscribeStep(i, instance, in steps[i]);
                instance = steps[i].GetStep(instance);
            }
        }

        PushValue(_compiled.Getter(source));
    }

    private void PushValue(TValue value)
    {
        if (IsDisposed)
            return;

        if (_typedPath)
        {
            _typedEntry ??= MaterializeTypedEntry();
            _entry = _typedEntry; // the core owns eviction/disposal via _entry
            _isPushingToTarget = true;
            try
            {
                _typedEntry.SetValue(value); // zero-box typed push (B147)
                _lastTyped = value;          // also the async-echo comparand (kept typed — no boxing)
                _hasLastTyped = true;
            }
            finally
            {
                _isPushingToTarget = false;
            }

            return;
        }

        // Forfeit (B182): a converter/StringFormat/null-or-fallback coercion, a non-StyledProperty<TValue>
        // target, or watch-only — box the value and run the shared boxed pipeline.
        ProduceFromLeaf(value);
    }

    private BindingEntry<TValue> MaterializeTypedEntry()
        => _hostFrame is null
            ? _target.Bind<TValue>(_typedProperty!, _installPriority, this)        // LocalValue / Template (§20)
            : _target.BindInFrame<TValue>(_typedProperty!, _hostFrame, this);       // frame-hosted (B186)

    private protected override void ProduceUnsetOrFallback()
    {
        // On the typed path (no fallback by predicate) an unset is a typed SetUnset (zero-box). With no
        // entry yet materialized, "no entry" already is the unset state — lower sources promote.
        if (_typedPath)
        {
            if (_typedEntry is not null)
            {
                _isPushingToTarget = true;
                try { _typedEntry.SetUnset(); }
                finally { _isPushingToTarget = false; }
                _lastPushedValue = NoPushSentinel;
            }

            _hasLastTyped = false;
            _lastProducedValue = UIProperty.UnsetValue;
            return;
        }

        base.ProduceUnsetOrFallback(); // boxed: honors FallbackValue
    }

    // ───────────────────────────── Steps subscription ─────────────────────────────

    private void SubscribeStep(int index, object instance, in CompiledPathStep step)
    {
        // OneTime / OneWayToSource skip path subscriptions entirely (BD11).
        if (_effectiveMode is BindingMode.OneTime or BindingMode.OneWayToSource)
            return;

        ref var sub = ref _subs[index];
        sub.Instance = instance;

        // Constant-index hop ("Item[]") → INotifyCollectionChanged (+ the "Item[]" INPC convention below).
        if (step.MemberName == "Item[]" && instance is INotifyCollectionChanged incc)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => DispatchSourceChange(index);
            incc.CollectionChanged += handler;
            sub.Incc = handler;
        }

        // A struct (or any non-INPC) intermediate simply isn't subscribed (B155) — the whole-chain Getter
        // still reads it correctly; it only won't push on a mutation of that hop.
        if (instance is INotifyPropertyChanged inpc)
        {
            PropertyChangedEventHandler handler = (_, e) => OnStepInpc(index, e);
            inpc.PropertyChanged += handler;
            sub.Inpc = handler;
        }
    }

    private void OnStepInpc(int index, PropertyChangedEventArgs e)
    {
        if (IsDisposed)
            return;

        // Filter on the hop's member name (stable for a wired step); "" / "Item[]" always match.
        var member = _compiled.Steps.Span[index].MemberName;
        var matches = string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, member, StringComparison.Ordinal)
            || (member == "Item[]" && string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal));
        if (!matches)
            return;

        DispatchSourceChange(index);
    }

    private void UnsubscribeFrom(int index)
    {
        for (var i = _subs.Length - 1; i >= index; i--)
        {
            ref var sub = ref _subs[i];
            if (sub.Instance is INotifyPropertyChanged inpc && sub.Inpc is { } h)
                inpc.PropertyChanged -= h;
            if (sub.Instance is INotifyCollectionChanged incc && sub.Incc is { } ih)
                incc.CollectionChanged -= ih;
            sub = default;
        }
    }

    // ───────────────────────────── target → source write-back ─────────────────────────────

    private protected override void WriteSourceLeaf()
    {
        if (_root is not TSource source)
            return;

        // The descriptor's Setter IS the resolved reverse chain (re-rooted on the source); a null Setter
        // is a read-only leaf (B152) → the shared degrade. ConvertBack + the write guard are shared.
        var setter = _compiled.Setter;
        WriteConvertedLeaf(setter is not null, typeof(TValue), value => setter!(source, (TValue)value!));
    }

    // ───────────────────────────── subscription record ─────────────────────────────

    private struct StepSub
    {
        public object? Instance;
        public PropertyChangedEventHandler? Inpc;
        public NotifyCollectionChangedEventHandler? Incc;
    }
}
