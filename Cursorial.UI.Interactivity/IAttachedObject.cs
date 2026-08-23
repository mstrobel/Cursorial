namespace Cursorial.UI.Interactivity;

/// <summary>
/// The shared attach contract (design doc §2): an object that associates with a single host —
/// <see cref="Behavior"/>, <see cref="TriggerBase"/>, and <see cref="TriggerAction"/> all implement it (a
/// trigger attaches its actions when it attaches). Attach/Detach are driven by the owning collection's
/// lifecycle (§3): items go live when the host enters an attached tree and unhook when it leaves.
/// </summary>
public interface IAttachedObject
{
    /// <summary>The host this object is attached to, or <see langword="null"/> while detached.</summary>
    object? AssociatedObject { get; }

    /// <summary>Associates this object with <paramref name="host"/> and runs its attach logic.</summary>
    void Attach(object host);

    /// <summary>Runs the detach logic and clears <see cref="AssociatedObject"/>. Idempotent.</summary>
    void Detach();
}
