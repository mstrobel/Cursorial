using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// A unit of effect a trigger runs when it fires (design doc §2/§5): <see cref="Execute"/> gates on
/// <see cref="IsEnabled"/> and calls the subclass <see cref="Invoke"/>. Actions attach to the SAME host
/// as their owning trigger (the trigger attaches its <c>Actions</c> when it attaches), so an action can
/// read/target the host directly.
/// </summary>
public abstract class TriggerAction : UIObject, IAttachedObject
{
    /// <summary>Gates <see cref="Execute"/> (a disabled action is skipped, never invoked). Default true.</summary>
    public static readonly StyledProperty<bool> IsEnabledProperty =
        UIProperty.Register<TriggerAction, bool>(nameof(IsEnabled), defaultValue: true);

    /// <summary>Whether the action runs when its trigger fires.</summary>
    public bool IsEnabled
    {
        get => GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    /// <inheritdoc/>
    public object? AssociatedObject { get; private set; }

    /// <inheritdoc/>
    public void Attach(object host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (ReferenceEquals(AssociatedObject, host))
            return;

        if (AssociatedObject is not null)
            throw new InvalidOperationException(
                $"This {GetType().Name} is already attached to another object. Detach it first.");

        ValidateHost(host);
        AssociatedObject = host;
        try
        {
            // The BD13 InputBinding precedent: parent to the host so DataContext (and the resource chain)
            // inherits — a Command="{Binding …}" on this action anchors on the host's DataContext.
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
            AssociatedObject = null;
            SetInheritanceParent(null);
            _detaching = false;
        }
    }

    /// <summary>Runs the action: skipped when <see cref="IsEnabled"/> is false, else <see cref="Invoke"/>.</summary>
    /// <param name="sender">The firing trigger's host (its <c>AssociatedObject</c>).</param>
    /// <param name="parameter">The trigger's firing payload (e.g. the routed event args).</param>
    public void Execute(object? sender, object? parameter)
    {
        if (!IsEnabled)
            return;

        Invoke(sender, parameter);
    }

    /// <summary>The action's effect (see <see cref="Execute"/> for the argument contract).</summary>
    protected abstract void Invoke(object? sender, object? parameter);

    /// <summary>Hook the host here (optional — many actions are stateless until invoked).</summary>
    protected virtual void OnAttached()
    {
    }

    /// <summary>Unhook everything here. <see cref="AssociatedObject"/> is still set during the call.</summary>
    protected virtual void OnDetaching()
    {
    }

    private protected virtual void ValidateHost(object host)
    {
    }
}

/// <summary>A <see cref="TriggerAction"/> whose host must be a <typeparamref name="T"/>.</summary>
public abstract class TriggerAction<T> : TriggerAction where T : class
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
