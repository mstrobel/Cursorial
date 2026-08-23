using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// The typed sink a view-model implements to receive its view (design doc §9): a typed interface
/// rather than a reflection set, so the pattern stays trimming/AOT-clean.
/// </summary>
public interface IResourceRootSink
{
    /// <summary>Receives the registered view element (<see langword="null"/> when it unregisters).</summary>
    void SetResourceRoot(UIElement? root);
}

/// <summary>
/// The self→VM registration behavior (design doc §9 — the pattern <c>{x:Self}</c> can never express,
/// because <c>DataContext</c> is attach-time): while attached, hands the HOST element to its
/// <c>DataContext</c> whenever that context is (or becomes) an <see cref="IResourceRootSink"/> — so a
/// view-model can use its view as a resource-resolution root without touching the element tree. Tracks
/// <c>DataContext</c> swaps (registering with the new sink, unregistering from the old) and unregisters
/// on detach so a dead view is never pinned.
/// </summary>
public sealed class ViewRegistration : Behavior<UIElement>, IValueObserver<object?>
{
    private IDisposable? _subscription;
    private IResourceRootSink? _registeredWith;

    /// <inheritdoc/>
    protected override void OnAttached()
    {
        var host = AssociatedObject!;
        _subscription = host.AddObserver(UIElement.DataContextProperty, this);
        Register(host.DataContext);
    }

    /// <inheritdoc/>
    protected override void OnDetaching()
    {
        _subscription?.Dispose();
        _subscription = null;
        Register(null); // unregister — a detached view must not be pinned by its VM
    }

    void IValueObserver<object?>.OnPropertyChanged(
        UIObject source, UIProperty property, object? oldValue, object? newValue, BindingPriority priority)
        => Register(newValue);

    private void Register(object? dataContext)
    {
        var sink = dataContext as IResourceRootSink;
        if (ReferenceEquals(sink, _registeredWith))
            return;

        _registeredWith?.SetResourceRoot(null);
        _registeredWith = sink;
        sink?.SetResourceRoot(AssociatedObject);
    }
}
