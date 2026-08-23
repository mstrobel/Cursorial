using Cursorial.Markup;
using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// A source of firings (design doc §2/§4): owns an <see cref="Actions"/> collection and runs it via
/// <see cref="Fire"/> when the subclass's condition occurs (an event raise, a data condition becoming
/// true). Attaching a trigger attaches its actions to the same host; detaching detaches them.
/// <c>Actions</c> is the XAML content property, so <c>&lt;i:EventTrigger&gt;&lt;i:InvokeCommandAction/&gt;</c>
/// fills it implicitly.
/// </summary>
[ContentProperty(nameof(Actions))]
public abstract class TriggerBase : UIObject, IAttachedObject
{
    private TriggerActionCollection? _actions;
    private bool _detaching;

    /// <summary>The actions collection if created (the teardown sweep's cascade seam).</summary>
    internal TriggerActionCollection? ActionsOrNull => _actions;

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
        try
        {
            // The BD13 InputBinding precedent: parent to the host so DataContext (and the resource chain)
            // inherits — a {Binding …} on this trigger (or its actions) anchors on the host's DataContext.
            if (host is UIObject inheritanceParent)
                SetInheritanceParent(inheritanceParent);
            // Actions ride the trigger's own lifecycle: same host, attach-with, detach-with (§2). The
            // collection's tree-following is NOT engaged here — the trigger IS the lifecycle authority
            // for its actions (its own collection already deferred to the host's tree attachment).
            _actions?.AttachAllTo(host);
            OnAttached();
        }
        catch
        {
            // ROLLBACK (audit: a throwing OnAttached — an unresolvable EventName — left a half-attached
            // zombie: AssociatedObject set, no hook). The throw stays loud; the state stays consistent.
            _actions?.DetachAll();
            AssociatedObject = null;
            SetInheritanceParent(null);
            throw;
        }
    }

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
            _actions?.DetachAll();
            AssociatedObject = null;
            SetInheritanceParent(null);
            _detaching = false;
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
