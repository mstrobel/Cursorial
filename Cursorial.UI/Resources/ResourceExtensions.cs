using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The resource lookup chain (design doc §11.4, CD11) plus the DynamicResource producer install
/// (<see cref="SetResourceReference{T}"/>). The walk is:
/// <c>element → logical ancestors (with the template-root resource hop) → visual root →
/// UIApplication.Resources → UIApplication.Theme → ThemeContributions → CursorialTheme.BuiltIn</c>; probing
/// each resourceful node at <c>ActualThemeVariant</c>. <see cref="IResourceHost.HasResources"/>
/// short-circuits a node with no resources; <see cref="ThemeContributions"/> is the library-registered tier
/// (design doc §11.3a), probed only when non-empty.
/// </summary>
public static class ResourceExtensions
{
    /// <summary>Resolves <paramref name="key"/> through the chain at the application's <c>ActualThemeVariant</c>; throws when missing (design doc §11.4).</summary>
    /// <exception cref="ResourceNotFoundException">The key resolves nowhere; the message renders the searched chain.</exception>
    public static object? FindResource(this UIElement element, object key)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(key);

        if (element.TryFindResource(key, out var value))
            return value;

        var searched = new List<string>();
        Walk(element, key, ResolveVariant(null), searched, out _);
        throw new ResourceNotFoundException(key, searched);
    }

    /// <summary>Resolves <paramref name="key"/> through the chain at the application's <c>ActualThemeVariant</c>; returns false on a miss.</summary>
    public static bool TryFindResource(this UIElement element, object key, out object? value)
        => element.TryFindResource(key, null, out value);

    /// <summary>Resolves <paramref name="key"/> through the chain at an explicit variant; returns false on a miss.</summary>
    public static bool TryFindResource(this UIElement element, object key, ThemeVariant? variant, out object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(key);

        return Walk(element, key, ResolveVariant(variant), searched: null, out value);
    }

    private static ThemeVariant ResolveVariant(ThemeVariant? explicitVariant)
        => explicitVariant ?? UIApplication.Current?.ActualThemeVariant ?? new ThemeVariant(ThemeBase.Dark, Output.ColorDepth.Truecolor);

    /// <summary>The maximum resource-alias (<see cref="ResourceReference"/>) chase depth before a cycle is declared (design doc §11.4a).</summary>
    private const int MaxAliasChase = 8;

    /// <summary>
    /// The chain walk with <see cref="ResourceReference"/> alias chasing (design doc §11.4a):
    /// a resolved value that is a <see cref="ResourceReference"/> is a live alias — its key is re-resolved
    /// from the SAME <paramref name="element"/> (so an app override of either the alias key or the target
    /// wins), bounded by <see cref="MaxAliasChase"/>. This is the per-control-key spine: the style-guide
    /// <c>Theme.&lt;Control&gt;&lt;Role&gt;</c> keys are variant-agnostic aliases of the palette role
    /// tokens, so a control template references its own key while a single role-token brush backs every
    /// consumer. <paramref name="searched"/> records hops for the diagnostic surface when non-null.
    /// </summary>
    internal static bool Walk(UIElement element, object key, ThemeVariant variant, List<string>? searched, out object? value)
        => Walk(element, key, variant, searched, out value, out _);

    /// <summary>
    /// The full chain walk (see the sibling overload's remarks). <c>chasedAlias</c> reports whether resolution
    /// followed at least one <see cref="ResourceReference"/> hop — the subscription registry uses it to keep an
    /// aliased subscription interested in keyed pulses of the target.
    /// </summary>
    internal static bool Walk(UIElement element, object key, ThemeVariant variant, List<string>? searched, out object? value, out bool chasedAlias)
    {
        chasedAlias = false;
        for (var hop = 0; ; hop++)
        {
            if (!WalkOnce(element, key, variant, searched, out value))
                return false;
            if (value is not ResourceReference alias)
                return true;
            chasedAlias = true;
            if (hop >= MaxAliasChase)
                return AliasCycle(key, searched, out value);
            searched?.Add($"  → alias → '{alias.Key}'");
            key = alias.Key;
        }
    }

    private static bool AliasCycle(object key, List<string>? searched, out object? value)
    {
        searched?.Add($"  → alias chain for '{key}' exceeded depth {MaxAliasChase} (cycle)");
        ResourceDiagnostics.OnCycle($"Resource alias chain for '{key}' exceeded depth {MaxAliasChase} — a cyclic ResourceReference (design doc §11.4a).");
        value = null;
        return false;
    }

    private static bool WalkOnce(UIElement element, object key, ThemeVariant variant, List<string>? searched, out object? value)
    {
        // 1. element → logical ancestors, with the template-root resource hop (CD11).
        var node = (UIElement?)element;
        while (node is not null)
        {
            if (node.HasResources)
            {
                searched?.Add($"element {Describe(node)} Resources");
                if (node.ResourcesOrNull!.TryGetResource(key, variant, out value))
                {
                    searched?.Add($"  → HIT at {Describe(node)}");
                    return true;
                }
            }
            else
            {
                searched?.Add($"element {Describe(node)} (no resources — skipped)");
            }

            // Template-root hop (CD11): a template part has LogicalParent == null and a non-null
            // TemplatedParent; the owning template's sealed Resources slot in, then continue at the
            // templated parent. The template Resources live on the TEMPLATED PARENT (the Control owns
            // the TemplateInstance), not on the part — a part is typically a plain element whose own
            // TemplateResourcesForChainWalk is null.
            if (node.LogicalParent is null && node.TemplatedParent is { } templatedParent)
            {
                if (templatedParent.TemplateResourcesForChainWalk is { } templateResources)
                {
                    searched?.Add($"template Resources of {Describe(templatedParent)}");
                    if (templateResources.TryGetResource(key, variant, out value))
                    {
                        searched?.Add("  → HIT in template Resources");
                        return true;
                    }
                }

                node = templatedParent;
                continue;
            }

            node = node.UIParent;
        }

        // Non-chasing tail: an alias found here bubbles back to Walk's loop, which re-chases from the
        // element (so an element-scoped override of the alias target still wins).
        return WalkApplicationTailOnce(key, variant, searched, out value);
    }

    /// <summary>The application tail (shared by element-rooted and detached lookups) with alias chasing: app Resources → app Theme → BuiltIn.</summary>
    internal static bool WalkApplicationTail(object key, ThemeVariant variant, List<string>? searched, out object? value)
    {
        for (var hop = 0; ; hop++)
        {
            if (!WalkApplicationTailOnce(key, variant, searched, out value))
                return false;
            if (value is not ResourceReference alias)
                return true;
            if (hop >= MaxAliasChase)
                return AliasCycle(key, searched, out value);
            searched?.Add($"  → alias → '{alias.Key}'");
            key = alias.Key;
        }
    }

    private static bool WalkApplicationTailOnce(object key, ThemeVariant variant, List<string>? searched, out object? value)
    {
        if (UIApplication.Current is { } app)
        {
            searched?.Add(app.ResourcesOrNull is null ? "UIApplication.Resources (empty)" : "UIApplication.Resources");
            if (app.ResourcesOrNull is { } resources && resources.TryGetResource(key, variant, out value))
            {
                searched?.Add("  → HIT in UIApplication.Resources");
                return true;
            }

            searched?.Add(app.Theme is null ? "UIApplication.Theme (none)" : "UIApplication.Theme");
            if (app.Theme is { } theme && theme.TryGetResource(key, variant, out value))
            {
                searched?.Add("  → HIT in UIApplication.Theme");
                return true;
            }
        }

        // The assembly theme-contribution tier (design doc §11.3a): library-registered dictionaries, above
        // BuiltIn so a contribution overrides the built-in default and may reference core ThemeKeys (resolved
        // onward in BuiltIn); below App so an app overrides any contributed key. Short-circuited (no hop, no
        // diagnostic text) when nothing is registered — the common case for an app that uses no bar/control
        // library.
        if (ThemeContributions.HasContributions)
        {
            searched?.Add("ThemeContributions");
            if (ThemeContributions.TryGetResource(key, variant, out value))
            {
                searched?.Add("  → HIT in ThemeContributions");
                return true;
            }
        }

        searched?.Add("CursorialTheme.BuiltIn");
        if (CursorialTheme.BuiltIn.TryGetResource(key, variant, out value))
        {
            searched?.Add("  → HIT in CursorialTheme.BuiltIn");
            return true;
        }

        value = null;
        return false;
    }

    private static string Describe(UIElement element)
        => element.Name is { } name ? $"{element.GetType().Name}#{name}" : element.GetType().Name;

    // ───────────────────────────── DynamicResource on a direct property (CD12) ─────────────────────────────

    /// <summary>
    /// Installs a DynamicResource producer on a styled property (design doc §11.5): a value producer
    /// at <see cref="BindingPriority.LocalValue"/>, re-resolved live on resource pulses reaching the
    /// element's root. A later <c>SetValue</c>/<c>Bind</c> evicts it (it disposes its subscription —
    /// no zombie clobber); <c>ClearValue</c> detaches it. A miss is <see cref="UIProperty.UnsetValue"/>
    /// end-to-end (lower sources promote).
    /// </summary>
    public static void SetResourceReference<T>(this UIElement element, StyledProperty<T> property, object key)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(key);

        DynamicResourceProducer<T>.Install(element, property, key);
    }
}
