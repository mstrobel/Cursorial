// ReSharper disable CheckNamespace

using Cursorial.Media;

namespace Cursorial.UI;

/// <summary>
/// Resource lookup / subscription diagnostics (design doc §11.10): hop-by-hop lookup records
/// (<see cref="Trace"/>/<see cref="Explain"/>), the subscription leak-hunting surface
/// (<see cref="Subscriptions"/>), and deferred-entry inspection (<see cref="DeferredEntries"/>).
/// Also the channel for the one-time miss / rejected-value / cycle diagnostics.
/// </summary>
public static class ResourceDiagnostics
{
    // One-time miss diagnostics, keyed by (element, key) so a given reference warns once (design doc §11.5).
    private static readonly HashSet<(UIElement, object)> MissedOnce = [];

    /// <summary>Raised once per (element, key) on a first DynamicResource miss (design doc §11.5/CD12).</summary>
    public static event Action<string>? ResourceMissed;

    /// <summary>Raised when a resolved resource value is type-incompatible with its consumer (design doc §11.5).</summary>
    public static event Action<string>? RejectedValue;

    /// <summary>Raised on a resource pulse cycle (the generation-cap backstop, design doc §11.6).</summary>
    public static event Action<string>? Cycle;

    /// <summary>The hop-by-hop trace of resolving <paramref name="key"/> from <paramref name="element"/> (design doc §11.10).</summary>
    public static IReadOnlyList<string> Trace(UIElement element, object key)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(key);

        var lines = new List<string>();
        var variant = UIApplication.Current?.ActualThemeVariant ?? new ThemeVariant(ThemeBase.Dark, ColorDepth.Truecolor);
        ResourceExtensions.Walk(element, key, variant, lines, out _);
        return lines;
    }

    /// <summary>Renders the searched chain for <paramref name="key"/> (the <see cref="ResourceNotFoundException.SearchedScopes"/> shape) — equivalent to <see cref="Trace"/> for a miss (design doc §11.10).</summary>
    public static IReadOnlyList<string> Explain(UIElement element, object key) => Trace(element, key);

    /// <summary>
    /// The <b>reverse</b> of <see cref="Trace"/>: the resource key the effective value of
    /// <paramref name="property"/> on <paramref name="element"/> resolved <em>through</em> — an instance
    /// <c>SetResourceReference</c>/<c>{DynamicResource}</c> (checked first, since it wins at the Local/Template
    /// lane), else a style/theme <c>{DynamicResource}</c> setter — or <see langword="null"/> when the value is
    /// not resource-backed. The resource-inspector hook (design doc §14 P9), a companion to
    /// <c>StyleDiagnostics.Explain</c>; pair the key with <see cref="Trace"/> to render the full lookup chain.
    /// Cold path.
    /// </summary>
    public static object? GetResourceKey(UIElement element, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);
        element.VerifyAccess();

        var basePriority = element.GetValueSource(property).BasePriority;

        // An instance SetResourceReference/{DynamicResource} produces at LocalValue (document-level) or
        // Template (in-template) — report its key only while ITS OWN lane owns the winning base (audit fix
        // 2026-07-12): under the amended lattice an active conditional rule pierces a Template-lane
        // reference (and any stronger lane masks either), making the effective value not-resource-backed
        // even though the subscription stays live. Gate on the BASE lane so an animation holding a
        // resource-backed value still reports its key.
        if (element.FindInstanceResourceKey(property) is { } instance && instance.Priority == basePriority)
            return instance.Key;

        // Else a style/theme {DynamicResource} setter — but ONLY when a style slot actually owns the effective
        // value (either lane: a conditional rule at StyleTrigger or a resting rule at Style). A LocalValue (or
        // Template) literal masking a resource-backed style setter is NOT resource-backed: the winning value is
        // the literal, so report no key (else this would lie about provenance). Pass the winning lane down so
        // the key comes from the slot that won, not a masked rule in the other slot.
        return basePriority is BindingPriority.Style or BindingPriority.StyleTrigger
            ? element.GetWinningStyleResourceKey(property, basePriority)
            : null;
    }

    /// <summary>The live registry nodes for a root — the subscription leak-hunting surface (design doc §11.10); empty after the owners detach.</summary>
    public static int Subscriptions(UIElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return UIApplication.Current?.RegistryForRoot(ResourceServices.LogicalRoot(root)).LiveNodeCount ?? 0;
    }

    /// <summary>The deferred-entry states + <c>RealizedAtVariant</c> for a dictionary (design doc §11.10/CD7).</summary>
    public static IReadOnlyList<DeferredEntryInfo> DeferredEntries(ResourceDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return [.. dictionary.EnumerateDeferredEntries()];
    }

    internal static void OnMiss(UIElement element, object key)
    {
        if (!MissedOnce.Add((element, key)))
            return;

        var chain = string.Join(" → ", Trace(element, key));
        ResourceMissed?.Invoke($"Resource '{key}' not found (first miss). Chain: {chain}");
    }

    internal static void OnRejectedValue(object key, string message)
        => RejectedValue?.Invoke($"Resource '{key}': {message}");

    internal static void OnCycle(string message) => Cycle?.Invoke(message);

    /// <summary>Test seam: clears the one-time miss memo so a test can re-observe the diagnostic.</summary>
    internal static void ResetMissMemo() => MissedOnce.Clear();
}
