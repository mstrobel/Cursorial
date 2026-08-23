using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// A source of firings (design doc §2/§4): owns an <see cref="Actions"/> collection and runs it via
/// <see cref="Fire"/> when the subclass's condition occurs (an event raise, a data condition becoming
/// true). Attaching a trigger attaches its actions to the same host; detaching detaches them.
/// </summary>
public abstract class TriggerBase : UIObject, IAttachedObject
{
    private TriggerActionCollection? _actions;

    /// <summary>The actions this trigger runs when it fires (order preserved; lazily created).</summary>
    public TriggerActionCollection Actions
    {
        get
        {
            if (_actions is null)
            {
                _actions = new TriggerActionCollection();
                if (AssociatedObject is { } host)
                    _actions.AttachAllTo(host); // created after the trigger attached — go live now, so adds attach
            }

            return _actions;
        }
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
        // Actions ride the trigger's own lifecycle: same host, attach-with, detach-with (§2). The
        // collection's tree-following is NOT engaged here — the trigger IS the lifecycle authority
        // for its actions (its own collection already deferred to the host's tree attachment).
        _actions?.AttachAllTo(host);
        OnAttached();
    }

    /// <inheritdoc/>
    public void Detach()
    {
        if (AssociatedObject is null)
            return;

        try
        {
            OnDetaching();
        }
        finally
        {
            _actions?.DetachAll();
            AssociatedObject = null;
        }
    }

    /// <summary>
    /// Runs every attached, enabled action in order, passing this trigger's host as the sender and
    /// <paramref name="parameter"/> as the payload (an <c>EventTrigger</c> passes the routed event args).
    /// Snapshot semantics: an action that mutates <see cref="Actions"/> during the run doesn't affect
    /// this firing.
    /// </summary>
    protected void Fire(object? parameter)
    {
        if (_actions is not { Count: > 0 } actions)
            return;

        var snapshot = new TriggerAction[actions.Count];
        actions.CopyTo(snapshot, 0);
        foreach (var action in snapshot)
            action.Execute(AssociatedObject, parameter);
    }

    /// <summary>Hook the condition source here (e.g. <c>AddHandler</c> the routed event).</summary>
    protected virtual void OnAttached()
    {
    }

    /// <summary>Unhook EVERYTHING here. <see cref="AssociatedObject"/> is still set during the call.</summary>
    protected virtual void OnDetaching()
    {
    }

    private protected virtual void ValidateHost(object host)
    {
    }
}

/// <summary>A <see cref="TriggerBase"/> whose host must be a <typeparamref name="T"/>.</summary>
public abstract class TriggerBase<T> : TriggerBase where T : class
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
