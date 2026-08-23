using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// A unit of standalone attached logic (Blend parity, design doc §2): hooks its host in
/// <see cref="OnAttached"/> and MUST unhook everything in <see cref="OnDetaching"/> (the S2/S5
/// leak-tracker discipline — a behavior that leaves a subscription behind pins its host alive).
/// Built on <see cref="UIObject"/> so behaviors are first-class property-system objects (bindings,
/// resources, <c>x:Name</c>). Lifecycle (§3): attach when the host enters an attached tree (or
/// immediately for a non-<see cref="UIElement"/> host), detach when it leaves, re-attach on re-entry.
/// </summary>
public abstract class Behavior : UIObject, IAttachedObject
{
    /// <inheritdoc/>
    public object? AssociatedObject { get; private set; }

    /// <inheritdoc/>
    public void Attach(object host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (ReferenceEquals(AssociatedObject, host))
            return; // already attached to this host — the collection may re-signal on tree re-entry

        if (AssociatedObject is not null)
            throw new InvalidOperationException(
                $"This {GetType().Name} is already attached to another object. Detach it first (an attached " +
                "object has exactly one host).");

        ValidateHost(host);
        AssociatedObject = host;
        try
        {
            // The BD13 InputBinding precedent: parent to the host so DataContext (and the resource chain)
            // inherits — a {Binding …} on this object anchors on the host's DataContext.
            if (host is UIObject inheritanceParent)
                SetInheritanceParent(inheritanceParent);
            OnAttached();
        }
        catch
        {
            // ROLLBACK (audit): a throwing OnAttached must not leave a half-attached zombie. Loud + consistent.
            AssociatedObject = null;
            SetInheritanceParent(null);
            throw;
        }
    }

    private bool _detaching;

    /// <inheritdoc/>
    public void Detach()
    {
        // The re-entrancy guard (audit): an OnDetaching that removes THIS item from its collection re-enters
        // Detach through RemoveItem — unguarded that recursed unboundedly (stack overflow).
        if (AssociatedObject is null || _detaching)
            return;

        _detaching = true;
        try
        {
            OnDetaching();
        }
        finally
        {
            AssociatedObject = null; // cleared even if OnDetaching throws — a half-detached zombie is worse
            SetInheritanceParent(null);
            _detaching = false;
        }
    }

    /// <summary>Hook the host here. <see cref="AssociatedObject"/> is set.</summary>
    protected virtual void OnAttached()
    {
    }

    /// <summary>Unhook EVERYTHING here. <see cref="AssociatedObject"/> is still set during the call.</summary>
    protected virtual void OnDetaching()
    {
    }

    /// <summary>Typed subclasses reject an incompatible host before any state changes.</summary>
    private protected virtual void ValidateHost(object host)
    {
    }
}

/// <summary>A <see cref="Behavior"/> whose host must be a <typeparamref name="T"/> (design doc §2).</summary>
public abstract class Behavior<T> : Behavior where T : class
{
    /// <summary>The typed host (null while detached).</summary>
    public new T? AssociatedObject => (T?)base.AssociatedObject;

    private protected override void ValidateHost(object host)
    {
        if (host is not T)
            throw new InvalidOperationException(
                $"{GetType().Name} requires a host of type {typeof(T).Name}; got {host.GetType().Name}.");
    }
}
