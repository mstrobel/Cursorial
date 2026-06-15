using System.Collections;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// An ordered collection of <see cref="Style"/>s scoped to one owner — an element subtree
/// (<see cref="UIElement.Styles"/>, layer <see cref="StyleLayer.Scoped"/>) or the application
/// (<see cref="UIApplication.Styles"/>, layer <see cref="StyleLayer.App"/>). A <c>Styles</c>
/// instance attaches to <b>one owner at a time</b> (style matrix SD19 — attaching an
/// already-attached instance throws); the sealed <see cref="Style"/> instances inside are
/// immutable and freely shareable across collections.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seal-on-attach</b> (doc §3.3): every style added to an attached collection — and every style
/// present when the collection attaches — is sealed and placement-validated in the same operation
/// (SD17: a selector-less, key-less style and a <c>^</c>-rooted style are both invalid here).
/// </para>
/// <para>
/// <b>Mutation</b> of an attached collection raises the internal invalidation hook (SD21 — the
/// coarse re-match tier; the engine diffs armed frames by rule identity, so unrelated rules'
/// frames survive untouched).
/// </para>
/// </remarks>
public sealed class Styles : IList<Style>, IReadOnlyList<Style>
{
    private readonly List<Style> _items = [];
    private object? _owner;
    private StyleScopeIndex? _cachedIndex;
    private bool _frozen;

    /// <summary>Creates a detached, empty collection (supports collection initializers).</summary>
    public Styles() {}

    /// <summary>The number of styles in the collection.</summary>
    public int Count => _items.Count;

    /// <inheritdoc/>
    bool ICollection<Style>.IsReadOnly => false;

    /// <summary>The style at <paramref name="index"/>; replacement validates and seals when attached.</summary>
    public Style this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ThrowIfFrozen();
            ValidateForAttach(value);
            _items[index] = value;
            NotifyChanged();
        }
    }

    /// <summary>The current owner (a <see cref="UIElement"/> or <see cref="UIApplication"/>), or null when detached.</summary>
    internal object? Owner => _owner;

    /// <summary>
    /// Adds <paramref name="style"/>. On an attached collection the style is placement-validated,
    /// auto-sealed, and (once the matcher lands) armed in the same operation (S41).
    /// </summary>
    public void Add(Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        ThrowIfFrozen();
        ValidateForAttach(style);
        _items.Add(style);
        NotifyChanged();
    }

    /// <summary>Inserts <paramref name="style"/> at <paramref name="index"/> (validating/sealing when attached).</summary>
    public void Insert(int index, Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        ThrowIfFrozen();
        ValidateForAttach(style);
        _items.Insert(index, style);
        NotifyChanged();
    }

    /// <summary>Removes <paramref name="style"/>; on an attached collection its rules retract scope-wide (S136 — Y3).</summary>
    public bool Remove(Style style)
    {
        ThrowIfFrozen();
        if (!_items.Remove(style))
            return false;

        NotifyChanged();
        return true;
    }

    /// <summary>Removes the style at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        ThrowIfFrozen();
        _items.RemoveAt(index);
        NotifyChanged();
    }

    /// <summary>Removes every style.</summary>
    public void Clear()
    {
        ThrowIfFrozen();
        if (_items.Count == 0)
            return;

        _items.Clear();
        NotifyChanged();
    }

    /// <summary>Whether <paramref name="style"/> is in the collection (reference identity).</summary>
    public bool Contains(Style style) => _items.Contains(style);

    /// <summary>The index of <paramref name="style"/>, or −1.</summary>
    public int IndexOf(Style style) => _items.IndexOf(style);

    /// <inheritdoc/>
    void ICollection<Style>.CopyTo(Style[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>Enumerates the styles in declaration order.</summary>
    public List<Style>.Enumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<Style> IEnumerable<Style>.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    // ───────────────────────────── attach / scope surface (internal) ─────────────────────────────

    /// <summary>
    /// Binds the collection to its owner (SD19 single ownership) and validates + seals every
    /// member (seal-on-attach). Throws <see cref="InvalidOperationException"/> when already
    /// attached elsewhere or when a member violates the SD17 placement rules.
    /// </summary>
    internal void AttachTo(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (_owner is not null && !ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "This Styles collection is already attached to an owner. A Styles instance attaches to one " +
                "owner at a time (SD19); sealed Style instances are shareable — share those instead.");
        }

        foreach (var style in _items)
            ValidateStylePlacement(style);

        _owner = owner;
    }

    /// <summary>Releases the owner binding (owner replacement / teardown).</summary>
    internal void Detach()
    {
        _owner = null;
        _cachedIndex = null;
    }

    /// <summary>
    /// Permanently freezes the collection's membership (seals every member, then blocks further
    /// Add/Insert/Remove/Clear/index-set). Used by <see cref="Controls.ControlTemplate.Seal"/> so a
    /// sealed, shared template's <c>Styles</c> cannot be mutated after the first instantiation arms
    /// them (post-seal additions would never be placement-validated). Idempotent.
    /// </summary>
    internal void Freeze()
    {
        if (_frozen)
            return;

        foreach (var style in _items)
            style.Seal();

        _frozen = true;
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "This Styles collection is frozen (its owning ControlTemplate is sealed) and can no longer be mutated. " +
                "Author all template styles before the template is first instantiated.");
        }
    }

    /// <summary>
    /// The cached matcher index for this scope (rules + discriminator buckets), rebuilt when absent
    /// or when the owner's styling depth changed since the build (reattachment at another depth).
    /// Mutation clears the cache (SD21).
    /// </summary>
    internal StyleScopeIndex GetOrBuildIndex(StyleLayer layer, int scopeDepth, int orderBase = 0)
    {
        var index = _cachedIndex;
        if (index is null || index.Layer != layer || index.ScopeDepth != scopeDepth || index.OrderBase != orderBase)
            _cachedIndex = index = new StyleScopeIndex(layer, scopeDepth, orderBase, BuildScopeRules(layer, scopeDepth, orderBase));

        return index;
    }

    /// <summary>
    /// Compiles the scope's full rule list with depth-first declaration order indices and packed
    /// sort keys (SD5/SD6): for each style in order, its <see cref="Style.CompiledRules"/> (own
    /// selector-list members first, then nested children — already DFS within the style), each rule
    /// taking the next consecutive order index.
    /// </summary>
    /// <param name="layer">The channel layer of this scope (App or Scoped at P3).</param>
    /// <param name="scopeDepth">The scope owner's styling-parent depth (Scoped only; 0 otherwise — SD6).</param>
    /// <param name="orderBase">The DFS-order start index (non-zero only for the app-theme leg, so its rules sort above the BuiltIn theme leg within <see cref="StyleLayer.Theme"/> — R2/B13).</param>
    internal List<ScopeRule> BuildScopeRules(StyleLayer layer, int scopeDepth, int orderBase = 0)
    {
        var rules = new List<ScopeRule>();
        var order = orderBase;

        foreach (var style in _items)
        {
            style.Seal();

            foreach (var rule in style.CompiledRules)
            {
                rules.Add(new ScopeRule(rule, StyleSortKey.Create(
                                            layer, rule.Names, rule.ClassLike, rule.Types, scopeDepth, order), order));

                order++;
            }
        }

        return rules;
    }

    private void ValidateForAttach(Style style)
    {
        if (_owner is not null)
            ValidateStylePlacement(style);
    }

    /// <summary>The SD17 placement rules for scoped collections + seal-on-attach.</summary>
    private static void ValidateStylePlacement(Style style)
    {
        if (style.Selector is null && style.Key is null)
        {
            throw new InvalidOperationException(
                $"Style '{style.IdentityForDiagnostics}' has neither a selector nor a key: a selector-less, " +
                "key-less style cannot live in a Styles collection — its legal homes are UIElement.Style " +
                "and the keyed theme channel (SD17).");
        }

        if (style.Selector is { HasNestingRoot: true })
        {
            throw new InvalidOperationException(
                $"Style '{style.IdentityForDiagnostics}' is '^'-rooted: nesting-anchored styles cannot live at " +
                "the top level of a Styles collection — '^' is valid in Style.Children and explicit " +
                "UIElement.Style attachments only (SD17).");
        }

        style.Seal();
    }

    private void NotifyChanged()
    {
        _cachedIndex = null; // SD21: any mutation invalidates the matcher index

        // SD21: the coarse re-match tier — the owning scope re-matches its subtree.
        switch (_owner)
        {
            case UIElement element:
                element.OnStylingStylesInvalidated();
                break;

            case UIApplication application:
                application.OnStylesInvalidated(this);
                break;

            case ResourceDictionary dictionary:
                dictionary.OnStylesMutated(); // the theme-styles channel pulse path (C25)
                break;
        }
    }
}

/// <summary>
/// One armed-able rule of a scope: the compiled rule plus its scope-assigned packed key and
/// declaration order index (the Y3 matcher's input; <c>Styles.BuildScopeRules</c> produces these).
/// </summary>
internal readonly struct ScopeRule(CompiledRule rule, StyleSortKey key, int order)
{
    /// <summary>The scope-independent compiled rule.</summary>
    internal CompiledRule Rule { get; } = rule;

    /// <summary>The fully packed sort key (layer + specificity + scope depth + order).</summary>
    internal StyleSortKey Key { get; } = key;

    /// <summary>The DFS declaration index within the scope.</summary>
    internal int Order { get; } = order;
}