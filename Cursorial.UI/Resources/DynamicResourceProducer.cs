// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>An element-held resource producer; the attach/detach walk re-registers/unregisters its registry node (CD16).</summary>
internal interface IResourceSubscriber
{
    /// <summary>Re-registers the registry node and forces one re-resolve (element attach, CD16).</summary>
    void OnElementAttached();

    /// <summary>Unregisters the registry node (element detach).</summary>
    void OnElementDetached();

    /// <summary>The (property, resource key) this subscriber feeds — the resource-provenance diagnostic seam (W3); null for a non-property-bound subscriber.</summary>
    (UIProperty Property, object Key)? ResourceProvenance { get; }
}

/// <summary>
/// A DynamicResource producer on a direct (styled) property (design doc §11.5, CD12): installs a
/// <see cref="BindingPriority.LocalValue"/> producer, subscribes the element's registry for the key,
/// pushes the resolved value (or withdraws on a miss / <see cref="UIProperty.UnsetValue"/>), and
/// evicts cleanly when displaced by a later <c>SetValue</c>/<c>Bind</c> (no zombie clobber).
/// </summary>
internal sealed class DynamicResourceProducer<T> : IResourceChangeListener, IValueEvictionListener, IResourceSubscriber
{
    private readonly UIElement _element;
    // ReSharper disable once NotAccessedField.Local
    private readonly StyledProperty<T> _property;
    private readonly object _key;
    private BindingEntry<T>? _entry;
    private ResourceSubscription _subscription;
    private bool _missDiagnosticEmitted;

    private DynamicResourceProducer(UIElement element, StyledProperty<T> property, object key)
    {
        _element = element;
        _property = property;
        _key = key;
    }

    public static void Install(UIElement element, StyledProperty<T> property, object key)
    {
        var producer = new DynamicResourceProducer<T>(element, property, key);
        // §20/PD24: a SetResourceReference issued inside a template-instantiation scope installs at
        // the Template lane (overridable by a page/theme Style); outside, LocalValue. The entry's
        // Priority is fixed here at install, so later resource-change pushes keep the captured lane.
        var priority = TemplateInstantiationScope.CurrentInstallPriority;
        producer._entry = element.Bind(property, priority, producer);
        if (priority == BindingPriority.Template)
            producer._entry.SourceKind = ValueSourceKind.TemplateResource; // PD25 within-lane provenance
        element.RegisterResourceSubscriber(producer);
        producer.SubscribeAndPush();
    }

    private void SubscribeAndPush()
    {
        _subscription = ResourceServices.Subscribe(_element, _key, this, out var initial);
        Apply(initial);
    }

    public void OnResourceChanged(object key, object? newValue) => Apply(newValue);

    private void Apply(object? value)
    {
        if (_entry is not { } entry || entry.IsDetached)
            return;

        if (ReferenceEquals(value, UIProperty.UnsetValue))
        {
            entry.SetUnset(); // HasValue = false ⇒ lower sources promote (CD12)
            EmitMissDiagnosticOnce();
            return;
        }

        // Type-incompatible resources are discarded with a diagnostic (design doc §11.5).
        if (value is T typed)
            entry.SetValue(typed);
        else if (value is null && default(T) is null)
            entry.SetValue(default!);
        else
            ResourceDiagnostics.OnRejectedValue(_key, $"resource '{_key}' resolved to {value?.GetType().Name ?? "null"}, incompatible with {typeof(T).Name}");
    }

    private void EmitMissDiagnosticOnce()
    {
        if (_missDiagnosticEmitted)
            return;
        _missDiagnosticEmitted = true;
        ResourceDiagnostics.OnMiss(_element, _key);
    }

    // The store evicted us (a later SetValue/Bind at LocalValue, ClearValue, or TearDown): dispose
    // our subscription so the next pulse can't clobber the displacing value (CD12).
    public void OnEvicted(BindingEntryBase entry)
    {
        _subscription.Dispose();
        _element.UnregisterResourceSubscriber(this);
        _entry = null;
    }

    // ───────────────────────────── IResourceSubscriber (attach/detach hygiene) ─────────────────────────────

    public void OnElementAttached()
    {
        if (_entry is null)
            return; // already evicted

        // Re-subscribe against the (possibly new) root and force one re-resolve regardless of stored
        // version — covers cross-root moves (CD16). Dispose any stale node first.
        _subscription.Dispose();
        SubscribeAndPush();
    }

    public void OnElementDetached() => _subscription.Dispose();

    // The resource-provenance seam (W3): which key this producer feeds which property.
    public (UIProperty Property, object Key)? ResourceProvenance => (_property, _key);
}
