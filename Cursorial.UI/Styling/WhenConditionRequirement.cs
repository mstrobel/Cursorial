using Cursorial.UI.Data;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// One armed <c>When</c> <see cref="DataCondition"/> of a <see cref="StyleRuleFrame"/> (design doc
/// §3.3 / §6.8): the live <see cref="IBindingWatch"/> arming the condition's binding on the styled
/// element plus the condition's verdict. The styling engine binds these at arm time (parallel to
/// <see cref="AncestorStateRequirement"/>) and disposes them at disarm/detach — the watcher lifetime
/// equals the armed rule's lifetime (ledger B16: live across deactivation — it is the re-activation
/// predicate — never paused).
/// </summary>
/// <remarks>
/// The watch delivers the binding's raw/converted value (the data half, S2); this requirement applies
/// the <see cref="DataCondition.IsMetBy"/> verdict (the styling half). An unresolved binding delivers
/// <see cref="UIProperty.UnsetValue"/> ⇒ unmet (doc §3.3). DataContext changes on the element rebind
/// and re-deliver through the watch's own behavior; the engine's reconcile callback fires on every
/// delivery, recomputing satisfaction.
/// </remarks>
internal sealed class WhenConditionRequirement
{
    private readonly DataCondition _condition;
    private IBindingWatch? _watch;

    internal WhenConditionRequirement(DataCondition condition) => _condition = condition;

    /// <summary>Attaches the live watch (set by the engine immediately after construction).</summary>
    internal IBindingWatch? Watch
    {
        get => _watch;
        set => _watch = value;
    }

    /// <summary>Whether the condition currently holds (the watch's last value against the verdict; unset ⇒ unmet).</summary>
    internal bool IsMet => _condition.IsMetBy(_watch is {} w ? w.Value : UIProperty.UnsetValue);

    /// <summary>Disposes the watch (idempotent — disarm/detach end of life).</summary>
    internal void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
    }
}
