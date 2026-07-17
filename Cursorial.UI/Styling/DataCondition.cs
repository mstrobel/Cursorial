using System.Diagnostics.CodeAnalysis;

using Cursorial.UI.Data;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One data condition of a <see cref="Style.When"/> conjunction (design doc §3.1 / §3.3; the
/// DataTrigger equivalent — the single <em>non-structural</em> activation mechanism). A condition
/// carries a <see cref="BindingBase"/> (the data half, owned by S2) plus either an
/// equality-test value or a <c>Func&lt;object?, bool&gt;</c> predicate (the verdict half,
/// owned by the styling engine). The styling engine arms one watch
/// (<see cref="BindingOperations.Watch(UIElement, BindingBase, System.Action{object?})"/>) per
/// condition when the carrying rule structurally matches; a delivered value is evaluated through
/// <c>IsMetBy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Namespace note:</b> <see cref="DataCondition"/> lives in <c>Cursorial.UI</c> (beside
/// <c>Style</c>), but its constructors take a <see cref="BindingBase"/> from
/// <c>Cursorial.UI.Data</c>. Authoring a <c>When</c> condition therefore needs both
/// <c>using Cursorial.UI;</c> and <c>using Cursorial.UI.Data;</c> in scope. Only reflection-lane
/// <see cref="Binding"/> descriptors are accepted in v1 (validated at construction); compiled-lane
/// conditions are deferred.
/// </para>
/// <para>
/// <b>Pinned semantics</b> (doc §3.1 / §3.3 / ledger B16): an unknown or
/// <see cref="UIProperty.UnsetValue"/> binding value ⇒ <b>unmet</b> (a path that does not resolve
/// never satisfies a condition); the watcher's lifetime equals the armed rule's lifetime (live across
/// deactivation, disposed at disarm/element detach — it is the re-activation predicate, so there is
/// no pause); a DataContext change on the element automatically rebinds and re-delivers (the watch's
/// own behavior). Each <see cref="DataCondition"/> counts <b>1 classLike</b> toward specificity
/// (SD5 — data conditions ARE specificity; a <c>When</c>-guarded style beats its unguarded base with
/// no extra mechanism, doc §3.4).
/// </para>
/// <para>
/// There is no <c>Or</c> in a <see cref="Style.When"/> collection — disjunction is two styles via
/// <c>BasedOn</c> or a selector list (doc §3.1). <see cref="Negate"/> inverts a single condition's
/// verdict (so "value is NOT X" needs no separate predicate); it is applied <em>after</em> the
/// unmet-on-unset rule, so a negated condition over an unresolved path is still unmet (an unresolved
/// path produces no verdict to invert — fail-closed).
/// </para>
/// </remarks>
public sealed class DataCondition
{
    private readonly Func<object?, bool>? _predicate;

    /// <summary>
    /// Xaml-friendly constructor for creating an equality condition.
    /// </summary>
    [SetsRequiredMembers]
    public DataCondition() {}

    /// <summary>
    /// Creates an equality condition: met when the binding's delivered value equals
    /// <paramref name="value"/> (per <see cref="object.Equals(object?, object?)"/>; an
    /// <see cref="UIProperty.UnsetValue"/> delivery is never met).
    /// </summary>
    /// <param name="binding">The data binding (the source half; owned by S2).</param>
    /// <param name="value">The value the delivered binding value must equal.</param>
    /// <param name="negate">Inverts the verdict (the constructor-discoverable form of
    /// <see cref="Negate"/>): met when the delivered value does <em>not</em> equal
    /// <paramref name="value"/>. Applied after the unmet-on-unset rule (see remarks).</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is null.</exception>
    [SetsRequiredMembers]
    public DataCondition(BindingBase binding, object? value, bool negate = false)
    {
        RequireReflectionLane(binding);
        Binding = binding;
        Value = value;
        Negate = negate;
    }

    /// <summary>
    /// Creates a predicate condition: met when <paramref name="predicate"/> returns
    /// <see langword="true"/> for the binding's delivered value. The predicate is <em>not</em> invoked
    /// for an <see cref="UIProperty.UnsetValue"/> delivery (unresolved ⇒ unmet, never a predicate call).
    /// </summary>
    /// <param name="binding">The data binding.</param>
    /// <param name="predicate">The verdict predicate over the delivered value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> or <paramref name="predicate"/> is null.</exception>
    [SetsRequiredMembers]
    public DataCondition(BindingBase binding, Func<object?, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        RequireReflectionLane(binding);
        Binding = binding;
        _predicate = predicate;
    }

    // A DataCondition arms one BindingOperations.Watch per condition, and Watch is reflection-lane
    // only in v1 (compiled-lane watches are deferred). Validate at construction so an unsupported
    // descriptor fails at the authoring site, not deep inside style arming. The Binding property
    // stays BindingBase so the deferred compiled-lane support can widen this without a break.
    private static void RequireReflectionLane(BindingBase binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding is not Cursorial.UI.Data.Binding)
        {
            throw new ArgumentException(
                $"DataCondition currently requires a reflection-lane Binding; got {binding.GetType().Name}.",
                nameof(binding));
        }
    }

    /// <summary>The data binding whose delivered value drives the condition (the S2 source half).</summary>
    public required BindingBase Binding { get; init; } = null!;

    /// <summary>Whether the verdict is inverted (applied after the unmet-on-unset rule — see remarks).</summary>
    public bool Negate { get; init; }

    /// <summary>The value the delivered binding value must equal.</summary>
    public object? Value { get; init; }

    /// <summary>
    /// Evaluates the condition against a value delivered by the watch (design doc §3.3): an
    /// <see cref="UIProperty.UnsetValue"/> delivery is <b>unmet</b> regardless of <see cref="Negate"/>
    /// (fail-closed — an unresolved path produces no verdict to invert); otherwise the equality or
    /// predicate verdict, inverted by <see cref="Negate"/>.
    /// </summary>
    /// <param name="deliveredValue">The value the watch delivered (its <c>Value</c>).</param>
    /// <returns>Whether the condition currently holds.</returns>
    internal bool IsMetBy(object? deliveredValue)
    {
        // Unknown / UnsetValue ⇒ unmet (doc §3.3; ledger B16). The unset verdict is NOT invertible:
        // an unresolved binding has no value to compare or invert, so the condition fails closed even
        // when Negate is set.
        if (ReferenceEquals(deliveredValue, UIProperty.UnsetValue))
            return false;

        var raw = _predicate?.Invoke(deliveredValue) ?? Equals(deliveredValue, Value);
        return Negate ? !raw : raw;
    }
}
