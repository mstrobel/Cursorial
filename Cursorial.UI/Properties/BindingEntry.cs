// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// Non-generic base for binding producer entries (ledger A7) — the store-side contract the binding
/// engine pushes through. An entry is installed <em>valueless</em> (ledger A8) and contributes
/// nothing until its first <c>SetValue</c>; pushing <see cref="UIProperty.UnsetValue"/> (or calling
/// <see cref="SetUnset"/>) returns it to the valueless state and the store promotes the next
/// source. Free-standing entries (<c>UIObject.Bind</c>) contribute at
/// <see cref="BindingPriority.LocalValue"/> only (A6); frame-hosted entries
/// (<c>UIObject.BindInFrame</c>) contribute at their host frame's <see cref="StyleSortKey"/> inside
/// the single Style slot (A5).
/// </summary>
/// <remarks>
/// <b>Lifecycle.</b> <see cref="Dispose"/> is idempotent and legal re-entrantly from within
/// <see cref="IValueEvictionListener.OnEvicted"/> (A7). Self-dispose withdraws the entry's
/// contribution but never fires <c>OnEvicted</c> — eviction is store-initiated only (matrix PD12):
/// <c>ClearValue</c> (A9), frame removal, displacement by a newer local entry, and the
/// <c>TearDown</c> sweep (A13). A dead entry discards all further pushes silently.
/// </remarks>
public abstract class BindingEntryBase : IDisposable, IValueEntry
{
    private protected readonly UIObject Target;
    private protected readonly ValueFrame? HostFrame;
    private readonly IValueEvictionListener? _listener;
    private bool _detached;

    private protected BindingEntryBase(
        UIObject target, UIProperty property, BindingPriority priority,
        ValueFrame? hostFrame, IValueEvictionListener? listener)
    {
        Target = target;
        Property = property;
        Priority = priority;
        HostFrame = hostFrame;
        _listener = listener;
        SourceKind = priority switch
        {
            BindingPriority.Template => ValueSourceKind.TemplateBinding,
            BindingPriority.Style => ValueSourceKind.StyleSetter,
            _ => ValueSourceKind.Local,
        };
    }

    /// <summary>
    /// The within-lane provenance this entry contributes (PD25) — defaulted from <see cref="Priority"/>
    /// (a Template entry is a <see cref="ValueSourceKind.TemplateBinding"/>, a local one
    /// <see cref="ValueSourceKind.Local"/>); a resource producer overrides it to
    /// <see cref="ValueSourceKind.TemplateResource"/>. Surfaced through <c>GetValueSource(...).Kind</c>.
    /// </summary>
    internal ValueSourceKind SourceKind { get; set; }

    /// <summary>The property this entry produces values for.</summary>
    public UIProperty Property { get; }

    /// <summary>
    /// The priority lane the entry contributes at: <see cref="BindingPriority.LocalValue"/> or
    /// <see cref="BindingPriority.Template"/> for free-standing entries (A6 — Template when installed
    /// inside the template-instantiation scope, §20/PD24), <see cref="BindingPriority.Style"/> for
    /// frame-hosted ones (A5).
    /// </summary>
    public BindingPriority Priority { get; }

    /// <summary>Whether the entry currently carries a value (ledger A8 — installs <see langword="false"/>).</summary>
    public bool HasValue { get; private protected set; }

    /// <summary>Whether the entry is dead (evicted or disposed); pushes are then silent no-ops.</summary>
    internal bool IsDetached => _detached;

    /// <summary>
    /// The untyped push mouth (the XAML/binding lane): type-checked against
    /// <see cref="UIProperty.PropertyType"/> with no silent conversion;
    /// <see cref="UIProperty.UnsetValue"/> is the untyped spelling of <see cref="SetUnset"/>
    /// (ledger A8 — never a null/default clobber).
    /// </summary>
    public void SetValue(object? value)
    {
        if (ReferenceEquals(value, UIProperty.UnsetValue))
        {
            SetUnset();
            return;
        }

        UIObject.ValidateValueType(Property, value);
        SetValueUntypedCore(value);
    }

    /// <summary>
    /// Withdraws the entry's contribution (<see cref="HasValue"/> becomes <see langword="false"/>)
    /// and the store promotes the next source. The entry stays installed — a later push re-applies.
    /// </summary>
    public abstract void SetUnset();

    /// <summary>
    /// Detaches the entry and withdraws its contribution (the next source promotes with one
    /// notification). Idempotent; legal from within <see cref="IValueEvictionListener.OnEvicted"/>
    /// (ledger A7); never fires <c>OnEvicted</c> itself (matrix PD12).
    /// </summary>
    public void Dispose()
    {
        if (_detached)
            return;

        _detached = true;
        DisposeCore();
    }

    /// <summary>Routes a type-checked boxed value to the typed push.</summary>
    private protected abstract void SetValueUntypedCore(object? value);

    /// <summary>Withdraws the contribution and detaches store/frame bookkeeping (self-dispose path).</summary>
    private protected abstract void DisposeCore();

    /// <summary>
    /// Store-initiated eviction: marks the entry dead and fires the eviction listener exactly once.
    /// The <em>caller</em> owns withdrawing the value and notifying (PD2: evict → recompute →
    /// notify); a re-entrant <see cref="Dispose"/> from the listener is a no-op.
    /// </summary>
    internal void Evict()
    {
        if (_detached)
            return;

        _detached = true;
        _listener?.OnEvicted(this);
    }
}

/// <summary>
/// The typed binding producer entry (design doc §2.4). Holds the <em>raw</em> pushed value —
/// coercion runs at effective-value computation; validation runs at this mouth and rejected values
/// are discarded with <see cref="UIDiagnostics"/> notification, keeping the previous state (the
/// producer cannot meaningfully catch).
/// </summary>
public sealed class BindingEntry<T> : BindingEntryBase, IValueEntry<T>
{
    private T _value = default!;

    internal BindingEntry(
        UIObject target, StyledProperty<T> property, BindingPriority priority,
        ValueFrame? hostFrame, IValueEvictionListener? listener)
        : base(target, property, priority, hostFrame, listener)
    {
    }

    /// <summary>The typed property identity.</summary>
    internal new StyledProperty<T> Property => (StyledProperty<T>)base.Property;

    /// <summary>The held raw value (the <see cref="IValueEntry{T}"/> read surface). Valid only while <see cref="BindingEntryBase.HasValue"/>.</summary>
    public T GetValue()
    {
        if (!HasValue)
            throw new InvalidOperationException($"Binding entry for '{base.Property}' holds no value (ledger A8 — valueless until the first SetValue).");
        return _value;
    }

    /// <summary>
    /// Pushes a value. Within one priority, last writer wins and a binding's push counts as a write
    /// (design doc §2.2); invalid values are discarded with a diagnostic (the entry keeps its prior
    /// state, matrix M149); equal values short-circuit (A8). Dead entries discard silently.
    /// </summary>
    public void SetValue(T value)
    {
        if (IsDetached)
            return; // post-eviction pushes are discarded (matrix M144/M165)

        Target.VerifyAccess();

        var metadata = Property.GetMetadata(Target.GetType());
        if (metadata.Validate is { } validate && !validate(value))
        {
            UIDiagnostics.OnRejectedValue(Target, Property, value); // producer mouth: discard, keep prior state
            return;
        }

        if (HostFrame is null)
        {
            _value = value;
            HasValue = true;
            if (Priority == BindingPriority.Template)
                Target.SetTemplateValueFromEntry(Property, metadata, value, this); // the Template-lane entry (§20)
            else
                Target.SetLocalValueFromEntry(Property, metadata, value, this);
        }
        else
        {
            if (HasValue && metadata.EffectiveComparer.Equals(_value, value))
                return; // producer-mouth equality short-circuit (A8)

            _value = value;
            HasValue = true;
            Target.ReevaluateFromEntry(Property, this);
        }
    }

    /// <inheritdoc/>
    public override void SetUnset()
    {
        if (IsDetached)
            return;

        Target.VerifyAccess();

        var hadValue = HasValue;
        HasValue = false;
        _value = default!;

        if (HostFrame is null)
        {
            if (Priority == BindingPriority.Template)
                Target.UnsetTemplateValueFromEntry(Property); // clears the shared template slot (§20)
            else
                Target.UnsetLocalValueFromEntry(Property); // clears the shared local slot (an unset is a push)
        }
        else if (hadValue)
        {
            Target.ReevaluateFromEntry(Property, this);
        }
    }

    private protected override void SetValueUntypedCore(object? value) => SetValue((T)value!);

    private protected override void DisposeCore()
    {
        HasValue = false;
        _value = default!;

        if (HostFrame is null)
        {
            if (Priority == BindingPriority.Template)
                Target.DetachTemplateEntry(Property, this); // the Template-lane twin (§20)
            else
                Target.DetachLocalEntry(Property, this);
        }
        else
        {
            HostFrame.RemoveHostedEntry(this);
            if (HostFrame.Store is not null)
                Target.ReevaluateFromEntry(Property, changedEntry: null);
        }
    }
}
