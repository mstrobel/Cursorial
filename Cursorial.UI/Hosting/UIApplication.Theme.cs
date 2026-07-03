using Cursorial.Output;
using Cursorial.Terminal;
using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The S7 theme surface (design doc §11.3, punch 44) folded onto the merged <see cref="UIApplication"/>:
/// the resource dictionaries, the active theme, the theme-variant axes, and the variant/resource
/// lifecycle. Capability-class stamping re-points to the <b>effective</b> tier
/// (<see cref="ActualThemeVariant"/>.Tier, inversion 6) here.
/// </summary>
public sealed partial class UIApplication : IResourceHost
{
    private ResourceDictionary? _resources;
    private ResourceDictionary? _theme;
    private ThemeBase? _requestedThemeBase;
    private ColorDepth? _requestedColorTier;
    private ThemeVariant _actualThemeVariant = new(ThemeBase.Dark, ColorDepth.Truecolor);
    private bool _variantInitialized;

    // One subscription registry per visual root (design doc §11.6). App-level pulses fan to all.
    private readonly Dictionary<UIElement, ResourceSubscriptionRegistry> _registries = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The application-level resources (design doc §11.3) — the chain hop above every root, below
    /// <see cref="Theme"/>. Replacing the dictionary pulses a catch-all to every root.
    /// </summary>
    public ResourceDictionary Resources
    {
        get
        {
            Dispatcher.VerifyAccess();
            if (_resources is null)
            {
                _resources = new ResourceDictionary();
                _resources.Acquire(this);
                _resources.Changed += OnApplicationResourcesChanged;
            }

            return _resources;
        }
        set
        {
            Dispatcher.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(_resources, value))
                return;

            if (_resources is { } old)
            {
                old.Changed -= OnApplicationResourcesChanged;
                old.Release(this);
            }

            value.Acquire(this);
            _resources = value;
            _resources.Changed += OnApplicationResourcesChanged;
            RaiseApplicationCatchAll(); // an app-scope replace is a catch-all (C60)
        }
    }

    /// <inheritdoc/>
    public bool HasResources => _resources is { Count: > 0 } or { HasThemeDictionaries: true };

    /// <summary>The application resources slot without lazy allocation (the chain-walk read surface).</summary>
    internal ResourceDictionary? ResourcesOrNull => _resources;

    /// <summary>
    /// An <b>optional</b> application theme dictionary (design doc §11.3) — the chain hop <i>above</i>
    /// <see cref="CursorialTheme.BuiltIn"/>. Leave it <see langword="null"/> (the normal state): the lookup
    /// chain probes <see cref="CursorialTheme.BuiltIn"/> unconditionally as its final tail, so controls are
    /// fully themed without setting this, and the active look is driven by <see cref="ActualThemeVariant"/>
    /// (via <see cref="RequestedColorTier"/> / <see cref="RequestedThemeBase"/>), not by this slot. Set it
    /// only to <i>layer</i> your own theme dictionary over BuiltIn (chain: <c>Resources → Theme → BuiltIn</c>,
    /// so your keys win and BuiltIn fills the gaps). It is deliberately NOT defaulted to BuiltIn — that would
    /// make assigning a custom theme <i>replace</i> the fallback rather than layer over it.
    /// </summary>
    public ResourceDictionary? Theme
    {
        get => _theme;
        set
        {
            Dispatcher.VerifyAccess();

            if (ReferenceEquals(_theme, value))
                return;

            // Hand the theme-styles re-match hook from the old theme to the new (R2/B13 / C100): while a
            // dictionary is the active theme, a mutation of its Styles slot re-matches the Theme(2) leg.
            // Only an UNSEALED theme gets the hook — a sealed dictionary's Styles can never mutate (CD4), and
            // the process-shared sealed CursorialTheme.BuiltIn must never be polluted with one app's engine.
            _theme?.ThemeStylesReMatchHook = null;
            _theme = value;
            if (_theme is { IsSealed: false })
                _theme.ThemeStylesReMatchHook = StyleEngineInternal.OnThemeStylesInvalidated;

            RaiseApplicationCatchAll();                     // resource leg: DynamicResource re-resolve + ResourcesChanged
            StyleEngineInternal.OnThemeStylesInvalidated(); // style leg: re-match the Theme(2) channel against the new theme
        }
    }

    /// <summary>An explicit light/dark override (design doc §11.3); <see langword="null"/> derives from the terminal background.</summary>
    public ThemeBase? RequestedThemeBase
    {
        get => _requestedThemeBase;
        set
        {
            Dispatcher.VerifyAccess();
            if (_requestedThemeBase == value)
                return;
            _requestedThemeBase = value;
            UpdateActualThemeVariant(reStampClasses: false); // a base flip changes only resources (CD14)
        }
    }

    /// <summary>A preview/testing color-tier override (design doc §11.3); <see langword="null"/> uses the negotiated depth. Re-points the effective tier (inversion 6).</summary>
    public ColorDepth? RequestedColorTier
    {
        get => _requestedColorTier;
        set
        {
            Dispatcher.VerifyAccess();
            if (_requestedColorTier == value)
                return;
            _requestedColorTier = value;
            UpdateActualThemeVariant(reStampClasses: true); // a tier flip re-stamps caps classes (CD14)
        }
    }

    /// <summary>The effective theme variant (design doc §11.3): <c>(RequestedThemeBase ?? derived, RequestedColorTier ?? negotiated)</c> per axis.</summary>
    public ThemeVariant ActualThemeVariant => _actualThemeVariant;

    /// <summary>Raised on the UI thread when <see cref="ActualThemeVariant"/> changes (design doc §11.3).</summary>
    public event EventHandler? ActualThemeVariantChanged;

    /// <summary>The external variant signal (design doc §11.3): a catch-all on app-scope or variant changes.</summary>
    public event EventHandler<ResourcesChangedEventArgs>? ResourcesChanged;

    // ───────────────────────────── capability coherence (design doc §11.7) ─────────────────────────────

    /// <summary>
    /// The S7 capability leg (design doc §11.7): re-derives the effective variant from
    /// <paramref name="capabilities"/> (honoring the requested overrides) and, on change, raises
    /// <see cref="ActualThemeVariantChanged"/> then <see cref="ResourcesChanged"/>(CatchAll), pulses
    /// every root's registry, and re-stamps the effective-tier capability classes (inversion 6). No
    /// dictionary mutates and no dictionary <c>Changed</c> fires (CD15).
    /// </summary>
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities)
    {
        Dispatcher.VerifyAccess();
        _negotiatedVariant = ThemeVariant.FromCapabilities(capabilities);
        UpdateActualThemeVariant(reStampClasses: false); // StyleEngineInternal.OnCapabilitiesChanged will restamp.
        StyleEngineInternal.OnCapabilitiesChanged(capabilities);
        StyleHooks?.OnCapabilitiesChanged(capabilities);
        InputDispatchTarget?.OnCapabilitiesChanged(capabilities);
        _accessKeys.OnCapabilitiesChanged(capabilities);
    }

    // The negotiated-only variant (the per-axis derivation source before the requested overrides).
    private ThemeVariant _negotiatedVariant = new(ThemeBase.Dark, ColorDepth.Truecolor);

    private void UpdateActualThemeVariant(bool reStampClasses)
    {
        var effective = new ThemeVariant(
            _requestedThemeBase ?? _negotiatedVariant.Base,
            _requestedColorTier ?? _negotiatedVariant.Tier);

        var first = !_variantInitialized;
        var changed = first || effective != _actualThemeVariant;
        var tierChanged = first || effective.Tier != _actualThemeVariant.Tier;

        _actualThemeVariant = effective;
        _variantInitialized = true;

        if (!changed)
            return;

        if (!first)
        {
            ActualThemeVariantChanged?.Invoke(this, EventArgs.Empty); // order pinned: variant-changed first (C64)
            RaiseApplicationCatchAll();                                // then ResourcesChanged + pulse all roots
        }

        // The color-tier capability class re-points to the effective tier (CD14). A base-only flip
        // (reStampClasses == false) does not touch classes.
        if (reStampClasses && tierChanged)
            StyleEngineInternal.OnEffectiveTierChanged(effective.Tier);

        // A RUNTIME variant flip re-emits the theme cursor color out-of-band (the loop is running, so the
        // wake is correct). The INITIAL color is written into the startup byte sequence (RunLoop's UI-mode
        // entry), NOT here — so merely constructing an app (no loop) never queues + wakes the dispatcher
        // (a bare dispatcher must stay unsignaled; UIDispatcher tests rely on it).
        if (!first)
            QueueControlSequence(WriteThemeCursorColor);
    }

    // Give the real terminal caret (TerminalCaretService publishes the terminal cursor, which paints in the
    // terminal's own cursor color) a theme-driven color so it stays legible across variants — the terminal
    // default washes out against the opposite background (the light-theme caret-invisibility report). Resolve
    // the accent at the RGB tier (canonical: OSC 12 takes an rgb: value regardless of the effective cell tier);
    // a no-op when the accent does not resolve to an RGB brush. RunTeardown emits OSC 112 to restore.
    // Whether WriteThemeCursorColor ever actually emitted an OSC 12 (the accent resolved to an RGB brush).
    // RunTeardown gates its OSC 112 reset on this so an app whose accent is non-RGB (no set was emitted)
    // does NOT revert a user's terminal-configured cursor color (review finding #9 — set/reset symmetry).
    private bool _cursorColorEmitted;

    internal void WriteThemeCursorColor(System.Buffers.IBufferWriter<byte> output)
    {
        var rgbVariant = new ThemeVariant(_actualThemeVariant.Base, ColorDepth.Truecolor);
        if (ResourceExtensions.WalkApplicationTail(ThemeKeys.AccentBrush, rgbVariant, searched: null, out var value) &&
            value is Drawing.Media.SolidColorBrush { Color: { Kind: ColorKind.Rgb } accent })
        {
            PaletteWriter.WriteSetCursor(output, accent.Red, accent.Green, accent.Blue);
            _cursorColorEmitted = true;
        }
    }

    // ───────────────────────────── subscription registry per root (design doc §11.6) ─────────────────────────────

    internal ResourceSubscriptionRegistry RegistryForRoot(UIElement root)
    {
        if (!_registries.TryGetValue(root, out var registry))
            _registries[root] = registry = new ResourceSubscriptionRegistry(this);
        return registry;
    }

    internal void ReleaseRegistryForRoot(UIElement root) => _registries.Remove(root);

    /// <summary>Routes an element-scoped <see cref="ResourceDictionary"/> mutation to its root's registry (design doc §11.6).</summary>
    internal void OnResourceDictionaryChanged(UIElement scope, ResourceDictionary dictionary, ResourcesChangedEventArgs e)
    {
        var root = ResourceServices.LogicalRoot(scope);
        if (!_registries.TryGetValue(root, out var registry))
            return; // no subscribers under this root yet

        if (e.Kind == ResourceChangeKind.Keyed && e.Key is { } key)
            registry.PulseKeyed(scope, key);
        else
            registry.PulseCatchAll(scope);
    }

    private void OnApplicationResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        // Application-level dictionary mutation fans to every root. The app is above every root, so an
        // app-scope keyed pulse uses the root itself as the containing scope (every node is contained).
        // Snapshot first: a listener could attach/detach a root re-entrantly during a pulse, which would
        // otherwise throw "collection modified" mid-iteration.
        foreach (var (root, registry) in _registries.ToArray())
        {
            if (e.Kind == ResourceChangeKind.Keyed && e.Key is { } key)
                registry.PulseKeyed(root, key);
            else
                registry.PulseCatchAll(pulsingScope: null);
        }

        ResourcesChanged?.Invoke(this, e);
    }

    private void RaiseApplicationCatchAll()
    {
        foreach (var registry in _registries.Values.ToArray()) // snapshot — a pulse may mutate _registries
            registry.PulseCatchAll(pulsingScope: null);

        ResourcesChanged?.Invoke(this, new ResourcesChangedEventArgs(ResourceChangeKind.CatchAll, null));
    }
}
