namespace Cursorial.UI.Themes;

/// <summary>
/// The assembly theme-contribution tier (design doc §11.3a): a process-shared, ordered set of sealed
/// <see cref="ResourceDictionary"/> instances a control library registers — typically from a
/// <c>[ModuleInitializer]</c> — to ship its default control themes <b>and</b> the brushes/resources those
/// themes reference through <c>{DynamicResource}</c> / <see cref="Style.SetResource{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The tier sits in the resource chain BETWEEN <c>UIApplication.Theme</c> and
/// <see cref="CursorialTheme.BuiltIn"/>:
/// <c>element → ancestors → App.Resources → App.Theme → ThemeContributions → BuiltIn</c>. So an app still
/// overrides any contributed key (App.* is nearer the element), a contribution overrides the BuiltIn default,
/// and a contribution's own control theme may reference core <see cref="ThemeKeys"/> (resolved onward in
/// BuiltIn) as well as its own keys (resolved in the same contributed dictionary).
/// </para>
/// <para>
/// This is what a per-type <c>Control.Theme</c> default
/// (<see cref="StyledProperty{T}.OverrideDefaultValue{TOwner}"/>) can NOT do: that install delivers the theme
/// <see cref="Style"/> but is not a chain node, so a contributed control template's DynamicResource references
/// have nowhere to resolve. A contribution IS a chain node, so both the control theme and every resource it
/// references resolve. A contribution may carry <see cref="ResourceDictionary.ThemeDictionaries"/> for
/// per-(base × tier) variants, resolved by the same variant descent BuiltIn uses.
/// </para>
/// <para>
/// Control themes are keyed by control <see cref="Type"/> and resolve <b>exact-key</b> — the same contract as
/// every other chain tier (design doc CD13, no base probing). An app subclass of a library control opts into
/// the base theme the idiomatic way: it overrides <c>Control.ControlThemeKey</c> to return the base type
/// (e.g. <c>GalleryRibbon : Ribbon</c> returns <c>typeof(Ribbon)</c>), exactly as a subclass of a core control
/// does — WPF <c>DefaultStyleKey</c> parity. This keeps the tier consistent with App/BuiltIn and lets a
/// subclass deliberately choose its base's look (or its own) rather than inheriting one implicitly.
/// </para>
/// <para>
/// A contribution's <see cref="ResourceDictionary.Styles"/> selector channel is NOT consumed (only
/// <c>UIApplication.Theme.Styles</c> is, design doc §11.8) — a library ships <see cref="Type"/>-keyed control
/// themes and resources, not app-level selector styles.
/// </para>
/// </remarks>
public static class ThemeContributions
{
    // Copy-on-write snapshot: registration rebuilds the array under the gate and publishes it atomically;
    // the chain walk reads the reference once and iterates, lock-free.
    private static volatile ResourceDictionary[] _contributions = [];
    private static readonly object Gate = new();

    /// <summary>
    /// Registers a library theme dictionary into the contribution tier (idempotent by reference). The
    /// dictionary is <see cref="ResourceDictionary.Seal">sealed</see> on registration so it is freely shared
    /// process-wide and never pulses. Call from a <c>[ModuleInitializer]</c> so the tier is present before the
    /// library's controls first render.
    /// </summary>
    /// <param name="contribution">The library dictionary: control themes keyed by control <see cref="Type"/>,
    /// plus any brushes/resources those themes reference.</param>
    public static void Register(ResourceDictionary contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        contribution.Seal(); // freely shareable, never pulses (CD4/CD5)

        lock (Gate)
        {
            if (Array.IndexOf(_contributions, contribution) >= 0)
                return; // idempotent — a repeated module-init (test reload) is a no-op

            var next = new ResourceDictionary[_contributions.Length + 1];
            Array.Copy(_contributions, next, _contributions.Length);
            next[^1] = contribution;
            _contributions = next; // publish atomically (volatile write)
        }

        // Belt-and-suspenders: module inits normally run before any app exists, so this is a rarely-hit
        // safety net. A contribution registered AFTER the current thread's app is live invalidates its
        // already-resolved DynamicResource + control-theme lookups.
        UIApplication.Current?.OnThemeContributionsChanged();
    }

    /// <summary>Whether any contribution is registered — the chain-walk short-circuit (no hop, no diagnostic text, when empty).</summary>
    internal static bool HasContributions => _contributions.Length > 0;

    /// <summary>Resolves <paramref name="key"/> across the registered contributions at <paramref name="variant"/> (lock-free snapshot read).</summary>
    internal static bool TryGetResource(object key, ThemeVariant variant, out object? value)
        => TryGetResource(_contributions, key, variant, out value);

    /// <summary>
    /// The pure resolution over an explicit snapshot (unit-testable without touching global state):
    /// last-registered wins, so a later library shadows an earlier one on a shared key. Each dictionary's
    /// own probe does the variant descent + merged scan. Resolution is exact-key — the same contract as
    /// every other chain tier (design doc CD13); an app subclass of a library control opts into the base
    /// theme by overriding <c>Control.ControlThemeKey</c> to return the base type (WPF <c>DefaultStyleKey</c>
    /// parity), not by base-type probing here.
    /// </summary>
    internal static bool TryGetResource(ResourceDictionary[] contributions, object key, ThemeVariant variant, out object? value)
    {
        for (var i = contributions.Length - 1; i >= 0; i--)
        {
            if (contributions[i].TryGetResource(key, variant, out value))
                return true;
        }

        value = null;
        return false;
    }
}
