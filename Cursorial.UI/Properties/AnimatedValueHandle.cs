namespace Cursorial.UI;

/// <summary>
/// The sole producer for the <see cref="BindingPriority.Animation"/> lane (matrix PD1), held by the
/// storyboard layer. One handle is active per <c>(object, property)</c>: <c>BeginAnimation</c>
/// while another handle is attached detaches the prior one (last-started wins; richer
/// composition/handoff is the storyboard's job, using <c>GetBaseValue</c> for snapshots). A fresh
/// handle is <em>inert</em> until its first <see cref="SetValue"/> (PD4 — the orchestrator owns the
/// clock; until a tick produces a value there is nothing to apply).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-frame writes are allocation-free</b>: <see cref="SetValue"/> mutates the store entry in
/// place with an equality short-circuit, so an animation that quantizes to the same cell value for
/// several frames produces zero downstream work (design doc §2.3).
/// </para>
/// <para>
/// <b>Restoration is store-owned</b> (invariant 4): <see cref="Dispose"/> removes the animated
/// value and the base — which kept living underneath, absorbing masked writes — resurfaces with one
/// notification at its own lane. Dispose is idempotent; a detached handle's <see cref="SetValue"/>
/// is a silent no-op and its <see cref="Dispose"/> does nothing (ledger A19).
/// </para>
/// </remarks>
public sealed class AnimatedValueHandle<T> : IDisposable
{
    private readonly UIObject _target;
    private readonly StyledProperty<T> _property;

    internal AnimatedValueHandle(UIObject target, StyledProperty<T> property)
    {
        _target = target;
        _property = property;
    }

    /// <summary>
    /// Whether the handle has been detached — by <see cref="Dispose"/>, by a later
    /// <c>BeginAnimation</c> on the same property (last-started wins), or by the store's teardown
    /// sweep. Detached handles ignore all further calls (ledger A19).
    /// </summary>
    public bool IsDetached { get; private set; }

    /// <summary>
    /// Pushes this frame's animated value and returns whether the <em>effective</em> value actually
    /// changed (ledger A18 — feeds the frame loop's dirty signal). The value is coerced at
    /// effective computation and the equality gate runs on the coerced result; values rejected by
    /// <c>Validate</c> are discarded with a <see cref="UIDiagnostics"/> notification (producer
    /// mouth). The first push flips the property's source to
    /// <see cref="BindingPriority.Animation"/> even when the value equals the base (a silent lane
    /// flip, matrix PD9).
    /// </summary>
    public bool SetValue(T value)
    {
        if (IsDetached)
            return false; // A19: silent no-op after detach

        _target.VerifyAccess();

        var metadata = _property.GetMetadata(_target.GetType());
        if (metadata.Validate is { } validate && !validate(value))
        {
            UIDiagnostics.OnRejectedValue(_target, _property, value); // discard, keep previous value
            return false;
        }

        return _target.SetAnimatedValue(_property, metadata, value);
    }

    /// <summary>
    /// Ends the animation: the base value resurfaces with one notification at its own lane (silent
    /// when equal — the gate applies). Idempotent; a no-op on detached handles (ledger A19).
    /// </summary>
    public void Dispose()
    {
        if (IsDetached)
            return;

        IsDetached = true;
        _target.VerifyAccess();
        _target.EndAnimation(_property, this);
    }

    /// <summary>Marks the handle detached without store work (displacement by a later handle; teardown).</summary>
    internal void MarkDetached() => IsDetached = true;
}
