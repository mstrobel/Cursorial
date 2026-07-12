namespace Cursorial.UI;

public abstract partial class UIElement : IResourceHost
{
    private ResourceDictionary? _resources;

    /// <summary>
    /// The element's keyed resource dictionary (design doc §11.1) — lazily allocated. Resources
    /// scoped here shadow ancestor/application/theme resources for this element and its logical
    /// subtree (the lookup chain, design doc §11.4).
    /// </summary>
    public ResourceDictionary Resources
    {
        get
        {
            VerifyAccess();

            if (_resources is null)
            {
                _resources = new ResourceDictionary();
                _resources.Acquire(this);
                _resources.Changed += OnOwnResourcesChanged;
            }

            return _resources;
        }
    }

    /// <summary>Whether a non-empty dictionary has been allocated — the chain-walk short-circuit (C29/C51).</summary>
    public bool HasResources => _resources is { Count: > 0 } or { HasThemeDictionaries: true };

    /// <summary>The resources slot without lazy allocation (the chain walk's read surface).</summary>
    internal ResourceDictionary? ResourcesOrNull => _resources;

    // Inline handle list of this element's DynamicResource producers + control-theme nodes — detach
    // unregisters them in O(own subscriptions) and re-attach re-resolves each (CD16, design doc §11.6).
    private List<IResourceSubscriber>? _resourceSubscribers;

    // Property ids whose DEFAULT-tier read resolved through a DefaultResourceKey here — the
    // theme catch-all repaints exactly these readers (a handful of ids at most; UI-thread affine).
    private int[] _themedDefaultReads = [];

    internal void MarkThemedDefaultRead(int propertyId)
    {
        if (Array.IndexOf(_themedDefaultReads, propertyId) < 0)
            _themedDefaultReads = [.. _themedDefaultReads, propertyId];
    }

    internal bool HasThemedDefaultRead(int propertyId) => Array.IndexOf(_themedDefaultReads, propertyId) >= 0;

    internal void RegisterResourceSubscriber(IResourceSubscriber subscriber)
        => (_resourceSubscribers ??= []).Add(subscriber);

    internal void UnregisterResourceSubscriber(IResourceSubscriber subscriber)
        => _resourceSubscribers?.Remove(subscriber);

    /// <summary>The resource key + producer lane an instance <c>SetResourceReference</c>/<c>{DynamicResource}</c>
    /// feeds <paramref name="property"/>, or <see langword="null"/> (the W3 resource-provenance seam; the lane
    /// lets <c>ResourceDiagnostics.GetResourceKey</c> gate on the reference actually owning the winning base).</summary>
    internal (object Key, BindingPriority Priority)? FindInstanceResourceKey(UIProperty property)
    {
        if (_resourceSubscribers is {} subscribers)
        {
            foreach (var subscriber in subscribers)
            {
                if (subscriber.ResourceProvenance is {} provenance && ReferenceEquals(provenance.Property, property))
                    return (provenance.Key, provenance.Priority);
            }
        }

        return null;
    }

    /// <summary>The attach-walk resource step: (re)register each producer's node and force one re-resolve (CD16).</summary>
    internal void OnResourcesAttached()
    {
        if (_resourceSubscribers is not {} subscribers)
            return;

        for (var i = 0; i < subscribers.Count; i++)
            subscribers[i].OnElementAttached();
    }

    /// <summary>The detach-walk resource step: unregister each producer's node (O(own subscriptions)).</summary>
    internal void OnResourcesDetached()
    {
        if (_resourceSubscribers is not {} subscribers)
            return;

        for (var i = 0; i < subscribers.Count; i++)
            subscribers[i].OnElementDetached();
    }

    private void OnOwnResourcesChanged(object? sender, ResourcesChangedEventArgs e)
        => UIApplication.Current?.OnResourceDictionaryChanged(this, _resources!, e);

    /// <summary>
    /// The owning control template's sealed <c>Resources</c>, for the chain-walk template-root hop
    /// (design doc §11.4, CD11). Populated by S8's <c>Control</c>/<c>TemplateInstance</c> at C0;
    /// <see langword="null"/> until then. Read only when <see cref="LogicalParent"/> is null and
    /// <see cref="TemplatedParent"/> is non-null (a template root).
    /// </summary>
    internal virtual ResourceDictionary? TemplateResourcesForChainWalk => null;
}