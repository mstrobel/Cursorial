// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The typed per-type metadata <c>Changed</c> callback — the invalidation channel. It is the first
/// of the synchronous notification channels (metadata <c>Changed</c> → typed observers → untyped
/// observers → virtual <c>OnPropertyChanged</c>, design doc §2.3) and the channel the element tree's
/// <c>Affects*</c> dispatch is built on.
/// </summary>
public delegate void PropertyChangedCallback<T>(UIObject sender, T oldValue, T newValue);

/// <summary>
/// Per-type metadata for a <see cref="StyledProperty{T}"/>. Resolved metadata for a runtime type is
/// computed by merging down the type chain (registration metadata first, then overrides base-first)
/// and cached per concrete type — see <see cref="StyledProperty{T}.GetMetadata"/>.
/// </summary>
/// <remarks>
/// Merge rules (pinned, design doc §2 / §2.3): <see cref="Changed"/> callbacks <b>chain, base
/// first</b>; <see cref="Coerce"/>, <see cref="Validate"/>, <see cref="Comparer"/>, and
/// <see cref="ParsesAccessKeyLiterals"/> fall through to the base metadata when <see langword="null"/>
/// (nearest non-null wins); <see cref="DefaultValue"/> always <b>replaces</b>. Because
/// <see cref="DefaultValue"/> is structural (not nullable), an override that only wants to change a
/// callback must restate the default it intends to keep — or use
/// <see cref="StyledProperty{T}.OverrideDefaultValue{TOwner}"/> for the default-only case.
/// </remarks>
/// <param name="DefaultValue">The value resolved at the <see cref="BindingPriority.Default"/> tier.
/// Defaults are returned as registered: neither coerced nor validated (matrix PD8).</param>
/// <param name="Coerce">Coercion run inside effective-value computation — the cell-grid guardrail
/// (§2.6): clamp to grid constraints so an overshooting easing or bad binding can't detonate a
/// constructor three layers down. Inherited reads skip coercion (coercion applies where a value is
/// set).</param>
/// <param name="Validate">Raw-value validation, run <b>before</b> <paramref name="Coerce"/> (matrix
/// PD7). A local <c>SetValue</c> throws on invalid; producer-fed invalid values are discarded with a
/// diagnostic.</param>
/// <param name="Changed">The per-type changed callback (invalidation channel).</param>
/// <param name="Comparer">The equality gate for change detection; <see langword="null"/> means
/// <see cref="EqualityComparer{T}.Default"/>.</param>
public sealed record PropertyMetadata<T>(
    T DefaultValue = default!,
    Func<UIObject, T, T>? Coerce = null,
    Func<T, bool>? Validate = null,
    PropertyChangedCallback<T>? Changed = null,
    IEqualityComparer<T>? Comparer = null)
{
    /// <summary>
    /// Per-type flag slot for access-key literal folding (ledger A21): when resolved
    /// <see langword="true"/> for an instance's runtime type, string content assigned to the property
    /// parses <c>_</c>-prefixed access-key literals (<c>Header="_File"</c>) into <c>AccessText</c>.
    /// Set via metadata override on exactly the four pinned properties (<c>ButtonBase.Content</c>,
    /// <c>MenuItem.Header</c>, <c>TabItem.Header</c>, <c>Label.Content</c>); consumed by the XAML
    /// pipeline and runtime <c>GetAccessText()</c> — storage only at this layer, no consumer yet.
    /// <see langword="null"/> falls through to the base metadata during merge; the effective default
    /// is <see langword="false"/>.
    /// </summary>
    public bool? ParsesAccessKeyLiterals { get; init; }

    /// <summary>
    /// A resource key the <see cref="BindingPriority.Default"/> tier resolves through the element's
    /// resource chain (design: the theme-reactive metadata default). When set, an effective-value
    /// read that reaches the default tier on a <c>UIElement</c> looks the key up first and falls
    /// back to <see cref="DefaultValue"/> when the key is unresolvable (or the element is outside a
    /// resource chain). Every REAL contribution — inherited values included — beats it by
    /// construction, which is the whole point: a bare <c>TextBlock</c> reads the theme's text brush,
    /// while one inside a template or under a foreground-setting ancestor keeps the inherited value.
    /// <see langword="null"/> falls through to the base metadata during merge (nearest non-null wins).
    /// </summary>
    public object? DefaultResourceKey { get; init; }

    private object? _boxedDefault;

    /// <summary>The change-detection comparer, defaulting to <see cref="EqualityComparer{T}.Default"/>.</summary>
    internal IEqualityComparer<T> EffectiveComparer => Comparer ?? EqualityComparer<T>.Default;

    /// <summary>
    /// The boxed <see cref="DefaultValue"/>, cached so repeated untyped reads of default-valued
    /// properties allocate nothing after the first box (the metadata instance is itself cached per
    /// resolved type, so the box is shared across instances; matrix M14/M267 mechanism).
    /// </summary>
    internal object? BoxedDefault => _boxedDefault ??= ValueBoxes.Box(DefaultValue);
}
