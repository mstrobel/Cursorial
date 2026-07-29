// ReSharper disable CheckNamespace

using Cursorial.UI.Controls;

namespace Cursorial.UI;

/// <summary>
/// The DynamicResource subscription surface (design doc §11.5): <see cref="Subscribe"/> for a keyed
/// resource, <see cref="SubscribeControlTheme"/> for the one-handle control-theme watch, and
/// <see cref="GetResourceVersion"/> — the root-global monotonic version S8's <c>FormattedText</c>
/// cache key consumes (the staleness contract, CD16).
/// </summary>
public static class ResourceServices
{
    /// <summary>
    /// Subscribes <paramref name="listener"/> for changes to <paramref name="key"/> resolved from
    /// <paramref name="scope"/>'s chain (design doc §11.5). <paramref name="initialValue"/> is the
    /// resolved value, or <see cref="UIProperty.UnsetValue"/> on a miss.
    /// </summary>
    public static ResourceSubscription Subscribe(UIElement scope, object key, IResourceChangeListener listener, out object? initialValue)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(listener);

        if (RegistryFor(scope) is not { } registry)
        {
            // Detached / no application: a degenerate no-op subscription resolving once.
            initialValue = scope.TryFindResource(key, out var value) ? value : UIProperty.UnsetValue;
            return default;
        }

        var node = registry.Subscribe(scope, key, listener, out initialValue);
        return new ResourceSubscription(node);
    }

    /// <summary>
    /// The control-theme subscription (design doc §11.5, CD13): one handle owning the chain-lookup
    /// node by <c>ControlThemeKey</c>; <paramref name="theme"/> = <c>control.Theme ?? chain lookup</c>.
    /// Styling arms the result at <see cref="StyleLayer.ControlTheme"/> and re-arms on identity change.
    /// The <c>ThemeProperty</c> observer half (the explicit-override identity watch) folds into this
    /// handle at C0 when S8's <c>Control</c> (the <see cref="IControlThemeHost"/> implementor) lands.
    /// </summary>
    public static ResourceSubscription SubscribeControlTheme(IControlThemeHost control, IResourceChangeListener listener, out Style? theme)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(listener);

        var key = control.ControlThemeKey;

        // The explicit Control.Theme override wins; else the chain lookup by ControlThemeKey. Either
        // way the chain node is registered so a variant flip / shadowing re-resolution reaches it; the
        // explicit override short-circuits the initial value.
        var sub = Subscribe(control.Element, key, new ControlThemeListenerAdapter(listener), out var resolved);
        theme = control.ThemeOverride ?? resolved as Style;
        return sub;
    }

    /// <summary>The root-global monotonic resource version (design doc §11.6/CD16); 0 when detached.</summary>
    public static int GetResourceVersion(UIElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return RegistryFor(scope)?.Version ?? 0;
    }

    internal static ResourceSubscriptionRegistry? RegistryFor(UIElement scope)
    {
        // A node registers under the registry of the root its logical chain tops out at (design doc §11.6).
        var root = LogicalRoot(scope);
        return UIApplication.Current?.RegistryForRoot(root);
    }

    internal static UIElement LogicalRoot(UIElement element, bool stopAtTemplateBoundary = false)
    {
        var node = element;

        while (true)
        {
            var next = node.LogicalParent ?? node.TemplatedParent ?? node.VisualParent;

            if (next is null)
                return node;

            if (stopAtTemplateBoundary)
            {
                if (next == node.TemplatedParent) return node;
                if (next is ContentPresenter && node.UIParent is null) return node;
            }

            node = next;
        }
    }

    // A thin adapter so a control-theme listener sees Style? semantics (the registry stores object?).
    private sealed class ControlThemeListenerAdapter(IResourceChangeListener inner) : IResourceChangeListener
    {
        public void OnResourceChanged(object key, object? newValue)
            => inner.OnResourceChanged(key, ReferenceEquals(newValue, UIProperty.UnsetValue) ? null : newValue);
    }
}

/// <summary>
/// The control-theme host seam consumed by <see cref="ResourceServices.SubscribeControlTheme"/>
/// before S8's <c>Control</c> lands at C0 (design doc §11.3). <c>Control</c> implements this at C0.
/// </summary>
public interface IControlThemeHost
{
    /// <summary>The control itself, as a <see cref="UIElement"/> (used to resolve its resource chain). The single implementor (<c>Control</c>) returns <c>this</c>.</summary>
    UIElement Element { get; }

    /// <summary>The control-theme key — exact-key, no base probing (CD13).</summary>
    object ControlThemeKey { get; }

    /// <summary>The per-instance <c>Control.Theme</c> override, or <see langword="null"/>.</summary>
    Style? ThemeOverride { get; }
}
