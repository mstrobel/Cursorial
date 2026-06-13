namespace Cursorial.UI;

/// <summary>
/// A live resource subscription handle (design doc §11.5). Wraps one registry node; copies share
/// the same node. <see cref="Pause"/>/<see cref="Resume"/> are O(1) flag writes (they ride the
/// <c>:pointerover</c> hot path under any-event motion). <c>default</c> is a no-op.
/// </summary>
public struct ResourceSubscription : IDisposable
{
    private ResourceSubscriptionRegistry.Node? _node;

    internal ResourceSubscription(ResourceSubscriptionRegistry.Node node) => _node = node;

    /// <summary>Pauses delivery without unregistering (frame deactivation, design doc §11.5). O(1).</summary>
    public void Pause() => _node?.Pause();

    /// <summary>Resumes delivery, catching up at most one re-resolve via version compare (design doc §11.5). O(1).</summary>
    public void Resume() => _node?.Resume();

    /// <summary>Unregisters the node (frame disarm / element detach). Idempotent (design doc §11.5).</summary>
    public void Dispose()
    {
        _node?.Dispose();
        _node = null;
    }
}

/// <summary>The DynamicResource change-notification seam (design doc §11.5).</summary>
public interface IResourceChangeListener
{
    /// <summary>Invoked when a subscribed key's resolved value changes; <see cref="UIProperty.UnsetValue"/> on a transition-to-missing.</summary>
    void OnResourceChanged(object key, object? newValue);
}
